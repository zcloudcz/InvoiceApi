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

    private static readonly JsonSerializerOptions JsonOptions = GeminiApi.JsonOptions;

    public string ProviderName => "Gemini";

    /// <summary>
    /// Gemini supports native function calling on every model we ship with (gemini-2.0-flash
    /// and newer). Starts optimistic and is switched off only when the API definitively
    /// refuses the tools — see <see cref="GeminiApi.CompleteWithToolsAsync"/>. Once off,
    /// ChatService uses the text-based tool protocol instead, so tools keep working either way.
    /// </summary>
    private bool _supportsNativeTools = true;
    public bool SupportsNativeTools => _supportsNativeTools;

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
        var requestBody = GeminiApi.BuildRequestBody(messages, systemPrompt);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        _logger.LogDebug("Sending {Count} messages to Gemini model {Model}", messages.Count, _model);

        var response = await _httpClient.PostAsJsonAsync(url, requestBody, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GeminiApi.Response>(JsonOptions, ct);
        return GeminiApi.FirstText(result) ?? string.Empty;
    }

    /// <summary>
    /// Sends the conversation with native function declarations to Gemini.
    /// The model answers either with functionCall parts or with plain text — see
    /// <see cref="GeminiApi"/> for the wire format, which is shared with the
    /// per-company ad-hoc Gemini provider.
    /// </summary>
    public Task<NativeToolCallResult?> GetCompletionWithToolsAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt,
        List<NativeToolDefinition> tools,
        CancellationToken ct = default)
        => GeminiApi.CompleteWithToolsAsync(
            _httpClient,
            _apiKey,
            _model,
            messages,
            systemPrompt,
            tools,
            _logger,
            () => _supportsNativeTools = false,
            ct);

    /// <summary>
    /// Streams the response from Gemini using SSE (Server-Sent Events).
    /// Gemini's streaming endpoint returns SSE with JSON data payloads.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = GeminiApi.BuildRequestBody(messages, systemPrompt);
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
            var chunk = JsonSerializer.Deserialize<GeminiApi.Response>(json, JsonOptions);
            var text = GeminiApi.FirstText(chunk);

            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }
}
