using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fakvio.Contracts.Dto.Chat;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Represents a single event from the chat SSE stream.
/// Can be either a text chunk (partial AI response) or a UI action command.
///
/// Junior note: The SSE stream from the API contains two types of events:
/// 1. Text events (default): "data: {json-string}\n\n" — each is a text chunk of the AI response
/// 2. Action events (optional): "event: action\ndata: {json}\n\n" — a UI command (e.g., navigate)
///
/// ChatPanel processes text events for progressive display and action events for navigation.
/// </summary>
public class ChatStreamEvent
{
    /// <summary>
    /// Text content (non-null for text chunks, null for action events).
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// UI action to execute (non-null for action events, null for text chunks).
    /// </summary>
    public ChatUiAction? Action { get; init; }

    /// <summary>True if this event contains a text chunk.</summary>
    public bool IsText => Text != null;

    /// <summary>True if this event contains a UI action command.</summary>
    public bool IsAction => Action != null;
}

/// <summary>
/// Blazor UI service for communicating with the Chat API endpoints.
/// Inherits ApiClientBase for shared auth, logging, and impersonation.
///
/// Supports both regular (non-streaming) and SSE (streaming) message sending.
/// The streaming method reads Server-Sent Events from the API and yields
/// ChatStreamEvent objects (text chunks or UI actions).
/// </summary>
public class ChatApiService : ApiClientBase
{
    public ChatApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ChatApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Gets all conversations for the current user.
    /// Returns lightweight DTOs for the conversation list sidebar.
    /// </summary>
    public async Task<List<ChatConversationListDto>> GetConversationsAsync()
    {
        try
        {
            return await GetAsync<List<ChatConversationListDto>>("/api/chat/conversations")
                   ?? new List<ChatConversationListDto>();
        }
        catch (ApiException)
        {
            return new List<ChatConversationListDto>();
        }
    }

    /// <summary>
    /// Gets a single conversation with all its messages.
    /// </summary>
    public async Task<ChatConversationDto?> GetConversationAsync(long conversationId)
    {
        return await GetAsync<ChatConversationDto>($"/api/chat/conversations/{conversationId}");
    }

    /// <summary>
    /// Sends a message and returns the complete AI response (non-streaming).
    /// </summary>
    public async Task<SendMessageResponse?> SendMessageAsync(SendMessageRequest request)
    {
        return await PostAsync<SendMessageRequest, SendMessageResponse>("/api/chat/send", request);
    }

