namespace Fakvio.Contracts.Dto.Chat;

/// <summary>
/// DTO for a single chat message.
/// Role is represented as a string ("User", "Assistant", "System") for simplicity
/// in the UI layer, which doesn't reference the Domain enum directly.
/// </summary>
public class ChatMessageDto
{
    public long Id { get; set; }

    /// <summary>
    /// Message role as string: "User", "Assistant", or "System".
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// The text content of the message (may contain markdown).
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Which AI provider generated this response (e.g., "Claude", "OpenAI").
    /// Null for user messages.
    /// </summary>
    public string? ProviderUsed { get; set; }

    /// <summary>
    /// When this message was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Transient property for passing image data from ChatService to IAiProvider
    /// within a single request. Contains base64-encoded image strings.
    ///
    /// [JsonIgnore] prevents accidental serialization of multi-MB base64 data
    /// to the API response or database. This field is only used in-memory
    /// during the ChatService → IAiProvider call chain.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string>? Images { get; set; }
}
