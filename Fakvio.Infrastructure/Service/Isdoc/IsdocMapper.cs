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
///
/// Element order strictly follows the official ISDOC 6.0.2 XSD (isdoc.cz/6.0.2/xsd/).
/// </summary>
internal static class IsdocMapper
{
    /// <summary>XML namespace used by ISDOC 6.0.2 standard.</summary>
    internal static readonly XNamespace Ns = "http://isdoc.cz/namespace/2013";

    private const string IsdocVersion = "6.0.2";

    internal const string ForeignCurrencyNote =
        "Foreign currency invoice -- exchange rate not available in source data";

    /// <summary>
    /// Maps an Invoice entity to a fully-formed ISDOC 6.0.2 XDocument.
    /// </summary>
    internal static XDocument Map(Invoice invoice)
    {
        var root = new XElement(Ns + "Invoice",
            new XAttribute("version", IsdocVersion));

        root.Add(MapHeader(invoice));

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    // --------------------------------------------------------------------------
    // Header (top-level InvoiceType children in XSD sequence order)
    // --------------------------------------------------------------------------

    private static IEnumerable<object> MapHeader(Invoice invoice)
    {
        var currencyCode = invoice.Currency?.Code ?? "CZK";
        var isCzk = currencyCode.Equals("CZK", StringComparison.OrdinalIgnoreCase);
        var docType = invoice.DocumentType == EDocumentType.CreditNote ? "5" : "1";

        // --- Identification block ---
        yield return new XElement(Ns + "DocumentType", docType);
        yield return new XElement(Ns + "ID", invoice.DocumentNumber ?? DeterministicUuid(invoice.Id));
        yield return new XElement(Ns + "UUID", DeterministicUuid(invoice.Id));
        yield return new XElement(Ns + "IssuingSystem", "Fakvio");

        // --- Date block ---
        yield return new XElement(Ns + "IssueDate", FormatDate(invoice.IssueDate));
        if (invoice.TaxableSupplyDate.HasValue)
            yield return new XElement(Ns + "TaxPointDate", FormatDate(invoice.TaxableSupplyDate));

        // --- VATApplicable (required) ---
        yield return new XElement(Ns + "VATApplicable",
            (invoice.Issuer?.IsVatPayer == true).ToString().ToLowerInvariant());

        // --- ElectronicPossibilityAgreementReference (required) ---
        yield return new XElement(Ns + "ElectronicPossibilityAgreementReference",
            "Electronic invoice");

        // --- Note (optional, max 1 in official XSD) ---
        var noteParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            noteParts.Add(invoice.Notes);
        if (!isCzk)
            noteParts.Add(ForeignCurrencyNote);
        if (noteParts.Count > 0)
            yield return new XElement(Ns + "Note", string.Join(" | ", noteParts));

        // --- Currency block (CurrRate and RefCurrRate are required) ---
        yield return new XElement(Ns + "LocalCurrencyCode", "CZK");
        if (!isCzk)
            yield return new XElement(Ns + "ForeignCurrencyCode", currencyCode);
        yield return new XElement(Ns + "CurrRate", FormatDecimal(1));
        yield return new XElement(Ns + "RefCurrRate", FormatDecimal(1));

        // --- Parties ---
        yield return MapSupplierParty(invoice.Issuer!);
        yield return MapCustomerParty(invoice.Client);

        // --- Lines (wrapped in InvoiceLines container) ---
        yield return new XElement(Ns + "InvoiceLines", MapInvoiceLines(invoice));

        // --- Totals ---
        yield return MapTaxTotal(invoice);
        yield return MapLegalMonetaryTotal(invoice);

        // --- Payment ---
        var paymentMeans = MapPaymentMeans(invoice);
        if (paymentMeans != null) yield return paymentMeans;
    }

    // --------------------------------------------------------------------------
    // Parties
    // --------------------------------------------------------------------------

    private static XElement MapSupplierParty(Client issuer) =>
        new(Ns + "AccountingSupplierParty",
            new XElement(Ns + "Party", BuildPartyElements(issuer)));

    private static XElement MapCustomerParty(Client? client) =>
        new(Ns + "AccountingCustomerParty",
            new XElement(Ns + "Party", BuildPartyElements(client)));

    private static IEnumerable<object> BuildPartyElements(Client? client)
    {
        if (client == null) yield break;

        // PartyIdentification (required) -- holds ICO
        yield return new XElement(Ns + "PartyIdentification",
            new XElement(Ns + "ID", client.RegistrationNumber ?? string.Empty));

        // PartyName (required)
        yield return new XElement(Ns + "PartyName",
            new XElement(Ns + "Name", client.CompanyName ?? string.Empty));

        // PostalAddress (required) -- all children required in official XSD
        var address = client.Address?.FirstOrDefault(a => a.IsPrimary)
                   ?? client.Address?.FirstOrDefault();

        var street = address?.Street ?? string.Empty;
        var city = address?.City ?? string.Empty;
        var postalCode = address?.PostalCode ?? string.Empty;
        var countryCode = string.IsNullOrWhiteSpace(address?.Country) ? "CZ" : address!.Country;

        yield return new XElement(Ns + "PostalAddress",
            new XElement(Ns + "StreetName", street),
            new XElement(Ns + "BuildingNumber", ExtractBuildingNumber(street)),
            new XElement(Ns + "CityName", city),
            new XElement(Ns + "PostalZone", postalCode),
            new XElement(Ns + "Country",
                new XElement(Ns + "IdentificationCode", countryCode),
                new XElement(Ns + "Name", countryCode)));

        // PartyTaxScheme (optional) -- only for VAT payers
        if (client.IsVatPayer && !string.IsNullOrWhiteSpace(client.TaxNumber))
            yield return new XElement(Ns + "PartyTaxScheme",
                new XElement(Ns + "CompanyID", client.TaxNumber),
                new XElement(Ns + "TaxScheme", "VAT"));

        // Contact (optional) -- XSD: Name?, Telephone?, ElectronicMail?
        var contact = MapContact(client);
        if (contact != null)
            yield return contact;
    }

    private static XElement? MapContact(Client client)
    {
        var contacts = client.Contact;
        if (contacts == null || contacts.Count == 0) return null;

        var phone = contacts.FirstOrDefault(c => c.ContactType == EContactType.Phone)?.ContactValue;
        var email = contacts.FirstOrDefault(c => c.ContactType == EContactType.Email)?.ContactValue;
        var name = contacts.FirstOrDefault(c => c.ContactType == EContactType.Phone)?.Label
                ?? contacts.FirstOrDefault(c => c.ContactType == EContactType.Email)?.Label;

        if (string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(email))
            return null;

        var el = new XElement(Ns + "Contact");
        if (!string.IsNullOrWhiteSpace(name))
            el.Add(new XElement(Ns + "Name", name));
        if (!string.IsNullOrWhiteSpace(phone))
            el.Add(new XElement(Ns + "Telephone", phone));
        if (!string.IsNullOrWhiteSpace(email))
            el.Add(new XElement(Ns + "ElectronicMail", email));
        return el;
    }

    // --------------------------------------------------------------------------
    // Invoice lines
    // --------------------------------------------------------------------------

    private static IEnumerable<XElement> MapInvoiceLines(Invoice invoice)
    {
        var items = invoice.InvoiceItem?
            .Where(i => !i.IsTextRow)
            .OrderBy(i => i.OrderIndex)
            .ToList() ?? new List<InvoiceItem>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lineId = (i + 1).ToString(CultureInfo.InvariantCulture);

            // Official XSD InvoiceLineType sequence:
            //   ID -> ... -> InvoicedQuantity? -> LineExtensionAmountCurr? ->
            //   LineExtensionAmount -> ... -> LineExtensionAmountTaxInclusive ->
            //   ... -> LineExtensionTaxAmount -> UnitPrice -> UnitPriceTaxInclusive ->
            //   ClassifiedTaxCategory -> ... -> Item?
            var unitPriceTaxInclusive = item.UnitPrice * (1 + item.VatRatePercentage / 100m);

            yield return new XElement(Ns + "InvoiceLine",
                new XElement(Ns + "ID", lineId),
                new XElement(Ns + "InvoicedQuantity",
                    new XAttribute("unitCode", item.Unit ?? "H87"),
                    FormatDecimal(item.Quantity)),
                new XElement(Ns + "LineExtensionAmount",
                    FormatDecimal(item.TotalBeforeVat)),
                new XElement(Ns + "LineExtensionAmountTaxInclusive",
                    FormatDecimal(item.TotalWithVat)),
                new XElement(Ns + "LineExtensionTaxAmount",
                    FormatDecimal(item.VatAmount)),
                new XElement(Ns + "UnitPrice",
                    FormatDecimal(item.UnitPrice)),
                new XElement(Ns + "UnitPriceTaxInclusive",
                    FormatDecimal(unitPriceTaxInclusive)),
                new XElement(Ns + "ClassifiedTaxCategory",
                    new XElement(Ns + "Percent", FormatDecimal(item.VatRatePercentage)),
                    new XElement(Ns + "VATCalculationMethod", "0")),
                new XElement(Ns + "Item",
                    new XElement(Ns + "Description", item.Description ?? string.Empty)));
        }
    }

