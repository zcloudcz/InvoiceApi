using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.Isdoc;

/// <summary>
/// Pure static mapper: converts an Invoice entity to an ISDOC 6.0.2 XDocument.
/// No database access, no I/O -- just mapping.
/// </summary>
internal static class IsdocMapper
{
    internal static readonly XNamespace Ns = "http://isdoc.cz/namespace/2013";
    private const string IsdocVersion = "6.0.2";

    internal const string ForeignCurrencyNote =
        "Foreign currency invoice -- exchange rate not available in source data";
    internal static XDocument Map(Invoice invoice)
    {
        var root = new XElement(Ns + "Invoice",
            new XAttribute("xmlns", Ns.NamespaceName),
            new XAttribute("version", IsdocVersion));
        root.Add(MapHeader(invoice));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static IEnumerable<object> MapHeader(Invoice invoice)
    {
        var isCzk = string.IsNullOrWhiteSpace(invoice.Currency?.Code) ||
                    invoice.Currency.Code.Equals("CZK", StringComparison.OrdinalIgnoreCase);
        var docType = invoice.DocumentType == EDocumentType.CreditNote ? "5" : "1";

        yield return new XElement(Ns + "DocumentType", docType);
        yield return new XElement(Ns + "ID", DeterministicUuid(invoice.Id));
        yield return new XElement(Ns + "UUID", DeterministicUuid(invoice.Id));
        yield return new XElement(Ns + "IssuingSystem", "Fakvio");
        yield return new XElement(Ns + "DocumentCurrencyCode",
            isCzk ? "CZK" : (invoice.Currency?.Code ?? "CZK"));
        yield return new XElement(Ns + "IssueDate", FormatDate(invoice.IssueDate));

        if (invoice.TaxableSupplyDate.HasValue)
            yield return new XElement(Ns + "TaxPointDate", FormatDate(invoice.TaxableSupplyDate));
        if (invoice.DueDate.HasValue)
            yield return new XElement(Ns + "DueDate", FormatDate(invoice.DueDate));
        if (!string.IsNullOrWhiteSpace(invoice.DocumentNumber))
            yield return new XElement(Ns + "LocalCurrencyCode", "CZK");

        var noteText = !isCzk ? ForeignCurrencyNote : invoice.Notes;
        if (!string.IsNullOrWhiteSpace(noteText))
            yield return new XElement(Ns + "Note", noteText);

        yield return MapSupplierParty(invoice.Issuer!);
        yield return MapCustomerParty(invoice.Client);

        var paymentMeans = MapPaymentMeans(invoice);
        if (paymentMeans != null) yield return paymentMeans;

        foreach (var line in MapInvoiceLines(invoice))
            yield return line;

        yield return MapTaxTotal(invoice);
        yield return MapLegalMonetaryTotal(invoice);
    }
    private static XElement MapSupplierParty(Client issuer) =>
        new(Ns + "AccountingSupplierParty",
            new XElement(Ns + "Party", BuildPartyElements(issuer)));

    private static XElement MapCustomerParty(Client? client) =>
        new(Ns + "AccountingCustomerParty",
            new XElement(Ns + "Party", BuildPartyElements(client)));

    private static IEnumerable<object> BuildPartyElements(Client? client)
    {
        if (client == null) yield break;

        if (!string.IsNullOrWhiteSpace(client.RegistrationNumber))
            yield return new XElement(Ns + "PartyIdentification",
                new XElement(Ns + "ID", client.RegistrationNumber));

        yield return new XElement(Ns + "PartyName",
            new XElement(Ns + "Name", client.CompanyName ?? string.Empty));

        // Address is the nav prop name on Client entity (ICollection<Address> Address)
        var address = client.Address?.FirstOrDefault(a => a.IsPrimary)
                   ?? client.Address?.FirstOrDefault();
        if (address != null)
        {
            yield return new XElement(Ns + "PostalAddress",
                OptionalElement(Ns + "StreetName", address.Street),
                OptionalElement(Ns + "CityName", address.City),
                OptionalElement(Ns + "PostalZone", address.PostalCode),
                new XElement(Ns + "Country",
                    new XElement(Ns + "IdentificationCode",
                        string.IsNullOrWhiteSpace(address.Country) ? "CZ" : address.Country)));
        }

        if (client.IsVatPayer && !string.IsNullOrWhiteSpace(client.TaxNumber))
            yield return new XElement(Ns + "PartyTaxScheme",
                new XElement(Ns + "CompanyID", client.TaxNumber),
                new XElement(Ns + "TaxScheme", "VAT"));
    }
    private static IEnumerable<XElement> MapInvoiceLines(Invoice invoice)
    {
        // InvoiceItem is the nav prop name on Invoice entity
        var items = invoice.InvoiceItem?
            .Where(i => !i.IsTextRow)
            .OrderBy(i => i.OrderIndex)
            .ToList() ?? new List<InvoiceItem>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lineId = (i + 1).ToString(CultureInfo.InvariantCulture);
            yield return new XElement(Ns + "InvoiceLine",
                new XElement(Ns + "ID", lineId),
                new XElement(Ns + "InvoicedQuantity",
                    new XAttribute("unitCode", item.Unit ?? "H87"),
                    FormatDecimal(item.Quantity)),
                new XElement(Ns + "LineExtensionAmount",
                    new XAttribute("currencyID", invoice.Currency?.Code ?? "CZK"),
                    FormatDecimal(item.TotalBeforeVat)),
                new XElement(Ns + "Item",
                    new XElement(Ns + "Description", item.Description ?? string.Empty),
                    OptionalElement(Ns + "SellersItemIdentification", item.ProductCode),
                    new XElement(Ns + "ClassifiedTaxCategory",
                        new XElement(Ns + "Percent", FormatDecimal(item.VatRatePercentage)),
                        new XElement(Ns + "TaxScheme", "VAT"))),
                new XElement(Ns + "Price",
                    new XElement(Ns + "PriceAmount",
                        new XAttribute("currencyID", invoice.Currency?.Code ?? "CZK"),
                        FormatDecimal(item.UnitPrice))));
        }
    }
    private static XElement MapTaxTotal(Invoice invoice)
    {
        var cur = invoice.Currency?.Code ?? "CZK";
        var taxGroups = invoice.InvoiceItem?
            .Where(i => !i.IsTextRow)
            .GroupBy(i => i.VatRatePercentage)
            .Select(g => new { Rate = g.Key, TaxableAmount = g.Sum(x => x.TotalBeforeVat), TaxAmount = g.Sum(x => x.VatAmount) })
            .OrderByDescending(g => g.Rate)
            .ToList();

        var taxTotalEl = new XElement(Ns + "TaxTotal",
            new XElement(Ns + "TaxAmount",
                new XAttribute("currencyID", cur),
                FormatDecimal(invoice.TotalVat)));

        if (taxGroups != null)
        {
            foreach (var group in taxGroups)
            {
                var catCode = MapTaxCategoryCode(group.Rate);
                taxTotalEl.Add(new XElement(Ns + "TaxSubtotal",
                    new XElement(Ns + "TaxableAmount",
                        new XAttribute("currencyID", cur), FormatDecimal(group.TaxableAmount)),
                    new XElement(Ns + "TaxAmount",
                        new XAttribute("currencyID", cur), FormatDecimal(group.TaxAmount)),
                    new XElement(Ns + "TaxCategory",
                        new XElement(Ns + "Percent", FormatDecimal(group.Rate)),
                        new XElement(Ns + "TaxExemptionReasonCode",
                            catCode == "Z" ? "VATEX-EU-O" : string.Empty),
                        new XElement(Ns + "TaxScheme", "VAT"))));
            }
        }
        return taxTotalEl;
    }

