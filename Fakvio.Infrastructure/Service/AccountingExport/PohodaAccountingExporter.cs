using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.AccountingExport;

/// <summary>
/// Exports issued and received invoices as a Stormware POHODA "dataPack" XML (data.xsd /
/// invoice.xsd / type.xsd, schema version 2.0 — https://www.stormware.cz/xml/schema/version_2/).
/// Importable in POHODA via Soubor &gt; Datová komunikace &gt; XML import.
///
/// Known gaps (DEVGUIDE §4.15):
/// - Validated in the unit tests against the official Stormware XSDs (data/invoice/type .xsd, version 2).
/// - rateVAT only distinguishes "high"/"low"/"none" — Fakvio's VatRate table has no concept of
///   Pohoda's historical "third" (10 %) rate, see <see cref="AccountingExportCommon.ClassifyVatRate"/>.
/// - Foreign-currency invoices fill the foreignCurrency blocks WITHOUT rate/amount — Fakvio does not
///   store the exchange rate, so POHODA applies its own rate list for the document date.
/// - Only the 2024+ VAT rates 21 / 12 / 0 % are mapped; documents with other rates are skipped (CanExport).
/// - Advance-payment tax receipts (DPP) are skipped; proformas map to issuedProformaInvoice.
/// </summary>
public class PohodaAccountingExporter : IAccountingExporter
{
    public EAccountingSystem System => EAccountingSystem.Pohoda;
    public string FileExtension => "xml";
    // POHODA XML import expects Windows-1250 — the standard Czech/Central-European legacy
    // encoding (confirmed by the "kodovani" attribute convention used across Stormware samples).
    public string ContentType => "application/xml";

    private static readonly XNamespace Dat = "http://www.stormware.cz/schema/version_2/data.xsd";
    private static readonly XNamespace Inv = "http://www.stormware.cz/schema/version_2/invoice.xsd";
    private static readonly XNamespace Typ = "http://www.stormware.cz/schema/version_2/type.xsd";

    // Advance-payment tax receipts (DPP) are not importable as standalone Pohoda invoices
    // (Pohoda creates them from the advance invoice) — skipped rather than exported as a normal invoice.
    public bool CanExport(Invoice invoice) =>
        invoice.DocumentType != EDocumentType.TaxReceiptForAdvance
        && (invoice.DocumentNumber?.Length ?? 0) <= 20 // inv:numberRequested is string20 in the XSD
        && AccountingExportCommon.AllRatesSupported((invoice.InvoiceItem ?? []).Where(i => !i.IsTextRow).Select(i => i.VatRatePercentage));

    public bool CanExport(ReceivedInvoice invoice) =>
        AccountingExportCommon.AllRatesSupported((invoice.Items ?? []).Select(i => i.VatRatePercentage));

