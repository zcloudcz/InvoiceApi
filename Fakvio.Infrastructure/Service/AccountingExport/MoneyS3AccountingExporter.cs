using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.AccountingExport;

/// <summary>
/// Exports issued and received invoices as a Money S3 "MoneyData" XML import file
/// (Money S3 &gt; XML přenosy &gt; Import). Issued invoices go under &lt;SeznamFaktVyd&gt;,
/// received invoices under &lt;SeznamFaktPrij&gt;.
///
/// Mapped against the official Money S3 schemas published by Seyfor (money.cz, "XML přenosy",
/// schemas.zip — __Faktura.xsd / __Firma.xsd / __Comtypes.xsd) and validated against them in the
/// unit tests. Element order follows the XSD sequence.
///
/// Rules and known gaps (DEVGUIDE §4.15):
/// - Only the 2024+ VAT rates 21 % (SazbaDPH2, Zaklad22/DPH22), 12 % (SazbaDPH1, Zaklad5/DPH5) and 0 %
///   are mapped; other rates are skipped (<see cref="CanExport(Invoice)"/>).
/// - Credit notes are exported with Dobropis = 1 and POSITIVE amounts (Fakvio stores them negative).
/// - Proforma → Druh = F. Advance-payment tax receipts (DPP) are skipped.
/// - Foreign-currency documents are skipped: Money requires the exchange rate for the home-currency
///   summary and Fakvio does not store it — fabricating one would produce wrong totals.
/// - The full document number goes to EvCisDokl; Doklad (max 10 characters) is written only when it fits,
///   otherwise Money assigns its own number. Received invoices get their Money number from Money's own series; the supplier's number goes to PrijatDokl.
/// - Celkem is required by the schema and written as the sum of the summary; Money recalculates it. Proplatit is not written.
/// </summary>
public class MoneyS3AccountingExporter : IAccountingExporter
{
    public EAccountingSystem System => EAccountingSystem.MoneyS3;
    public string FileExtension => "xml";
    public string ContentType => "application/xml";

    public bool CanExport(Invoice invoice) =>
        invoice.DocumentType != EDocumentType.TaxReceiptForAdvance
        && AccountingExportCommon.IsHomeCurrency(invoice.Currency?.Code)
        && AccountingExportCommon.AllRatesSupported((invoice.InvoiceItem ?? []).Where(i => !i.IsTextRow).Select(i => i.VatRatePercentage));

    public bool CanExport(ReceivedInvoice invoice) =>
        AccountingExportCommon.IsHomeCurrency(invoice.Currency?.Code)
        && AccountingExportCommon.AllRatesSupported((invoice.Items ?? []).Select(i => i.VatRatePercentage));

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
        var isCreditNote = invoice.DocumentType == EDocumentType.CreditNote;
        var items = invoice.InvoiceItem?.Where(i => !i.IsTextRow).OrderBy(i => i.OrderIndex).ToList() ?? [];

