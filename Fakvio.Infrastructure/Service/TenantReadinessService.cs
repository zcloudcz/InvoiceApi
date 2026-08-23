using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Tenant readiness rules — see <see cref="ITenantReadinessService"/>.
///
/// Data sources:
///  - tenant DB: issuers (<c>Client.IsIssuer</c>) with their addresses and bank accounts,
///    plus number sequences,
///  - master DB: <see cref="CompanySystemSettings"/> for the EPO header fields (same place
///    <c>VatReportService</c> reads them from).
///
/// The rules are plain inline checks on purpose: the project does not use FluentValidation,
/// and a rule-engine abstraction for seven fixed checks would only add indirection.
/// </summary>
public class TenantReadinessService : ITenantReadinessService
{
    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<TenantReadinessService> _logger;

    // Relative UI routes used as "fix it here" links. They must match the @page directives
    // in Fakvio.UI.Shared/Components/Pages — there is no route table to resolve them from.
    private const string MyCompanyRoute      = "/my-company";
    private const string NumberSequenceRoute = "/number-sequences";
    private const string CompanySettingsRoute = "/company-settings";

    /// <summary>
    /// Document types that must have an active default number sequence before the tenant
    /// can issue anything. Proforma / tax receipts fall back to the invoice sequence,
    /// so they are not required here.
    /// </summary>
    private static readonly EDocumentType[] RequiredSequenceTypes =
        [EDocumentType.Invoice, EDocumentType.CreditNote];

    public TenantReadinessService(
        TenantDbContext context,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ILogger<TenantReadinessService> logger)
    {
        _context = context;
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReadinessReportDto> GetReportAsync(long? issuerId = null, CancellationToken ct = default)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException(
                "No CompanyId is available for the current request. " +
                "Ensure the user is authenticated and assigned to a company.");

        // AsNoTracking + Include: the rules read Address and BankAccount collections, and
        // without the explicit Include EF would hand back empty collections (issue #104).
        var issuers = await _context.Client
            .AsNoTracking()
            .Include(c => c.Address)
            .Include(c => c.BankAccount)
            .Where(c => c.IsIssuer && (issuerId == null || c.Id == issuerId.Value))
            .ToListAsync(ct);

        var issues = new List<ReadinessIssueDto>();

        if (issuers.Count == 0)
        {
            // Either the tenant was never set up, or the caller asked for an issuer that
            // does not exist. Both mean "there is no issuer to invoice with".
            issues.Add(new ReadinessIssueDto
            {
                Code          = ReadinessCodes.IssuerMissing,
                Severity      = EReadinessSeverity.Blocking,
                MissingFields = [nameof(Client.IsIssuer)],
                FixRoute      = MyCompanyRoute
            });
        }

        foreach (var issuer in issuers)
            AddIssuerIssues(issuer, issues);

        await AddNumberSequenceIssuesAsync(issues, ct);
        await AddEpoHeaderIssuesAsync(companyId, issues, ct);

        _logger.LogInformation(
            "Readiness check for company {CompanyId} (issuerId={IssuerId}): {IssueCount} issue(s)",
            companyId, issuerId, issues.Count);

        return new ReadinessReportDto { Issues = issues };
    }

    /// <inheritdoc />
    public async Task EnsureReadyAsync(long? issuerId = null, CancellationToken ct = default)
    {
        var report = await GetReportAsync(issuerId, ct);

        var blocking = report.Issues
            .Where(i => i.Severity == EReadinessSeverity.Blocking)
            .ToList();

        if (blocking.Count == 0)
            return;

        _logger.LogWarning(
            "Operation refused — tenant not ready: {Codes}",
            string.Join(", ", blocking.Select(i => i.Code)));

        throw new TenantNotReadyException(blocking);
    }

    // =========================================================================
    // Rules — issuer scoped
    // =========================================================================

