namespace Fakvio.Application.Service;

/// <summary>
/// Service interface for generating UBL 2.1 / Peppol BIS Billing 3.0 XML exports from invoices
/// (ADR 0002, F1.5). Complements <see cref="IIsdocExportService"/> — same invoice, a second
/// standard, aimed at SK e-invoicing (and, longer term, ViDA cross-border B2B).
/// </summary>
public interface IUblExportService
{
    /// <summary>
    /// Generates the UBL XML for the specified invoice and returns UTF-8 bytes.
    /// </summary>
    /// <param name="invoiceId">Database ID of the invoice to export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UTF-8 encoded bytes of the UBL XML document.</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">When invoice not found.</exception>
    /// <exception cref="Fakvio.Application.Exceptions.TenantNotReadyException">
    /// When the invoice is not ready to export (Draft, pro-forma, missing Peppol ID, …) —
    /// carries the blocking <c>EINVOICE_*</c> issue codes.
    /// </exception>
    Task<byte[]> ExportInvoiceAsync(long invoiceId, CancellationToken ct = default);
}
