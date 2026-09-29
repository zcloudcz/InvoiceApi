namespace Fakvio.Contracts.Dto.Readiness;

/// <summary>
/// Machine-readable codes carried by <see cref="ReadinessIssueDto.Code"/>.
///
/// They are string constants (not an enum) on purpose — the API already returns string
/// codes for the same kind of problem (<c>EPO_HEADER_INCOMPLETE</c>, <c>VAT_PAYER_REQUIRED</c>)
/// and the UI uses the code as a localization key. Keeping them in one place means the
/// service, the tests and the UI cannot drift apart through a typo.
/// </summary>
public static class ReadinessCodes
{
    /// <summary>The tenant has no <c>Client</c> flagged as issuer at all (brand new / broken tenant).</summary>
    public const string IssuerMissing = "ISSUER_MISSING";

    /// <summary>The issuer has no address, or its address is missing required parts.</summary>
    public const string IssuerAddressIncomplete = "ISSUER_ADDRESS_INCOMPLETE";

    /// <summary>The issuer has no registration number (IČO).</summary>
    public const string IssuerRegistrationNumberMissing = "ISSUER_REGISTRATION_NUMBER_MISSING";

    /// <summary>The issuer is a VAT payer but has no tax number (DIČ).</summary>
    public const string IssuerTaxNumberMissing = "ISSUER_TAX_NUMBER_MISSING";

    /// <summary>The issuer has no bank account, so invoices would carry no payment destination.</summary>
    public const string IssuerBankAccountMissing = "ISSUER_BANK_ACCOUNT_MISSING";

    /// <summary>
    /// No active default number sequence exists for a document type.
    /// <see cref="ReadinessIssueDto.MissingFields"/> carries the document type name.
    /// </summary>
    public const string NumberSequenceMissing = "NUMBER_SEQUENCE_MISSING";

    /// <summary>
    /// EPO header fields are not configured. Same code the EPO export already returns
    /// (see <c>VatReportController</c>) — reused deliberately so the UI has one mapping.
    /// </summary>
    public const string EpoHeaderIncomplete = "EPO_HEADER_INCOMPLETE";

    // ─── UBL / Peppol eInvoice export (ADR 0002, F1.5) ─────────────────────────────
    // Per-document pre-flight for GET /api/invoice/{id}/ubl — unlike the codes above (tenant
    // readiness, checked once for the whole tenant), these depend on the specific invoice being
    // exported, so they are raised directly by UblExportService instead of ITenantReadinessService.

    /// <summary>The invoice is still a Draft — it has no final document number yet.</summary>
    public const string EinvoiceDraft = "EINVOICE_DRAFT";

    /// <summary>Pro-forma invoices are not tax documents and cannot be exported as an eInvoice ([FAQ] I/34).</summary>
    public const string EinvoiceProformaNotSupported = "EINVOICE_PROFORMA_NOT_SUPPORTED";

    /// <summary>The invoice has no buyer (Client) — cannot build AccountingCustomerParty.</summary>
    public const string EinvoiceBuyerMissing = "EINVOICE_BUYER_MISSING";

    /// <summary>No Peppol endpoint ID could be derived for the seller (PEPPOL-EN16931-R020).</summary>
    public const string EinvoiceSellerEndpointMissing = "EINVOICE_SELLER_ENDPOINT_MISSING";

    /// <summary>No Peppol endpoint ID could be derived for the buyer (PEPPOL-EN16931-R010).</summary>
    public const string EinvoiceBuyerEndpointMissing = "EINVOICE_BUYER_ENDPOINT_MISSING";

    /// <summary>
    /// Seller's or buyer's postal address is missing street, city, postal code or a resolvable
    /// ISO country. <see cref="ReadinessIssueDto.MissingFields"/> carries the empty parts.
    /// </summary>
    public const string EinvoiceAddressIncomplete = "EINVOICE_ADDRESS_INCOMPLETE";

    /// <summary>Bank transfer selected but the invoice has neither an IBAN nor an account number.</summary>
    public const string EinvoicePaymentAccountMissing = "EINVOICE_PAYMENT_ACCOUNT_MISSING";

    /// <summary>
    /// A VAT-payer issuer mixed an out-of-scope (category O) line with a chargeable line —
    /// forbidden by BR-O-11..14 (a document cannot carry two contradictory VAT categories
    /// where one says "no VAT applies at all").
    /// </summary>
    public const string EinvoiceOutOfScopeMixed = "EINVOICE_OUT_OF_SCOPE_MIXED";

    /// <summary>A reverse-charge line exists but seller's or buyer's VAT ID is missing (both are mandatory).</summary>
    public const string EinvoiceReverseChargeVatIdMissing = "EINVOICE_REVERSE_CHARGE_VAT_ID_MISSING";

    /// <summary>
    /// A Slovak seller's invoice is not in EUR — phase 1 has no stored exchange rate for
    /// BT-6/BT-111, so it cannot be exported yet (ADR 0002 §4.1.4).
    /// </summary>
    public const string EinvoiceSkNonEurCurrency = "EINVOICE_SK_NON_EUR_CURRENCY";

    /// <summary>The invoice has no billable lines at all.</summary>
    public const string EinvoiceNoLines = "EINVOICE_NO_LINES";
}
