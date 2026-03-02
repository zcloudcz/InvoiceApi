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
