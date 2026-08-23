using System.Globalization;
using System.Text;
using System.Text.Json;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Orchestrates the AI chat tool catalog: prompt generation, tool-call parsing,
/// parameter validation and dispatch to the matching IChatTool.
///
/// Two flows exist (ChatService picks one based on the provider):
/// - Native tool calling: GetToolDefinitions() hands a JSON Schema to the provider,
///   the model answers with a structured tool call.
/// - Text-based tool calling: BuildToolInstructions() is appended to the system prompt,
///   ParseToolCall() extracts the JSON tool call from the model's plain-text answer.
///
/// Both flows converge on ExecuteToolAsync(), which validates the parameters against
/// the tool schema BEFORE the tool runs. Everything — prompt text, JSON Schema and
/// validation — is derived from IChatTool.Parameters, so a tool is described in
/// exactly one place: its own class.
///
/// All registered IChatTool implementations are injected via IEnumerable{IChatTool} from DI.
/// To add a new tool: implement IChatTool (including its parameter schema) and register it in DI.
/// </summary>
public class ChatToolExecutor : IChatToolExecutor
{
    private readonly Dictionary<string, IChatTool> _tools;
    private readonly ILogger<ChatToolExecutor> _logger;

    public ChatToolExecutor(
        IEnumerable<IChatTool> tools,
        ILogger<ChatToolExecutor> logger)
    {
        _logger = logger;

        // Build a case-insensitive lookup dictionary from all registered tools.
        // ToDictionary throws on duplicate tool names — a registration mistake fails at startup.
        _tools = tools.ToDictionary(
            t => t.ToolName,
            t => t,
            StringComparer.OrdinalIgnoreCase);

        // Fail fast: a tool with a broken schema breaks the whole chat silently at runtime
        // (the model would call it with parameters nobody validates), so reject it at startup.
        foreach (var tool in _tools.Values)
        {
            ValidateSchema(tool);
        }

        _logger.LogInformation("ChatToolExecutor initialized with {Count} tools: {Tools}",
            _tools.Count, string.Join(", ", _tools.Keys));
    }

    /// <summary>
    /// List of available tool names (for logging/debugging).
    /// </summary>
    public IReadOnlyList<string> AvailableTools => _tools.Keys.ToList().AsReadOnly();

    // ─── Schema Validation (startup) ──────────────────────────────────────

