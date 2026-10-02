using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Enums;

namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Decides when the first-run setup wizard (/setup) is offered. Pure logic, unit tested.
/// </summary>
public static class SetupWizardPolicy
{
    /// <summary>
    /// Auto-redirect after login only when the issuer (ISSUER_*) or its bank account is still
    /// missing AND the user has not dismissed the wizard. Number sequence, EPO etc. are
    /// intentionally not a reason to interrupt the user — the dashboard checklist covers them.
    /// </summary>
    public static bool ShouldRedirect(ReadinessReportDto report, UserPreferencesDto preferences)
    {
        if (preferences.SetupWizardDismissedAt is not null)
            return false;

        return report.Issues.Any(i => i.Severity == EReadinessSeverity.Blocking && IsCompanyOrBankIssue(i.Code));
    }

    /// <summary>True for the issuer-scoped codes the wizard's Firma and Banka steps resolve.</summary>
    public static bool IsCompanyOrBankIssue(string code) =>
        code.StartsWith("ISSUER_", StringComparison.Ordinal);

    /// <summary>Firma step: issuer exists with name/IČ/address (and DIČ for VAT payers).</summary>
    public static bool CompanyStepDone(ReadinessReportDto report) => !report.Issues.Any(i =>
        i.Code is ReadinessCodes.IssuerMissing or ReadinessCodes.IssuerAddressIncomplete
            or ReadinessCodes.IssuerRegistrationNumberMissing or ReadinessCodes.IssuerTaxNumberMissing);

    /// <summary>Banka step: at least one bank account with a number.</summary>
    public static bool BankStepDone(ReadinessReportDto report) =>
        !report.Issues.Any(i => i.Code == ReadinessCodes.IssuerBankAccountMissing);

    /// <summary>Fakturace step: default number sequences exist for the required document types.</summary>
    public static bool BillingStepDone(ReadinessReportDto report) =>
        !report.Issues.Any(i => i.Code == ReadinessCodes.NumberSequenceMissing);
}
