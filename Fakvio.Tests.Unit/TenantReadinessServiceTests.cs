using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="TenantReadinessService"/> — one positive and one negative case
/// per readiness rule, plus the issuer filter and the <see cref="TenantNotReadyException"/> guard.
///
/// Seeding discipline: every seed runs in its OWN DbContext which is disposed before the
/// service context is created. Seeding through the service's context would leave the entities
/// in EF's change tracker, and navigation collections would appear populated even if the
/// service forgot its <c>Include()</c> — a false green (issues #104/#106).
/// </summary>
public class TenantReadinessServiceTests : IDisposable
{
    private const long CompanyId = 7L;
    private const long IssuerId = 100L;
    private const long SecondIssuerId = 200L;

    // Both in-memory databases are unique per test instance (xUnit creates one per test).
    private readonly string _tenantDbName = $"readiness-tenant-{Guid.NewGuid()}";
    private readonly string _masterDbName = $"readiness-master-{Guid.NewGuid()}";

    // Contexts handed to the service — kept only so Dispose can clean them up.
    private readonly List<DbContext> _serviceContexts = new();

    public void Dispose()
    {
        foreach (var context in _serviceContexts)
        {
            context.Database.EnsureDeleted();
            context.Dispose();
        }
    }

    // =========================================================================
    // Rule: issuer exists
    // =========================================================================

    [Fact]
    public async Task GetReportAsync_FullyConfiguredTenant_ReportsReady()
    {
        SeedIssuer(NewCompleteIssuer());
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var report = await CreateService().GetReportAsync();

        report.Issues.ShouldBeEmpty();
        report.IsReady.ShouldBeTrue();
    }

    [Fact]
    public async Task GetReportAsync_NoIssuer_ReportsIssuerMissing()
    {
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var report = await CreateService().GetReportAsync();

        var issue = report.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(ReadinessCodes.IssuerMissing);
        issue.Severity.ShouldBe(EReadinessSeverity.Blocking);
        issue.FixRoute.ShouldBe("/my-company");
        report.IsReady.ShouldBeFalse();
    }

    // =========================================================================
    // Rule: issuer address
    // =========================================================================

    [Fact]
    public async Task GetReportAsync_IssuerWithoutAddress_ReportsAddressIncomplete()
    {
        var issuer = NewCompleteIssuer();
        issuer.Address.Clear();
        SeedReadyTenantWith(issuer);

        var issue = await SingleIssueAsync(ReadinessCodes.IssuerAddressIncomplete);

        issue.MissingFields.ShouldBe([nameof(Client.Address)]);
        issue.Severity.ShouldBe(EReadinessSeverity.Blocking);
        issue.IssuerId.ShouldBe(IssuerId);
        issue.IssuerName.ShouldBe("Fakvio s.r.o.");
    }

    [Fact]
    public async Task GetReportAsync_AddressWithEmptyParts_ListsOnlyTheEmptyOnes()
    {
        var issuer = NewCompleteIssuer();
        var address = issuer.Address.First();
        address.City = "";
        address.PostalCode = "   ";   // whitespace counts as empty
        SeedReadyTenantWith(issuer);

        var issue = await SingleIssueAsync(ReadinessCodes.IssuerAddressIncomplete);

        issue.MissingFields.ShouldBe([nameof(Address.City), nameof(Address.PostalCode)]);
    }

    [Fact]
    public async Task GetReportAsync_IncompletePrimaryAddress_IsCheckedInsteadOfCompleteSecondary()
    {
        // The primary address is the one printed on invoices — a complete billing address
        // must not mask an incomplete primary one.
        var issuer = NewCompleteIssuer();
        var primary = issuer.Address.First();
        primary.Street = "";

        // The complete billing address is inserted BEFORE the broken primary one, so a naive
        // "take the first address" implementation would pick it and miss the real problem.
        issuer.Address.Clear();
        issuer.Address.Add(new Address
        {
            Id = 90, AddressType = EAddressType.Billing, IsPrimary = false,
            Street = "Vedlejší 2", City = "Brno", PostalCode = "60200", Country = "CZ"
        });
        issuer.Address.Add(primary);
        SeedReadyTenantWith(issuer);

        var issue = await SingleIssueAsync(ReadinessCodes.IssuerAddressIncomplete);

        issue.MissingFields.ShouldBe([nameof(Address.Street)]);
    }

