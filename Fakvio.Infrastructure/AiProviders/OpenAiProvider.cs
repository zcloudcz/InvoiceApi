using System.ClientModel;
using System.Runtime.CompilerServices;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// AI provider implementation for OpenAI (GPT-4o, GPT-4, etc.).
/// Uses the official OpenAI .NET SDK (NuGet: OpenAI).
///
/// Supports both synchronous and streaming completions via the Chat Completions API.
/// </summary>
public class OpenAiProvider : IAiProvider
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<OpenAiProvider> _logger;

    public string ProviderName => "OpenAI";

    public OpenAiProvider(IOptions<AiSettings> settings, ILogger<OpenAiProvider> logger)
    {
        _logger = logger;
        var config = settings.Value.OpenAI;
        var model = string.IsNullOrEmpty(config.Model) ? "gpt-4o" : config.Model;

        var client = new OpenAIClient(config.ApiKey);
        _chatClient = client.GetChatClient(model);
    }

    /// <summary>
    /// Sends messages to OpenAI and returns the complete response.
    /// </summary>
    public async Task<string> GetCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var chatMessages = BuildMessages(messages, systemPrompt);

        _logger.LogDebug("Sending {Count} messages to OpenAI", messages.Count);

        var completion = await _chatClient.CompleteChatAsync(chatMessages, cancellationToken: ct);

        return completion.Value.Content?.FirstOrDefault()?.Text ?? string.Empty;
    }

    /// <summary>
    /// Streams the response from OpenAI token-by-token.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var chatMessages = BuildMessages(messages, systemPrompt);

        _logger.LogDebug("Starting streaming from OpenAI");

        AsyncCollectionResult<StreamingChatCompletionUpdate> updates =
            _chatClient.CompleteChatStreamingAsync(chatMessages, cancellationToken: ct);

        await foreach (var update in updates.WithCancellation(ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    yield return part.Text;
                }
            }
        }
    }

    /// <summary>
    /// Builds OpenAI ChatMessage list from our DTOs.
    /// Maps roles: "System" → SystemChatMessage, "User" → UserChatMessage, "Assistant" → AssistantChatMessage.
    /// </summary>
    private static List<ChatMessage> BuildMessages(List<ChatMessageDto> messages, string? systemPrompt)
    {
        var chatMessages = new List<ChatMessage>();

        // System prompt goes first as a SystemChatMessage.
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            chatMessages.Add(ChatMessage.CreateSystemMessage(systemPrompt));
        }

        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case "System":
                    chatMessages.Add(ChatMessage.CreateSystemMessage(msg.Content));
                    break;
                case "Assistant":
                    chatMessages.Add(ChatMessage.CreateAssistantMessage(msg.Content));
                    break;
                default: // "User" or unknown → user message
                    chatMessages.Add(ChatMessage.CreateUserMessage(msg.Content));
                    break;
            }
        }

        return chatMessages;
    }
}
