using System.Runtime.CompilerServices;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Service for AI chat operations: conversation CRUD, message sending, and streaming.
///
/// Orchestrates the flow:
/// 1. User sends a message (optionally in a new or existing conversation)
/// 2. ChatContextBuilder builds the system prompt with tenant business data
/// 3. (Optional) If tool intent detected → two-pass flow with tool execution
/// 4. IAiProvider generates the AI response (sync or streaming)
/// 5. Both user and assistant messages are persisted to the tenant database
///
/// Tool flow (two-pass, when IČO+keyword or navigation keyword detected):
/// - First pass: non-streaming AI call with tool instructions → AI responds with JSON tool call
/// - Tool execution: ARES lookup, client creation, navigation, etc.
/// - Second pass: streaming AI call with tool results → natural-language response to user
/// - If the tool returns a UiAction, it's stored in _pendingUiAction for the controller to send
///
/// All operations are scoped to the current tenant (TenantDbContext).
/// </summary>
public class ChatService : IChatService
{
    private readonly TenantDbContext _context;
    private readonly IAiProviderFactory _providerFactory;
    private readonly IChatContextBuilder _contextBuilder;
    private readonly IChatToolExecutor _toolExecutor;
    private readonly ILogger<ChatService> _logger;

    /// <summary>
    /// Stores the UI action from the last tool execution in this request scope.
    /// Read by ChatController after streaming completes to send a post-stream SSE event.
    /// Safe because ChatService is scoped (one instance per HTTP request).
    /// </summary>
    private ChatUiAction? _pendingUiAction;

    public ChatService(
        TenantDbContext context,
        IAiProviderFactory providerFactory,
        IChatContextBuilder contextBuilder,
        IChatToolExecutor toolExecutor,
        ILogger<ChatService> logger)
    {
        _context = context;
        _providerFactory = providerFactory;
        _contextBuilder = contextBuilder;
        _toolExecutor = toolExecutor;
        _logger = logger;
    }

    /// <inheritdoc />
    public ChatUiAction? GetPendingUiAction() => _pendingUiAction;

