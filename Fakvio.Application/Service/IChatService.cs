using Fakvio.Contracts.Dto.Chat;

namespace Fakvio.Application.Service;

/// <summary>
/// Service interface for AI chat operations.
/// Manages conversations, sends messages to AI providers, and persists chat history.
///
/// All methods are tenant-scoped — the TenantDbContext is resolved from the user's JWT.
/// UserId parameter ensures a user can only access their own conversations.
///
/// After calling StreamMessageAsync or SendMessageAsync, check GetPendingUiAction()
/// for any UI action the tool wants the client to execute (e.g., navigate to a page).
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Lists all conversations for a user, ordered by most recent first.
    /// Returns lightweight DTOs (no message content) for the sidebar list.
    /// </summary>
    Task<List<ChatConversationListDto>> GetConversationsAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Loads a single conversation with all its messages.
    /// Throws if the conversation doesn't exist or doesn't belong to the user.
    /// </summary>
    Task<ChatConversationDto> GetConversationAsync(long conversationId, long userId, CancellationToken ct = default);

    /// <summary>
    /// Sends a message and returns the complete AI response (non-streaming).
    /// If ConversationId is null in the request, creates a new conversation.
    /// Saves both the user message and the AI response to the database.
    /// </summary>
    Task<SendMessageResponse> SendMessageAsync(long userId, SendMessageRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sends a message and streams the AI response token-by-token.
    /// Use this for the SSE endpoint to provide real-time UI updates.
    /// The full response is saved to the database after streaming completes.
    /// </summary>
    IAsyncEnumerable<string> StreamMessageAsync(long userId, SendMessageRequest request, CancellationToken ct = default);

    /// <summary>
    /// Deletes a conversation and all its messages.
    /// Throws if the conversation doesn't exist or doesn't belong to the user.
    /// </summary>
    Task DeleteConversationAsync(long conversationId, long userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the list of available AI provider names (e.g., ["Claude", "OpenAI"]).
    /// </summary>
    IReadOnlyList<string> GetAvailableProviders();

    /// <summary>
    /// Gets the UI action from the last tool execution in this request scope.
    /// Returns null if no tool was executed or the tool didn't produce a UI action.
    ///
    /// Must be called AFTER StreamMessageAsync or SendMessageAsync completes.
    /// The ChatController uses this to send a post-stream SSE action event.
    ///
    /// Junior note: This works because ChatService is registered as "scoped" in DI,
    /// meaning each HTTP request gets its own instance with its own _pendingUiAction field.
    /// </summary>
    ChatUiAction? GetPendingUiAction();
}
