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
}