    /// <summary>
    /// Lists all conversations for a user, ordered by most recent first.
    /// </summary>
    public async Task<List<ChatConversationListDto>> GetConversationsAsync(
        long userId, CancellationToken ct = default)
    {
        return await _context.ChatConversation
            .AsNoTracking()
            .Where(c => c.UserId == userId && !c.IsArchived)
            .OrderByDescending(c => c.LastMessageAt)
            .Select(c => new ChatConversationListDto
            {
                Id = c.Id,
                Title = c.Title,
                LastMessageAt = c.LastMessageAt,
                MessageCount = c.Messages.Count(m => m.Role != EChatRole.System)
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Loads a single conversation with all its messages.
    /// Throws InvalidOperationException if the conversation doesn't belong to the user.
    /// </summary>
    public async Task<ChatConversationDto> GetConversationAsync(
        long conversationId, long userId, CancellationToken ct = default)
    {
        var conversation = await _context.ChatConversation
            .AsNoTracking()
            .Include(c => c.Messages.Where(m => m.Role != EChatRole.System))
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found.");

        return new ChatConversationDto
        {
            Id = conversation.Id,
            Title = conversation.Title,
            LastMessageAt = conversation.LastMessageAt,
            IsArchived = conversation.IsArchived,
            MessageCount = conversation.Messages.Count,
            Messages = conversation.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(MapMessageToDto)
                .ToList()
        };
    }

    /// <summary>
    /// Sends a message and returns the complete AI response (non-streaming).
    /// Creates a new conversation if ConversationId is null.
    /// </summary>
    public async Task<SendMessageResponse> SendMessageAsync(
        long userId, SendMessageRequest request, CancellationToken ct = default)
    {
        // Resolve or create the conversation.
        var conversation = await GetOrCreateConversationAsync(userId, request.ConversationId, request.Message, ct);

        // Save the user's message.
        var userMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = EChatRole.User,
            Content = request.Message
        };
        _context.ChatMessage.Add(userMessage);
        await _context.SaveChangesAsync(ct);

        // Build context and get AI response.
        var provider = ResolveProvider(request.Provider);
        var systemPrompt = await _contextBuilder.BuildSystemPromptAsync(ct);
        var history = await GetConversationHistoryAsync(conversation.Id, ct);

        string responseText;

        // ─── Tool detection: two-pass flow when IČO + keyword found ──────
        // If the user's message mentions an IČO and a relevant keyword (e.g., "najdi firmu"),
        // we do a non-streaming first AI call to get a structured tool call,
        // execute the tool, then make a second AI call with the tool result.
        if (_toolExecutor.DetectToolIntent(request.Message))
        {
            _logger.LogInformation(
                "Tool intent detected in conversation {ConversationId}, starting two-pass flow",
                conversation.Id);

            // First pass: AI call with tool instructions appended to the system prompt.
            var toolSystemPrompt = systemPrompt + _toolExecutor.BuildToolInstructions();
            var firstPassResponse = await provider.GetCompletionAsync(history, toolSystemPrompt, ct);

            // Try to parse a structured tool call from the AI response.
            var toolCall = _toolExecutor.ParseToolCall(firstPassResponse);

            if (toolCall != null)
            {
                _logger.LogInformation("Tool call parsed: {Action}, executing...", toolCall.Action);

                // Execute the tool (ARES lookup, client creation, navigation, etc.).
                var toolResult = await _toolExecutor.ExecuteToolAsync(toolCall, ct);

                // Capture any UI action for the controller to send after the response.
                _pendingUiAction = toolResult.UiAction;

                // Second pass: AI call with tool result injected into the system prompt.
                // The AI uses this to generate a natural-language response for the user.
                var resultPrompt = systemPrompt +
                    $"\n\nTool '{toolCall.Action}' was executed. Result:\n{toolResult.OutputText}\n\n" +
                    "Now respond to the user in a friendly, concise way based on the tool result above. " +
                    "Include the key information from the result. Respond in the same language as the user.";

                responseText = await provider.GetCompletionAsync(history, resultPrompt, ct);
            }
            else
            {
                // AI decided no tool was needed — use its text response directly.
                _logger.LogDebug("Tool intent detected but AI did not produce a tool call, using direct response");
                responseText = firstPassResponse;
            }
        }
        else
        {
            // ─── Regular path (no tool intent) ───────────────────────────
            _logger.LogInformation(
                "Sending message to {Provider} in conversation {ConversationId}",
                provider.ProviderName, conversation.Id);

            responseText = await provider.GetCompletionAsync(history, systemPrompt, ct);
        }

        // Save the assistant's response.
        var assistantMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = EChatRole.Assistant,
            Content = responseText,
            ProviderUsed = provider.ProviderName
        };
        _context.ChatMessage.Add(assistantMessage);

        // Update conversation metadata.
        conversation.LastMessageAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return new SendMessageResponse
        {
            ConversationId = conversation.Id,
            AssistantMessage = MapMessageToDto(assistantMessage)
        };
    }

    /// <summary>
    /// Sends a message and streams the AI response token-by-token.
    /// The full response is accumulated and saved to the database after streaming completes.
    /// </summary>
    public async IAsyncEnumerable<string> StreamMessageAsync(
        long userId, SendMessageRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Resolve or create the conversation.
        var conversation = await GetOrCreateConversationAsync(userId, request.ConversationId, request.Message, ct);

        // Save the user's message.
        var userMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = EChatRole.User,
            Content = request.Message
        };
        _context.ChatMessage.Add(userMessage);
        await _context.SaveChangesAsync(ct);

        // Build context and start streaming.
        var provider = ResolveProvider(request.Provider);
        var systemPrompt = await _contextBuilder.BuildSystemPromptAsync(ct);
        var history = await GetConversationHistoryAsync(conversation.Id, ct);

        // ─── Tool detection: two-pass flow when IČO + keyword found ──────
        // Check if the user's message looks like it needs a tool (IČO + keyword).
        // If so, do a non-streaming "first pass" to get the tool call JSON,
        // execute the tool, then stream the "second pass" with tool results.
        if (_toolExecutor.DetectToolIntent(request.Message))
        {
            _logger.LogInformation(
                "Tool intent detected in conversation {ConversationId}, starting two-pass streaming flow",
                conversation.Id);

            // First pass: non-streaming call with tool instructions in the system prompt.
            var toolSystemPrompt = systemPrompt + _toolExecutor.BuildToolInstructions();
            var firstPassResponse = await provider.GetCompletionAsync(history, toolSystemPrompt, ct);

            // Try to parse a tool call from the AI's response.
            var toolCall = _toolExecutor.ParseToolCall(firstPassResponse);

            if (toolCall != null)
            {
                _logger.LogInformation("Tool call parsed: {Action}, executing...", toolCall.Action);

                // Execute the tool (ARES lookup, client creation, navigation, etc.).
                var toolResult = await _toolExecutor.ExecuteToolAsync(toolCall, ct);

                // Capture any UI action for the controller to send after streaming completes.
                _pendingUiAction = toolResult.UiAction;

                // Second pass: stream the final response with tool result in the system prompt.
                var resultPrompt = systemPrompt +
                    $"\n\nTool '{toolCall.Action}' was executed. Result:\n{toolResult.OutputText}\n\n" +
                    "Now respond to the user in a friendly, concise way based on the tool result above. " +
                    "Include the key information from the result. Respond in the same language as the user.";

                var toolFullResponse = new StringBuilder();

                await foreach (var chunk in provider.StreamCompletionAsync(history, resultPrompt, ct))
                {
                    toolFullResponse.Append(chunk);
                    yield return chunk;
                }

                // Save the assistant's response after streaming completes.
                var toolAssistantMessage = new ChatMessage
                {
                    ConversationId = conversation.Id,
                    Role = EChatRole.Assistant,
                    Content = toolFullResponse.ToString(),
                    ProviderUsed = provider.ProviderName
                };
                _context.ChatMessage.Add(toolAssistantMessage);
                conversation.LastMessageAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                yield break; // Two-pass complete, skip regular streaming below.
            }

            // AI decided no tool was needed despite intent detection.
            // Fall through to regular streaming with the first-pass response.
            _logger.LogDebug(
                "Tool intent detected but AI did not produce a tool call, falling through to regular streaming");
        }

        // ─── Regular streaming path (no tool intent or AI declined tool) ──
        _logger.LogInformation(
            "Streaming message from {Provider} in conversation {ConversationId}",
            provider.ProviderName, conversation.Id);

        // Accumulate the full response while streaming chunks to the caller.
        var fullResponse = new StringBuilder();

        await foreach (var chunk in provider.StreamCompletionAsync(history, systemPrompt, ct))
        {
            fullResponse.Append(chunk);
            yield return chunk;
        }

        // After streaming completes, save the full assistant response.
        var assistantMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = EChatRole.Assistant,
            Content = fullResponse.ToString(),
            ProviderUsed = provider.ProviderName
        };
        _context.ChatMessage.Add(assistantMessage);

