using System.Reflection;
using AresService;
using AresService.Model;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Regression tests for what self-registration takes over from the ARES lookup.
///
/// Issue #157 — the registered office address was fetched and then thrown away.
/// Issue #208 — the VAT payer flag was never derived, so every self-registered
/// company started as a non-payer even when ARES knew its DIČ.
///
/// The chain under test is:
///   1. <see cref="AuthService.RegisterAsync"/> — must persist the address and the
///      VAT payer flag on the master <see cref="Client"/> (the issuer record of the
///      new company).
///   2. TenantProvisioningService.CreateIssuerInTenantAsync — must copy that address
///      into the freshly provisioned tenant schema, so PDF invoices have a complete
///      issuer block.
///
/// Both steps run against InMemoryDatabase — no PostgreSQL and no live ARES call.
/// The provisioning step is a private static method, so it is invoked via reflection,
/// following the pattern already established in <c>CreateDefaultNumberSequencesTests</c>.
/// </summary>
public class RegistrationAresAddressTests : IDisposable
{
    private readonly MasterDbContext _masterContext;
    private readonly IAresService _aresService;
    private readonly AuthService _service;

    // Reflection handle for the private static provisioning step under test.
    // Resolved once per test class instance, not per test.
    private static readonly MethodInfo _createIssuerInTenant =
        typeof(TenantProvisioningService)
            .GetMethod(
                "CreateIssuerInTenantAsync",
                BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "CreateIssuerInTenantAsync not found via reflection. " +
            "Check the method name hasn't been renamed.");

    public RegistrationAresAddressTests()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _masterContext = new MasterDbContext(options);

