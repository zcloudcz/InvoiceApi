using System.Runtime.CompilerServices;
using System.Text;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Service for AI chat operations: conversation CRUD, message sending, and streaming.
///
/// Orchestrates the flow:
/// 1. User sends a message (optionally in a new or existing conversation)
/// 2. ChatContextBuilder builds the system prompt with tenant business data
/// 3. (Optional) If tool intent detected → two-pass flow with tool execution
/// 4. IAiProvider generates the AI response (sync or streaming)
/// 5. Both user and assistant messages are persisted to the tenant database
///
/// Tool flow (two-pass, when IČO+keyword or navigation keyword detected):
/// - First pass: non-streaming AI call with tool instructions → AI responds with JSON tool call
/// - Tool execution: ARES lookup, client creation, navigation, etc.
/// - Second pass: streaming AI call with tool results → natural-language response to user
/// - If the tool returns a UiAction, it's stored in _pendingUiAction for the controller to send
///
/// All operations are scoped to the current tenant (TenantDbContext).
/// </summary>
public class ChatService : IChatService
{
    private readonly TenantDbContext _context;
    private readonly ITenantResolver _tenantResolver;
    private readonly IAiProviderFactory _providerFactory;
    private readonly ICompanyAiSettingsResolver _companyAiResolver;
    private readonly IChatContextBuilder _contextBuilder;
    private readonly IChatToolExecutor _toolExecutor;
    private readonly ILogger<ChatService> _logger;

    /// <summary>
    /// Stores the UI action from the last tool execution in this request scope.
    /// Read by ChatController after streaming completes to send a post-stream SSE event.
    /// Safe because ChatService is scoped (one instance per HTTP request).
    /// </summary>
    private ChatUiAction? _pendingUiAction;

    public ChatService(
        TenantDbContext context,
        ITenantResolver tenantResolver,
        IAiProviderFactory providerFactory,
        ICompanyAiSettingsResolver companyAiResolver,
        IChatContextBuilder contextBuilder,
        IChatToolExecutor toolExecutor,
        ILogger<ChatService> logger)
    {
        _context = context;
        _tenantResolver = tenantResolver;
        _providerFactory = providerFactory;
        _companyAiResolver = companyAiResolver;
        _contextBuilder = contextBuilder;
        _toolExecutor = toolExecutor;
        _logger = logger;
    }

    /// <inheritdoc />
    public ChatUiAction? GetPendingUiAction() => _pendingUiAction;

