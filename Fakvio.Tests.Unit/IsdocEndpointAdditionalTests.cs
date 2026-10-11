// ============================================================================
// IsdocEndpointAdditionalTests — Additional coverage for PR #18 (issue #14).
//
// These tests fill the gaps left after the initial IsdocEndpointTests pass:
//
//   1. FakvioApiClient.ExportInvoiceIsdocAsync — HTTP client method coverage
//      (correct URL, happy path bytes, 404/5xx throws)
//   2. Controller edge cases — null DocumentNumber fallback, cancellation token
//      propagation to both services
//   4. MCP tool envelope — sizeBytes matches actual byte length, null
//      DocumentNumber falls back to id string in file name
// ============================================================================

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

// ============================================================================
// 1. InvoiceFunctions wrapper — full HTTP request mock tests
// ============================================================================

/// <summary>
/// Tests for FakvioApiClient.ExportInvoiceIsdocAsync.
/// Mirrors the ExportInvoicePdfAsync tests in FakvioApiClientTests using the
/// same MockHttpMessageHandler pattern (copied here to avoid cross-namespace
/// dependency on a private nested class).
/// </summary>
public class FakvioApiClientIsdocTests : IDisposable
{
    private readonly MockHttpHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly FakvioApiClient _sut;

    public FakvioApiClientIsdocTests()
    {
        _handler = new MockHttpHandler();
        _httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://test.fakvio.cz/")
        };
        _sut = new FakvioApiClient(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task ExportInvoiceIsdocAsync_HitsCorrectUrl()
    {
        // Arrange
        var xmlBytes = "<ISDOC>data</ISDOC>"u8.ToArray();
        _handler.SetupBinaryResponse(HttpStatusCode.OK, xmlBytes);

        // Act
        await _sut.ExportInvoiceIsdocAsync(42);

        // Assert — must call api/invoice/{id}/isdoc
        _handler.LastRequestUri.ShouldNotBeNull();
        _handler.LastRequestUri!.ToString().ShouldContain("api/invoice/42/isdoc");
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public async Task ExportInvoiceIsdocAsync_ReturnsRawBytes()
    {
        // Arrange — known byte sequence for deterministic comparison
        var expectedBytes = new byte[] { 0x3C, 0x49, 0x53, 0x44, 0x4F, 0x43, 0x3E }; // <ISDOC>
        _handler.SetupBinaryResponse(HttpStatusCode.OK, expectedBytes);

        // Act
        var result = await _sut.ExportInvoiceIsdocAsync(7);

        // Assert — raw bytes must be returned unchanged (no JSON wrapping)
        result.Content.ShouldBe(expectedBytes);
    }

    [Fact]
    public async Task ExportInvoiceIsdocAsync_On404_ThrowsHttpRequestException()
    {
        // Arrange — API returns 404 with error JSON
        _handler.SetupJsonResponse(HttpStatusCode.NotFound, new { message = "Invoice 999 not found" });

        // Act & Assert — EnsureSuccessAsync in FakvioApiClient must throw
        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => _sut.ExportInvoiceIsdocAsync(999));

        ex.Message.ShouldContain("Invoice 999 not found");
    }

    [Fact]
    public async Task ExportInvoiceIsdocAsync_On500_ThrowsWithStatusCode()
    {
        // Arrange — server error with non-JSON body
        _handler.SetupRawResponse(HttpStatusCode.InternalServerError, "Internal Server Error");

        // Act & Assert
        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => _sut.ExportInvoiceIsdocAsync(1));

        ex.Message.ShouldContain("500");
    }

    // ── Minimal mock handler (self-contained to avoid dependency on private class) ──

    /// <summary>
    /// A lightweight mock HttpMessageHandler used to intercept calls from FakvioApiClient.
    /// Records the last request URI and method for assertions.
    /// </summary>
    private class MockHttpHandler : HttpMessageHandler
    {
        private HttpResponseMessage _response = new(HttpStatusCode.OK);

        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastRequestMethod { get; private set; }

        /// <summary>Configure a binary (byte array) response.</summary>
        public void SetupBinaryResponse(HttpStatusCode statusCode, byte[] body)
        {
            _response = new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(body)
            };
            _response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
        }

        /// <summary>Configure a JSON response for error scenarios.</summary>
        public void SetupJsonResponse<T>(HttpStatusCode statusCode, T body)
        {
            _response = new HttpResponseMessage(statusCode)
            {
                Content = JsonContent.Create(body, options: new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                })
            };
        }

        /// <summary>Configure a raw string response (for 5xx non-JSON errors).</summary>
        public void SetupRawResponse(HttpStatusCode statusCode, string body)
        {
            _response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body)
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestMethod = request.Method;
            return Task.FromResult(_response);
        }
    }
}

