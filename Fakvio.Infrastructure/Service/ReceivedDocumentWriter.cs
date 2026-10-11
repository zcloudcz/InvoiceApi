using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// THE place for the RECEIVED-document type policy of create and update (DEVGUIDE §5.6). Together with
/// <see cref="DocumentTransitions"/> (status changes) it is everything that makes a received invoice differ from an
/// issued one when it is written:
///   - the counterparty is the SUPPLIER and lives in <c>ClientId</c> (must exist and be active);
///   - <c>IssuerId</c> is OUR company (the <c>Client</c> with <c>IsIssuer</c>, lowest Id), always set by the server;
///   - <c>DocumentNumber</c> is the supplier's number: supplied by the caller, editable, never generated from a number
///     sequence and never released;
///   - <c>ReceivedDate</c> is settable (default: now); the issued-only pipeline (bank account defaults, OSS, VAT payer
///     checks, variable symbol derivation, due date from billing settings) does not run;
///   - editing is allowed in Draft and in Completed / PartiallyPaid ("approved"); the exchange rate only in Draft.
///
/// Used by <c>InvoiceService</c> (the unified API) and by the <c>ReceivedInvoiceService</c> facade, which maps its
/// legacy DTOs onto <see cref="CreateInvoiceDto"/> / <see cref="UpdateInvoiceDto"/>. That is how both entry points
/// end up with identical rows: the rules exist once, here.
/// </summary>
internal sealed class ReceivedDocumentWriter
{
    private readonly TenantDbContext _context;
    private readonly IWebhookPublisher? _webhookPublisher; // optional so manually-constructed instances (tests) work
    private readonly ILogger _logger;

    public ReceivedDocumentWriter(TenantDbContext context, IWebhookPublisher? webhookPublisher, ILogger logger)
    {
        _context = context;
        _webhookPublisher = webhookPublisher;
        _logger = logger;
    }

    /// <summary>Creates a received document (always a Draft) and returns its id; the caller loads the DTO it wants.</summary>
    public async Task<long> CreateAsync(CreateInvoiceDto dto, CancellationToken ct)
    {
        _logger.LogInformation("Creating received invoice from supplier {SupplierId}", dto.ClientId);

        // These fields only make sense for documents WE issue. Refusing beats silently ignoring them.
        if (dto.NumberSequenceId.HasValue || dto.OriginalInvoiceId.HasValue || dto.BankAccountId.HasValue || dto.ApplyOss)
            throw new InvalidOperationException(
                "NumberSequenceId, OriginalInvoiceId, BankAccountId and ApplyOss do not apply to a received invoice.");

        // Validate supplier exists
        var supplier = await _context.Client
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == dto.ClientId && c.IsActive, ct);

        if (supplier is null)
            throw new InvalidOperationException($"Supplier with ID {dto.ClientId} not found or inactive.");

        ValidateItems(dto.InvoiceItem);

        // IssuerId is NOT NULL on Invoice: for a received document it is OUR company (the IsIssuer client).
        // Lowest Id wins - the same rule the data migration used for the existing rows.
        var issuerId = await _context.Client
            .AsNoTracking()
            .Where(c => c.IsIssuer)
            .OrderBy(c => c.Id)
            .Select(c => (long?)c.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Tenant has no issuer company (Client with IsIssuer = true) configured.");

        // The issuer of a received document is never the caller's choice. 0 = "not sent" (IssuerId is a plain
        // long on the DTO) and our own id are fine; any other value is refused like the issued-only fields above,
        // because silently storing a different issuer than the caller asked for hides a client bug.
        if (dto.IssuerId != 0 && dto.IssuerId != issuerId)
            throw new InvalidOperationException(
                $"IssuerId {dto.IssuerId} does not apply to a received invoice: the issuer is always the tenant's own company ({issuerId}). Omit it or send 0.");

        var entity = new Invoice
        {
            DocumentType = EDocumentType.ReceivedInvoice,
            DocumentNumber = dto.CustomDocumentNumber, // the supplier's number; never generated
            Status = EInvoiceStatus.Draft,
            ClientId = dto.ClientId,
            IssuerId = issuerId,
            // Dates are stored as given (a received document carries the supplier's dates, not "today" defaults).
            IssueDate = dto.IssueDate,
            ReceivedDate = dto.ReceivedDate ?? DateTime.UtcNow,
            DueDate = dto.DueDate,
            TaxableSupplyDate = dto.TaxableSupplyDate ?? dto.IssueDate,
            VariableSymbol = dto.VariableSymbol,
            ConstantSymbol = dto.ConstantSymbol,
            SpecificSymbol = dto.SpecificSymbol,
            CurrencyId = dto.CurrencyId,
            ExchangeRate = dto.ExchangeRate, // manual override (null = ČNB rate is assigned on approval)
            PaymentMethod = dto.PaymentMethod,
            BankAccountNumber = dto.BankAccountNumber,
            IBAN = dto.IBAN,
            SWIFT = dto.SWIFT,
            Notes = dto.Notes
        };

        var items = await BuildItemsAsync(dto.InvoiceItem, ct);
        foreach (var item in items) entity.InvoiceItem.Add(item);
        ApplyTotals(entity, items);

        _context.Invoice.Add(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Created received invoice ID {Id} from supplier {SupplierId}, total {Total}",
            entity.Id, entity.ClientId, entity.TotalWithVat);

        // "received_invoice.created" webhook (the only invoice webhook a received document publishes) - never throws.
        if (_webhookPublisher != null)
            await _webhookPublisher.PublishReceivedInvoiceEventAsync(
                Contracts.Dto.Webhook.WebhookEventCatalog.ReceivedInvoiceCreated, entity.Id, ct);

        return entity.Id;
    }

