using System.Globalization;
using System.Xml.Linq;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.Ubl;

/// <summary>
/// Pure static mapper: converts an Invoice entity to a UBL 2.1 / Peppol BIS Billing 3.0
/// XDocument. No database access, no I/O -- just mapping (vzor <c>IsdocMapper</c>).
///
/// Element order strictly follows the official UBL 2.1 XSD sequence (see
/// <c>Fakvio.Tests.Unit/Ubl/Vendored/maindoc/</c>) -- getting the order wrong fails XSD
/// validation even when every individual element is otherwise correct.
///
/// Covers <see cref="EDocumentType.Invoice"/> (InvoiceTypeCode 380) and
/// <see cref="EDocumentType.TaxReceiptForAdvance"/> (386) — both use the UBL <c>Invoice-2</c>
/// root. <see cref="EDocumentType.CreditNote"/> (root <c>CreditNote-2</c>) is added in F1.4.
/// <see cref="EDocumentType.Proforma"/> is not an eInvoice at all ([FAQ] I/34) and is rejected.
/// </summary>
internal static class UblMapper
{
    internal static readonly XNamespace CacNs = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    internal static readonly XNamespace CbcNs = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace InvoiceNs = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";

    // Peppol BIS Billing 3.0 identifiers — constant for every document Fakvio produces.
    private const string CustomizationId =
        "urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0";
    private const string ProfileId = "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0";

    private const string VatTaxSchemeId = "VAT";