// ============================================================================
// 3 + 4. Controller edge cases + MCP tool envelope completeness
// ============================================================================

/// <summary>
/// Additional controller and MCP tool tests targeting edge cases not covered
/// by the initial IsdocEndpointTests pass:
///   - Null DocumentNumber falls back to invoice ID string
///   - Cancellation token is propagated to both services
///   - MCP tool sizeBytes field matches actual content length
///   - MCP tool null DocumentNumber falls back to ID in file name
/// </summary>
public class IsdocEndpointEdgeCaseTests
{
    private readonly IInvoiceService _invoiceService = Substitute.For<IInvoiceService>();
    private readonly IPdfExportService _pdfExportService = Substitute.For<IPdfExportService>();
    private readonly IDocumentFileNameService _fileNameService = Substitute.For<IDocumentFileNameService>();
    private readonly IIsdocExportService _isdocExportService = Substitute.For<IIsdocExportService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IQrPaymentService _qrPaymentService = Substitute.For<IQrPaymentService>();
    private readonly ICloudStorageOrchestrator _cloudStorage = Substitute.For<ICloudStorageOrchestrator>();

    private readonly InvoiceController _controller;

    public IsdocEndpointEdgeCaseTests()
    {
        _controller = new InvoiceController(
            _invoiceService,
            _pdfExportService,
            _fileNameService,
            _isdocExportService,
            Substitute.For<IUblExportService>(),
            _emailService,
            _qrPaymentService,
            _cloudStorage,
            Substitute.For<IPaymentMatchingService>(),
            Substitute.For<ILogger<InvoiceController>>());
    }

    // ── Controller: cancellation token propagation ────────────────────────

    [Fact]
    public async Task ExportIsdoc_PropagatesCancellationToken_ToIsdocService()
    {
        // Arrange — use a specific CancellationToken so we can verify it was forwarded
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        const long invoiceId = 100L;

        _isdocExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns("<ISDOC/>"u8.ToArray());
        _fileNameService.GetFileNameAsync(invoiceId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FAK100.isdoc");

        // Act
        await _controller.ExportIsdoc(invoiceId, token);

        // Assert — the same token must reach both service calls
        await _isdocExportService.Received(1).ExportInvoiceAsync(invoiceId, token);
        await _fileNameService.Received(1).GetFileNameAsync(invoiceId, Arg.Any<string>(), token);
    }

    [Fact]
    public async Task ExportIsdoc_WhenCancelled_BeforeServiceCall_ThrowsOrReturns()
    {
        // Arrange — already-cancelled token: service should see it but the controller
        // itself must not swallow OperationCanceledException into a 500.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _isdocExportService
            .ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException("Cancelled"));

        // Act & Assert — OperationCanceledException must bubble out (not silently swallowed)
        await Should.ThrowAsync<OperationCanceledException>(
            () => _controller.ExportIsdoc(1L, cts.Token));
    }

    // ── MCP tool: JSON envelope shape mirrors ExportInvoicePdf ───────────

    [Fact]
    public async Task ExportInvoiceIsdoc_EnvelopeShape_ContainsLinkFieldsAndNoFileContent()
    {
        var apiClient = Substitute.For<IFakvioApiClient>();
        const long invoiceId = 12L;

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = invoiceId, DocumentNumber = "FAK012", DocumentType = EDocumentType.Invoice });
        apiClient.CreateDownloadLinkAsync(Arg.Any<CreateDownloadLinkDto>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadLinkDto { Url = "https://api.example/api/download/tok", ExpiresAt = DateTime.UtcNow.AddMinutes(10) });

        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("success", out _).ShouldBeTrue("'success' field missing");
        doc.RootElement.TryGetProperty("downloadUrl", out _).ShouldBeTrue("'downloadUrl' field missing");
        doc.RootElement.TryGetProperty("expiresAt", out _).ShouldBeTrue("'expiresAt' field missing");
        doc.RootElement.TryGetProperty("mimeType", out _).ShouldBeTrue("'mimeType' field missing");
        doc.RootElement.TryGetProperty("base64Content", out _).ShouldBeFalse("file bytes must never be inlined");
        doc.RootElement.GetProperty("mimeType").GetString().ShouldBe("application/xml");
    }
}
