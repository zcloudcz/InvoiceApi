using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents a chat conversation between a user and the AI assistant.
/// Stored per-tenant in the tenant schema.
/// Each conversation belongs to one user and contains an ordered list of messages.
/// </summary>
public class ChatConversation : BaseEntity
{
    /// <summary>
    /// ID of the user who owns this conversation.
    /// References User in the master schema (not an EF navigation — cross-schema).
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// Short title for the conversation — auto-generated from the first user message.
    /// Displayed in the conversation list sidebar.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of the most recent message in this conversation.
    /// Used for sorting conversations by recency.
    /// </summary>
    public DateTime LastMessageAt { get; set; }

    /// <summary>
    /// Whether this conversation has been archived (hidden from the active list).
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// All messages in this conversation, ordered by CreatedAt.
    /// </summary>
    public List<ChatMessage> Messages { get; set; } = new();
}
