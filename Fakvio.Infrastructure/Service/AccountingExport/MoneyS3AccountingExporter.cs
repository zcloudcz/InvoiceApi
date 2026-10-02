using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.AccountingExport;

/// <summary>
/// Exports issued and received invoices as a Money S3 "MoneyData" XML import file
/// (Money.cz / Seyfor — Soubor &gt; Import &gt; XML). Issued invoices go under
/// &lt;SeznamFaktVyd&gt;, received invoices under &lt;SeznamFaktPrij&gt;.
///
/// Known gaps (DEVGUIDE §4.15): mapped from the publicly documented "Money S3 – XML formát"
/// element names, not against a locally validated XSD — same caveat as the Pohoda exporter.
/// SazbaDPH is emitted as the plain percentage (e.g. "21") rather than Money's named rate slot,
/// which importers typically accept but should be verified against the target database's
/// VAT rate table before bulk use.
/// </summary>
public class MoneyS3AccountingExporter : IAccountingExporter
{
    public EAccountingSystem System => EAccountingSystem.MoneyS3;
    public string FileExtension => "xml";
    public string ContentType => "application/xml";

    public byte[] Export(IReadOnlyList<Invoice> issuedInvoices, IReadOnlyList<ReceivedInvoice> receivedInvoices, Client issuer)
    {
        var root = new XElement("MoneyData");

        if (issuedInvoices.Count > 0)
            root.Add(new XElement("SeznamFaktVyd", issuedInvoices.Select(BuildIssued)));

        if (receivedInvoices.Count > 0)
            root.Add(new XElement("SeznamFaktPrij", receivedInvoices.Select(BuildReceived)));

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);

        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  "
        };
        using (var writer = XmlWriter.Create(ms, settings))
            document.WriteTo(writer);
        return ms.ToArray();
    }

    private static XElement BuildIssued(Invoice invoice)
    {
        var el = new XElement("FaktVyd",
            new XElement("Doklad", invoice.DocumentNumber ?? string.Empty),
            new XElement("VarSymbol", invoice.VariableSymbol ?? invoice.DocumentNumber ?? string.Empty),
            new XElement("TypDokladu", invoice.DocumentType == EDocumentType.CreditNote ? "DobropisVyd" : "FakturaVyd"),
            new XElement("Vystaveno", AccountingExportCommon.FormatDate(invoice.IssueDate)),
            new XElement("DatUcPr", AccountingExportCommon.FormatDate(invoice.IssueDate)),
            new XElement("DatSplat", AccountingExportCommon.FormatDate(invoice.DueDate)),
            new XElement("DatZdPlnSouhrn", AccountingExportCommon.FormatDate(invoice.TaxableSupplyDate ?? invoice.IssueDate)));

        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            el.Add(new XElement("Popis", invoice.Notes));

        el.Add(BuildAdresa(invoice.Client));

        var items = invoice.InvoiceItem?.Where(i => !i.IsTextRow).OrderBy(i => i.OrderIndex).ToList() ?? [];
        var currencyCode = invoice.Currency?.Code ?? "CZK";
        el.Add(new XElement("SeznamPolozek", items.Select(i => BuildPolozka(
            i.Description, i.Quantity, i.Unit, i.UnitPrice, i.VatRatePercentage, currencyCode))));

        el.Add(new XElement("ZaklCelkem", AccountingExportCommon.FormatDecimal(invoice.TotalBeforeVat)));
        el.Add(new XElement("DphCelkem", AccountingExportCommon.FormatDecimal(invoice.TotalVat)));
        el.Add(new XElement("Celkem", AccountingExportCommon.FormatDecimal(invoice.TotalWithVat)));

        return el;
    }

    private static XElement BuildReceived(ReceivedInvoice invoice)
    {
        var el = new XElement("FaktPrij",
            new XElement("Doklad", invoice.DocumentNumber ?? string.Empty),
            new XElement("VarSymbol", invoice.VariableSymbol ?? invoice.DocumentNumber ?? string.Empty),
            new XElement("Vystaveno", AccountingExportCommon.FormatDate(invoice.IssueDate)),
            new XElement("DatUcPr", AccountingExportCommon.FormatDate(invoice.ReceivedDate ?? invoice.IssueDate)),
            new XElement("DatSplat", AccountingExportCommon.FormatDate(invoice.DueDate)),
            new XElement("DatZdPlnSouhrn", AccountingExportCommon.FormatDate(invoice.TaxableSupplyDate ?? invoice.IssueDate)));

        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            el.Add(new XElement("Popis", invoice.Notes));

        el.Add(BuildAdresa(invoice.Supplier));

        var items = invoice.Items?.OrderBy(i => i.OrderIndex).ToList() ?? [];
        var currencyCode = invoice.Currency?.Code ?? "CZK";
        el.Add(new XElement("SeznamPolozek", items.Select(i => BuildPolozka(
            i.Description, i.Quantity, i.Unit, i.UnitPrice, i.VatRatePercentage, currencyCode))));

        el.Add(new XElement("ZaklCelkem", AccountingExportCommon.FormatDecimal(invoice.TotalBeforeVat)));
        el.Add(new XElement("DphCelkem", AccountingExportCommon.FormatDecimal(invoice.TotalVat)));
        el.Add(new XElement("Celkem", AccountingExportCommon.FormatDecimal(invoice.TotalWithVat)));

        return el;
    }

    private static XElement BuildAdresa(Client? partner)
    {
        partner ??= new Client();
        var address = AccountingExportCommon.PrimaryAddress(partner);

        var el = new XElement("Adresa",
            new XElement("Firma", partner.CompanyName ?? string.Empty),
            new XElement("Ulice", address?.Street ?? string.Empty),
            new XElement("Misto", address?.City ?? string.Empty),
            new XElement("PSC", address?.PostalCode ?? string.Empty),
            new XElement("ICO", partner.RegistrationNumber ?? string.Empty));

        if (!string.IsNullOrWhiteSpace(partner.TaxNumber))
            el.Add(new XElement("DIC", partner.TaxNumber));

        return el;
    }

    private static XElement BuildPolozka(
        string description, decimal quantity, string unit, decimal unitPrice,
        decimal vatRatePercentage, string currencyCode)
    {
        var el = new XElement("Polozka",
            new XElement("Popis", description),
            new XElement("PocetMJ", AccountingExportCommon.FormatDecimal(quantity)),
            new XElement("MJ", unit),
            new XElement("Cena", AccountingExportCommon.FormatDecimal(unitPrice)),
            new XElement("SazbaDPH", AccountingExportCommon.FormatDecimal(vatRatePercentage)));

        // Only emit <Valuty> for non-CZK documents — Money S3 treats its absence as "home currency".
        if (!currencyCode.Equals("CZK", StringComparison.OrdinalIgnoreCase))
            el.Add(new XElement("Valuty", new XElement("Mena", currencyCode)));

        return el;
    }
}
