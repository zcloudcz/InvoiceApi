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
/// Element order strictly follows the official ISDOC 6.0.2 XSD InvoiceType sequence:
///   DocumentType -> SubDocumentType? -> ... -> ID -> UUID -> EgovFlag? ->
///   ... -> IssuingSystem? -> IssueDate -> TaxPointDate -> VATApplicable? ->
///   Note(0..n) -> LocalCurrencyCode -> ForeignCurrencyCode? ->
///   CurrRate? -> RefCurrRate? -> AccountingSupplierParty -> AccountingCustomerParty ->
///   InvoiceLine(1..n) -> TaxTotal -> LegalMonetaryTotal -> PaymentMeans -> Supplements
/// </summary>
internal static class IsdocMapper
{
    /// <summary>XML namespace used by ISDOC 6.0.2 standard.</summary>
    internal static readonly XNamespace Ns = "http://isdoc.cz/namespace/2013";

    private const string IsdocVersion = "6.0.2";

    /// <summary>
    /// Note appended to foreign-currency invoices to explain that the
    /// exchange rate (CurrRate) is not available in the source data.
    /// A future story (#4) will add live rate lookup.
    /// </summary>
    internal const string ForeignCurrencyNote =
        "Foreign currency invoice -- exchange rate not available in source data";

    /// <summary>
    /// Maps an Invoice entity to a fully-formed ISDOC 6.0.2 XDocument.
    /// The root XElement gets its xmlns declaration from LINQ-to-XML automatically
    /// (do NOT add an explicit XAttribute("xmlns", ...) -- that would duplicate it).
    /// </summary>
    internal static XDocument Map(Invoice invoice)
    {
        // LINQ-to-XML adds xmlns="..." automatically when the element uses a namespace.
        // Adding XAttribute("xmlns",...) manually on top would create a redundant/empty
        // default namespace on child elements -- so we let LINQ handle it.
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

        // --- Identification block (XSD sequence: DocumentType, ID, UUID, ..., IssuingSystem) ---
        yield return new XElement(Ns + "DocumentType", docType);
        yield return new XElement(Ns + "ID", invoice.DocumentNumber ?? DeterministicUuid(invoice.Id));
        yield return new XElement(Ns + "UUID", DeterministicUuid(invoice.Id));
        yield return new XElement(Ns + "IssuingSystem", "Fakvio");

        // --- Date block ---
        yield return new XElement(Ns + "IssueDate", FormatDate(invoice.IssueDate));
        if (invoice.TaxableSupplyDate.HasValue)
            yield return new XElement(Ns + "TaxPointDate", FormatDate(invoice.TaxableSupplyDate));

        // --- Notes (before LocalCurrencyCode per XSD sequence) ---
        // For foreign-currency invoices emit both the user's notes AND the
        // exchange-rate note. XSD allows Note maxOccurs="unbounded".
        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            yield return new XElement(Ns + "Note", invoice.Notes);
        if (!isCzk)
            yield return new XElement(Ns + "Note", ForeignCurrencyNote);

        // --- Currency block (LocalCurrencyCode is required by XSD, no minOccurs=0) ---
        // LocalCurrencyCode = accounting-entity's own currency (always CZK for CZ issuers).
        // ForeignCurrencyCode = invoice currency when it differs from CZK.
        yield return new XElement(Ns + "LocalCurrencyCode", "CZK");
        if (!isCzk)
            yield return new XElement(Ns + "ForeignCurrencyCode", currencyCode);

        // --- Parties ---
        yield return MapSupplierParty(invoice.Issuer!);
        yield return MapCustomerParty(invoice.Client);

        // --- Lines ---
        foreach (var line in MapInvoiceLines(invoice))
            yield return line;

        // --- Totals ---
        yield return MapTaxTotal(invoice);
        yield return MapLegalMonetaryTotal(invoice);

        // --- Payment (last in InvoiceType sequence before Supplements) ---
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

        // PartyIdentification -- holds ICO (registration number)
        if (!string.IsNullOrWhiteSpace(client.RegistrationNumber))
            yield return new XElement(Ns + "PartyIdentification",
                new XElement(Ns + "ID", client.RegistrationNumber));

        // Company display name
        yield return new XElement(Ns + "PartyName",
            new XElement(Ns + "Name", client.CompanyName ?? string.Empty));

        // First address (prefer primary)
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

        // Tax scheme with DIC (VAT number) -- only for VAT payers
        if (client.IsVatPayer && !string.IsNullOrWhiteSpace(client.TaxNumber))
            yield return new XElement(Ns + "PartyTaxScheme",
                new XElement(Ns + "CompanyID", client.TaxNumber),
                new XElement(Ns + "TaxScheme", "VAT"));
    }

    // --------------------------------------------------------------------------
    // Invoice lines
    // --------------------------------------------------------------------------

