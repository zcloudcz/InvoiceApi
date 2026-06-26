using System.Globalization;
using System.Xml.Linq;
using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Parses ISDOC 6.0.2 XML into <see cref="InvoiceExtractedData"/>.
/// Synchronous, stateless, no AI — pure XML deserialization.
/// Most reliable extraction path for Czech electronic invoices.
///
/// ISDOC namespace: http://isdoc.cz/namespace/2013
/// Element order and names match the official XSD and our IsdocMapper export.
/// </summary>
public class IsdocImportParser : IIsdocImportParser
{
    private static readonly XNamespace Ns = "http://isdoc.cz/namespace/2013";
    private readonly ILogger<IsdocImportParser> _logger;

    public IsdocImportParser(ILogger<IsdocImportParser> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public InvoiceExtractedData? Parse(string isdocXml)
    {
        if (string.IsNullOrWhiteSpace(isdocXml))
            return null;

        try
        {
            var doc = XDocument.Parse(isdocXml);
            var root = doc.Root;
            if (root == null)
                return null;

            // Handle both namespaced and non-namespaced ISDOC (some generators omit the namespace)
            var ns = root.Name.Namespace != XNamespace.None ? root.Name.Namespace : Ns;

            var result = new InvoiceExtractedData
            {
                Source = EExtractionSource.Merged,
                DocumentNumber = Str(root, ns, "ID"),
                IssueDate = Date(root, ns, "IssueDate"),
                TaxableSupplyDate = Date(root, ns, "TaxPointDate"),
                Currency = Str(root, ns, "ForeignCurrencyCode") ?? Str(root, ns, "LocalCurrencyCode") ?? "CZK",
                DetectedDocumentType = MapIsdocDocumentType(Str(root, ns, "DocumentType")),
            };

            // Supplier (issuer) party
            var supplier = root.Element(ns + "AccountingSupplierParty")?.Element(ns + "Party");
            if (supplier != null)
            {
                result.IssuerRegistrationNumber = Str(supplier, ns, "PartyIdentification", "ID");
                result.IssuerName = Str(supplier, ns, "PartyName", "Name");
                result.IssuerTaxNumber = Str(supplier, ns, "PartyTaxScheme", "CompanyID");
            }

            // Buyer (recipient) party
            var buyer = root.Element(ns + "AccountingCustomerParty")?.Element(ns + "Party");
            if (buyer != null)
            {
                result.RecipientRegistrationNumber = Str(buyer, ns, "PartyIdentification", "ID");
                result.RecipientName = Str(buyer, ns, "PartyName", "Name");
                result.RecipientTaxNumber = Str(buyer, ns, "PartyTaxScheme", "CompanyID");
            }

            // Legal monetary totals
            var totals = root.Element(ns + "LegalMonetaryTotal");
            if (totals != null)
            {
                result.TotalBeforeVat = Dec(totals, ns, "TaxExclusiveAmount");
                result.TotalAmount = Dec(totals, ns, "PayableAmount")
                                     ?? Dec(totals, ns, "TaxInclusiveAmount");
            }

            // Tax total
            var taxTotal = root.Element(ns + "TaxTotal");
            if (taxTotal != null)
            {
                result.TotalVat = Dec(taxTotal, ns, "TaxAmount");
            }

            // Payment means
            var payment = root.Element(ns + "PaymentMeans")?.Element(ns + "Payment");
            if (payment != null)
            {
                var details = payment.Element(ns + "Details");
                if (details != null)
                {
                    result.DueDate = Date(details, ns, "PaymentDueDate");
                    result.VariableSymbol = Str(details, ns, "VariableSymbol");
                    result.IBAN = Str(details, ns, "IBAN");
                    result.SWIFT = Str(details, ns, "BIC");

                    var accountId = Str(details, ns, "ID");
                    var bankCode = Str(details, ns, "BankCode");
                    if (!string.IsNullOrEmpty(accountId) && !string.IsNullOrEmpty(bankCode))
                        result.BankAccountNumber = $"{accountId}/{bankCode}";
                }
            }

            // Invoice lines
            var linesElement = root.Element(ns + "InvoiceLines");
            if (linesElement != null)
            {
                var lines = linesElement.Elements(ns + "InvoiceLine").ToList();
                if (lines.Count > 0)
                {
                    result.Items = lines.Select(line => new ExtractedInvoiceItem
                    {
                        Description = Str(line, ns, "Item", "Description"),
                        Quantity = Dec(line, ns, "InvoicedQuantity"),
                        UnitPrice = Dec(line, ns, "UnitPrice"),
                        VatRate = Dec(line.Element(ns + "ClassifiedTaxCategory"), ns, "Percent"),
                        Unit = line.Element(ns + "InvoicedQuantity")?.Attribute("unitCode")?.Value,
                    }).ToList();
                }
            }

            _logger.LogDebug(
                "ISDOC parsed: DocNum={DocNum} Issuer={Issuer} Recipient={Recipient} Total={Total}",
                result.DocumentNumber, result.IssuerRegistrationNumber,
                result.RecipientRegistrationNumber, result.TotalAmount);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse ISDOC XML");
            return null;
        }
    }

    // ─── XML helpers ─────────────────────────────────────────────────────

    private static string? Str(XElement? parent, XNamespace ns, string element)
    {
        var value = parent?.Element(ns + element)?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Str(XElement? parent, XNamespace ns, string child, string grandchild)
    {
        return Str(parent?.Element(ns + child), ns, grandchild);
    }

    private static decimal? Dec(XElement? parent, XNamespace ns, string element)
    {
        var str = Str(parent, ns, element);
        if (str == null) return null;
        return decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    /// <summary>
    /// Maps ISDOC DocumentType integer to EDocumentType name string.
    /// ISDOC values: 1=Invoice, 2=CreditNote, 3=DebitNote, 4=ProformaInvoice,
    /// 5=AdvanceInvoice (=Proforma), 6=CreditAdvanceInvoice.
    /// </summary>
    private static string? MapIsdocDocumentType(string? value) => value switch
    {
        "1" => "Invoice",
        "2" or "5" => "CreditNote",
        "3" => "Invoice", // DebitNote → treat as Invoice
        "4" => "Proforma",
        "6" => "CreditNote", // CreditAdvanceInvoice → CreditNote
        _ => null
    };

    private static DateTime? Date(XElement? parent, XNamespace ns, string element)
    {
        var str = Str(parent, ns, element);
        if (str == null) return null;
        return DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var val)
            ? DateTime.SpecifyKind(val, DateTimeKind.Utc)
            : null;
    }
}