    /// <summary>
    /// Applies the four issuer-scoped rules: address, IČO, DIČ (VAT payers only), bank account.
    /// </summary>
    private static void AddIssuerIssues(Client issuer, List<ReadinessIssueDto> issues)
    {
        var missingAddressFields = GetMissingAddressFields(issuer);
        if (missingAddressFields.Count > 0)
            issues.Add(IssuerIssue(issuer, ReadinessCodes.IssuerAddressIncomplete, missingAddressFields, MyCompanyRoute));

        if (string.IsNullOrWhiteSpace(issuer.RegistrationNumber))
            issues.Add(IssuerIssue(issuer, ReadinessCodes.IssuerRegistrationNumberMissing,
                [nameof(Client.RegistrationNumber)], MyCompanyRoute));

        // Only VAT payers need a DIČ. A non-payer without one is perfectly fine.
        if (issuer.IsVatPayer && string.IsNullOrWhiteSpace(issuer.TaxNumber))
            issues.Add(IssuerIssue(issuer, ReadinessCodes.IssuerTaxNumberMissing,
                [nameof(Client.TaxNumber)], MyCompanyRoute));

        // At least one usable account — an account row without a number is as good as none.
        if (!issuer.BankAccount.Any(b => !string.IsNullOrWhiteSpace(b.AccountNumber)))
            issues.Add(IssuerIssue(issuer, ReadinessCodes.IssuerBankAccountMissing,
                [nameof(Client.BankAccount)], MyCompanyRoute));
    }

    /// <summary>
    /// Returns the empty parts of the issuer's address, or the single entry "Address"
    /// when there is no address at all. Empty list = the address is complete.
    ///
    /// A client may hold several addresses (billing, shipping, …); the primary one is the
    /// one printed on invoices, so that is the one checked — falling back to the first
    /// address when none is flagged as primary.
    /// </summary>
    private static List<string> GetMissingAddressFields(Client issuer)
    {
        var address = issuer.Address.FirstOrDefault(a => a.IsPrimary) ?? issuer.Address.FirstOrDefault();
        if (address is null)
            return [nameof(Client.Address)];

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(address.Street))     missing.Add(nameof(Address.Street));
        if (string.IsNullOrWhiteSpace(address.City))       missing.Add(nameof(Address.City));
        if (string.IsNullOrWhiteSpace(address.PostalCode)) missing.Add(nameof(Address.PostalCode));
        if (string.IsNullOrWhiteSpace(address.Country))    missing.Add(nameof(Address.Country));
        return missing;
    }

    // =========================================================================
    // Rules — tenant scoped
    // =========================================================================

    /// <summary>
    /// Every required document type needs one active default number sequence,
    /// otherwise numbering fails at the moment the document is completed.
    /// </summary>
    private async Task AddNumberSequenceIssuesAsync(List<ReadinessIssueDto> issues, CancellationToken ct)
    {
        var configuredTypes = await _context.NumberSequence
            .AsNoTracking()
            .Where(s => s.IsDefault && s.IsActive)
            .Select(s => s.DocumentType)
            .Distinct()
            .ToListAsync(ct);

        foreach (var documentType in RequiredSequenceTypes)
        {
            if (configuredTypes.Contains(documentType))
                continue;

            issues.Add(new ReadinessIssueDto
            {
                Code          = ReadinessCodes.NumberSequenceMissing,
                Severity      = EReadinessSeverity.Blocking,
                MissingFields = [documentType.ToString()],
                FixRoute      = NumberSequenceRoute
            });
        }
    }

    /// <summary>
    /// EPO header fields live in the master DB and are only needed for VAT filings,
    /// so a missing value is a warning — it must not block ordinary invoicing.
    /// Reuses the code the EPO export already returns so the UI has a single mapping.
    /// </summary>
    private async Task AddEpoHeaderIssuesAsync(long companyId, List<ReadinessIssueDto> issues, CancellationToken ct)
    {
        var settings = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);

        // No settings row yet = both fields are unset.
        var missing = new List<string>();
        if (settings?.EpoTaxOfficeCode is null)       missing.Add(nameof(CompanySystemSettings.EpoTaxOfficeCode));
        if (settings?.EpoTaxOfficeBranchCode is null) missing.Add(nameof(CompanySystemSettings.EpoTaxOfficeBranchCode));

        if (missing.Count == 0)
            return;

        issues.Add(new ReadinessIssueDto
        {
            Code          = ReadinessCodes.EpoHeaderIncomplete,
            Severity      = EReadinessSeverity.Warning,
            MissingFields = missing,
            FixRoute      = CompanySettingsRoute
        });
    }

    /// <summary>
    /// Builds a blocking issue tagged with the issuer it belongs to, so the UI can tell
    /// several issuers apart in one report.
    /// </summary>
    private static ReadinessIssueDto IssuerIssue(Client issuer, string code, List<string> missingFields, string fixRoute)
        => new()
        {
            Code          = code,
            Severity      = EReadinessSeverity.Blocking,
            MissingFields = missingFields,
            FixRoute      = fixRoute,
            IssuerId      = issuer.Id,
            IssuerName    = issuer.CompanyName
        };
}
