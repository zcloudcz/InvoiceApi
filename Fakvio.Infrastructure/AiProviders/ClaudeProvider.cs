using System.Runtime.CompilerServices;
using Anthropic;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// AI provider implementation for Anthropic Claude.
/// Uses the tryAGI/Anthropic NuGet SDK (auto-generated from the official OpenAPI spec).
///
/// Supports both synchronous and streaming completions via the Messages API.
/// System prompts are passed as a separate parameter (Claude API separates system from messages).
///
/// API surface (v3.3.0):
///   - Non-streaming: client.Messages.MessagesPostAsync(model, messages, maxTokens, system: ...)
///   - Streaming: client.CreateMessageAsStreamAsync(CreateMessageParams)
///   - Messages: InputMessage with InputMessageRole.User / InputMessageRole.Assistant
///   - Text extraction: response.AsSimpleText() / evt.ContentBlockDelta?.Delta.TextDelta?.Text
/// </summary>
public class ClaudeProvider : IAiProvider, IDisposable
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly ILogger<ClaudeProvider> _logger;

    public string ProviderName => "Claude";

    public ClaudeProvider(IOptions<AiSettings> settings, ILogger<ClaudeProvider> logger)
    {
        _logger = logger;
        var config = settings.Value.Claude;
        _client = new AnthropicClient(config.ApiKey);
        _model = string.IsNullOrEmpty(config.Model) ? "claude-3-5-sonnet-20241022" : config.Model;
    }

    /// <summary>
    /// Sends messages to Claude and returns the complete response.
    /// Uses the Messages API via client.Messages.MessagesPostAsync().
    /// </summary>
    public async Task<string> GetCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var anthropicMessages = BuildMessages(messages);

        _logger.LogDebug("Sending {Count} messages to Claude model {Model}", messages.Count, _model);

        try
        {
            // Call the Messages API with individual parameters.
            // The 'system' parameter accepts AnyOf<string, IList<RequestTextBlock>>? — string works via implicit conversion.
            var response = await _client.Messages.MessagesPostAsync(
                model: _model,
                messages: anthropicMessages,
                maxTokens: 4096,
                system: systemPrompt,
                cancellationToken: ct);

            // AsSimpleText() extracts the concatenated text from all content blocks.
            return response.AsSimpleText();
        }
        catch (Exception ex)
        {
            // Log the full error response from Claude API for debugging.
            _logger.LogError(ex, "Claude API error: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Streams the response from Claude token-by-token.
    /// Uses client.CreateMessageAsStreamAsync() which returns IAsyncEnumerable of MessageStreamEvent.
    /// Each event is a discriminated union — we look for ContentBlockDelta events containing text.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var anthropicMessages = BuildMessages(messages);

        _logger.LogDebug("Starting streaming from Claude model {Model}", _model);

        // CreateMessageAsStreamAsync takes a CreateMessageParams object and returns IAsyncEnumerable<MessageStreamEvent>.
        var events = _client.CreateMessageAsStreamAsync(
            new CreateMessageParams
            {
                Model = _model,
                Messages = anthropicMessages,
                MaxTokens = 4096,
                System = systemPrompt
            },
            cancellationToken: ct);

        await foreach (var evt in events.WithCancellation(ct))
        {
            // Each streaming event may contain a ContentBlockDelta with a TextDelta.
            // The path is: evt.ContentBlockDelta?.Delta.TextDelta?.Text
            var text = evt.ContentBlockDelta?.Delta.TextDelta?.Text;
            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// Builds the Anthropic InputMessage list from our DTOs.
    /// Maps "User" → InputMessageRole.User, "Assistant" → InputMessageRole.Assistant.
    /// System messages are excluded (they go to the system parameter).
    ///
    /// The SDK provides string extension methods:
    ///   - Implicit string → InputMessage conversion (defaults to User role)
    ///   - string.AsAssistantMessage() → InputMessage with Assistant role
    /// </summary>
    private static List<InputMessage> BuildMessages(List<ChatMessageDto> messages)
    {
        var result = new List<InputMessage>();

        foreach (var msg in messages)
        {
            // System messages are handled separately via the system parameter.
            if (msg.Role == "System") continue;

            if (msg.Role == "Assistant")
            {
                // AsAssistantMessage() is a string extension that creates an InputMessage with Role = Assistant.
                result.Add(msg.Content.AsAssistantMessage());
            }
            else
            {
                // Implicit string → InputMessage conversion creates a User message.
                result.Add(msg.Content);
            }
        }

        return result;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
