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
}
