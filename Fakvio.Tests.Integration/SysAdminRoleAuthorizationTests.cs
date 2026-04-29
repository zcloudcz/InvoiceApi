using System.Net;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration regression tests for issue #20: SysAdmin role authorization failure.
///
/// ROOT CAUSE (issue #20):
///   AuthenticationExtensions.AddFakvioAuthentication() was missing RoleClaimType and
///   NameClaimType in TokenValidationParameters. The fix adds:
///     RoleClaimType = ClaimTypes.Role
///     NameClaimType = ClaimTypes.Name
///
///   Without RoleClaimType explicitly set, certain JWT handler configurations
///   (especially JsonWebTokenHandler-based pipelines, e.g. Azure Functions Isolated Worker,
///   or when MapInboundClaims is changed) leave the role claim under the short JWT key
///   "role" instead of the long URI ClaimTypes.Role. That causes:
///     ClaimsPrincipal.IsInRole("SysAdmin") → false → [Authorize(Roles = "SysAdmin")] → 403
///
/// WHY THESE TESTS ARE THE CORRECT GUARD:
///   These tests exercise the FULL pipeline end-to-end:
///     POST /api/auth/login → IAuthService.GenerateJwtTokenAsync → JWT issued
///     → Authorization: Bearer header → real JwtBearer middleware (via AddFakvioAuthentication)
///     → [Authorize(Roles = "SysAdmin")] → controller action
///
///   The unit tests in AuthServiceTests.cs:267-326 use a hand-rolled
///   TokenValidationParameters and therefore do NOT go through AddFakvioAuthentication.
///   They pass with or without the fix and give false confidence.
///   These integration tests use the REAL AddFakvioAuthentication registration.
///
/// WHAT WOULD BREAK THESE TESTS:
///   Any change that causes the SysAdmin JWT to fail [Authorize(Roles = "SysAdmin")]:
///   - Removing RoleClaimType from TokenValidationParameters (original bug)
///   - Changing the claim emitted by AuthService from ClaimTypes.Role to a different type
///   - Setting MapInboundClaims = false without a compensating RoleClaimType (future risk)
///   - JWT secret/issuer/audience mismatch between AuthService and JwtBearer options
///
/// FUNCTIONS PARITY:
///   The Azure Functions host uses JwtAuthenticationMiddleware.cs which already had
///   RoleClaimType set (line 97). Functions also call AddFakvioAuthentication (Program.cs:118)
///   which now also has RoleClaimType set — redundant but consistent. No separate
///   fix needed for Functions. The fix is purely additive and benefits both hosts.
/// </summary>
public class SysAdminRoleAuthorizationTests : IClassFixture<FakvioFactory>
{
    private readonly FakvioFactory _factory;

    // Credentials for the regular (non-SysAdmin) user seeded by this test class.
    // A unique email avoids collisions when multiple test classes share the same factory.
    private const string RegularUserEmail = "regular-user-role-auth-test@example.com";
    private const long RegularUserId = 200; // distinct from seed admin (id=1)
    private readonly string _regularUserPassword;

    public SysAdminRoleAuthorizationTests(FakvioFactory factory)
    {
        _factory = factory;

        // Schema + SysAdmin seed data must be ready before we add the extra user.
        _factory.InitializeDatabase();

        // Seed a User-role account for negative (403) tests.
        // SeedRegularUser() is idempotent — safe to call once per constructor.
        _regularUserPassword = _factory.SeedRegularUser(RegularUserEmail, RegularUserId);
    }

    // ─── Positive tests: SysAdmin must pass [Authorize(Roles = "SysAdmin")] ──────

    /// <summary>
    /// Core regression test for issue #20.
    ///
    /// Exercises the FULL pipeline:
    ///   POST /api/auth/login (IAuthService.GenerateJwtTokenAsync)
    ///   → JWT stored in Authorization: Bearer header
    ///   → GET /api/sysadmin/payment-matching/settings
    ///   → JwtBearer middleware (configured by AddFakvioAuthentication)
    ///   → [Authorize(Roles = "SysAdmin")] → ClaimsPrincipal.IsInRole("SysAdmin")
    ///   → 200 OK (not 403)
    ///
    /// This test fails if AddFakvioAuthentication's TokenValidationParameters has a
    /// configuration that prevents role claims from being recognized (original bug).
    /// </summary>
    [Fact]
    public async Task SysAdmin_Get_PaymentMatchingSettings_Returns200_NotForbidden()
    {
        // Arrange: log in as the seeded SysAdmin and attach the JWT to the client
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);

