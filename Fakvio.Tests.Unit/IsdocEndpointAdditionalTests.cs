// ============================================================================
// IsdocEndpointAdditionalTests — Additional coverage for PR #18 (issue #14).
//
// These tests fill the gaps left after the initial IsdocEndpointTests pass:
//
//   1. InvoiceFunctions.Invoice_ExportIsdoc — full HTTP request mock tests
//      (anonymous → 401, bad id → 400, happy path, cancellation token)
//   2. FakvioApiClient.ExportInvoiceIsdocAsync — HTTP client method coverage
//      (correct URL, happy path bytes, 404/5xx throws)
//   3. Controller edge cases — null DocumentNumber fallback, cancellation token
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
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.Functions.Generated;
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
/// Exercises Invoice_ExportIsdoc at the Functions layer using DefaultHttpContext,
/// matching the pattern established in PaymentMatchingFunctionsTests.
/// Verifies auth gating, route parameter parsing, and controller delegation.
/// </summary>
public class InvoiceFunctionsIsdocTests
{
    // ── Dependencies shared by all wrapper tests ───────────────────────────

    private readonly IInvoiceService _invoiceService = Substitute.For<IInvoiceService>();
    private readonly IPdfExportService _pdfExportService = Substitute.For<IPdfExportService>();
    private readonly IIsdocExportService _isdocExportService = Substitute.For<IIsdocExportService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IQrPaymentService _qrPaymentService = Substitute.For<IQrPaymentService>();
    private readonly ICloudStorageOrchestrator _cloudStorage = Substitute.For<ICloudStorageOrchestrator>();

    /// <summary>
    /// Creates a fresh (controller, wrapper) pair.
    /// The same controller instance is injected into the wrapper so we can
    /// verify the wrapper actually delegates to the real controller logic.
    /// </summary>
    private (InvoiceFunctions Sut, InvoiceController Controller) BuildSut()
    {
        var controller = new InvoiceController(
            _invoiceService,
            _pdfExportService,
            _isdocExportService,
            _emailService,
            _qrPaymentService,
            _cloudStorage,
            Substitute.For<IPaymentMatchingService>(),
            Substitute.For<ILogger<InvoiceController>>());

        var sut = new InvoiceFunctions(controller);
        return (sut, controller);
    }

