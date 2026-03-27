namespace Fakvio.Contracts.Dto.Chat;

/// <summary>
/// Represents a UI action that the AI chat assistant wants the client to perform.
/// Sent as a structured SSE event after the text stream completes.
///
/// This is a "command" pattern — the server tells the Blazor client what to do,
/// and the client decides how to execute it (e.g., NavigationManager.NavigateTo).
///
/// Currently supported types:
/// - "navigate": Navigate to a page URL
/// - "download": Download a file (PDF, etc.) via JS interop
///
/// Future types (extensible): "openDialog", "showNotification", "scrollTo", etc.
///
/// Junior note: This DTO lives in Contracts (not Domain) because it's a communication
/// contract between the API and Blazor UI — it doesn't represent a database entity.
/// </summary>
public class ChatUiAction
{
    /// <summary>
    /// The type of UI action to perform.
    /// Currently: "navigate", "download". Future: "openDialog", "showNotification", etc.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The target URL for "navigate" actions (e.g., "/invoices/create?clientId=5").
    /// Null for non-navigation actions.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Optional parameters for extensibility.
    /// Example: for future "openDialog" action, could contain {"dialogId": "emailInvoice"}.
    /// </summary>
    public Dictionary<string, string>? Parameters { get; set; }

    // ── Factory methods ────────────────────────────────────────────────

    /// <summary>
    /// Creates a navigation action to the specified URL.
    /// Example: ChatUiAction.Navigate("/invoices/create?clientId=5")
    /// </summary>
    public static ChatUiAction Navigate(string url)
        => new() { Type = "navigate", Url = url };

    /// <summary>
    /// Creates a file download action. The Blazor client will trigger a browser download
    /// using the provided URL (calls the API endpoint and downloads via JS interop).
    /// </summary>
    /// <param name="url">API endpoint that returns the file (e.g., "/api/invoice/5/pdf")</param>
    /// <param name="fileName">Suggested file name for the download (e.g., "Faktura_FV2024001.pdf")</param>
    /// <param name="mimeType">MIME type (e.g., "application/pdf")</param>
    public static ChatUiAction Download(string url, string fileName, string mimeType = "application/pdf")
        => new()
        {
            Type = "download",
            Url = url,
            Parameters = new Dictionary<string, string>
            {
                ["fileName"] = fileName,
                ["mimeType"] = mimeType
            }
        };
}
