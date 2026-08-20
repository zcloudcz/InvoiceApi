using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Reflection;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for TenantProvisioningService.CreateIssuerInTenantAsync (private static).
///
/// The method runs as Step 6 of tenant provisioning: it copies the master
/// <see cref="Client"/> record (the issuer / "my company") into the freshly created
/// tenant schema. Regression coverage for issue #153 — bank accounts and billing
/// settings were silently dropped, so a provisioned tenant issuer had no account
/// number, no DueDays and no default payment method.
///
/// Just like CreateDefaultNumberSequencesTests we reach the private static method
/// through reflection. That lets us exercise the real production logic against an
/// InMemoryDatabase — no PostgreSQL instance required.
/// </summary>
public class CreateIssuerInTenantTests : IDisposable
{
    private readonly TenantDbContext _tenantContext;

    // Reflection handle for the private static method under test.
    // Resolved once per test class instance instead of once per test.
    private static readonly MethodInfo _createIssuer =
        typeof(TenantProvisioningService)
            .GetMethod(
                "CreateIssuerInTenantAsync",
                BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "CreateIssuerInTenantAsync not found via reflection. " +
            "Check the method name hasn't been renamed.");

    public CreateIssuerInTenantTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _tenantContext = new TenantDbContext(options);
    }

    public void Dispose()
    {
        _tenantContext.Database.EnsureDeleted();
        _tenantContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls TenantProvisioningService.CreateIssuerInTenantAsync via reflection.
    /// The method signature is:
    ///   private static Task CreateIssuerInTenantAsync(
    ///       TenantDbContext tenantContext, Client masterCompany, CancellationToken cancellationToken)
    /// </summary>
    private Task InvokeCreateIssuerAsync(Client masterCompany, CancellationToken ct = default)
        => (Task)_createIssuer.Invoke(null, [_tenantContext, masterCompany, ct])!;

    /// <summary>
    /// Builds a detached master issuer with a full set of related data — this mirrors
    /// what Step 2 of provisioning loads from the master database.
    /// </summary>
    private static Client BuildMasterCompany()
    {
        return new Client
        {
            Id = 42,
            CompanyName = "Test Company s.r.o.",
            TradingName = "Test Co",
            RegistrationNumber = "12345678",
            TaxNumber = "CZ12345678",
            IsVatPayer = true,
            IsIssuer = true,
            IsActive = true,
            Address =
            [
                new Address
                {
                    AddressType = EAddressType.Billing,
                    Street = "Hlavní 1",
                    City = "Praha",
                    PostalCode = "11000",
                    Country = "CZ",
                    IsPrimary = true
                }
            ],
            Contact =
            [
                new Contact
                {
                    ContactType = EContactType.Email,
                    ContactValue = "info@test.cz",
                    IsPrimary = true
                }
            ],
            BankAccount =
            [
                new BankAccount
                {
                    Id = 1,
                    ClientId = 42,
                    Label = "CZK účet",
                    BankName = "Fio banka",
                    AccountNumber = "2400123456/2010",
                    IBAN = "CZ6520100000002400123456",
                    SWIFT = "FIOBCZPP",
                    CurrencyCode = "CZK",
                    IsDefault = true
                },
                new BankAccount
                {
                    Id = 2,
                    ClientId = 42,
                    Label = "EUR účet",
                    BankName = "Fio banka",
                    AccountNumber = "2400999888/2010",
                    CurrencyCode = "EUR",
                    IsDefault = false
                }
            ],
            BillingSettings = new BillingSettings
            {
                Id = 7,
                ClientId = 42,
                DueDateCalculationType = EDueDateCalculationType.DaysFromEndOfMonth,
                DueDays = 21,
                DefaultPaymentMethod = EPaymentMethod.BankTransfer,
                InvoiceNumberPrefix = "EXP-",
                InvoiceNumberSuffix = "-EU",
                CreditNoteNumberPrefix = "CN-",
                CreditNoteNumberSuffix = "-EU",
                Notes = "Vždy poslat kopii účetní",
                // FKs into the MASTER number sequences — must NOT survive the copy,
                // tenant sequences have completely different Ids.
                CustomInvoiceNumberSequenceId = 99,
                CustomCreditNoteNumberSequenceId = 98
            }
        };
    }

    private Task<Client> LoadTenantIssuerAsync()
        => _tenantContext.Client
            .Include(c => c.BankAccount)
            .Include(c => c.BillingSettings)
            .FirstAsync(c => c.IsIssuer);

    // ─── Bank accounts (issue #153) ───────────────────────────────────────────

    [Fact]
    public async Task CreateIssuerInTenant_CopiesAllBankAccounts()
    {
        var master = BuildMasterCompany();

        await InvokeCreateIssuerAsync(master);

        var issuer = await LoadTenantIssuerAsync();
        issuer.BankAccount.Count.ShouldBe(2, "both master bank accounts must land in the tenant");
    }

    [Fact]
    public async Task CreateIssuerInTenant_CopiesBankAccountFieldsIncludingIsDefault()
    {
        var master = BuildMasterCompany();

        await InvokeCreateIssuerAsync(master);

        var issuer = await LoadTenantIssuerAsync();
        var czk = issuer.BankAccount.Single(a => a.CurrencyCode == "CZK");

        czk.Label.ShouldBe("CZK účet");
        czk.BankName.ShouldBe("Fio banka");
        czk.AccountNumber.ShouldBe("2400123456/2010");
        czk.IBAN.ShouldBe("CZ6520100000002400123456");
        czk.SWIFT.ShouldBe("FIOBCZPP");
        czk.IsDefault.ShouldBeTrue("the default flag decides which account is pre-selected on new invoices");

        var eur = issuer.BankAccount.Single(a => a.CurrencyCode == "EUR");
        eur.IsDefault.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateIssuerInTenant_BankAccountsBelongToTheTenantIssuer()
    {
        var master = BuildMasterCompany();

        await InvokeCreateIssuerAsync(master);

        var issuer = await LoadTenantIssuerAsync();

        // The copies must be re-parented to the NEW tenant issuer, never keep the master Id.
        issuer.BankAccount.ShouldAllBe(a => a.ClientId == issuer.Id);
    }

    [Fact]
    public async Task CreateIssuerInTenant_MasterWithoutBankAccounts_CreatesIssuerWithNone()
    {
        var master = BuildMasterCompany();
        master.BankAccount.Clear();

        await InvokeCreateIssuerAsync(master);

        var issuer = await LoadTenantIssuerAsync();
        issuer.BankAccount.ShouldBeEmpty();
    }

    // ─── Billing settings (issue #153) ────────────────────────────────────────

    [Fact]
    public async Task CreateIssuerInTenant_CopiesBillingSettings()
    {
        var master = BuildMasterCompany();

        await InvokeCreateIssuerAsync(master);

        var issuer = await LoadTenantIssuerAsync();
        var billing = issuer.BillingSettings.ShouldNotBeNull("billing settings drive due dates and payment method");

        billing.DueDays.ShouldBe(21);
        billing.DueDateCalculationType.ShouldBe(EDueDateCalculationType.DaysFromEndOfMonth);
        billing.DefaultPaymentMethod.ShouldBe(EPaymentMethod.BankTransfer);
        billing.InvoiceNumberPrefix.ShouldBe("EXP-");
        billing.InvoiceNumberSuffix.ShouldBe("-EU");
        billing.CreditNoteNumberPrefix.ShouldBe("CN-");
        billing.CreditNoteNumberSuffix.ShouldBe("-EU");
        billing.Notes.ShouldBe("Vždy poslat kopii účetní");
        billing.ClientId.ShouldBe(issuer.Id);
    }

    [Fact]
    public async Task CreateIssuerInTenant_DoesNotCopyMasterNumberSequenceReferences()
    {
        var master = BuildMasterCompany();

        await InvokeCreateIssuerAsync(master);

        var issuer = await LoadTenantIssuerAsync();
        var billing = issuer.BillingSettings.ShouldNotBeNull();

        // Number sequences are re-created per tenant (Step 5 + Step 7) with their own Ids.
        // Carrying the master Ids over would point at a foreign or non-existent row.
        billing.CustomInvoiceNumberSequenceId.ShouldBeNull();
        billing.CustomCreditNoteNumberSequenceId.ShouldBeNull();
    }

    /// <summary>
    /// The copy also skips BillingSettings.BankAccountNumber — the [Obsolete] single-account
    /// field that the Client.BankAccount collection replaced. That field is still written and
    /// displayed in a few places, so pin the decision: the tenant issuer must not end up with
    /// a second, stale copy of its account number that nothing keeps in sync with the
    /// collection the invoices actually read.
    /// </summary>
    [Fact]
    public async Task CreateIssuerInTenant_DoesNotCopyObsoleteBankAccountNumber()
    {
        const string legacyAccountNumber = "2400123456/2010";
        var master = BuildMasterCompany();
#pragma warning disable CS0618 // Touching the obsolete field IS the subject of this test.
        master.BillingSettings!.BankAccountNumber = legacyAccountNumber;

        await InvokeCreateIssuerAsync(master);

        var issuer = await LoadTenantIssuerAsync();
        issuer.BillingSettings.ShouldNotBeNull().BankAccountNumber.ShouldBeNull();
#pragma warning restore CS0618

        // The number is not lost — it lives in the collection that superseded the field.
        issuer.BankAccount.ShouldContain(a => a.AccountNumber == legacyAccountNumber);
    }

    [Fact]
    public async Task CreateIssuerInTenant_MasterWithoutBillingSettings_CreatesIssuerWithoutThem()
    {
        var master = BuildMasterCompany();
        master.BillingSettings = null;

        await InvokeCreateIssuerAsync(master);

        var issuer = await LoadTenantIssuerAsync();
        issuer.BillingSettings.ShouldBeNull();
    }

    // ─── Idempotence ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIssuerInTenant_CalledTwice_DoesNotDuplicateIssuerOrItsData()
    {
        var master = BuildMasterCompany();

        await InvokeCreateIssuerAsync(master);
        await InvokeCreateIssuerAsync(master);

        (await _tenantContext.Client.CountAsync(c => c.IsIssuer)).ShouldBe(1);
        (await _tenantContext.BankAccount.CountAsync()).ShouldBe(2, "re-provisioning must not duplicate accounts");
        (await _tenantContext.BillingSettings.CountAsync()).ShouldBe(1);
    }

    // ─── Existing behaviour must not regress ──────────────────────────────────

    [Fact]
    public async Task CreateIssuerInTenant_StillCopiesAddressesAndContacts()
    {
        var master = BuildMasterCompany();

        await InvokeCreateIssuerAsync(master);

        var issuer = await _tenantContext.Client
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .FirstAsync(c => c.IsIssuer);

        issuer.Address.ShouldHaveSingleItem().City.ShouldBe("Praha");
        issuer.Contact.ShouldHaveSingleItem().ContactValue.ShouldBe("info@test.cz");
    }
}
