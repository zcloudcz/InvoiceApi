using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// AI provider implementation for Ollama (local LLM server).
/// Uses the Ollama REST API via HttpClient — no NuGet SDK needed.
///
/// Ollama runs locally on the user's machine (default: http://localhost:11434).
/// No API key required — just a running Ollama instance with a model pulled.
///
/// Supports native tool/function calling via Ollama's tools API parameter.
/// This is much more reliable than text-based tool instructions because:
/// - Models are fine-tuned to produce structured tool calls (not free-text JSON)
/// - The API enforces parameter schema
/// - No need for regex-based intent detection — the model decides when to use tools
///
/// API docs: https://github.com/ollama/ollama/blob/main/docs/api.md
/// </summary>
public class OllamaProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ILogger<OllamaProvider> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ProviderName => "Ollama";

    /// <summary>
    /// Whether the current Ollama model supports native tool calling.
    /// Not all Ollama models support tools — e.g., gemma3 and phi4 do NOT,
    /// while llama3.1 and qwen2.5-coder DO.
    ///
    /// This starts as true (optimistic). If the model returns a "does not support tools"
    /// error, it's set to false for the rest of the application lifetime — subsequent
    /// requests skip the tool-calling path and go straight to streaming.
    /// </summary>
    private bool _supportsNativeTools = true;
    public bool SupportsNativeTools => _supportsNativeTools;

    public OllamaProvider(
        HttpClient httpClient,
        IOptions<AiSettings> settings,
        ILogger<OllamaProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        var config = settings.Value.Ollama;
        _httpClient.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/'));
        // Default to gemma3:12b — a multimodal model that supports both text and image inputs.
        _model = string.IsNullOrEmpty(config.Model) ? "gemma3:12b" : config.Model;
    }

    /// <summary>
    /// Sends messages to Ollama and returns the complete response.
    /// Uses stream: false for a single response.
    /// </summary>
    public async Task<string> GetCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, systemPrompt, stream: false);

        _logger.LogDebug("Sending {Count} messages to Ollama model {Model}", messages.Count, _model);

        var response = await _httpClient.PostAsJsonAsync("/api/chat", requestBody, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, ct);
        return result?.Message?.Content ?? string.Empty;
    }

    /// <summary>
    /// Streams the response from Ollama.
    /// Ollama streams NDJSON (newline-delimited JSON) — each line is a complete JSON object.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, systemPrompt, stream: true);

        _logger.LogDebug("Starting streaming from Ollama model {Model}", _model);

        var requestContent = new StringContent(
            JsonSerializer.Serialize(requestBody, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = requestContent };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Ollama streams NDJSON: each line is a JSON object with "message.content".
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);

            if (string.IsNullOrEmpty(line))
                continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, JsonOptions);
            var text = chunk?.Message?.Content;

            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }

            // Ollama sends done: true on the last chunk.
            if (chunk?.Done == true)
                break;
        }
    }

    /// <summary>
    /// Sends messages with native tool definitions to Ollama's /api/chat endpoint.
    /// Ollama models (llama3.1+, gemma3, mistral, etc.) support function calling
    /// via the "tools" parameter — the model produces structured tool_calls in the response.
    ///
    /// Returns a NativeToolCallResult with either:
    /// - Tool calls (model chose to use a tool) — ChatService will execute and continue
    /// - Text content (model chose to respond directly) — used as the AI response
    /// - Null if something went wrong
    ///
    /// Junior note: This is the key improvement for Ollama — instead of asking the model
    /// to produce JSON in free text (unreliable with small models), we use the native API
    /// which the model was specifically fine-tuned to handle.
    /// </summary>
    public async Task<NativeToolCallResult?> GetCompletionWithToolsAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt,
        List<NativeToolDefinition> tools,
        CancellationToken ct = default)
    {
        var ollamaMessages = BuildOllamaMessages(messages, systemPrompt);
        var ollamaTools = BuildOllamaTools(tools);

        var requestBody = new
        {
            model = _model,
            messages = ollamaMessages,
            tools = ollamaTools,
            stream = false
        };

        _logger.LogInformation(
            "Sending {Count} messages to Ollama with {ToolCount} native tools (model: {Model})",
            messages.Count, tools.Count, _model);

        var response = await _httpClient.PostAsJsonAsync("/api/chat", requestBody, JsonOptions, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        // Some Ollama models (gemma3, phi4) don't support tools and return an error.
        // Detect this and disable native tools for subsequent requests so ChatService
        // falls through to text-based streaming instead of blocking on every message.
        if (!response.IsSuccessStatusCode || responseText.Contains("does not support tools"))
        {
            _logger.LogWarning(
                "Ollama model {Model} does not support native tools. " +
                "Disabling native tool calling — future requests will use streaming directly.",
                _model);
            _supportsNativeTools = false;
            return null; // null tells ChatService to fall back to regular streaming.
        }

        _logger.LogDebug("Ollama native tool response: {Response}",
            responseText.Length > 500 ? responseText[..500] + "..." : responseText);

        var result = JsonSerializer.Deserialize<OllamaChatResponse>(responseText, JsonOptions);

        if (result?.Message == null)
            return null;

        // Check if the model produced tool calls.
        if (result.Message.ToolCalls is { Count: > 0 })
        {
            var nativeResult = new NativeToolCallResult();

            foreach (var toolCall in result.Message.ToolCalls)
            {
                if (toolCall.Function == null)
                    continue;

                var args = new Dictionary<string, string>();

                // Parse the function arguments — Ollama returns them as a JSON object.
                if (toolCall.Function.Arguments is { } argsElement)
                {
                    foreach (var prop in argsElement.EnumerateObject())
                    {
                        // Convert all values to strings for compatibility with IChatTool.ExecuteAsync.
                        args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? ""
                            : prop.Value.GetRawText();
                    }
                }

                _logger.LogInformation(
                    "Ollama native tool call: {ToolName}({Args})",
                    toolCall.Function.Name,
                    string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}")));

                nativeResult.ToolCalls.Add(new NativeToolCall
                {
                    ToolName = toolCall.Function.Name ?? "",
                    Arguments = args
                });
            }

            return nativeResult;
        }

        // Model chose to respond with text (no tool call needed).
        return new NativeToolCallResult
        {
            TextContent = result.Message.Content
        };
    }

    // ─── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Builds the Ollama chat API request body (without tools).
    /// Ollama uses "system", "user", "assistant" roles in the messages array.
    /// </summary>
    private object BuildRequestBody(List<ChatMessageDto> messages, string? systemPrompt, bool stream)
    {
        var ollamaMessages = BuildOllamaMessages(messages, systemPrompt);

        return new
        {
            model = _model,
            messages = ollamaMessages,
            stream
        };
    }

    /// <summary>
    /// Converts ChatMessageDto list + system prompt into Ollama message format.
    /// Shared between regular and tool-calling request builders.
    /// </summary>
    private List<object> BuildOllamaMessages(List<ChatMessageDto> messages, string? systemPrompt)
    {
        var ollamaMessages = new List<object>();

        // System prompt goes as a system message at the start.
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            ollamaMessages.Add(new { role = "system", content = systemPrompt });
        }

        foreach (var msg in messages)
        {
            var role = msg.Role.ToLowerInvariant() switch
            {
                "system" => "system",
                "assistant" => "assistant",
                _ => "user"
            };

            // If the message has attached images (e.g., from user upload),
            // include them in the Ollama request. Multimodal models like Gemma3
            // accept a "images" array of base64-encoded strings alongside the text content.
            if (msg.Images is { Count: > 0 })
            {
                ollamaMessages.Add(new { role, content = msg.Content, images = msg.Images });
            }
            else
            {
                ollamaMessages.Add(new { role, content = msg.Content });
            }
        }

        return ollamaMessages;
    }

    /// <summary>
    /// Converts NativeToolDefinition list into Ollama's tools format.
    /// Ollama follows the OpenAI function calling format:
    ///   { "type": "function", "function": { "name": "...", "description": "...", "parameters": {...} } }
    ///
    /// The parameters follow JSON Schema format with "type": "object", "properties", and "required".
    /// </summary>
    private static List<object> BuildOllamaTools(List<NativeToolDefinition> tools)
    {
        return tools.Select(tool =>
        {
            // Build JSON Schema properties from NativeToolParameter list.
            var properties = new Dictionary<string, object>();
            foreach (var param in tool.Parameters)
            {
                var propDef = new Dictionary<string, object>
                {
                    ["type"] = param.Type,
                    ["description"] = param.Description
                };

                // Add enum constraint if the parameter has specific allowed values.
                if (param.EnumValues is { Count: > 0 })
                {
                    propDef["enum"] = param.EnumValues;
                }

                // Array parameters must declare the schema of their elements.
                if (param.ArrayItemType is { Length: > 0 })
                {
                    propDef["items"] = new Dictionary<string, object> { ["type"] = param.ArrayItemType };
                }

                properties[param.Name] = propDef;
            }

            return (object)new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = new
                    {
                        type = "object",
                        properties,
                        required = tool.Required
                    }
                }
            };
        }).ToList();
    }

    // ─── Ollama response models ───────────────────────────────────────────

    private class OllamaChatResponse
    {
        public OllamaMessage? Message { get; set; }
        public bool Done { get; set; }
    }

    private class OllamaMessage
    {
        public string? Role { get; set; }
        public string? Content { get; set; }

        /// <summary>
        /// Tool calls produced by the model when native tool calling is used.
        /// Null or empty when the model responds with text instead of a tool call.
        /// </summary>
        [JsonPropertyName("tool_calls")]
        public List<OllamaToolCall>? ToolCalls { get; set; }
    }

    /// <summary>
    /// Ollama tool call response format.
    /// Mirrors the OpenAI function calling response structure.
    /// </summary>
    private class OllamaToolCall
    {
        public OllamaFunctionCall? Function { get; set; }
    }

    private class OllamaFunctionCall
    {
        public string? Name { get; set; }

        /// <summary>
        /// Function arguments as a JSON element (object with key-value pairs).
        /// Ollama returns these as a JSON object, not a string — we parse them manually.
        /// </summary>
        public JsonElement? Arguments { get; set; }
    }
}