    // =========================================================================
    // Rule: registration number (IČO)
    // =========================================================================

    [Fact]
    public async Task GetReportAsync_IssuerWithoutRegistrationNumber_ReportsIt()
    {
        var issuer = NewCompleteIssuer();
        issuer.RegistrationNumber = "";
        SeedReadyTenantWith(issuer);

        var issue = await SingleIssueAsync(ReadinessCodes.IssuerRegistrationNumberMissing);

        issue.MissingFields.ShouldBe([nameof(Client.RegistrationNumber)]);
        issue.Severity.ShouldBe(EReadinessSeverity.Blocking);
    }

    // =========================================================================
    // Rule: tax number (DIČ) — only for VAT payers
    // =========================================================================

    [Fact]
    public async Task GetReportAsync_VatPayerWithoutTaxNumber_ReportsIt()
    {
        var issuer = NewCompleteIssuer();
        issuer.IsVatPayer = true;
        issuer.TaxNumber = null;
        SeedReadyTenantWith(issuer);

        var issue = await SingleIssueAsync(ReadinessCodes.IssuerTaxNumberMissing);

        issue.MissingFields.ShouldBe([nameof(Client.TaxNumber)]);
    }

    [Fact]
    public async Task GetReportAsync_NonVatPayerWithoutTaxNumber_IsFine()
    {
        var issuer = NewCompleteIssuer();
        issuer.IsVatPayer = false;
        issuer.TaxNumber = null;
        SeedReadyTenantWith(issuer);

        var report = await CreateService().GetReportAsync();

        report.Issues.ShouldNotContain(i => i.Code == ReadinessCodes.IssuerTaxNumberMissing);
        report.IsReady.ShouldBeTrue();
    }

    // =========================================================================
    // Rule: bank account
    // =========================================================================

    [Fact]
    public async Task GetReportAsync_IssuerWithoutBankAccount_ReportsIt()
    {
        var issuer = NewCompleteIssuer();
        issuer.BankAccount.Clear();
        SeedReadyTenantWith(issuer);

        var issue = await SingleIssueAsync(ReadinessCodes.IssuerBankAccountMissing);

        issue.MissingFields.ShouldBe([nameof(Client.BankAccount)]);
        issue.FixRoute.ShouldBe("/my-company");
    }

    [Fact]
    public async Task GetReportAsync_BankAccountWithoutNumber_CountsAsMissing()
    {
        var issuer = NewCompleteIssuer();
        issuer.BankAccount.First().AccountNumber = "";
        SeedReadyTenantWith(issuer);

        await SingleIssueAsync(ReadinessCodes.IssuerBankAccountMissing);
    }

    // =========================================================================
    // Rule: number sequences
    // =========================================================================

    [Fact]
    public async Task GetReportAsync_MissingCreditNoteSequence_ReportsOnlyThatType()
    {
        SeedIssuer(NewCompleteIssuer());
        SeedSequences(EDocumentType.Invoice);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var issue = await SingleIssueAsync(ReadinessCodes.NumberSequenceMissing);

        issue.MissingFields.ShouldBe([nameof(EDocumentType.CreditNote)]);
        issue.Severity.ShouldBe(EReadinessSeverity.Blocking);
        issue.FixRoute.ShouldBe("/number-sequences");
        issue.IssuerId.ShouldBeNull();   // tenant-wide, not tied to an issuer
    }

    [Fact]
    public async Task GetReportAsync_InactiveOrNonDefaultSequences_DoNotCount()
    {
        SeedIssuer(NewCompleteIssuer());
        SeedSequences(
            (EDocumentType.Invoice,    IsDefault: true,  IsActive: false),
            (EDocumentType.CreditNote, IsDefault: false, IsActive: true));
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var report = await CreateService().GetReportAsync();

        var missingTypes = report.Issues
            .Where(i => i.Code == ReadinessCodes.NumberSequenceMissing)
            .SelectMany(i => i.MissingFields)
            .ToList();
        missingTypes.ShouldBe([nameof(EDocumentType.Invoice), nameof(EDocumentType.CreditNote)]);
    }

    // =========================================================================
    // Rule: EPO header (warning only)
    // =========================================================================