    // --------------------------------------------------------------------------
    // Tax totals
    // --------------------------------------------------------------------------

    private static XElement MapTaxTotal(Invoice invoice)
    {
        // Official XSD TaxTotalType: TaxSubTotal(1..n) -> TaxAmountCurr? -> TaxAmount
        var taxGroups = (invoice.InvoiceItem ?? Enumerable.Empty<InvoiceItem>())
            .Where(i => !i.IsTextRow)
            .GroupBy(i => i.VatRatePercentage)
            .Select(g => new
            {
                Rate = g.Key,
                TaxableAmount = g.Sum(x => x.TotalBeforeVat),
                TaxAmount = g.Sum(x => x.VatAmount),
                TaxInclusiveAmount = g.Sum(x => x.TotalWithVat)
            })
            .OrderByDescending(g => g.Rate)
            .ToList();

        var taxTotalEl = new XElement(Ns + "TaxTotal");

        foreach (var group in taxGroups)
        {
            // Official TaxSubTotalType has many required elements for advance-payment
            // scenarios. For standard invoices: AlreadyClaimed* = 0, Difference* = actual.
            taxTotalEl.Add(new XElement(Ns + "TaxSubTotal",
                new XElement(Ns + "TaxableAmount",                       FormatDecimal(group.TaxableAmount)),
                new XElement(Ns + "TaxAmount",                           FormatDecimal(group.TaxAmount)),
                new XElement(Ns + "TaxInclusiveAmount",                  FormatDecimal(group.TaxInclusiveAmount)),
                new XElement(Ns + "AlreadyClaimedTaxableAmount",         FormatDecimal(0)),
                new XElement(Ns + "AlreadyClaimedTaxAmount",             FormatDecimal(0)),
                new XElement(Ns + "AlreadyClaimedTaxInclusiveAmount",    FormatDecimal(0)),
                new XElement(Ns + "DifferenceTaxableAmount",             FormatDecimal(group.TaxableAmount)),
                new XElement(Ns + "DifferenceTaxAmount",                 FormatDecimal(group.TaxAmount)),
                new XElement(Ns + "DifferenceTaxInclusiveAmount",        FormatDecimal(group.TaxInclusiveAmount)),
                new XElement(Ns + "TaxCategory",
                    new XElement(Ns + "Percent", FormatDecimal(group.Rate)),
                    new XElement(Ns + "TaxScheme", "VAT"))));
        }

        // TaxAmount at the end
        taxTotalEl.Add(new XElement(Ns + "TaxAmount", FormatDecimal(invoice.TotalVat)));

        return taxTotalEl;
    }

