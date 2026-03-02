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

    public OllamaProvider(
        HttpClient httpClient,
        IOptions<AiSettings> settings,
        ILogger<OllamaProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        var config = settings.Value.Ollama;
        _httpClient.BaseAddress = new Uri(config.BaseUrl.TrimEnd('/'));
        _model = string.IsNullOrEmpty(config.Model) ? "llama3.2" : config.Model;
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
    /// Builds the Ollama chat API request body.
    /// Ollama uses "system", "user", "assistant" roles in the messages array.
    /// </summary>
    private object BuildRequestBody(List<ChatMessageDto> messages, string? systemPrompt, bool stream)
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

            ollamaMessages.Add(new { role, content = msg.Content });
        }

        return new
        {
            model = _model,
            messages = ollamaMessages,
            stream
        };
    }

    // ─── Ollama response models (minimal) ────────────────────────────────

    private class OllamaChatResponse
    {
        public OllamaMessage? Message { get; set; }
        public bool Done { get; set; }
    }

    private class OllamaMessage
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }
}
