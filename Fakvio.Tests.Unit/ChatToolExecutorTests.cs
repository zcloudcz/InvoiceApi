using Fakvio.Application.Service;
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
}