    /// <summary>
    /// Maps an issued <see cref="Invoice"/> (document type <c>Invoice</c> or
    /// <c>TaxReceiptForAdvance</c>) to a fully-formed UBL 2.1 <c>Invoice-2</c> XDocument.
    /// </summary>
    /// <param name="invoice">
    /// The invoice to map. Must have <see cref="Invoice.Issuer"/>, <see cref="Invoice.Client"/>,
    /// <see cref="Invoice.Currency"/> and <see cref="Invoice.InvoiceItem"/> loaded.
    /// </param>
    /// <param name="precedingDocumentNumbers">
    /// Document numbers this invoice references via <c>cac:BillingReference</c> — e.g. the tax
    /// receipts for advance a final invoice deducts (F1.4). Null/empty emits no reference.
    /// </param>
    internal static XDocument Map(Invoice invoice, IReadOnlyList<string>? precedingDocumentNumbers = null)
    {
        if (invoice.DocumentType == EDocumentType.Proforma)
            throw new InvalidOperationException(
                "A pro-forma invoice is not a tax document and cannot be exported as an eInvoice " +
                "([FAQ] I/34) — the caller must filter it out before calling UblMapper.Map.");

        if (invoice.DocumentType == EDocumentType.CreditNote)
            throw new InvalidOperationException(
                "Credit notes use the UBL CreditNote-2 root, not Invoice-2 — call the CreditNote " +
                "overload of UblMapper.Map instead.");

        var root = new XElement(InvoiceNs + "Invoice");
        root.Add(MapInvoiceHeader(invoice, precedingDocumentNumbers));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    // --------------------------------------------------------------------------
    // Header (top-level Invoice children, in UBL 2.1 XSD sequence order)
    // --------------------------------------------------------------------------

    private static IEnumerable<object> MapInvoiceHeader(Invoice invoice, IReadOnlyList<string>? precedingDocumentNumbers)
    {
        var currencyCode = invoice.Currency?.Code ?? "CZK";
        var issuerIsVatPayer = invoice.Issuer?.IsVatPayer == true;
        var language = invoice.Client?.Language ?? "cs";

        yield return new XElement(CbcNs + "CustomizationID", CustomizationId);
        yield return new XElement(CbcNs + "ProfileID", ProfileId);
        yield return new XElement(CbcNs + "ID", invoice.DocumentNumber ?? string.Empty);
        yield return new XElement(CbcNs + "IssueDate", FormatDate(invoice.IssueDate));
        if (invoice.DueDate.HasValue)
            yield return new XElement(CbcNs + "DueDate", FormatDate(invoice.DueDate));
        yield return new XElement(CbcNs + "InvoiceTypeCode", MapInvoiceTypeCode(invoice.DocumentType));

        // Text-only rows are not InvoiceLines (they carry no amount) — they surface here as
        // free-text notes instead, one per row, before the user's own invoice note.
        foreach (var textRowNote in TextRowNotes(invoice))
            yield return textRowNote;
        if (!string.IsNullOrWhiteSpace(invoice.Notes))
            yield return new XElement(CbcNs + "Note", invoice.Notes);

        // Only emitted when it actually differs from IssueDate — EN 16931 treats TaxPointDate as
        // meaningful precisely because it usually equals IssueDate and is omitted then.
        if (invoice.TaxableSupplyDate.HasValue &&
            invoice.TaxableSupplyDate.Value.Date != invoice.IssueDate?.Date)
        {
            yield return new XElement(CbcNs + "TaxPointDate", FormatDate(invoice.TaxableSupplyDate));
        }

        yield return new XElement(CbcNs + "DocumentCurrencyCode", currencyCode);

        // BT-10 fallback (PEPPOL-EN16931-R003 requires BuyerReference or OrderReference and
        // Fakvio has neither a real buyer-reference nor a PO-number field) — the document's own
        // number stands in, per ADR 0002 §4.1.4.
        yield return new XElement(CbcNs + "BuyerReference", invoice.DocumentNumber ?? string.Empty);

        foreach (var billingReference in MapBillingReferences(precedingDocumentNumbers))
            yield return billingReference;

        yield return MapParty("AccountingSupplierParty", invoice.Issuer, issuerIsVatPayer, invoice.InvoiceItem, language);
        yield return MapParty("AccountingCustomerParty", invoice.Client, issuerIsVatPayer, invoice.InvoiceItem, language);

        var paymentMeans = MapPaymentMeans(invoice, currencyCode);
        if (paymentMeans != null)
            yield return paymentMeans;

        var vatGroups = GroupLinesByVat(invoice.InvoiceItem, issuerIsVatPayer).ToList();
        yield return MapTaxTotal(vatGroups, currencyCode, language);
        yield return MapLegalMonetaryTotal(vatGroups, currencyCode);

        foreach (var line in MapInvoiceLines(invoice.InvoiceItem, issuerIsVatPayer, currencyCode))
            yield return line;
    }

    private static IEnumerable<XElement> TextRowNotes(Invoice invoice)
        => (invoice.InvoiceItem ?? Enumerable.Empty<InvoiceItem>())
            .Where(i => i.IsTextRow && !string.IsNullOrWhiteSpace(i.Description))
            .OrderBy(i => i.OrderIndex)
            .Select(i => new XElement(CbcNs + "Note", i.Description));

    private static string MapInvoiceTypeCode(EDocumentType documentType) => documentType switch
    {
        EDocumentType.Invoice => "380",
        EDocumentType.TaxReceiptForAdvance => "386",
        _ => throw new InvalidOperationException(
            $"Document type {documentType} has no UBL InvoiceTypeCode mapping.")
    };

    // --------------------------------------------------------------------------
    // BillingReference — references to preceding documents (F1.4 populates this for real)
    // --------------------------------------------------------------------------

    private static IEnumerable<XElement> MapBillingReferences(IReadOnlyList<string>? precedingDocumentNumbers)
    {
        if (precedingDocumentNumbers is null)
            yield break;

        foreach (var documentNumber in precedingDocumentNumbers)
        {
            if (string.IsNullOrWhiteSpace(documentNumber))
                continue;

            yield return new XElement(CacNs + "BillingReference",
                new XElement(CacNs + "InvoiceDocumentReference",
                    new XElement(CbcNs + "ID", documentNumber)));
        }
    }

    // --------------------------------------------------------------------------
    // Parties
    // --------------------------------------------------------------------------

    /// <summary>
    /// Builds <c>cac:AccountingSupplierParty</c> or <c>cac:AccountingCustomerParty</c>.
    /// <paramref name="issuerIsVatPayer"/> gates whether *either* party gets a
    /// <c>cac:PartyTaxScheme</c> at all — a non-VAT-payer issuer's document carries no VAT
    /// accounting information anywhere (ADR 0002 §4.1.2: category O, no BT-31/BT-48).
    /// </summary>
    private static XElement MapParty(
        string wrapperElementName, Client? party, bool issuerIsVatPayer,
        ICollection<InvoiceItem>? items, string language)
    {
        party ??= new Client();

        var elements = new List<object>();

        var endpointId = UblCodes.EndpointId(party);
        if (endpointId is { } id)
            elements.Add(new XElement(CbcNs + "EndpointID", new XAttribute("schemeID", id.SchemeId), id.Value));

        if (!string.IsNullOrWhiteSpace(party.RegistrationNumber))
            elements.Add(new XElement(CacNs + "PartyIdentification",
                new XElement(CbcNs + "ID", party.RegistrationNumber)));

        elements.Add(new XElement(CacNs + "PartyName",
            new XElement(CbcNs + "Name", party.TradingName ?? party.CompanyName ?? string.Empty)));

        elements.Add(MapPostalAddress(party));

        // A non-VAT-payer issuer's invoice has no VAT accounting at all (category O everywhere) —
        // omit PartyTaxScheme for both parties rather than declare a VAT ID that would be
        // meaningless on a document with no VAT category to attach it to.
        if (issuerIsVatPayer && party.IsVatPayer && !string.IsNullOrWhiteSpace(party.TaxNumber))
        {
            elements.Add(new XElement(CacNs + "PartyTaxScheme",
                new XElement(CbcNs + "CompanyID", party.TaxNumber),
                new XElement(CacNs + "TaxScheme", new XElement(CbcNs + "ID", VatTaxSchemeId))));
        }

        if (!string.IsNullOrWhiteSpace(party.RegistrationNumber) || !string.IsNullOrWhiteSpace(party.CompanyName))
        {
            var legalEntity = new List<object>
            {
                new XElement(CbcNs + "RegistrationName", party.CompanyName ?? string.Empty)
            };
            if (!string.IsNullOrWhiteSpace(party.RegistrationNumber))
            {
                var country = PrimaryCountry(party);
                var companyIdElement = new XElement(CbcNs + "CompanyID", party.RegistrationNumber);
                // schemeID 0158 = Slovak business register (RPO) identifier — only meaningful
                // (and only used in the SK transposition table) for a Slovak party.
                if (country == "SK")
                    companyIdElement.Add(new XAttribute("schemeID", "0158"));
                legalEntity.Add(companyIdElement);
            }
            elements.Add(new XElement(CacNs + "PartyLegalEntity", legalEntity));
        }

        var contact = MapContact(party);
        if (contact != null)
            elements.Add(contact);

        return new XElement(CacNs + wrapperElementName, new XElement(CacNs + "Party", elements));
    }

    private static string? PrimaryCountry(Client party)
    {
        var address = party.Address?.FirstOrDefault(a => a.IsPrimary) ?? party.Address?.FirstOrDefault();
        return UblCodes.CountryToIso2(address?.Country);
    }

    private static XElement MapPostalAddress(Client party)
    {
        var address = party.Address?.FirstOrDefault(a => a.IsPrimary) ?? party.Address?.FirstOrDefault();

        var elements = new List<object>
        {
            new XElement(CbcNs + "StreetName", address?.Street ?? string.Empty)
        };
        if (!string.IsNullOrWhiteSpace(address?.AddressLine2))
            elements.Add(new XElement(CbcNs + "AdditionalStreetName", address.AddressLine2));
        elements.Add(new XElement(CbcNs + "CityName", address?.City ?? string.Empty));
        elements.Add(new XElement(CbcNs + "PostalZone", address?.PostalCode ?? string.Empty));
        elements.Add(new XElement(CacNs + "Country",
            new XElement(CbcNs + "IdentificationCode", UblCodes.CountryToIso2(address?.Country) ?? "CZ")));

        return new XElement(CacNs + "PostalAddress", elements);
    }

    private static XElement? MapContact(Client party)
    {
        var contacts = party.Contact;
        if (contacts == null || contacts.Count == 0)
            return null;

        var phone = contacts.FirstOrDefault(c => c.ContactType == EContactType.Phone)?.ContactValue;
        var email = contacts.FirstOrDefault(c => c.ContactType == EContactType.Email)?.ContactValue;
        if (string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(email))
            return null;

        var elements = new List<object>();
        if (!string.IsNullOrWhiteSpace(phone))
            elements.Add(new XElement(CbcNs + "Telephone", phone));
        if (!string.IsNullOrWhiteSpace(email))
            elements.Add(new XElement(CbcNs + "ElectronicMail", email));

        return new XElement(CacNs + "Contact", elements);
    }

    // --------------------------------------------------------------------------
    // Payment means
    // --------------------------------------------------------------------------

    private static XElement? MapPaymentMeans(Invoice invoice, string currencyCode)
    {
        if (!invoice.PaymentMethod.HasValue)
            return null;

        var hasIban = !string.IsNullOrWhiteSpace(invoice.IBAN);
        var currencyIsEur = currencyCode.Equals("EUR", StringComparison.OrdinalIgnoreCase);
        var code = UblCodes.PaymentMeansCode(invoice.PaymentMethod, hasIban, currencyIsEur);

        var elements = new List<object>
        {
            new XElement(CbcNs + "PaymentMeansCode", code)
        };

        if (!string.IsNullOrWhiteSpace(invoice.VariableSymbol))
            elements.Add(new XElement(CbcNs + "PaymentID", invoice.VariableSymbol));

        // BR-61: a SEPA credit transfer (code 58) must carry an IBAN. Any other bank-transfer
        // account is emitted with whatever identifier Fakvio has (IBAN preferred).
        var accountId = invoice.IBAN ?? invoice.BankAccountNumber;
        if (invoice.PaymentMethod == EPaymentMethod.BankTransfer && !string.IsNullOrWhiteSpace(accountId))
        {
            var accountElements = new List<object> { new XElement(CbcNs + "ID", accountId) };
            if (!string.IsNullOrWhiteSpace(invoice.SWIFT))
                accountElements.Add(new XElement(CacNs + "FinancialInstitutionBranch",
                    new XElement(CbcNs + "ID", invoice.SWIFT)));

            elements.Add(new XElement(CacNs + "PayeeFinancialAccount", accountElements));
        }

        return new XElement(CacNs + "PaymentMeans", elements);
    }

    // --------------------------------------------------------------------------
    // Lines
    // --------------------------------------------------------------------------

    private static IEnumerable<XElement> MapInvoiceLines(
        ICollection<InvoiceItem>? items, bool issuerIsVatPayer, string currencyCode)
    {
        var lineId = 0;
        foreach (var item in (items ?? Enumerable.Empty<InvoiceItem>()).Where(i => !i.IsTextRow).OrderBy(i => i.OrderIndex))
        {
            lineId++;
            yield return MapInvoiceLine(lineId.ToString(CultureInfo.InvariantCulture), item, issuerIsVatPayer, currencyCode);
        }
    }

    private static XElement MapInvoiceLine(string lineId, InvoiceItem item, bool issuerIsVatPayer, string currencyCode)
    {
        var category = UblCodes.VatCategory(item.VatRegime, item.VatRatePercentage, issuerIsVatPayer);

        return new XElement(CacNs + "InvoiceLine",
            new XElement(CbcNs + "ID", lineId),
            new XElement(CbcNs + "InvoicedQuantity",
                new XAttribute("unitCode", UblCodes.UnitToRec20(item.Unit)),
                FormatDecimal(item.Quantity)),
            AmountElement(CbcNs + "LineExtensionAmount", item.TotalBeforeVat, currencyCode),
            new XElement(CacNs + "Item", MapItem(item, category)),
            new XElement(CacNs + "Price", AmountElement(CbcNs + "PriceAmount", item.UnitPrice, currencyCode)));
    }

    private static IEnumerable<object> MapItem(InvoiceItem item, UblCodes.VatCategoryResult category)
    {
        var description = item.Description ?? string.Empty;
        // BT-153 (Item name) has no hard length limit in the XSD, but EN 16931 guidance treats
        // it as a short label — the full text always stays available in cbc:Description too
        // when it does not fit in 100 characters.
        var name = description.Length > 100 ? description[..100] : description;

        if (description.Length > 100)
            yield return new XElement(CbcNs + "Description", description);
        yield return new XElement(CbcNs + "Name", name);

        if (!string.IsNullOrWhiteSpace(item.ProductCode))
            yield return new XElement(CacNs + "SellersItemIdentification",
                new XElement(CbcNs + "ID", item.ProductCode));

        // No exemption reason at line level — matches the official Peppol fixtures (e.g.
        // vat-category-O.xml), which only state the exemption text once, on the header
        // TaxTotal/TaxSubtotal/TaxCategory, not repeated on every ClassifiedTaxCategory.
        yield return BuildTaxCategory("ClassifiedTaxCategory", category, includeExemptionReason: false, language: null);
    }

    // --------------------------------------------------------------------------
    // Tax totals (computed from the lines, not from the stored Invoice totals —
    // see ADR 0002 §4.1.2; UblExportService, F1.5, cross-checks against Invoice.TotalVat)
    // --------------------------------------------------------------------------

    /// <summary>One VAT bucket on the document: a (category, rate) pair and its summed amounts.</summary>
    private readonly record struct VatGroup(UblCodes.VatCategoryResult Category, decimal TaxableAmount, decimal TaxAmount);

    private static IEnumerable<VatGroup> GroupLinesByVat(ICollection<InvoiceItem>? items, bool issuerIsVatPayer)
    {
        return (items ?? Enumerable.Empty<InvoiceItem>())
            .Where(i => !i.IsTextRow)
            .Select(i => (Item: i, Category: UblCodes.VatCategory(i.VatRegime, i.VatRatePercentage, issuerIsVatPayer)))
            .GroupBy(x => (x.Category.Code, x.Category.Percent))
            .Select(g =>
            {
                var taxableAmount = g.Sum(x => x.Item.TotalBeforeVat);
                // BR-CO-17: recomputed from the rounded taxable base, not summed from the
                // individual lines' own VatAmount — keeps the document internally consistent
                // even if a line-level rounding artifact exists in the stored data.
                var rate = g.Key.Percent ?? 0m;
                var taxAmount = Math.Round(taxableAmount * rate / 100m, 2, MidpointRounding.AwayFromZero);
                return new VatGroup(g.First().Category, taxableAmount, taxAmount);
            });
    }

    private static XElement MapTaxTotal(IReadOnlyList<VatGroup> vatGroups, string currencyCode, string language)
    {
        var totalTaxAmount = vatGroups.Sum(g => g.TaxAmount);

        var taxTotal = new XElement(CacNs + "TaxTotal",
            AmountElement(CbcNs + "TaxAmount", totalTaxAmount, currencyCode));

        foreach (var group in vatGroups.OrderBy(g => g.Category.Code).ThenByDescending(g => g.Category.Percent))
        {
            taxTotal.Add(new XElement(CacNs + "TaxSubtotal",
                AmountElement(CbcNs + "TaxableAmount", group.TaxableAmount, currencyCode),
                AmountElement(CbcNs + "TaxAmount", group.TaxAmount, currencyCode),
                BuildTaxCategory("TaxCategory", group.Category, includeExemptionReason: true, language)));
        }

        return taxTotal;
    }

    /// <summary>
    /// Builds a <c>cac:TaxCategory</c> or <c>cac:ClassifiedTaxCategory</c> element (same
    /// UBL complex type, different wrapper name at header vs. line level).
    /// </summary>
    private static XElement BuildTaxCategory(
        string elementName, UblCodes.VatCategoryResult category, bool includeExemptionReason, string? language)
    {
        var elements = new List<object> { new XElement(CbcNs + "ID", category.Code) };
        if (category.Percent.HasValue)
            elements.Add(new XElement(CbcNs + "Percent", FormatDecimal(category.Percent.Value)));

        if (includeExemptionReason && language != null)
        {
            if (category.ExemptionCode != null)
                elements.Add(new XElement(CbcNs + "TaxExemptionReasonCode", category.ExemptionCode));
            var text = UblCodes.ExemptionText(category.ExemptionTextKey, language);
            if (text != null)
                elements.Add(new XElement(CbcNs + "TaxExemptionReason", text));
        }

        elements.Add(new XElement(CacNs + "TaxScheme", new XElement(CbcNs + "ID", VatTaxSchemeId)));
        return new XElement(CacNs + elementName, elements);
    }

    private static XElement MapLegalMonetaryTotal(IReadOnlyList<VatGroup> vatGroups, string currencyCode)
    {
        var lineExtensionAmount = vatGroups.Sum(g => g.TaxableAmount);
        var taxAmount = vatGroups.Sum(g => g.TaxAmount);
        var taxInclusiveAmount = lineExtensionAmount + taxAmount;

        return new XElement(CacNs + "LegalMonetaryTotal",
            AmountElement(CbcNs + "LineExtensionAmount", lineExtensionAmount, currencyCode),
            AmountElement(CbcNs + "TaxExclusiveAmount", lineExtensionAmount, currencyCode),
            AmountElement(CbcNs + "TaxInclusiveAmount", taxInclusiveAmount, currencyCode),
            AmountElement(CbcNs + "PayableAmount", taxInclusiveAmount, currencyCode));
    }

    // --------------------------------------------------------------------------
    // Helpers
    // --------------------------------------------------------------------------

    private static XElement AmountElement(XName name, decimal value, string currencyCode)
        => new(name, new XAttribute("currencyID", currencyCode), FormatDecimal(value));

    private static string FormatDecimal(decimal value)
        => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime? date)
        => date.HasValue
            ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;
}
