using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.AccountingExport;

/// <summary>
/// Exports issued and received invoices as an ABRA Flexi "winstrom" XML import file
/// (importable via Nástroje &gt; Import XML, or POSTed directly to the Flexi REST API —
/// see https://podpora.flexibee.eu/ and the demo evidence at
/// https://demo.flexibee.eu/c/demo/faktura-vydana/properties for the field catalogue).
/// Issued invoices become &lt;faktura-vydana&gt;, received invoices &lt;faktura-prijata&gt;.
///
/// Known gaps (DEVGUIDE §4.15): mapped from the publicly documented Flexi evidence field
/// names, not against a locally embedded/validated XSD — same caveat as the other two
/// exporters. The "firma" (partner) block is emitted inline with just enough fields for Flexi
/// to either match an existing adresář record by IČO or create a new one; it does not attempt
/// to look up/reuse an existing Flexi company code.
/// </summary>
public class AbraFlexiAccountingExporter : IAccountingExporter
{
    public EAccountingSystem System => EAccountingSystem.AbraFlexi;
    public string FileExtension => "xml";
    public string ContentType => "application/xml";

    public byte[] Export(IReadOnlyList<Invoice> issuedInvoices, IReadOnlyList<ReceivedInvoice> receivedInvoices, Client issuer)
    {
        var root = new XElement("winstrom", new XAttribute("version", "1.0"));

        foreach (var invoice in issuedInvoices)
            root.Add(BuildIssued(invoice));
        foreach (var invoice in receivedInvoices)
            root.Add(BuildReceived(invoice));

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
        var typDokl = invoice.DocumentType == EDocumentType.CreditNote ? "code:DOBROPIS" : "code:FAKTURA";

        var el = new XElement("faktura-vydana",
            new XElement("kod", invoice.DocumentNumber ?? string.Empty),
            new XElement("varSym", invoice.VariableSymbol ?? invoice.DocumentNumber ?? string.Empty),
            new XElement("typDokl", typDokl),
            new XElement("datVyst", AccountingExportCommon.FormatDate(invoice.IssueDate)),
            new XElement("datSplat", AccountingExportCommon.FormatDate(invoice.DueDate)),
            new XElement("duzpPuv", AccountingExportCommon.FormatDate(invoice.TaxableSupplyDate ?? invoice.IssueDate)));

        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            el.Add(new XElement("poznam", invoice.Notes));

        AddCurrency(el, invoice.Currency?.Code);
        el.Add(BuildFirma(invoice.Client));

        var items = invoice.InvoiceItem?.Where(i => !i.IsTextRow).OrderBy(i => i.OrderIndex).ToList() ?? [];
        el.Add(new XElement("polozkyFaktury", items.Select(i => BuildItem("faktura-vydana-polozka",
            i.Description, i.Quantity, i.Unit, i.UnitPrice, i.VatRatePercentage))));

        el.Add(new XElement("sumZklZakl", AccountingExportCommon.FormatDecimal(invoice.TotalBeforeVat)));
        el.Add(new XElement("sumDphZakl", AccountingExportCommon.FormatDecimal(invoice.TotalVat)));
        el.Add(new XElement("sumCelkem", AccountingExportCommon.FormatDecimal(invoice.TotalWithVat)));

        return el;
    }

    private static XElement BuildReceived(ReceivedInvoice invoice)
    {
        var el = new XElement("faktura-prijata",
            new XElement("kod", invoice.DocumentNumber ?? string.Empty),
            new XElement("varSym", invoice.VariableSymbol ?? invoice.DocumentNumber ?? string.Empty),
            new XElement("typDokl", "code:FAKTURA"),
            new XElement("datVyst", AccountingExportCommon.FormatDate(invoice.IssueDate)),
            new XElement("datSplat", AccountingExportCommon.FormatDate(invoice.DueDate)),
            new XElement("duzpPuv", AccountingExportCommon.FormatDate(invoice.TaxableSupplyDate ?? invoice.IssueDate)));

        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            el.Add(new XElement("poznam", invoice.Notes));

        AddCurrency(el, invoice.Currency?.Code);
        el.Add(BuildFirma(invoice.Supplier));

        var items = invoice.Items?.OrderBy(i => i.OrderIndex).ToList() ?? [];
        el.Add(new XElement("polozkyFaktury", items.Select(i => BuildItem("faktura-prijata-polozka",
            i.Description, i.Quantity, i.Unit, i.UnitPrice, i.VatRatePercentage))));

        el.Add(new XElement("sumZklZakl", AccountingExportCommon.FormatDecimal(invoice.TotalBeforeVat)));
        el.Add(new XElement("sumDphZakl", AccountingExportCommon.FormatDecimal(invoice.TotalVat)));
        el.Add(new XElement("sumCelkem", AccountingExportCommon.FormatDecimal(invoice.TotalWithVat)));

        return el;
    }

    /// <summary>Flexi's home currency is implicit; foreign documents carry <c>mena</c> as "code:EUR".</summary>
    private static void AddCurrency(XElement el, string? code)
    {
        if (!string.IsNullOrEmpty(code) && !code.Equals("CZK", StringComparison.OrdinalIgnoreCase))
            el.Add(new XElement("mena", "code:" + code.ToUpperInvariant()));
    }

    private static XElement BuildFirma(Client? partner)
    {
        partner ??= new Client();
        var address = AccountingExportCommon.PrimaryAddress(partner);

        var el = new XElement("firma",
            new XElement("nazev", partner.CompanyName ?? string.Empty),
            new XElement("ulice", address?.Street ?? string.Empty),
            new XElement("mesto", address?.City ?? string.Empty),
            new XElement("psc", address?.PostalCode ?? string.Empty),
            new XElement("ic", partner.RegistrationNumber ?? string.Empty));

        if (!string.IsNullOrWhiteSpace(partner.TaxNumber))
            el.Add(new XElement("dic", partner.TaxNumber));

        return el;
    }

    private static XElement BuildItem(
        string elementName, string description, decimal quantity, string unit,
        decimal unitPrice, decimal vatRatePercentage)
    {
        var bucket = AccountingExportCommon.ClassifyVatRate(vatRatePercentage);
        var typSzbDphK = bucket switch
        {
            AccountingExportCommon.VatBucket.High => "typSzbDph.zakladni",
            AccountingExportCommon.VatBucket.Low => "typSzbDph.snizena",
            _ => "typSzbDph.bezDph"
        };

        return new XElement(elementName,
            new XElement("nazev", description),
            new XElement("mnozstvi", AccountingExportCommon.FormatDecimal(quantity)),
            new XElement("mj", unit),
            new XElement("cenaMj", AccountingExportCommon.FormatDecimal(unitPrice)),
            new XElement("typSzbDphK", typSzbDphK));
    }
}