        conversation.LastMessageAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a conversation and all its messages (cascade delete configured in EF).
    /// </summary>
    public async Task DeleteConversationAsync(
        long conversationId, long userId, CancellationToken ct = default)
    {
        var conversation = await _context.ChatConversation
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
            ?? throw new InvalidOperationException($"Conversation {conversationId} not found.");

        _context.ChatConversation.Remove(conversation);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted conversation {ConversationId} for user {UserId}", conversationId, userId);
    }

    /// <summary>
    /// Returns the list of available AI provider names.
    /// </summary>
    public IReadOnlyList<string> GetAvailableProviders()
    {
        return _providerFactory.AvailableProviders;
    }

    // ─── Private helpers ────────────────────────────────────────────────

    /// <summary>
    /// Gets an existing conversation or creates a new one.
    /// Auto-generates the title from the first user message (first 50 chars).
    /// </summary>
    private async Task<ChatConversation> GetOrCreateConversationAsync(
        long userId, long? conversationId, string firstMessage, CancellationToken ct)
    {
        if (conversationId.HasValue)
        {
            var existing = await _context.ChatConversation
                .FirstOrDefaultAsync(c => c.Id == conversationId.Value && c.UserId == userId, ct)
                ?? throw new InvalidOperationException($"Conversation {conversationId} not found.");
            return existing;
        }

        // Create a new conversation with an auto-generated title.
        var title = firstMessage.Length > 50
            ? firstMessage[..50] + "..."
            : firstMessage;

        var conversation = new ChatConversation
        {
            UserId = userId,
            Title = title,
            LastMessageAt = DateTime.UtcNow,
            IsArchived = false
        };

        _context.ChatConversation.Add(conversation);
        await _context.SaveChangesAsync(ct);

        return conversation;
    }

    /// <summary>
    /// Loads the full conversation history as DTOs for sending to the AI provider.
    /// Only includes User and Assistant messages (System messages are handled via systemPrompt).
    /// </summary>
    private async Task<List<ChatMessageDto>> GetConversationHistoryAsync(
        long conversationId, CancellationToken ct)
    {
        return await _context.ChatMessage
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role != EChatRole.System)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessageDto
            {
                Id = m.Id,
                Role = m.Role.ToString(),
                Content = m.Content,
                ProviderUsed = m.ProviderUsed,
                CreatedAt = m.CreatedAt
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Resolves the AI provider by name, or returns the default if no name specified.
    /// </summary>
    private IAiProvider ResolveProvider(string? providerName)
    {
        return string.IsNullOrEmpty(providerName)
            ? _providerFactory.GetDefaultProvider()
            : _providerFactory.GetProvider(providerName);
    }

    /// <summary>
    /// Maps a ChatMessage entity to a ChatMessageDto.
    /// </summary>
    private static ChatMessageDto MapMessageToDto(ChatMessage message)
    {
        return new ChatMessageDto
        {
            Id = message.Id,
            Role = message.Role.ToString(),
            Content = message.Content,
            ProviderUsed = message.ProviderUsed,
            CreatedAt = message.CreatedAt
        };
    }
}
