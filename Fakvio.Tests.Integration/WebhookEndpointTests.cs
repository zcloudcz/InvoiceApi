using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Webhook;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for <c>api/webhooks</c> (DEVGUIDE §4.15): role gating, secret shown only
/// on create/rotate, URL validation (https only) and SSRF refusal on the synchronous test action.
/// </summary>
public class WebhookEndpointTests : IClassFixture<FakvioFactory>
{
    private const long CompanyId = 7800L;
    private const string UserEmail = "webhook-user@test.invalid";

    private readonly FakvioFactory _factory;
    private readonly string _userPassword;

    public WebhookEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        SeedCompany();
        _userPassword = _factory.SeedRegularUser(UserEmail, userId: 7801, companyId: CompanyId);
    }

    private void SeedCompany()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        if (!masterDb.Client.Any(c => c.Id == CompanyId))
            masterDb.Client.Add(new Client { Id = CompanyId, CompanyName = "Webhook Co", RegistrationNumber = "WH000001", IsIssuer = true, IsActive = true });
        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings { CompanyId = CompanyId, SchemaName = $"tenant_wh_{CompanyId}", IsProvisioned = true, IsActive = true });
        masterDb.SaveChanges();
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, (await AuthHelper.LoginAsSysAdminAsync(client)).Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    [Fact]
    public async Task OrdinaryUser_IsForbidden()
    {
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, (await AuthHelper.LoginAsync(client, UserEmail, _userPassword)).Token);

        (await client.GetAsync("/api/webhooks")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_ReturnsSecretOnce_ListNeverDoes()
    {
        var client = await AdminClientAsync();

        var response = await client.PostAsJsonAsync("/api/webhooks", new CreateWebhookSubscriptionDto
        {
            Url = "https://hooks.example.com/fakvio", Events = [WebhookEventCatalog.InvoicePaid],
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = (await response.Content.ReadFromJsonAsync<WebhookSubscriptionCreatedDto>())!;
        created.Secret.ShouldNotBeNullOrEmpty();

        var listJson = await (await client.GetAsync("/api/webhooks")).Content.ReadAsStringAsync();
        listJson.ShouldContain("hooks.example.com");
        listJson.ShouldNotContain(created.Secret);

        var rotated = (await (await client.PostAsync($"/api/webhooks/{created.Subscription.Id}/rotate-secret", null))
            .Content.ReadFromJsonAsync<WebhookSubscriptionCreatedDto>())!;
        rotated.Secret.ShouldNotBe(created.Secret);
    }

    [Fact]
    public async Task Deliveries_ForUnknownSubscription_Returns404()
    {
        var client = await AdminClientAsync();
        (await client.GetAsync("/api/webhooks/999999/deliveries")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("http://hooks.example.com/x")]
    [InlineData("not a url")]
    public async Task Create_RejectsNonHttpsUrl(string url)
    {
        var client = await AdminClientAsync();
        var response = await client.PostAsJsonAsync("/api/webhooks", new CreateWebhookSubscriptionDto
        {
            Url = url, Events = [WebhookEventCatalog.InvoicePaid],
        });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Test_AgainstLoopbackTarget_IsRefusedBySsrfGuard()
    {
        var client = await AdminClientAsync();
        // https://127.0.0.1 passes the scheme check but must be refused at connect time.
        var created = (await (await client.PostAsJsonAsync("/api/webhooks", new CreateWebhookSubscriptionDto
        {
            Url = "https://127.0.0.1:9/hook", Events = [WebhookEventCatalog.InvoicePaid],
        })).Content.ReadFromJsonAsync<WebhookSubscriptionCreatedDto>())!;

        var result = (await (await client.PostAsync($"/api/webhooks/{created.Subscription.Id}/test", null))
            .Content.ReadFromJsonAsync<WebhookTestResultDto>())!;

        result.Success.ShouldBeFalse();
        // The refusal must come from the SSRF guard, not from a generic connection failure.
        result.Error.ShouldNotBeNull().ShouldContain("blocked");
    }
}