    /// <summary>
    /// Builds an HttpRequest with an authenticated user (IsAuthenticated == true).
    /// </summary>
    private static HttpRequest BuildAuthenticatedRequest()
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("UserId", "1"), new Claim(ClaimTypes.NameIdentifier, "1") },
            authenticationType: "TestAuth")); // non-null authenticationType → IsAuthenticated = true
        return ctx.Request;
    }

    /// <summary>
    /// Builds an unauthenticated request (anonymous identity, IsAuthenticated == false).
    /// </summary>
    private static HttpRequest BuildAnonymousRequest()
    {
        // DefaultHttpContext has no user — IsAuthenticated defaults to false
        return new DefaultHttpContext().Request;
    }

    // ── Authorization gating ──────────────────────────────────────────────

    [Fact]
    public async Task Invoice_ExportIsdoc_Anonymous_Returns401()
    {
        // Arrange
        var (sut, _) = BuildSut();
        var req = BuildAnonymousRequest();

        // Act
        var result = await sut.Invoice_ExportIsdoc(req, "42");

        // Assert — no token → Unauthorized before any service is called
        result.ShouldBeOfType<UnauthorizedResult>();
        await _isdocExportService.DidNotReceiveWithAnyArgs()
            .ExportInvoiceAsync(default, default);
    }

    // ── Route parameter validation ────────────────────────────────────────

    [Fact]
    public async Task Invoice_ExportIsdoc_NonNumericId_Returns400()
    {
        // Arrange — Azure Functions routes always deliver `id` as string;
        // the wrapper must reject non-long values before calling the controller.
        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        // Act
        var result = await sut.Invoice_ExportIsdoc(req, "not-a-number");

        // Assert
        result.ShouldBeOfType<BadRequestObjectResult>();
        await _isdocExportService.DidNotReceiveWithAnyArgs()
            .ExportInvoiceAsync(default, default);
    }

    [Fact]
    public async Task Invoice_ExportIsdoc_NegativeId_Returns200OrFile()
    {
        // Arrange — negative longs are technically valid long.TryParse values.
        // The wrapper should parse them and delegate; it's the service that decides
        // whether -1 is a valid invoice ID (here it returns bytes for the test).
        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        var fakeBytes = "<ISDOC/>"u8.ToArray();
        _isdocExportService.ExportInvoiceAsync(-1L, Arg.Any<CancellationToken>())
            .Returns(fakeBytes);
        _invoiceService.GetInvoiceByIdAsync(-1L, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = -1, DocumentNumber = "X", DocumentType = EDocumentType.Invoice });

        // Act
        var result = await sut.Invoice_ExportIsdoc(req, "-1");

        // Assert — wrapper parsed the id and delegated; result is whatever the controller returned
        result.ShouldBeOfType<FileContentResult>();
    }

    // ── Happy-path controller delegation ─────────────────────────────────

    [Fact]
    public async Task Invoice_ExportIsdoc_Authenticated_DelegatesToController()
    {
        // Arrange — verify the wrapper wires up HttpContext and calls ExportIsdoc
        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        const long invoiceId = 55L;
        var fakeBytes = "<?xml version=\"1.0\"?><ISDOC/>"u8.ToArray();

        _isdocExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(fakeBytes);
        _invoiceService.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto
            {
                Id = invoiceId,
                DocumentNumber = "FAK2026055",
                DocumentType = EDocumentType.Invoice
            });

        // Act
        var result = await sut.Invoice_ExportIsdoc(req, invoiceId.ToString());

        // Assert — the wrapper must have forwarded to the controller
        await _isdocExportService.Received(1).ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>());
        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.ContentType.ShouldBe("application/xml");
        fileResult.FileDownloadName.ShouldBe("Invoice_FAK2026055.isdoc");
    }

    [Fact]
    public async Task Invoice_ExportIsdoc_ServiceThrowsKeyNotFound_Returns404()
    {
        // Arrange — service throws KeyNotFoundException → controller maps to 404
        var (sut, _) = BuildSut();
        var req = BuildAuthenticatedRequest();

        _isdocExportService.ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new KeyNotFoundException("Invoice 999 not found."));

        // Act
        var result = await sut.Invoice_ExportIsdoc(req, "999");

        // Assert
        result.ShouldBeOfType<NotFoundObjectResult>();
    }
}

