using System.Net;
using System.Net.Http.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Vies;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>GET api/vies/{vatId}: requires auth, returns the service result (VIES itself is stubbed).</summary>
public class ViesEndpointTests : IClassFixture<ViesEndpointTests.ViesStubFactory>
{
    public sealed class ViesStubFactory : FakvioFactory
    {
        public IViesService Vies { get; } = Substitute.For<IViesService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                // Drop the typed HttpClient registration so no real call can leave the test host.
                s.RemoveAll<IViesService>();
                s.AddSingleton(Vies);
            });
        }
    }

    private readonly ViesStubFactory _factory;

    public ViesEndpointTests(ViesStubFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync("/api/vies/CZ12345678");
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticated_ReturnsServiceResult()
    {
        _factory.Vies.VerifyAsync("CZ12345678", Arg.Any<CancellationToken>())
            .Returns(new ViesVerificationResult { Status = EViesCheckStatus.Valid, CountryCode = "CZ", VatNumber = "12345678", Name = "ACME" });
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);

        var response = await client.GetAsync("/api/vies/CZ12345678");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ViesVerificationResult>();
        body!.Valid.ShouldBeTrue();
        body.Name.ShouldBe("ACME");
    }
}
