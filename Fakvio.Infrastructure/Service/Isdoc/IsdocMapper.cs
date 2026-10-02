using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.Ubl;

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

    /// <summary>
    /// Maps a ReceivedInvoice entity to a fully-formed ISDOC 6.0.2 XDocument.
    /// The supplier party is the invoice's supplier; the customer party is the
    /// tenant's own company (<paramref name="customer"/>, Client with IsIssuer = true).
    /// ISDOC itself has no incoming/outgoing distinction — the direction is implied
    /// by the parties, so accounting software imports this as a received invoice.
    /// </summary>
    internal static XDocument Map(ReceivedInvoice invoice, Client? customer)
    {
        var root = new XElement(Ns + "Invoice",
            new XAttribute("version", IsdocVersion));

        root.Add(MapReceivedHeader(invoice, customer));

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
        var uuid = DeterministicUuid($"fakvio-invoice-{invoice.Id}");
        yield return new XElement(Ns + "DocumentType", docType);
        yield return new XElement(Ns + "ID", invoice.DocumentNumber ?? uuid);
        yield return new XElement(Ns + "UUID", uuid);
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
    // Header — received (incoming) invoice
    // --------------------------------------------------------------------------

    private static IEnumerable<object> MapReceivedHeader(ReceivedInvoice invoice, Client? customer)
    {
        var currencyCode = invoice.Currency?.Code ?? "CZK";
        var isCzk = currencyCode.Equals("CZK", StringComparison.OrdinalIgnoreCase);

        // --- Identification block ---
        // Received documents are always regular invoices ("1") — credit notes
        // from suppliers are not tracked as a separate document type.
        // The UUID name is prefixed differently from issued invoices so the two
        // ID sequences can never produce the same UUID.
        var uuid = DeterministicUuid($"fakvio-received-invoice-{invoice.Id}");
        yield return new XElement(Ns + "DocumentType", "1");
        yield return new XElement(Ns + "ID", invoice.DocumentNumber ?? uuid);
        yield return new XElement(Ns + "UUID", uuid);
        yield return new XElement(Ns + "IssuingSystem", "Fakvio");

        // --- Date block ---
        yield return new XElement(Ns + "IssueDate", FormatDate(invoice.IssueDate));
        if (invoice.TaxableSupplyDate.HasValue)
            yield return new XElement(Ns + "TaxPointDate", FormatDate(invoice.TaxableSupplyDate));

        // --- VATApplicable (required) — the document issuer is the supplier ---
        yield return new XElement(Ns + "VATApplicable",
            (invoice.Supplier?.IsVatPayer == true).ToString().ToLowerInvariant());

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

        // --- Parties — supplier issued the document, our company receives it ---
        yield return MapSupplierParty(invoice.Supplier);
        yield return MapCustomerParty(customer);

        // --- Lines (wrapped in InvoiceLines container) ---
        yield return new XElement(Ns + "InvoiceLines", MapReceivedInvoiceLines(invoice));

        // --- Totals ---
        yield return MapReceivedTaxTotal(invoice);
        yield return BuildLegalMonetaryTotal(invoice.TotalBeforeVat, invoice.TotalWithVat);

        // --- Payment ---
        var paymentMeans = MapReceivedPaymentMeans(invoice);
        if (paymentMeans != null) yield return paymentMeans;
    }

    // --------------------------------------------------------------------------
    // Parties
    // --------------------------------------------------------------------------

    private static XElement MapSupplierParty(Client? issuer) =>
        new(Ns + "AccountingSupplierParty",
            new XElement(Ns + "Party", BuildPartyElements(issuer)));

    private static XElement MapCustomerParty(Client? client) =>
        new(Ns + "AccountingCustomerParty",
            new XElement(Ns + "Party", BuildPartyElements(client)));

    private static IEnumerable<object> BuildPartyElements(Client? client)
    {
        // A missing party (e.g. misconfigured tenant without an issuer record)
        // must still emit the XSD-required skeleton — an empty <Party> element
        // is schema-invalid and would be rejected by importing software.
        client ??= new Client();

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
        // ISO 3166-1 alpha-2 code, not the free-text country name Fakvio's address form stores
        // (issue noted in ADR 0002 §1.3) — shared with the UBL export (F1.1) so both formats
        // agree on the same country for the same address.
        var countryCode = UblCodes.CountryToIso2(address?.Country) ?? "CZ";

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
            .OrderBy(i => i.OrderIndex)
            .ToList() ?? new List<InvoiceItem>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lineId = (i + 1).ToString(CultureInfo.InvariantCulture);

            // Text rows carry only a description — emit zero amounts so they
            // appear in the ISDOC document while not affecting totals.
            var quantity = item.IsTextRow ? 0m : item.Quantity;
            var totalBeforeVat = item.IsTextRow ? 0m : item.TotalBeforeVat;
            var totalWithVat = item.IsTextRow ? 0m : item.TotalWithVat;
            var vatAmount = item.IsTextRow ? 0m : item.VatAmount;
            var unitPrice = item.IsTextRow ? 0m : item.UnitPrice;
            var vatRate = item.IsTextRow ? 0m : item.VatRatePercentage;
            // Reverse charge (PDP, §92a-92e ZDPH): supplier bills 0 VAT, so ClassifiedTaxCategory
            // carries a LocalReverseCharge block with the MFCR code (kod predmetu plneni) instead
            // of a plain VAT amount. Text rows never carry a regime.
            var isReverseCharge = !item.IsTextRow && item.VatRegime == EVatRegime.ReverseCharge;
            var reverseChargeCode = isReverseCharge ? item.ReverseChargeCode?.Code : null;
            yield return BuildInvoiceLine(lineId, item.Unit ?? "H87", quantity,
                totalBeforeVat, totalWithVat, vatAmount, unitPrice, vatRate,
                item.Description ?? string.Empty, reverseChargeCode, quantity);
        }
    }

    private static IEnumerable<XElement> MapReceivedInvoiceLines(ReceivedInvoice invoice)
    {
        var items = invoice.Items?
            .OrderBy(i => i.OrderIndex)
            .ToList() ?? new List<ReceivedInvoiceItem>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lineId = (i + 1).ToString(CultureInfo.InvariantCulture);

            // ReceivedInvoiceItem has no reverse-charge regime yet (received-invoice PDP is
            // handled separately) — always pass null/no reverse charge for this line.
            yield return BuildInvoiceLine(lineId, item.Unit, item.Quantity,
                item.TotalBeforeVat, item.TotalWithVat, item.VatAmount,
                item.UnitPrice, item.VatRatePercentage, item.Description,
                reverseChargeCode: null, reverseChargeQuantity: 0);
        }
    }

    /// <summary>
    /// Builds one InvoiceLine element in the official XSD sequence order.
    /// Shared by issued and received invoice mapping — the line structure is identical.
    /// </summary>
    /// <param name="reverseChargeCode">MFCR "kod predmetu plneni" (e.g. "4" = construction/assembly
    /// work) when this line is under local reverse charge (PDP, §92a-92e ZDPH); null otherwise.</param>
    /// <param name="reverseChargeQuantity">Quantity reported in the LocalReverseCharge block —
    /// ISDOC XSD LocalReverseChargeType.LocalReverseChargeQuantity (optional, same unit as the line).</param>
    private static XElement BuildInvoiceLine(
        string lineId, string unit, decimal quantity,
        decimal totalBeforeVat, decimal totalWithVat, decimal vatAmount,
        decimal unitPrice, decimal vatRate, string description,
        string? reverseChargeCode = null, decimal reverseChargeQuantity = 0)
    {
        var unitPriceTaxInclusive = unitPrice * (1 + vatRate / 100m);

        var taxCategory = new XElement(Ns + "ClassifiedTaxCategory",
            new XElement(Ns + "Percent", FormatDecimal(vatRate)),
            new XElement(Ns + "VATCalculationMethod", "0"));

        if (!string.IsNullOrWhiteSpace(reverseChargeCode))
        {
            // XSD sequence inside ClassifiedTaxCategoryType: Percent, VATCalculationMethod,
            // VATApplicable?, LocalReverseCharge? — LocalReverseCharge must come last.
            taxCategory.Add(new XElement(Ns + "LocalReverseCharge",
                new XElement(Ns + "LocalReverseChargeCode", reverseChargeCode),
                new XElement(Ns + "LocalReverseChargeQuantity", FormatDecimal(reverseChargeQuantity))));
        }

        return new XElement(Ns + "InvoiceLine",
            new XElement(Ns + "ID", lineId),
            new XElement(Ns + "InvoicedQuantity",
                new XAttribute("unitCode", unit),
                FormatDecimal(quantity)),
            new XElement(Ns + "LineExtensionAmount",
                FormatDecimal(totalBeforeVat)),
            new XElement(Ns + "LineExtensionAmountTaxInclusive",
                FormatDecimal(totalWithVat)),
            new XElement(Ns + "LineExtensionTaxAmount",
                FormatDecimal(vatAmount)),
            new XElement(Ns + "UnitPrice",
                FormatDecimal(unitPrice)),
            new XElement(Ns + "UnitPriceTaxInclusive",
                FormatDecimal(unitPriceTaxInclusive)),
            taxCategory,
            new XElement(Ns + "Item",
                new XElement(Ns + "Description", description)));
    }

    // --------------------------------------------------------------------------
    // Tax totals
    // --------------------------------------------------------------------------

    private static XElement MapTaxTotal(Invoice invoice)
    {
        // Group by (Rate, IsReverseCharge): a reverse-charge item bills 0 VAT even though it
        // carries a normal-looking rate, so it must never be summed into the same TaxSubTotal as
        // a Standard-regime item at the same rate — that would silently net away real VAT.
        var taxGroups = (invoice.InvoiceItem ?? Enumerable.Empty<InvoiceItem>())
            .Where(i => !i.IsTextRow)
            .GroupBy(i => (i.VatRatePercentage, IsReverseCharge: i.VatRegime == EVatRegime.ReverseCharge))
            .Select(g => (
                Rate: g.Key.VatRatePercentage,
                TaxableAmount: g.Sum(x => x.TotalBeforeVat),
                TaxAmount: g.Sum(x => x.VatAmount),
                TaxInclusiveAmount: g.Sum(x => x.TotalWithVat),
                IsReverseCharge: g.Key.IsReverseCharge));

        return BuildTaxTotal(taxGroups, invoice.TotalVat);
    }

    private static XElement MapReceivedTaxTotal(ReceivedInvoice invoice)
    {
        var taxGroups = (invoice.Items ?? Enumerable.Empty<ReceivedInvoiceItem>())
            .GroupBy(i => i.VatRatePercentage)
            .Select(g => (
                Rate: g.Key,
                TaxableAmount: g.Sum(x => x.TotalBeforeVat),
                TaxAmount: g.Sum(x => x.VatAmount),
                TaxInclusiveAmount: g.Sum(x => x.TotalWithVat),
                IsReverseCharge: false));

        return BuildTaxTotal(taxGroups, invoice.TotalVat);
    }

    /// <summary>
    /// Builds the TaxTotal element from per-VAT-rate groups.
    /// Official XSD TaxTotalType: TaxSubTotal(1..n) -> TaxAmountCurr? -> TaxAmount.
    /// </summary>
    private static XElement BuildTaxTotal(
        IEnumerable<(decimal Rate, decimal TaxableAmount, decimal TaxAmount, decimal TaxInclusiveAmount, bool IsReverseCharge)> taxGroups,
        decimal totalVat)
    {
        var taxTotalEl = new XElement(Ns + "TaxTotal");

        foreach (var group in taxGroups.OrderByDescending(g => g.Rate).ThenBy(g => g.IsReverseCharge))
        {
            // Official TaxSubTotalType has many required elements for advance-payment
            // scenarios. For standard invoices: AlreadyClaimed* = 0, Difference* = actual.
            var taxCategory = new XElement(Ns + "TaxCategory",
                new XElement(Ns + "Percent", FormatDecimal(group.Rate)),
                new XElement(Ns + "TaxScheme", "VAT"));
            // LocalReverseChargeFlag (minOccurs=0) marks this sub-total as PDP (§92a ZDPH) —
            // TaxAmount is 0 here by construction (CalculateItemVat never bills VAT for RC items).
            if (group.IsReverseCharge)
                taxCategory.Add(new XElement(Ns + "LocalReverseChargeFlag", "true"));

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
                taxCategory));
        }

        // TaxAmount at the end
        taxTotalEl.Add(new XElement(Ns + "TaxAmount", FormatDecimal(totalVat)));

        return taxTotalEl;
    }

    // --------------------------------------------------------------------------
    // Legal monetary total
    // --------------------------------------------------------------------------

    private static XElement MapLegalMonetaryTotal(Invoice invoice)
        => BuildLegalMonetaryTotal(invoice.TotalBeforeVat, invoice.TotalWithVat);

    /// <summary>
    /// Builds the LegalMonetaryTotal element. Shared by issued and received invoices.
    /// Official XSD LegalMonetaryTotalType: many required elements for advance-payment.
    /// For standard invoices: AlreadyClaimed* = 0, Difference* = actual, PaidDeposits = 0.
    /// </summary>
    private static XElement BuildLegalMonetaryTotal(decimal totalBeforeVat, decimal totalWithVat)
    {
        return new XElement(Ns + "LegalMonetaryTotal",
            new XElement(Ns + "TaxExclusiveAmount",                  FormatDecimal(totalBeforeVat)),
            new XElement(Ns + "TaxInclusiveAmount",                  FormatDecimal(totalWithVat)),
            new XElement(Ns + "AlreadyClaimedTaxExclusiveAmount",    FormatDecimal(0)),
            new XElement(Ns + "AlreadyClaimedTaxInclusiveAmount",    FormatDecimal(0)),
            new XElement(Ns + "DifferenceTaxExclusiveAmount",        FormatDecimal(totalBeforeVat)),
            new XElement(Ns + "DifferenceTaxInclusiveAmount",        FormatDecimal(totalWithVat)),
            new XElement(Ns + "PaidDepositsAmount",                  FormatDecimal(0)),
            new XElement(Ns + "PayableAmount",                       FormatDecimal(totalWithVat)));
    }

    // --------------------------------------------------------------------------
    // Payment means
    // --------------------------------------------------------------------------

    private static XElement? MapPaymentMeans(Invoice invoice)
        => BuildPaymentMeans(invoice.PaymentMethod, invoice.TotalWithVat, invoice.DueDate,
            invoice.BankAccountNumber, invoice.IBAN, invoice.SWIFT,
            invoice.VariableSymbol, invoice.ConstantSymbol, invoice.SpecificSymbol);

    private static XElement? MapReceivedPaymentMeans(ReceivedInvoice invoice)
        // ReceivedInvoice stores only the variable symbol — constant/specific symbols
        // are not captured for incoming documents.
        => BuildPaymentMeans(invoice.PaymentMethod, invoice.TotalWithVat, invoice.DueDate,
            invoice.BankAccountNumber, invoice.IBAN, invoice.SWIFT,
            invoice.VariableSymbol, constantSymbol: null, specificSymbol: null);

    /// <summary>
    /// Builds the PaymentMeans element. Shared by issued and received invoices.
    /// Official XSD PaymentType: PaidAmount (required) -> PaymentMeansCode -> Details?
    /// </summary>
    private static XElement? BuildPaymentMeans(
        EPaymentMethod? paymentMethod, decimal paidAmount, DateTime? dueDate,
        string? bankAccountNumber, string? iban, string? swift,
        string? variableSymbol, string? constantSymbol, string? specificSymbol)
    {
        if (!paymentMethod.HasValue) return null;

        var payment = new XElement(Ns + "Payment",
            new XElement(Ns + "PaidAmount", FormatDecimal(paidAmount)),
            new XElement(Ns + "PaymentMeansCode",
                MapPaymentMeansCode(paymentMethod.Value)));

        var details = BuildPaymentDetails(paymentMethod, dueDate,
            bankAccountNumber, iban, swift, variableSymbol, constantSymbol, specificSymbol);
        if (details != null) payment.Add(details);

        return new XElement(Ns + "PaymentMeans", payment);
    }

    private static XElement? BuildPaymentDetails(
        EPaymentMethod? paymentMethod, DateTime? dueDate,
        string? bankAccountNumber, string? iban, string? swift,
        string? variableSymbol, string? constantSymbol, string? specificSymbol)
    {
        // Official XSD DetailsType uses xs:choice:
        //   1) Cash: DocumentID + IssueDate
        //   2) Transfer: PaymentDueDate -> BankAccount(ID,BankCode,Name,IBAN,BIC) -> symbols
        // For bank transfer, we need at minimum PaymentDueDate + BankAccount group.
        // All BankAccount elements are required (no minOccurs=0).

        var isBankTransfer = paymentMethod == EPaymentMethod.BankTransfer;

        if (isBankTransfer && dueDate.HasValue)
        {
            // Parse CZ bank account format "number/bankcode"
            var (accountNumber, bankCode) = ParseBankAccount(bankAccountNumber);

            var el = new XElement(Ns + "Details",
                new XElement(Ns + "PaymentDueDate", FormatDate(dueDate)),
                new XElement(Ns + "ID", accountNumber),
                new XElement(Ns + "BankCode", bankCode),
                new XElement(Ns + "Name", string.Empty),
                new XElement(Ns + "IBAN", iban ?? string.Empty),
                new XElement(Ns + "BIC", swift ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(variableSymbol))
                el.Add(new XElement(Ns + "VariableSymbol", variableSymbol));
            if (!string.IsNullOrWhiteSpace(constantSymbol))
                el.Add(new XElement(Ns + "ConstantSymbol", constantSymbol));
            if (!string.IsNullOrWhiteSpace(specificSymbol))
                el.Add(new XElement(Ns + "SpecificSymbol", specificSymbol));

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
    /// Generates a deterministic UUID v5 (SHA-1, RFC 4122 DNS namespace)
    /// from the given name, e.g. "fakvio-invoice-42" or "fakvio-received-invoice-42".
    /// </summary>
    private static string DeterministicUuid(string name)
    {
        byte[] ns = [0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1,
                     0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8];
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var combined = ns.Concat(nameBytes).ToArray();
        var hash = SHA1.HashData(combined);
        var uuid = hash.Take(16).ToArray();
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        return new Guid(uuid).ToString("D");
    }
}
