using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.Ubl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Generates UBL 2.1 / Peppol BIS Billing 3.0 XML exports from invoices stored in the tenant
/// database (ADR 0002, F1.5). Loads the invoice with all related data, runs the per-document
/// pre-flight (<see cref="UblPreflight"/>), delegates XML mapping to <see cref="UblMapper"/>,
/// then serialises the output — same shape as <see cref="IsdocExportService"/>.
/// </summary>
public class UblExportService : IUblExportService
{
    private readonly TenantDbContext _db;
    private readonly ILogger<UblExportService> _logger;

    /// <summary>
    /// Above this absolute difference (currency units) between the mapper's recomputed VAT
    /// total and the invoice's stored <c>TotalVat</c>, the PDF and the UBL export would show
    /// different numbers — worth a warning to investigate, but not worth blocking the export
    /// over (the UBL total is always the one that is internally consistent per BR-CO-17).
    /// </summary>
    private const decimal VatDiscrepancyWarningThreshold = 0.05m;

    public UblExportService(TenantDbContext db, ILogger<UblExportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<byte[]> ExportInvoiceAsync(long invoiceId, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting UBL export for invoice {InvoiceId}", invoiceId);

        // Same loading strategy as IsdocExportService: read-only, split query (avoids a
        // cartesian explosion across the Issuer/Client Address+Contact collections), soft-deleted
        // invoices excluded.
        var invoice = await _db.Invoice
            .AsNoTracking()
            .AsSplitQuery()
            .Where(i => i.Status != EInvoiceStatus.Deleted)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem)
            .Include(i => i.Issuer)
                .ThenInclude(c => c!.Address)
            .Include(i => i.Issuer)
                .ThenInclude(c => c!.Contact)
            .Include(i => i.Client)
                .ThenInclude(c => c!.Address)
            .Include(i => i.Client)
                .ThenInclude(c => c!.Contact)
            .Include(i => i.OriginalInvoice)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

        if (invoice == null)
        {
            _logger.LogError("Invoice {InvoiceId} not found for UBL export", invoiceId);
            throw new KeyNotFoundException($"Invoice {invoiceId} not found.");
        }

        var blockingIssues = UblPreflight.Check(invoice);
        if (blockingIssues.Count > 0)
        {
            _logger.LogWarning(
                "UBL export refused for invoice {InvoiceId} — not ready: {Codes}",
                invoiceId, string.Join(", ", blockingIssues.Select(i => i.Code)));
            throw new TenantNotReadyException(blockingIssues);
        }

        var precedingDocumentNumbers = await ResolvePrecedingDocumentNumbersAsync(invoice, ct);

        var document = UblMapper.Map(invoice, precedingDocumentNumbers);
        WarnIfVatTotalDiffers(document, invoiceId, invoice.TotalVat);
        return SerialiseToBytes(document);
    }

    // --------------------------------------------------------------------------
    // BillingReference resolution — the one piece of DB knowledge UblMapper itself
    // does not have (it only maps whatever list it is handed).
    // --------------------------------------------------------------------------

    private async Task<IReadOnlyList<string>?> ResolvePrecedingDocumentNumbersAsync(
        Invoice invoice, CancellationToken ct)
    {
        if (invoice.DocumentType == EDocumentType.CreditNote)
        {
            // A credit note references the invoice it corrects.
            return invoice.OriginalInvoice?.DocumentNumber is { } correctedNumber
                ? [correctedNumber]
                : null;
        }

        if (invoice.DocumentType != EDocumentType.Invoice || invoice.OriginalInvoiceId is null)
            return null;

        // A "final invoice closing a pro-forma" has OriginalInvoiceId pointing at the pro-forma
        // (InvoiceService sets this when generating the closing invoice). Its BillingReference
        // lists every tax receipt for advance already issued against that same pro-forma —
        // the reader needs those to reconcile the deduction row against what was already paid.
        // Excludes Draft explicitly (not just "DocumentNumber != null" as a proxy for it) —
        // a draft tax receipt is not "already issued" yet even on the rare path where it somehow
        // carries a document number.
        var taxReceiptNumbers = await _db.Invoice
            .AsNoTracking()
            .Where(i => i.OriginalInvoiceId == invoice.OriginalInvoiceId
                     && i.DocumentType == EDocumentType.TaxReceiptForAdvance
                     && i.Status != EInvoiceStatus.Deleted
                     && i.Status != EInvoiceStatus.Draft
                     && i.DocumentNumber != null)
            .OrderBy(i => i.IssueDate)
            .Select(i => i.DocumentNumber!)
            .ToListAsync(ct);

        return taxReceiptNumbers.Count > 0 ? taxReceiptNumbers : null;
    }

    // --------------------------------------------------------------------------
    // VAT total cross-check (non-blocking: warns but does not throw)
    // --------------------------------------------------------------------------

    private void WarnIfVatTotalDiffers(XDocument document, long invoiceId, decimal storedTotalVat)
    {
        // cac:TaxTotal/cbc:TaxAmount lives directly under the document root regardless of
        // whether that root is Invoice-2 or CreditNote-2 — no need to branch on document type.
        var taxAmountText = document.Root?
            .Element(UblMapper.CacNs + "TaxTotal")?
            .Element(UblMapper.CbcNs + "TaxAmount")?
            .Value;

        if (taxAmountText == null || !decimal.TryParse(taxAmountText, System.Globalization.CultureInfo.InvariantCulture, out var computedTaxAmount))
            return;

        // A credit note's stored TotalVat can carry either sign (ADR 0002 §4.1.2 — Fakvio does
        // not enforce one), while the mapped UBL TaxAmount is always positive — compare
        // magnitudes only, the sign difference is expected, not a discrepancy.
        var storedMagnitude = Math.Abs(storedTotalVat);
        var diff = Math.Abs(computedTaxAmount - storedMagnitude);
        if (diff > VatDiscrepancyWarningThreshold)
        {
            _logger.LogWarning(
                "UBL export for invoice {InvoiceId}: recomputed VAT total {ComputedVat} differs from " +
                "the stored Invoice.TotalVat {StoredVat} by {Diff} — PDF and UBL would show different " +
                "numbers, worth investigating (not auto-corrected).",
                invoiceId, computedTaxAmount, storedMagnitude, diff);
        }
    }

    // --------------------------------------------------------------------------
    // Serialisation
    // --------------------------------------------------------------------------

    private static byte[] SerialiseToBytes(XDocument document)
    {
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  "
        };
        using (var writer = XmlWriter.Create(ms, settings))
        {
            document.WriteTo(writer);
        }
        return ms.ToArray();
    }
}