    /// <summary>
    /// Lists all conversations for a user, ordered by most recent first.
    /// </summary>
    public async Task<List<ChatConversationListDto>> GetConversationsAsync(
        long userId, CancellationToken ct = default)
    {
        return await _context.ChatConversation
            .AsNoTracking()
            .Where(c => c.UserId == userId && !c.IsArchived)
            .OrderByDescending(c => c.LastMessageAt)
            .Select(c => new ChatConversationListDto
            {
                Id = c.Id,
                Title = c.Title,
                LastMessageAt = c.LastMessageAt,
                MessageCount = c.Messages.Count(m => m.Role != EChatRole.System)
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Loads a single conversation with all its messages.
    /// Throws ChatConversationNotFoundException if the conversation does not exist
    /// or does not belong to the user.
    /// </summary>
    public async Task<ChatConversationDto> GetConversationAsync(
        long conversationId, long userId, CancellationToken ct = default)
    {
        var conversation = await _context.ChatConversation
            .AsNoTracking()
            .Include(c => c.Messages.Where(m => m.Role != EChatRole.System))
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
            ?? throw new ChatConversationNotFoundException(conversationId);

        return new ChatConversationDto
        {
            Id = conversation.Id,
            Title = conversation.Title,
            LastMessageAt = conversation.LastMessageAt,
            IsArchived = conversation.IsArchived,
            MessageCount = conversation.Messages.Count,
            Messages = conversation.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(MapMessageToDto)
                .ToList()
        };
    }

    /// <summary>
    /// Sends a message and returns the complete AI response (non-streaming).
    /// Creates a new conversation if ConversationId is null.
    /// </summary>
    public async Task<SendMessageResponse> SendMessageAsync(
        long userId, SendMessageRequest request, CancellationToken ct = default)
    {
        // Resolve or create the conversation.
        var conversation = await GetOrCreateConversationAsync(userId, request.ConversationId, request.Message, ct);

        // If a PDF file is attached, prepend its content to the user message.
        // This gives the AI full context of the document so it can answer questions about it.
        var effectiveMessage = BuildMessageWithAttachment(request);

        // Save the user's message.
        var userMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = EChatRole.User,
            Content = effectiveMessage
        };
        _context.ChatMessage.Add(userMessage);
        await _context.SaveChangesAsync(ct);

        // Build context and get AI response.
        var provider = await ResolveProviderAsync(request.Provider, ct);
        var systemPrompt = await _contextBuilder.BuildSystemPromptAsync(ct);
        var history = await GetConversationHistoryAsync(conversation.Id, ct);

        // If an image is attached, set the transient Images property on the last user message.
        // This passes the base64 data to the AI provider without storing it in the database.
        AttachImageToLastMessage(history, request);

        string responseText;

        // ─── Path A: Native tool calling (for providers that support it) ────────────
        // Always send tool definitions — the model decides when to use a tool.
        // No regex gate needed: models with native tool support are fine-tuned to determine
        // when a tool is appropriate based on the user's message and context.
        if (provider.SupportsNativeTools)
        {
            responseText = await HandleNativeToolCallAsync(
                provider, history, systemPrompt, conversation.Id, ct);
        }
        // ─── Path B: Text-based tool calling (for providers WITHOUT native tool API) ─
        // Always include tool instructions — the LLM decides whether to use a tool.
        // No regex gate: the model is the decision maker.
        else
        {
            _logger.LogInformation(
                "Using text-based tool flow for {Provider} in conversation {ConversationId}",
                provider.ProviderName, conversation.Id);

            // First pass: AI call with tool instructions appended to the system prompt.
            var toolSystemPrompt = systemPrompt + _toolExecutor.BuildToolInstructions();
            var firstPassResponse = await provider.GetCompletionAsync(history, toolSystemPrompt, ct);

            // Try to parse a structured tool call from the AI response.
            var toolCall = _toolExecutor.ParseToolCall(firstPassResponse);

            if (toolCall != null)
            {
                _logger.LogInformation("Tool call parsed: {Action}, executing...", toolCall.Action);

                // Execute the tool (ARES lookup, client creation, navigation, etc.).
                var toolResult = await _toolExecutor.ExecuteToolAsync(toolCall, ct);

                // Capture any UI action for the controller to send after the response.
                _pendingUiAction = toolResult.UiAction;

                // Second pass: AI call with tool result injected into the system prompt.
                var resultPrompt = BuildToolResultPrompt(systemPrompt, toolCall.Action, toolResult);

                responseText = await provider.GetCompletionAsync(history, resultPrompt, ct);
            }
            else
            {
                // AI decided no tool was needed — use its text response directly.
                _logger.LogDebug("No tool call in first pass, using text response directly");
                responseText = firstPassResponse;
            }
        }

        // Save the assistant's response.
        var assistantMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = EChatRole.Assistant,
            Content = responseText,
            ProviderUsed = provider.ProviderName
        };
        _context.ChatMessage.Add(assistantMessage);

        // Update conversation metadata.
        conversation.LastMessageAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return new SendMessageResponse
        {
            ConversationId = conversation.Id,
            AssistantMessage = MapMessageToDto(assistantMessage)
        };
    }

    /// <summary>
    /// Sends a message and streams the AI response token-by-token.
    /// The full response is accumulated and saved to the database after streaming completes.
    /// </summary>
    public async IAsyncEnumerable<string> StreamMessageAsync(
        long userId, SendMessageRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Resolve or create the conversation.
        var conversation = await GetOrCreateConversationAsync(userId, request.ConversationId, request.Message, ct);

        // If a PDF file is attached, prepend its content to the user message.
        var effectiveMessage = BuildMessageWithAttachment(request);

        // Save the user's message.
        var userMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = EChatRole.User,
            Content = effectiveMessage
        };
        _context.ChatMessage.Add(userMessage);
        await _context.SaveChangesAsync(ct);

        // Build context and start streaming.
        var provider = await ResolveProviderAsync(request.Provider, ct);
        var systemPrompt = await _contextBuilder.BuildSystemPromptAsync(ct);
        var history = await GetConversationHistoryAsync(conversation.Id, ct);

        // If an image is attached, set the transient Images property on the last user message.
        // This passes the base64 data to the AI provider without storing it in the database.
        AttachImageToLastMessage(history, request);

        // ─── Path A: Native tool calling (for providers that support it) ────────────
        // Always send tool definitions — the model decides when to use a tool.
        // The initial non-streaming call may block briefly while the model processes,
        // but the UI shows a "thinking" spinner during this time. This is acceptable
        // because it enables reliable tool calling without regex heuristics.
        if (provider.SupportsNativeTools)
        {
            _logger.LogInformation(
                "Using native tool calling for {Provider} in conversation {ConversationId}",
                provider.ProviderName, conversation.Id);

            // Heartbeat to keep SSE connection alive during the non-streaming tool call.
            yield return "";

            var toolDefs = _toolExecutor.GetToolDefinitions();
            var nativeResult = await provider.GetCompletionWithToolsAsync(history, systemPrompt, toolDefs, ct);

            // ── Multi-step tool call loop ─────────────────────────────────────
            // AI can chain tool calls (e.g., create_client → import_invoice).
            // After each FAILED tool, we let AI try another tool (max 3 iterations).
            // After a SUCCESSFUL tool, we stop the loop and stream the final response.
            // This prevents the AI from re-calling the same tool and creating duplicates.
            const int maxToolIterations = 3;
            var toolIteration = 0;
            var toolResultsLog = new StringBuilder();

            // Issue #212: the closing instruction must match the LAST result. A confirmable tool
            // called without approval only produced a preview, and a preview always ends the loop
            // (it is reported as success), so the last result is the one that decides the framing.
            var lastResultNeedsConfirmation = false;

            while (nativeResult?.HasToolCalls == true && toolIteration < maxToolIterations)
            {
                toolIteration++;
                var nativeToolCall = nativeResult.ToolCalls[0];
                _logger.LogInformation("Tool call #{Iteration}: {ToolName}", toolIteration, nativeToolCall.ToolName);

                // Send heartbeat to keep SSE connection alive during tool execution.
                yield return "";

                var parsedCall = new ParsedToolCall
                {
                    Action = nativeToolCall.ToolName,
                    Parameters = nativeToolCall.Arguments
                };
                var toolResult = await _toolExecutor.ExecuteToolAsync(parsedCall, ct);

                // Keep the LAST UI action (e.g., navigate to the created invoice).
                if (toolResult.UiAction != null)
                    _pendingUiAction = toolResult.UiAction;

                // Accumulate tool results so AI has full context. Same wording as every other
                // tool flow — a preview is announced as "was NOT executed" here as well.
                toolResultsLog.AppendLine(DescribeToolResult(nativeToolCall.ToolName, toolResult));
                lastResultNeedsConfirmation = toolResult.RequiresConfirmation;

                // If the tool SUCCEEDED → stop the loop. Don't let AI call more tools
                // because it tends to re-call the same tool and create duplicates.
                if (toolResult.IsSuccess)
                {
                    _logger.LogInformation("Tool '{Tool}' succeeded — stopping tool loop", nativeToolCall.ToolName);
                    break;
                }

                // Tool FAILED → let AI try a different tool (e.g., create_client after "client not found").
                _logger.LogInformation("Tool '{Tool}' failed — letting AI try another tool", nativeToolCall.ToolName);
                var iterationPrompt = systemPrompt + "\n\n" + toolResultsLog +
                    "\nThe previous tool failed. If you can fix the issue with another tool, do so now. " +
                    "Otherwise explain the error to the user.";

                nativeResult = await provider.GetCompletionWithToolsAsync(
                    history, iterationPrompt, toolDefs, ct);
            }

            // After the loop: stream the final AI response summarizing all tool results.
            if (toolIteration > 0)
            {
                var finalPrompt = systemPrompt + "\n\n" + toolResultsLog +
                    "\nDo NOT call any more tools. " +
                    BuildToolResultInstruction(lastResultNeedsConfirmation);

                string finalText;
                if (nativeResult != null && !nativeResult.HasToolCalls
                    && !string.IsNullOrEmpty(nativeResult.TextContent))
                {
                    finalText = nativeResult.TextContent;
                    yield return finalText;
                }
                else
                {
                    var toolFullResponse = new StringBuilder();
                    await foreach (var chunk in provider.StreamCompletionAsync(history, finalPrompt, ct))
                    {
                        toolFullResponse.Append(chunk);
                        yield return chunk;
                    }
                    finalText = toolFullResponse.ToString();
                }

                var nativeAssistantMsg = new ChatMessage
                {
                    ConversationId = conversation.Id,
                    Role = EChatRole.Assistant,
                    Content = finalText,
                    ProviderUsed = provider.ProviderName
                };
                _context.ChatMessage.Add(nativeAssistantMsg);
                conversation.LastMessageAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                yield break;
            }

            // Model received tools but chose not to use them — use its text directly.
            if (!string.IsNullOrEmpty(nativeResult?.TextContent))
            {
                _logger.LogDebug("Native tool calling: model chose text response, yielding directly");

                yield return nativeResult.TextContent;

                var directMsg = new ChatMessage
                {
                    ConversationId = conversation.Id,
                    Role = EChatRole.Assistant,
                    Content = nativeResult.TextContent,
                    ProviderUsed = provider.ProviderName
                };
                _context.ChatMessage.Add(directMsg);
                conversation.LastMessageAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);
                yield break;
            }

            // Null result — fall through to regular streaming as fallback.
            _logger.LogDebug("Native tool calling returned null, falling through to regular streaming");
        }