    private static XElement MapLegalMonetaryTotal(Invoice invoice)
    {
        var cur = invoice.Currency?.Code ?? "CZK";
        return new XElement(Ns + "LegalMonetaryTotal",
            new XElement(Ns + "LineExtensionAmount", new XAttribute("currencyID", cur), FormatDecimal(invoice.TotalBeforeVat)),
            new XElement(Ns + "TaxExclusiveAmount",  new XAttribute("currencyID", cur), FormatDecimal(invoice.TotalBeforeVat)),
            new XElement(Ns + "TaxInclusiveAmount",  new XAttribute("currencyID", cur), FormatDecimal(invoice.TotalWithVat)),
            new XElement(Ns + "PayableAmount",       new XAttribute("currencyID", cur), FormatDecimal(invoice.TotalWithVat)));
    }
    private static XElement? MapPaymentMeans(Invoice invoice)
    {
        var hasMethod = invoice.PaymentMethod.HasValue;
        var hasBankDetails = !string.IsNullOrWhiteSpace(invoice.BankAccountNumber)
                          || !string.IsNullOrWhiteSpace(invoice.IBAN);
        var hasVs = !string.IsNullOrWhiteSpace(invoice.VariableSymbol);
        if (!hasMethod && !hasBankDetails && !hasVs) return null;

        var codeEl = hasMethod
            ? new XElement(Ns + "PaymentMeansCode", MapPaymentMeansCode(invoice.PaymentMethod!.Value))
            : null;
        var details = BuildPaymentDetails(invoice);
        if (codeEl == null && details == null) return null;

        var el = new XElement(Ns + "PaymentMeans");
        if (codeEl != null) el.Add(codeEl);
        if (details != null) el.Add(details);
        return el;
    }

