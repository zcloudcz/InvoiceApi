using AresService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Boundary test against the live ARES registry (https://ares.gov.cz).
///
/// Issue #157 is about the registered office address being dropped during
/// self-registration. The unit tests cover our own code with a stubbed
/// <see cref="IAresService"/>; this test covers the other half of the contract —
/// that the real registry still returns an address in the shape our parser expects.
/// If ARES changes its JSON (renames "sidlo", "nazevObce", "psc"), the address would
/// silently become empty again and only this test would notice.
///
/// The test is OFF by default: it needs internet access and depends on a third-party
/// service, so it must never break a normal `dotnet test` run. Enable it with:
///     RUN_ARES_LIVE_TESTS=true dotnet test Fakvio.Tests.Integration
/// </summary>
public class AresAddressBoundaryTests
{
    /// <summary>Environment variable that enables the live ARES call.</summary>
    private const string LiveTestsEnvVar = "RUN_ARES_LIVE_TESTS";

    /// <summary>
    /// IČO of the Czech Ministry of Finance — a long-lived public subject whose
    /// registered office is guaranteed to be present in ARES.
    /// </summary>
    private const string WellKnownIco = "00006947";

    [SkippableFact]
    public async Task GetCompanyInfo_LiveAres_ReturnsRegisteredOfficeAddress()
    {
        // ── Gate: only run when explicitly requested ─────────────────────────
        var runLive = Environment.GetEnvironmentVariable(LiveTestsEnvVar);
        Skip.IfNot(string.Equals(runLive, "true", StringComparison.OrdinalIgnoreCase),
            $"Skipped: set {LiveTestsEnvVar}=true to run the live ARES boundary test.");

        // ── Arrange: real HTTP client, no cache (substitute returns null) ─────
        var cacheRepository = Substitute.For<IAresCacheRepository>();
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var configuration = new ConfigurationBuilder().Build(); // uses the default ARES URL

        var service = new AresServiceImpl(
            httpClient,
            cacheRepository,
            NullLogger<AresServiceImpl>.Instance,
            configuration);

        // ── Act ───────────────────────────────────────────────────────────────
        var info = await service.GetCompanyInfoAsync(WellKnownIco);

        // ARES outage must not turn into a red build — skip instead.
        Skip.IfNot(info.IsSuccessful,
            $"ARES lookup failed ({info.ErrorMessage}) — skipping (registry unavailable).");

        // ── Assert: the address block our registration flow relies on ────────
        info.CompanyName.ShouldNotBeNullOrWhiteSpace();
        info.Address.ShouldNotBeNull("ARES must return the registered office (sidlo) block.");
        info.Address!.Street.ShouldNotBeNullOrWhiteSpace();
        info.Address.City.ShouldNotBeNullOrWhiteSpace();
        info.Address.PostalCode.ShouldNotBeNullOrWhiteSpace();
        info.Address.Country.ShouldNotBeNullOrWhiteSpace();
    }
}
