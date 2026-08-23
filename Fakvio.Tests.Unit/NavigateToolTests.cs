using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for NavigateTool.
/// Tests navigation URL generation, client name resolution, and error handling.
///
/// Uses NSubstitute to mock IClientService for client search results.
/// </summary>
public class NavigateToolTests
{
    private readonly NavigateTool _tool;
    private readonly IClientService _clientService;
    private readonly ILogger<NavigateTool> _logger;

    public NavigateToolTests()
    {
        _clientService = Substitute.For<IClientService>();
        _logger = Substitute.For<ILogger<NavigateTool>>();
        _tool = new NavigateTool(_clientService, _logger);
    }

    // ─── Basic Navigation Tests ──────────────────────────────────────────

    [Fact]
    public async Task NewInvoice_WithoutClient_ReturnsNavigateAction()
    {
        // Arrange
        var parameters = new Dictionary<string, string> { ["target"] = "new_invoice" };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Type.ShouldBe("navigate");
        result.UiAction.Url.ShouldBe("/invoices/create");
    }

    [Fact]
    public async Task NewCreditNote_WithoutClient_ReturnsNavigateAction()
    {
        // Arrange
        var parameters = new Dictionary<string, string> { ["target"] = "new_credit_note" };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Url.ShouldBe("/invoices/create?type=CreditNote");
    }

    [Fact]
    public async Task ClientList_ReturnsNavigateAction()
    {
        // Arrange
        var parameters = new Dictionary<string, string> { ["target"] = "client_list" };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Url.ShouldBe("/clients");
    }

    [Fact]
    public async Task InvoiceList_ReturnsNavigateAction()
    {
        // Arrange
        var parameters = new Dictionary<string, string> { ["target"] = "invoice_list" };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Url.ShouldBe("/invoices");
    }

    [Fact]
    public async Task NewClient_ReturnsNavigateAction()
    {
        // Arrange
        var parameters = new Dictionary<string, string> { ["target"] = "new_client" };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Url.ShouldBe("/clients/create");
    }

    // ─── Client Resolution Tests ─────────────────────────────────────────

    [Fact]
    public async Task NewInvoice_WithClient_ResolvesClientId()
    {
        // Arrange — mock single client match.
        var client = new ClientDto { Id = 42, CompanyName = "Test s.r.o.", RegistrationNumber = "12345678" };
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(new List<ClientDto> { client }, 1, 1, 5));

        var parameters = new Dictionary<string, string>
        {
            ["target"] = "new_invoice",
            ["client_name"] = "Test"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Url.ShouldBe("/invoices/create?clientId=42");
        result.OutputText.ShouldContain("Test s.r.o.");
    }

    [Fact]
    public async Task NewCreditNote_WithClient_ResolvesClientId()
    {
        // Arrange
        var client = new ClientDto { Id = 10, CompanyName = "ABC Corp" };
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(new List<ClientDto> { client }, 1, 1, 5));

        var parameters = new Dictionary<string, string>
        {
            ["target"] = "new_credit_note",
            ["client_name"] = "ABC"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Url.ShouldBe("/invoices/create?type=CreditNote&clientId=10");
    }

    [Fact]
    public async Task ClientDetail_ResolvesClientId()
    {
        // Arrange
        var client = new ClientDto { Id = 7, CompanyName = "Firma XYZ" };
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(new List<ClientDto> { client }, 1, 1, 5));

        var parameters = new Dictionary<string, string>
        {
            ["target"] = "client_detail",
            ["client_name"] = "Firma XYZ"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Url.ShouldBe("/clients/7");
        result.OutputText.ShouldContain("Firma XYZ");
    }

    [Fact]
    public async Task ClientDetail_WithoutClientName_ReturnsFailure()
    {
        // Arrange — client_detail requires a client name.
        var parameters = new Dictionary<string, string> { ["target"] = "client_detail" };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.UiAction.ShouldBeNull();
        result.OutputText.ShouldContain("client_name");
    }

    [Fact]
    public async Task ClientNotFound_ReturnsFailureText()
    {
        // Arrange — no matches found.
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(new List<ClientDto>(), 0, 1, 5));

        var parameters = new Dictionary<string, string>
        {
            ["target"] = "client_detail",
            ["client_name"] = "NonExistentCompany"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — failure, no navigation action.
        result.IsSuccess.ShouldBeFalse();
        result.UiAction.ShouldBeNull();
        result.OutputText.ShouldContain("NonExistentCompany");
    }

    [Fact]
    public async Task MultipleClients_ReturnsAmbiguityMessage()
    {
        // Arrange — 3 matches found, ambiguous.
        var clients = new List<ClientDto>
        {
            new() { Id = 1, CompanyName = "ABC Alpha", RegistrationNumber = "11111111" },
            new() { Id = 2, CompanyName = "ABC Beta", RegistrationNumber = "22222222" },
            new() { Id = 3, CompanyName = "ABC Gamma", RegistrationNumber = "33333333" }
        };
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(clients, 3, 1, 5));