    private static IEnumerable<XElement> MapInvoiceLines(Invoice invoice)
    {
        // Skip text-only rows (section headers, comments) -- they carry no amounts
        var items = invoice.InvoiceItem?
            .Where(i => !i.IsTextRow)
            .OrderBy(i => i.OrderIndex)
            .ToList() ?? new List<InvoiceItem>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lineId = (i + 1).ToString(CultureInfo.InvariantCulture);

            // XSD InvoiceLineType sequence:
            //   ID -> InvoicedQuantity -> LineExtensionAmount -> ... ->
            //   UnitPrice -> ... -> ClassifiedTaxCategory -> ... -> Item
            //
            // AmountType in XSD is plain xs:decimal -- no currencyID attribute allowed.
            // ClassifiedTaxCategoryType has: Percent, VATCalculationMethod, VATApplicable.
            //   (NOT TaxScheme -- that lives in TaxTotal/TaxSubTotal/TaxCategory.)
            // Item (ItemType) contains ONLY Description -- no SellersItemIdentification or
            //   ClassifiedTaxCategory inside Item.
            //
            // unitCode "H87" is UN/ECE Recommendation 20 code for "piece" (kus).
            // We use it as fallback when the item has no explicit unit.
            yield return new XElement(Ns + "InvoiceLine",
                new XElement(Ns + "ID", lineId),
                new XElement(Ns + "InvoicedQuantity",
                    new XAttribute("unitCode", item.Unit ?? "H87"), // H87 = UN/ECE Rec.20 code for "piece"
                    FormatDecimal(item.Quantity)),
                new XElement(Ns + "LineExtensionAmount",
                    FormatDecimal(item.TotalBeforeVat)),
                new XElement(Ns + "UnitPrice",
                    FormatDecimal(item.UnitPrice)),
                new XElement(Ns + "ClassifiedTaxCategory",
                    new XElement(Ns + "Percent", FormatDecimal(item.VatRatePercentage))),
                new XElement(Ns + "Item",
                    new XElement(Ns + "Description", item.Description ?? string.Empty)));
        }
    }

    // --------------------------------------------------------------------------
    // Tax totals
    // --------------------------------------------------------------------------

    private static XElement MapTaxTotal(Invoice invoice)
    {
        // TaxTotal holds one TaxAmount (overall) + one TaxSubTotal per VAT rate.
        // Element name is TaxSubTotal (capital T) -- XSD TaxTotalType uses "TaxSubTotal".
        // TaxCategoryType allows only: Percent, TaxScheme.
        // AmountType = plain xs:decimal, no currencyID attribute.
        var taxGroups = invoice.InvoiceItem?
            .Where(i => !i.IsTextRow)
            .GroupBy(i => i.VatRatePercentage)
            .Select(g => new
            {
                Rate = g.Key,
                TaxableAmount = g.Sum(x => x.TotalBeforeVat),
                TaxAmount = g.Sum(x => x.VatAmount)
            })
            .OrderByDescending(g => g.Rate)
            .ToList();

        var taxTotalEl = new XElement(Ns + "TaxTotal",
            new XElement(Ns + "TaxAmount", FormatDecimal(invoice.TotalVat)));

        if (taxGroups != null)
        {
            foreach (var group in taxGroups)
            {
                taxTotalEl.Add(new XElement(Ns + "TaxSubTotal",   // capital T: TaxSubTotal
                    new XElement(Ns + "TaxableAmount", FormatDecimal(group.TaxableAmount)),
                    new XElement(Ns + "TaxAmount",     FormatDecimal(group.TaxAmount)),
                    new XElement(Ns + "TaxCategory",
                        new XElement(Ns + "Percent",   FormatDecimal(group.Rate)),
                        new XElement(Ns + "TaxScheme", "VAT"))));
            }
        }

        return taxTotalEl;
    }

    // --------------------------------------------------------------------------
    // Legal monetary total
    // --------------------------------------------------------------------------

    private static XElement MapLegalMonetaryTotal(Invoice invoice)
    {
        // LegalMonetaryTotalType sequence starts with TaxExclusiveAmount (not LineExtensionAmount).
        // AmountType = plain xs:decimal, no currencyID attribute.
        return new XElement(Ns + "LegalMonetaryTotal",
            new XElement(Ns + "TaxExclusiveAmount",  FormatDecimal(invoice.TotalBeforeVat)),
            new XElement(Ns + "TaxInclusiveAmount",  FormatDecimal(invoice.TotalWithVat)),
            new XElement(Ns + "PayableAmount",       FormatDecimal(invoice.TotalWithVat)));
    }

    // --------------------------------------------------------------------------
    // Payment means
    // --------------------------------------------------------------------------

    private static XElement? MapPaymentMeans(Invoice invoice)
    {
        // PaymentMeansType wraps everything inside a <Payment> element:
        //   PaymentMeans -> Payment -> PaidAmount? + PaymentMeansCode + Details?
        //
        // PaymentMeansCode is required inside Payment (no minOccurs=0 in PaymentType),
        // so we only emit PaymentMeans when a payment method is known.
        if (!invoice.PaymentMethod.HasValue) return null;

        var payment = new XElement(Ns + "Payment",
            new XElement(Ns + "PaymentMeansCode",
                MapPaymentMeansCode(invoice.PaymentMethod.Value)));

        var details = BuildPaymentDetails(invoice);
        if (details != null) payment.Add(details);

        return new XElement(Ns + "PaymentMeans", payment);
    }

    private static XElement? BuildPaymentDetails(Invoice invoice)
    {
        var hasBankDetails = !string.IsNullOrWhiteSpace(invoice.BankAccountNumber)
                          || !string.IsNullOrWhiteSpace(invoice.IBAN);
        var hasSymbol = !string.IsNullOrWhiteSpace(invoice.VariableSymbol)
                     || !string.IsNullOrWhiteSpace(invoice.ConstantSymbol)
                     || !string.IsNullOrWhiteSpace(invoice.SpecificSymbol);
        var hasDueDate = invoice.DueDate.HasValue;

        if (!hasBankDetails && !hasSymbol && !hasDueDate) return null;

        // DetailsType sequence (XSD):
        //   PaymentDueDate -> ID -> BankCode -> Name -> IBAN -> BIC ->
        //   VariableSymbol -> ConstantSymbol -> SpecificSymbol -> DocumentID -> IssueDate
        var el = new XElement(Ns + "Details");

        // Due date belongs here (NOT at the invoice header level)
        if (invoice.DueDate.HasValue)
            el.Add(new XElement(Ns + "PaymentDueDate", FormatDate(invoice.DueDate)));

        // ID = local bank account number (CZ format "XXXXXXXXXX/CCCC")
        if (!string.IsNullOrWhiteSpace(invoice.BankAccountNumber))
            el.Add(new XElement(Ns + "ID", invoice.BankAccountNumber));

        // IBAN and BIC/SWIFT have their own dedicated elements in DetailsType
        if (!string.IsNullOrWhiteSpace(invoice.IBAN))
            el.Add(new XElement(Ns + "IBAN", invoice.IBAN));
        if (!string.IsNullOrWhiteSpace(invoice.SWIFT))
            el.Add(new XElement(Ns + "BIC", invoice.SWIFT));

        // Czech payment symbols
        if (!string.IsNullOrWhiteSpace(invoice.VariableSymbol))
            el.Add(new XElement(Ns + "VariableSymbol", invoice.VariableSymbol));
        if (!string.IsNullOrWhiteSpace(invoice.ConstantSymbol))
            el.Add(new XElement(Ns + "ConstantSymbol", invoice.ConstantSymbol));
        if (!string.IsNullOrWhiteSpace(invoice.SpecificSymbol))
            el.Add(new XElement(Ns + "SpecificSymbol", invoice.SpecificSymbol));

        return el;
    }

    // --------------------------------------------------------------------------
    // Tax category code helpers (open for extension per story #4)
    // --------------------------------------------------------------------------

    /// <summary>S = standard rate (rate > 0%),  Z = zero rate (0%).</summary>
    internal static string MapTaxCategoryCode(decimal vatRatePercentage)
        => vatRatePercentage > 0 ? "S" : "Z";

    private static string MapPaymentMeansCode(EPaymentMethod method) =>
        method switch
        {
            EPaymentMethod.BankTransfer => "42",  // UNCL4461: credit transfer
            EPaymentMethod.Cash         => "10",  // UNCL4461: cash
            EPaymentMethod.CreditCard   => "48",  // UNCL4461: bank card
            EPaymentMethod.PayPal       => "ZZZ", // mutually defined
            EPaymentMethod.Other        => "ZZZ",
            _                           => "ZZZ"
        };

    // --------------------------------------------------------------------------
    // Formatting helpers
    // --------------------------------------------------------------------------

    private static string FormatDecimal(decimal value)
        => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime? date)
        => date.HasValue
            ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;

    private static XElement? OptionalElement(XName name, string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);

    /// <summary>
    /// Generates a deterministic UUID v5 (SHA-1, RFC 4122 DNS namespace).
    /// The same invoiceId always produces the same UUID, so re-exporting
    /// the same invoice is idempotent and safe for deduplication.
    /// </summary>
    private static string DeterministicUuid(long invoiceId)
    {
        // RFC 4122 DNS namespace UUID (6ba7b810-9dad-11d1-80b4-00c04fd430c8)
        byte[] ns = [0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1,
                     0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8];
        var nameBytes = Encoding.UTF8.GetBytes($"fakvio-invoice-{invoiceId}");
        var combined = ns.Concat(nameBytes).ToArray();
        var hash = SHA1.HashData(combined);
        var uuid = hash.Take(16).ToArray();
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50); // set version = 5
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80); // set RFC 4122 variant
        return new Guid(uuid).ToString("D");
    }
}
