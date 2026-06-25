using Fakvio.Application.QrPayment;

namespace Fakvio.Application.Service;

/// <summary>
/// Parses ISDOC 6.0.2 XML into structured invoice data.
/// Synchronous — pure XML deserialization, no AI or external calls.
/// Most reliable extraction path (structured format, no ambiguity).
/// </summary>
public interface IIsdocImportParser
{
    /// <summary>
    /// Parses an ISDOC XML string into extracted invoice data.
    /// Returns null if the XML is malformed or missing critical fields.
    /// </summary>
    /// <param name="isdocXml">Raw ISDOC XML content.</param>
    InvoiceExtractedData? Parse(string isdocXml);
}
