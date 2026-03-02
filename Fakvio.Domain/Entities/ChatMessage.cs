using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// A single message within a chat conversation.
/// Can be from the user, the AI assistant, or a system instruction.
/// </summary>
public class ChatMessage : BaseEntity
{
    /// <summary>
    /// FK to the parent conversation.
    /// </summary>
    public long ConversationId { get; set; }

    /// <summary>
    /// Navigation property to the parent conversation.
    /// </summary>
    public ChatConversation Conversation { get; set; } = null!;

    /// <summary>
    /// Who sent this message: System, User, or Assistant.
    /// </summary>
    public EChatRole Role { get; set; }

    /// <summary>
    /// The text content of the message.
    /// Can contain markdown for assistant responses.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Which AI provider generated this response (e.g., "Claude", "OpenAI").
    /// Null for user and system messages.
    /// </summary>
    public string? ProviderUsed { get; set; }

    /// <summary>
    /// Approximate token count for this message (if reported by the AI provider).
    /// Null for user messages or when the provider doesn't report usage.
    /// </summary>
    public int? TokensUsed { get; set; }
}
