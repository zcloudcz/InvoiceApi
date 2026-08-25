using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// AI provider implementation for Anthropic Claude.
/// Uses the tryAGI/Anthropic NuGet SDK (auto-generated from the official OpenAPI spec).
///
/// Supports both synchronous and streaming completions via the Messages API.
/// System prompts are passed as a separate parameter (Claude API separates system from messages).
///
/// Also supports native tool/function calling via the Claude Tools API.
/// Claude is excellent at tool selection — it reliably determines when and which tool to use.
///
/// API surface (v3.3.0):
///   - Non-streaming: client.Messages.MessagesPostAsync(model, messages, maxTokens, system: ...)
///   - Streaming: client.CreateMessageAsStreamAsync(CreateMessageParams)
///   - Messages: InputMessage with InputMessageRole.User / InputMessageRole.Assistant
///   - Text extraction: response.AsSimpleText() / evt.ContentBlockDelta?.Delta.TextDelta?.Text
///   - Tools: Tool with InputSchema, response.Content with ToolUseContent
/// </summary>
public class ClaudeProvider : IAiProvider, IDisposable
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly ILogger<ClaudeProvider> _logger;

    public string ProviderName => "Claude";

    /// <summary>
    /// Claude supports native tool calling via its Tools API.
    /// This allows the model to produce structured tool_use blocks in its response
    /// instead of free-text JSON that needs manual parsing.
    /// </summary>
    public bool SupportsNativeTools => true;

    public ClaudeProvider(IOptions<AiSettings> settings, ILogger<ClaudeProvider> logger)
    {
        _logger = logger;
        var config = settings.Value.Claude;
        _client = new AnthropicClient(config.ApiKey);
        _model = string.IsNullOrEmpty(config.Model) ? "claude-sonnet-4-6" : config.Model;
    }

    /// <summary>
    /// Sends messages to Claude and returns the complete response.
    /// Uses the Messages API via client.Messages.MessagesPostAsync().
    /// </summary>
    public async Task<string> GetCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var anthropicMessages = BuildMessages(messages);

        _logger.LogDebug("Sending {Count} messages to Claude model {Model}", messages.Count, _model);

        try
        {
            var response = await _client.Messages.MessagesPostAsync(
                model: _model,
                messages: anthropicMessages,
                maxTokens: 4096,
                system: systemPrompt,
                temperature: 0.2,
                cancellationToken: ct);

            return response.AsSimpleText();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude API error: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Streams the response from Claude token-by-token.
    /// Uses client.CreateMessageAsStreamAsync() which returns IAsyncEnumerable of MessageStreamEvent.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var anthropicMessages = BuildMessages(messages);

        _logger.LogDebug("Starting streaming from Claude model {Model}", _model);

        var events = _client.CreateMessageAsStreamAsync(
            new CreateMessageParams
            {
                Model = _model,
                Messages = anthropicMessages,
                MaxTokens = 4096,
                System = systemPrompt
            },
            cancellationToken: ct);

        await foreach (var evt in events.WithCancellation(ct))
        {
            var text = evt.ContentBlockDelta?.Delta.TextDelta?.Text;
            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// Sends messages with native tool definitions to Claude's Messages API.
    /// Claude uses the "tools" parameter to receive tool definitions, and responds with
    /// tool_use content blocks when it decides to call a tool.
    ///
    /// Claude's tool calling is very reliable — it correctly determines:
    /// - WHEN to use a tool (vs. just answering in text)
    /// - WHICH tool to use (based on the user's intent)
    /// - WHAT parameters to pass (extracted from the conversation)
    ///
    /// Response handling:
    /// - If response contains ToolUseContent → extract tool name + arguments → return as NativeToolCallResult
    /// - If response contains only TextContent → return as text response
    /// </summary>
    public async Task<NativeToolCallResult?> GetCompletionWithToolsAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt,
        List<NativeToolDefinition> tools,
        CancellationToken ct = default)
    {
        var anthropicMessages = BuildMessages(messages);
        var anthropicTools = BuildClaudeTools(tools);

        _logger.LogInformation(
            "Sending {Count} messages to Claude with {ToolCount} native tools (model: {Model})",
            messages.Count, tools.Count, _model);

        try
        {
            // The SDK expects IList<OneOf<Tool, BashTool20250124, TextEditor20250124>>.
            // OneOf has an implicit conversion from Tool, so we cast each Tool to OneOf.
            var toolsParam = anthropicTools
                .Select(t => (OneOf<Tool, BashTool20250124, TextEditor20250124>)t)
                .ToList();

            var response = await _client.Messages.MessagesPostAsync(
                model: _model,
                messages: anthropicMessages,
                maxTokens: 4096,
                system: systemPrompt,
                tools: toolsParam,
                cancellationToken: ct);

            // Check if Claude chose to use a tool.
            // Claude's response.Content is a list of ContentBlock items:
            // - TextContent: regular text response
            // - ToolUseContent: structured tool call with name + input (JSON object)
            var result = new NativeToolCallResult();

            foreach (var block in response.Content)
            {
                // Check for tool_use content block.
                if (block.IsToolUse)
                {
                    var toolUse = block.ToolUse!;

                    _logger.LogInformation(
                        "Claude native tool call: {ToolName}, input: {Input}",
                        toolUse.Name, toolUse.Input);

                    var args = new Dictionary<string, string>();

                    // Parse the tool input — Claude returns it as a JsonNode/string.
                    // We need to convert it to Dictionary<string, string> for IChatTool compatibility.
                    if (toolUse.Input != null)
                    {
                        try
                        {
                            var inputJson = toolUse.Input.ToString();
                            if (!string.IsNullOrEmpty(inputJson))
                            {
                                using var doc = JsonDocument.Parse(inputJson);

                                // Shared with the text-based flow and with Ollama, so the same
                                // model answer produces the same arguments on every provider.
                                args = ToolArgumentReader.ReadArguments(doc.RootElement);
                            }
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse Claude tool input as JSON");
                        }
                    }

                    result.ToolCalls.Add(new NativeToolCall
                    {
                        ToolName = toolUse.Name,
                        Arguments = args
                    });
                }
                else if (block.IsText)
                {
                    // Collect text content (Claude sometimes includes text alongside tool calls).
                    result.TextContent = (result.TextContent ?? "") + block.Text;
                }
            }

            // If we found tool calls, return them (they take priority over text).
            if (result.HasToolCalls)
                return result;

            // No tool calls — return text content.
            return new NativeToolCallResult
            {
                TextContent = response.AsSimpleText()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude API error during tool calling: {Message}", ex.Message);
            return null;
        }
    }

    // ─── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Builds Claude Tool definitions from NativeToolDefinition list.
    /// Claude uses JSON Schema for tool input parameters.
    ///
    /// Format:
    ///   Tool { Name, Description, InputSchema { Type = "object", Properties = {...}, Required = [...] } }
    /// </summary>
    private static List<Tool> BuildClaudeTools(List<NativeToolDefinition> tools)
    {
        // The SDK's InputSchema class has no Required property, so we hand it the plain
        // JSON Schema dictionary built by the shared translator (Tool.InputSchema is object).
        return tools.Select(tool => new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = NativeToolSchema.BuildJsonSchema(tool)
        }).ToList();
    }

    /// <summary>
    /// Builds the Anthropic InputMessage list from our DTOs.
    /// Maps "User" → InputMessageRole.User, "Assistant" → InputMessageRole.Assistant.
    /// System messages are excluded (they go to the system parameter).
    /// </summary>
    private static List<InputMessage> BuildMessages(List<ChatMessageDto> messages)
    {
        var result = new List<InputMessage>();

        foreach (var msg in messages)
        {
            if (msg.Role == "System") continue;

            if (msg.Role == "Assistant")
            {
                result.Add(msg.Content.AsAssistantMessage());
            }
            else
            {
                result.Add(msg.Content);
            }
        }

        return result;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
