using System.Security.Claims;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for AI chat operations.
/// Manages conversations, sends messages to AI providers, and streams responses via SSE.
///
/// All endpoints require JWT authentication — each user can only access their own conversations.
/// Data is scoped to the current tenant (resolved from JWT CompanyId).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly IChatService _chatService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(IChatService chatService, ILogger<ChatController> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    /// <summary>
    /// Lists all conversations for the authenticated user.
    /// Returns lightweight DTOs (no message content) for the sidebar list.
    /// </summary>
    /// <response code="200">List of conversations ordered by most recent first</response>
    [HttpGet("conversations")]
    [ProducesResponseType(typeof(List<ChatConversationListDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ChatConversationListDto>>> GetConversations(
        CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        var conversations = await _chatService.GetConversationsAsync(userId, ct);
        return Ok(conversations);
    }

    /// <summary>
    /// Gets a single conversation with all its messages.
    /// </summary>
    /// <param name="id">Conversation ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">Conversation with messages</response>
    /// <response code="404">Conversation not found or doesn't belong to user</response>
    [HttpGet("conversations/{id:long}")]
    [ProducesResponseType(typeof(ChatConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChatConversationDto>> GetConversation(
        long id, CancellationToken ct = default)
    {
        try
        {
            var userId = GetCurrentUserId();
            var conversation = await _chatService.GetConversationAsync(id, userId, ct);
            return Ok(conversation);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Sends a message and returns the complete AI response (non-streaming).
    /// If ConversationId is null, creates a new conversation automatically.
    /// </summary>
    /// <param name="request">Message request with optional conversation ID and provider override</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="200">AI response with conversation metadata</response>
    /// <response code="400">Invalid request</response>
    [HttpPost("send")]
    [ProducesResponseType(typeof(SendMessageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SendMessageResponse>> SendMessage(
        [FromBody] SendMessageRequest request, CancellationToken ct = default)
    {
        try
        {
            var userId = GetCurrentUserId();
            var response = await _chatService.SendMessageAsync(userId, request, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending chat message");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while processing your message." });
        }
    }

    /// <summary>
    /// Sends a message and streams the AI response via Server-Sent Events (SSE).
    /// Each event contains a JSON-encoded text chunk. The stream ends with "[DONE]".
    ///
    /// Client usage:
    ///   const response = await fetch('/api/chat/stream', { method: 'POST', body: JSON.stringify(request) });
    ///   const reader = response.body.getReader();
    ///   // Read "data: ..." lines, parse JSON chunks, display progressively.
    /// </summary>
    /// <param name="request">Message request with optional conversation ID and provider override</param>
    [HttpPost("stream")]
    public async Task StreamMessage([FromBody] SendMessageRequest request)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        try
        {
            var userId = GetCurrentUserId();

            await foreach (var chunk in _chatService.StreamMessageAsync(userId, request, HttpContext.RequestAborted))
            {
                // SSE format: "data: {json}\n\n"
                var json = JsonSerializer.Serialize(chunk);
                await Response.WriteAsync($"data: {json}\n\n", HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
            }

            // Signal end of stream.
            await Response.WriteAsync("data: \"[DONE]\"\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);

            // If a tool produced a UI action (e.g., navigation), send it as a separate SSE event.
            // The Blazor client reads this after [DONE] and executes the action.
            // Uses the standard SSE "event:" field to distinguish from text data events.
            var pendingAction = _chatService.GetPendingUiAction();
            if (pendingAction != null)
            {
                var actionJson = JsonSerializer.Serialize(pendingAction);
                await Response.WriteAsync($"event: action\ndata: {actionJson}\n\n", HttpContext.RequestAborted);
                await Response.Body.FlushAsync(HttpContext.RequestAborted);
                _logger.LogInformation("Sent UI action event: {ActionType} → {Url}",
                    pendingAction.Type, pendingAction.Url);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — this is normal for SSE, not an error.
            _logger.LogDebug("SSE stream cancelled (client disconnected)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during SSE streaming");

            // Try to send an error event to the client (may fail if connection is broken).
            try
            {
                await Response.WriteAsync(
                    $"data: {JsonSerializer.Serialize(new { error = "An error occurred." })}\n\n",
                    HttpContext.RequestAborted);
            }
            catch { /* Connection already closed */ }
        }
    }

    /// <summary>
    /// Deletes a conversation and all its messages.
    /// </summary>
    /// <param name="id">Conversation ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <response code="204">Conversation deleted</response>
    /// <response code="404">Conversation not found</response>
    [HttpDelete("conversations/{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteConversation(long id, CancellationToken ct = default)
    {
        try
        {
            var userId = GetCurrentUserId();
            await _chatService.DeleteConversationAsync(id, userId, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Returns the list of available AI provider names.
    /// Used by the UI to populate the provider dropdown.
    /// </summary>
    /// <response code="200">List of provider names (e.g., ["Claude", "OpenAI"])</response>
    [HttpGet("providers")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> GetProviders()
    {
        return Ok(_chatService.GetAvailableProviders());
    }

    // ─── Private helpers ────────────────────────────────────────────────

    /// <summary>
    /// Extracts the current user's ID from JWT claims.
    /// Uses the standard NameIdentifier claim type.
    /// </summary>
    private long GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(userIdClaim) || !long.TryParse(userIdClaim, out var userId))
        {
            throw new InvalidOperationException("User ID not found in JWT token.");
        }

        return userId;
    }
}