        // EvCisDokl (max 50) always carries the full number. Doklad is limited to 10 characters, so a longer
        // number is omitted there and Money assigns its own from the series.
        var number = invoice.DocumentNumber ?? string.Empty;
        var el = new XElement("FaktVyd");
        if (number.Length is > 0 and <= 10)
            el.Add(new XElement("Doklad", number));
        el.Add(new XElement("EvCisDokl", AccountingExportCommon.Truncate(number, 50)));
        AddDates(el, invoice.IssueDate, invoice.TaxableSupplyDate, invoice.DueDate);
        el.Add(new XElement("VarSymbol", AccountingExportCommon.Truncate(invoice.VariableSymbol ?? invoice.DocumentNumber, 20)));
        AddKindAndTotals(el, invoice.DocumentType == EDocumentType.Proforma ? "F" : "N", isCreditNote,
            items.Select(i => (i.VatRatePercentage, i.TotalBeforeVat, i.VatAmount)), invoice.Notes);
        el.Add(BuildPartner(invoice.Client));
        el.Add(BuildItems(items.Select(i => (i.Description, i.Quantity, i.Unit, i.UnitPrice, i.VatRatePercentage)), isCreditNote));
        return el;
    }

    private static XElement BuildReceived(ReceivedInvoice invoice)
    {
        var items = invoice.Items?.OrderBy(i => i.OrderIndex).ToList() ?? [];

        var el = new XElement("FaktPrij");
        AddDates(el, invoice.IssueDate, invoice.TaxableSupplyDate, invoice.DueDate);
        el.Add(new XElement("VarSymbol", AccountingExportCommon.Truncate(invoice.VariableSymbol ?? invoice.DocumentNumber, 20)));
        el.Add(new XElement("PrijatDokl", AccountingExportCommon.Truncate(invoice.DocumentNumber, 50)));
        // Fakvio has no credit-note flag on received invoices, so they are exported as normal documents.
        AddKindAndTotals(el, "N", isCreditNote: false,
            items.Select(i => (i.VatRatePercentage, i.TotalBeforeVat, i.VatAmount)), invoice.Notes);
        el.Add(BuildPartner(invoice.Supplier));
        el.Add(BuildItems(items.Select(i => (i.Description, i.Quantity, i.Unit, i.UnitPrice, i.VatRatePercentage)), false));
        return el;
    }

    /// <summary>Vystaveno, DatUcPr, PlnenoDPH, Splatno in schema order.</summary>
    private static void AddDates(XElement el, DateTime? issued, DateTime? taxable, DateTime? due)
    {
        el.Add(new XElement("Vystaveno", AccountingExportCommon.FormatDate(issued)));
        el.Add(new XElement("DatUcPr", AccountingExportCommon.FormatDate(issued)));
        el.Add(new XElement("PlnenoDPH", AccountingExportCommon.FormatDate(taxable ?? issued)));
        if (due.HasValue)
            el.Add(new XElement("Splatno", AccountingExportCommon.FormatDate(due)));
    }

    /// <summary>Druh, Dobropis, SazbaDPH1/2, SouhrnDPH and the memo Poznamka — all before the partner block.</summary>
    private static void AddKindAndTotals(
        XElement el, string druh, bool isCreditNote,
        IEnumerable<(decimal Rate, decimal Base, decimal Vat)> items, string? notes)
    {
        decimal zaklad0 = 0, zaklad5 = 0, zaklad22 = 0, dph5 = 0, dph22 = 0;
        foreach (var (rate, @base, vat) in items)
        {
            // Credit notes: Money wants positive amounts together with Dobropis = 1.
            var b = isCreditNote ? Math.Abs(@base) : @base;
            var v = isCreditNote ? Math.Abs(vat) : vat;
            switch (AccountingExportCommon.ClassifyVatRate(rate))
            {
                case AccountingExportCommon.VatBucket.High: zaklad22 += b; dph22 += v; break;
                case AccountingExportCommon.VatBucket.Low: zaklad5 += b; dph5 += v; break;
                default: zaklad0 += b; break;
            }
        }

        el.Add(new XElement("Druh", druh));
        el.Add(new XElement("Dobropis", isCreditNote ? "1" : "0"));
        el.Add(new XElement("SazbaDPH1", "12"));
        el.Add(new XElement("SazbaDPH2", "21"));
        el.Add(new XElement("SouhrnDPH",
            new XElement("Zaklad0", AccountingExportCommon.FormatDecimal(zaklad0)),
            new XElement("Zaklad5", AccountingExportCommon.FormatDecimal(zaklad5)),
            new XElement("Zaklad22", AccountingExportCommon.FormatDecimal(zaklad22)),
            new XElement("DPH5", AccountingExportCommon.FormatDecimal(dph5)),
            new XElement("DPH22", AccountingExportCommon.FormatDecimal(dph22))));
        // Required by the schema; Money recalculates it on import.
        el.Add(new XElement("Celkem", AccountingExportCommon.FormatDecimal(zaklad0 + zaklad5 + zaklad22 + dph5 + dph22)));
        if (!string.IsNullOrWhiteSpace(notes))
            el.Add(new XElement("Poznamka", notes));
    }

    private static XElement BuildPartner(Client? partner)
    {
        partner ??= new Client();
        var address = AccountingExportCommon.PrimaryAddress(partner);

        var el = new XElement("DodOdb",
            new XElement("ObchNazev", partner.CompanyName ?? string.Empty),
            new XElement("ObchAdresa",
                new XElement("Ulice", AccountingExportCommon.Truncate(address?.Street, 50)),
                new XElement("Misto", AccountingExportCommon.Truncate(address?.City, 40)),
                new XElement("PSC", AccountingExportCommon.Truncate(address?.PostalCode, 10)),
                new XElement("KodStatu", AccountingExportCommon.CountryIso2(address))));

        if (!string.IsNullOrWhiteSpace(partner.RegistrationNumber))
            el.Add(new XElement("ICO", AccountingExportCommon.Truncate(partner.RegistrationNumber, 10)));
        if (!string.IsNullOrWhiteSpace(partner.TaxNumber))
            el.Add(new XElement("DIC", AccountingExportCommon.Truncate(partner.TaxNumber, 20)));
        return el;
    }

    /// <summary>
    /// Items are priced without VAT (CenaTyp 0). Credit-note lines are exported with positive
    /// quantity and price because the Dobropis flag already carries the "negative" meaning.
    /// </summary>
    private static XElement BuildItems(
        IEnumerable<(string Description, decimal Quantity, string Unit, decimal UnitPrice, decimal Rate)> items, bool positive)
    {
        var list = new XElement("SeznamPolozek");
        var index = 1;
        foreach (var (description, quantity, unit, unitPrice, rate) in items)
        {
            var q = positive ? Math.Abs(quantity) : quantity;
            var p = positive ? Math.Abs(unitPrice) : unitPrice;
            var polozka = new XElement("Polozka", new XElement("Popis", AccountingExportCommon.Truncate(description, 50)));
            // popisType is limited to 50 characters; the full text goes to the memo field.
            if (description.Length > 50)
                polozka.Add(new XElement("Poznamka", description));
            polozka.Add(
                new XElement("PocetMJ", AccountingExportCommon.FormatDecimal(q)),
                new XElement("SazbaDPH", AccountingExportCommon.FormatDecimal(rate)),
                new XElement("Cena", AccountingExportCommon.FormatDecimal(p)),
                new XElement("CenaTyp", "0"),
                new XElement("Poradi", index++),
                // Protizapis is required by the schema (0 = not an advance deduction).
                new XElement("NesklPolozka", new XElement("MJ", unit), new XElement("Protizapis", "0")));
            list.Add(polozka);
        }
        return list;
    }
}
