using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.AccountingExport;

/// <summary>
/// Exports issued and received invoices as an ABRA Flexi "winstrom" XML import file
/// (importable via Nástroje &gt; Import XML, or POSTed to the Flexi REST API).
/// Issued invoices become &lt;faktura-vydana&gt;, received invoices &lt;faktura-prijata&gt;.
///
/// Field names, writability and select values were verified against the live Flexi evidence
/// catalogue (https://demo.flexibee.eu/c/demo/faktura-vydana/properties, faktura-prijata and the
/// *-polozka evidences) — Flexi publishes no XSD, so the unit tests check element names against an
/// embedded snapshot of that catalogue instead.
///
/// Rules and known gaps (DEVGUIDE §4.15):
/// - Partner data is sent as flat nazFirmy/ulice/mesto/psc/ic/dic on the document (no nested
///   &lt;firma&gt; without a Flexi address-book code); Flexi does not create address-book records.
/// - Sums (sumZklZakl, sumCelkem, ...) are computed by Flexi from the items and are NOT written.
/// - Unit (mj) is a code reference ("code:KS"); only well-known units are mapped, others are omitted.
/// - typDokl uses the codes of a default Flexi database (FAKTURA, DOBROPIS); a company that renamed
///   them must adjust the document type after import. Credit notes carry POSITIVE amounts.
/// - Received invoices: Flexi assigns its own internal "kod" from its number series; the supplier's
///   number goes to cisDosle (+ varSym).
/// - Only the 2024+ VAT rates 21 % / 12 % / 0 % are mapped; other rates are skipped.
/// - Foreign-currency documents are exported only when they carry a ČNB rate (Invoice.ExchangeRate, DEVGUIDE §4.17):
///   mena = code:XXX, kurz = CZK per 1 unit, kurzMnozstvi = 1; item prices (cenaMj) stay in the document currency
///   and Flexi computes the CZK sums from the rate. Without a rate the document is skipped.
///   Proformas and advance-payment tax receipts are skipped (advance document types are tenant-specific in Flexi).
/// </summary>
public class AbraFlexiAccountingExporter : IAccountingExporter
{
    public EAccountingSystem System => EAccountingSystem.AbraFlexi;
    public string FileExtension => "xml";
    public string ContentType => "application/xml";

    public bool CanExport(Invoice invoice) =>
        invoice.DocumentType is EDocumentType.Invoice or EDocumentType.CreditNote
        && AccountingExportCommon.CurrencyExportable(invoice.Currency?.Code, invoice.ExchangeRate)
        && (invoice.DocumentNumber?.Length ?? 0) is > 0 and <= 20 // faktura-vydana.kod max length
        && AccountingExportCommon.AllRatesSupported((invoice.InvoiceItem ?? []).Where(i => !i.IsTextRow).Select(i => i.VatRatePercentage));

    public bool CanExport(ReceivedInvoice invoice) =>
        AccountingExportCommon.CurrencyExportable(invoice.Currency?.Code, invoice.ExchangeRate)
        && AccountingExportCommon.AllRatesSupported((invoice.Items ?? []).Select(i => i.VatRatePercentage));

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
        var isCreditNote = invoice.DocumentType == EDocumentType.CreditNote;

        var el = new XElement("faktura-vydana",
            new XElement("kod", invoice.DocumentNumber),
            new XElement("typDokl", isCreditNote ? "code:DOBROPIS" : "code:FAKTURA"),
            new XElement("varSym", AccountingExportCommon.Truncate(invoice.VariableSymbol ?? invoice.DocumentNumber, 30)),
            new XElement("datVyst", AccountingExportCommon.FormatDate(invoice.IssueDate)));
        AddDates(el, invoice.DueDate, invoice.TaxableSupplyDate ?? invoice.IssueDate);
        AddCurrency(el, invoice.Currency?.Code, invoice.ExchangeRate);
        AddNote(el, invoice.Notes);
        AddPartner(el, invoice.Client);

