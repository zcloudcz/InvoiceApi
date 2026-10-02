using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// One implementation per target accounting system (Pohoda, Money S3, ABRA Flexi).
/// Pure mapper: no database access, no I/O — takes already-loaded entities and
/// returns a ready-to-download XML file. Mirrors the IsdocMapper/UblMapper split
/// (DB loading stays in <see cref="IAccountingExportService"/>, mapping is pure).
/// </summary>
public interface IAccountingExporter
{
    /// <summary>Which <see cref="EAccountingSystem"/> this implementation produces XML for.</summary>
    EAccountingSystem System { get; }

    /// <summary>File extension to use for the downloaded file, without the dot (e.g. "xml").</summary>
    string FileExtension { get; }

    /// <summary>HTTP content type of the produced file (e.g. "application/xml").</summary>
    string ContentType { get; }

    /// <summary>
    /// Builds a single XML document containing both issued and received invoices
    /// (whichever lists are non-empty — callers filter by the IncludeIssued/IncludeReceived
    /// request flags before calling this method).
    /// </summary>
    /// <param name="issuedInvoices">Issued invoices/credit notes/proformas to export (already date-filtered).</param>
    /// <param name="receivedInvoices">Received invoices to export (already date-filtered).</param>
    /// <param name="issuer">The tenant's own company (Client with IsIssuer = true) — used as "our company" on every document.</param>
    /// <returns>UTF-8 or system-specific encoded bytes of the XML document (see implementation remarks).</returns>
    byte[] Export(
        IReadOnlyList<Invoice> issuedInvoices,
        IReadOnlyList<ReceivedInvoice> receivedInvoices,
        Client issuer);
}