        // Act: hit a SysAdmin-only endpoint through the real JwtBearer middleware
        var response = await client.GetAsync("/api/sysadmin/payment-matching/settings");

        // Assert: must NOT be 403 (original symptom of issue #20)
        // 200 = correct — endpoint found and authorized
        // If this returns 403, AddFakvioAuthentication is broken for role authorization.
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden,
            "SysAdmin JWT must satisfy [Authorize(Roles = \"SysAdmin\")]. " +
            "403 means role claim is not being recognized by the JwtBearer middleware " +
            "(missing or wrong RoleClaimType in AddFakvioAuthentication — issue #20 bug).");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Smoke-checks three additional SysAdmin-only controllers with the same token.
    /// Confirms the fix covers every controller, not just PaymentMatchingSysAdminController.
    ///
    /// Controllers tested:
    ///   - CompanyController         [Authorize(Roles = "SysAdmin")] at class level
    ///   - AppLogController          [Authorize(Roles = "SysAdmin")] at class level
    ///   - DashboardController/sysadmin [Authorize(Roles = "SysAdmin")] at action level
    /// </summary>
    [Fact]
    public async Task SysAdmin_SmokeCheck_MultipleControllers_NoneReturn403()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);

        // ── GET /api/company ─────────────────────────────────────────────────
        var companyResponse = await client.GetAsync("/api/company");
        companyResponse.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden,
            "/api/company must not return 403 for SysAdmin (CompanyController class-level attribute)");
        companyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // ── GET /api/logs/paged ──────────────────────────────────────────────
        // AppLogController route is /api/logs, action is GET /paged
        var logsResponse = await client.GetAsync("/api/logs/paged");
        logsResponse.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden,
            "/api/logs/paged must not return 403 for SysAdmin (AppLogController class-level attribute)");
        logsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // ── GET /api/dashboard/sysadmin ──────────────────────────────────────
        var dashboardResponse = await client.GetAsync("/api/dashboard/sysadmin");
        dashboardResponse.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden,
            "/api/dashboard/sysadmin must not return 403 for SysAdmin");
        dashboardResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ─── Negative tests: regular User MUST be blocked (403) by SysAdmin endpoints ─

    /// <summary>
    /// Verifies that a User-role JWT is correctly blocked (403) by SysAdmin-only endpoints.
    ///
    /// This is the NEGATIVE complement to SysAdmin_Get_PaymentMatchingSettings_Returns200.
    ///
    /// It confirms:
    ///   a) The fix did not accidentally grant SysAdmin access to everyone.
    ///   b) The role claim IS present in the token (otherwise we'd see 401, not 403),
    ///      proving the full authentication → authorization pipeline works end-to-end.
    ///
    /// Issue #20 context: before the fix, BOTH SysAdmin AND User tokens returned 403 for
    /// SysAdmin-only endpoints (role authorization completely broken). After the fix:
    ///   SysAdmin → 200  (authorized)
    ///   User     → 403  (authenticated but not authorized — correct asymmetry)
    /// </summary>
    [Fact]
    public async Task RegularUser_Get_PaymentMatchingSettings_Returns403()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, RegularUserEmail, _regularUserPassword);
        AuthHelper.SetAuthToken(client, login.Token);

        var response = await client.GetAsync("/api/sysadmin/payment-matching/settings");

        // Must be 403 Forbidden (authenticated but not authorized).
        // 401 would mean the token itself was rejected — unexpected.
        // 200 would mean role gate is completely broken — critical regression.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "A User-role JWT must not pass [Authorize(Roles = \"SysAdmin\")]. " +
            "Expected 403 Forbidden — role gate is working correctly when this passes.");
    }

    /// <summary>
    /// Verifies that a regular User cannot access CompanyController (SysAdmin-only).
    /// </summary>
    [Fact]
    public async Task RegularUser_Get_CompanyList_Returns403()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, RegularUserEmail, _regularUserPassword);
        AuthHelper.SetAuthToken(client, login.Token);

        var response = await client.GetAsync("/api/company");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "A User-role JWT must not pass [Authorize(Roles = \"SysAdmin\")] on CompanyController.");
    }
}