    private static XElement? BuildPaymentDetails(Invoice invoice)
    {
        var hasBankDetails = !string.IsNullOrWhiteSpace(invoice.BankAccountNumber)
                          || !string.IsNullOrWhiteSpace(invoice.IBAN);
        var hasVs = !string.IsNullOrWhiteSpace(invoice.VariableSymbol);
        if (!hasBankDetails && !hasVs) return null;

        var el = new XElement(Ns + "Details");
        if (!string.IsNullOrWhiteSpace(invoice.IBAN))
            el.Add(new XElement(Ns + "ID", invoice.IBAN));
        else if (!string.IsNullOrWhiteSpace(invoice.BankAccountNumber))
            el.Add(new XElement(Ns + "ID", invoice.BankAccountNumber));
        if (!string.IsNullOrWhiteSpace(invoice.VariableSymbol))
            el.Add(new XElement(Ns + "VariableSymbol", invoice.VariableSymbol));
        if (!string.IsNullOrWhiteSpace(invoice.ConstantSymbol))
            el.Add(new XElement(Ns + "ConstantSymbol", invoice.ConstantSymbol));
        if (!string.IsNullOrWhiteSpace(invoice.SpecificSymbol))
            el.Add(new XElement(Ns + "SpecificSymbol", invoice.SpecificSymbol));
        return el;
    }

    /// <summary>S = rate > 0%, Z = zero rate (0%).</summary>
    internal static string MapTaxCategoryCode(decimal vatRatePercentage)
        => vatRatePercentage > 0 ? "S" : "Z";

    private static string MapPaymentMeansCode(EPaymentMethod method) =>
        method switch
        {
            EPaymentMethod.BankTransfer => "42",
            EPaymentMethod.Cash        => "10",
            EPaymentMethod.CreditCard  => "48",
            EPaymentMethod.PayPal      => "ZZZ",
            EPaymentMethod.Other       => "ZZZ",
            _                          => "ZZZ"
        };

    private static string FormatDecimal(decimal value)
        => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime? date)
        => date.HasValue
            ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;

    private static XElement? OptionalElement(XName name, string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);

    /// <summary>
    /// Deterministic UUID v5 (SHA-1, RFC 4122).
    /// Same invoiceId always produces same UUID -- safe to re-export.
    /// </summary>
    private static string DeterministicUuid(long invoiceId)
    {
        byte[] ns = [0x6b,0xa7,0xb8,0x10,0x9d,0xad,0x11,0xd1,0x80,0xb4,0x00,0xc0,0x4f,0xd4,0x30,0xc8];
        var nameBytes = Encoding.UTF8.GetBytes($"fakvio-invoice-{invoiceId}");
        var combined = ns.Concat(nameBytes).ToArray();
        var hash = SHA1.HashData(combined);
        var uuid = hash.Take(16).ToArray();
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50); // version 5
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(uuid).ToString("D");
    }
}