    public byte[] Export(IReadOnlyList<Invoice> issuedInvoices, IReadOnlyList<ReceivedInvoice> receivedInvoices, Client issuer)
    {
        // The dataPack ico attribute is mandatory for Pohoda to pick the right accounting unit.
        if (string.IsNullOrWhiteSpace(issuer.RegistrationNumber))
            throw new InvalidOperationException("Tenant has no issuer company (Client with IsIssuer = true) configured.");

        // Windows-1250 is a Windows code page not present on .NET Core by default —
        // CodePagesEncodingProvider registers it. Reuses the same pattern as CsvTable.cs import.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var winCp1250 = Encoding.GetEncoding(1250);

        var dataPack = new XElement(Dat + "dataPack",
            new XAttribute("version", "2.0"),
            new XAttribute("id", "Fakvio" + DateTime.UtcNow.Ticks),
            new XAttribute("ico", AccountingExportCommon.Truncate(issuer.RegistrationNumber, 15)),
            new XAttribute("application", "Fakvio"),
            new XAttribute("note", "Export z Fakvio"));

        int seq = 1;
        foreach (var invoice in issuedInvoices)
            dataPack.Add(BuildDataPackItem(seq++, BuildIssuedInvoice(invoice)));
        foreach (var invoice in receivedInvoices)
            dataPack.Add(BuildDataPackItem(seq++, BuildReceivedInvoice(invoice, issuer)));

        var document = new XDocument(new XDeclaration("1.0", "Windows-1250", null), dataPack);

        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = winCp1250, Indent = true, IndentChars = "  " };
        using (var writer = XmlWriter.Create(ms, settings))
            document.WriteTo(writer);
        return ms.ToArray();
    }

    private static XElement BuildDataPackItem(int seq, XElement invoiceEl) =>
        new(Dat + "dataPackItem",
            new XAttribute("id", "Invoice" + seq),
            new XAttribute("version", "2.0"),
            invoiceEl);

    // --------------------------------------------------------------------------
    // Issued invoices
    // --------------------------------------------------------------------------

    private static XElement BuildIssuedInvoice(Invoice invoice)
    {
        var invoiceType = invoice.DocumentType switch
        {
            EDocumentType.CreditNote => "issuedCreditNotice",
            EDocumentType.Proforma => "issuedProformaInvoice",
            _ => "issuedInvoice"
        };

        var header = new XElement(Inv + "invoiceHeader",
            new XElement(Inv + "invoiceType", invoiceType),
            new XElement(Inv + "number", new XElement(Typ + "numberRequested", invoice.DocumentNumber ?? string.Empty)),
            new XElement(Inv + "symVar", AccountingExportCommon.Truncate(invoice.VariableSymbol ?? invoice.DocumentNumber, 20)),
            new XElement(Inv + "date", AccountingExportCommon.FormatDate(invoice.IssueDate)),
            new XElement(Inv + "dateTax", AccountingExportCommon.FormatDate(invoice.TaxableSupplyDate ?? invoice.IssueDate)),
            new XElement(Inv + "dateDue", AccountingExportCommon.FormatDate(invoice.DueDate)));

        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            header.Add(new XElement(Inv + "text", AccountingExportCommon.Truncate(invoice.Notes, 240)));

        header.Add(BuildPartnerIdentity(invoice.Client));

        AddPaymentType(header, invoice.PaymentMethod);

        if (!string.IsNullOrWhiteSpace(invoice.BankAccountNumber))
        {
            var (accountNumber, bankCode) = AccountingExportCommon.ParseBankAccount(invoice.BankAccountNumber);
            header.Add(new XElement(Inv + "account",
                new XElement(Typ + "accountNo", accountNumber),
                new XElement(Typ + "bankCode", bankCode)));
        }

        if (!string.IsNullOrWhiteSpace(invoice.ConstantSymbol))
            header.Add(new XElement(Inv + "symConst", invoice.ConstantSymbol));

        var currency = invoice.Currency?.Code ?? "CZK";
        var items = invoice.InvoiceItem?.Where(i => !i.IsTextRow).OrderBy(i => i.OrderIndex).ToList() ?? [];
        var detail = new XElement(Inv + "invoiceDetail",
            items.Select(i => BuildItem(i.Description, i.Quantity, i.Unit, i.UnitPrice,
                i.TotalBeforeVat, i.VatAmount, i.TotalWithVat, i.VatRatePercentage, currency)));

        var summary = BuildSummary(items.Select(i => (i.VatRatePercentage, i.TotalBeforeVat, i.VatAmount)), currency);

        return new XElement(Inv + "invoice", new XAttribute("version", "2.0"), header, detail, summary);
    }

    // --------------------------------------------------------------------------
    // Received invoices
    // --------------------------------------------------------------------------

    private static XElement BuildReceivedInvoice(ReceivedInvoice invoice, Client issuer)
    {
        var header = new XElement(Inv + "invoiceHeader",
            new XElement(Inv + "invoiceType", "receivedInvoice"),
            // The supplier's own document number goes to numberKHDPH (+ symVar); Pohoda assigns its
            // internal number from its own series, so we must not request one via <number>.
            new XElement(Inv + "numberKHDPH", AccountingExportCommon.Truncate(invoice.DocumentNumber, 32)),
            new XElement(Inv + "symVar", AccountingExportCommon.Truncate(invoice.VariableSymbol ?? invoice.DocumentNumber, 20)),
            new XElement(Inv + "date", AccountingExportCommon.FormatDate(invoice.IssueDate)),
            new XElement(Inv + "dateTax", AccountingExportCommon.FormatDate(invoice.TaxableSupplyDate ?? invoice.IssueDate)),
            new XElement(Inv + "dateDue", AccountingExportCommon.FormatDate(invoice.DueDate)));

        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            header.Add(new XElement(Inv + "text", AccountingExportCommon.Truncate(invoice.Notes, 240)));

        header.Add(BuildPartnerIdentity(invoice.Supplier));

        AddPaymentType(header, invoice.PaymentMethod);

        var currency = invoice.Currency?.Code ?? "CZK";
        var items = invoice.Items?.OrderBy(i => i.OrderIndex).ToList() ?? [];
        var detail = new XElement(Inv + "invoiceDetail",
            items.Select(i => BuildItem(i.Description, i.Quantity, i.Unit, i.UnitPrice,
                i.TotalBeforeVat, i.VatAmount, i.TotalWithVat, i.VatRatePercentage, currency)));

        var summary = BuildSummary(items.Select(i => (i.VatRatePercentage, i.TotalBeforeVat, i.VatAmount)), currency);

        return new XElement(Inv + "invoice", new XAttribute("version", "2.0"), header, detail, summary);
    }

    // --------------------------------------------------------------------------
    // Shared building blocks
    // --------------------------------------------------------------------------

    private static XElement BuildPartnerIdentity(Client? partner)
    {
        partner ??= new Client();
        var address = AccountingExportCommon.PrimaryAddress(partner);

        var addr = new XElement(Typ + "address",
            new XElement(Typ + "company", partner.CompanyName ?? string.Empty),
            new XElement(Typ + "city", address?.City ?? string.Empty),
            new XElement(Typ + "street", address?.Street ?? string.Empty),
            new XElement(Typ + "zip", AccountingExportCommon.Truncate(address?.PostalCode, 15)),
            new XElement(Typ + "ico", AccountingExportCommon.Truncate(partner.RegistrationNumber, 15)));

        if (!string.IsNullOrWhiteSpace(partner.TaxNumber))
            addr.Add(new XElement(Typ + "dic", AccountingExportCommon.Truncate(partner.TaxNumber, 18)));

        return new XElement(Inv + "partnerIdentity", addr);
    }

    private static XElement BuildItem(
        string description, decimal quantity, string unit, decimal unitPrice,
        decimal totalBeforeVat, decimal vatAmount, decimal totalWithVat, decimal vatRatePercentage, string currency)
    {
        var bucket = AccountingExportCommon.ClassifyVatRate(vatRatePercentage);
        return new XElement(Inv + "invoiceItem",
            new XElement(Inv + "text", AccountingExportCommon.Truncate(description, 90)),
            new XElement(Inv + "quantity", AccountingExportCommon.FormatDecimal(quantity)),
            new XElement(Inv + "unit", AccountingExportCommon.Truncate(unit, 10)),
            new XElement(Inv + "payVAT", "false"), // "false" = amounts below are without-VAT base (unitPrice is net)
            new XElement(Inv + "rateVAT", bucket switch { AccountingExportCommon.VatBucket.High => "high", AccountingExportCommon.VatBucket.Low => "low", _ => "none" }),
            // Home-currency (CZK) documents fill homeCurrency; any other currency fills foreignCurrency.
            new XElement(Inv + (IsHome(currency) ? "homeCurrency" : "foreignCurrency"),
                new XElement(Typ + "unitPrice", AccountingExportCommon.FormatDecimal(unitPrice)),
                new XElement(Typ + "price", AccountingExportCommon.FormatDecimal(totalBeforeVat)),
                new XElement(Typ + "priceVAT", AccountingExportCommon.FormatDecimal(vatAmount)),
                new XElement(Typ + "priceSum", AccountingExportCommon.FormatDecimal(totalWithVat))));
    }

    private static bool IsHome(string currency) => currency.Equals("CZK", StringComparison.OrdinalIgnoreCase);

    private static XElement BuildSummary(IEnumerable<(decimal Rate, decimal Base, decimal Vat)> items, string currency)
    {
        decimal noneBase = 0, lowBase = 0, lowVat = 0, highBase = 0, highVat = 0;
        foreach (var (rate, @base, vat) in items)
        {
            switch (AccountingExportCommon.ClassifyVatRate(rate))
            {
                case AccountingExportCommon.VatBucket.High: highBase += @base; highVat += vat; break;
                case AccountingExportCommon.VatBucket.Low: lowBase += @base; lowVat += vat; break;
                default: noneBase += @base; break;
            }
        }

        var summary = new XElement(Inv + "invoiceSummary", new XElement(Inv + "roundingDocument", "none"));
        if (IsHome(currency))
        {
            summary.Add(new XElement(Inv + "homeCurrency",
                new XElement(Typ + "priceNone", AccountingExportCommon.FormatDecimal(noneBase)),
                new XElement(Typ + "priceLow", AccountingExportCommon.FormatDecimal(lowBase)),
                new XElement(Typ + "priceLowVAT", AccountingExportCommon.FormatDecimal(lowVat)),
                new XElement(Typ + "priceLowSum", AccountingExportCommon.FormatDecimal(lowBase + lowVat)),
                new XElement(Typ + "priceHigh", AccountingExportCommon.FormatDecimal(highBase)),
                new XElement(Typ + "priceHighVAT", AccountingExportCommon.FormatDecimal(highVat)),
                new XElement(Typ + "priceHighSum", AccountingExportCommon.FormatDecimal(highBase + highVat))));
        }
        else
        {
            // No rate/amount on purpose: Fakvio stores no exchange rate, so Pohoda applies its own
            // exchange-rate list for the document date (both elements are optional in the schema).
            summary.Add(new XElement(Inv + "foreignCurrency",
                new XElement(Typ + "currency", new XElement(Typ + "ids", currency)),
                new XElement(Typ + "priceSum", AccountingExportCommon.FormatDecimal(noneBase + lowBase + lowVat + highBase + highVat))));
        }
        return summary;
    }

    /// <summary>
    /// Adds inv:paymentType only for methods that have an equivalent in the Pohoda enumeration
    /// (draft, cash, creditcard, ...). Unmapped methods (PayPal, other) omit the element so Pohoda
    /// applies its own default instead of rejecting the file.
    /// </summary>
    private static void AddPaymentType(XElement header, EPaymentMethod? method)
    {
        var value = method switch
        {
            EPaymentMethod.Cash => "cash",
            EPaymentMethod.CreditCard => "creditcard",
            EPaymentMethod.BankTransfer => "draft", // Pohoda "draft" = platební příkaz (bank transfer)
            _ => null
        };
        if (value != null)
            header.Add(new XElement(Inv + "paymentType", new XElement(Typ + "paymentType", value)));
    }
}
