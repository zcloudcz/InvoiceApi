using System.Net;
using AresService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Pins the rule <see cref="AresServiceImpl"/> uses to decide whether a company is a
/// VAT payer: the registry payload carries a DIČ ("dic") or it does not.
///
/// WHY THIS FILE EXISTS
///   Two places create a company from an ARES lookup and both copy the flag rather than
///   deriving it again — CompanyController.CreateCompany (SysAdmin) and
///   AuthService.RegisterAsync (self-registration, issue #208). Their acceptance criteria
///   are written in terms of the DIČ, so the derivation itself needs a test; otherwise a
///   change here would silently flip newly registered companies to the wrong VAT regime
///   with every consumer test still green.
///
/// The HTTP layer is stubbed — these tests never touch the network.
/// </summary>
public class AresVatPayerDerivationTests
{
    private const string ValidIco = "12345678";

    [Fact]
    public async Task RefreshCompanyInfo_PayloadCarriesDic_IsVatPayerAndTaxNumberAreSet()
    {
        var service = CreateService(
            """{"obchodniJmeno":"Plátce s.r.o.","dic":"CZ12345678"}""");

        var result = await service.RefreshCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeTrue();
        result.TaxNumber.ShouldBe("CZ12345678");
        result.IsVatPayer.ShouldBeTrue();
    }

    [Fact]
    public async Task RefreshCompanyInfo_PayloadHasNoDic_IsNotAVatPayer()
    {
        var service = CreateService(
            """{"obchodniJmeno":"Neplátce s.r.o."}""");

        var result = await service.RefreshCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeTrue();
        result.TaxNumber.ShouldBeNull();
        result.IsVatPayer.ShouldBeFalse();
    }

    /// <summary>
    /// A company deregistered from VAT keeps its entry in ARES, but the "dic" element is
    /// serialized as JSON null. That must read as "not a VAT payer", not as a DIČ.
    /// </summary>
    [Fact]
    public async Task RefreshCompanyInfo_DicIsJsonNull_IsNotAVatPayer()
    {
        var service = CreateService(
            """{"obchodniJmeno":"Bývalý plátce s.r.o.","dic":null}""");

        var result = await service.RefreshCompanyInfoAsync(ValidIco);

        result.IsSuccessful.ShouldBeTrue();
        result.TaxNumber.ShouldBeNull();
        result.IsVatPayer.ShouldBeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the service over a stubbed registry answering with the given JSON body.
    /// The cache substitute stores nothing, so every call parses the payload.
    /// </summary>
    private static AresServiceImpl CreateService(string aresJson)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        return new AresServiceImpl(
            new HttpClient(new StubHttpMessageHandler(aresJson)),
            Substitute.For<IAresCacheRepository>(),
            Substitute.For<ILogger<AresServiceImpl>>(),
            configuration);
    }

    /// <summary>
    /// HttpMessageHandler stub answering every request with HTTP 200 and the given JSON body.
    ///
    /// It builds a NEW response per request on purpose. A response content stream can only be
    /// read once, so handing out one shared instance would make a second call (a retry, or a
    /// future test that looks up twice) fail on an empty body instead of on the real assertion.
    /// </summary>
    private sealed class StubHttpMessageHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
    }
}
