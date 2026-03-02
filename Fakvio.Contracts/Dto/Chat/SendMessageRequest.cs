using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Chat;

/// <summary>
/// Request DTO for sending a chat message.
/// If ConversationId is null, a new conversation is created automatically.
/// </summary>
public class SendMessageRequest
{
    /// <summary>
    /// Existing conversation ID. Null to start a new conversation.
    /// </summary>
    public long? ConversationId { get; set; }

    /// <summary>
    /// The user's message text.
    /// </summary>
    [Required]
    [MaxLength(10000)]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional AI provider override (e.g., "Claude", "OpenAI", "Gemini", "Ollama").
    /// If null, the system default provider is used.
    /// </summary>
    public string? Provider { get; set; }
}

/// <summary>
/// Response metadata returned after sending a message (non-streaming).
/// The actual AI response content is in the conversation messages.
/// </summary>
public class SendMessageResponse
{
    /// <summary>
    /// The conversation ID (useful when a new conversation was created).
    /// </summary>
    public long ConversationId { get; set; }

    /// <summary>
    /// The AI's response message.
    /// </summary>
    public ChatMessageDto AssistantMessage { get; set; } = null!;
}
