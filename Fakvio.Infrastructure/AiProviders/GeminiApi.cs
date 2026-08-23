using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// Gemini REST wire format in one place — request body, response shape and native
/// function calling.
///
/// Gemini has no official .NET SDK here, so the payloads are hand-built. They are needed
/// by two providers with identical wire behaviour: the singleton <see cref="GeminiProvider"/>
/// and the per-company <c>AdHocGeminiProvider</c> in <c>CompanyAiSettingsResolver</c>.
/// Keeping the format here means a change to the Gemini contract is a one-file change.
///
/// API docs: https://ai.google.dev/api/generate-content
/// Function calling: https://ai.google.dev/gemini-api/docs/function-calling
/// </summary>
internal static class GeminiApi
{
    /// <summary>
    /// Gemini speaks camelCase ("systemInstruction", "functionCall"), and nulls are
    /// omitted so an absent system prompt does not turn into <c>"systemInstruction": null</c>.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Builds the generateContent request body.
    /// Gemini uses a "contents" array with "user" and "model" roles (there is no "assistant"),
    /// and takes system instructions in a separate "systemInstruction" field.
    /// </summary>
    /// <param name="messages">Conversation history.</param>
    /// <param name="systemPrompt">Optional system instruction.</param>
    /// <param name="tools">
    /// Native tool definitions, or null for the plain text/streaming paths.
    /// When supplied, they are emitted as a single "tools" entry with functionDeclarations.
    /// </param>
    public static object BuildRequestBody(
        List<ChatMessageDto> messages,
        string? systemPrompt,
        IReadOnlyList<NativeToolDefinition>? tools = null)
    {
        var contents = messages
            .Where(m => m.Role != "System")
            .Select(m => new
            {
                role = m.Role == "Assistant" ? "model" : "user",
                parts = new[] { new { text = m.Content } }
            })
            .ToList();

        // A Dictionary keeps the optional parts optional — Gemini rejects a null
        // "systemInstruction" and an empty "tools" array, so absent means absent.
        var body = new Dictionary<string, object> { ["contents"] = contents };

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            body["systemInstruction"] = new { parts = new[] { new { text = systemPrompt } } };
        }

        if (tools is { Count: > 0 })
        {
            body["tools"] = new[] { new { functionDeclarations = BuildFunctionDeclarations(tools) } };
        }

        return body;
    }

    /// <summary>
    /// Converts the shared tool definitions into Gemini's functionDeclarations.
    /// The parameter schema is the shared OpenAPI schema — no per-provider re-derivation.
    /// </summary>
    public static List<object> BuildFunctionDeclarations(IReadOnlyList<NativeToolDefinition> tools)
    {
        return tools.Select(tool =>
        {
            var declaration = new Dictionary<string, object>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description
            };

            // A parameterless function must omit "parameters" entirely; Gemini rejects
            // a Schema of type OBJECT with no properties.
            if (tool.Parameters.Count > 0)
            {
                declaration["parameters"] = NativeToolSchema.BuildOpenApiSchema(tool);
            }

            return (object)declaration;
        }).ToList();
    }

    /// <summary>
    /// Sends the conversation to Gemini with native function declarations attached.
    ///
    /// Returns null when the call could not be completed — the caller then degrades to the
    /// text-based flow. When Gemini refuses the request outright (a 4xx that is not a rate
    /// limit, e.g. a model that has no function calling), <paramref name="disableNativeTools"/>
    /// is invoked so the provider stops paying for a doomed extra round trip on every message.
    /// </summary>
    public static async Task<NativeToolCallResult?> CompleteWithToolsAsync(
        HttpClient httpClient,
        string apiKey,
        string model,
        List<ChatMessageDto> messages,
        string? systemPrompt,
        List<NativeToolDefinition> tools,
        ILogger logger,
        Action disableNativeTools,
        CancellationToken ct)
    {
        var requestBody = BuildRequestBody(messages, systemPrompt, tools);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        logger.LogInformation(
            "Sending {Count} messages to Gemini with {ToolCount} native tools (model: {Model})",
            messages.Count, tools.Count, model);

        try
        {
            using var response = await httpClient.PostAsJsonAsync(url, requestBody, JsonOptions, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;

                // 4xx (except 429 "too many requests") means Gemini will keep refusing this
                // request shape — retrying native tools every message only doubles latency.
                if (statusCode is >= 400 and < 500 && statusCode != 429)
                {
                    logger.LogWarning(
                        "Gemini model {Model} rejected native tool calling ({Status}). " +
                        "Falling back to text-based tools for the rest of this provider's lifetime.",
                        model, statusCode);
                    disableNativeTools();
                }
                else
                {
                    logger.LogWarning(
                        "Gemini native tool call failed with {Status} — falling back for this message.",
                        statusCode);
                }

                return null;
            }

            return ParseToolResponse(responseText, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Gemini API error during tool calling: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads a generateContent response that may contain function calls.
    ///
    /// Gemini answers with a parts array that mixes text parts and functionCall parts,
    /// so both are collected; tool calls win when present, exactly like Claude and Ollama.
    /// </summary>
    public static NativeToolCallResult ParseToolResponse(string responseText, ILogger logger)
    {
        var response = JsonSerializer.Deserialize<Response>(responseText, JsonOptions);
        var parts = response?.Candidates?.FirstOrDefault()?.Content?.Parts ?? [];

        var result = new NativeToolCallResult();
        var text = new System.Text.StringBuilder();

        foreach (var part in parts)
        {
            if (part.FunctionCall is { } call && !string.IsNullOrEmpty(call.Name))
            {
                // The shared reader converts the arguments object identically for every
                // provider — including "a JSON null means the parameter did not arrive".
                var arguments = call.Args is { } args
                    ? ToolArgumentReader.ReadArguments(args)
                    : [];

                logger.LogInformation(
                    "Gemini native tool call: {ToolName}({Args})",
                    call.Name,
                    string.Join(", ", arguments.Select(kv => $"{kv.Key}={kv.Value}")));

                result.ToolCalls.Add(new NativeToolCall { ToolName = call.Name, Arguments = arguments });
            }
            else if (!string.IsNullOrEmpty(part.Text))
            {
                text.Append(part.Text);
            }
        }

        if (text.Length > 0)
        {
            result.TextContent = text.ToString();
        }

        return result;
    }

    /// <summary>
    /// First text part of a response, or null — the plain (non-tool) completion paths.
    /// </summary>
    public static string? FirstText(Response? response)
        => response?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

    // ─── Gemini response model (minimal, for deserialization) ─────────────

    public sealed class Response
    {
        public List<Candidate>? Candidates { get; set; }
    }

    public sealed class Candidate
    {
        public Content? Content { get; set; }
    }

    public sealed class Content
    {
        public List<Part>? Parts { get; set; }
    }

    /// <summary>
    /// One part of a model answer. Exactly one of Text / FunctionCall is set;
    /// plain completions only ever see Text.
    /// </summary>
    public sealed class Part
    {
        public string? Text { get; set; }
        public FunctionCall? FunctionCall { get; set; }
    }

    public sealed class FunctionCall
    {
        public string? Name { get; set; }

        /// <summary>
        /// Arguments as a raw JSON object — kept as JsonElement so ToolArgumentReader
        /// can apply the same conversion rules used by every other provider.
        /// </summary>
        public JsonElement? Args { get; set; }
    }
}
