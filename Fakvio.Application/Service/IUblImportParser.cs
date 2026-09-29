using Fakvio.Application.QrPayment;

namespace Fakvio.Application.Service;

/// <summary>
/// Parses UBL 2.1 / Peppol BIS Billing 3.0 invoices and credit notes into
/// structured invoice data — the import counterpart of <see cref="IIsdocImportParser"/>,
/// for the Slovak mandatory e-invoicing format from 2027
/// (see docs/adr/0002-sk-einvoicing-peppol.md, task F1.10).
///
/// Synchronous — pure XML deserialization, no AI and no network calls.
/// Implementations must treat the input as untrusted (email attachment or
/// user upload) and never throw out of <see cref="Parse"/>.
/// </summary>
public interface IUblImportParser
{
    /// <summary>
    /// Parses a UBL XML document (an <c>Invoice</c> or <c>CreditNote</c> root, in the
    /// respective OASIS UBL 2.1 namespace) into extracted invoice data.
    /// </summary>
    /// <param name="xmlBytes">Raw XML bytes, e.g. an email attachment or an uploaded file.</param>
    /// <returns>
    /// Extracted data, or null if the bytes are not a well-formed, recognized UBL
    /// Invoice/CreditNote — malformed XML, a DOCTYPE (rejected outright, see implementation),
    /// an oversized payload, or an unknown root element all result in null rather than
    /// an exception.
    /// </returns>
    InvoiceExtractedData? Parse(byte[] xmlBytes);
}