        var items = invoice.InvoiceItem?.Where(i => !i.IsTextRow).OrderBy(i => i.OrderIndex).ToList() ?? [];
        el.Add(new XElement("polozkyFaktury", items.Select(i => BuildItem("faktura-vydana-polozka",
            i.Description, i.Quantity, i.Unit, i.UnitPrice, i.VatRatePercentage, isCreditNote))));
        return el;
    }

    private static XElement BuildReceived(ReceivedInvoice invoice)
    {
        // No "kod": Flexi assigns its own internal number from the series.
        var el = new XElement("faktura-prijata",
            new XElement("typDokl", "code:FAKTURA"),
            new XElement("cisDosle", AccountingExportCommon.Truncate(invoice.DocumentNumber, 20)),
            new XElement("varSym", AccountingExportCommon.Truncate(invoice.VariableSymbol ?? invoice.DocumentNumber, 30)),
            new XElement("datVyst", AccountingExportCommon.FormatDate(invoice.IssueDate)));
        AddDates(el, invoice.DueDate, invoice.TaxableSupplyDate ?? invoice.IssueDate);
        AddCurrency(el, invoice.Currency?.Code, invoice.ExchangeRate);
        AddNote(el, invoice.Notes);
        AddPartner(el, invoice.Supplier);

        var items = invoice.Items?.OrderBy(i => i.OrderIndex).ToList() ?? [];
        el.Add(new XElement("polozkyFaktury", items.Select(i => BuildItem("faktura-prijata-polozka",
            i.Description, i.Quantity, i.Unit, i.UnitPrice, i.VatRatePercentage, false))));
        return el;
    }

    private static void AddDates(XElement el, DateTime? due, DateTime? taxable)
    {
        if (due.HasValue) el.Add(new XElement("datSplat", AccountingExportCommon.FormatDate(due)));
        if (taxable.HasValue) el.Add(new XElement("duzpPuv", AccountingExportCommon.FormatDate(taxable)));
    }

    /// <summary>Foreign-currency document: mena + kurz (CZK per kurzMnozstvi units, always 1 here). CZK adds nothing.</summary>
    private static void AddCurrency(XElement el, string? currencyCode, decimal? exchangeRate)
    {
        if (AccountingExportCommon.IsHomeCurrency(currencyCode) || exchangeRate is not > 0m) return;
        el.Add(new XElement("mena", "code:" + currencyCode!.ToUpperInvariant()),
               new XElement("kurz", AccountingExportCommon.FormatRate(exchangeRate.Value)),
               new XElement("kurzMnozstvi", "1"));
    }

    private static void AddNote(XElement el, string? notes)
    {
        if (!string.IsNullOrWhiteSpace(notes))
            el.Add(new XElement("poznam", notes));
    }

    /// <summary>Flat partner fields (nazFirmy, ulice, mesto, psc, ic, dic) — lengths per the Flexi catalogue.</summary>
    private static void AddPartner(XElement el, Client? partner)
    {
        partner ??= new Client();
        var address = AccountingExportCommon.PrimaryAddress(partner);

        el.Add(new XElement("nazFirmy", AccountingExportCommon.Truncate(partner.CompanyName, 255)),
               new XElement("ulice", AccountingExportCommon.Truncate(address?.Street, 255)),
               new XElement("mesto", AccountingExportCommon.Truncate(address?.City, 255)),
               new XElement("psc", AccountingExportCommon.Truncate(address?.PostalCode, 255)));
        if (!string.IsNullOrWhiteSpace(partner.RegistrationNumber))
            el.Add(new XElement("ic", AccountingExportCommon.Truncate(partner.RegistrationNumber, 20)));
        if (!string.IsNullOrWhiteSpace(partner.TaxNumber))
            el.Add(new XElement("dic", AccountingExportCommon.Truncate(partner.TaxNumber, 20)));
    }

    // Units that exist in every default Flexi database (measure-unit codes).
    private static readonly Dictionary<string, string> KnownUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ks"] = "KS", ["pcs"] = "KS", ["hod"] = "HOD", ["h"] = "HOD", ["m"] = "M", ["kg"] = "KG", ["l"] = "L", ["km"] = "KM"
    };

    private static XElement BuildItem(
        string elementName, string description, decimal quantity, string unit,
        decimal unitPrice, decimal vatRatePercentage, bool positive)
    {
        var typSzbDphK = AccountingExportCommon.ClassifyVatRate(vatRatePercentage) switch
        {
            AccountingExportCommon.VatBucket.High => "typSzbDph.dphZakl",
            AccountingExportCommon.VatBucket.Low => "typSzbDph.dphSniz",
            _ => "typSzbDph.dphOsv"
        };

        var el = new XElement(elementName,
            new XElement("typPolozkyK", "typPolozky.obecny"),
            new XElement("nazev", AccountingExportCommon.Truncate(description, 255)),
            new XElement("mnozMj", AccountingExportCommon.FormatDecimal(positive ? Math.Abs(quantity) : quantity)));
        if (KnownUnits.TryGetValue(unit?.Trim() ?? string.Empty, out var code))
            el.Add(new XElement("mj", "code:" + code));
        el.Add(
            new XElement("cenaMj", AccountingExportCommon.FormatDecimal(positive ? Math.Abs(unitPrice) : unitPrice)),
            new XElement("typCenyDphK", "typCeny.bezDph"),
            new XElement("typSzbDphK", typSzbDphK),
            new XElement("szbDph", AccountingExportCommon.FormatDecimal(vatRatePercentage)));
        return el;
    }
}
