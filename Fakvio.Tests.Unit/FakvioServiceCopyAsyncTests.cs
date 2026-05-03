using System.Net;
using System.Text;
using System.Text.Json;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="FakvioService.CopyAsync"/>.
///
/// CopyAsync calls POST /api/invoice/{id}/copy (no body) via PostWithoutBodyAsync.
/// Tested behaviors:
///   1. 200 OK — returned DTO fields are deserialized correctly.
///   2. URL — the POST is sent to the correct endpoint including the invoice ID.
///   3. 404 Not Found — ApiException is propagated to the caller (not swallowed).
///   4. 500 Internal Server Error — ApiException is propagated to the caller.
/// </summary>
public class FakvioServiceCopyAsyncTests
{
    // ── Factory helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="FakvioService"/> whose HTTP client returns the given response
    /// for any request. The capturing handler records the last outgoing URI.
    /// </summary>
    private static (FakvioService service, CapturingHttpMessageHandler handler) CreateService(
        HttpResponseMessage response)
    {
        var capturingHandler = new CapturingHttpMessageHandler(response);
        var httpClient = new HttpClient(capturingHandler) { BaseAddress = new Uri("https://test.local") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);

        var authProvider = Substitute.For<AuthenticationStateProvider>();

        var svc = new FakvioService(factory, NullLogger<FakvioService>.Instance, authProvider);
        return (svc, capturingHandler);
    }

    /// <summary>Creates a 200 OK response containing <paramref name="dto"/> serialized as JSON.</summary>
    private static HttpResponseMessage OkDtoResponse(InvoiceDto dto)
    {
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            // Match the casing the real API uses (camelCase from System.Text.Json defaults).
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>Creates a JSON error response with the given status and message.</summary>
    private static HttpResponseMessage ErrorJsonResponse(HttpStatusCode status, string message)
    {
        var body = JsonSerializer.Serialize(new { message });
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_200Ok_ReturnsDeserializedDto()
    {
        // Arrange — server returns the newly created copy as a Draft invoice.
        var expected = new InvoiceDto
        {
            Id = 99,
            DocumentNumber = "FV2026-099",
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Draft
        };
        var (svc, _) = CreateService(OkDtoResponse(expected));

        // Act
        var result = await svc.CopyAsync(42);

        // Assert — all key fields round-tripped correctly.
        result.ShouldNotBeNull();
        result!.Id.ShouldBe(expected.Id);
        result.DocumentNumber.ShouldBe(expected.DocumentNumber);
        result.DocumentType.ShouldBe(EDocumentType.Invoice);
        result.Status.ShouldBe(EInvoiceStatus.Draft);
    }

    // ── URL construction ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1L)]
    [InlineData(42L)]
    [InlineData(999L)]
    public async Task CopyAsync_PostsToCorrectEndpoint(long invoiceId)
    {
        // Arrange
        var dto = new InvoiceDto { Id = invoiceId + 100, DocumentNumber = "FV-NEW" };
        var (svc, handler) = CreateService(OkDtoResponse(dto));

        // Act
        await svc.CopyAsync(invoiceId);

        // Assert — POST must target /api/invoice/{id}/copy.
        handler.LastRequestUri.ShouldNotBeNull();
        handler.LastRequestUri!.AbsolutePath.ShouldBe($"/api/invoice/{invoiceId}/copy");
        handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    // ── Error paths ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_404NotFound_ThrowsApiException()
    {
        // Arrange — backend returns 404 when the invoice does not exist.
        var (svc, _) = CreateService(ErrorJsonResponse(
            HttpStatusCode.NotFound, "Invoice with ID 999 not found"));

        // Act + Assert — ApiException must propagate; CopyAsync does NOT swallow it.
        await Should.ThrowAsync<ApiException>(async () => await svc.CopyAsync(999));
    }

    [Fact]
    public async Task CopyAsync_500InternalServerError_ThrowsApiException()
    {
        // Arrange — unexpected server failure.
        var (svc, _) = CreateService(ErrorJsonResponse(
            HttpStatusCode.InternalServerError, "Unexpected error during copy"));

        // Act + Assert — 5xx is treated the same as 4xx: ApiException is re-thrown.
        await Should.ThrowAsync<ApiException>(async () => await svc.CopyAsync(1));
    }

    [Fact]
    public async Task CopyAsync_404NotFound_ExceptionCarriesStatusCode()
    {
        // Arrange
        var (svc, _) = CreateService(ErrorJsonResponse(
            HttpStatusCode.NotFound, "Not found"));

        // Act
        var ex = await Should.ThrowAsync<ApiException>(async () => await svc.CopyAsync(7));

        // Assert — callers can inspect StatusCode to decide on toast wording.
        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Stub infrastructure ───────────────────────────────────────────────────

    /// <summary>
    /// Captures the last outgoing request URI and HTTP method while returning a fixed response.
    /// Allows tests to verify URL construction without real HTTP calls.
    /// </summary>
    private sealed class CapturingHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public HttpMethod? LastRequestMethod { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestMethod = request.Method;
            return Task.FromResult(response);
        }
    }
}