    // --------------------------------------------------------------------------
    // Legal monetary total
    // --------------------------------------------------------------------------

    private static XElement MapLegalMonetaryTotal(Invoice invoice)
    {
        // Official XSD LegalMonetaryTotalType: many required elements for advance-payment.
        // For standard invoices: AlreadyClaimed* = 0, Difference* = actual, PaidDeposits = 0.
        return new XElement(Ns + "LegalMonetaryTotal",
            new XElement(Ns + "TaxExclusiveAmount",                  FormatDecimal(invoice.TotalBeforeVat)),
            new XElement(Ns + "TaxInclusiveAmount",                  FormatDecimal(invoice.TotalWithVat)),
            new XElement(Ns + "AlreadyClaimedTaxExclusiveAmount",    FormatDecimal(0)),
            new XElement(Ns + "AlreadyClaimedTaxInclusiveAmount",    FormatDecimal(0)),
            new XElement(Ns + "DifferenceTaxExclusiveAmount",        FormatDecimal(invoice.TotalBeforeVat)),
            new XElement(Ns + "DifferenceTaxInclusiveAmount",        FormatDecimal(invoice.TotalWithVat)),
            new XElement(Ns + "PaidDepositsAmount",                  FormatDecimal(0)),
            new XElement(Ns + "PayableAmount",                       FormatDecimal(invoice.TotalWithVat)));
    }

    // --------------------------------------------------------------------------
    // Payment means
    // --------------------------------------------------------------------------