        // ─── Path B: Text-based tool calling (for providers WITHOUT native tool API) ─
        // For models that don't support the native tools API (e.g., gemma3, phi4),
        // we always do a "first pass" non-streaming call with tool instructions in the system prompt.
        // The model decides whether to produce a JSON tool call or a regular text response.
        // No regex gate — the LLM is the decision maker, not a regex pattern.
        //
        // Flow:
        // 1. First pass (non-streaming): AI sees tool instructions → responds with JSON tool call or text
        // 2a. If tool call → execute tool → second pass (streaming) with tool result → yield chunks
        // 2b. If no tool call → yield the first-pass text directly (no second call)
        if (!provider.SupportsNativeTools)
        {
            _logger.LogInformation(
                "Using text-based tool flow for {Provider} in conversation {ConversationId}",
                provider.ProviderName, conversation.Id);

            // Yield an empty string as a SSE heartbeat to keep the connection alive.
            // The first pass is non-streaming and can take 30-120+ seconds with local models.
            // Without this, the HTTP client may time out before the model responds.
            // The empty string is harmless — ChatApiService skips empty chunks.
            yield return "";

            // First pass: non-streaming call with tool instructions appended to the system prompt.
            // The model sees the available tools and decides whether to use one.
            var toolSystemPrompt = systemPrompt + _toolExecutor.BuildToolInstructions();
            var firstPassResponse = await provider.GetCompletionAsync(history, toolSystemPrompt, ct);

            // Try to parse a structured tool call from the AI response.
            var toolCall = _toolExecutor.ParseToolCall(firstPassResponse);

            if (toolCall != null)
            {
                _logger.LogInformation("Tool call parsed: {Action}, executing...", toolCall.Action);

                // Execute the tool (ARES lookup, client creation, navigation, etc.).
                var toolResult = await _toolExecutor.ExecuteToolAsync(toolCall, ct);

                // Capture any UI action for the controller to send after streaming completes.
                _pendingUiAction = toolResult.UiAction;

                // Second pass: stream the final response with tool result in the system prompt.
                var resultPrompt = BuildToolResultPrompt(systemPrompt, toolCall.Action, toolResult);

                var toolFullResponse = new StringBuilder();

                await foreach (var chunk in provider.StreamCompletionAsync(history, resultPrompt, ct))
                {
                    toolFullResponse.Append(chunk);
                    yield return chunk;
                }

                // Save the assistant's response after streaming completes.
                var toolAssistantMessage = new ChatMessage
                {
                    ConversationId = conversation.Id,
                    Role = EChatRole.Assistant,
                    Content = toolFullResponse.ToString(),
                    ProviderUsed = provider.ProviderName
                };
                _context.ChatMessage.Add(toolAssistantMessage);
                conversation.LastMessageAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                yield break; // Two-pass complete, done.
            }

            // AI decided no tool was needed — yield its text response directly.
            // No second AI call needed — avoids double latency.
            _logger.LogDebug("First pass: no tool call, yielding text response directly");

            yield return firstPassResponse;

            var firstPassMsg = new ChatMessage
            {
                ConversationId = conversation.Id,
                Role = EChatRole.Assistant,
                Content = firstPassResponse,
                ProviderUsed = provider.ProviderName
            };
            _context.ChatMessage.Add(firstPassMsg);
            conversation.LastMessageAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            yield break;
        }

