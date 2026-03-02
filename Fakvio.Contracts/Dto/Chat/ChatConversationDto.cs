namespace Fakvio.Contracts.Dto.Chat;

/// <summary>
/// Full conversation DTO including message count.
/// Used when loading a single conversation with its messages.
/// </summary>
public class ChatConversationDto
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime LastMessageAt { get; set; }
    public bool IsArchived { get; set; }
    public int MessageCount { get; set; }
    public List<ChatMessageDto> Messages { get; set; } = new();
}

/// <summary>
/// Lightweight conversation DTO for the sidebar list.
/// Does not include full message content — only metadata.
/// </summary>
public class ChatConversationListDto
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime LastMessageAt { get; set; }
    public int MessageCount { get; set; }
}