    /// <summary>
    /// Verifies that a tool's parameter schema is usable before the application serves traffic.
    /// Catches the mistakes the compiler cannot: empty or duplicated parameter names,
    /// and enum constraints on non-string parameters (JSON Schema would silently ignore them).
    /// </summary>
    /// <exception cref="InvalidOperationException">The schema is invalid.</exception>
    private static void ValidateSchema(IChatTool tool)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in tool.Parameters)
        {
            // 'confirm' is appended by the confirm gate itself (ChatToolConfirmation) — a tool
            // that also declares it would produce a duplicated, self-contradicting schema.
            if (string.Equals(parameter.Name, ChatToolConfirmation.ParameterName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Chat tool '{tool.ToolName}' declares the reserved parameter " +
                    $"'{ChatToolConfirmation.ParameterName}' — implement IConfirmableChatTool instead.");

            if (string.IsNullOrWhiteSpace(parameter.Name))
                throw new InvalidOperationException(
                    $"Chat tool '{tool.ToolName}' declares a parameter with an empty name.");

            if (!seenNames.Add(parameter.Name))
                throw new InvalidOperationException(
                    $"Chat tool '{tool.ToolName}' declares parameter '{parameter.Name}' more than once.");

            if (string.IsNullOrWhiteSpace(parameter.Description))
                throw new InvalidOperationException(
                    $"Chat tool '{tool.ToolName}' parameter '{parameter.Name}' has no description — " +
                    "the model needs it to decide what to send.");

            if (parameter.AllowedValues is null)
                continue;

            if (parameter.AllowedValues.Count == 0)
                throw new InvalidOperationException(
                    $"Chat tool '{tool.ToolName}' parameter '{parameter.Name}' has an empty AllowedValues list.");

            if (parameter.Type != ChatToolParameterType.String)
                throw new InvalidOperationException(
                    $"Chat tool '{tool.ToolName}' parameter '{parameter.Name}' restricts values but is " +
                    $"of type {parameter.Type} — allowed values are only supported for string parameters.");
        }
    }

    // ─── Tool Instructions for System Prompt ──────────────────────────────

    /// <summary>
    /// Builds tool instructions to append to the system prompt (text-based flow).
    /// The tool list and parameter lines are generated from the tool schemas,
    /// so they can never drift from what the tools actually accept.
    ///
    /// IMPORTANT: instructions stay simple and explicit — small models
    /// (Ollama llama3.1:8b) need clear, unambiguous wording.
    /// </summary>
    public string BuildToolInstructions()
    {
        var sb = new StringBuilder();

        sb.Append("\n\nTOOLS:\n");
        sb.AppendLine("You have access to these tools:");

        foreach (var tool in _tools.Values)
        {
            sb.AppendLine($"  - {tool.ToolName}: {tool.Description}");

            var parameters = ChatToolConfirmation.EffectiveParameters(tool);
            if (parameters.Count == 0)
            {
                sb.AppendLine("    Parameters: none");
                continue;
            }

            sb.AppendLine("    Parameters:");
            foreach (var parameter in parameters)
            {
                sb.AppendLine($"      - {DescribeParameter(parameter)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("HOW TO USE TOOLS:");
        sb.AppendLine("Your ENTIRE response must be ONLY this JSON, nothing else:");
        sb.AppendLine("{\"action\": \"tool_name\", \"parameters\": {\"key\": \"value\"}}");
        sb.AppendLine("Use the exact parameter names listed above.");
        sb.AppendLine("Send numbers as numbers, booleans as true or false, and arrays as JSON arrays — never quoted.");
        sb.AppendLine("Omit optional parameters you have no value for — never invent one.");

        // Only emitted when something can actually be confirmed — a rule about a parameter
        // no registered tool has would just be noise for a small model.
        if (_tools.Values.Any(tool => tool is IConfirmableChatTool))
        {
            sb.AppendLine(
                $"Tools with a '{ChatToolConfirmation.ParameterName}' parameter change data: call them WITHOUT it first, " +
                $"show the returned preview to the user, and repeat the same call with " +
                $"\"{ChatToolConfirmation.ParameterName}\": true only after the user approves.");
        }

        sb.AppendLine();

        sb.AppendLine("EXAMPLES (required parameters only — replace the <placeholders> with real values):");
        foreach (var tool in _tools.Values)
        {
            sb.AppendLine($"  {BuildExampleCall(tool)}");
        }

        sb.AppendLine();
        sb.Append("If no tool is needed, respond normally with text.");

        return sb.ToString();
    }

    /// <summary>
    /// Renders one ready-to-copy call for the given tool, e.g.
    /// {"action": "navigate", "parameters": {"target": "new_invoice"}}.
    ///
    /// Generated from the schema rather than hand-written, so an example can never
    /// advertise a parameter the tool does not accept. Only required parameters appear —
    /// the instructions above already tell the model to omit the optional ones.
    /// </summary>
    private static string BuildExampleCall(IChatTool tool)
    {
        var arguments = string.Join(", ", tool.Parameters
            .Where(parameter => parameter.IsRequired)
            .Select(parameter => $"\"{parameter.Name}\": {ExampleValue(parameter)}"));

        // Plain concatenation on purpose: in an interpolated string every JSON brace would
        // have to be doubled, which is much harder to read than this.
        return "{\"action\": \"" + tool.ToolName + "\", \"parameters\": {" + arguments + "}}";
    }

    /// <summary>
    /// Placeholder value for one parameter — always valid JSON of the declared type,
    /// so a model that copies the example verbatim still produces a parsable call.
    /// Parameters with a closed value list show a real allowed value (the best guidance
    /// we can give); everything else shows a &lt;placeholder&gt; the model must replace.
    /// </summary>
    private static string ExampleValue(ChatToolParameter parameter)
    {
        if (parameter.AllowedValues is { Count: > 0 } allowed)
            return $"\"{allowed[0]}\"";

        return parameter.Type switch
        {
            ChatToolParameterType.Integer => "1",
            ChatToolParameterType.Number => "100.50",
            ChatToolParameterType.Boolean => "true",
            ChatToolParameterType.ObjectArray => "[{\"<field>\": \"<value>\"}]",
            _ => $"\"<{parameter.Name}>\""
        };
    }

    /// <summary>
    /// Renders one parameter as a single instruction line, e.g.
    /// "target (string, required, one of: new_invoice | client_list): Where to navigate".
    /// </summary>
    private static string DescribeParameter(ChatToolParameter parameter)
    {
        var requirement = parameter.IsRequired ? "required" : "optional";
        var allowed = parameter.AllowedValues is { Count: > 0 }
            ? $", one of: {string.Join(" | ", parameter.AllowedValues)}"
            : string.Empty;

        return $"{parameter.Name} ({parameter.Type.ToJsonSchemaType()}, {requirement}{allowed}): {parameter.Description}";
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

            // Must have a STRING "action" property to be a valid tool call.
            // The ValueKind check comes first because GetString() throws
            // InvalidOperationException on a number/boolean/array/object — and that is not a
            // JsonException, so it would escape the catch below and kill the whole chat turn.
            // A model that answers {"action": 123} is simply not calling a tool.
            if (!root.TryGetProperty("action", out var actionElement) ||
                actionElement.ValueKind != JsonValueKind.String)
                return null;

            var action = actionElement.GetString();
            if (string.IsNullOrEmpty(action))
                return null;

            // Extract parameters (optional — some tools might not need params).
            // The shared reader is what makes the text-based flow behave exactly like the
            // native provider flows, JSON nulls included.
            Dictionary<string, string> parameters =
                root.TryGetProperty("parameters", out var paramsElement)
                    ? ToolArgumentReader.ReadArguments(paramsElement)
                    : [];

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

    // ─── Native Tool Definitions ─────────────────────────────────────────

    /// <summary>
    /// Builds NativeToolDefinition list from all registered IChatTool instances.
    /// Used by providers that support native tool calling (Ollama, Claude, …).
    ///
    /// Junior note: native tool calling works much better than text-based instructions
    /// because models are fine-tuned to produce structured tool calls. The provider API
    /// enforces the JSON Schema we generate here, so the model cannot produce
    /// malformed output — which is why the schema must carry real types, not just strings.
    /// </summary>
    public List<NativeToolDefinition> GetToolDefinitions()
    {
        return _tools.Values.Select(tool =>
        {
            // Includes the confirm flag for confirmable tools — a provider with a strict schema
            // would otherwise reject the very call that carries the user's approval.
            var parameters = ChatToolConfirmation.EffectiveParameters(tool);

            return new NativeToolDefinition
            {
                Name = tool.ToolName,
                Description = tool.Description,
                Parameters = parameters.Select(parameter => new NativeToolParameter
                {
                    Name = parameter.Name,
                    Type = parameter.Type.ToJsonSchemaType(),
                    Description = parameter.Description,
                    EnumValues = parameter.AllowedValues?.ToList(),
                    ArrayItemType = parameter.Type.ToJsonSchemaItemType()
                }).ToList(),
                Required = parameters
                    .Where(parameter => parameter.IsRequired)
                    .Select(parameter => parameter.Name)
                    .ToList()
            };
        }).ToList();
    }

    // ─── Tool Execution ───────────────────────────────────────────────────

    /// <summary>
    /// Finds the matching tool by action name, validates the parameters against its schema
    /// and executes it. Returns a failure result if the tool is unknown or the parameters
    /// do not match the schema — the message goes back to the model, which can then retry
    /// with a corrected call.
    ///
    /// A tool implementing <see cref="IConfirmableChatTool"/> only executes when the call
    /// carries <c>confirm: true</c>; otherwise its preview is returned and nothing is written.
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

        // Central parameter validation — done once here instead of in every ExecuteAsync.
        var validationError = ValidateParameters(tool, toolCall.Parameters);
        if (validationError != null)
        {
            _logger.LogWarning("Tool {ToolName} called with invalid parameters: {Error}",
                tool.ToolName, validationError);
            return ChatToolResult.Failure(validationError);
        }

        _logger.LogInformation("Executing tool {ToolName} with parameters: {Parameters}",
            tool.ToolName,
            string.Join(", ", toolCall.Parameters.Select(kv => $"{kv.Key}={kv.Value}")));

        try
        {
            // ── Confirm gate ──────────────────────────────────────────────
            // A data-changing tool runs ONLY with the user's explicit approval. Without it the
            // tool's ExecuteAsync is never reached — the user sees a preview instead. Central on
            // purpose: a per-tool check is one forgotten `if` away from a silent overwrite.
            if (tool is IConfirmableChatTool confirmable &&
                !ChatToolConfirmation.IsConfirmed(toolCall.Parameters))
            {
                _logger.LogInformation(
                    "Tool {ToolName} requires confirmation — returning preview, nothing was written",
                    tool.ToolName);

                var preview = await confirmable.BuildPreviewAsync(toolCall.Parameters, ct);

                // A preview that failed (record not found, …) stays a plain failure — there is
                // nothing to confirm, so the model must not be invited to retry with confirm=true.
                return preview.IsSuccess
                    ? preview with
                    {
                        RequiresConfirmation = true,
                        OutputText = preview.OutputText + ChatToolConfirmation.PreviewSuffix
                    }
                    : preview;
            }

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

    // ─── Central Parameter Validation ─────────────────────────────────────

    /// <summary>
    /// Checks the supplied parameters against the tool's schema:
    /// required parameters present, values inside the allowed set, values of the declared type.
    ///
    /// Returns null when everything is fine, otherwise a single human-readable message
    /// listing every problem at once (the model gets one shot to fix all of them).
    ///
    /// Blank optional parameters are treated as "not supplied" — models like to send
    /// empty strings for parameters they have no value for.
    /// </summary>
    private static string? ValidateParameters(IChatTool tool, Dictionary<string, string> parameters)
    {
        List<string>? errors = null;

        foreach (var schema in ChatToolConfirmation.EffectiveParameters(tool))
        {
            parameters.TryGetValue(schema.Name, out var rawValue);

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                if (schema.IsRequired)
                    (errors ??= []).Add($"missing required parameter '{schema.Name}' ({schema.Description})");

                continue;
            }

            var value = rawValue.Trim();

            if (schema.AllowedValues is { Count: > 0 } &&
                !schema.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                (errors ??= []).Add(
                    $"parameter '{schema.Name}' must be one of: {string.Join(", ", schema.AllowedValues)} (got '{value}')");
                continue;
            }

            if (!MatchesType(value, schema.Type))
            {
                (errors ??= []).Add(
                    $"parameter '{schema.Name}' must be a {schema.Type.ToJsonSchemaType()} (got '{value}')");
            }
        }

        return errors == null
            ? null
            : $"Invalid parameters for tool '{tool.ToolName}': {string.Join("; ", errors)}.";
    }

    /// <summary>
    /// Checks whether a raw value can be interpreted as the declared type.
    /// Values always arrive as strings (that is the IChatTool contract), so numbers and
    /// booleans are validated by parsing, and arrays by parsing their raw JSON.
    ///
    /// InvariantCulture on purpose: the model produces JSON numbers ("1234.56"),
    /// never Czech-formatted ones.
    /// </summary>
    private static bool MatchesType(string value, ChatToolParameterType type) => type switch
    {
        ChatToolParameterType.String => true,
        ChatToolParameterType.Integer => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        ChatToolParameterType.Number => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        ChatToolParameterType.Boolean => bool.TryParse(value, out _),
        ChatToolParameterType.ObjectArray => IsJsonArray(value),
        _ => true
    };

    /// <summary>
    /// True when the text is a well-formed JSON array (the model may also send it
    /// as a quoted string containing JSON — that arrives here already unquoted).
    /// </summary>
    private static bool IsJsonArray(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
