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

    /// <summary>
    /// Optional text content extracted from an uploaded PDF file.
    /// When set, this content is prepended to the AI prompt as context,
    /// allowing the AI to answer questions about the PDF document.
    /// Populated by the Blazor UI after calling the extract-pdf endpoint.
    /// </summary>
    public string? AttachedFileContent { get; set; }

    /// <summary>
    /// Original file name of the attached PDF (e.g., "invoice_2024.pdf").
    /// Used for display in the UI and in the AI context block.
    /// </summary>
    public string? AttachedFileName { get; set; }

    /// <summary>
    /// Optional base64-encoded image data for multimodal AI processing.
    /// Raw base64 string (no "data:image/..." prefix), max ~10 MB image.
    /// When set, the image is passed to the AI provider (e.g., Gemma3) for visual analysis.
    /// Unlike PDFs (which need server-side text extraction), images are read as bytes
    /// directly in the browser and sent as base64 — no separate extraction endpoint needed.
    /// </summary>
    [MaxLength(15_000_000)]
    public string? AttachedImageBase64 { get; set; }
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
