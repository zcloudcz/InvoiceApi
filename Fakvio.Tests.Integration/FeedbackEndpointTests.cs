using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Feedback;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>Uses real authentication, scopes, impersonation, routing and MasterDb queries.</summary>
public class FeedbackEndpointTests : IClassFixture<FeedbackEndpointTests.Factory>
{
    public sealed class Factory : FakvioFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("McpOAuth:Enabled", "true");
            builder.UseSetting("McpOAuth:ResourceProofSecret", "feedback-test-proof");
        }
    }
    private readonly Factory _factory;
    public FeedbackEndpointTests(Factory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        if (db.User.Any(u => u.Id == 910)) return;
        db.Client.AddRange(new Client { Id = 920, IsIssuer = true, RegistrationNumber = "99999921", CompanyName = "Feedback A" },
            new Client { Id = 921, IsIssuer = true, RegistrationNumber = "99999922", CompanyName = "Feedback B" });
        db.User.AddRange(new User { Id = 910, CompanyId = 920, Email = "feedback-a@example.test", Role = EUserRole.User },
            new User { Id = 911, CompanyId = 920, Email = "feedback-b@example.test", Role = EUserRole.User },
            new User { Id = 912, CompanyId = 921, Email = "feedback-c@example.test", Role = EUserRole.User },
            new User { Id = 913, Email = "feedback-admin@example.test", Role = EUserRole.SysAdmin },
            new User { Id = 914, Email = "feedback-unbound@example.test", Role = EUserRole.User });
        db.SaveChanges();
    }

    private HttpClient Jwt(long? userId = 910, long? companyId = 920, string role = "User")
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role) };
        if (userId.HasValue) claims.Add(new(ClaimTypes.NameIdentifier, userId.ToString()!));
        if (companyId.HasValue) claims.Add(new("CompanyId", companyId.ToString()!));
        var token = new JwtSecurityToken("Fakvio.Tests", "Fakvio.Tests.Client", claims,
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes("TestSecretKeyForIntegrationTestsThatMustBeAtLeast32BytesLong!")), SecurityAlgorithms.HmacSha256));
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private async Task<HttpClient> Machine(long userId, string scopes, bool oauth)
    {
        // Seed only credential material; requests still traverse the production authenticator.
        var raw = (oauth ? "fak_oat_" : "fak_live_") + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var key = new ApiKey { UserId = userId, Name = "Feedback test", KeyPrefix = raw[..12],
            KeyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw))), Scopes = scopes, ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
        if (oauth)
            key.OAuthGrant = new OAuthGrant { UserId = userId, ClientId = "https://example.test/client", ClientName = "Test", Scopes = scopes,
                Resource = "https://mcp.example.test", ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.ApiKey.Add(key); await db.SaveChangesAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", raw);
        if (oauth) client.DefaultRequestHeaders.Add("X-Fakvio-Resource-Proof", "feedback-test-proof");
        return client;
    }

    private static CreateFeedbackDto Input() => new() { Subject = "Feedback test", Description = "Private <script>text</script>", Page = "/invoices?secret=remove#fragment" };
    private static async Task<FeedbackDto> Submit(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/feedback", Input());
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FeedbackDto>())!;
    }

    [Fact]
    public async Task Anonymous_CannotReadOrSubmit()
    {
        using var client = _factory.CreateClient();
        (await client.GetAsync("/api/feedback")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/feedback", Input())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/sysadmin/feedback")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Submission_IgnoresOverpostedOwnershipStatusAndStripsPageSecrets()
    {
        using var client = Jwt();
        var response = await client.PostAsJsonAsync("/api/feedback", new { Type = 0, Subject = "Overpost", Description = "body", Page = "/p?token=secret#x", UserId = 911, CompanyId = 921, Status = 2, PublicResponse = "spoof" });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var report = (await response.Content.ReadFromJsonAsync<FeedbackDto>())!;
        report.UserId.ShouldBe(910); report.CompanyId.ShouldBe(920); report.Status.ShouldBe(EFeedbackStatus.New); report.Page.ShouldBe("/p"); report.PublicResponse.ShouldBeNull();
    }

    [Theory]
    [InlineData(911, 920)] [InlineData(912, 921)]
    public async Task GuessedId_AnotherUserOrCompanyGetsNotFound(long user, long company)
    {
        using var owner = Jwt(); var report = await Submit(owner);
        using var other = Jwt(user, company);
        (await other.GetAsync($"/api/feedback/{report.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var list = await other.GetFromJsonAsync<PagedResult<FeedbackDto>>("/api/feedback");
        list!.Items.ShouldNotContain(r => r.Id == report.Id);
    }

    [Theory]
    [InlineData(null, 920)] [InlineData(910, null)] [InlineData(910, 921)] [InlineData(999, 920)]
    public async Task MissingOrStaleBinding_IsForbidden(int? user, int? company)
    {
        using var client = Jwt(user, company);
        (await client.PostAsJsonAsync("/api/feedback", Input())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_CanRespondWithoutImpersonation_RegularUserCannot()
    {
        using var owner = Jwt(); var report = await Submit(owner);
        var update = new UpdateFeedbackStatusDto { Status = EFeedbackStatus.Resolved, PublicResponse = "Resolved <b>plain text</b>" };
        (await owner.GetAsync("/api/sysadmin/feedback")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.GetAsync($"/api/sysadmin/feedback/{report.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.PatchAsJsonAsync($"/api/sysadmin/feedback/{report.Id}", update)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var admin = Jwt(913, null, "SysAdmin");
        (await admin.GetAsync("/api/sysadmin/feedback")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PatchAsJsonAsync($"/api/sysadmin/feedback/{report.Id}", update)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var detail = await owner.GetFromJsonAsync<FeedbackDto>($"/api/feedback/{report.Id}");
        detail!.PublicResponse.ShouldBe(update.PublicResponse); detail.Status.ShouldBe(EFeedbackStatus.Resolved);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MachineCredentials_EnforceScopesAndOwnership(bool oauth)
    {
        using var read = await Machine(910, "read", oauth);
        (await read.PostAsJsonAsync("/api/feedback", Input())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await read.GetAsync("/api/feedback")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var write = await Machine(910, "read,write", oauth);
        var report = await Submit(write);
        using var other = await Machine(911, "read,write", oauth);
        (await other.GetAsync($"/api/feedback/{report.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var otherCompany = await Machine(912, "read,write", oauth);
        otherCompany.DefaultRequestHeaders.Add("X-Company-Id", "920");
        (await otherCompany.GetAsync($"/api/feedback/{report.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await write.GetAsync("/api/sysadmin/feedback")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await write.PatchAsJsonAsync($"/api/sysadmin/feedback/{report.Id}", new UpdateFeedbackStatusDto())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnboundApiKey_CannotSubmit()
    {
        using var key = await Machine(914, "read,write", false);
        (await key.PostAsJsonAsync("/api/feedback", Input())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task InvalidInput_IsBadRequest()
    {
        using var client = Jwt(); var input = Input(); input.Type = (EFeedbackType)99;
        (await client.PostAsJsonAsync("/api/feedback", input)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        input = Input(); input.Page = "//example.test";
        (await client.PostAsJsonAsync("/api/feedback", input)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/feedback?pageSize=101")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/feedback?status=99")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