        var parameters = new Dictionary<string, string>
        {
            ["target"] = "new_invoice",
            ["client_name"] = "ABC"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — success (not an error) but no navigation — user needs to clarify.
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldBeNull(); // No navigation for ambiguous results.
        result.OutputText.ShouldContain("ABC Alpha");
        result.OutputText.ShouldContain("ABC Beta");
        result.OutputText.ShouldContain("3 clients");
    }

    // ─── Error Handling Tests ────────────────────────────────────────────

    [Fact]
    public async Task UnknownTarget_ReturnsFailure()
    {
        // Arrange — invalid target value.
        var parameters = new Dictionary<string, string> { ["target"] = "unknown_page" };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("unknown_page");
    }

    [Fact]
    public async Task ClientName_OnATargetThatCannotPreselectAClient_IsIgnored()
    {
        // Only the invoice / credit note forms understand ?clientId=. Everywhere else a name
        // the model volunteered must not trigger a lookup, and must not end up in the URL.
        var parameters = new Dictionary<string, string>
        {
            ["target"] = "client_list",
            ["client_name"] = "Test s.r.o."
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeTrue();
        result.UiAction!.Url.ShouldBe("/clients");
        await _clientService.DidNotReceive().GetClientsPagedAsync(
            Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("system_settings")]
    [InlineData("logs")]
    [InlineData("companies")]
    public async Task SysAdminTarget_IsRejectedBeforeTheToolRuns(string target)
    {
        // SysAdmin pages are deliberately absent from the catalog (issue #229). The executor's
        // central AllowedValues check is what stops the model from asking for them, so this
        // goes through the executor rather than calling the tool directly.
        var executor = new ChatToolExecutor([_tool], Substitute.For<ILogger<ChatToolExecutor>>());
        var call = executor.ParseToolCall(
            $"{{\"action\": \"navigate\", \"parameters\": {{\"target\": \"{target}\"}}}}");

        call.ShouldNotBeNull();
        var result = await executor.ExecuteToolAsync(call);

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain(target);
        await _clientService.DidNotReceive().GetClientsPagedAsync(
            Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>());
    }

    // ─── Target normalisation (#229) ─────────────────────────────────────

    [Theory]
    [InlineData("NEW_INVOICE")]
    [InlineData("New_Invoice")]
    [InlineData("  new_invoice  ")]
    public async Task Target_IsTrimmedAndLowerCased_BeforeTheCatalogLookup(string target)
    {
        // The catalog is keyed ordinally, so the tool has to normalise first. It matters in
        // production: the executor accepts targets case-insensitively and hands the raw value
        // over, so without normalisation a model shouting "NEW_INVOICE" would get "Unknown target".
        var result = await _tool.ExecuteAsync(new Dictionary<string, string> { ["target"] = target });

        result.IsSuccess.ShouldBeTrue();
        result.UiAction!.Url.ShouldBe("/invoices/create");
    }

    [Fact]
    public async Task UpperCaseTarget_TravelsThroughTheExecutor_AndStillNavigates()
    {
        // End-to-end over the seam above: AllowedValues are compared case-insensitively by the
        // executor, so the tool is the only place where the casing can still break navigation.
        var executor = new ChatToolExecutor([_tool], Substitute.For<ILogger<ChatToolExecutor>>());
        var call = executor.ParseToolCall(
            "{\"action\": \"navigate\", \"parameters\": {\"target\": \"INVOICE_LIST\"}}");

        var result = await executor.ExecuteToolAsync(call!);

        result.IsSuccess.ShouldBeTrue(result.OutputText);
        result.UiAction!.Url.ShouldBe("/invoices");
    }

    // ─── Missing target ──────────────────────────────────────────────────

    [Fact]
    public async Task MissingTarget_IsRejectedByTheExecutor_WithoutRunningTheTool()
    {
        // 'target' is required, and the executor validates required parameters centrally —
        // this is the guard the tool relies on instead of checking the dictionary itself.
        var executor = new ChatToolExecutor([_tool], Substitute.For<ILogger<ChatToolExecutor>>());
        var call = executor.ParseToolCall("{\"action\": \"navigate\", \"parameters\": {}}");

        var result = await executor.ExecuteToolAsync(call!);

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("target");
        await _clientService.DidNotReceive().GetClientsPagedAsync(
            Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MissingTarget_ThrowsWhenTheToolIsCalledWithoutTheExecutor()
    {
        // Characterisation of today's behaviour, not an endorsement: ExecuteAsync indexes
        // parameters["target"] directly. Every production caller goes through the executor
        // (test above), so the throw is unreachable there — but a direct caller gets a
        // KeyNotFoundException instead of a Failure result. If the tool ever grows its own
        // guard, replace this with an assertion on the failure message.
        Should.Throw<KeyNotFoundException>(
            () => _tool.ExecuteAsync(new Dictionary<string, string>()));
    }

    // ─── Client name handling ────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ClientDetail_WithBlankClientName_ReturnsFailure_WithoutSearching(string clientName)
    {
        // A model that sends an empty string means "I have no value" — that must hit the same
        // guard as a completely missing name, not a search for whitespace.
        var parameters = new Dictionary<string, string>
        {
            ["target"] = "client_detail",
            ["client_name"] = clientName
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeFalse();
        result.UiAction.ShouldBeNull();
        result.OutputText.ShouldContain("client_name");
        await _clientService.DidNotReceive().GetClientsPagedAsync(
            Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NewInvoice_WithBlankClientName_NavigatesWithoutPreselection()
    {
        var parameters = new Dictionary<string, string>
        {
            ["target"] = "new_invoice",
            ["client_name"] = "   "
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeTrue();
        result.UiAction!.Url.ShouldBe("/invoices/create");
        await _clientService.DidNotReceive().GetClientsPagedAsync(
            Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClientName_IsTrimmedBeforeTheSearch()
    {
        StubClientSearch(new ClientDto { Id = 3, CompanyName = "Test s.r.o." });

        var parameters = new Dictionary<string, string>
        {
            ["target"] = "client_detail",
            ["client_name"] = "  Test s.r.o.  "
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.UiAction!.Url.ShouldBe("/clients/3");
        await _clientService.Received(1).GetClientsPagedAsync(
            Arg.Is<ClientFilterDto>(filter => filter.Search == "Test s.r.o."),
            Arg.Any<CancellationToken>());
    }

    // ─── client_detail resolution paths ──────────────────────────────────

    [Fact]
    public async Task ClientDetail_WithMultipleMatches_ListsThem_AndDoesNotNavigate()
    {
        // The ambiguity branch is shared with the invoice forms, but client_detail is the only
        // target where a name is mandatory — a wrong pick here opens a stranger's record.
        StubClientSearch(
            new ClientDto { Id = 1, CompanyName = "ABC Alpha", RegistrationNumber = "11111111" },
            new ClientDto { Id = 2, CompanyName = "ABC Beta" });

        var parameters = new Dictionary<string, string>
        {
            ["target"] = "client_detail",
            ["client_name"] = "ABC"
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldBeNull();
        // The client with an IČO shows it; the one without must not render an empty "IČO: ".
        result.OutputText.ShouldContain("ABC Alpha (ID: 1, IČO: 11111111)");
        result.OutputText.ShouldContain("ABC Beta (ID: 2)");
    }

    [Fact]
    public async Task ClientDetail_WhenNoClientMatches_ReturnsFailure_WithoutNavigating()
    {
        StubClientSearch();

        var parameters = new Dictionary<string, string>
        {
            ["target"] = "client_detail",
            ["client_name"] = "Ghost s.r.o."
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeFalse();
        result.UiAction.ShouldBeNull();
        result.OutputText.ShouldContain("Ghost s.r.o.");
    }

    // ─── Query separator derivation ──────────────────────────────────────

    [Theory]
    [InlineData("new_invoice")]
    [InlineData("new_credit_note")]
    public async Task PreselectingTarget_AppendsClientId_AsASingleWellFormedQuery(string target)
    {
        // The separator is derived from the route ('?' for a bare route, '&' when the route
        // already carries ?type=CreditNote). Hard-coding either one produces a malformed URL
        // for the other target — asserted here as a shape rule so a future route with its own
        // query string is covered too.
        StubClientSearch(new ClientDto { Id = 42, CompanyName = "Test s.r.o." });

        var result = await _tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["target"] = target,
            ["client_name"] = "Test"
        });

        var url = result.UiAction!.Url!;
        url.Count(character => character == '?').ShouldBe(1, $"'{url}' must contain exactly one '?'");
        url.ShouldEndWith("clientId=42");
        url.ShouldNotContain("?&");
    }

    [Fact]
    public async Task ClientSearch_ExcludesIssuers()
    {
        // Arrange — verify that the search filter excludes issuers.
        var client = new ClientDto { Id = 1, CompanyName = "Test" };
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(new List<ClientDto> { client }, 1, 1, 5));

        var parameters = new Dictionary<string, string>
        {
            ["target"] = "new_invoice",
            ["client_name"] = "Test"
        };

        // Act
        await _tool.ExecuteAsync(parameters);

        // Assert — verify filter was called with IsIssuer = false.
        await _clientService.Received(1).GetClientsPagedAsync(
            Arg.Is<ClientFilterDto>(f => f.IsIssuer == false && f.Search == "Test"),
            Arg.Any<CancellationToken>());
    }

    // ─── Test helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Makes the client search return exactly these matches (none = "no client found").
    /// PageSize 5 mirrors the filter the tool builds.
    /// </summary>
    private void StubClientSearch(params ClientDto[] matches)
        => _clientService
            .GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(matches.ToList(), matches.Length, 1, 5));
}
