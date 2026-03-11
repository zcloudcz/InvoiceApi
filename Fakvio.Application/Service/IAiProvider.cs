using Fakvio.Contracts.Dto.Chat;

namespace Fakvio.Application.Service;

/// <summary>
/// Interface for AI completion providers (Claude, OpenAI, Gemini, Ollama, etc.).
/// Each provider wraps a specific AI API and exposes a unified interface
/// for both synchronous (full response) and streaming completions.
///
/// Implementations live in Fakvio.Infrastructure.AiProviders.
/// </summary>
public interface IAiProvider
{
    /// <summary>
    /// Human-readable name of the provider (e.g., "Claude", "OpenAI", "Gemini", "Ollama").
    /// Used in the UI dropdown and stored in ChatMessage.ProviderUsed.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Whether this provider supports native tool/function calling via its API.
    /// When true, ChatService uses GetCompletionWithToolsAsync instead of text-based instructions.
    ///
    /// Native tool calling is more reliable because:
    /// - Models are fine-tuned to produce structured tool calls (not free-text JSON)
    /// - The API enforces the tool schema (parameter names, types)
    /// - No need for regex-based intent detection — the model decides when to use tools
    ///
    /// Default: false (providers opt-in by overriding).
    /// </summary>
    bool SupportsNativeTools => false;

    /// <summary>
    /// Sends the full conversation history to the AI and returns the complete response.
    /// Use this for simple request-response flows without streaming.
    /// </summary>
    /// <param name="messages">Conversation history (role + content pairs).</param>
    /// <param name="systemPrompt">Optional system instruction prepended to the conversation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The AI's complete text response.</returns>
    Task<string> GetCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends the conversation to the AI and streams the response token-by-token.
    /// Use this for real-time UI updates (SSE endpoint).
    /// Each yielded string is a small text chunk (typically 1-5 tokens).
    /// </summary>
    /// <param name="messages">Conversation history (role + content pairs).</param>
    /// <param name="systemPrompt">Optional system instruction prepended to the conversation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async stream of text chunks.</returns>
    IAsyncEnumerable<string> StreamCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends messages with native tool definitions and returns a structured response
    /// that may include tool calls. Only available when SupportsNativeTools is true.
    ///
    /// The model decides whether to call a tool or respond with text. If it chooses a tool,
    /// the response contains a list of tool calls (action + parameters) instead of text content.
    ///
    /// Default implementation returns null (no tool call detected).
    /// </summary>
    /// <param name="messages">Conversation history.</param>
    /// <param name="systemPrompt">Optional system instruction.</param>
    /// <param name="tools">Tool definitions (name, description, parameter schema).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tool call result, or null if the model chose to respond with text.</returns>
    Task<NativeToolCallResult?> GetCompletionWithToolsAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt,
        List<NativeToolDefinition> tools,
        CancellationToken ct = default)
        => Task.FromResult<NativeToolCallResult?>(null);
}

// ─── Native Tool Calling Models ──────────────────────────────────────────────

/// <summary>
/// Defines a tool that can be passed to an AI provider's native tool calling API.
/// Maps to the OpenAI/Ollama function calling format:
///   { "type": "function", "function": { "name": "...", "description": "...", "parameters": {...} } }
///
/// Junior note: This is provider-agnostic — each provider translates it to its own API format.
/// </summary>
public class NativeToolDefinition
{
    /// <summary>
    /// Tool name in snake_case (e.g., "ares_lookup", "create_invoice").
    /// Must match the IChatTool.ToolName so we can dispatch the call.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// What this tool does — shown to the model so it knows when to use it.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Parameter definitions in JSON Schema format.
    /// Each key is a parameter name, value is { type, description, enum? }.
    /// </summary>
    public List<NativeToolParameter> Parameters { get; set; } = new();

    /// <summary>
    /// Which parameters are required (subset of parameter names).
    /// </summary>
    public List<string> Required { get; set; } = new();
}

/// <summary>
/// A single parameter in a tool definition.
/// Maps to a JSON Schema property: { "type": "string", "description": "..." }
/// </summary>
public class NativeToolParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Optional enum values — restricts the parameter to these specific values.
    /// Example: ["new_invoice", "client_list", "invoice_list"] for navigation targets.
    /// </summary>
    public List<string>? EnumValues { get; set; }
}

/// <summary>
/// Result from a native tool calling API response.
/// Contains either tool calls (model wants to use a tool) or text content (regular response).
/// </summary>
public class NativeToolCallResult
{
    /// <summary>
    /// Tool calls requested by the model. Empty if the model chose to respond with text.
    /// </summary>
    public List<NativeToolCall> ToolCalls { get; set; } = new();

    /// <summary>
    /// Text content from the model (when it doesn't use a tool).
    /// </summary>
    public string? TextContent { get; set; }

    /// <summary>
    /// True if the model chose to use at least one tool.
    /// </summary>
    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>
/// A single tool call from the model's response.
/// Contains the tool name and a dictionary of parameter values.
/// </summary>
public class NativeToolCall
{
    public string ToolName { get; set; } = string.Empty;
    public Dictionary<string, string> Arguments { get; set; } = new();
}

/// <summary>
/// Factory that resolves an IAiProvider by name.
/// Registered providers are determined by configuration — only providers
/// with valid API keys (or reachable base URLs) are available.
/// </summary>
public interface IAiProviderFactory
{
    /// <summary>
    /// Gets a provider by name (case-insensitive).
    /// Throws InvalidOperationException if the provider is not configured.
    /// </summary>
    /// <param name="providerName">Provider name (e.g., "Claude", "OpenAI").</param>
    /// <returns>The resolved IAiProvider instance.</returns>
    IAiProvider GetProvider(string providerName);

    /// <summary>
    /// Gets the default provider (first configured, or the one marked as default in config).
    /// </summary>
    IAiProvider GetDefaultProvider();

    /// <summary>
    /// Lists the names of all configured and available providers.
    /// Only providers with valid credentials appear here.
    /// </summary>
    IReadOnlyList<string> AvailableProviders { get; }
}