    private static XElement? MapPaymentMeans(Invoice invoice)
    {
        if (!invoice.PaymentMethod.HasValue) return null;

        // Official XSD PaymentType: PaidAmount (required) -> PaymentMeansCode -> Details?
        var payment = new XElement(Ns + "Payment",
            new XElement(Ns + "PaidAmount", FormatDecimal(invoice.TotalWithVat)),
            new XElement(Ns + "PaymentMeansCode",
                MapPaymentMeansCode(invoice.PaymentMethod.Value)));

        var details = BuildPaymentDetails(invoice);
        if (details != null) payment.Add(details);

        return new XElement(Ns + "PaymentMeans", payment);
    }

    private static XElement? BuildPaymentDetails(Invoice invoice)
    {
        // Official XSD DetailsType uses xs:choice:
        //   1) Cash: DocumentID + IssueDate
        //   2) Transfer: PaymentDueDate -> BankAccount(ID,BankCode,Name,IBAN,BIC) -> symbols
        // For bank transfer, we need at minimum PaymentDueDate + BankAccount group.
        // All BankAccount elements are required (no minOccurs=0).

        var isBankTransfer = invoice.PaymentMethod == EPaymentMethod.BankTransfer;

        if (isBankTransfer && invoice.DueDate.HasValue)
        {
            // Parse CZ bank account format "number/bankcode"
            var (accountNumber, bankCode) = ParseBankAccount(invoice.BankAccountNumber);

            var el = new XElement(Ns + "Details",
                new XElement(Ns + "PaymentDueDate", FormatDate(invoice.DueDate)),
                new XElement(Ns + "ID", accountNumber),
                new XElement(Ns + "BankCode", bankCode),
                new XElement(Ns + "Name", string.Empty),
                new XElement(Ns + "IBAN", invoice.IBAN ?? string.Empty),
                new XElement(Ns + "BIC", invoice.SWIFT ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(invoice.VariableSymbol))
                el.Add(new XElement(Ns + "VariableSymbol", invoice.VariableSymbol));
            if (!string.IsNullOrWhiteSpace(invoice.ConstantSymbol))
                el.Add(new XElement(Ns + "ConstantSymbol", invoice.ConstantSymbol));
            if (!string.IsNullOrWhiteSpace(invoice.SpecificSymbol))
                el.Add(new XElement(Ns + "SpecificSymbol", invoice.SpecificSymbol));

            return el;
        }

        return null;
    }

    // --------------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------------

    /// <summary>S = standard rate (rate > 0%),  Z = zero rate (0%).</summary>
    internal static string MapTaxCategoryCode(decimal vatRatePercentage)
        => vatRatePercentage > 0 ? "S" : "Z";

    private static string MapPaymentMeansCode(EPaymentMethod method) =>
        method switch
        {
            EPaymentMethod.BankTransfer => "42",
            EPaymentMethod.Cash         => "10",
            EPaymentMethod.CreditCard   => "48",
            EPaymentMethod.PayPal       => "ZZZ",
            EPaymentMethod.Other        => "ZZZ",
            _                           => "ZZZ"
        };

    private static string FormatDecimal(decimal value)
        => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime? date)
        => date.HasValue
            ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>
    /// Extracts a building number from a Czech street address string.
    /// E.g. "Narodni 1" -> "1", "Masarykova 2" -> "2".
    /// Returns empty string if no number found.
    /// </summary>
    private static string ExtractBuildingNumber(string street)
    {
        if (string.IsNullOrWhiteSpace(street)) return string.Empty;
        var parts = street.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[^1] : string.Empty;
    }

    /// <summary>
    /// Parses CZ bank account format "number/bankcode" into components.
    /// </summary>
    private static (string AccountNumber, string BankCode) ParseBankAccount(string? bankAccount)
    {
        if (string.IsNullOrWhiteSpace(bankAccount)) return (string.Empty, string.Empty);
        var idx = bankAccount.IndexOf('/');
        if (idx < 0) return (bankAccount, string.Empty);
        return (bankAccount[..idx], bankAccount[(idx + 1)..]);
    }

    private static XElement? OptionalElement(XName name, string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);

    /// <summary>
    /// Generates a deterministic UUID v5 (SHA-1, RFC 4122 DNS namespace).
    /// </summary>
    private static string DeterministicUuid(long invoiceId)
    {
        byte[] ns = [0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1,
                     0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8];
        var nameBytes = Encoding.UTF8.GetBytes($"fakvio-invoice-{invoiceId}");
        var combined = ns.Concat(nameBytes).ToArray();
        var hash = SHA1.HashData(combined);
        var uuid = hash.Take(16).ToArray();
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        return new Guid(uuid).ToString("D");
    }
}
