using Fakvio.Application.Common.Extensions;
using Fakvio.Contracts.Common;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Loads everything a document file name needs in ONE query and builds the name via
/// <see cref="DocumentFileName"/>. The configured format is picked per document type:
/// client's BillingSettings → issuer's BillingSettings (company-wide default) → system default.
/// </summary>
public class DocumentFileNameService : IDocumentFileNameService
{
    private readonly TenantDbContext _context;

    public DocumentFileNameService(TenantDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<string> GetFileNameAsync(long invoiceId, string extension, CancellationToken ct = default)
    {
        var names = await GetFileNamesAsync([invoiceId], extension, ct);
        return names.TryGetValue(invoiceId, out var name)
            ? name
            : throw new KeyNotFoundException($"Invoice with ID {invoiceId} not found.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, string>> GetFileNamesAsync(
        IReadOnlyCollection<long> invoiceIds, string extension, CancellationToken ct = default)
    {
        if (invoiceIds.Count == 0)
            return new Dictionary<long, string>();

        // One projection query for all invoices: the client and issuer BillingSettings are joined
        // (LEFT JOIN — both are optional), so there is no per-invoice round trip.
        // Client is nullable on Invoice, hence the null checks.
        var rows = await _context.Invoice.IssuedOnly()
            .AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id))
            .Select(i => new
            {
                i.Id,
                i.DocumentType,
                i.DocumentNumber,
                i.VariableSymbol,
                i.IssueDate,
                i.TaxableSupplyDate,
                i.DueDate,
                ClientName = i.Client != null ? i.Client.CompanyName : null,
                Language = i.Client != null ? i.Client.Language : null,
                IssuerName = i.Issuer.CompanyName,
                ClientInvoice = i.Client!.BillingSettings!.InvoiceFileNameFormat,
                ClientCreditNote = i.Client!.BillingSettings!.CreditNoteFileNameFormat,
                ClientProforma = i.Client!.BillingSettings!.ProformaFileNameFormat,
                ClientTaxReceipt = i.Client!.BillingSettings!.TaxReceiptFileNameFormat,
                IssuerInvoice = i.Issuer.BillingSettings!.InvoiceFileNameFormat,
                IssuerCreditNote = i.Issuer.BillingSettings!.CreditNoteFileNameFormat,
                IssuerProforma = i.Issuer.BillingSettings!.ProformaFileNameFormat,
                IssuerTaxReceipt = i.Issuer.BillingSettings!.TaxReceiptFileNameFormat
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Id,
            r =>
            {
                var (clientFormat, issuerFormat) = r.DocumentType switch
                {
                    EDocumentType.CreditNote => (r.ClientCreditNote, r.IssuerCreditNote),
                    EDocumentType.Proforma => (r.ClientProforma, r.IssuerProforma),
                    EDocumentType.TaxReceiptForAdvance => (r.ClientTaxReceipt, r.IssuerTaxReceipt),
                    _ => (r.ClientInvoice, r.IssuerInvoice)
                };

                return DocumentFileName.Build(
                    DocumentFileName.ResolveFormat(clientFormat, issuerFormat),
                    new DocumentFileNameValues(
                        r.DocumentType, r.Language, r.Id, r.DocumentNumber, r.VariableSymbol,
                        r.IssueDate, r.ClientName, r.IssuerName,
                        r.TaxableSupplyDate, r.DueDate),
                    extension);
            });
    }
}