        // ─── Path C: Regular streaming (should not normally reach here) ──
        _logger.LogInformation(
            "Streaming message from {Provider} in conversation {ConversationId}",
            provider.ProviderName, conversation.Id);

        // Accumulate the full response while streaming chunks to the caller.
        var fullResponse = new StringBuilder();

        await foreach (var chunk in provider.StreamCompletionAsync(history, systemPrompt, ct))
        {
            fullResponse.Append(chunk);
            yield return chunk;
        }

        // After streaming completes, save the full assistant response.
        var assistantMessage = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = EChatRole.Assistant,
            Content = fullResponse.ToString(),
            ProviderUsed = provider.ProviderName
        };
        _context.ChatMessage.Add(assistantMessage);

        conversation.LastMessageAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes a conversation and all its messages (cascade delete configured in EF).
    /// </summary>
    public async Task DeleteConversationAsync(
        long conversationId, long userId, CancellationToken ct = default)
    {
        var conversation = await _context.ChatConversation
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
            ?? throw new ChatConversationNotFoundException(conversationId);

        _context.ChatConversation.Remove(conversation);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted conversation {ConversationId} for user {UserId}", conversationId, userId);
    }

    /// <summary>
    /// Returns the list of available AI provider names for the current company.
    /// Includes company-specific providers (if configured) plus system-wide providers.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailableProvidersAsync(CancellationToken ct = default)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? ParseCompanyIdFromSchema(_context.Schema);
        return await _companyAiResolver.GetAvailableProvidersAsync(companyId, ct);
    }

    // ─── Private helpers ────────────────────────────────────────────────

    /// <summary>
    /// Builds the second-pass system prompt that turns a raw tool result into an answer.
    /// Shared by all tool flows (streaming / non-streaming, native / text-based) so the
    /// model is framed identically no matter which provider the tenant uses.
    ///
    /// The distinction that matters: a confirmable tool that was called without approval
    /// only produced a PREVIEW (see <see cref="IConfirmableChatTool"/>). Telling the model it
    /// "was executed" would make the assistant announce a change that never happened.
    /// </summary>
    private static string BuildToolResultPrompt(string systemPrompt, string toolName, ChatToolResult result)
        => systemPrompt + "\n\n" + DescribeToolResult(toolName, result)
           + "\n\n" + BuildToolResultInstruction(result.RequiresConfirmation);

    /// <summary>
    /// Renders one tool result for the second-pass prompt. The native streaming flow accumulates
    /// several of these into one log, the other flows use exactly one — either way the sentence
    /// that says whether the tool actually ran comes from here, never from the tool's own text.
    /// </summary>
    private static string DescribeToolResult(string toolName, ChatToolResult result)
        => result.RequiresConfirmation
            ? $"Tool '{toolName}' was NOT executed — nothing has been changed. " +
              $"It returned a preview of the change:\n{result.OutputText}"
            : $"Tool '{toolName}' was executed. Result:\n{result.OutputText}";

    /// <summary>
    /// Closing instruction of the second-pass prompt — the last thing the model reads, so it has
    /// to match the framing above. A preview must never be summarised as a completed change.
    /// </summary>
    private static string BuildToolResultInstruction(bool requiresConfirmation)
        => requiresConfirmation
            ? "Show the user what would change and ask them to confirm. " +
              "Do NOT claim the change is done. Respond in the same language as the user."
            : "Now respond to the user in a friendly, concise way based on the tool result above. " +
              "Include the key information from the result. Respond in the same language as the user.";

    /// <summary>
    /// Handles the non-streaming native tool calling flow.
    /// Sends tool definitions to the provider, executes any tool calls,
    /// and returns the final AI response text.
    ///
    /// Flow:
    /// 1. Send messages + tool definitions to the provider
    /// 2. If the model produces a tool call → execute it → second AI call with results
    /// 3. If the model produces text → return it directly
    /// </summary>
    private async Task<string> HandleNativeToolCallAsync(
        IAiProvider provider,
        List<ChatMessageDto> history,
        string systemPrompt,
        long conversationId,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Using native tool calling for {Provider} in conversation {ConversationId}",
            provider.ProviderName, conversationId);

        var toolDefs = _toolExecutor.GetToolDefinitions();
        var nativeResult = await provider.GetCompletionWithToolsAsync(history, systemPrompt, toolDefs, ct);

        if (nativeResult?.HasToolCalls == true)
        {
            // Execute the first tool call (single-tool per turn for now).
            var nativeToolCall = nativeResult.ToolCalls[0];
            _logger.LogInformation("Native tool call: {ToolName}, executing...", nativeToolCall.ToolName);

            var parsedCall = new ParsedToolCall
            {
                Action = nativeToolCall.ToolName,
                Parameters = nativeToolCall.Arguments
            };
            var toolResult = await _toolExecutor.ExecuteToolAsync(parsedCall, ct);

            // Capture any UI action for the controller.
            _pendingUiAction = toolResult.UiAction;

            // Second pass: AI generates a natural-language response using the tool result.
            var resultPrompt = BuildToolResultPrompt(systemPrompt, nativeToolCall.ToolName, toolResult);

            return await provider.GetCompletionAsync(history, resultPrompt, ct);
        }

        // Model chose not to use a tool — return its text response.
        if (!string.IsNullOrEmpty(nativeResult?.TextContent))
            return nativeResult.TextContent;

        // Fallback: regular completion without tools.
        _logger.LogDebug("Native tool calling returned null, falling back to regular completion");
        return await provider.GetCompletionAsync(history, systemPrompt, ct);
    }

    /// <summary>
    /// Gets an existing conversation or creates a new one.
    /// Auto-generates the title from the first user message (first 50 chars).
    /// </summary>
    private async Task<ChatConversation> GetOrCreateConversationAsync(
        long userId, long? conversationId, string firstMessage, CancellationToken ct)
    {
        if (conversationId.HasValue)
        {
            var existing = await _context.ChatConversation
                .FirstOrDefaultAsync(c => c.Id == conversationId.Value && c.UserId == userId, ct)
                ?? throw new ChatConversationNotFoundException(conversationId.Value);
            return existing;
        }

        // Create a new conversation with an auto-generated title.
        var title = firstMessage.Length > 50
            ? firstMessage[..50] + "..."
            : firstMessage;

        var conversation = new ChatConversation
        {
            UserId = userId,
            Title = title,
            LastMessageAt = DateTime.UtcNow,
            IsArchived = false
        };

        _context.ChatConversation.Add(conversation);
        await _context.SaveChangesAsync(ct);

        return conversation;
    }

    /// <summary>
    /// Loads the full conversation history as DTOs for sending to the AI provider.
    /// Only includes User and Assistant messages (System messages are handled via systemPrompt).
    /// </summary>
    private async Task<List<ChatMessageDto>> GetConversationHistoryAsync(
        long conversationId, CancellationToken ct)
    {
        return await _context.ChatMessage
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Role != EChatRole.System)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessageDto
            {
                Id = m.Id,
                Role = m.Role.ToString(),
                Content = m.Content,
                ProviderUsed = m.ProviderUsed,
                CreatedAt = m.CreatedAt
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Resolves the AI provider for the current request.
    /// Uses company-level AI settings first (from CompanySystemSettings),
    /// then falls back to system-wide settings (from appsettings.json).
    ///
    /// This enables per-company AI provider configuration (e.g., company's own Claude API key)
    /// following the same 2-tier pattern as SMTP resolution in EmailService.
    /// </summary>
    private async Task<IAiProvider> ResolveProviderAsync(string? providerName, CancellationToken ct = default)
    {
        // Get CompanyId from ITenantResolver (reads JWT claims via IHttpContextAccessor).
        // If that returns null (can happen in Azure Functions dual-scope),
        // fall back to parsing the CompanyId from TenantDbContext.Schema ("tenant_42" → 42).
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? ParseCompanyIdFromSchema(_context.Schema);

        return await _companyAiResolver.ResolveProviderAsync(companyId, providerName, ct);
    }

    /// <summary>
    /// Extracts CompanyId from the tenant schema name (e.g., "tenant_42" → 42).
    /// Used as fallback when ITenantResolver can't read claims from IHttpContextAccessor.
    /// Returns null if the schema name doesn't follow the "tenant_{id}" convention.
    /// </summary>
    private static long? ParseCompanyIdFromSchema(string? schema)
    {
        if (string.IsNullOrEmpty(schema) || !schema.StartsWith("tenant_"))
            return null;

        return long.TryParse(schema.AsSpan("tenant_".Length), out var id) ? id : null;
    }

    /// <summary>
    /// Maps a ChatMessage entity to a ChatMessageDto.
    /// </summary>
    private static ChatMessageDto MapMessageToDto(ChatMessage message)
    {
        return new ChatMessageDto
        {
            Id = message.Id,
            Role = message.Role.ToString(),
            Content = message.Content,
            ProviderUsed = message.ProviderUsed,
            CreatedAt = message.CreatedAt
        };
    }

    /// <summary>
    /// Builds the effective message content by prepending attachment context (if any).
    /// Supports two types of attachments:
    ///   1. Image (base64) — stored as a short placeholder "[Image: filename.jpg]" in DB,
    ///      actual image bytes are passed separately via ChatMessageDto.Images
    ///   2. PDF (extracted text) — stored inline with the full extracted text
    ///
    /// Image takes precedence when both are set (edge case, UI prevents this).
    /// </summary>
    private static string BuildMessageWithAttachment(SendMessageRequest request)
    {
        // Image attachment takes precedence over PDF — the actual base64 data
        // is passed via ChatMessageDto.Images (not stored in the message text).
        // We only store a short placeholder in the database to indicate an image was attached.
        if (!string.IsNullOrWhiteSpace(request.AttachedImageBase64))
        {
            var imageFileName = request.AttachedFileName ?? "image.png";
            return $"[Image: {imageFileName}]\n\nUser question: {request.Message}";
        }

        // PDF attachment — extracted text is included inline for the AI to reference.
        if (!string.IsNullOrWhiteSpace(request.AttachedFileContent))
        {
            var fileName = request.AttachedFileName ?? "document.pdf";

            return $"""
                [Attached PDF: {fileName}]
                --- PDF Content ---
                {request.AttachedFileContent}
                --- End PDF Content ---

                User question: {request.Message}
                """;
        }

        // No attachment — return the original message as-is.
        return request.Message;
    }

    /// <summary>
    /// Attaches image data to the last user message in the conversation history.
    /// This sets the transient ChatMessageDto.Images property so that the AI provider
    /// (e.g., OllamaProvider with Gemma3) can include the image in its API request.
    ///
    /// The image is NOT stored in the database — only passed in-memory during this request.
    /// </summary>
    private static void AttachImageToLastMessage(List<ChatMessageDto> history, SendMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AttachedImageBase64))
            return;

        // Find the last user message in the history and attach the image to it.
        var lastUserMessage = history.LastOrDefault(m =>
            m.Role.Equals("User", StringComparison.OrdinalIgnoreCase));

        if (lastUserMessage != null)
        {
            lastUserMessage.Images = new List<string> { request.AttachedImageBase64 };
        }
    }
}
