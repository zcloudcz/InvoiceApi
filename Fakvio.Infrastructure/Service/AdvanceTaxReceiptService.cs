using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Creates a tax receipt for advance payment (daňový doklad o přijaté platbě / DPP)
/// from a paid pro-forma invoice.
///
/// This service is the single place that contains the DPP creation logic.
/// It is called by two different entry points depending on EAdvanceTaxReceiptMode:
///   - PaymentMatchingService (OnPaymentMatch + OnAnyPayment modes)
///   - InvoiceService.MarkAsPaidAsync (OnAnyPayment mode only)
///
/// Logging relies on ILogger&lt;AdvanceTaxReceiptService&gt;. In production the
/// DatabaseLoggerProvider (registered in DI) forwards structured log entries to
/// the AppLog table in the tenant database, so all DPP creation events are
/// visible in the SysAdmin log viewer without extra instrumentation.
///
/// See IAdvanceTaxReceiptService for the full contract description.
/// </summary>
public class AdvanceTaxReceiptService : IAdvanceTaxReceiptService
{
    private readonly TenantDbContext _context;
    private readonly INumberSequenceService _numberSequenceService;
    private readonly ILogger<AdvanceTaxReceiptService> _logger;

    public AdvanceTaxReceiptService(
        TenantDbContext context,
        INumberSequenceService numberSequenceService,
        ILogger<AdvanceTaxReceiptService> logger)
    {
        _context = context;
        _numberSequenceService = numberSequenceService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<long?> IssueFromPaidProformaAsync(
        long proformaInvoiceId,
        decimal paidAmount,
        DateTime paymentDate,
        CancellationToken ct = default)
    {
        // Load the pro-forma with its items and related entities needed for the DPP.
        var proforma = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .Include(i => i.Issuer)
            .Include(i => i.Client)
            .Include(i => i.Currency)
            .FirstOrDefaultAsync(i => i.Id == proformaInvoiceId, ct);

        if (proforma == null)
        {
            _logger.LogWarning(
                "AdvanceTaxReceiptService: pro-forma {ProformaId} not found — skipping DPP creation",
                proformaInvoiceId);
            return null;
        }

        if (proforma.DocumentType != EDocumentType.Proforma)
        {
            _logger.LogWarning(
                "AdvanceTaxReceiptService: invoice {InvoiceId} is {Type}, not Proforma — skipping DPP creation",
                proformaInvoiceId, proforma.DocumentType);
            return null;
        }

        // ── Idempotence guard ─────────────────────────────────────────────────
        // If a TaxReceiptForAdvance already exists for this pro-forma (in any non-deleted
        // status), return its ID without creating a duplicate.
        var existingDpp = await _context.Invoice
            .AsNoTracking()
            .Where(i => i.OriginalInvoiceId == proformaInvoiceId
                     && i.DocumentType == EDocumentType.TaxReceiptForAdvance
                     && i.Status != EInvoiceStatus.Deleted)
            .Select(i => i.Id)
            .FirstOrDefaultAsync(ct);

        if (existingDpp != 0)
        {
            _logger.LogInformation(
                "AdvanceTaxReceiptService: DPP {DppId} already exists for pro-forma {ProformaId} — idempotent no-op",
                existingDpp, proformaInvoiceId);
            return existingDpp;
        }

        // ── Overpayment check ─────────────────────────────────────────────────
        // When the received payment exceeds the pro-forma total, we still issue the
        // DPP for the full paidAmount and flag it for manual review.
        var isOverpayment = paidAmount > proforma.TotalWithVat;
        if (isOverpayment)
        {
            _logger.LogWarning(
                "AdvanceTaxReceiptService: overpayment detected for pro-forma {ProformaId}. " +
                "Paid={PaidAmount}, ProformaTotal={ProformaTotal}. DPP will be issued for full paid amount with HasAlert=true",
                proformaInvoiceId, paidAmount, proforma.TotalWithVat);
        }

        // ── Generate document number ──────────────────────────────────────────
        // Use the default TaxReceiptForAdvance sequence (DPP-* prefix, configured by issue #2).
        // paymentDate is used for year/month placeholders in the format string.
        var paymentDateUtc = DateTime.SpecifyKind(paymentDate.Date, DateTimeKind.Utc);

        string documentNumber;
        try
        {
            documentNumber = await _numberSequenceService.GenerateNextNumberForDocumentTypeAsync(
                EDocumentType.TaxReceiptForAdvance,
                paymentDateUtc,
                cancellationToken: ct);
        }
        catch (InvalidOperationException ex)
        {
            // Fallback when no TaxReceiptForAdvance sequence is configured yet.
            _logger.LogWarning(
                "AdvanceTaxReceiptService: no number sequence for TaxReceiptForAdvance — using fallback. {Message}",
                ex.Message);
            var year = paymentDateUtc.Year;
            var count = await _context.Invoice
                .Where(i => i.DocumentType == EDocumentType.TaxReceiptForAdvance
                         && i.Status != EInvoiceStatus.Deleted)
                .CountAsync(ct);
            documentNumber = $"DPP{year:0000}{(count + 1):000}";
        }

        // ── Variable symbol ───────────────────────────────────────────────────
        // The DPP carries the same variable symbol as the pro-forma so that any
        // incoming bank reference can be matched to either document.
        var variableSymbol = proforma.VariableSymbol;

        // ── VAT breakdown — proportional split ───────────────────────────────
        // Each VAT rate used in the pro-forma gets one DPP line item.
        // The amounts are scaled proportionally: if the pro-forma had 60 % at 21 %
        // VAT and 40 % at 12 % VAT, the DPP keeps the same ratio, applied to paidAmount.
        //
        // This is the legally correct approach: the DPP must reflect the VAT composition
        // of the advance received, not just a flat amount.
        //
        // When paidAmount == proforma.TotalWithVat (exact payment), the proportional
        // calculation reproduces the original items exactly.
        // When paidAmount > proforma.TotalWithVat (overpayment), the excess is distributed
        // across the existing VAT rates proportionally (HasAlert flags this for the accountant).
        var dppItems = BuildProportionalItems(proforma, paidAmount);

        // ── Create the DPP entity ─────────────────────────────────────────────
        decimal totalBeforeVat = dppItems.Sum(i => i.TotalBeforeVat);
        decimal totalVat       = dppItems.Sum(i => i.VatAmount);
        decimal totalWithVat   = totalBeforeVat + totalVat;

        var dpp = new Invoice
        {
            DocumentType        = EDocumentType.TaxReceiptForAdvance,
            // DPP is issued immediately as Completed — it is not a draft that a user edits.
            // Czech tax law requires it to be issued on the day of payment receipt.
            Status              = EInvoiceStatus.Completed,
            DocumentNumber      = documentNumber,
            IssueDate           = paymentDateUtc,
            // DUZP (datum uskutečnění zdanitelného plnění) = day of payment for advance receipts.
            // Czech VAT Act §21/1b: DPP becomes taxable on the day the advance is received.
            TaxableSupplyDate   = paymentDateUtc,
            DueDate             = paymentDateUtc, // DPP doesn't collect money — due = issue
            IssuerId            = proforma.IssuerId,
            ClientId            = proforma.ClientId,
            CurrencyId          = proforma.CurrencyId,
            OriginalInvoiceId   = proforma.Id,
            VariableSymbol      = variableSymbol,
            TotalBeforeVat      = totalBeforeVat,
            TotalVat            = totalVat,
            TotalWithVat        = totalWithVat,
            // Set HasAlert when the DPP covers an overpayment — the accountant must
            // decide how to handle the excess (refund, credit, or leave as deposit).
            // Issue #8 will surface this flag in the UI.
            HasAlert            = isOverpayment,
            InvoiceItem         = dppItems
        };

        _context.Invoice.Add(dpp);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "AdvanceTaxReceiptService: created DPP {DppId} ({DocNum}) for pro-forma {ProformaId}. " +
            "PaidAmount={PaidAmount}, Overpayment={IsOverpayment}, HasAlert={HasAlert}",
            dpp.Id, dpp.DocumentNumber, proformaInvoiceId, paidAmount, isOverpayment, dpp.HasAlert);

        return dpp.Id;
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the DPP line items by distributing <paramref name="paidAmount"/> across
    /// the VAT rates used in the pro-forma, proportionally to the original item amounts.
    ///
    /// Algorithm:
    ///   1. Group pro-forma items by VatRateId (null = 0 % VAT bucket).
    ///   2. For each VAT bucket, compute its share = bucket.TotalWithVat / proforma.TotalWithVat.
    ///   3. DPP item TotalWithVat = share * paidAmount.
    ///   4. Back-calculate TotalBeforeVat and VatAmount from the VAT rate percentage.
    ///
    /// When the pro-forma has TotalWithVat = 0 (unusual edge case), all amounts land
    /// in a single item with 0 % VAT so we still produce a valid document.
    ///
    /// Rounding: values are rounded to 2 decimal places per item. Any rounding residual
    /// is absorbed into the last item to keep Sum(TotalWithVat) == paidAmount exactly.
    /// </summary>
    private static List<InvoiceItem> BuildProportionalItems(Invoice proforma, decimal paidAmount)
    {
        var billableItems = proforma.InvoiceItem
            .Where(i => !i.IsTextRow)
            .ToList();

        // Fallback: when the pro-forma has no billable items or zero total,
        // create a single 0 %-VAT item for the full paid amount.
        if (!billableItems.Any() || proforma.TotalWithVat == 0m)
        {
            var fallbackTotal = Math.Round(paidAmount, 2);
            return new List<InvoiceItem>
            {
                new()
                {
                    OrderIndex       = 1,
                    Description      = "Přijatá záloha / Advance payment received",
                    Quantity         = 1,
                    Unit             = "pcs",
                    UnitPrice        = fallbackTotal,
                    VatRateId        = null,
                    VatRatePercentage = 0m,
                    TotalBeforeVat   = fallbackTotal,
                    VatAmount        = 0m,
                    TotalWithVat     = fallbackTotal
                }
            };
        }

        // Group items by VAT rate (null VatRateId maps to 0 % bucket).
        var vatGroups = billableItems
            .GroupBy(i => i.VatRatePercentage)
            .Select(g => new
            {
                VatRateId          = g.First().VatRateId,
                VatRatePercentage  = g.Key,
                GroupTotalWithVat  = g.Sum(i => i.TotalWithVat)
            })
            .OrderBy(g => g.VatRatePercentage)
            .ToList();

        var result = new List<InvoiceItem>();
        decimal assignedTotal = 0m;

        for (int idx = 0; idx < vatGroups.Count; idx++)
        {
            var group = vatGroups[idx];
            bool isLast = idx == vatGroups.Count - 1;

            // Proportional share of the paid amount for this VAT bucket.
            decimal itemTotalWithVat;
            if (isLast)
            {
                // Last item absorbs any rounding residual so the sum is exact.
                itemTotalWithVat = paidAmount - assignedTotal;
            }
            else
            {
                var share = group.GroupTotalWithVat / proforma.TotalWithVat;
                itemTotalWithVat = Math.Round(share * paidAmount, 2);
            }

            // Back-calculate base and VAT from the total-with-vat amount.
            // TotalWithVat = TotalBeforeVat * (1 + rate/100)
            // → TotalBeforeVat = TotalWithVat / (1 + rate/100)
            decimal vatMultiplier = 1m + (group.VatRatePercentage / 100m);
            decimal itemBeforeVat;
            decimal itemVatAmount;

            if (vatMultiplier == 0m)
            {
                // Defensive: VAT rate of -100 % would cause division by zero — treat as zero VAT.
                itemBeforeVat = itemTotalWithVat;
                itemVatAmount = 0m;
            }
            else
            {
                itemBeforeVat = Math.Round(itemTotalWithVat / vatMultiplier, 2);
                itemVatAmount = itemTotalWithVat - itemBeforeVat;
            }

            result.Add(new InvoiceItem
            {
                OrderIndex        = idx + 1,
                Description       = "Přijatá záloha / Advance payment received",
                Quantity          = 1,
                Unit              = "pcs",
                UnitPrice         = itemBeforeVat,
                VatRateId         = group.VatRateId,
                VatRatePercentage = group.VatRatePercentage,
                TotalBeforeVat    = itemBeforeVat,
                VatAmount         = itemVatAmount,
                TotalWithVat      = itemTotalWithVat
            });

            assignedTotal += itemTotalWithVat;
        }

        return result;
    }
}