    [Fact]
    public async Task GetReportAsync_MissingEpoHeader_IsWarningAndDoesNotBlock()
    {
        SeedIssuer(NewCompleteIssuer());
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: null, branchCode: null);

        var issue = await SingleIssueAsync(ReadinessCodes.EpoHeaderIncomplete);

        issue.Severity.ShouldBe(EReadinessSeverity.Warning);
        issue.MissingFields.ShouldBe([
            nameof(CompanySystemSettings.EpoTaxOfficeCode),
            nameof(CompanySystemSettings.EpoTaxOfficeBranchCode)
        ]);
        issue.FixRoute.ShouldBe("/company-settings");

        var report = await CreateService().GetReportAsync();
        report.IsReady.ShouldBeTrue();   // warnings never make the tenant "not ready"
    }

    [Fact]
    public async Task GetReportAsync_PartiallyFilledEpoHeader_ListsOnlyTheMissingField()
    {
        SeedIssuer(NewCompleteIssuer());
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: null);

        var issue = await SingleIssueAsync(ReadinessCodes.EpoHeaderIncomplete);

        issue.MissingFields.ShouldBe([nameof(CompanySystemSettings.EpoTaxOfficeBranchCode)]);
    }

    [Fact]
    public async Task GetReportAsync_NoCompanySystemSettingsRow_ReportsBothEpoFields()
    {
        SeedIssuer(NewCompleteIssuer());
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        // deliberately no SeedEpoSettings call — the master DB has no row at all

        var issue = await SingleIssueAsync(ReadinessCodes.EpoHeaderIncomplete);

        issue.MissingFields.Count.ShouldBe(2);
    }

    // =========================================================================
    // Issuer filter (multi-issuer tenants)
    // =========================================================================

    [Fact]
    public async Task GetReportAsync_WithoutIssuerId_ChecksEveryIssuer()
    {
        var broken = NewCompleteIssuer(SecondIssuerId, "Druhá firma s.r.o.");
        broken.RegistrationNumber = "";
        SeedIssuer(NewCompleteIssuer());
        SeedIssuer(broken);
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var report = await CreateService().GetReportAsync();

        var issue = report.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(ReadinessCodes.IssuerRegistrationNumberMissing);
        issue.IssuerId.ShouldBe(SecondIssuerId);
        issue.IssuerName.ShouldBe("Druhá firma s.r.o.");
    }

    [Fact]
    public async Task GetReportAsync_WithIssuerId_IgnoresTheOtherIssuers()
    {
        var broken = NewCompleteIssuer(SecondIssuerId, "Druhá firma s.r.o.");
        broken.RegistrationNumber = "";
        SeedIssuer(NewCompleteIssuer());
        SeedIssuer(broken);
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var report = await CreateService().GetReportAsync(IssuerId);

        report.Issues.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetReportAsync_UnknownIssuerId_ReportsIssuerMissing()
    {
        SeedIssuer(NewCompleteIssuer());
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var report = await CreateService().GetReportAsync(issuerId: 999);

        report.Issues.ShouldHaveSingleItem().Code.ShouldBe(ReadinessCodes.IssuerMissing);
    }

    [Fact]
    public async Task GetReportAsync_ClientThatIsNotAnIssuer_IsIgnored()
    {
        var customer = NewCompleteIssuer(SecondIssuerId, "Zákazník s.r.o.");
        customer.IsIssuer = false;
        customer.RegistrationNumber = "";   // would be a blocking issue if checked
        SeedIssuer(NewCompleteIssuer());
        SeedIssuer(customer);
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var report = await CreateService().GetReportAsync();

        report.Issues.ShouldBeEmpty();
    }

    // =========================================================================
    // Inactive issuers — characterization of the CURRENT behaviour
    // =========================================================================

    /// <summary>
    /// The issuer query deliberately does NOT filter on <c>IsActive</c>: a deactivated issuer
    /// is still evaluated. This is what the readiness consumers rely on — they hand in an
    /// issuer id and expect an answer about THAT issuer, not "no issuer at all".
    ///
    /// The rest of the repo is inconsistent here (VatReportService and DashboardController do
    /// filter), so this is a characterization test: if anyone ever adds <c>&amp;&amp; c.IsActive</c>
    /// to the query, the change stops being silent and fails here instead.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_InactiveIssuer_IsStillChecked()
    {
        var inactive = NewCompleteIssuer(SecondIssuerId, "Pozastavená firma s.r.o.");
        inactive.IsActive = false;
        inactive.RegistrationNumber = "";
        SeedIssuer(NewCompleteIssuer());
        SeedIssuer(inactive);
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var report = await CreateService().GetReportAsync();

        var issue = report.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(ReadinessCodes.IssuerRegistrationNumberMissing);
        issue.IssuerId.ShouldBe(SecondIssuerId);
    }

    /// <summary>
    /// Same rule seen through the explicit issuer filter — the path #206 uses. Asking about an
    /// inactive issuer must report that issuer's real problems, not degrade to ISSUER_MISSING.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_WithIdOfAnInactiveIssuer_ReportsItsOwnIssues()
    {
        var inactive = NewCompleteIssuer(SecondIssuerId, "Pozastavená firma s.r.o.");
        inactive.IsActive = false;
        inactive.BankAccount.Clear();
        SeedIssuer(NewCompleteIssuer());
        SeedIssuer(inactive);
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);

        var report = await CreateService().GetReportAsync(SecondIssuerId);

        var issue = report.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(ReadinessCodes.IssuerBankAccountMissing);
        issue.IssuerId.ShouldBe(SecondIssuerId);
    }

    // =========================================================================
    // Tenant isolation of the master-database read
    // =========================================================================

    /// <summary>
    /// The EPO settings live in the SHARED master database, one row per company — the one
    /// query in this service that can leak across tenants. It must match on
    /// <c>CompanyId</c> and never fall back to "whatever row is there".
    ///
    /// Shape of the test: another company has COMPLETE settings, our company has no row at
    /// all. So the answer differs whichever row a broken query happens to return first —
    /// the assertion does not depend on the in-memory provider's row order.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_EpoSettingsOfAnotherCompany_AreNeverRead()
    {
        const long foreignCompanyId = 99L;
        SeedIssuer(NewCompleteIssuer());
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017, companyId: foreignCompanyId, id: 2);

        var issue = await SingleIssueAsync(ReadinessCodes.EpoHeaderIncomplete);

        issue.Severity.ShouldBe(EReadinessSeverity.Warning);
        issue.MissingFields.ShouldBe([
            nameof(CompanySystemSettings.EpoTaxOfficeCode),
            nameof(CompanySystemSettings.EpoTaxOfficeBranchCode)
        ]);
    }

    // =========================================================================
    // EnsureReadyAsync + TenantNotReadyException
    // =========================================================================

    [Fact]
    public async Task EnsureReadyAsync_BlockingIssues_ThrowsWithCodeAndMissingFields()
    {
        var issuer = NewCompleteIssuer();
        issuer.RegistrationNumber = "";
        issuer.BankAccount.Clear();
        SeedReadyTenantWith(issuer);

        var ex = await Should.ThrowAsync<TenantNotReadyException>(
            () => CreateService().EnsureReadyAsync());

        ex.Code.ShouldBe("TENANT_NOT_READY");
        ex.MissingFields.ShouldBe([nameof(Client.RegistrationNumber), nameof(Client.BankAccount)]);
        ex.Issues.Count.ShouldBe(2);
        ex.Issues.ShouldAllBe(i => i.Severity == EReadinessSeverity.Blocking);
    }

    [Fact]
    public async Task EnsureReadyAsync_OnlyWarnings_DoesNotThrow()
    {
        SeedIssuer(NewCompleteIssuer());
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: null, branchCode: null);

        await Should.NotThrowAsync(() => CreateService().EnsureReadyAsync());
    }

    [Fact]
    public void TenantNotReadyException_WithoutIssues_FailsFast()
    {
        Should.Throw<ArgumentException>(() => new TenantNotReadyException([]));
    }

    // =========================================================================
    // Tenant context guard
    // =========================================================================

    [Fact]
    public async Task GetReportAsync_WithoutCompanyContext_Throws()
    {
        var service = CreateService(companyId: null);

        await Should.ThrowAsync<InvalidOperationException>(() => service.GetReportAsync());
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Builds an issuer that satisfies every rule — tests break exactly one thing on it.
    /// </summary>
    private static Client NewCompleteIssuer(long id = IssuerId, string name = "Fakvio s.r.o.") => new()
    {
        Id                 = id,
        CompanyName        = name,
        RegistrationNumber = "12345678",
        TaxNumber          = "CZ12345678",
        IsVatPayer         = true,
        IsIssuer           = true,
        IsActive           = true,
        Address =
        {
            new Address
            {
                Id = id + 1, AddressType = EAddressType.Primary, IsPrimary = true,
                Street = "Hlavní 1", City = "Praha", PostalCode = "11000", Country = "CZ"
            }
        },
        BankAccount =
        {
            new BankAccount { Id = id + 2, AccountNumber = "1234567890/0100" }
        }
    };

    /// <summary>Seeds the given issuer plus everything else a ready tenant needs.</summary>
    private void SeedReadyTenantWith(Client issuer)
    {
        SeedIssuer(issuer);
        SeedSequences(EDocumentType.Invoice, EDocumentType.CreditNote);
        SeedEpoSettings(taxOfficeCode: 451, branchCode: 2017);
    }

    private void SeedIssuer(Client issuer) => SeedTenant(ctx => ctx.Client.Add(issuer));

    private void SeedSequences(params EDocumentType[] documentTypes)
        => SeedSequences(documentTypes.Select(t => (t, IsDefault: true, IsActive: true)).ToArray());

    /// <summary>
    /// Replaces all number sequences with exactly the given ones. Replacing (not adding) keeps
    /// the test independent of the default sequences seeded by <c>TenantDbContext.SeedData</c>.
    /// </summary>
    private void SeedSequences(params (EDocumentType Type, bool IsDefault, bool IsActive)[] sequences)
        => SeedTenant(ctx =>
        {
            ctx.NumberSequence.RemoveRange(ctx.NumberSequence);
            ctx.SaveChanges();

            var id = 500L;
            foreach (var (type, isDefault, isActive) in sequences)
            {
                ctx.NumberSequence.Add(new NumberSequence
                {
                    Id = id++,
                    Name = $"{type} sequence",
                    DocumentType = type,
                    IsDefault = isDefault,
                    IsActive = isActive,
                    NumberSequenceFormatId = 1
                });
            }
        });

    /// <summary>
    /// Seeds one master-DB settings row. <paramref name="companyId"/> defaults to the tenant
    /// under test; tenant-isolation tests pass a foreign company (and a distinct
    /// <paramref name="id"/>, because the primary key is shared across companies).
    /// </summary>
    private void SeedEpoSettings(int? taxOfficeCode, int? branchCode,
        long companyId = CompanyId, long id = 1)
    {
        using var context = NewContext<MasterDbContext>(_masterDbName);
        context.CompanySystemSettings.Add(new CompanySystemSettings
        {
            Id = id,
            CompanyId = companyId,
            SchemaName = "tenant_test",
            EpoTaxOfficeCode = taxOfficeCode,
            EpoTaxOfficeBranchCode = branchCode
        });
        context.SaveChanges();
    }

    /// <summary>
    /// Runs a seed action in a throw-away context that is disposed before the service
    /// context is created — see the class remarks for why that matters.
    /// </summary>
    private void SeedTenant(Action<TenantDbContext> seed)
    {
        using var context = NewContext<TenantDbContext>(_tenantDbName);
        seed(context);
        context.SaveChanges();
    }

    private static TContext NewContext<TContext>(string databaseName) where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    private TenantReadinessService CreateService(long? companyId = CompanyId)
    {
        var tenantContext = NewContext<TenantDbContext>(_tenantDbName);
        var masterContext = NewContext<MasterDbContext>(_masterDbName);
        _serviceContexts.Add(tenantContext);
        _serviceContexts.Add(masterContext);

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(companyId);

        return new TenantReadinessService(
            tenantContext, masterContext, tenantResolver,
            Substitute.For<ILogger<TenantReadinessService>>());
    }

    /// <summary>Runs the report and asserts that <paramref name="code"/> is its only issue.</summary>
    private async Task<ReadinessIssueDto> SingleIssueAsync(string code)
    {
        var report = await CreateService().GetReportAsync();
        var issue = report.Issues.ShouldHaveSingleItem();
        issue.Code.ShouldBe(code);
        return issue;
    }
}
