using Fakvio.Application.Service;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ChatToolExecutor.
/// Tests intent detection (regex matching), JSON tool call parsing,
/// tool dispatch, and tool instruction generation.
///
/// Uses NSubstitute mocks for IChatTool implementations.
/// </summary>
public class ChatToolExecutorTests
{
    private readonly ChatToolExecutor _executor;
    private readonly IChatTool _mockAresTool;
    private readonly IChatTool _mockCreateTool;

    public ChatToolExecutorTests()
    {
        var logger = Substitute.For<ILogger<ChatToolExecutor>>();

        // Mock ARES lookup tool.
        _mockAresTool = Substitute.For<IChatTool>();
        _mockAresTool.ToolName.Returns("ares_lookup");
        _mockAresTool.Description.Returns("Looks up a company in ARES");
        _mockAresTool.ParameterDescription.Returns("registration_number (string): IČO");
        _mockAresTool
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("Company found: Test s.r.o."));

        // Mock create client tool.
        _mockCreateTool = Substitute.For<IChatTool>();
        _mockCreateTool.ToolName.Returns("create_client");
        _mockCreateTool.Description.Returns("Creates a client");
        _mockCreateTool.ParameterDescription.Returns("registration_number (string): IČO");
        _mockCreateTool
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("Client created: Test s.r.o."));

        // Mock navigate tool.
        var mockNavigateTool = Substitute.For<IChatTool>();
        mockNavigateTool.ToolName.Returns("navigate");
        mockNavigateTool.Description.Returns("Navigates to a page");
        mockNavigateTool.ParameterDescription.Returns("target (string): page target");
        mockNavigateTool
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.SuccessWithAction("Navigating...",
                Fakvio.Contracts.Dto.Chat.ChatUiAction.Navigate("/invoices/create")));

        // Mock create invoice tool.
        var mockCreateInvoiceTool = Substitute.For<IChatTool>();
        mockCreateInvoiceTool.ToolName.Returns("create_invoice");
        mockCreateInvoiceTool.Description.Returns("Creates an invoice");
        mockCreateInvoiceTool.ParameterDescription.Returns("client_name (string): client, items (string): JSON array");
        mockCreateInvoiceTool
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.SuccessWithAction("Invoice created.",
                Fakvio.Contracts.Dto.Chat.ChatUiAction.Navigate("/invoices/100")));

        _executor = new ChatToolExecutor(
            new List<IChatTool> { _mockAresTool, _mockCreateTool, mockNavigateTool, mockCreateInvoiceTool },
            logger);
    }

    // ─── DetectToolIntent Tests ───────────────────────────────────────────

    [Theory]
    [InlineData("Najdi firmu s IČO 12345678")]
    [InlineData("ARES lookup ICO 87654321")]
    [InlineData("Založ klienta s IČO 12345678")]
    [InlineData("create client 12345678")]
    [InlineData("Find company ICO: 12345678")]
    [InlineData("hledej firma 12345678")]
    [InlineData("Vyhledej společnost IČO 99887766")]
    [InlineData("search ares 12345678")]
    public void DetectToolIntent_ReturnsTrue_ForValidToolMessages(string message)
    {
        _executor.DetectToolIntent(message).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Kolik mám faktur?")]
    [InlineData("Hello")]
    [InlineData("")]
    [InlineData("Moje telefonní číslo je 12345678")] // IČO pattern but no tool keyword
    [InlineData("12345")]                            // Too short for IČO
    [InlineData("123456789")]                        // Too long for IČO (9 digits)
    public void DetectToolIntent_ReturnsFalse_ForNonToolMessages(string message)
    {
        _executor.DetectToolIntent(message).ShouldBeFalse();
    }

    // ─── Navigation Intent Detection Tests ──────────────────────────────

    [Theory]
    [InlineData("Otevři novou fakturu")]
    [InlineData("Ukaž mi klienta ABC")]
    [InlineData("Zobraz seznam faktur")]
    [InlineData("Přejdi na klienty")]
    [InlineData("Naviguj na nového klienta")]
    [InlineData("Nová faktura pro klienta XYZ")]
    [InlineData("Nový dobropis")]
    [InlineData("open new invoice")]
    [InlineData("show client ABC")]
    [InlineData("go to invoice list")]
    [InlineData("new client")]
    [InlineData("new credit note")]
    [InlineData("display client list")]
    public void DetectToolIntent_ReturnsTrue_ForNavigationKeywords(string message)
    {
        _executor.DetectToolIntent(message).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Kolik dlužím?")]               // Question, not navigation
    [InlineData("Jaká je celková suma?")]        // Question
    [InlineData("Poděkuj zákazníkovi")]          // Not a navigation keyword
    [InlineData("Najdi klienta")]                // "Najdi" is a tool keyword but no IČO and not navigation
    public void DetectToolIntent_ReturnsFalse_ForNonNavigationQuestions(string message)
    {
        _executor.DetectToolIntent(message).ShouldBeFalse();
    }

    [Fact]
    public void DetectToolIntent_ReturnsFalse_ForNull()
    {
        _executor.DetectToolIntent(null!).ShouldBeFalse();
    }

    // ─── ParseToolCall Tests ──────────────────────────────────────────────

    [Fact]
    public void ParseToolCall_ParsesValidJson()
    {
        var json = """{"action": "ares_lookup", "parameters": {"registration_number": "12345678"}}""";

        var result = _executor.ParseToolCall(json);

        result.ShouldNotBeNull();
        result.Action.ShouldBe("ares_lookup");
        result.Parameters["registration_number"].ShouldBe("12345678");
    }

    [Fact]
    public void ParseToolCall_ParsesJsonWrappedInCodeBlock()
    {
        var response = "```json\n{\"action\": \"ares_lookup\", \"parameters\": {\"registration_number\": \"12345678\"}}\n```";

        var result = _executor.ParseToolCall(response);

        result.ShouldNotBeNull();
        result.Action.ShouldBe("ares_lookup");
        result.Parameters["registration_number"].ShouldBe("12345678");
    }

    [Fact]
    public void ParseToolCall_ParsesJsonWithPreambleText()
    {
        var response = "Sure, I'll look that up.\n{\"action\": \"ares_lookup\", \"parameters\": {\"registration_number\": \"12345678\"}}";

        var result = _executor.ParseToolCall(response);

        result.ShouldNotBeNull();
        result.Action.ShouldBe("ares_lookup");
    }

    [Fact]
    public void ParseToolCall_ReturnsNull_ForRegularText()
    {
        var result = _executor.ParseToolCall("Here is your answer about invoices.");

        result.ShouldBeNull();
    }

    [Fact]
    public void ParseToolCall_ReturnsNull_ForInvalidJson()
    {
        var result = _executor.ParseToolCall("{broken json");

        result.ShouldBeNull();
    }

    [Fact]
    public void ParseToolCall_ReturnsNull_ForJsonWithoutAction()
    {
        var result = _executor.ParseToolCall("""{"foo": "bar", "baz": 123}""");

        result.ShouldBeNull();
    }

    [Fact]
    public void ParseToolCall_ReturnsNull_ForEmptyString()
    {
        _executor.ParseToolCall("").ShouldBeNull();
        _executor.ParseToolCall(null!).ShouldBeNull();
    }

    [Fact]
    public void ParseToolCall_HandlesEmptyParameters()
    {
        var json = """{"action": "ares_lookup"}""";

        var result = _executor.ParseToolCall(json);

        result.ShouldNotBeNull();
        result.Action.ShouldBe("ares_lookup");
        result.Parameters.ShouldBeEmpty();
    }

    // ─── ExecuteToolAsync Tests ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteToolAsync_DispatchesToCorrectTool()
    {
        var toolCall = new ParsedToolCall
        {
            Action = "ares_lookup",
            Parameters = new Dictionary<string, string> { ["registration_number"] = "12345678" }
        };

        var result = await _executor.ExecuteToolAsync(toolCall);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Test s.r.o.");

        // Verify the correct tool was called.
        await _mockAresTool.Received(1).ExecuteAsync(
            Arg.Is<Dictionary<string, string>>(d => d["registration_number"] == "12345678"),
            Arg.Any<CancellationToken>());

        // Verify the other tool was NOT called.
        await _mockCreateTool.DidNotReceive().ExecuteAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteToolAsync_ReturnsFailure_ForUnknownTool()
    {
        var toolCall = new ParsedToolCall
        {
            Action = "nonexistent_tool",
            Parameters = new Dictionary<string, string>()
        };

        var result = await _executor.ExecuteToolAsync(toolCall);

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Unknown tool");
        result.OutputText.ShouldContain("ares_lookup"); // Should list available tools
    }

    [Fact]
    public async Task ExecuteToolAsync_CatchesUnhandledException()
    {
        // Make the tool throw an unexpected exception.
        _mockAresTool
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns<ChatToolResult>(x => throw new InvalidOperationException("Unexpected DB error"));

        var toolCall = new ParsedToolCall
        {
            Action = "ares_lookup",
            Parameters = new Dictionary<string, string> { ["registration_number"] = "12345678" }
        };

        var result = await _executor.ExecuteToolAsync(toolCall);

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Tool execution failed");
    }

    // ─── BuildToolInstructions Tests ──────────────────────────────────────

    [Fact]
    public void BuildToolInstructions_ContainsAllToolNames()
    {
        var instructions = _executor.BuildToolInstructions();

        instructions.ShouldContain("ares_lookup");
        instructions.ShouldContain("create_client");
    }

    [Fact]
    public void BuildToolInstructions_ContainsJsonFormat()
    {
        var instructions = _executor.BuildToolInstructions();

        // Should contain the JSON format example.
        instructions.ShouldContain("\"action\"");
        instructions.ShouldContain("\"parameters\"");
        instructions.ShouldContain("registration_number");
    }

    // ─── AvailableTools Tests ─────────────────────────────────────────────

    [Fact]
    public void AvailableTools_ReturnsAllRegisteredTools()
    {
        var tools = _executor.AvailableTools;

        tools.Count.ShouldBe(4);
        tools.ShouldContain("ares_lookup");
        tools.ShouldContain("create_client");
        tools.ShouldContain("navigate");
        tools.ShouldContain("create_invoice");
    }

    [Fact]
    public void BuildToolInstructions_ContainsNavigateToolInfo()
    {
        var instructions = _executor.BuildToolInstructions();

        // Should contain navigate-specific instructions.
        instructions.ShouldContain("navigate");
        instructions.ShouldContain("new_invoice");
        instructions.ShouldContain("client_detail");
    }

    [Fact]
    public void BuildToolInstructions_ContainsCreateInvoiceToolInfo()
    {
        var instructions = _executor.BuildToolInstructions();

        // Should contain create_invoice-specific instructions.
        instructions.ShouldContain("create_invoice");
        instructions.ShouldContain("client_name");
        instructions.ShouldContain("items");
    }

    // ─── Invoice Creation Intent Detection Tests ─────────────────────────

    [Theory]
    [InlineData("Vytvoř fakturu pro Alza za mléko na 999,-")]
    [InlineData("Vytvor fakturu za 500 pro klienta ABC")]
    [InlineData("Udělej fakturu pro firmu XYZ za služby na 1000")]
    [InlineData("Vystavit fakturu za konzultaci 2000")]
    [InlineData("create invoice for Alza for 999")]
    [InlineData("make invoice for 500")]
    [InlineData("generate invoice for client ABC for 1000")]
    [InlineData("Faktura za mléko na 50")]
    [InlineData("Faktura pro Alza za 999")]
    public void DetectToolIntent_ReturnsTrue_ForInvoiceCreationKeywords(string message)
    {
        _executor.DetectToolIntent(message).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Co je faktura?")]                  // Question about invoices, not creation
    [InlineData("Kolik faktur mám?")]               // Question about count
    [InlineData("Kolik stojí mléko?")]              // Price question, not invoice
    public void DetectToolIntent_ReturnsFalse_ForInvoiceQuestions(string message)
    {
        _executor.DetectToolIntent(message).ShouldBeFalse();
    }
}
