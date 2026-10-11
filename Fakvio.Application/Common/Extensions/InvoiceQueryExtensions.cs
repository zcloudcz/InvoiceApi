using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Common.Extensions;

/// <summary>
/// The <c>Invoice</c> table holds BOTH issued documents (invoice, credit note, proforma, tax receipt) and
/// received (supplier) invoices - they differ only by <see cref="Invoice.DocumentType"/>.
/// Every query on the <c>Invoice</c> DbSet must therefore say which side it wants, otherwise e.g. payment
/// reminders would be mailed to suppliers or supplier invoices would be counted as revenue.
/// <c>InvoiceDbSetUsageGuardTests</c> fails the build when a query forgets it.
/// </summary>
public static class InvoiceQueryExtensions
{
    /// <summary>Only documents WE issued (everything except received supplier invoices).</summary>
    public static IQueryable<Invoice> IssuedOnly(this IQueryable<Invoice> query) =>
        query.Where(i => i.DocumentType != EDocumentType.ReceivedInvoice);

    /// <summary>Only received (supplier) invoices.</summary>
    public static IQueryable<Invoice> ReceivedOnly(this IQueryable<Invoice> query) =>
        query.Where(i => i.DocumentType == EDocumentType.ReceivedInvoice);

    /// <summary>
    /// ANY document of ANY type, issued or received - for the unified document operations (get / update / transitions /
    /// delete by id, scoped lists) where the type is taken from the stored row. Still excludes invoice TEMPLATES,
    /// which share the table through the <c>InvoiceType</c> discriminator and are not documents.
    /// Reports, VAT, dashboards, dunning and number sequences must NOT use this: they are about one side only.
    /// </summary>
    public static IQueryable<Invoice> AnyDocument(this IQueryable<Invoice> query) =>
        query.Where(i => !(i is InvoiceTemplate));
}
