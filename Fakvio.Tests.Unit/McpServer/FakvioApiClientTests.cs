using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Contracts.Dto.VatRate;
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
    public async Task UploadFileAttachmentAsync_SendsMultipartWithExpectedFields()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new FileAttachmentDto { Id = 9 });

        var result = await _sut.UploadFileAttachmentAsync("ReceivedInvoice", 27, "a.pdf", "application/pdf", [1, 2, 3], "desc");

        result.Id.ShouldBe(9);
        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
        _handler.LastRequestUri!.AbsolutePath.ShouldBe("/api/file-attachment/upload");
        _handler.LastRequestContentType.ShouldStartWith("multipart/form-data");
        // Field names are the controller's contract — a typo here would be a silent 400 in prod.
        _handler.LastRequestBody.ShouldContain("name=file; filename=a.pdf");
        _handler.LastRequestBody.ShouldContain("name=entityName");
        _handler.LastRequestBody.ShouldContain("ReceivedInvoice");
        _handler.LastRequestBody.ShouldContain("name=recordId");
        _handler.LastRequestBody.ShouldContain("name=description");
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

    // ── Currency tests ──────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveCurrenciesAsync_CallsActiveEndpoint_AndDeserializesResult()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new List<CurrencyDto>
        {
            new() { Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč" }
        });

        var result = await _sut.GetActiveCurrenciesAsync();

        _handler.LastRequestUri?.ToString().ShouldEndWith("api/currency/active");
        result.ShouldHaveSingleItem();
        result[0].Code.ShouldBe("CZK");
    }

    [Fact]
    public async Task GetActiveCurrenciesAsync_EmptyResponseBody_ReturnsEmptyList()
    {
        _handler.SetupRawResponse(HttpStatusCode.OK, "null");

        var result = await _sut.GetActiveCurrenciesAsync();

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// Codex review follow-up: every GET added by story N2/N3 must route a non-success response
    /// through <c>EnsureSuccessAsync</c> (via the shared <c>GetJsonAsync&lt;T&gt;</c> helper), not
    /// <c>HttpClientJsonExtensions.GetFromJsonAsync</c>'s own <c>EnsureSuccessStatusCode</c> — the
    /// latter throws a bare <see cref="HttpRequestException"/> that <c>McpToolError</c> cannot
    /// distinguish from a real server crash. One representative endpoint per wrapped-GET group
    /// (currency here, VAT rate/number sequence/payment/reminder below) proves the fix; they all
    /// go through the same helper, so a regression in it fails here first.
    /// </summary>
    [Fact]
    public async Task GetActiveCurrenciesAsync_On403_ThrowsFakvioApiException_NotBareHttpRequestException()
    {
        _handler.SetupResponse(HttpStatusCode.Forbidden, new { message = "read-only key" });

        var ex = await Should.ThrowAsync<FakvioApiException>(() => _sut.GetActiveCurrenciesAsync());

        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── VAT rate tests ──────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveVatRatesAsync_NoDate_NoQueryString()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new List<VatRateDto>());

        await _sut.GetActiveVatRatesAsync();

        _handler.LastRequestUri?.ToString().ShouldEndWith("api/vatrate/active");
    }

    [Fact]
    public async Task GetActiveVatRatesAsync_WithDate_AppendsDateQuery()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new List<VatRateDto> { new() { Id = 1, Rate = 21m } });

        var result = await _sut.GetActiveVatRatesAsync(new DateTime(2026, 3, 1));

        _handler.LastRequestUri?.ToString().ShouldContain("date=");
        result.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetActiveVatRatesAsync_On400_ThrowsFakvioApiException_NotBareHttpRequestException()
    {
        _handler.SetupResponse(HttpStatusCode.BadRequest, new { message = "invalid date" });

        var ex = await Should.ThrowAsync<FakvioApiException>(() => _sut.GetActiveVatRatesAsync());

        ex.SafeMessage.ShouldBe("invalid date");
    }

    // ── Number sequence tests ───────────────────────────────────────────

    [Fact]
    public async Task GetNumberSequencesAsync_NoFilters_NoQueryString()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new List<NumberSequenceDto>());

        await _sut.GetNumberSequencesAsync();

        _handler.LastRequestUri?.ToString().ShouldEndWith("api/numbersequence");
    }

    [Fact]
    public async Task GetNumberSequencesAsync_WithFilters_AppendsQueryString()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new List<NumberSequenceDto>());

        await _sut.GetNumberSequencesAsync(EDocumentType.CreditNote, includeInactive: true);

        _handler.LastRequestUri?.ToString().ShouldContain("documentType=CreditNote");
        _handler.LastRequestUri?.ToString().ShouldContain("includeInactive=true");
    }

    [Fact]
    public async Task GetNumberSequenceFormatsAsync_CallsFormatsEndpoint()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new List<NumberSequenceFormatDto> { new() { Id = 1 } });

        var result = await _sut.GetNumberSequenceFormatsAsync();

        _handler.LastRequestUri?.ToString().ShouldEndWith("api/numbersequence/formats");
        result.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetNumberSequenceFormatsAsync_On403_ThrowsFakvioApiException_NotBareHttpRequestException()
    {
        _handler.SetupResponse(HttpStatusCode.Forbidden, new { message = "read-only key" });

        await Should.ThrowAsync<FakvioApiException>(() => _sut.GetNumberSequenceFormatsAsync());
    }

    [Fact]
    public async Task CreateNumberSequenceAsync_PostsAndReturnsCreated()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new NumberSequenceDto { Id = 9, Name = "Faktury" });

        var result = await _sut.CreateNumberSequenceAsync(new CreateNumberSequenceDto { Name = "Faktury" });

        _handler.LastRequestMethod.ShouldBe(HttpMethod.Post);
        result.Id.ShouldBe(9);
    }

    [Fact]
    public async Task UpdateNumberSequenceAsync_NotFound_ReturnsNull()
    {
        _handler.SetupResponse(HttpStatusCode.NotFound, new { message = "not found" });

        var result = await _sut.UpdateNumberSequenceAsync(999, new UpdateNumberSequenceDto());

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SetDefaultNumberSequenceAsync_PostsToSetDefaultRoute()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new NumberSequenceDto { Id = 1, IsDefault = true });

        var result = await _sut.SetDefaultNumberSequenceAsync(1);

        _handler.LastRequestUri?.ToString().ShouldEndWith("api/numbersequence/1/set-default");
        result!.IsDefault.ShouldBeTrue();
    }

    // ── Payment / reminder tests ────────────────────────────────────────

    [Fact]
    public async Task GetPaymentsPagedAsync_AppendsFilterQueryString()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new PagedResult<BankTransactionDto>([], 0, 1, 20));

        await _sut.GetPaymentsPagedAsync(EMatchStatus.Unmatched, EPaymentDirection.Incoming,
            new DateTime(2026, 1, 1), new DateTime(2026, 1, 31), page: 1, pageSize: 20);

        var url = _handler.LastRequestUri?.ToString();
        url.ShouldContain("status=Unmatched");
        url.ShouldContain("direction=Incoming");
        url.ShouldContain("from=");
        url.ShouldContain("to=");
    }

    [Fact]
    public async Task GetPaymentsPagedAsync_On403_ThrowsFakvioApiException_NotBareHttpRequestException()
    {
        _handler.SetupResponse(HttpStatusCode.Forbidden, new { message = "read-only key" });

        await Should.ThrowAsync<FakvioApiException>(
            () => _sut.GetPaymentsPagedAsync(null, null, null, null, 1, 20));
    }

    [Fact]
    public async Task GetPaymentByIdAsync_NotFound_ReturnsNull()
    {
        _handler.SetupResponse(HttpStatusCode.NotFound, new { message = "not found" });

        var result = await _sut.GetPaymentByIdAsync(999);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetRemindersPagedAsync_AppendsFilterQueryString()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new PagedResult<ReminderDto>([], 0, 1, 20));

        await _sut.GetRemindersPagedAsync(new ReminderFilterDto { InvoiceId = 7, Status = EReminderStatus.Sent });

        var url = _handler.LastRequestUri?.ToString();
        url.ShouldContain("invoiceId=7");
        url.ShouldContain("status=Sent");
    }

    [Fact]
    public async Task GetRemindersPagedAsync_On403_ThrowsFakvioApiException_NotBareHttpRequestException()
    {
        _handler.SetupResponse(HttpStatusCode.Forbidden, new { message = "read-only key" });

        await Should.ThrowAsync<FakvioApiException>(() => _sut.GetRemindersPagedAsync(new ReminderFilterDto()));
    }

    [Fact]
    public async Task GetRemindersByInvoiceAsync_CallsInvoiceEndpoint()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new List<ReminderDto> { new() { Id = 1 } });

        var result = await _sut.GetRemindersByInvoiceAsync(7);

        _handler.LastRequestUri?.ToString().ShouldEndWith("api/reminder/invoice/7");
        result.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetReminderSettingsAsync_CallsSettingsEndpoint()
    {
        _handler.SetupResponse(HttpStatusCode.OK, new ReminderSettingsDto { IsEnabled = true });

        var result = await _sut.GetReminderSettingsAsync();

        _handler.LastRequestUri?.ToString().ShouldEndWith("api/reminder/settings");
        result.IsEnabled.ShouldBeTrue();
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

    [Fact]
    public async Task GetReadinessAsync_TenantWide404_Throws_InsteadOfLookingLikeAMissingIssuer()
    {
        // Without an issuerId the endpoint never answers 404, so a 404 here is a broken route,
        // not a domain answer. Swallowing it into null would make the MCP tool tell the user
        // "Issuer with ID  not found." — an empty ID and a factually wrong diagnosis.
        _handler.SetupResponse(HttpStatusCode.NotFound, new { message = "Not found" });

        var ex = await Should.ThrowAsync<HttpRequestException>(() => _sut.GetReadinessAsync());

        ex.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetReadinessAsync_AlreadyCancelledToken_NeverSendsTheRequest()
    {
        // Proves the token reaches the HTTP call itself. The throw alone would not: without
        // the token on GetAsync the request goes out and only the body read notices. So the
        // real assertion is that the handler was never reached at all.
        _handler.SetupResponse(HttpStatusCode.OK, new ReadinessReportDto());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => _sut.GetReadinessAsync(ct: cts.Token));

        _handler.LastRequestUri.ShouldBeNull();
    }

    [Fact]
    public async Task GetReadinessAsync_CancelledWhileTheResponseIsInFlight_StopsBeforeDeserializing()
    {
        // The second use of the token: reading the body. The handler cancels the caller's
        // source as it answers, so the response exists but must never be deserialized.
        _handler.SetupResponse(HttpStatusCode.OK, new ReadinessReportDto());
        using var cts = new CancellationTokenSource();
        _handler.CancelWhenSending = cts;

        await Should.ThrowAsync<OperationCanceledException>(() => _sut.GetReadinessAsync(ct: cts.Token));
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

    /// <summary>
    /// #342: a TENANT_NOT_READY 400 must survive as a structured exception, not collapse into
    /// the flattened HttpRequestException every other 400 becomes — otherwise an MCP tool has
    /// only ex.Message to work with and cannot forward the fix route to its caller.
    /// </summary>
    [Fact]
    public async Task EnsureSuccessAsync_ThrowsTenantNotReadyApiException_OnTenantNotReady400()
    {
        _handler.SetupResponse(HttpStatusCode.BadRequest, new
        {
            code = "TENANT_NOT_READY",
            message = "Tenant is not ready. Unresolved blocking issue(s): ISSUER_BANK_ACCOUNT_MISSING.",
            missingFields = new[] { "BankAccount" },
            issues = new[]
            {
                new ReadinessIssueDto
                {
                    Code = "ISSUER_BANK_ACCOUNT_MISSING",
                    Severity = EReadinessSeverity.Blocking,
                    MissingFields = ["BankAccount"],
                    FixRoute = "/my-company"
                }
            }
        });

        var ex = await Should.ThrowAsync<TenantNotReadyApiException>(() => _sut.CompleteInvoiceAsync(1));

        ex.Message.ShouldContain("ISSUER_BANK_ACCOUNT_MISSING");
        ex.MissingFields.ShouldBe(["BankAccount"]);
        ex.Issues.Single().FixRoute.ShouldBe("/my-company");
    }

    /// <summary>A 400 with an unrelated (or missing) code must not be swallowed by the new branch.</summary>
    [Fact]
    public async Task EnsureSuccessAsync_OtherBadRequestCode_StaysAPlainHttpRequestException()
    {
        _handler.SetupResponse(HttpStatusCode.BadRequest, new { code = "SOME_OTHER_CODE", message = "nope" });

        await Should.ThrowAsync<HttpRequestException>(() => _sut.CompleteInvoiceAsync(1));
    }

    /// <summary>
    /// The TENANT_NOT_READY probe runs on EVERY 400 this client sees, so it must survive body
    /// shapes it was not written for. A bare JSON string at the root is the common one —
    /// <c>TaxController</c> answers <c>BadRequest("Gross income cannot be negative.")</c> and
    /// <see cref="FakvioApiClient.EstimateTaxAsync"/> calls it — and it makes
    /// <c>TryGetProperty</c> throw <see cref="InvalidOperationException"/>, not return false.
    /// Each of these bodies must still come out as the ordinary flattened HttpRequestException.
    /// </summary>
    [Theory]
    [InlineData("\"Gross income cannot be negative.\"")]   // string root — TaxController:46
    [InlineData("[]")]                                      // array root
    [InlineData("null")]                                    // JSON null root
    [InlineData("42")]                                      // number root
    [InlineData("{\"code\":123,\"message\":\"nope\"}")]   // object, but code is not a string
    [InlineData("{\"code\":null}")]                         // object, code is JSON null
    public async Task EnsureSuccessAsync_UnexpectedBadRequestBodyShape_StaysAPlainHttpRequestException(string body)
    {
        _handler.SetupRawResponse(HttpStatusCode.BadRequest, body);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => _sut.CompleteInvoiceAsync(1));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// A TENANT_NOT_READY body whose optional parts are the wrong JSON type still has to produce
    /// the structured exception — the code is what identifies the refusal, the rest degrades to
    /// empty rather than dropping the caller back onto a flattened error string.
    /// </summary>
    [Fact]
    public async Task EnsureSuccessAsync_TenantNotReadyWithOddOptionalFields_StillThrowsTheStructuredException()
    {
        _handler.SetupRawResponse(HttpStatusCode.BadRequest,
            """{"code":"TENANT_NOT_READY","message":42,"missingFields":"BankAccount","issues":null}""");

        var ex = await Should.ThrowAsync<TenantNotReadyApiException>(() => _sut.CompleteInvoiceAsync(1));

        ex.MissingFields.ShouldBeEmpty();
        ex.Issues.ShouldBeEmpty();
    }

    /// <summary>
    /// Characterizes today's fallback when "message" is missing or not a string: the whole raw
    /// 400 body (not a neutral message) becomes <see cref="Exception.Message"/>. Flagged by
    /// review as a non-blocking latent risk (#342 round 2) — currently unreachable because the
    /// only real TENANT_NOT_READY producer (<c>TenantNotReadyExceptionExtensions</c>) always
    /// emits a safe, machine-composed string message, but this pins the *current* behaviour so a
    /// future change to that fallback (e.g. hardcoding a neutral message per the review comment)
    /// is a deliberate, visible diff here — not a silent behaviour change.
    /// </summary>
    [Fact]
    public async Task EnsureSuccessAsync_TenantNotReadyWithoutStringMessage_MessageFallsBackToTheRawBody()
    {
        const string body = """{"code":"TENANT_NOT_READY","message":42}""";
        _handler.SetupRawResponse(HttpStatusCode.BadRequest, body);

        var ex = await Should.ThrowAsync<TenantNotReadyApiException>(() => _sut.CompleteInvoiceAsync(1));

        ex.Message.ShouldBe(body);
    }

    /// <summary>
    /// N2.2 (#279 follow-up): a 403 from a read-only API key must come out as a
    /// <see cref="FakvioApiException"/> with SafeMessage null when the body carries no
    /// domain message — McpToolError.ToJson uses a fixed guidance text for 401/403 regardless,
    /// but the API client itself must not invent a SafeMessage that was never in the body.
    /// </summary>
    [Fact]
    public async Task EnsureSuccessAsync_On403_ThrowsFakvioApiException_WithNullSafeMessage_WhenBodyHasNoMessage()
    {
        _handler.SetupResponse(HttpStatusCode.Forbidden, new { error = "Forbidden" });

        var ex = await Should.ThrowAsync<FakvioApiException>(() => _sut.CompleteInvoiceAsync(1));

        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ex.SafeMessage.ShouldBeNull();
    }

    /// <summary>A 400 with a string "message" property must surface it as SafeMessage verbatim.</summary>
    [Fact]
    public async Task EnsureSuccessAsync_On400WithMessage_SetsSafeMessage()
    {
        _handler.SetupResponse(HttpStatusCode.BadRequest, new { message = "Duplicate variable symbol." });

        var ex = await Should.ThrowAsync<FakvioApiException>(() => _sut.CompleteInvoiceAsync(1));

        ex.SafeMessage.ShouldBe("Duplicate variable symbol.");
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

        /// <summary>Last request Content-Type and body — used to verify multipart uploads.</summary>
        public string? LastRequestContentType { get; private set; }
        public string? LastRequestBody { get; private set; }

        /// <summary>
        /// When set, the handler cancels this source as it answers — simulating a caller that
        /// gives up while the response is on the wire. Lets a test prove the token is still
        /// honoured while the body is being read, not only before the request is sent.
        /// </summary>
        public CancellationTokenSource? CancelWhenSending { get; set; }

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
            // A real handler aborts on a cancelled token; the mock has to do the same,
            // otherwise a dropped token would look like a perfectly healthy call.
            cancellationToken.ThrowIfCancellationRequested();

            // Record the request details for assertion
            LastRequestUri = request.RequestUri;
            LastRequestMethod = request.Method;
            LastRequestContentType = request.Content?.Headers.ContentType?.ToString();
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

            CancelWhenSending?.Cancel();
            return Task.FromResult(_response);
        }
    }
}
