using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Domain.Validation;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Service for generating QR codes for Czech invoices.
///
/// QR code generation strategy (in priority order):
/// 1. Valid IBAN available → local SPD generation via QRCoder (fast, no external dependency)
/// 2. Valid Czech bank account available (e.g., "1342333010/3030") → paylibo.com API
///    (converts Czech account format to valid QR Platba — same approach as Monarc.Core)
/// 3. Neither → <see cref="QrPaymentUnavailableException"/>. There is NO third strategy.
///
/// The paylibo API is the proven solution for Czech domestic bank accounts
/// that don't have an IBAN. It generates a valid SPD QR code that all Czech
/// banking apps (George, mBank, Fio, etc.) can reliably scan.
///
/// WHY there is no fallback any more (issue #154):
/// this service used to fall back to a SIND-only "QR Faktura" whenever no bank connection
/// was available. That QR code carries invoice metadata but no payment instructions — it
/// scans fine and then does nothing, and it was printed on the PDF looking exactly like a
/// real QR Platba. A QR code that cannot be paid is worse than no QR code, because both the
/// issuer and the recipient believe payment is a scan away. The generator now refuses and
/// reports why, so the caller can show the user an actionable message.
/// SIND remains available on its own, through <see cref="GenerateSindStringAsync"/>, where
/// the caller explicitly asks for that format and cannot be misled about what it contains.
/// </summary>
public class QrPaymentService : IQrPaymentService
{
    private readonly TenantDbContext _context;
    private readonly IPayliboClient _payliboClient;
    private readonly ILogger<QrPaymentService> _logger;

    /// <summary>
    /// Application name embedded in the X-SW proprietary SIND attribute.
    /// </summary>
    private const string SoftwareName = "INVOICEAPI";

    public QrPaymentService(
        TenantDbContext context,
        IPayliboClient payliboClient,
        ILogger<QrPaymentService> logger)
    {
        _context = context;
        _payliboClient = payliboClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> GenerateSindStringAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await LoadInvoiceWithDetailsAsync(invoiceId, cancellationToken);
        var builder = BuildSindFromInvoice(invoice);
        return builder.Build();
    }

    /// <inheritdoc />
    public async Task<string> GenerateSpdWithInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await LoadInvoiceWithDetailsAsync(invoiceId, cancellationToken);

        // An SPD string can only be built locally from an IBAN. The Czech-account route goes
        // through paylibo, which returns a rendered image and never the underlying string —
        // so there is nothing honest this method could return for an account-only invoice.
        // It used to return a SIND string in a field named "spd", which was simply a lie.
        if (string.IsNullOrWhiteSpace(invoice.IBAN))
        {
            throw NoIbanForSpdString(invoice);
        }

