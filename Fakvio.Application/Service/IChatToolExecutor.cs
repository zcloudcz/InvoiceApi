namespace Fakvio.Application.Service;

/// <summary>
/// Represents a parsed tool call extracted from an AI response.
/// The AI responds with JSON when it wants to use a tool (e.g., ARES lookup),
/// and this record holds the parsed action name and parameters.
///
/// Example AI response that gets parsed into this:
///   {"action": "ares_lookup", "parameters": {"registration_number": "12345678"}}
/// </summary>
public record ParsedToolCall
{
    /// <summary>
    /// The tool name requested by the AI (e.g., "ares_lookup", "create_client").
    /// Must match an IChatTool.ToolName registered in DI.
    /// </summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// Parameters for the tool, extracted from the AI's JSON.
    /// Example: {"registration_number": "12345678"}.
    /// </summary>
    public Dictionary<string, string> Parameters { get; init; } = new();
}

/// <summary>
/// Orchestrates chat tool detection, AI interaction, and tool execution.
///
/// The two-pass flow (used by ChatService):
/// 1. DetectToolIntent — fast regex check on user message (IČO pattern + keywords)
/// 2. BuildToolInstructions — appended to system prompt for the first AI call
/// 3. ParseToolCall — extracts structured JSON tool call from AI response
/// 4. ExecuteToolAsync — runs the matching IChatTool
/// 5. ChatService then makes a second AI call with the tool result in context
///
/// This is provider-agnostic — works with any IAiProvider (Claude, OpenAI, Gemini, Ollama)
/// because the AI interaction uses the standard text-in/text-out interface.
///
/// Junior note: This is the "Mediator" pattern — it sits between ChatService and the tools,
/// handling detection, parsing, and dispatch so ChatService stays clean.
/// </summary>
public interface IChatToolExecutor
{
    /// <summary>
    /// Checks if the user's message likely requires a tool call.
    /// Uses fast regex matching — no AI call, no network request.
    /// Returns true if patterns like "IČO 12345678" or "založ klienta" are found.
    ///
    /// This is the performance gate: regular messages skip tool processing entirely,
    /// so there's zero latency overhead for normal chat.
    /// </summary>
    /// <param name="userMessage">The user's chat message text.</param>
    /// <returns>True if the message likely needs tool execution.</returns>
    bool DetectToolIntent(string userMessage);

    /// <summary>
    /// Builds tool instruction text to append to the system prompt.
    /// Describes available tools and the JSON format the AI should use.
    /// Only appended when DetectToolIntent returns true (not for every message).
    /// </summary>
    /// <returns>Tool instruction text to append to the system prompt.</returns>
    string BuildToolInstructions();

    /// <summary>
    /// Parses the AI's response to extract a tool call.
    /// Expected format: {"action": "tool_name", "parameters": {"key": "value"}}
    /// Returns null if the response is not a tool call (just regular text).
    /// Handles markdown code blocks and preamble text.
    /// </summary>
    /// <param name="aiResponse">The full AI response text.</param>
    /// <returns>Parsed tool call, or null if not a valid tool call.</returns>
    ParsedToolCall? ParseToolCall(string aiResponse);

    /// <summary>
    /// Executes a parsed tool call by dispatching to the matching IChatTool.
    /// Returns the tool result (success with output, or failure with error message).
    /// </summary>
    /// <param name="toolCall">The parsed tool call from the AI.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tool execution result.</returns>
    Task<ChatToolResult> ExecuteToolAsync(ParsedToolCall toolCall, CancellationToken ct = default);

    /// <summary>
    /// Returns the list of registered tool names (for logging and debugging).
    /// </summary>
    IReadOnlyList<string> AvailableTools { get; }

    /// <summary>
    /// Builds native tool definitions from all registered IChatTool instances.
    /// Used by providers that support native tool calling (e.g., Ollama).
    /// Each IChatTool is converted to a NativeToolDefinition with JSON Schema parameters.
    ///
    /// Junior note: Native tool calling is more reliable than text-based instructions
    /// because the AI model was fine-tuned to produce structured tool calls,
    /// not free-text JSON that needs parsing.
    /// </summary>
    List<NativeToolDefinition> GetToolDefinitions();
}