    /// <summary>
    /// Applies <paramref name="dto"/> to a received document. <paramref name="entity"/> must be a TRACKED received
    /// invoice loaded with <c>InvoiceItem</c>; the caller has already checked <c>ExpectedStatus</c>.
    /// </summary>
    public async Task UpdateAsync(Invoice entity, UpdateInvoiceDto dto, CancellationToken ct)
    {
        // Only allow updates in Draft ("received") or Completed / PartiallyPaid ("approved")
        if (entity.Status is not (EInvoiceStatus.Draft or EInvoiceStatus.Completed or EInvoiceStatus.PartiallyPaid))
            throw new InvalidOperationException(
                $"Cannot update a received invoice in {entity.Status} status. Only Draft or Completed ones can be updated.");

        if (dto.BankAccountId.HasValue || dto.ApplyOss.HasValue)
            throw new InvalidOperationException("BankAccountId and ApplyOss do not apply to a received invoice.");

        // Update simple fields (only if provided)
        if (dto.DocumentNumber is not null) entity.DocumentNumber = dto.DocumentNumber;
        if (dto.IssueDate.HasValue) entity.IssueDate = dto.IssueDate;
        if (dto.ReceivedDate.HasValue) entity.ReceivedDate = dto.ReceivedDate;
        if (dto.DueDate.HasValue) entity.DueDate = dto.DueDate;
        if (dto.TaxableSupplyDate.HasValue) entity.TaxableSupplyDate = dto.TaxableSupplyDate;
        if (dto.VariableSymbol is not null) entity.VariableSymbol = dto.VariableSymbol;
        if (dto.ConstantSymbol is not null) entity.ConstantSymbol = dto.ConstantSymbol;
        if (dto.SpecificSymbol is not null) entity.SpecificSymbol = dto.SpecificSymbol;
        if (dto.CurrencyId.HasValue) entity.CurrencyId = dto.CurrencyId.Value;
        if (dto.ExchangeRate.HasValue)
        {
            // Like an issued invoice: the rate is fixed once the document is approved.
            if (entity.Status != EInvoiceStatus.Draft)
                throw new InvalidOperationException("The exchange rate can be changed only before the invoice is approved.");
            entity.ExchangeRate = dto.ExchangeRate.Value;
            entity.ExchangeRateDate = null; // manual rate has no ČNB date
        }
        if (dto.PaymentMethod.HasValue) entity.PaymentMethod = dto.PaymentMethod;
        if (dto.BankAccountNumber is not null) entity.BankAccountNumber = dto.BankAccountNumber;
        if (dto.IBAN is not null) entity.IBAN = dto.IBAN;
        if (dto.SWIFT is not null) entity.SWIFT = dto.SWIFT;
        if (dto.Notes is not null) entity.Notes = dto.Notes;

        // If items provided, replace all existing items
        if (dto.InvoiceItem is not null)
        {
            ValidateItems(dto.InvoiceItem);

            // Remove old items
            _context.InvoiceItem.RemoveRange(entity.InvoiceItem);

            // Add new items and recalculate totals
            var items = await BuildItemsAsync(dto.InvoiceItem, ct);
            foreach (var item in items)
            {
                item.InvoiceId = entity.Id;
                _context.InvoiceItem.Add(item);
            }
            ApplyTotals(entity, items);
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Updated received invoice ID {Id}", entity.Id);
    }

    private async Task<List<InvoiceItem>> BuildItemsAsync(IEnumerable<CreateInvoiceItemDto> itemDtos, CancellationToken ct)
    {
        var items = new List<InvoiceItem>();

        foreach (var itemDto in itemDtos)
        {
            var vatPercentage = itemDto.VatRatePercentage;

            // If VatRateId provided, fetch the actual percentage from DB
            if (itemDto.VatRateId.HasValue)
            {
                var vatRate = await _context.VatRate
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == itemDto.VatRateId.Value, ct);

                if (vatRate is not null)
                    vatPercentage = vatRate.Rate;
            }

            var item = new InvoiceItem
            {
                OrderIndex = itemDto.OrderIndex,
                Description = itemDto.Description,
                Quantity = itemDto.Quantity,
                Unit = itemDto.Unit,
                UnitPrice = itemDto.UnitPrice,
                VatRateId = itemDto.VatRateId,
                VatRatePercentage = vatPercentage,
                TotalBeforeVat = itemDto.Quantity * itemDto.UnitPrice,
                VatRegime = itemDto.VatRegime,
                ReverseChargeCodeId = itemDto.ReverseChargeCodeId,
                ProductCode = itemDto.ProductCode,
                Notes = itemDto.Notes
            };
            CalculateItemVat(item);
            items.Add(item);
        }

        return items;
    }