        // JWT settings are required by the AuthService constructor even though
        // registration itself does not issue a token.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:Secret"] = "ThisIsAVeryLongSecretKeyForTestingPurposesOnly1234567890",
                ["JwtSettings:Issuer"] = "FakvioTest",
                ["JwtSettings:Audience"] = "FakvioTestClient",
                ["JwtSettings:ExpirationHours"] = "24"
            })
            .Build();

        _aresService = Substitute.For<IAresService>();

        _service = new AuthService(
            _masterContext,
            configuration,
            Substitute.For<ITenantProvisioningService>(),
            Substitute.For<IEmailService>(),
            _aresService,
            Substitute.For<ILogger<AuthService>>(),
            Substitute.For<ITwoFactorService>());
    }

    public void Dispose()
    {
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures the ARES mock to answer for the given IČO with a successful
    /// lookup that contains a registered office address.
    /// </summary>
    private void ArrangeAresWithAddress(string registrationNumber)
        => ArrangeAresWithAddress(registrationNumber, taxNumber: "CZ" + registrationNumber);

    /// <summary>
    /// Same as above, but lets a test choose whether the registry knows a DIČ.
    /// <c>null</c> models a company that is not registered for VAT.
    /// </summary>
    private void ArrangeAresWithAddress(string registrationNumber, string? taxNumber)
    {
        _aresService.GetCompanyInfoAsync(registrationNumber, Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                RegistrationNumber = registrationNumber,
                CompanyName = "ARES Corp s.r.o.",
                TaxNumber = taxNumber,
                // AresServiceImpl.ParseAresResponse derives the flag from the presence of the
                // "dic" element. The double mirrors that rule so it cannot drift into asserting
                // a combination the real registry never produces.
                IsVatPayer = !string.IsNullOrEmpty(taxNumber),
                IsSuccessful = true,
                FetchedAt = DateTime.UtcNow,
                Address = new AresAddress
                {
                    Street = "Hlavní 123",
                    City = "Praha",
                    PostalCode = "120 00",
                    Country = "Česká republika"
                }
            });
    }

    /// <summary>
    /// Builds a minimal valid registration request. Address fields stay empty
    /// unless a test fills them in — that is the "user did not correct ARES" case.
    /// </summary>
    private static RegisterRequest BuildRequest(string registrationNumber) => new()
    {
        Email = $"user{registrationNumber}@example.com",
        FirstName = "Jan",
        LastName = "Novák",
        CompanyName = "User Entered Name",
        RegistrationNumber = registrationNumber
    };

    /// <summary>
    /// Loads the freshly registered issuer together with its addresses.
    /// </summary>
    private async Task<Client> LoadIssuerAsync(string registrationNumber) =>
        await _masterContext.Client
            .Include(c => c.Address)
            .FirstAsync(c => c.RegistrationNumber == registrationNumber);

    /// <summary>
    /// Calls TenantProvisioningService.CreateIssuerInTenantAsync via reflection.
    /// Signature:
    ///   private static Task CreateIssuerInTenantAsync(
    ///       TenantDbContext tenantContext, Client masterCompany, CancellationToken ct)
    /// </summary>
    private static Task InvokeCreateIssuerInTenantAsync(TenantDbContext tenantContext, Client masterCompany) =>
        (Task)_createIssuerInTenant.Invoke(null, [tenantContext, masterCompany, CancellationToken.None])!;

    // ─── Registration stores the ARES address ────────────────────────────────

    /// <summary>
    /// The reproducing test for #157: ARES returns an address, so the master issuer
    /// must end up with a primary address — previously it was silently discarded.
    /// </summary>
    [Fact]
    public async Task Register_AresReturnsAddress_StoresPrimaryAddressOnIssuer()
    {
        const string ico = "12345678";
        ArrangeAresWithAddress(ico);

        await _service.RegisterAsync(BuildRequest(ico), "https://app.example.com");

        var issuer = await LoadIssuerAsync(ico);
        issuer.Address.Count.ShouldBe(1, "The registered office address from ARES must be persisted.");

        var address = issuer.Address.First();
        address.Street.ShouldBe("Hlavní 123");
        address.City.ShouldBe("Praha");
        address.PostalCode.ShouldBe("120 00");
        address.Country.ShouldBe("Česká republika");
        address.AddressType.ShouldBe(EAddressType.Primary);
        address.IsPrimary.ShouldBeTrue();
    }

    /// <summary>
    /// The user can correct the pre-filled address in the registration form.
    /// Values supplied in the request therefore win over the ARES lookup.
    /// </summary>
    [Fact]
    public async Task Register_RequestCarriesAddress_UserValuesWinOverAres()
    {
        const string ico = "22345678";
        ArrangeAresWithAddress(ico);

        var request = BuildRequest(ico);
        request.Street = "Opravená 9";
        request.City = "Brno";
        request.PostalCode = "602 00";
        request.Country = "Česká republika";

        await _service.RegisterAsync(request, "https://app.example.com");

        var address = (await LoadIssuerAsync(ico)).Address.ShouldHaveSingleItem();
        address.Street.ShouldBe("Opravená 9");
        address.City.ShouldBe("Brno");
        address.PostalCode.ShouldBe("602 00");
    }

    /// <summary>
    /// Pins the one behaviour USERGUIDE §0 has to warn about: clearing the address
    /// section does NOT register the company without an address — on the wire an empty
    /// field is indistinguishable from "an API client sent no address", so the ARES
    /// value comes back. Country stays pre-filled on purpose and must not, on its own,
    /// count as "the user typed an address" (that would store an address with an empty
    /// street, city and postcode).
    /// </summary>
    [Fact]
    public async Task Register_UserClearsAddressFields_FallsBackToAresAddress()
    {
        const string ico = "32345678";
        ArrangeAresWithAddress(ico);

        var request = BuildRequest(ico);
        request.Street = "";
        request.City = "   ";       // whitespace counts as cleared
        request.PostalCode = null;
        request.Country = "Česká republika";  // left untouched by the user

        await _service.RegisterAsync(request, "https://app.example.com");

        var address = (await LoadIssuerAsync(ico)).Address.ShouldHaveSingleItem();
        address.Street.ShouldBe("Hlavní 123", "cleared fields fall back to the ARES address");
        address.City.ShouldBe("Praha");
        address.PostalCode.ShouldBe("120 00");
    }

    /// <summary>
    /// ARES is unreachable (or the IČO is unknown) and the user supplied no address —
    /// registration must still succeed, just without an address record.
    /// </summary>
    [Fact]
    public async Task Register_AresFails_AndNoManualAddress_CreatesIssuerWithoutAddress()
    {
        const string ico = "32345678";
        _aresService.GetCompanyInfoAsync(ico, Arg.Any<CancellationToken>())
            .Returns<AresCompanyInfo>(_ => throw new HttpRequestException("ARES unreachable"));

        await _service.RegisterAsync(BuildRequest(ico), "https://app.example.com");

        (await LoadIssuerAsync(ico)).Address.ShouldBeEmpty();
    }

    /// <summary>
    /// An unsuccessful ARES answer (company not found) must not produce an empty
    /// address record — a blank address block on an invoice is worse than none.
    /// </summary>
    [Fact]
    public async Task Register_AresLookupUnsuccessful_DoesNotCreateEmptyAddress()
    {
        const string ico = "42345678";
        _aresService.GetCompanyInfoAsync(ico, Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                RegistrationNumber = ico,
                IsSuccessful = false,
                ErrorMessage = "Company not found in registry (HTTP NotFound)",
                FetchedAt = DateTime.UtcNow
            });

        await _service.RegisterAsync(BuildRequest(ico), "https://app.example.com");

        (await LoadIssuerAsync(ico)).Address.ShouldBeEmpty();
    }

    // ─── Registration derives the VAT payer flag (#208) ──────────────────────

    /// <summary>
    /// The reproducing test for #208: ARES returned a DIČ, so the company is registered
    /// for VAT and the issuer must be created as a VAT payer. Previously the flag was
    /// never assigned, so every self-registered company defaulted to a non-payer and its
    /// invoices came out without VAT until someone noticed and fixed it by hand.
    /// </summary>
    [Fact]
    public async Task Register_AresReturnsTaxNumber_MarksIssuerAsVatPayer()
    {
        const string ico = "62345678";
        ArrangeAresWithAddress(ico);

        await _service.RegisterAsync(BuildRequest(ico), "https://app.example.com");

        var issuer = await LoadIssuerAsync(ico);
        issuer.TaxNumber.ShouldBe("CZ" + ico);
        issuer.IsVatPayer.ShouldBeTrue("ARES returned a DIČ, so the company is a VAT payer.");
    }

    /// <summary>
    /// The other branch: the company exists in ARES but has no DIČ, so it is not
    /// registered for VAT. The flag must stay false (the onboarding assistant asks the
    /// user to confirm it — a DIČ is strong evidence, not a legal guarantee).
    /// </summary>
    [Fact]
    public async Task Register_AresReturnsNoTaxNumber_LeavesIssuerAsNonVatPayer()
    {
        const string ico = "72345678";
        ArrangeAresWithAddress(ico, taxNumber: null);

        await _service.RegisterAsync(BuildRequest(ico), "https://app.example.com");

        var issuer = await LoadIssuerAsync(ico);
        issuer.TaxNumber.ShouldBeNull();
        issuer.IsVatPayer.ShouldBeFalse("no DIČ in ARES means the company is not a VAT payer.");
    }

    /// <summary>
    /// ARES is unreachable — registration still succeeds and must not guess. A company
    /// wrongly flagged as a VAT payer would start issuing invoices with VAT it does not owe.
    /// </summary>
    [Fact]
    public async Task Register_AresFails_LeavesIssuerAsNonVatPayer()
    {
        const string ico = "82345678";
        _aresService.GetCompanyInfoAsync(ico, Arg.Any<CancellationToken>())
            .Returns<AresCompanyInfo>(_ => throw new HttpRequestException("ARES unreachable"));

        await _service.RegisterAsync(BuildRequest(ico), "https://app.example.com");

        (await LoadIssuerAsync(ico)).IsVatPayer.ShouldBeFalse();
    }

    // ─── Registration → provisioning: the address reaches the tenant ─────────

    /// <summary>
    /// Full acceptance criterion of #157: after registration AND tenant provisioning,
    /// the issuer inside the tenant schema must carry the ARES address, because the
    /// PDF template resolves its issuer block from exactly that record.
    /// </summary>
    [Fact]
    public async Task RegisterThenProvision_IssuerInTenantHasAddress()
    {
        const string ico = "52345678";
        ArrangeAresWithAddress(ico);

        await _service.RegisterAsync(BuildRequest(ico), "https://app.example.com");
        var masterIssuer = await LoadIssuerAsync(ico);

        // Provisioning step 6 — copy the issuer into the (here in-memory) tenant schema.
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        using var tenantContext = new TenantDbContext(tenantOptions);

        await InvokeCreateIssuerInTenantAsync(tenantContext, masterIssuer);

        var tenantIssuer = await tenantContext.Client
            .Include(c => c.Address)
            .FirstAsync(c => c.IsIssuer);

        var address = tenantIssuer.Address.ShouldHaveSingleItem();
        address.Street.ShouldBe("Hlavní 123");
        address.City.ShouldBe("Praha");
        address.PostalCode.ShouldBe("120 00");
        address.Country.ShouldBe("Česká republika");
    }
}
