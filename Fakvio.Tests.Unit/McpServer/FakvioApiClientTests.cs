using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for FakvioApiClient — the typed HttpClient wrapper.
/// Uses a MockHttpMessageHandler to intercept HTTP calls and return
/// controlled responses without hitting a real API.
///
/// Junior note: We can't easily mock HttpClient directly because it's
/// a concrete class. Instead, we mock the underlying HttpMessageHandler
/// which processes all HTTP requests. This gives us full control over
/// what the client "sees" as API responses.
/// </summary>
public class FakvioApiClientTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly MockHttpMessageHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly FakvioApiClient _sut; // SUT = System Under Test

    public FakvioApiClientTests()
    {
        _handler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://test-api.fakvio.cz/")
        };
        _sut = new FakvioApiClient(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    // ── Invoice tests ──────────────────────────────────────────────────

    [Fact]
    public async Task GetInvoicesPagedAsync_BuildsCorrectQueryString()
    {
        // Arrange: set up a filter with multiple properties
        var filter = new InvoiceFilterDto
        {
            Page = 2,
            PageSize = 10,
            Search = "test",
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            IsOverdue = true
        };

        var expectedResult = new PagedResult<InvoiceDto>(
            [new InvoiceDto { Id = 1, DocumentNumber = "FAK001" }],
            totalCount: 1, pageNumber: 2, pageSize: 10);

        _handler.SetupResponse(HttpStatusCode.OK, expectedResult);

        // Act
        var result = await _sut.GetInvoicesPagedAsync(filter);

        // Assert: verify the request URL contains all filter params
        var requestUrl = _handler.LastRequestUri?.ToString() ?? "";
        requestUrl.ShouldContain("page=2");
        requestUrl.ShouldContain("pageSize=10");
        requestUrl.ShouldContain("search=test");
        requestUrl.ShouldContain("documentType=Invoice");
        requestUrl.ShouldContain("status=Completed");
        requestUrl.ShouldContain("isOverdue=true");

        result.Items.Count.ShouldBe(1);
        result.Items[0].DocumentNumber.ShouldBe("FAK001");
    }

    [Fact]
    public async Task GetInvoicesPagedAsync_DefaultFilter_MinimalQueryString()
    {
        // Arrange: empty filter should produce minimal query string
        var filter = new InvoiceFilterDto();
        _handler.SetupResponse(HttpStatusCode.OK, new PagedResult<InvoiceDto>());

        // Act
        await _sut.GetInvoicesPagedAsync(filter);

        // Assert: default values should not appear in query string
        var requestUrl = _handler.LastRequestUri?.ToString() ?? "";
        requestUrl.ShouldNotContain("page=");
        requestUrl.ShouldNotContain("search=");
    }

    [Fact]
    public async Task GetInvoiceByIdAsync_ReturnsInvoice_WhenFound()
    {
        // Arrange
        var invoice = new InvoiceDto { Id = 42, DocumentNumber = "FAK2026001" };
        _handler.SetupResponse(HttpStatusCode.OK, invoice);

        // Act
        var result = await _sut.GetInvoiceByIdAsync(42);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(42);
        result.DocumentNumber.ShouldBe("FAK2026001");
    }

    [Fact]
    public async Task GetInvoiceByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange: 404 response
        _handler.SetupResponse(HttpStatusCode.NotFound, new { message = "Not found" });

        // Act
        var result = await _sut.GetInvoiceByIdAsync(999);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetInvoiceByDocumentNumberAsync_EncodesSpecialCharacters()
    {
        // Arrange
        _handler.SetupResponse(HttpStatusCode.NotFound, new { message = "Not found" });

        // Act
        await _sut.GetInvoiceByDocumentNumberAsync("FAK/2026-001");

        // Assert: verify URL encoding
        var requestUrl = _handler.LastRequestUri?.ToString() ?? "";
        requestUrl.ShouldContain("by-number/FAK%2F2026-001");
    }

    [Fact]
    public async Task CreateInvoiceAsync_SendsPostRequest()
    {
        // Arrange
        var dto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = 1,
            IssuerId = 2,
            CurrencyId = 1,
            InvoiceItem = [new CreateInvoiceItemDto { Description = "Test", Quantity = 1, UnitPrice = 100, VatRatePercentage = 21 }]
        };

        var expected = new InvoiceDto { Id = 10, DocumentNumber = null };
        _handler.SetupResponse(HttpStatusCode.Created, expected);

        // Act
        var result = await _sut.CreateInvoiceAsync(dto);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(10);
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task CompleteInvoiceAsync_SendsPostToCorrectUrl()
    {
        // Arrange
        _handler.SetupResponse(HttpStatusCode.OK, new InvoiceDto { Id = 5, Status = EInvoiceStatus.Completed });

        // Act
        var result = await _sut.CompleteInvoiceAsync(5);

        // Assert
        result.Status.ShouldBe(EInvoiceStatus.Completed);
        _handler.LastRequestUri?.ToString().ShouldContain("api/invoice/5/complete");
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task DeleteInvoiceAsync_SendsDeleteRequest()
    {
        // Arrange
        _handler.SetupResponse(HttpStatusCode.NoContent);

        // Act & Assert: should not throw
        await _sut.DeleteInvoiceAsync(7);

        _handler.LastRequestMethod.ShouldBe(HttpMethod.Delete);
        _handler.LastRequestUri?.ToString().ShouldContain("api/invoice/7");
    }

    [Fact]
    public async Task SendInvoiceEmailAsync_SendsCorrectPayload()
    {
        // Arrange
        var emailDto = new SendInvoiceEmailDto
        {
            RecipientEmail = "test@example.com",
            Subject = "Your Invoice",
            Message = "Please find attached"
        };
        _handler.SetupResponse(HttpStatusCode.OK);

        // Act
        await _sut.SendInvoiceEmailAsync(3, emailDto);

        // Assert
        _handler.LastRequestUri?.ToString().ShouldContain("api/invoice/3/send-email");
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }

    // ── Client tests ───────────────────────────────────────────────────

    [Fact]
    public async Task GetClientsPagedAsync_BuildsClientFilterQuery()
    {
        // Arrange
        var filter = new ClientFilterDto
        {
            Page = 1,
            PageSize = 25,
            Search = "Acme",
            IsVatPayer = true,
            IncludeInactive = true
        };
        _handler.SetupResponse(HttpStatusCode.OK, new PagedResult<ClientDto>());

        // Act
        await _sut.GetClientsPagedAsync(filter);

        // Assert
        var url = _handler.LastRequestUri?.ToString() ?? "";
        url.ShouldContain("search=Acme");
        url.ShouldContain("isVatPayer=true");
        url.ShouldContain("includeInactive=true");
    }

    [Fact]
    public async Task GetIssuerAsync_ReturnsIssuer()
    {
        // Arrange
        var issuer = new ClientDto { Id = 1, CompanyName = "My Company", IsIssuer = true };
        _handler.SetupResponse(HttpStatusCode.OK, issuer);

        // Act
        var result = await _sut.GetIssuerAsync();

        // Assert
        result.ShouldNotBeNull();
        result.CompanyName.ShouldBe("My Company");
        result.IsIssuer.ShouldBeTrue();
    }

    [Fact]
    public async Task FetchFromAresAsync_ReturnsNull_OnBadRequest()
    {
        // Arrange: ARES returns 400 for invalid IČO
        _handler.SetupResponse(HttpStatusCode.BadRequest, new { message = "Invalid registration number" });

        // Act
        var result = await _sut.FetchFromAresAsync("invalid");

        // Assert
        result.ShouldBeNull();
    }

    // ── Template tests ─────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveTemplatesAsync_AppendDocumentTypeFilter()
    {
        // Arrange
        _handler.SetupResponse(HttpStatusCode.OK, new List<InvoiceTemplateDto>());

        // Act
        await _sut.GetActiveTemplatesAsync("Invoice");

        // Assert
        _handler.LastRequestUri?.ToString().ShouldContain("documentType=Invoice");
    }

    [Fact]
    public async Task GetActiveTemplatesAsync_NoFilter_NoQueryString()
    {
        // Arrange
        _handler.SetupResponse(HttpStatusCode.OK, new List<InvoiceTemplateDto>());

        // Act
        await _sut.GetActiveTemplatesAsync();

        // Assert
        _handler.LastRequestUri?.ToString().ShouldNotContain("?");
    }

    // ── Dashboard tests ────────────────────────────────────────────────

    [Fact]
    public async Task GetDashboardAsync_ReturnsDashboard()
    {
        // Arrange
        var dashboard = new DashboardDto { InvoicesDueThisMonthCount = 15, TotalClients = 42, UnpaidAmount = 150000m };
        _handler.SetupResponse(HttpStatusCode.OK, dashboard);

        // Act
        var result = await _sut.GetDashboardAsync();

        // Assert
        result.InvoicesDueThisMonthCount.ShouldBe(15);
        result.TotalClients.ShouldBe(42);
        result.UnpaidAmount.ShouldBe(150000m);
    }

    // ── Readiness tests ────────────────────────────────────────────────

    [Fact]
    public async Task GetReadinessAsync_NoIssuerId_CallsEndpointWithoutQueryString()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new ReadinessReportDto());

        await _sut.GetReadinessAsync();

        var url = _handler.LastRequestUri?.ToString() ?? "";
        url.ShouldEndWith("api/readiness");
    }

    [Fact]
    public async Task GetReadinessAsync_WithIssuerId_AppendsItAsQueryParameter()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new ReadinessReportDto());

        await _sut.GetReadinessAsync(42);

        _handler.LastRequestUri?.ToString().ShouldContain("api/readiness?issuerId=42");
    }

    [Fact]
    public async Task GetReadinessAsync_IncompleteSetup_ReturnsReportWithIssues()
    {
        // An unfinished setup is a normal 200 — the report is the answer, not an error.
        _handler.SetupResponse(HttpStatusCode.OK, new ReadinessReportDto
        {
            Issues =
            [
                new ReadinessIssueDto
                {
                    Code = "ISSUER_MISSING",
                    Severity = EReadinessSeverity.Blocking,
                    MissingFields = ["Issuer"],
                    FixRoute = "/my-company"
                }
            ]
        });

        var result = await _sut.GetReadinessAsync();

        result.ShouldNotBeNull();
        result.IsReady.ShouldBeFalse();
        result.Issues.Single().FixRoute.ShouldBe("/my-company");
    }

    [Fact]
    public async Task GetReadinessAsync_ReturnsNull_WhenIssuerNotFound()
    {
        // 404 is reserved for "this issuerId is not in the tenant" (see ReadinessController).
        _handler.SetupResponse(HttpStatusCode.NotFound, new { message = "Not found" });

        var result = await _sut.GetReadinessAsync(999);

        result.ShouldBeNull();
    }

    // ── Error handling tests ───────────────────────────────────────────

    [Fact]
    public async Task EnsureSuccessAsync_ThrowsWithApiMessage_On400()
    {
        // Arrange: API returns 400 with a message
        _handler.SetupResponse(HttpStatusCode.BadRequest, new { message = "Validation failed: ClientId is required" });

        // Act & Assert
        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => _sut.CreateInvoiceAsync(new CreateInvoiceDto()));

        ex.Message.ShouldContain("Validation failed: ClientId is required");
    }

    [Fact]
    public async Task EnsureSuccessAsync_ThrowsWithStatusCode_On500()
    {
        // Arrange: API returns 500 with non-JSON body
        _handler.SetupRawResponse(HttpStatusCode.InternalServerError, "Internal Server Error");

        // Act & Assert
        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => _sut.CompleteInvoiceAsync(1));

        ex.Message.ShouldContain("500");
    }

    // ── Mock HttpMessageHandler ────────────────────────────────────────

    /// <summary>
    /// A simple mock HttpMessageHandler that returns a preconfigured response.
    /// Records the last request URI and method for verification in assertions.
    ///
    /// Junior note: This replaces the real HTTP stack. When FakvioApiClient
    /// calls _http.GetAsync(), the request hits this mock instead of the network.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private HttpResponseMessage _response = new(HttpStatusCode.OK);

        /// <summary>Last request URI — checked in assertions to verify correct URL building.</summary>
        public Uri? LastRequestUri { get; private set; }

        /// <summary>Last request HTTP method — checked to verify POST vs GET vs DELETE.</summary>
        public HttpMethod? LastRequestMethod { get; private set; }

        /// <summary>
        /// Configure the mock to return a JSON-serialized object with the given status code.
        /// </summary>
        public void SetupResponse<T>(HttpStatusCode statusCode, T body)
        {
            _response = new HttpResponseMessage(statusCode)
            {
                Content = JsonContent.Create(body, options: new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                })
            };
        }

        /// <summary>
        /// Configure the mock to return an empty response with the given status code.
        /// Used for DELETE and other endpoints that return no body.
        /// </summary>
        public void SetupResponse(HttpStatusCode statusCode)
        {
            _response = new HttpResponseMessage(statusCode);
        }

        /// <summary>
        /// Configure the mock to return a raw string response (non-JSON).
        /// Used for testing error handling when API returns plain text.
        /// </summary>
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
            // Record the request details for assertion
            LastRequestUri = request.RequestUri;
            LastRequestMethod = request.Method;
            return Task.FromResult(_response);
        }
    }
}