    private static void ApplyTotals(Invoice entity, List<InvoiceItem> items)
    {
        entity.TotalBeforeVat = items.Sum(i => i.TotalBeforeVat);
        entity.TotalVat = items.Sum(i => i.VatAmount);
        entity.TotalWithVat = entity.TotalBeforeVat + entity.TotalVat;
    }

    /// <summary>
    /// Validates the items of a received document: no text rows (the received document has none) and the Reverse
    /// Charge code consistency (same rules as the issued side, issue epo-vat-reporting):
    /// 1. VatRegime == ReverseCharge requires ReverseChargeCodeId to be set.
    /// 2. Any other regime requires ReverseChargeCodeId to be null.
    /// </summary>
    private static void ValidateItems(IEnumerable<CreateInvoiceItemDto> items)
    {
        foreach (var item in items)
        {
            if (item.IsTextRow)
                throw new InvalidOperationException(
                    $"Received invoice item '{item.Description}' is a text row, which a received invoice does not support.");

            // JSON clients (MCP/API) can send an undefined numeric enum value; reject it up front.
            if (!Enum.IsDefined(item.VatRegime))
                throw new InvalidOperationException(
                    $"Received invoice item '{item.Description}' has an invalid VatRegime value '{(int)item.VatRegime}'.");

            if (item.VatRegime == EVatRegime.ReverseCharge && !item.ReverseChargeCodeId.HasValue)
                throw new InvalidOperationException(
                    $"Received invoice item '{item.Description}' has VatRegime=ReverseCharge but no ReverseChargeCodeId. " +
                    "A reverse charge code (kód předmětu plnění) is required for PDP items.");

            if (item.VatRegime != EVatRegime.ReverseCharge && item.ReverseChargeCodeId.HasValue)
                throw new InvalidOperationException(
                    $"Received invoice item '{item.Description}' has ReverseChargeCodeId set but VatRegime={item.VatRegime}. " +
                    "ReverseChargeCodeId must only be set for ReverseCharge items.");
        }
    }

    /// <summary>
    /// Calculates VatAmount, InformationalVatAmount, and TotalWithVat for a received invoice
    /// item, applying the correct logic per the item's VatRegime.
    ///
    /// Unlike the issued-invoice side, "VatAmount" here is money WE actually owe to the
    /// supplier - for ReverseCharge it stays 0 because the supplier's invoice itself does
    /// not bill VAT. InformationalVatAmount instead holds the self-assessed amount we must
    /// declare as both output tax (we owe it) and input tax (we may deduct it) - see
    /// VatReportService rows 10/11 and 43/44 of DPHDP3.
    ///
    /// TotalBeforeVat must be set on the item before calling this method.
    /// </summary>
    private static void CalculateItemVat(InvoiceItem item)
    {
        switch (item.VatRegime)
        {
            case EVatRegime.Standard:
                item.VatAmount = Math.Round(
                    item.TotalBeforeVat * (item.VatRatePercentage / 100m),
                    2, MidpointRounding.AwayFromZero);
                item.TotalWithVat = item.TotalBeforeVat + item.VatAmount;
                item.InformationalVatAmount = 0;
                break;

            case EVatRegime.ReverseCharge:
                // PDP: supplier billed 0 VAT - WE self-assess it (§92a ZDPH). A 0 % rate would
                // silently drop the supply from the VAT filings, so reject it.
                if (item.VatRatePercentage <= 0m)
                    throw new InvalidOperationException(
                        $"Received invoice item '{item.Description}' uses reverse charge, so it needs the VAT rate that applies " +
                        "in the Czech Republic (VatRatePercentage > 0).");
                item.VatAmount = 0;
                item.TotalWithVat = item.TotalBeforeVat;
                item.InformationalVatAmount = Math.Round(
                    item.TotalBeforeVat * (item.VatRatePercentage / 100m),
                    2, MidpointRounding.AwayFromZero);
                break;

            case EVatRegime.Exempt:
            case EVatRegime.OutOfScope:
                item.VatAmount = 0;
                item.TotalWithVat = item.TotalBeforeVat;
                item.InformationalVatAmount = 0;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(item.VatRegime),
                    item.VatRegime, "Unhandled VatRegime value in CalculateItemVat.");
        }
    }
}