// ============================================================================
// 2. FakvioApiClient.ExportInvoiceIsdocAsync — HTTP client method coverage
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
        result.ShouldBe(expectedBytes);
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
            _isdocExportService,
            _emailService,
            _qrPaymentService,
            _cloudStorage,
            Substitute.For<IPaymentMatchingService>(),
            Substitute.For<ILogger<InvoiceController>>());
    }

    // ── Controller: null DocumentNumber → fallback to id ─────────────────

    [Fact]
    public async Task ExportIsdoc_NullDocumentNumber_UsesIdAsFileName()
    {
        // Arrange — invoice exists but DocumentNumber is not yet assigned (draft)
        const long invoiceId = 77L;
        var fakeBytes = "<ISDOC/>"u8.ToArray();

        _isdocExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(fakeBytes);
        _invoiceService.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto
            {
                Id = invoiceId,
                DocumentNumber = null,          // <-- null: document number not yet set
                DocumentType = EDocumentType.Invoice
            });

        // Act
        var result = await _controller.ExportIsdoc(invoiceId, CancellationToken.None);

        // Assert — file name falls back to "Invoice_{id}.isdoc"
        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.FileDownloadName.ShouldBe($"Invoice_{invoiceId}.isdoc");
    }

    [Fact]
    public async Task ExportIsdoc_NullDocumentNumber_CreditNote_UsesIdWithCorrectPrefix()
    {
        // Arrange — credit note with null document number
        const long invoiceId = 88L;
        var fakeBytes = "<ISDOC/>"u8.ToArray();

        _isdocExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(fakeBytes);
        _invoiceService.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto
            {
                Id = invoiceId,
                DocumentNumber = null,
                DocumentType = EDocumentType.CreditNote
            });

        // Act
        var result = await _controller.ExportIsdoc(invoiceId, CancellationToken.None);

        // Assert — prefix must still be "CreditNote" even when DocumentNumber is null
        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.FileDownloadName.ShouldBe($"CreditNote_{invoiceId}.isdoc");
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
        _invoiceService.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = invoiceId, DocumentNumber = "FAK100", DocumentType = EDocumentType.Invoice });

        // Act
        await _controller.ExportIsdoc(invoiceId, token);

        // Assert — the same token must reach both service calls
        await _isdocExportService.Received(1).ExportInvoiceAsync(invoiceId, token);
        await _invoiceService.Received(1).GetInvoiceByIdAsync(invoiceId, token);
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

    // ── MCP tool: sizeBytes field matches actual byte length ─────────────

    [Fact]
    public async Task ExportInvoiceIsdoc_SizeBytes_MatchesActualByteLength()
    {
        // Arrange — use a known-size byte array
        var apiClient = Substitute.For<IFakvioApiClient>();
        const long invoiceId = 9L;
        var xmlBytes = new byte[137]; // arbitrary known length
        new Random(42).NextBytes(xmlBytes); // fill with pseudo-random bytes

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = invoiceId, DocumentNumber = "INV009", DocumentType = EDocumentType.Invoice });
        apiClient.ExportInvoiceIsdocAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(xmlBytes);

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        // Assert — sizeBytes must equal the actual byte array length
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("sizeBytes").GetInt32().ShouldBe(137);
    }

    [Fact]
    public async Task ExportInvoiceIsdoc_ZeroByteResponse_SizeIsZero()
    {
        // Edge case: API returns empty byte array (e.g. service bug returns nothing).
        // The tool should report sizeBytes = 0 and still return success JSON.
        var apiClient = Substitute.For<IFakvioApiClient>();
        const long invoiceId = 10L;
        var emptyBytes = Array.Empty<byte>();

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = invoiceId, DocumentNumber = "INV010", DocumentType = EDocumentType.Invoice });
        apiClient.ExportInvoiceIsdocAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(emptyBytes);

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("sizeBytes").GetInt32().ShouldBe(0);
        doc.RootElement.GetProperty("base64Content").GetString().ShouldBe(""); // empty base64
    }

    // ── MCP tool: null DocumentNumber → fallback to id in file name ──────

    [Fact]
    public async Task ExportInvoiceIsdoc_NullDocumentNumber_FallsBackToId()
    {
        // Arrange — invoice has no document number yet (draft state)
        var apiClient = Substitute.For<IFakvioApiClient>();
        const long invoiceId = 11L;
        var xmlBytes = "<ISDOC/>"u8.ToArray();

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = invoiceId, DocumentNumber = null, DocumentType = EDocumentType.Invoice });
        apiClient.ExportInvoiceIsdocAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(xmlBytes);

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        // Assert — file name must fall back to "Invoice_11.isdoc"
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("fileName").GetString().ShouldBe($"Invoice_{invoiceId}.isdoc");
    }

    // ── MCP tool: JSON envelope shape mirrors ExportInvoicePdf ───────────

    [Fact]
    public async Task ExportInvoiceIsdoc_EnvelopeShape_ContainsAllRequiredFields()
    {
        // Arrange — verify every field that ExportInvoicePdf returns is also present
        // in ExportInvoiceIsdoc (mirrors the sibling tool for consistency).
        var apiClient = Substitute.For<IFakvioApiClient>();
        const long invoiceId = 12L;
        var xmlBytes = "<?xml?>"u8.ToArray();

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = invoiceId, DocumentNumber = "FAK012", DocumentType = EDocumentType.Invoice });
        apiClient.ExportInvoiceIsdocAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(xmlBytes);

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        // Assert — all five fields from ExportInvoicePdf envelope must exist
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("success", out _).ShouldBeTrue("'success' field missing");
        doc.RootElement.TryGetProperty("fileName", out _).ShouldBeTrue("'fileName' field missing");
        doc.RootElement.TryGetProperty("mimeType", out _).ShouldBeTrue("'mimeType' field missing");
        doc.RootElement.TryGetProperty("sizeBytes", out _).ShouldBeTrue("'sizeBytes' field missing");
        doc.RootElement.TryGetProperty("base64Content", out _).ShouldBeTrue("'base64Content' field missing");

        // ISDOC-specific: mimeType must be application/xml (not application/pdf)
        doc.RootElement.GetProperty("mimeType").GetString().ShouldBe("application/xml");
        // file extension must be .isdoc (not .pdf)
        doc.RootElement.GetProperty("fileName").GetString().ShouldEndWith(".isdoc");
    }
}
