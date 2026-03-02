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
/// AI provider implementation for Google Gemini.
/// Uses the Gemini REST API directly via HttpClient (no Google SDK needed).
///
/// API docs: https://ai.google.dev/api/generate-content
/// Streaming endpoint: streamGenerateContent with ?alt=sse
/// </summary>
public class GeminiProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<GeminiProvider> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ProviderName => "Gemini";

    public GeminiProvider(
        HttpClient httpClient,
        IOptions<AiSettings> settings,
        ILogger<GeminiProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        var config = settings.Value.Gemini;
        _apiKey = config.ApiKey;
        _model = string.IsNullOrEmpty(config.Model) ? "gemini-2.0-flash" : config.Model;
    }

    /// <summary>
    /// Sends messages to Gemini and returns the complete response.
    /// </summary>
    public async Task<string> GetCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, systemPrompt);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        _logger.LogDebug("Sending {Count} messages to Gemini model {Model}", messages.Count, _model);

        var response = await _httpClient.PostAsJsonAsync(url, requestBody, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(JsonOptions, ct);
        return result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
    }

    /// <summary>
    /// Streams the response from Gemini using SSE (Server-Sent Events).
    /// Gemini's streaming endpoint returns SSE with JSON data payloads.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, systemPrompt);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:streamGenerateContent?alt=sse&key={_apiKey}";

        _logger.LogDebug("Starting streaming from Gemini model {Model}", _model);

        var requestContent = new StringContent(
            JsonSerializer.Serialize(requestBody, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = requestContent };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Parse SSE: lines starting with "data: " contain JSON payloads.
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);

            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var json = line["data: ".Length..];
            var chunk = JsonSerializer.Deserialize<GeminiResponse>(json, JsonOptions);
            var text = chunk?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// Builds the Gemini API request body.
    /// Gemini uses "contents" array with "user" and "model" roles (no "assistant" role).
    /// System instructions go to a separate "systemInstruction" field.
    /// </summary>
    private static object BuildRequestBody(List<ChatMessageDto> messages, string? systemPrompt)
    {
        var contents = messages
            .Where(m => m.Role != "System")
            .Select(m => new
            {
                role = m.Role == "Assistant" ? "model" : "user",
                parts = new[] { new { text = m.Content } }
            })
            .ToList();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            return new
            {
                contents,
                systemInstruction = new
                {
                    parts = new[] { new { text = systemPrompt } }
                }
            };
        }

        return new { contents };
    }

    // ─── Gemini response model (minimal, for deserialization) ─────────────

    private class GeminiResponse
    {
        public List<GeminiCandidate>? Candidates { get; set; }
    }

    private class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
    }

    private class GeminiContent
    {
        public List<GeminiPart>? Parts { get; set; }
    }

    private class GeminiPart
    {
        public string? Text { get; set; }
    }
}
