using System.Net;
using System.Net.Http.Json;
using AresService;
using AresService.Model;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Regression tests for the second half of issue #157.
///
/// THE BUG (found in review round 1 of PR #173):
///   /register is an anonymous page, but its "Load from ARES" button called
///   GET /api/client/ares/{ico}. ClientController is [Authorize] at class level, so the
///   visitor — who has no account and therefore no JWT — always got 401. The Azure
///   Functions host had the same guard generated into ClientFunctions.Client_FetchFromAres.
///   Result: the company name never pre-filled, the registered office section never
///   populated, and acceptance criterion #2 of the issue could not be met.
///
/// THE FIX:
///   A dedicated anonymous endpoint GET /api/auth/ares/{ico} on AuthController, guarded
///   by the same reCAPTCHA v3 gate as login/register and returning only the narrow
///   AresLookupResponse (name + registered office). ClientController stays authenticated.
///
/// WHAT THESE TESTS PIN:
///   - the anonymous endpoint really is reachable without a token (the actual bug),
///   - it returns the registered office the form needs,
///   - the tenant-scoped ClientController endpoint stays behind [Authorize],
///   - malformed input never reaches the public ARES registry.
///
/// The registry itself is substituted — these tests must not depend on the network.
/// The live-registry contract is covered separately by AresAddressBoundaryTests.
/// </summary>
public class AnonymousAresLookupTests : IClassFixture<AnonymousAresLookupTests.AresStubFactory>
{
    private readonly AresStubFactory _factory;

    // Distinct IČOs per test — the substitute is shared by the class fixture, so
    // configuring different numbers keeps the tests independent of execution order.
    private const string FoundIco = "11111111";
    private const string NotFoundIco = "22222222";
    private const string MalformedIco = "12ab";

    public AnonymousAresLookupTests(AresStubFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The core regression test: an anonymous caller (a visitor of /register) must get
    /// the company data, not 401. Before the fix this route did not exist at all.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_AnonymousCaller_ReturnsCompanyWithRegisteredOffice()
    {
        // Arrange — no Authorization header is set anywhere in this test.
        _factory.Ares
            .GetCompanyInfoAsync(FoundIco, Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                IsSuccessful = true,
                RegistrationNumber = FoundIco,
                CompanyName = "Testovací firma s.r.o.",
                Address = new AresAddress
                {
                    Street = "Hlavní 123",
                    City = "Praha",
                    PostalCode = "120 00",
                    Country = "Česká republika"
                }
            });

        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/api/auth/ares/{FoundIco}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "the registration form calls this endpoint without a token");

        var payload = await response.Content.ReadFromJsonAsync<AresLookupResponse>();
        payload.ShouldNotBeNull();
        payload!.CompanyName.ShouldBe("Testovací firma s.r.o.");
        payload.Street.ShouldBe("Hlavní 123");
        payload.City.ShouldBe("Praha");
        payload.PostalCode.ShouldBe("120 00");
        payload.Country.ShouldBe("Česká republika");
    }

    /// <summary>
    /// The tenant-scoped proxy must NOT be opened up as a side effect of the fix.
    /// If someone points the registration form back at it, this test fails first.
    /// </summary>
    [Fact]
    public async Task ClientAresEndpoint_AnonymousCaller_StaysUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/client/ares/{FoundIco}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            "ClientController is tenant-scoped and must keep requiring a JWT");
    }

    /// <summary>
    /// Fail fast at the boundary: garbage input is rejected before any outbound call,
    /// so the anonymous endpoint cannot be used to hammer the public ARES registry.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_MalformedIco_ReturnsBadRequestWithoutCallingRegistry()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/ares/{MalformedIco}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.Ares.DidNotReceive()
            .GetCompanyInfoAsync(MalformedIco, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An unknown IČO is a normal outcome — the form shows "failed to load", so a 4xx
    /// (not a 500) is the right answer.
    /// </summary>
    [Fact]
    public async Task AuthAresEndpoint_CompanyNotFound_ReturnsBadRequest()
    {
        _factory.Ares
            .GetCompanyInfoAsync(NotFoundIco, Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                IsSuccessful = false,
                ErrorMessage = "Company not found in registry (HTTP NotFound)"
            });

        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/auth/ares/{NotFoundIco}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Test host with the ARES registry replaced by a substitute — no network access.
    /// Everything else (routing, authentication, authorization) is the real pipeline,
    /// which is exactly what these tests are about.
    /// </summary>
    public class AresStubFactory : FakvioFactory
    {
        /// <summary>The substituted registry — configure it per test.</summary>
        public IAresService Ares { get; } = Substitute.For<IAresService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                // AddHttpClient<IAresService, AresServiceImpl>() registers a typed client;
                // RemoveAll drops it so no real HTTP call can ever leave the test host.
                services.RemoveAll<IAresService>();
                services.AddSingleton(Ares);
            });
        }
    }
}
