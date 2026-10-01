using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

public sealed class CompanySessionServiceTests : IDisposable
{
    private readonly MasterDbContext _db = new(new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly DefaultHttpContext _http = new();
    private readonly CompanySessionService _service;
    public CompanySessionServiceTests()
    {
        _db.Client.AddRange(new Client { Id = 20, IsIssuer = true, RegistrationNumber = "20", CompanyName = "Default" }, new Client { Id = 21, IsIssuer = true, RegistrationNumber = "21", CompanyName = "Second" });
        _db.User.Add(new User { Id = 10, CompanyId = 20, Email = "member@test.cz", Role = EUserRole.Admin });
        _db.UserCompanyMembership.Add(new UserCompanyMembership { UserId = 10, CompanyId = 21, Role = EUserRole.User });
        _db.CompanySystemSettings.Add(new CompanySystemSettings { CompanyId = 21, SchemaName = "tenant_21", IsProvisioned = true });
        _db.SaveChanges();
        var current = Substitute.For<ICurrentUserService>(); current.GetCurrentUserId().Returns(10);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["JwtSettings:Secret"] = new string('s', 64), ["JwtSettings:ExpirationHours"] = "2" }).Build();
        _service = new(_db, current, new HttpContextAccessor { HttpContext = _http }, config);
    }
    [Fact]
    public async Task Switch_UsesLiveRoleAndCompanyInTokenAndResponse_WithoutChangingDefault()
    {
        var response = await _service.CreateAsync(21);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(response.Token);
        token.Claims.Single(c => c.Type == "CompanyId").Value.ShouldBe("21");
        token.Claims.Single(c => c.Type == ClaimTypes.Role).Value.ShouldBe("User");
        response.Role.ShouldBe(EUserRole.User); response.CompanyId.ShouldBe(21);
        response.CompanyName.ShouldBe("Second");
        _db.User.Single().CompanyId.ShouldBe(20);
        response.ExpiresAt.ShouldBeInRange(DateTime.UtcNow.AddMinutes(119), DateTime.UtcNow.AddMinutes(121));
        (await _service.CreateAsync(response.CompanyId)).CompanyId.ShouldBe(21);
    }
    [Theory]
    [InlineData("revoked")][InlineData("inactive-user")][InlineData("customer")][InlineData("unprovisioned")]
    public async Task InvalidAccess_CannotIssueToken(string condition)
    {
        if (condition == "revoked") _db.UserCompanyMembership.Single(m => m.CompanyId == 21).IsActive = false;
        if (condition == "inactive-user") _db.User.Single().IsActive = false;
        if (condition == "customer") _db.Client.Single(c => c.Id == 21).IsIssuer = false;
        if (condition == "unprovisioned") _db.CompanySystemSettings.Single().IsProvisioned = false;
        await _db.SaveChangesAsync();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.CreateAsync(21));
    }
    [Fact]
    public async Task ForeignCompany_CannotIssueToken()
        => await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.CreateAsync(999));
    [Fact]
    public async Task ScopedCredential_CannotMintBrowserToken()
    {
        _http.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ApiKeyAuthenticationDefaults.ScopeClaimType, "read,write") }, "ApiKey"));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.CreateAsync(21));
    }
    public void Dispose() => _db.Dispose();
}
