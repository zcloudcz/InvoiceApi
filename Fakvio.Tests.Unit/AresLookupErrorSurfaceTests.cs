using System.Net;
using AresService;
using AresService.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Characterization tests for the failure surface of <see cref="AresServiceImpl"/>.
///
/// WHY THIS FILE EXISTS
///   PR #173 exposed the ARES registry to anonymous callers through
///   GET /api/auth/ares/{ico}. The controller deliberately swallows
///   <see cref="AresCompanyInfo.ErrorMessage"/> and answers with a fixed generic text,
///   because several of the strings this service produces are built from raw exception
///   text (connection errors, JSON parse errors, cache deserialization errors) and would
///   otherwise describe our internals to an unauthenticated visitor.
///
///   AnonymousAresLookupTests pins the controller side (nothing leaks out of the HTTP
///   response). This file pins the other half of that contract: WHICH failure shapes the
///   service can hand to the controller, including the two indirect paths that are easy
///   to miss — a value replayed from the cache, and a cache write that fails.
///   If a future change adds a new failure shape, the two files must move together.
///
/// The HTTP layer is stubbed — these tests never touch the network.
/// </summary>
public class AresLookupErrorSurfaceTests
{
    private const string ValidIco = "12345678";

    // Text that must never end up in an anonymous HTTP response. Used as the payload of
    // the simulated exceptions so the assertions can point at a concrete leak.
    private const string InternalDetail = "10.0.0.7:5432 refused";

    // Smallest ARES payload that ParseAresResponse accepts (name is mandatory, the rest
    // is optional). Enough to produce IsSuccessful = true.
    private const string MinimalAresJson =
        """{"obchodniJmeno":"Testovací firma s.r.o.","sidlo":{"nazevObce":"Praha","psc":12000}}""";

    // ── Direct failure shapes ─────────────────────────────────────────────────

    [Fact]
    public async Task GetCompanyInfo_RegistrationNumberNotEightChars_FailsWithoutTouchingCacheOrRegistry()
    {
        var cache = Substitute.For<IAresCacheRepository>();
        var service = CreateService(cache, new StubHttpMessageHandler(Ok(MinimalAresJson)));

        var result = await service.GetCompanyInfoAsync("1234");

        result.IsSuccessful.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Registration number must be exactly 8 digits");
        await cache.DidNotReceiveWithAnyArgs().GetCachedDataAsync(default!, default);
    }

