using System.Text.Json;
using System.Text.RegularExpressions;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Orchestrates chat tool detection, AI interaction, and tool execution.
///
/// How the "two-pass" flow works (called by ChatService):
/// 1. DetectToolIntent() — fast regex: does the message contain an IČO + relevant keyword?
/// 2. If yes → ChatService makes a non-streaming AI call with BuildToolInstructions() appended
/// 3. ParseToolCall() — extracts JSON tool call from the AI response
/// 4. ExecuteToolAsync() — dispatches to the matching IChatTool
/// 5. ChatService makes a streaming AI call with the tool result injected
///
/// Why regex-based intent detection (not sending every message to the AI)?
/// Performance: regex is ~0ms, AI call is ~500-2000ms. For 95% of messages
/// that don't need tools, we avoid the overhead entirely.
///
/// All registered IChatTool implementations are injected via IEnumerable{IChatTool} from DI.
/// To add a new tool: implement IChatTool, register it in DI, and it's automatically available.
///
/// Junior note: This uses the "partial class" feature with [GeneratedRegex] attributes
/// to get compiled regex at build time (faster than runtime-compiled regex).
/// </summary>
public partial class ChatToolExecutor : IChatToolExecutor
{
    private readonly Dictionary<string, IChatTool> _tools;
    private readonly ILogger<ChatToolExecutor> _logger;

