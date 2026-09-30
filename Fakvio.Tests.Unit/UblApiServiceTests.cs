using System.Net;
using System.Text;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="FakvioService.ExportUblAsync"/> (ADR 0002, F1.6) — the HTTP-level
/// counterpart to <see cref="DownloadUblLogicTests"/>. Same stub-HttpMessageHandler approach as
/// <c>VatReportApiServiceTests</c>: the point is proving the 400 "TENANT_NOT_READY" body's
/// <c>issues</c> array survives the round-trip into <c>UblDownloadResult.Issues</c>, which a
/// plain <c>GetBytesAsync</c>/<c>ApiException</c> call (as used by <c>ExportIsdocAsync</c>)
/// would have lost.
/// </summary>
public class UblApiServiceTests
{
    private static FakvioService CreateService(HttpResponseMessage response)
    {
        var handler = new StubHttpMessageHandler(response);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);

        var authProvider = Substitute.For<AuthenticationStateProvider>();
        return new FakvioService(factory, NullLogger<FakvioService>.Instance, authProvider);
    }

    [Fact]
    public async Task ExportUblAsync_Success_ReturnsBytesAndFileName()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("<Invoice/>"u8.ToArray())
        };
        var service = CreateService(response);

        var result = await service.ExportUblAsync(42, "INV2026001.xml");

        result.IsSuccess.ShouldBeTrue();
        result.FileBytes.ShouldBe("<Invoice/>"u8.ToArray());
        result.FileName.ShouldBe("INV2026001.xml");
    }

    [Fact]
    public async Task ExportUblAsync_TenantNotReady_ParsesIssuesFromBody()
    {
        var body = """
            {
              "code": "TENANT_NOT_READY",
              "message": "Tenant is not ready. Unresolved blocking issue(s): EINVOICE_DRAFT.",
              "missingFields": [],
              "issues": [
                { "code": "EINVOICE_DRAFT", "severity": 1, "missingFields": [], "fixRoute": "" }
              ]
            }
            """;
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        var service = CreateService(response);

        var result = await service.ExportUblAsync(7, "INV2026007.xml");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("TENANT_NOT_READY");
        result.Issues.ShouldHaveSingleItem();
        result.Issues[0].Code.ShouldBe("EINVOICE_DRAFT");
    }

    [Fact]
    public async Task ExportUblAsync_NotFound_ReturnsNotFoundCode_NoIssues()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"message":"Invoice with ID 999 not found"}""", Encoding.UTF8, "application/json")
        };
        var service = CreateService(response);

        var result = await service.ExportUblAsync(999, "999.xml");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("NOT_FOUND");
        result.Issues.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExportUblAsync_MalformedBody_FallsBackToRawText_DoesNotThrow()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html")
        };
        var service = CreateService(response);

        var result = await service.ExportUblAsync(1, "1.xml");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("not json");
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
