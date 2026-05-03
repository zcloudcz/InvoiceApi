using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.ReverseChargeCode;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for ReverseChargeCodeController (issue #46):
///   GET /api/reversechargecode       — returns active codes; 401 for unauthenticated
///   GET /api/reversechargecode/{id}  — returns single code; 404 when not found
///
/// Covered acceptance criteria:
///   - Unauthenticated request → 401 Unauthorized (the endpoint requires [Authorize]).
///   - Authenticated + impersonating SysAdmin → 200 OK with seeded active codes.
///   - Inactive codes are NOT returned by the list endpoint.
///   - GET /{id} returns the correct code when it exists.
///   - GET /{id} returns 404 when the ID is not found.
///
/// ReverseChargeCode lives in the TenantDbContext schema, so SysAdmin must
/// impersonate a company via X-Company-Id — the same pattern used in EpoApiEndpointTests.
///
/// Uses FakvioFactory (WebApplicationFactory) with InMemoryDatabase — no real DB needed.
/// The factory is shared (IClassFixture) so the host is started once for this test class.
/// Each test seeds its own data and company to avoid cross-test state pollution.
/// </summary>
public class ReverseChargeCodeControllerTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    // Fixed date used in seed — codes have ValidFrom in the past so they appear as "active".
    private static readonly DateOnly ValidFromPast = new(2016, 1, 1);

    // Company ID used for SysAdmin impersonation in this test class.
    // Chosen to avoid collision with EPO tests (42, 43) and Multi-tenant tests (dynamic).
    private const long TestCompanyId = 501L;

    // Reverse charge code IDs used across multiple tests — seeded once by SeedAll().
    private const long ActiveCode1Id  = 2001;
    private const long ActiveCode2Id  = 2002;
    private const long InactiveCodeId = 2003;
    private const long NonExistentId  = 99999;

    public ReverseChargeCodeControllerTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    // ── Unauthenticated: 401 ──────────────────────────────────────────────────

    /// <summary>
    /// Without a JWT the endpoint must reject the request with 401.
    /// Guards that [Authorize] is correctly applied to the controller.
    /// </summary>
    [Fact]
    public async Task GetAllActive_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();
        // No Authorization header set.

        var response = await client.GetAsync("/api/reversechargecode");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            "GET /api/reversechargecode must require authentication ([Authorize] on controller).");
    }

    [Fact]
    public async Task GetById_WithoutAuth_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/reversechargecode/{ActiveCode1Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            "GET /api/reversechargecode/{id} must require authentication.");
    }

    // ── Authenticated + impersonating: list endpoint ──────────────────────────

    [Fact]
    public async Task GetAllActive_Authenticated_Returns200()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var response = await client.GetAsync("/api/reversechargecode");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAllActive_ReturnsSeededActiveCodes()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var codes = await client.GetFromJsonAsync<List<ReverseChargeCodeDto>>("/api/reversechargecode");

        codes.ShouldNotBeNull();
        codes.ShouldContain(c => c.Id == ActiveCode1Id, "Active code 1 must be returned");
        codes.ShouldContain(c => c.Id == ActiveCode2Id, "Active code 2 must be returned");
    }

    [Fact]
    public async Task GetAllActive_ExcludesInactiveCodes()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var codes = await client.GetFromJsonAsync<List<ReverseChargeCodeDto>>("/api/reversechargecode");

        codes.ShouldNotBeNull();
        codes.ShouldNotContain(c => c.Id == InactiveCodeId,
            "Inactive codes must be excluded from the active list endpoint.");
    }

    [Fact]
    public async Task GetAllActive_ReturnsCorrectFieldsOnCode()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var codes = await client.GetFromJsonAsync<List<ReverseChargeCodeDto>>("/api/reversechargecode");

        codes.ShouldNotBeNull();
        var code = codes.Single(c => c.Id == ActiveCode1Id);
        code.Code.ShouldBe("11");
        code.NameCs.ShouldBe("Stavební práce");
        code.NameEn.ShouldBe("Construction work");
        code.ParagraphRef.ShouldBe("§92d");
        code.IsActive.ShouldBeTrue();
    }

    // ── Authenticated + impersonating: single-code endpoint ───────────────────

    [Fact]
    public async Task GetById_ReturnsCorrectCode_ForExistingId()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var code = await client.GetFromJsonAsync<ReverseChargeCodeDto>(
            $"/api/reversechargecode/{ActiveCode2Id}");

        code.ShouldNotBeNull();
        code.Id.ShouldBe(ActiveCode2Id);
        code.Code.ShouldBe("5");
        code.NameCs.ShouldBe("Mobilní telefony");
        code.ParagraphRef.ShouldBe("§92c");
    }

    [Fact]
    public async Task GetById_Returns404_ForNonExistentId()
    {
        SeedAll();
        var client = await CreateImpersonatingClientAsync();

        var response = await client.GetAsync($"/api/reversechargecode/{NonExistentId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "GET /api/reversechargecode/{id} must return 404 when the ID does not exist.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an HttpClient authenticated as SysAdmin and impersonating TestCompanyId.
    ///
    /// ReverseChargeCode lives in TenantDbContext — SysAdmin must supply X-Company-Id
    /// to pass TenantContextMiddleware (same pattern as EPO endpoint tests).
    /// </summary>
    private async Task<HttpClient> CreateImpersonatingClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, TestCompanyId);
        return client;
    }

    /// <summary>
    /// Seeds company settings (for impersonation) and reverse charge codes.
    /// Idempotent — safe to call multiple times per test run.
    ///
    /// Follows the EPO endpoint tests pattern: seed directly before each test
    /// using the factory's DI scope so the same InMemoryDatabase is used.
    /// </summary>
    private void SeedAll()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        // ── Master DB: company + settings for impersonation ────────────────────
        if (!masterDb.Client.Any(c => c.Id == TestCompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = TestCompanyId,
                CompanyName = "RCC Test Company",
                RegistrationNumber = "T0000501",
                TaxNumber = "CZ00000501",
                IsIssuer = true,
                IsActive = true
            });
        }

        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == TestCompanyId))
        {
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = TestCompanyId,
                SchemaName = $"tenant_{TestCompanyId}",
                IsProvisioned = true,
                IsActive = true
            });
        }

        masterDb.SaveChanges();

        // ── Tenant DB: test reverse charge codes ───────────────────────────────
        if (!tenantDb.ReverseChargeCode.Any(c => c.Id == ActiveCode1Id))
        {
            tenantDb.ReverseChargeCode.AddRange(
                new ReverseChargeCode
                {
                    Id = ActiveCode1Id,
                    Code = "11",
                    NameCs = "Stavební práce",
                    NameEn = "Construction work",
                    ParagraphRef = "§92d",
                    ValidFrom = ValidFromPast,
                    ValidTo = null,
                    IsActive = true
                },
                new ReverseChargeCode
                {
                    Id = ActiveCode2Id,
                    Code = "5",
                    NameCs = "Mobilní telefony",
                    NameEn = "Mobile phones",
                    ParagraphRef = "§92c",
                    ValidFrom = ValidFromPast,
                    ValidTo = null,
                    IsActive = true
                },
                new ReverseChargeCode
                {
                    Id = InactiveCodeId,
                    Code = "99",
                    NameCs = "Neaktivní kód",
                    NameEn = "Inactive code",
                    ParagraphRef = "§92e",
                    ValidFrom = ValidFromPast,
                    ValidTo = null,
                    IsActive = false  // must NOT appear in GetAllActive
                }
            );

            tenantDb.SaveChanges();
        }
    }
}