    public ChatToolExecutor(
        IEnumerable<IChatTool> tools,
        ILogger<ChatToolExecutor> logger)
    {
        _logger = logger;

        // Build a case-insensitive lookup dictionary from all registered tools.
        _tools = tools.ToDictionary(
            t => t.ToolName,
            t => t,
            StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("ChatToolExecutor initialized with {Count} tools: {Tools}",
            _tools.Count, string.Join(", ", _tools.Keys));
    }

    /// <summary>
    /// List of available tool names (for logging/debugging).
    /// </summary>
    public IReadOnlyList<string> AvailableTools => _tools.Keys.ToList().AsReadOnly();

    // ─── Compiled Regex Patterns ──────────────────────────────────────────

    /// <summary>
    /// Matches 8-digit sequences that look like Czech IČO numbers.
    /// Supports formats: "IČO 12345678", "ICO: 12345678", "ičo:12345678", standalone "12345678".
    /// Word boundaries (\b) prevent matching random 8-digit substrings of longer numbers.
    /// </summary>
    [GeneratedRegex(@"(?:IČO|ICO|ičo|ico)[\s:]*(\d{8})\b|\b(\d{8})\b", RegexOptions.Compiled)]
    private static partial Regex IcoPattern();

    /// <summary>
    /// Matches Czech and English keywords indicating the user wants to interact
    /// with company/client data. Case-insensitive.
    ///
    /// Czech: klient, firma, ARES, založ, najdi, vyhledej, společnost, etc.
    /// English: client, company, create, lookup, find, search, ARES, register, etc.
    /// </summary>
    [GeneratedRegex(
        @"\b(klient|firma|založ|zaloz|zaklad|najdi|najít|najit|vyhledej|hledej|ares|společnost|spolecnost|" +
        @"client|company|create|lookup|find|search|register)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ToolKeywordPattern();

    /// <summary>
    /// Matches Czech and English keywords indicating the user wants to navigate
    /// somewhere in the application or open a page/form.
    ///
    /// Czech: otevři, ukaž, přejdi, naviguj, zobraz, jdi na, nová faktura, nový klient, dobropis, etc.
    /// English: open, show, go to, navigate, display, new invoice, new client, new credit note, etc.
    ///
    /// This is a separate path from IČO + keyword detection — navigation doesn't require an IČO.
    /// </summary>
    [GeneratedRegex(
        @"\b(otevři|otevri|ukaž|ukaz|přejdi|prejdi|naviguj|zobraz|zobrazit|" +
        @"jdi na|jdi do|přejít|prejit|otevřít|otevrit|" +
        @"nová faktura|nova faktura|nový klient|novy klient|nový dobropis|novy dobropis|" +
        @"seznam faktur|seznam klientů|seznam klientu|" +
        @"open|show|go to|navigate|display|" +
        @"new invoice|new client|new credit note|" +
        @"invoice list|client list)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NavigationKeywordPattern();

    /// <summary>
    /// Matches Czech and English keywords indicating the user wants to CREATE an invoice
    /// (not just navigate to the create form). Must be combined with item/price context
    /// to distinguish "create invoice" (tool) from "open new invoice" (navigation).
    ///
    /// Czech: vytvoř/udělej/vystavit fakturu, faktura za/na (with amount context)
    /// English: create/make/generate invoice
    ///
    /// This pattern detects the intent to actually create a document, not just open a form.
    /// </summary>
    [GeneratedRegex(
        @"\b(vytvoř|vytvor|udělej|udelej|vystavit|vystav|vytvořit|vytvorit|" +
        @"create|make|generate|issue)\b.*\b(fakturu|faktura|faktur|invoice|dobropis|credit note)\b|" +
        @"\b(fakturu|faktura|invoice|dobropis|credit note)\b.*\b(za|na|for)\b.*\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InvoiceCreationPattern();

    // ─── Intent Detection ─────────────────────────────────────────────────

    /// <summary>
    /// Fast check: does the user's message indicate a need for tool processing?
    ///
    /// Two paths can trigger tool intent:
    /// 1. IČO pattern + tool keyword (e.g., "Najdi firmu s IČO 12345678")
    /// 2. Navigation keyword (e.g., "Otevři novou fakturu", "Ukaž mi klienta ABC")
    ///
    /// Deliberately conservative — better to miss a tool call (the AI can still answer in text)
    /// than to add ~1-2s latency to every regular message with an unnecessary first-pass AI call.
    /// </summary>
    public bool DetectToolIntent(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        // Path 1: IČO pattern + tool keyword (for ARES lookup / client creation).
        if (IcoPattern().IsMatch(userMessage) && ToolKeywordPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: IČO pattern + keyword match");
            return true;
        }

        // Path 2: Navigation keyword (no IČO required).
        if (NavigationKeywordPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: navigation keyword match");
            return true;
        }

        // Path 3: Invoice creation keyword + item/price context.
        // Example: "Vytvoř fakturu pro Alza za mléko na 999,-"
        if (InvoiceCreationPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: invoice creation pattern match");
            return true;
        }

        return false;
    }

    // ─── Tool Instructions for System Prompt ──────────────────────────────

    /// <summary>
    /// Builds tool instructions to append to the system prompt.
    /// These tell the AI how to format tool calls.
    ///
    /// IMPORTANT: Instructions are kept very simple and explicit for Ollama llama3.1:8b.
    /// Small models need clear, unambiguous instructions with examples.
    /// </summary>
    public string BuildToolInstructions()
    {
        // Build the list of available tools with their descriptions.
        var toolDescriptions = string.Join("\n", _tools.Values.Select(t =>
            $"  - {t.ToolName}: {t.Description}\n    Parameters: {t.ParameterDescription}"));

        return
            "\n\nTOOLS:\n" +
            "You have access to these tools:\n" +
            $"{toolDescriptions}\n\n" +
            "WHEN TO USE TOOLS:\n" +
            "- If the user mentions an IČO and wants to look up a company, use \"ares_lookup\".\n" +
            "- If the user explicitly asks to create/add/register a client, use \"create_client\".\n" +
            "- If unsure whether to look up or create, use \"ares_lookup\" first (read-only, safer).\n" +
            "- If the user wants to open a page, navigate somewhere, or view something, use \"navigate\".\n" +
            "  Valid targets: new_invoice, new_credit_note, client_detail, client_list, invoice_list, new_client.\n" +
            "  If a client is mentioned, include \"client_name\" in parameters.\n" +
            "- If the user wants to CREATE an invoice (not just open the form), use \"create_invoice\".\n" +
            "  IMPORTANT: Use \"create_invoice\" when the user provides item details (description, price).\n" +
            "  Use \"navigate\" with target \"new_invoice\" when the user just wants to open the form.\n" +
            "  The items parameter must be a JSON array. Extract items from the user's message.\n\n" +
            "HOW TO USE TOOLS:\n" +
            "Your ENTIRE response must be ONLY this JSON, nothing else:\n" +
            "{\"action\": \"tool_name\", \"parameters\": {\"key\": \"value\"}}\n\n" +
            "Examples:\n" +
            "- Look up IČO 12345678: {\"action\": \"ares_lookup\", \"parameters\": {\"registration_number\": \"12345678\"}}\n" +
            "- Open new invoice: {\"action\": \"navigate\", \"parameters\": {\"target\": \"new_invoice\"}}\n" +
            "- Open new invoice for client ABC: {\"action\": \"navigate\", \"parameters\": {\"target\": \"new_invoice\", \"client_name\": \"ABC\"}}\n" +
            "- Show client detail: {\"action\": \"navigate\", \"parameters\": {\"target\": \"client_detail\", \"client_name\": \"ABC\"}}\n" +
            "- Open client list: {\"action\": \"navigate\", \"parameters\": {\"target\": \"client_list\"}}\n" +
            "- Create invoice: {\"action\": \"create_invoice\", \"parameters\": {\"client_name\": \"Alza\", \"items\": \"[{\\\"description\\\": \\\"Mléko\\\", \\\"quantity\\\": 1, \\\"unit_price\\\": 999}]\"}}\n" +
            "- Create invoice with multiple items: {\"action\": \"create_invoice\", \"parameters\": {\"client_name\": \"ABC\", \"items\": \"[{\\\"description\\\": \\\"Item 1\\\", \\\"quantity\\\": 2, \\\"unit_price\\\": 500}, {\\\"description\\\": \\\"Item 2\\\", \\\"quantity\\\": 1, \\\"unit_price\\\": 300}]\"}}\n\n" +
            "If no tool is needed, respond normally with text.";
    }

    // ─── Parse Tool Call from AI Response ──────────────────────────────────

    /// <summary>
    /// Attempts to parse a tool call from the AI's response text.
    /// Expected format: {"action": "ares_lookup", "parameters": {"registration_number": "12345678"}}
    ///
    /// Returns null if the response is not a valid tool call.
    /// Handles edge cases:
    /// - AI wraps JSON in markdown code blocks (```json ... ```)
    /// - AI includes preamble text before the JSON
    /// - Malformed or missing "action" property
    /// </summary>
    public ParsedToolCall? ParseToolCall(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
            return null;

        var cleanedResponse = aiResponse.Trim();

        // Strip markdown code block wrappers if present.
        // Ollama/LLMs sometimes wrap JSON in ```json ... ``` blocks.
        if (cleanedResponse.StartsWith("```"))
        {
            var lines = cleanedResponse.Split('\n');
            var jsonLines = lines
                .SkipWhile(l => l.TrimStart().StartsWith("```"))
                .TakeWhile(l => !l.TrimStart().StartsWith("```"))
                .ToArray();
            cleanedResponse = string.Join('\n', jsonLines).Trim();
        }

        // Find the first JSON object in the response.
        // The AI might include preamble text like "Sure, I'll look that up." before the JSON.
        var jsonStart = cleanedResponse.IndexOf('{');
        var jsonEnd = cleanedResponse.LastIndexOf('}');

        if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
            return null;

        var jsonString = cleanedResponse[jsonStart..(jsonEnd + 1)];

        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            // Must have an "action" property to be a valid tool call.
            if (!root.TryGetProperty("action", out var actionElement))
                return null;

            var action = actionElement.GetString();
            if (string.IsNullOrEmpty(action))
                return null;

            // Extract parameters (optional — some tools might not need params).
            var parameters = new Dictionary<string, string>();
            if (root.TryGetProperty("parameters", out var paramsElement) &&
                paramsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in paramsElement.EnumerateObject())
                {
                    parameters[prop.Name] = prop.Value.GetString() ?? prop.Value.ToString();
                }
            }

            _logger.LogInformation("Parsed tool call: action={Action}, parameters={Parameters}",
                action, string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}")));

            return new ParsedToolCall
            {
                Action = action,
                Parameters = parameters
            };
        }
        catch (JsonException ex)
        {
            // Not valid JSON — this is a normal text response, not an error.
            _logger.LogDebug(ex, "AI response is not a JSON tool call (first 200 chars): {Response}",
                aiResponse[..Math.Min(200, aiResponse.Length)]);
            return null;
        }
    }

    // ─── Tool Execution ───────────────────────────────────────────────────

    /// <summary>
    /// Finds the matching tool by action name and executes it.
    /// Returns a failure result if the requested tool is not registered.
    /// </summary>
    public async Task<ChatToolResult> ExecuteToolAsync(
        ParsedToolCall toolCall,
        CancellationToken ct = default)
    {
        // Look up the tool in the dictionary (case-insensitive).
        if (!_tools.TryGetValue(toolCall.Action, out var tool))
        {
            _logger.LogWarning("Unknown tool requested: {Action}. Available: {Available}",
                toolCall.Action, string.Join(", ", _tools.Keys));
            return ChatToolResult.Failure(
                $"Unknown tool: {toolCall.Action}. Available tools: {string.Join(", ", _tools.Keys)}");
        }

        _logger.LogInformation("Executing tool {ToolName} with parameters: {Parameters}",
            tool.ToolName,
            string.Join(", ", toolCall.Parameters.Select(kv => $"{kv.Key}={kv.Value}")));

        try
        {
            var result = await tool.ExecuteAsync(toolCall.Parameters, ct);

            _logger.LogInformation("Tool {ToolName} completed: IsSuccess={IsSuccess}",
                tool.ToolName, result.IsSuccess);

            return result;
        }
        catch (Exception ex)
        {
            // Catch any unhandled exception from the tool to prevent the chat from crashing.
            _logger.LogError(ex, "Tool {ToolName} threw an unhandled exception", tool.ToolName);
            return ChatToolResult.Failure($"Tool execution failed: {ex.Message}");
        }
    }
}
