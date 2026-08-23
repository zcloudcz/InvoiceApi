using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ChatToolExecutor.
/// Covers the four things the executor is responsible for:
/// - startup validation of the tool parameter schemas
/// - generating the native tool definitions (JSON Schema) from those schemas
/// - generating the text-based tool instructions from those schemas
/// - central parameter validation + dispatch in ExecuteToolAsync
///
/// Uses NSubstitute mocks for IChatTool implementations, so the tests describe the
/// contract every tool must satisfy without depending on any concrete tool.
/// </summary>
public class ChatToolExecutorTests
{
    private readonly ChatToolExecutor _executor;
    private readonly IChatTool _mockAresTool;
    private readonly IChatTool _mockCreateTool;

    public ChatToolExecutorTests()
    {
        // Mock ARES lookup tool — one required string parameter.
        _mockAresTool = CreateTool("ares_lookup", "Looks up a company in ARES",
            new ChatToolParameter
            {
                Name = "registration_number",
                Type = ChatToolParameterType.String,
                Description = "Czech company IČO, exactly 8 digits",
                IsRequired = true
            });
        _mockAresTool
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("Company found: Test s.r.o."));

        // Mock create client tool.
        _mockCreateTool = CreateTool("create_client", "Creates a client",
            new ChatToolParameter
            {
                Name = "registration_number",
                Type = ChatToolParameterType.String,
                Description = "Czech company IČO, exactly 8 digits",
                IsRequired = true
            });
        _mockCreateTool
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("Client created: Test s.r.o."));

        // Mock navigate tool — required parameter with a closed value list.
        var mockNavigateTool = CreateTool("navigate", "Navigates to a page",
            new ChatToolParameter
            {
                Name = "target",
                Type = ChatToolParameterType.String,
                Description = "Where to navigate",
                IsRequired = true,
                AllowedValues = ["new_invoice", "client_detail", "client_list"]
            },
            new ChatToolParameter
            {
                Name = "client_name",
                Type = ChatToolParameterType.String,
                Description = "Client name to pre-select"
            });
        mockNavigateTool
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.SuccessWithAction("Navigating...",
                Fakvio.Contracts.Dto.Chat.ChatUiAction.Navigate("/invoices/create")));

        // Mock create invoice tool — mixes an array, a number and a boolean parameter.
        var mockCreateInvoiceTool = CreateTool("create_invoice", "Creates an invoice",
            new ChatToolParameter
            {
                Name = "client_name",
                Type = ChatToolParameterType.String,
                Description = "Client to invoice",
                IsRequired = true
            },
            new ChatToolParameter
            {
                Name = "items",
                Type = ChatToolParameterType.ObjectArray,
                Description = "Invoice line items",
                IsRequired = true
            },
            new ChatToolParameter
            {
                Name = "discount",
                Type = ChatToolParameterType.Number,
                Description = "Discount percentage"
            },
            new ChatToolParameter
            {
                Name = "send_email",
                Type = ChatToolParameterType.Boolean,
                Description = "Send the invoice by e-mail after creation"
            },
            new ChatToolParameter
            {
                Name = "copies",
                Type = ChatToolParameterType.Integer,
                Description = "Number of printed copies"
            });
        mockCreateInvoiceTool
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.SuccessWithAction("Invoice created.",
                Fakvio.Contracts.Dto.Chat.ChatUiAction.Navigate("/invoices/100")));

        _executor = CreateExecutor(_mockAresTool, _mockCreateTool, mockNavigateTool, mockCreateInvoiceTool);
    }

    // ─── Test helpers ─────────────────────────────────────────────────────

    private static IChatTool CreateTool(string name, string description, params ChatToolParameter[] parameters)
    {
        var tool = Substitute.For<IChatTool>();
        tool.ToolName.Returns(name);
        tool.Description.Returns(description);
        tool.Parameters.Returns(parameters);
        return tool;
    }

    private static ChatToolExecutor CreateExecutor(params IChatTool[] tools)
        => new(tools, Substitute.For<ILogger<ChatToolExecutor>>());

    /// <summary>Valid parameters for the create_invoice mock — used as a baseline in type tests.</summary>
    private static Dictionary<string, string> ValidInvoiceParameters() => new()
    {
        ["client_name"] = "Alza",
        ["items"] = """[{"description": "Mléko", "quantity": 1, "unit_price": 999}]"""
    };

    // ─── Schema validation (startup) ──────────────────────────────────────

    [Fact]
    public void Constructor_Throws_WhenParameterNameIsDuplicated()
    {
        var brokenTool = CreateTool("broken", "Broken tool",
            new ChatToolParameter { Name = "id", Type = ChatToolParameterType.String, Description = "First" },
            new ChatToolParameter { Name = "ID", Type = ChatToolParameterType.String, Description = "Second" });

        var ex = Should.Throw<InvalidOperationException>(() => CreateExecutor(brokenTool));
        ex.Message.ShouldContain("more than once");
    }

    [Fact]
    public void Constructor_Throws_WhenParameterNameIsEmpty()
    {
        var brokenTool = CreateTool("broken", "Broken tool",
            new ChatToolParameter { Name = "  ", Type = ChatToolParameterType.String, Description = "No name" });

        Should.Throw<InvalidOperationException>(() => CreateExecutor(brokenTool));
    }

    [Fact]
    public void Constructor_Throws_WhenParameterHasNoDescription()
    {
        var brokenTool = CreateTool("broken", "Broken tool",
            new ChatToolParameter { Name = "id", Type = ChatToolParameterType.String, Description = "" });

        Should.Throw<InvalidOperationException>(() => CreateExecutor(brokenTool));
    }

    [Fact]
    public void Constructor_Throws_WhenAllowedValuesAreUsedOnNonStringParameter()
    {
        var brokenTool = CreateTool("broken", "Broken tool",
            new ChatToolParameter
            {
                Name = "page",
                Type = ChatToolParameterType.Integer,
                Description = "Page number",
                AllowedValues = ["1", "2"]
            });

        var ex = Should.Throw<InvalidOperationException>(() => CreateExecutor(brokenTool));
        ex.Message.ShouldContain("allowed values");
    }

    [Fact]
    public void Constructor_Throws_WhenAllowedValuesAreEmpty()
    {
        var brokenTool = CreateTool("broken", "Broken tool",
            new ChatToolParameter
            {
                Name = "target",
                Type = ChatToolParameterType.String,
                Description = "Target",
                AllowedValues = []
            });

        Should.Throw<InvalidOperationException>(() => CreateExecutor(brokenTool));
    }

    [Fact]
    public void Constructor_Accepts_ToolWithoutParameters()
    {
        var executor = CreateExecutor(CreateTool("ping", "Does nothing"));

        executor.AvailableTools.ShouldContain("ping");
    }

    // ─── GetToolDefinitions Tests ─────────────────────────────────────────

    [Fact]
    public void GetToolDefinitions_GeneratesDefinitionForEveryRegisteredTool()
    {
        var definitions = _executor.GetToolDefinitions();

        definitions.Count.ShouldBe(4);
        definitions.Select(d => d.Name).ShouldContain("ares_lookup");
        definitions.Single(d => d.Name == "ares_lookup").Description.ShouldBe("Looks up a company in ARES");
    }

    [Fact]
    public void GetToolDefinitions_MapsRequiredParameters()
    {
        var definition = _executor.GetToolDefinitions().Single(d => d.Name == "navigate");

        definition.Required.ShouldBe(["target"]);
        definition.Parameters.Select(p => p.Name).ShouldBe(["target", "client_name"]);
    }

    [Fact]
    public void GetToolDefinitions_MapsAllowedValuesToEnum()
    {
        var target = _executor.GetToolDefinitions()
            .Single(d => d.Name == "navigate")
            .Parameters.Single(p => p.Name == "target");

        target.EnumValues.ShouldBe(["new_invoice", "client_detail", "client_list"]);
    }

    [Fact]
    public void GetToolDefinitions_UsesRealJsonSchemaTypes()
    {
        var parameters = _executor.GetToolDefinitions()
            .Single(d => d.Name == "create_invoice")
            .Parameters.ToDictionary(p => p.Name, p => p.Type);

        parameters["client_name"].ShouldBe("string");
        parameters["items"].ShouldBe("array");
        parameters["discount"].ShouldBe("number");
        parameters["send_email"].ShouldBe("boolean");
        parameters["copies"].ShouldBe("integer");
    }

    [Fact]
    public void GetToolDefinitions_DeclaresElementTypeForArrayParameters()
    {
        var parameters = _executor.GetToolDefinitions()
            .Single(d => d.Name == "create_invoice")
            .Parameters;

        // Arrays must carry an element schema; scalars must not.
        parameters.Single(p => p.Name == "items").ArrayItemType.ShouldBe("object");
        parameters.Single(p => p.Name == "client_name").ArrayItemType.ShouldBeNull();
    }

    [Fact]
    public void GetToolDefinitions_DoesNotInventParameters_ForToolWithoutSchema()
    {
        // Regression guard: the old implementation fell back to a single "input" parameter
        // for tools missing from a hardcoded switch, which silently broke them.
        var executor = CreateExecutor(CreateTool("ping", "Does nothing"));

        var definition = executor.GetToolDefinitions().Single();

        definition.Parameters.ShouldBeEmpty();
        definition.Required.ShouldBeEmpty();
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
    public void ParseToolCall_KeepsRawJson_ForNonStringValues()
    {
        // With a typed schema the model sends real numbers, booleans and arrays.
        var json = """
                   {"action": "create_invoice", "parameters": {
                     "client_name": "Alza",
                     "items": [{"description": "Mléko", "unit_price": 999}],
                     "discount": 10.5,
                     "send_email": true}}
                   """;

        var result = _executor.ParseToolCall(json);

        result.ShouldNotBeNull();
        result.Parameters["items"].ShouldStartWith("[");
        result.Parameters["discount"].ShouldBe("10.5");
        result.Parameters["send_email"].ShouldBe("true");
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

    [Fact]
    public void ParseToolCall_DropsJsonNullValues_InsteadOfTurningThemIntoTheText_null()
    {
        // A model that has no value for a parameter often sends a JSON null literal.
        // GetRawText() would turn that into the four-character string "null", which
        // every downstream check (blank test, string type check, tool-side
        // IsNullOrWhiteSpace guards) happily accepts — and "null" ends up persisted.
        var json = """{"action": "create_invoice", "parameters": {"client_name": "Alza", "notes": null}}""";

        var result = _executor.ParseToolCall(json);

        result.ShouldNotBeNull();
        result.Parameters.ShouldNotContainKey("notes");
    }

    [Fact]
    public async Task ExecuteToolAsync_RejectsRequiredParameterSentAsJsonNull()
    {
        // End-to-end proof of the same defect: a required parameter sent as JSON null
        // must be reported as missing, not silently accepted as the value "null".
        var parsed = _executor.ParseToolCall(
            """{"action": "ares_lookup", "parameters": {"registration_number": null}}""");
        parsed.ShouldNotBeNull();

        var result = await _executor.ExecuteToolAsync(parsed);

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("missing required parameter");
        await _mockAresTool.DidNotReceive().ExecuteAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"action": 123, "parameters": {}}""")]              // number
    [InlineData("""{"action": true, "parameters": {}}""")]             // boolean
    [InlineData("""{"action": null, "parameters": {}}""")]             // null literal
    [InlineData("""{"action": ["ares_lookup"], "parameters": {}}""")]  // array
    [InlineData("""{"action": {"name": "ares_lookup"}}""")]            // object
    public void ParseToolCall_ReturnsNull_ForNonStringAction(string json)
    {
        // GetString() throws InvalidOperationException on a non-string element, and that
        // is not a JsonException — it would escape the catch and kill the chat turn.
        // A malformed action is a plain text answer, so it must simply parse as "no tool call".
        var result = _executor.ParseToolCall(json);

        result.ShouldBeNull();
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

    // ─── Central parameter validation ─────────────────────────────────────

    [Fact]
    public async Task ExecuteToolAsync_RejectsMissingRequiredParameter_WithoutRunningTheTool()
    {
        var toolCall = new ParsedToolCall
        {
            Action = "ares_lookup",
            Parameters = new Dictionary<string, string>()
        };

        var result = await _executor.ExecuteToolAsync(toolCall);

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("registration_number");
        await _mockAresTool.DidNotReceive().ExecuteAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteToolAsync_TreatsBlankRequiredParameterAsMissing()
    {
        var toolCall = new ParsedToolCall
        {
            Action = "ares_lookup",
            Parameters = new Dictionary<string, string> { ["registration_number"] = "   " }
        };

        var result = await _executor.ExecuteToolAsync(toolCall);

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("missing required parameter");
    }

    [Fact]
    public async Task ExecuteToolAsync_ReportsAllInvalidParametersAtOnce()
    {
        var toolCall = new ParsedToolCall
        {
            Action = "create_invoice",
            Parameters = new Dictionary<string, string>()
        };

        var result = await _executor.ExecuteToolAsync(toolCall);

        result.OutputText.ShouldContain("client_name");
        result.OutputText.ShouldContain("items");
    }

    [Fact]
    public async Task ExecuteToolAsync_AllowsBlankOptionalParameter()
    {
        // Models like to send empty strings for optional parameters they have no value for.
        var parameters = ValidInvoiceParameters();
        parameters["discount"] = "";

        var result = await _executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "create_invoice",
            Parameters = parameters
        });

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("discount", "abc")]      // not a number
    [InlineData("copies", "1.5")]        // not an integer
    [InlineData("send_email", "yes")]    // not a boolean
    [InlineData("items", "not json")]    // not an array
    public async Task ExecuteToolAsync_RejectsValueOfWrongType(string parameterName, string value)
    {
        var parameters = ValidInvoiceParameters();
        parameters[parameterName] = value;

        var result = await _executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "create_invoice",
            Parameters = parameters
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain(parameterName);
    }

    [Theory]
    [InlineData("discount", "10.5")]
    [InlineData("copies", "-2")]
    [InlineData("send_email", "TRUE")]
    [InlineData("items", "[]")]
    public async Task ExecuteToolAsync_AcceptsWellTypedValue(string parameterName, string value)
    {
        var parameters = ValidInvoiceParameters();
        parameters[parameterName] = value;

        var result = await _executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "create_invoice",
            Parameters = parameters
        });

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteToolAsync_RejectsValueOutsideAllowedValues()
    {
        var result = await _executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "navigate",
            Parameters = new Dictionary<string, string> { ["target"] = "dashboard" }
        });

        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("must be one of");
        result.OutputText.ShouldContain("new_invoice");
    }

    [Fact]
    public async Task ExecuteToolAsync_AcceptsAllowedValueRegardlessOfCasing()
    {
        // Models are inconsistent about casing — rejecting "New_Invoice" would be a regression.
        var result = await _executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "navigate",
            Parameters = new Dictionary<string, string> { ["target"] = "New_Invoice" }
        });

        result.IsSuccess.ShouldBeTrue();
    }

    // ─── BuildToolInstructions Tests ──────────────────────────────────────

    [Fact]
    public void BuildToolInstructions_ContainsAllToolNames()
    {
        var instructions = _executor.BuildToolInstructions();

        instructions.ShouldContain("ares_lookup");
        instructions.ShouldContain("create_client");
        instructions.ShouldContain("navigate");
        instructions.ShouldContain("create_invoice");
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

    [Fact]
    public void BuildToolInstructions_DescribesParameterTypeAndRequirement()
    {
        var instructions = _executor.BuildToolInstructions();

        instructions.ShouldContain("registration_number (string, required)");
        instructions.ShouldContain("client_name (string, optional)");
        instructions.ShouldContain("items (array, required)");
    }

    [Fact]
    public void BuildToolInstructions_ListsAllowedValues()
    {
        var instructions = _executor.BuildToolInstructions();

        instructions.ShouldContain("new_invoice | client_detail | client_list");
    }

    [Fact]
    public void BuildToolInstructions_StatesWhenAToolTakesNoParameters()
    {
        var executor = CreateExecutor(CreateTool("ping", "Does nothing"));

        executor.BuildToolInstructions().ShouldContain("Parameters: none");
    }

    [Fact]
    public void BuildToolInstructions_ShowsOneExampleCallPerTool()
    {
        // Small models copy the example shape. Every tool needs one, and it has to be
        // generated from the schema so it can never drift from the real parameter list.
        var instructions = _executor.BuildToolInstructions();

        instructions.ShouldContain("""{"action": "ares_lookup", "parameters": {"registration_number": "<registration_number>"}}""");
        instructions.ShouldContain("""{"action": "create_client", "parameters": {"registration_number": "<registration_number>"}}""");
    }

    [Fact]
    public void BuildToolInstructions_ExampleUsesFirstAllowedValue_ForRestrictedParameter()
    {
        var instructions = _executor.BuildToolInstructions();

        instructions.ShouldContain("""{"action": "navigate", "parameters": {"target": "new_invoice"}}""");
    }

    [Fact]
    public void BuildToolInstructions_ExampleOmitsOptionalParameters_AndLeavesNonStringsUnquoted()
    {
        var instructions = _executor.BuildToolInstructions();

        // create_invoice requires client_name (string) + items (array); discount,
        // send_email and copies are optional and must not appear in the example.
        instructions.ShouldContain("""{"action": "create_invoice", "parameters": {"client_name": "<client_name>", "items": [{"<field>": "<value>"}]}}""");
    }

    [Fact]
    public void BuildToolInstructions_ExampleForToolWithoutParameters_HasEmptyParameterObject()
    {
        var executor = CreateExecutor(CreateTool("ping", "Does nothing"));

        executor.BuildToolInstructions().ShouldContain("""{"action": "ping", "parameters": {}}""");
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

    // ─── Confirm gate (issue #212) ────────────────────────────────────────
    //
    // The money path: a tool that changes data must not run until the user has approved it.
    // Everything below is about proving that ExecuteAsync is NOT reached, not just that some
    // preview text came back — a gate that returns a preview AND writes is the worst outcome.

    /// <summary>
    /// A data-changing tool: preview says what would change, execution says it changed.
    /// Both are recorded by NSubstitute, so the tests can assert which one ran.
    /// </summary>
    private static IConfirmableChatTool CreateConfirmableTool(string name = "update_settings")
    {
        var tool = Substitute.For<IConfirmableChatTool>();
        tool.ToolName.Returns(name);
        tool.Description.Returns("Changes a company setting");
        tool.Parameters.Returns(new[]
        {
            new ChatToolParameter
            {
                Name = "value",
                Type = ChatToolParameterType.String,
                Description = "New value",
                IsRequired = true
            }
        });
        tool.BuildPreviewAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("Invoice numbering would change from FA-2025 to FA-2026."));
        tool.ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("Setting saved."));
        return tool;
    }

    private static ParsedToolCall ConfirmableCall(string? confirm)
    {
        var parameters = new Dictionary<string, string> { ["value"] = "FA-2026" };
        if (confirm != null)
            parameters["confirm"] = confirm;

        return new ParsedToolCall { Action = "update_settings", Parameters = parameters };
    }

    [Fact]
    public async Task ExecuteToolAsync_DoesNotWrite_WhenConfirmIsMissing()
    {
        var tool = CreateConfirmableTool();
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteToolAsync(ConfirmableCall(confirm: null));

        // The whole point: the tool's write path was never entered.
        await tool.DidNotReceive().ExecuteAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
        await tool.Received(1).BuildPreviewAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
        result.OutputText.ShouldContain("would change from FA-2025 to FA-2026");
        result.OutputText.ShouldContain("NOTHING HAS BEEN CHANGED YET");
    }

    /// <summary>
    /// Review round 1 (S1): a preview must not move the UI. ChatService hands UiAction straight
    /// to the browser, so a preview carrying one would navigate the user to a record that the
    /// unconfirmed tool has not created or changed yet.
    /// </summary>
    [Fact]
    public async Task ExecuteToolAsync_DropsUiAction_FromAPreview()
    {
        var tool = CreateConfirmableTool();
        tool.BuildPreviewAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.SuccessWithAction(
                "Numbering would change.", ChatUiAction.Navigate("/settings/numbering")));
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteToolAsync(ConfirmableCall(confirm: null));

        result.RequiresConfirmation.ShouldBeTrue();
        result.UiAction.ShouldBeNull();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public async Task ExecuteToolAsync_DoesNotWrite_WhenConfirmIsFalse(string confirm)
    {
        var tool = CreateConfirmableTool();
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteToolAsync(ConfirmableCall(confirm));

        await tool.DidNotReceive().ExecuteAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
        result.RequiresConfirmation.ShouldBeTrue();
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("ano")]
    public async Task ExecuteToolAsync_DoesNotWrite_WhenConfirmIsNotABoolean(string confirm)
    {
        // Fail closed: an unparsable flag is rejected by the schema validation, so neither the
        // write nor the preview runs. The model gets a message and can send a proper boolean.
        var tool = CreateConfirmableTool();
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteToolAsync(ConfirmableCall(confirm));

        await tool.DidNotReceive().ExecuteAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("confirm");
        result.ErrorMessage.ShouldContain("boolean");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public async Task ExecuteToolAsync_Writes_WhenConfirmed(string confirm)
    {
        var tool = CreateConfirmableTool();
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteToolAsync(ConfirmableCall(confirm));

        await tool.Received(1).ExecuteAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
        await tool.DidNotReceive().BuildPreviewAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
        result.OutputText.ShouldBe("Setting saved.");
    }

    [Fact]
    public async Task ExecuteToolAsync_ValidatesParametersBeforePreview()
    {
        // A preview built from parameters nobody validated would report a change the write
        // path could never perform. Validation therefore comes first — for both paths.
        var tool = CreateConfirmableTool();
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteToolAsync(
            new ParsedToolCall { Action = "update_settings", Parameters = new Dictionary<string, string>() });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("missing required parameter 'value'");
        await tool.DidNotReceive().BuildPreviewAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
        await tool.DidNotReceive().ExecuteAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteToolAsync_KeepsFailedPreviewAsPlainFailure()
    {
        // Nothing to confirm when the preview itself failed — inviting the model to retry
        // with confirm=true would send it straight into the same error, but writing this time.
        var tool = CreateConfirmableTool();
        tool.BuildPreviewAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Failure("Setting 'value' not found."));
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteToolAsync(ConfirmableCall(confirm: null));

        result.IsSuccess.ShouldBeFalse();
        result.RequiresConfirmation.ShouldBeFalse();
        result.OutputText.ShouldNotContain("NOTHING HAS BEEN CHANGED YET");
    }

    [Fact]
    public async Task ExecuteToolAsync_ReturnsFailure_WhenPreviewThrows()
    {
        var tool = CreateConfirmableTool();
        tool.BuildPreviewAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatToolResult>>(_ => throw new InvalidOperationException("DB down"));
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteToolAsync(ConfirmableCall(confirm: null));

        result.IsSuccess.ShouldBeFalse();
        result.RequiresConfirmation.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("DB down");
    }

    [Fact]
    public async Task ExecuteToolAsync_LeavesNonConfirmableToolsAlone()
    {
        // Read-only tools must not grow a confirmation step.
        var result = await _executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "ares_lookup",
            Parameters = new Dictionary<string, string> { ["registration_number"] = "12345678" }
        });

        result.RequiresConfirmation.ShouldBeFalse();
        await _mockAresTool.Received(1).ExecuteAsync(
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_Throws_WhenToolDeclaresTheReservedConfirmParameter()
    {
        var brokenTool = CreateTool("broken", "Broken tool",
            new ChatToolParameter
            {
                Name = "Confirm",
                Type = ChatToolParameterType.Boolean,
                Description = "Hand-rolled confirmation"
            });

        var ex = Should.Throw<InvalidOperationException>(() => CreateExecutor(brokenTool));
        ex.Message.ShouldContain("IConfirmableChatTool");
    }

    [Fact]
    public void GetToolDefinitions_AddsOptionalConfirmFlag_ToConfirmableToolsOnly()
    {
        var executor = CreateExecutor(CreateConfirmableTool(), _mockAresTool);

        var confirmable = executor.GetToolDefinitions().Single(d => d.Name == "update_settings");
        var readOnly = executor.GetToolDefinitions().Single(d => d.Name == "ares_lookup");

        var confirmParameter = confirmable.Parameters.Single(p => p.Name == "confirm");
        confirmParameter.Type.ShouldBe("boolean");
        confirmable.Required.ShouldNotContain("confirm",
            "a required flag would force the model to send it on the first call too");

        readOnly.Parameters.ShouldNotContain(p => p.Name == "confirm");
    }

    [Fact]
    public void BuildToolInstructions_ExplainsTheTwoStepConfirmFlow_WhenAConfirmableToolExists()
    {
        var instructions = CreateExecutor(CreateConfirmableTool()).BuildToolInstructions();

        instructions.ShouldContain("confirm (boolean, optional)");
        instructions.ShouldContain("call them WITHOUT it first");
    }

    [Fact]
    public void BuildToolInstructions_ExampleForConfirmableTool_OmitsConfirm()
    {
        // The example is the first call, which must be the preview one.
        var instructions = CreateExecutor(CreateConfirmableTool()).BuildToolInstructions();

        instructions.ShouldContain("""{"action": "update_settings", "parameters": {"value": "<value>"}}""");
    }

    [Fact]
    public void BuildToolInstructions_SaysNothingAboutConfirm_WhenNoToolIsConfirmable()
    {
        _executor.BuildToolInstructions().ShouldNotContain("confirm");
    }
}