    [Fact]
    public async Task RefreshCompanyInfo_RegistryReturnsNotFound_FailsWithStatusCodeOnly()
    {
        var service = CreateService(
            Substitute.For<IAresCacheRepository>(),
            new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await service.RefreshCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Company not found in registry (HTTP NotFound)");
    }

    [Fact]
    public async Task RefreshCompanyInfo_TransportFailure_FailureMessageCarriesExceptionText()
    {
        var service = CreateService(
            Substitute.For<IAresCacheRepository>(),
            new ThrowingHttpMessageHandler(new HttpRequestException(InternalDetail)));

        var result = await service.RefreshCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldStartWith("ARES API connection error: ");
        result.ErrorMessage.ShouldContain(InternalDetail,
            customMessage: "this is exactly why the anonymous endpoint must not echo ErrorMessage");
    }

    [Fact]
    public async Task RefreshCompanyInfo_UnparsableRegistryPayload_FailureMessageCarriesExceptionText()
    {
        // "psc" as a string breaks GetInt32() — the contract change AresAddressBoundaryTests guards.
        const string payloadWithWrongPostalCodeType =
            """{"obchodniJmeno":"Testovací firma s.r.o.","sidlo":{"nazevObce":"Praha","psc":"12000"}}""";

        var service = CreateService(
            Substitute.For<IAresCacheRepository>(),
            new StubHttpMessageHandler(Ok(payloadWithWrongPostalCodeType)));

        var result = await service.RefreshCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldStartWith("Error parsing ARES response: ");
        result.ErrorMessage.Length.ShouldBeGreaterThan("Error parsing ARES response: ".Length,
            "the message appends the raw exception text");
    }

    // ── Indirect failure shapes: the cache read path ──────────────────────────

    [Fact]
    public async Task GetCompanyInfo_CorruptCachedJson_FailureMessageCarriesExceptionText()
    {
        var service = CreateService(
            CacheReturning(CachedEntry(jsonData: "{ this is not json")),
            new ThrowingHttpMessageHandler(new HttpRequestException("the registry must not be called")));

        var result = await service.GetCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldStartWith("Error reading cached data: ",
            customMessage: "DeserializeCachedData is the second producer of exception-derived text");
    }

    [Fact]
    public async Task GetCompanyInfo_CachedJsonDeserializesToNull_FailsWithFixedMessage()
    {
        var service = CreateService(
            CacheReturning(CachedEntry(jsonData: "null")),
            new ThrowingHttpMessageHandler(new HttpRequestException("the registry must not be called")));

        var result = await service.GetCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Failed to deserialize cached data");
    }

    [Fact]
    public async Task GetCompanyInfo_CachedFailureEntry_ReplaysStoredErrorMessageWithoutCallingRegistry()
    {
        // A failed lookup is cached too (1h TTL), so a message that was built from an
        // exception once comes back on every later hit — including hits from the
        // anonymous endpoint. The suppression in the controller is what stops it there.
        var leakedFromEarlierRun = $"ARES API connection error: {InternalDetail}";
        var cachedFailure = new AresCompanyInfo
        {
            RegistrationNumber = ValidIco,
            IsSuccessful = false,
            ErrorMessage = leakedFromEarlierRun
        };

        var service = CreateService(
            CacheReturning(CachedEntry(jsonData: System.Text.Json.JsonSerializer.Serialize(cachedFailure))),
            new ThrowingHttpMessageHandler(new HttpRequestException("the registry must not be called")));

        var result = await service.GetCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(leakedFromEarlierRun);
    }

    [Fact]
    public async Task GetCompanyInfo_CacheReadThrows_FallsBackToLiveRegistryCall()
    {
        var cache = Substitute.For<IAresCacheRepository>();
        cache.GetCachedDataAsync(ValidIco, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(InternalDetail));

        var service = CreateService(cache, new StubHttpMessageHandler(Ok(MinimalAresJson)));

        var result = await service.GetCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeTrue("an unavailable cache must not fail the lookup");
        result.CompanyName.ShouldBe("Testovací firma s.r.o.");
        result.ErrorMessage.ShouldBeNull();
    }

    // ── Indirect failure shape: the best-effort cache write path ──────────────

    [Fact]
    public async Task GetCompanyInfo_CacheWriteThrows_SuccessfulLookupStaysSuccessful()
    {
        // CacheResult is best-effort. A dead database must not turn a good ARES answer
        // into an error response — that would surface as "Company not found in ARES."
        var cache = Substitute.For<IAresCacheRepository>();
        cache.SaveCacheAsync(Arg.Any<AresCacheEntry>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(InternalDetail));

        var service = CreateService(cache, new StubHttpMessageHandler(Ok(MinimalAresJson)));

        var result = await service.GetCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeTrue();
        result.ErrorMessage.ShouldBeNull();
        result.Address.ShouldNotBeNull();
        result.Address!.City.ShouldBe("Praha");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AresServiceImpl CreateService(IAresCacheRepository cache, HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        return new AresServiceImpl(
            new HttpClient(handler),
            cache,
            Substitute.For<ILogger<AresServiceImpl>>(),
            configuration);
    }

    private static IAresCacheRepository CacheReturning(AresCacheEntry entry)
    {
        var cache = Substitute.For<IAresCacheRepository>();
        cache.GetCachedDataAsync(entry.RegistrationNumber, Arg.Any<CancellationToken>())
            .Returns(entry);
        return cache;
    }

    /// <summary>A cache row that is still valid, so GetCompanyInfoAsync serves it instead of fetching.</summary>
    private static AresCacheEntry CachedEntry(string jsonData) => new()
    {
        RegistrationNumber = ValidIco,
        JsonData = jsonData,
        FetchedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddHours(1)
    };

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    };

    /// <summary>HttpMessageHandler stub returning a fixed response for any request.</summary>
    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    /// <summary>HttpMessageHandler stub throwing the given exception on every request.</summary>
    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
