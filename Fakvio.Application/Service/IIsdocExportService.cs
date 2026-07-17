namespace Fakvio.Application.Service;

/// <summary>
/// Service interface for generating ISDOC 6.0.2 XML exports from invoices.
/// ISDOC is the Czech electronic invoice standard understood by Pohoda, Money, Helios.
/// </summary>
public interface IIsdocExportService
{
    /// <summary>
    /// Generates ISDOC 6.0.2 XML for the specified invoice and returns UTF-8 bytes.
    /// </summary>
    /// <param name="invoiceId">Database ID of the invoice to export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UTF-8 encoded bytes of the ISDOC XML document.</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">When invoice not found.</exception>
    Task<byte[]> ExportInvoiceAsync(long invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Generates ISDOC 6.0.2 XML for the specified received (incoming) invoice
    /// and returns UTF-8 bytes. The supplier on the document is the invoice's
    /// supplier; the customer is the tenant's own company (Client with IsIssuer = true).
    /// </summary>
    /// <param name="receivedInvoiceId">Database ID of the received invoice to export.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>UTF-8 encoded bytes of the ISDOC XML document.</returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">When received invoice not found.</exception>
    Task<byte[]> ExportReceivedInvoiceAsync(long receivedInvoiceId, CancellationToken ct = default);
}