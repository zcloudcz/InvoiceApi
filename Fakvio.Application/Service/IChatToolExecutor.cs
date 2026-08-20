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
/// Two flows exist, both ending in ExecuteToolAsync:
/// - Native tool calling (providers with a function-calling API):
///   GetToolDefinitions — JSON Schema handed to the provider, the model returns a structured call
/// - Text-based tool calling (providers without one):
///   BuildToolInstructions — appended to the system prompt, ParseToolCall — extracts the JSON call
///
/// ChatService then makes a second AI call with the tool result in context.
/// Both flows and both prompt surfaces are generated from IChatTool.Parameters,
/// so a tool is described in exactly one place.
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
    /// Builds tool instruction text to append to the system prompt.
    /// Describes available tools, their typed parameter schema and the JSON format
    /// the AI should answer with. Generated from IChatTool.Parameters.
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
    /// Validates the parameters against the tool schema and, if they pass,
    /// dispatches the call to the matching IChatTool.
    /// Returns the tool result (success with output, or failure with error message).
    /// The failure message is fed back to the model, so it can correct the call itself.
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
    /// Used by providers that support native tool calling (e.g., Ollama, Claude).
    /// Each IChatTool.Parameters entry becomes a JSON Schema property with its real type.
    ///
    /// Junior note: Native tool calling is more reliable than text-based instructions
    /// because the AI model was fine-tuned to produce structured tool calls,
    /// not free-text JSON that needs parsing.
    /// </summary>
    List<NativeToolDefinition> GetToolDefinitions();
}
