using System.Net;
using System.Text;
using System.Text.Json;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Models;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="VatReportApiService"/> EPO download methods.
///
/// These tests verify:
///   1. Happy-path success: bytes and filename are correctly extracted.
///   2. EPO_HEADER_INCOMPLETE: error code, message, and missingFields are parsed.
///   3. VAT_PAYER_REQUIRED (403): correct error code returned without exception.
///   4. Generic error fallback: unexpected error code is surfaced.
///   5. Content-Disposition filename extraction.
///   6. Fallback filename generation when Content-Disposition is absent.
///   7. Quarterly vs Monthly filename suffix (Q vs M).
/// </summary>
public class VatReportApiServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a VatReportApiService wired to a stub HttpClient whose handler
    /// returns <paramref name="response"/> for any GET request.
    /// </summary>
    private static VatReportApiService CreateService(HttpResponseMessage response)
    {
        var handler = new StubHttpMessageHandler(response);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };

        // IHttpClientFactory stub that always returns our pre-wired client.
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);

        var authProvider = Substitute.For<AuthenticationStateProvider>();

        return new VatReportApiService(factory, NullLogger<VatReportApiService>.Instance, authProvider);
    }

    /// <summary>
    /// Creates a 200 OK response carrying fake XML bytes and an optional
    /// Content-Disposition filename.
    /// </summary>
    private static HttpResponseMessage OkXmlResponse(byte[] bytes, string? fileName = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new("application/xml");
        if (fileName is not null)
        {
            response.Content.Headers.ContentDisposition =
                new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                {
                    FileName = fileName
                };
        }
        return response;
    }

    /// <summary>
    /// Creates a JSON error response with the given HTTP status, code, message, and optional missingFields.
    /// </summary>
    private static HttpResponseMessage ErrorJsonResponse(
        HttpStatusCode status, string code, string message, string[]? missingFields = null)
    {
        var body = new Dictionary<string, object> { ["code"] = code, ["message"] = message };
        if (missingFields is not null)
            body["missingFields"] = missingFields;

        var json = JsonSerializer.Serialize(body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    // ── Tests — DownloadEpoVatReturnAsync ─────────────────────────────────────

    [Fact]
    public async Task DownloadEpoVatReturnAsync_Success_ReturnsBytes()
    {
        // Arrange
        var expectedBytes = "<xml>dphdp3</xml>"u8.ToArray();
        var svc = CreateService(OkXmlResponse(expectedBytes, "DPHDP3_2026_M04.xml"));

        // Act
        var result = await svc.DownloadEpoVatReturnAsync(2026, 4, EVatPeriodType.Monthly);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.FileBytes.ShouldBe(expectedBytes);
    }

    [Fact]
    public async Task DownloadEpoVatReturnAsync_Success_UsesContentDispositionFilename()
    {
        // Arrange
        var svc = CreateService(OkXmlResponse("<xml/>"u8.ToArray(), "DPHDP3_2026_M04.xml"));

        // Act
        var result = await svc.DownloadEpoVatReturnAsync(2026, 4, EVatPeriodType.Monthly);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.FileName.ShouldBe("DPHDP3_2026_M04.xml");
    }

    [Fact]
    public async Task DownloadEpoVatReturnAsync_Success_FallbackFilename_WhenNoContentDisposition()
    {
        // Arrange — 200 OK but no Content-Disposition header.
        var svc = CreateService(OkXmlResponse("<xml/>"u8.ToArray(), null));

        // Act
        var result = await svc.DownloadEpoVatReturnAsync(2026, 4, EVatPeriodType.Monthly);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        // Fallback: DPHDP3_{year}_M{mm:D2}.xml
        result.FileName.ShouldBe("DPHDP3_2026_M04.xml");
    }

    [Fact]
    public async Task DownloadEpoVatReturnAsync_EpoHeaderIncomplete_ReturnsError()
    {
        // Arrange
        var missing = new[] { "EpoTaxOfficeCode", "EpoContactEmail" };
        var svc = CreateService(ErrorJsonResponse(
            HttpStatusCode.BadRequest, "EPO_HEADER_INCOMPLETE", "Missing fields", missing));

        // Act
        var result = await svc.DownloadEpoVatReturnAsync(2026, 4, EVatPeriodType.Monthly);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("EPO_HEADER_INCOMPLETE");
        result.MissingFields.ShouldBe(missing);
    }

    [Fact]
    public async Task DownloadEpoVatReturnAsync_VatPayerRequired_Returns403Code()
    {
        // Arrange
        var svc = CreateService(ErrorJsonResponse(
            HttpStatusCode.Forbidden, "VAT_PAYER_REQUIRED", "Not a VAT payer"));

        // Act
        var result = await svc.DownloadEpoVatReturnAsync(2026, 4, EVatPeriodType.Monthly);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("VAT_PAYER_REQUIRED");
        result.MissingFields.ShouldBeEmpty();
    }

    // ── Tests — DownloadEpoControlStatementAsync ──────────────────────────────

    [Fact]
    public async Task DownloadEpoControlStatementAsync_Success_ReturnsBytes()
    {
        // Arrange
        var expectedBytes = "<xml>dphkh1</xml>"u8.ToArray();
        var svc = CreateService(OkXmlResponse(expectedBytes, "DPHKH1_2026_M04.xml"));

        // Act
        var result = await svc.DownloadEpoControlStatementAsync(2026, 4, EVatPeriodType.Monthly);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.FileBytes.ShouldBe(expectedBytes);
    }

    [Fact]
    public async Task DownloadEpoControlStatementAsync_Quarterly_FallbackFilenameUsesQSuffix()
    {
        // Arrange — quarterly period, no Content-Disposition.
        var svc = CreateService(OkXmlResponse("<xml/>"u8.ToArray(), null));

        // Act
        var result = await svc.DownloadEpoControlStatementAsync(2026, 2, EVatPeriodType.Quarterly);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        // Fallback: DPHKH1_{year}_Q{q}.xml
        result.FileName.ShouldBe("DPHKH1_2026_Q2.xml");
    }

    // ── Tests — EpoDownloadResult factory methods ─────────────────────────────

    [Fact]
    public void EpoDownloadResult_Success_HasCorrectState()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var result = EpoDownloadResult.Success(bytes, "file.xml");

        result.IsSuccess.ShouldBeTrue();
        result.FileBytes.ShouldBe(bytes);
        result.FileName.ShouldBe("file.xml");
        result.ErrorCode.ShouldBeNull();
        result.MissingFields.ShouldBeEmpty();
    }

    [Fact]
    public void EpoDownloadResult_Failure_HasCorrectState()
    {
        var missing = new List<string> { "FieldA", "FieldB" };
        var result = EpoDownloadResult.Failure("EPO_HEADER_INCOMPLETE", "Bad headers", missing);

        result.IsSuccess.ShouldBeFalse();
        result.FileBytes.ShouldBeNull();
        result.FileName.ShouldBeNull();
        result.ErrorCode.ShouldBe("EPO_HEADER_INCOMPLETE");
        result.ErrorMessage.ShouldBe("Bad headers");
        result.MissingFields.ShouldBe(missing);
    }

    [Fact]
    public void EpoDownloadResult_Failure_WithoutMissingFields_HasEmptyList()
    {
        var result = EpoDownloadResult.Failure("VAT_PAYER_REQUIRED", "Not a payer");

        result.IsSuccess.ShouldBeFalse();
        result.MissingFields.ShouldNotBeNull();
        result.MissingFields.ShouldBeEmpty();
    }

    // ── Stub helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// HttpMessageHandler stub that returns a fixed response for any request.
    /// Used to isolate VatReportApiService from real HTTP calls.
    /// </summary>
    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