        RequireValidIban(invoice);
        return BuildSimpleSpd(invoice);
    }

    /// <inheritdoc />
    public async Task<byte[]> GenerateQrCodeImageAsync(long invoiceId, int pixelsPerModule = 10,
        CancellationToken cancellationToken = default)
    {
        var invoice = await LoadInvoiceWithDetailsAsync(invoiceId, cancellationToken);

        // Strategy 1: IBAN available → local SPD generation (fastest, no external dependency)
        if (!string.IsNullOrWhiteSpace(invoice.IBAN))
        {
            RequireValidIban(invoice);

            _logger.LogInformation("Generating QR Platba for invoice {InvoiceId} via local SPD (IBAN available)",
                invoiceId);
            return GenerateQrPng(BuildSimpleSpd(invoice), pixelsPerModule);
        }

        // Strategy 2: Czech bank account available → paylibo.com API
        // (same approach as Monarc.Core — the API handles Czech account format natively)
        if (!string.IsNullOrWhiteSpace(invoice.BankAccountNumber))
        {
            RequireValidCzechBankAccount(invoice);

            _logger.LogInformation(
                "Generating QR Platba for invoice {InvoiceId} via paylibo API (Czech bank account: {Account})",
                invoiceId, invoice.BankAccountNumber);

            return await GenerateViaPayliboAsync(invoice);
        }

        // No bank connection at all. This is the case reported in issue #154: the issuer has
        // no bank account, so there is nothing to pay to. Refuse instead of printing a QR code
        // that only looks payable.
        _logger.LogWarning(
            "QR payment code refused for invoice {InvoiceId}: the document carries no IBAN and no bank account",
            invoiceId);

        throw new QrPaymentUnavailableException(
            EQrPaymentUnavailableReason.NoBankAccount,
            invoice.Id,
            $"Invoice {invoice.DocumentNumber ?? invoice.Id.ToString()} carries no bank connection, " +
            "so no QR payment code can be generated. Add a bank account (account number or IBAN) " +
            "to your company profile and re-issue the document.");
    }

    /// <summary>
    /// Builds the simple SPD (QR Platba) payment string from the invoice.
    /// Shared by the string endpoint and the image endpoint so both always agree
    /// on the content of the payment code.
    /// </summary>
    private static string BuildSimpleSpd(Invoice invoice) =>
        SpdIntegrator.BuildSimpleSpdString(
            invoice.IBAN!,
            invoice.SWIFT,
            invoice.TotalWithVat,
            invoice.Currency?.Code,
            invoice.DueDate,
            invoice.VariableSymbol,
            invoice.DocumentNumber);

    /// <summary>
    /// Rejects an invoice whose IBAN is present but malformed.
    ///
    /// SpdIntegrator used to copy the IBAN into the SPD "ACC" attribute without looking at it,
    /// so a typo produced a QR code that scanned into a payment form the bank then refused —
    /// or, worse, into a different existing account. Validating here means the failure is
    /// reported to the user instead of being discovered by whoever tries to pay.
    /// </summary>
    private void RequireValidIban(Invoice invoice)
    {
        if (BankAccountValidator.TryValidateIban(invoice.IBAN, out var error))
        {
            return;
        }

        _logger.LogWarning("QR payment code refused for invoice {InvoiceId}: {Error}", invoice.Id, error);

        throw new QrPaymentUnavailableException(
            EQrPaymentUnavailableReason.InvalidBankAccount,
            invoice.Id,
            $"No QR payment code can be generated for invoice {invoice.DocumentNumber ?? invoice.Id.ToString()} — " +
            $"{error} Correct the IBAN on the bank account in your company profile and re-issue the document.");
    }

    /// <summary>
    /// Rejects an invoice whose bank account cannot be turned into a Czech QR Platba.
    ///
    /// Two distinct cases end up here, and both are genuine dead ends for a payment QR code:
    /// a Czech-shaped number that fails the modulo 11 check (a typo), and a foreign account
    /// number that paylibo cannot process at all (an IBAN is required for those).
    /// </summary>
    private void RequireValidCzechBankAccount(Invoice invoice)
    {
        if (BankAccountValidator.TryValidateCzechAccountNumber(invoice.BankAccountNumber, out var error))
        {
            return;
        }

        _logger.LogWarning("QR payment code refused for invoice {InvoiceId}: {Error}", invoice.Id, error);

        throw new QrPaymentUnavailableException(
            EQrPaymentUnavailableReason.InvalidBankAccount,
            invoice.Id,
            $"No QR payment code can be generated for invoice {invoice.DocumentNumber ?? invoice.Id.ToString()} — " +
            $"{error} Correct the bank account in your company profile, or add its IBAN, " +
            "and re-issue the document.");
    }

    /// <summary>
    /// Builds the error for the SPD-string endpoint when the invoice has no IBAN.
    /// </summary>
    private QrPaymentUnavailableException NoIbanForSpdString(Invoice invoice)
    {
        _logger.LogWarning("SPD string refused for invoice {InvoiceId}: the document has no IBAN", invoice.Id);

        return new QrPaymentUnavailableException(
            EQrPaymentUnavailableReason.NoBankAccount,
            invoice.Id,
            $"Invoice {invoice.DocumentNumber ?? invoice.Id.ToString()} has no IBAN, and an SPD payment " +
            "string can only be built from an IBAN. Add an IBAN to the bank account in your company " +
            "profile and re-issue the document.");
    }

    /// <summary>
    /// Generates a QR payment image via the paylibo.com API using Czech bank account format.
    /// Parses the bank account number from "prefix-account/bankCode" or "account/bankCode" format.
    ///
    /// Same approach as Monarc.Core InvoiceService.cs:
    ///   accountPrefix = part before dash (if present)
    ///   accountNumber = part after dash (or full number if no dash)
    ///   bankCode = part after slash
    ///
    /// A paylibo failure used to be swallowed into an empty byte array, which the caller then
    /// replaced with a SIND-only QR code. It is now surfaced as ProviderUnavailable so the
    /// operator sees a real error and the user is told to retry rather than handed a QR code
    /// that cannot be paid.
    /// </summary>
    private async Task<byte[]> GenerateViaPayliboAsync(Invoice invoice)
    {
        // Parse Czech bank account format: "prefix-account/bankCode" or "account/bankCode"
        var (prefix, accountNumber, bankCode) = ParseCzechBankAccount(invoice.BankAccountNumber!);

        var options = new PayliboQrOptions
        {
            accountPrefix = prefix,
            accountNumber = accountNumber,
            bankCode = bankCode,
            amount = invoice.TotalWithVat,
            currency = invoice.Currency?.Code ?? "CZK",
            vs = invoice.VariableSymbol,
            date = invoice.DueDate.HasValue ? DateOnly.FromDateTime(invoice.DueDate.Value) : null,
            message = invoice.DocumentNumber,
            size = 200,
            branding = false,
            compress = false
        };

        byte[] image;
        try
        {
            image = await _payliboClient.CreateQrPaymentImageAsync(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paylibo API failed for invoice {InvoiceId} — no QR payment code produced",
                invoice.Id);
            throw PayliboUnavailable(invoice, ex);
        }

        if (image.Length == 0)
        {
            _logger.LogError("Paylibo API returned an empty image for invoice {InvoiceId}", invoice.Id);
            throw PayliboUnavailable(invoice, innerException: null);
        }

        return image;
    }

    /// <summary>
    /// Builds the error for a paylibo outage. Kept in one place so both failure shapes
    /// (exception and empty response) tell the user the same thing.
    /// </summary>
    private static QrPaymentUnavailableException PayliboUnavailable(Invoice invoice, Exception? innerException) =>
        new(EQrPaymentUnavailableReason.ProviderUnavailable,
            invoice.Id,
            "The QR payment code could not be generated right now because the external QR generator " +
            "is unavailable. Nothing is misconfigured on the invoice — please try again in a moment.",
            innerException);

    /// <summary>
    /// Parses a Czech bank account string into prefix, account number, and bank code.
    ///
    /// Supported formats (same parsing logic as Monarc.Core):
    /// - "1342333010/3030" → prefix=null, account="1342333010", bankCode="3030"
    /// - "19-1234567890/0100" → prefix="19", account="1234567890", bankCode="0100"
    /// - "1234567890" → prefix=null, account="1234567890", bankCode=null (no slash)
    /// </summary>
    private static (string? prefix, string? accountNumber, string? bankCode) ParseCzechBankAccount(string bankAccount)
    {
        string? prefix = null;
        string? accountNumber = null;
        string? bankCode = null;

        // Split by "/" to extract bank code
        var slashParts = bankAccount.Split('/');
        var accountPart = slashParts[0];
        if (slashParts.Length > 1)
        {
            bankCode = slashParts[1].Trim();
        }

        // Split by "-" to extract prefix (same logic as Monarc.Core)
        var dashParts = accountPart.Split('-');
        if (dashParts.Length > 1)
        {
            // Has prefix: "19-1234567890"
            prefix = dashParts[0].Trim();
            accountNumber = dashParts[1].Trim();
        }
        else
        {
            // No prefix: "1342333010"
            accountNumber = dashParts[0].Trim();
        }

        return (prefix, accountNumber, bankCode);
    }

    /// <summary>
    /// Generates a QR code PNG image from any string content using QRCoder.
    /// Used for local SPD and SIND QR code generation.
    /// </summary>
    private static byte[] GenerateQrPng(string content, int pixelsPerModule)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Loads an invoice with all related data needed for QR code generation:
    /// client, issuer, currency, and line items (with VAT rates).
    /// </summary>
    private async Task<Invoice> LoadInvoiceWithDetailsAsync(long invoiceId, CancellationToken ct)
    {
        return await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem).ThenInclude(item => item.VatRate)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException($"Invoice with ID {invoiceId} not found.");
    }

    /// <summary>
    /// Builds a SindBuilder populated with all available invoice data.
    /// Maps invoice fields to SIND attributes (Czech QR Faktura standard).
    /// </summary>
    private static SindBuilder BuildSindFromInvoice(Invoice invoice)
    {
        var builder = new SindBuilder();

        // === Required attributes ===
        builder.SetDocumentId(invoice.DocumentNumber ?? invoice.Id.ToString());
        builder.SetIssueDate(invoice.IssueDate ?? DateTime.UtcNow);
        builder.SetAmount(invoice.TotalWithVat);

        // === Payment attributes ===
        builder.SetVariableSymbol(invoice.VariableSymbol);
        builder.SetAccount(invoice.IBAN, invoice.SWIFT);
        builder.SetCurrency(invoice.Currency?.Code);
        builder.SetDueDate(invoice.DueDate);

        // === Issuer identification ===
        builder.SetIssuerTaxNumber(invoice.Issuer?.TaxNumber);
        builder.SetIssuerRegistrationNumber(invoice.Issuer?.RegistrationNumber);

        // === Recipient identification ===
        builder.SetRecipientTaxNumber(invoice.Client?.TaxNumber);
        builder.SetRecipientRegistrationNumber(invoice.Client?.RegistrationNumber);

        // === Tax dates ===
        builder.SetTaxableSupplyDate(invoice.TaxableSupplyDate);

        // === Document type: 1=corrective (credit note), 9=other (standard invoice) ===
        builder.SetDocumentType(invoice.DocumentType == EDocumentType.CreditNote ? 1 : 9);

        // === VAT breakdown by rate group ===
        SetVatBreakdown(builder, invoice);

        // === Software identifier (proprietary attribute) ===
        builder.SetSoftware(SoftwareName);

        return builder;
    }

    /// <summary>
    /// Groups invoice items by VAT rate and sets the appropriate SIND tax attributes.
    /// Czech VAT rates: Standard (21%) → TB0/T0, First reduced (12%) → TB1/T1, Zero → NTB.
    /// </summary>
    private static void SetVatBreakdown(SindBuilder builder, Invoice invoice)
    {
        if (invoice.InvoiceItem == null || !invoice.InvoiceItem.Any())
        {
            builder.SetNonTaxableAmount(invoice.TotalWithVat);
            return;
        }

        var groups = invoice.InvoiceItem
            .GroupBy(item => item.VatRatePercentage)
            .ToDictionary(g => g.Key, g => new
            {
                TaxBase = g.Sum(i => i.TotalBeforeVat),
                TaxAmount = g.Sum(i => i.VatAmount)
            });

        if (groups.TryGetValue(21m, out var standard))
            builder.SetStandardVat(standard.TaxBase, standard.TaxAmount);

        if (groups.TryGetValue(12m, out var reduced1))
            builder.SetReducedVat1(reduced1.TaxBase, reduced1.TaxAmount);

        if (groups.TryGetValue(0m, out var zeroRate))
            builder.SetNonTaxableAmount(zeroRate.TaxBase);
    }
}