    /// <summary>
    /// Sends a message and streams the AI response via SSE.
    /// Yields ChatStreamEvent objects — either text chunks or UI action commands.
    ///
    /// SSE format from the API:
    ///   data: "text chunk"\n\n           → ChatStreamEvent { Text = "text chunk" }
    ///   data: "[DONE]"\n\n               → signals end of text stream
    ///   event: action\ndata: {...}\n\n   → ChatStreamEvent { Action = ChatUiAction }
    ///
    /// Usage in a Razor component:
    ///   ChatUiAction? pendingAction = null;
    ///   await foreach (var evt in ChatApi.StreamMessageAsync(request))
    ///   {
    ///       if (evt.IsText) { _streamingContent += evt.Text; StateHasChanged(); }
    ///       else if (evt.IsAction) { pendingAction = evt.Action; }
    ///   }
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamMessageAsync(SendMessageRequest request)
    {
        await AddAuthorizationHeaderAsync();

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Send POST request with streaming enabled (ResponseHeadersRead).
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = content
        };

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            // Read the error body so we can show a meaningful message to the user.
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Chat stream failed with status {Status}: {Error}",
                response.StatusCode, errorBody);

            // Try to extract "message" from JSON error response (e.g. from TenantContextMiddleware).
            string errorMessage = $"Chat request failed ({(int)response.StatusCode}).";
            try
            {
                var errorDoc = JsonDocument.Parse(errorBody);
                if (errorDoc.RootElement.TryGetProperty("message", out var msgProp))
                {
                    errorMessage = msgProp.GetString() ?? errorMessage;
                }
            }
            catch (JsonException)
            {
                // Not JSON — use the raw error body if it's short enough.
                if (!string.IsNullOrWhiteSpace(errorBody) && errorBody.Length < 200)
                {
                    errorMessage = errorBody;
                }
            }

            throw new ApiException(response.StatusCode, errorMessage, "/api/chat/stream");
        }

        // Read the SSE stream line by line.
        // IMPORTANT: Do NOT use reader.EndOfStream — it performs a synchronous read
        // which throws "Synchronous reads are not supported" in Blazor WebAssembly.
        // Instead, use ReadLineAsync() and check for null (= end of stream).
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // Track the current SSE event type. Standard SSE format:
        //   "event: action\n"  → sets currentEventType = "action"
        //   "data: {...}\n"    → uses currentEventType to decide how to parse
        //   "\n"               → resets event (end of SSE event block)
        // If no "event:" line, the event type is null (= default text event).
        string? currentEventType = null;
        var doneReceived = false;

        while (true)
        {
            var line = await reader.ReadLineAsync();

            // ReadLineAsync returns null at end of stream.
            if (line == null)
                break;

            // Empty line marks the end of an SSE event block — reset event type.
            if (string.IsNullOrEmpty(line))
            {
                currentEventType = null;
                continue;
            }

            // Check for SSE "event:" field — sets the type for the next "data:" line.
            if (line.StartsWith("event: "))
            {
                currentEventType = line["event: ".Length..].Trim();
                continue;
            }

            // Only process "data:" lines.
            if (!line.StartsWith("data: "))
                continue;

            var data = line["data: ".Length..];

            // Check for end-of-text-stream signal (text stream done, but action may follow).
            if (data == "\"[DONE]\"")
            {
                doneReceived = true;
                continue; // Don't break — an action event may follow after [DONE].
            }

            // Handle action events (sent after [DONE]).
            if (currentEventType == "action")
            {
                ChatUiAction? action = null;
                try
                {
                    action = JsonSerializer.Deserialize<ChatUiAction>(data);
                }
                catch (JsonException)
                {
                    _logger.LogDebug("Skipping malformed action SSE event: {Data}", data);
                }

                if (action != null)
                {
                    yield return new ChatStreamEvent { Action = action };
                }

                continue;
            }

            // After [DONE], only action events are expected — skip any stray text.
            if (doneReceived)
                continue;

            // Default: text chunk event.
            string? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<string>(data);
            }
            catch (JsonException)
            {
                _logger.LogDebug("Skipping malformed SSE chunk: {Data}", data);
            }

            if (!string.IsNullOrEmpty(chunk))
            {
                yield return new ChatStreamEvent { Text = chunk };
            }
        }
    }

    /// <summary>
    /// Extracts text content from a PDF file by sending it to the API.
    /// The API uses iText7 to parse the PDF and return the extracted text.
    ///
    /// How it works:
    /// 1. Wraps the PDF byte array in a MultipartFormDataContent (simulates file upload)
    /// 2. Sends it to POST /api/chat/extract-pdf
    /// 3. Parses the JSON response { "text": "..." } and returns the text
    ///
    /// Returns null if extraction fails (corrupt file, server error, etc.).
    /// </summary>
    /// <param name="fileBytes">Raw byte array of the PDF file.</param>
    /// <param name="fileName">Original file name (sent to the API for logging).</param>
    /// <returns>Extracted text content, or null on failure.</returns>
    public async Task<string?> ExtractPdfTextAsync(byte[] fileBytes, string fileName)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("POST (multipart) /api/chat/extract-pdf — file: {FileName}", fileName);

            // Build multipart/form-data content with the PDF file.
            // The field name "file" must match the IFormFile parameter name in the controller.
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            content.Add(fileContent, "file", fileName);

            var response = await _httpClient.PostAsync("/api/chat/extract-pdf", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("PDF extraction failed with status {Status}: {Error}",
                    response.StatusCode, errorBody);

                // Try to extract a user-friendly error message from JSON response.
                try
                {
                    var errorDoc = JsonDocument.Parse(errorBody);
                    if (errorDoc.RootElement.TryGetProperty("message", out var msgProp))
                    {
                        throw new ApiException(response.StatusCode, msgProp.GetString() ?? "PDF extraction failed.", "/api/chat/extract-pdf");
                    }
                }
                catch (JsonException) { }

                throw new ApiException(response.StatusCode, "PDF extraction failed.", "/api/chat/extract-pdf");
            }

            // Parse the response JSON: { "text": "extracted content..." }
            var responseBody = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(responseBody);

            if (doc.RootElement.TryGetProperty("text", out var textProp))
            {
                return textProp.GetString();
            }

            _logger.LogWarning("PDF extraction response did not contain 'text' property");
            return null;
        }
        catch (ApiException)
        {
            throw; // Re-throw API exceptions as-is for the UI to handle.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from PDF '{FileName}'", fileName);
            return null;
        }
    }

    /// <summary>
    /// Deletes a conversation and all its messages.
    /// </summary>
    public async Task<bool> DeleteConversationAsync(long conversationId)
    {
        return await DeleteAsync($"/api/chat/conversations/{conversationId}");
    }

    /// <summary>
    /// Gets the list of available AI provider names.
    /// </summary>
    public async Task<List<string>> GetProvidersAsync()
    {
        try
        {
            return await GetAsync<List<string>>("/api/chat/providers")
                   ?? new List<string>();
        }
        catch (ApiException)
        {
            return new List<string>();
        }
    }
}
