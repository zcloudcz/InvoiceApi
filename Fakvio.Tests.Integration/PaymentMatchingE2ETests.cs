using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.CompanySettings;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Full HTTP integration tests for the payment matching feature
/// (see PLATBY-ZADANI.md §11.2). Exercises the controllers, DI graph,
/// and MasterMailboxIndex routing — all backed by InMemoryDatabase.
///
/// IMAP / Email parsing isn't run here — those are unit-tested separately.
/// These tests focus on lifecycle endpoints that real users hit from the UI.
/// </summary>
public class PaymentMatchingE2ETests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    public PaymentMatchingE2ETests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    /// <summary>
    /// SysAdmin GET /settings on a fresh DB returns a default row (the service
    /// auto-materializes one on first call). The password must NEVER be returned.
    /// </summary>
    [Fact]
    public async Task SysAdmin_GetSettings_ReturnsDefaultRow_WithoutPassword()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);

        var response = await client.GetAsync("/api/sysadmin/payment-matching/settings");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<PaymentMatchingSystemSettingsDto>();
        dto.ShouldNotBeNull();

        // Defaults from PaymentMatchingSystemSettingsService.LoadOrCreateAsync.
        dto.PollIntervalMinutes.ShouldBe(30);
        dto.InboundDomain.ShouldBe("pay.fakvio.cz");
        dto.IsEnabled.ShouldBeFalse();

        // Password is write-only from the client perspective.
        dto.ImapPassword.ShouldBeNull();
    }

    /// <summary>
    /// SysAdmin PUT /settings rejects an out-of-range PollIntervalMinutes.
    /// Service throws ArgumentOutOfRangeException → controller returns 400.
    /// </summary>
    [Fact]
    public async Task SysAdmin_UpdateSettings_OutOfRangeInterval_Returns400()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);

        var dto = new PaymentMatchingSystemSettingsDto
        {
            ImapHost = "imap.example.com",
            ImapPort = 993,
            ImapUseSsl = true,
            ImapUsername = "pay@example.com",
            ImapPassword = "secret",
            ImapFolder = "INBOX",
            ProcessedFolder = "Processed",
            UnroutedFolder = "Unrouted",
            InboundDomain = "pay.example.com",
            PollIntervalMinutes = 1, // BELOW the allowed minimum (5)
            InboundEmailRetentionDays = 365,
        };

        var response = await client.PutAsJsonAsync("/api/sysadmin/payment-matching/settings", dto);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Non-SysAdmin users cannot access the SysAdmin settings endpoint —
    /// the [Authorize(Roles = "SysAdmin")] attribute returns 403 for everyone else.
    /// We verify by making a request WITHOUT a SysAdmin token.
    /// </summary>
    [Fact]
    public async Task Anonymous_GetSettings_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/sysadmin/payment-matching/settings");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Smoke test for the mailbox endpoints inside a real tenant: routing, DI, and
    /// 404-on-missing all wire up correctly through the full HTTP pipeline.
    ///
    /// Full lifecycle (Activate / Deactivate / Reactivate / Regenerate) is exercised
    /// at unit level by <c>BankAccountMailboxServiceTests</c> — repeating it through
    /// HTTP would require provisioning a BankAccount via internal DbContext, which
    /// adds complexity without exercising any new code path beyond DI + routing.
    /// </summary>
    [Fact]
    public async Task Mailbox_GetForUnknownAccount_Returns404()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);

        var companyId = await ProvisionTenantAsync(client);
        AuthHelper.SetImpersonation(client, companyId);

        var response = await client.GetAsync("/api/payment-matching/mailbox/999999");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Verifies the unmatched-count endpoint returns 0 on a freshly provisioned tenant.
    /// Real bank transactions can't be created via API — they are inserted by the
    /// IMAP worker / parser, both unit-tested separately. So 0 is the correct answer.
    /// </summary>
    [Fact]
    public async Task UnmatchedCount_FreshTenant_ReturnsZero()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);

        var companyId = await ProvisionTenantAsync(client);
        AuthHelper.SetImpersonation(client, companyId);

        var response = await client.GetAsync("/api/payment-matching/unmatched-count");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var count = await response.Content.ReadFromJsonAsync<int>();
        count.ShouldBe(0);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the company creation steps from MultiTenantE2ETests.
    /// Returns the new tenant's CompanyId once it is provisioned and active.
    /// </summary>
    private static async Task<long> ProvisionTenantAsync(HttpClient client)
    {
        var createCompany = await client.PostAsJsonAsync("/api/company", new CreateClientDto
        {
            CompanyName = $"Payment Test Co {Guid.NewGuid():N}",
            RegistrationNumber = Random.Shared.Next(10_000_000, 99_999_999).ToString(),
            IsIssuer = true,
            FetchFromAres = false,
        });
        createCompany.EnsureSuccessStatusCode();
        var company = (await createCompany.Content.ReadFromJsonAsync<ClientDto>())!;

        await client.PostAsJsonAsync("/api/company/settings", new CreateCompanySystemSettingsDto
        {
            CompanyId = company.Id,
            SchemaName = $"test_tenant_{company.Id}",
        });

        var provision = await client.PostAsync($"/api/company/{company.Id}/provision", null);
        provision.EnsureSuccessStatusCode();

        return company.Id;
    }

}
