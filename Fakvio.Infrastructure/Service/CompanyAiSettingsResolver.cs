using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Infrastructure.AiProviders;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Resolves the effective AI provider for the current request using a 3-tier chain:
///
///   Tier 1: Company-specific AI settings from CompanySystemSettings (master DB).
///           Used when the company has a non-empty API key for the requested provider.
///   Tier 2: System-wide AI settings from SystemConfiguration (master DB, SysAdmin UI).
///           Fallback when company settings are not configured.
///   Tier 3: appsettings.json (IAiProviderFactory singleton).
///           Fallback when neither company nor system DB settings are configured.
///
/// Ad-hoc providers are created on-the-fly for company/system-specific keys.
/// They are lightweight (just an HTTP client + API key) and scoped to the request.
///
/// Junior note: This service is SCOPED — one instance per HTTP request.
/// The ad-hoc providers it creates live only for the duration of the request.
/// </summary>
public class CompanyAiSettingsResolver : ICompanyAiSettingsResolver
{
    private readonly MasterDbContext _masterContext;
    private readonly IAiProviderFactory _globalFactory;
    private readonly IOptions<AiSettings> _globalSettings;
    private readonly ISystemConfigurationService _systemConfigService;
    private readonly ICredentialProtector _credentialProtector;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CompanyAiSettingsResolver> _logger;

    public CompanyAiSettingsResolver(
        MasterDbContext masterContext,
        IAiProviderFactory globalFactory,
        IOptions<AiSettings> globalSettings,
        ISystemConfigurationService systemConfigService,
        ICredentialProtector credentialProtector,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        ILogger<CompanyAiSettingsResolver> logger)
    {
        _masterContext = masterContext;
        _globalFactory = globalFactory;
        _globalSettings = globalSettings;
        _systemConfigService = systemConfigService;
        _credentialProtector = credentialProtector;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IAiProvider> ResolveProviderAsync(
        long? companyId, string? requestedProvider, CancellationToken ct = default)
    {
        // CompanyId is passed explicitly by the caller (ChatService, ChatController, etc.)
        // — no dependency on IHttpContextAccessor/ITenantResolver.
        _logger.LogInformation(
            "AI resolver: CompanyId={CompanyId}, RequestedProvider={Provider}",
            companyId, requestedProvider);

        if (companyId.HasValue)
        {
            var companySettings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CompanyId == companyId.Value, ct);

            if (companySettings != null)
            {
                // Decrypt API keys — they are stored encrypted at rest in the database.
                // Decrypt once here so all downstream code works with plaintext keys.
                // Migration-safe: legacy plaintext values are returned unchanged.
                companySettings.AiClaudeApiKey = _credentialProtector.Decrypt(companySettings.AiClaudeApiKey);
                companySettings.AiOpenAiApiKey = _credentialProtector.Decrypt(companySettings.AiOpenAiApiKey);
                companySettings.AiGeminiApiKey = _credentialProtector.Decrypt(companySettings.AiGeminiApiKey);

                // Determine which provider to use:
                // 1. User explicitly requested a provider → use that
                // 2. Company has a default provider set → use that
                // 3. Fall through to system default
                var effectiveProvider = !string.IsNullOrEmpty(requestedProvider)
                    ? requestedProvider
                    : !string.IsNullOrEmpty(companySettings.AiDefaultProvider)
                        ? companySettings.AiDefaultProvider
                        : null;

                _logger.LogInformation(
                    "AI resolver: CompanyId={CompanyId}, EffectiveProvider={Provider}, " +
                    "DefaultProvider={Default}, HasClaudeKey={HasClaude}, HasOpenAiKey={HasOpenAi}",
                    companyId.Value, effectiveProvider,
                    companySettings.AiDefaultProvider,
                    !string.IsNullOrEmpty(companySettings.AiClaudeApiKey),
                    !string.IsNullOrEmpty(companySettings.AiOpenAiApiKey));

                if (!string.IsNullOrEmpty(effectiveProvider))
                {
                    // Try to create a company-specific provider with custom API key.
                    var companyProvider = TryCreateCompanyProvider(effectiveProvider, companySettings);
                    if (companyProvider != null)
                    {
                        _logger.LogInformation(
                            "Using company AI provider {Provider} for CompanyId {CompanyId}",
                            effectiveProvider, companyId.Value);
                        return companyProvider;
                    }

                    _logger.LogWarning(
                        "AI resolver: failed to create company provider '{Provider}' for CompanyId {CompanyId} — " +
                        "API key may be missing for this provider. Falling through to system default.",
                        effectiveProvider, companyId.Value);
                }
                else
                {
                    _logger.LogWarning(
                        "AI resolver: CompanyId {CompanyId} has no default AI provider set " +
                        "and no provider was requested. Falling through to system default.",
                        companyId.Value);
                }
            }
            else
            {
                _logger.LogWarning(
                    "AI resolver: no CompanySystemSettings found for CompanyId {CompanyId}",
                    companyId.Value);
            }
        }
        else
        {
            _logger.LogWarning("AI resolver: CompanyId is null — " +
                "cannot check company-specific AI settings");
        }

        // ── Tier 2: System-wide AI settings from SystemConfiguration DB ──────
        // SysAdmin configures these via /system-settings page.
        _logger.LogInformation(
            "AI resolver: Tier 1 (company) didn't resolve. Trying system DB settings. " +
            "CompanyId={CompanyId}, RequestedProvider={RequestedProvider}",
            companyId, requestedProvider);

        try
        {
            var systemAi = await _systemConfigService.GetAiSettingsAsync(ct);
            var systemProvider = !string.IsNullOrEmpty(requestedProvider)
                ? requestedProvider
                : systemAi.DefaultProvider;

            if (!string.IsNullOrEmpty(systemProvider))
            {
                var provider = TryCreateSystemProvider(systemProvider, systemAi);
                if (provider != null)
                {
                    _logger.LogInformation("Using system DB AI provider {Provider}", systemProvider);
                    return provider;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI resolver: failed to load system AI settings from DB");
        }

        // ── Tier 3: appsettings.json (singleton factory) ────────────────────
        _logger.LogWarning("AI resolver: Tier 2 (system DB) didn't resolve. Trying appsettings.json fallback.");

        try
        {
            if (!string.IsNullOrEmpty(requestedProvider))
                return _globalFactory.GetProvider(requestedProvider);

            return _globalFactory.GetDefaultProvider();
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"No AI provider resolved. CompanyId={companyId?.ToString() ?? "NULL"}, " +
                $"RequestedProvider={requestedProvider ?? "NULL"}. " +
                "Tier 1 (company) → Tier 2 (system DB) → Tier 3 (appsettings.json) all failed. " +
                "Configure AI settings in SysAdmin System Settings or company settings.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAvailableProvidersAsync(long? companyId, CancellationToken ct = default)
    {
        // Start with Tier 3: appsettings.json providers
        var providers = new HashSet<string>(_globalFactory.AvailableProviders, StringComparer.OrdinalIgnoreCase);

        // Add Tier 2: system DB providers
        try
        {
            var systemAi = await _systemConfigService.GetAiSettingsAsync(ct);
            if (!string.IsNullOrEmpty(systemAi.ClaudeApiKey)) providers.Add("Claude");
            if (!string.IsNullOrEmpty(systemAi.OpenAiApiKey)) providers.Add("OpenAI");
            if (!string.IsNullOrEmpty(systemAi.GeminiApiKey)) providers.Add("Gemini");
            if (!string.IsNullOrEmpty(systemAi.OllamaBaseUrl)) providers.Add("Ollama");
        }
        catch { /* system config may not exist yet */ }

        // Add Tier 1: company-specific providers
        if (companyId.HasValue)
        {
            var companySettings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CompanyId == companyId.Value, ct);

            if (companySettings != null)
            {
                if (!string.IsNullOrEmpty(companySettings.AiClaudeApiKey)) providers.Add("Claude");
                if (!string.IsNullOrEmpty(companySettings.AiOpenAiApiKey)) providers.Add("OpenAI");
                if (!string.IsNullOrEmpty(companySettings.AiGeminiApiKey)) providers.Add("Gemini");
                if (!string.IsNullOrEmpty(companySettings.AiOllamaBaseUrl)) providers.Add("Ollama");
            }
        }

        return providers.ToList().AsReadOnly();
    }

    /// <summary>
    /// Tries to create an ad-hoc AI provider using system-wide DB settings (Tier 2).
    /// Same pattern as TryCreateCompanyProvider but reads from SystemAiSettingsInternal.
    /// Returns null if the system doesn't have an API key for the requested provider.
    /// </summary>
    private IAiProvider? TryCreateSystemProvider(string providerName, SystemAiSettingsInternal sys)
    {
        try
        {
            if (providerName.Equals("Claude", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sys.ClaudeApiKey))
                return new AdHocClaudeProvider(sys.ClaudeApiKey, sys.ClaudeModel ?? _globalSettings.Value.Claude.Model ?? "claude-sonnet-4-6", _loggerFactory.CreateLogger<ClaudeProvider>());

            if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sys.OpenAiApiKey))
                return new AdHocOpenAiProvider(sys.OpenAiApiKey, sys.OpenAiModel ?? _globalSettings.Value.OpenAI.Model ?? "gpt-4o", _loggerFactory.CreateLogger<OpenAiProvider>());

            if (providerName.Equals("Gemini", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sys.GeminiApiKey))
            {
                var httpClient = _httpClientFactory.CreateClient("AdHocGemini");
                return new AdHocGeminiProvider(httpClient, sys.GeminiApiKey, sys.GeminiModel ?? _globalSettings.Value.Gemini.Model ?? "gemini-2.0-flash", _loggerFactory.CreateLogger<GeminiProvider>());
            }

            if (providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(sys.OllamaBaseUrl))
            {
                var httpClient = _httpClientFactory.CreateClient("AdHocOllama");
                httpClient.BaseAddress = new Uri(sys.OllamaBaseUrl.TrimEnd('/'));
                return new AdHocOllamaProvider(httpClient, sys.OllamaModel ?? _globalSettings.Value.Ollama.Model ?? "gemma3:12b", _loggerFactory.CreateLogger<OllamaProvider>());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create system-level {Provider} provider", providerName);
        }
        return null;
    }

    /// <summary>
    /// Tries to create an ad-hoc AI provider using company-specific settings.
    /// Returns null if the company doesn't have an API key for the requested provider,
    /// allowing the caller to fall through to system defaults.
    ///
    /// The created provider is lightweight and lives only for this request scope.
    /// </summary>
    private IAiProvider? TryCreateCompanyProvider(
        string providerName,
        Domain.Entities.CompanySystemSettings settings)
    {
        try
        {
            // ── Claude ───────────────────────────────────────────────────────
            if (providerName.Equals("Claude", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(settings.AiClaudeApiKey))
            {
                var model = !string.IsNullOrEmpty(settings.AiClaudeModel)
                    ? settings.AiClaudeModel
                    : _globalSettings.Value.Claude.Model;

                if (string.IsNullOrEmpty(model))
                    model = "claude-sonnet-4-6";

                return new AdHocClaudeProvider(
                    settings.AiClaudeApiKey,
                    model,
                    _loggerFactory.CreateLogger<ClaudeProvider>());
            }

            // ── OpenAI ──────────────────────────────────────────────────────
            if (providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(settings.AiOpenAiApiKey))
            {
                var model = !string.IsNullOrEmpty(settings.AiOpenAiModel)
                    ? settings.AiOpenAiModel
                    : _globalSettings.Value.OpenAI.Model;

                if (string.IsNullOrEmpty(model))
                    model = "gpt-4o";

                return new AdHocOpenAiProvider(
                    settings.AiOpenAiApiKey,
                    model,
                    _loggerFactory.CreateLogger<OpenAiProvider>());
            }

            // ── Gemini ──────────────────────────────────────────────────────
            if (providerName.Equals("Gemini", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(settings.AiGeminiApiKey))
            {
                var model = !string.IsNullOrEmpty(settings.AiGeminiModel)
                    ? settings.AiGeminiModel
                    : _globalSettings.Value.Gemini.Model;

                if (string.IsNullOrEmpty(model))
                    model = "gemini-2.0-flash";

                // Create a fresh HttpClient via IHttpClientFactory (pooled, no BaseAddress needed).
                var httpClient = _httpClientFactory.CreateClient("AdHocGemini");

                return new AdHocGeminiProvider(
                    httpClient,
                    settings.AiGeminiApiKey,
                    model,
                    _loggerFactory.CreateLogger<GeminiProvider>());
            }

            // ── Ollama ──────────────────────────────────────────────────────
            if (providerName.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(settings.AiOllamaBaseUrl))
            {
                var model = !string.IsNullOrEmpty(settings.AiOllamaModel)
                    ? settings.AiOllamaModel
                    : _globalSettings.Value.Ollama.Model;

                if (string.IsNullOrEmpty(model))
                    model = "gemma3:12b";

                // Create HttpClient with the company's Ollama BaseAddress.
                var httpClient = _httpClientFactory.CreateClient("AdHocOllama");
                httpClient.BaseAddress = new Uri(settings.AiOllamaBaseUrl.TrimEnd('/'));

                return new AdHocOllamaProvider(
                    httpClient,
                    model,
                    _loggerFactory.CreateLogger<OllamaProvider>());
            }
        }
        catch (Exception ex)
        {
            // If ad-hoc provider creation fails (e.g., missing SDK assembly in test environment),
            // log the error and fall back to the global provider.
            _logger.LogWarning(ex,
                "Failed to create company-specific {Provider} provider, falling back to system default",
                providerName);
        }

        return null;
    }
}

/// <summary>
/// Lightweight Claude provider created on-the-fly with a company-specific API key.
/// Supports the same full feature set as the singleton ClaudeProvider (streaming + native tools).
///
/// Lives only for the duration of a single request — created by CompanyAiSettingsResolver
/// when the company has its own Claude API key.
/// </summary>
internal sealed class AdHocClaudeProvider : IAiProvider, IDisposable
{
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly ILogger _logger;

    public string ProviderName => "Claude";
    public bool SupportsNativeTools => true;

    public AdHocClaudeProvider(string apiKey, string model, ILogger logger)
    {
        _client = new AnthropicClient(apiKey);
        _model = model;
        _logger = logger;
    }

    public async Task<string> GetCompletionAsync(
        List<Contracts.Dto.Chat.ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var anthropicMessages = BuildMessages(messages);

        _logger.LogDebug("AdHoc Claude: sending {Count} messages to model {Model}", messages.Count, _model);

        var response = await _client.Messages.MessagesPostAsync(
            model: _model,
            messages: anthropicMessages,
            maxTokens: 4096,
            system: systemPrompt,
            cancellationToken: ct);

        return response.AsSimpleText();
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<Contracts.Dto.Chat.ChatMessageDto> messages,
        string? systemPrompt = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var anthropicMessages = BuildMessages(messages);

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
                yield return text;
        }
    }

    public async Task<NativeToolCallResult?> GetCompletionWithToolsAsync(
        List<Contracts.Dto.Chat.ChatMessageDto> messages,
        string? systemPrompt,
        List<NativeToolDefinition> tools,
        CancellationToken ct = default)
    {
        // Delegate to the same tool-calling logic as the singleton ClaudeProvider.
        // Build tool definitions and send them to the Claude API.
        var anthropicMessages = BuildMessages(messages);
        var anthropicTools = tools.Select(tool =>
        {
            var properties = new Dictionary<string, object>();
            foreach (var param in tool.Parameters)
            {
                var propDef = new Dictionary<string, object>
                {
                    ["type"] = param.Type,
                    ["description"] = param.Description
                };
                if (param.EnumValues is { Count: > 0 })
                    propDef["enum"] = param.EnumValues;
                properties[param.Name] = propDef;
            }

            var inputSchema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (tool.Required is { Count: > 0 })
                inputSchema["required"] = tool.Required;

            return new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = inputSchema
            };
        }).ToList();

        try
        {
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

            var result = new NativeToolCallResult();
            foreach (var block in response.Content)
            {
                if (block.IsToolUse)
                {
                    var toolUse = block.ToolUse!;
                    var args = new Dictionary<string, string>();
                    if (toolUse.Input != null)
                    {
                        try
                        {
                            var inputJson = toolUse.Input.ToString();
                            if (!string.IsNullOrEmpty(inputJson))
                            {
                                using var doc = System.Text.Json.JsonDocument.Parse(inputJson);
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                {
                                    args[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                                        ? prop.Value.GetString() ?? ""
                                        : prop.Value.GetRawText();
                                }
                            }
                        }
                        catch (System.Text.Json.JsonException) { }
                    }
                    result.ToolCalls.Add(new NativeToolCall { ToolName = toolUse.Name, Arguments = args });
                }
                else if (block.IsText)
                {
                    result.TextContent = (result.TextContent ?? "") + block.Text;
                }
            }

            return result.HasToolCalls ? result : new NativeToolCallResult { TextContent = response.AsSimpleText() };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AdHoc Claude API error during tool calling");
            return null;
        }
    }

    private static List<InputMessage> BuildMessages(List<Contracts.Dto.Chat.ChatMessageDto> messages)
    {
        var result = new List<InputMessage>();
        foreach (var msg in messages)
        {
            if (msg.Role == "System") continue;
            result.Add(msg.Role == "Assistant"
                ? msg.Content.AsAssistantMessage()
                : (InputMessage)msg.Content);
        }
        return result;
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Lightweight OpenAI provider created on-the-fly with a company-specific API key.
/// Supports streaming completions.
///
/// Lives only for the duration of a single request.
/// </summary>
internal sealed class AdHocOpenAiProvider : IAiProvider
{
    private readonly OpenAI.Chat.ChatClient _chatClient;
    private readonly ILogger _logger;

    public string ProviderName => "OpenAI";

    public AdHocOpenAiProvider(string apiKey, string model, ILogger logger)
    {
        _logger = logger;
        var client = new OpenAIClient(apiKey);
        _chatClient = client.GetChatClient(model);
    }

    public async Task<string> GetCompletionAsync(
        List<Contracts.Dto.Chat.ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var chatMessages = BuildMessages(messages, systemPrompt);

        _logger.LogDebug("AdHoc OpenAI: sending {Count} messages", messages.Count);

        var completion = await _chatClient.CompleteChatAsync(chatMessages, cancellationToken: ct);
        return completion.Value.Content?.FirstOrDefault()?.Text ?? string.Empty;
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<Contracts.Dto.Chat.ChatMessageDto> messages,
        string? systemPrompt = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var chatMessages = BuildMessages(messages, systemPrompt);

        _logger.LogDebug("AdHoc OpenAI: starting streaming");

        var stream = _chatClient.CompleteChatStreamingAsync(chatMessages, cancellationToken: ct);

        await foreach (var update in stream.WithCancellation(ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return part.Text;
            }
        }
    }

    private static List<OpenAI.Chat.ChatMessage> BuildMessages(
        List<ChatMessageDto> messages,
        string? systemPrompt)
    {
        var result = new List<OpenAI.Chat.ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
            result.Add(OpenAI.Chat.ChatMessage.CreateSystemMessage(systemPrompt));

        foreach (var msg in messages)
        {
            if (msg.Role == "System") continue;

            if (msg.Role == "Assistant")
                result.Add(OpenAI.Chat.ChatMessage.CreateAssistantMessage(msg.Content));
            else
                result.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(msg.Content));
        }

        return result;
    }
}

/// <summary>
/// Lightweight Gemini provider created on-the-fly with a company-specific API key.
/// Uses the Gemini REST API directly via HttpClient (same as the singleton GeminiProvider).
/// Supports both synchronous and streaming completions via SSE.
///
/// Lives only for the duration of a single request.
/// </summary>
internal sealed class AdHocGeminiProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ProviderName => "Gemini";

    public AdHocGeminiProvider(HttpClient httpClient, string apiKey, string model, ILogger logger)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _model = model;
        _logger = logger;
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

        _logger.LogDebug("AdHoc Gemini: sending {Count} messages to model {Model}", messages.Count, _model);

        var response = await _httpClient.PostAsJsonAsync(url, requestBody, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GeminiResponse>(JsonOptions, ct);
        return result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
    }

    /// <summary>
    /// Streams the response from Gemini using SSE (Server-Sent Events).
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, systemPrompt);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:streamGenerateContent?alt=sse&key={_apiKey}";

        _logger.LogDebug("AdHoc Gemini: starting streaming from model {Model}", _model);

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
                yield return text;
        }
    }

    /// <summary>
    /// Builds the Gemini API request body.
    /// Gemini uses "contents" array with "user" and "model" roles.
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

/// <summary>
/// Lightweight Ollama provider created on-the-fly with a company-specific base URL.
/// Uses the Ollama REST API via HttpClient (same as the singleton OllamaProvider).
/// Supports streaming (NDJSON) and native tool calling.
///
/// Lives only for the duration of a single request.
///
/// Junior note: Ollama runs locally — no API key needed, just a base URL.
/// Each company can point to a different Ollama server or use a different model.
/// </summary>
internal sealed class AdHocOllamaProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ProviderName => "Ollama";

    /// <summary>
    /// Optimistic: assume native tools are supported until proven otherwise.
    /// If the model returns a "does not support tools" error, this is set to false.
    /// </summary>
    private bool _supportsNativeTools = true;
    public bool SupportsNativeTools => _supportsNativeTools;

    public AdHocOllamaProvider(HttpClient httpClient, string model, ILogger logger)
    {
        _httpClient = httpClient;
        _model = model;
        _logger = logger;
    }

    /// <summary>
    /// Sends messages to Ollama and returns the complete response (stream: false).
    /// </summary>
    public async Task<string> GetCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, systemPrompt, stream: false);

        _logger.LogDebug("AdHoc Ollama: sending {Count} messages to model {Model}", messages.Count, _model);

        var response = await _httpClient.PostAsJsonAsync("/api/chat", requestBody, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, ct);
        return result?.Message?.Content ?? string.Empty;
    }

    /// <summary>
    /// Streams the response from Ollama via NDJSON (newline-delimited JSON).
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<ChatMessageDto> messages,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = BuildRequestBody(messages, systemPrompt, stream: true);

        _logger.LogDebug("AdHoc Ollama: starting streaming from model {Model}", _model);

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
            if (string.IsNullOrEmpty(line)) continue;

            var chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, JsonOptions);
            var text = chunk?.Message?.Content;

            if (!string.IsNullOrEmpty(text))
                yield return text;

            if (chunk?.Done == true)
                break;
        }
    }

    /// <summary>
    /// Native tool calling via Ollama's tools API parameter.
    /// Returns tool calls if the model decided to use a tool, or text if not.
    /// Returns null if the model doesn't support tools (disables native tools for subsequent calls).
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
            "AdHoc Ollama: sending {Count} messages with {ToolCount} native tools (model: {Model})",
            messages.Count, tools.Count, _model);

        var response = await _httpClient.PostAsJsonAsync("/api/chat", requestBody, JsonOptions, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        // Some Ollama models don't support tools — detect and disable.
        if (!response.IsSuccessStatusCode || responseText.Contains("does not support tools"))
        {
            _logger.LogWarning(
                "AdHoc Ollama model {Model} does not support native tools. Disabling.", _model);
            _supportsNativeTools = false;
            return null;
        }

        var result = JsonSerializer.Deserialize<OllamaChatResponse>(responseText, JsonOptions);

        if (result?.Message == null)
            return null;

        // Check if the model produced tool calls.
        if (result.Message.ToolCalls is { Count: > 0 })
        {
            var nativeResult = new NativeToolCallResult();
            foreach (var toolCall in result.Message.ToolCalls)
            {
                if (toolCall.Function == null) continue;

                var args = new Dictionary<string, string>();
                if (toolCall.Function.Arguments is { } argsElement)
                {
                    foreach (var prop in argsElement.EnumerateObject())
                    {
                        args[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? ""
                            : prop.Value.GetRawText();
                    }
                }

                nativeResult.ToolCalls.Add(new NativeToolCall
                {
                    ToolName = toolCall.Function.Name ?? "",
                    Arguments = args
                });
            }
            return nativeResult;
        }

        // Model chose to respond with text.
        return new NativeToolCallResult { TextContent = result.Message.Content };
    }

    // ─── Private helpers ──────────────────────────────────────────────────

    private object BuildRequestBody(List<ChatMessageDto> messages, string? systemPrompt, bool stream)
    {
        var ollamaMessages = BuildOllamaMessages(messages, systemPrompt);
        return new { model = _model, messages = ollamaMessages, stream };
    }

    private static List<object> BuildOllamaMessages(List<ChatMessageDto> messages, string? systemPrompt)
    {
        var ollamaMessages = new List<object>();

        if (!string.IsNullOrEmpty(systemPrompt))
            ollamaMessages.Add(new { role = "system", content = systemPrompt });

        foreach (var msg in messages)
        {
            var role = msg.Role.ToLowerInvariant() switch
            {
                "system" => "system",
                "assistant" => "assistant",
                _ => "user"
            };

            if (msg.Images is { Count: > 0 })
                ollamaMessages.Add(new { role, content = msg.Content, images = msg.Images });
            else
                ollamaMessages.Add(new { role, content = msg.Content });
        }

        return ollamaMessages;
    }

    private static List<object> BuildOllamaTools(List<NativeToolDefinition> tools)
    {
        return tools.Select(tool =>
        {
            var properties = new Dictionary<string, object>();
            foreach (var param in tool.Parameters)
            {
                var propDef = new Dictionary<string, object>
                {
                    ["type"] = param.Type,
                    ["description"] = param.Description
                };
                if (param.EnumValues is { Count: > 0 })
                    propDef["enum"] = param.EnumValues;
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

        [JsonPropertyName("tool_calls")]
        public List<OllamaToolCall>? ToolCalls { get; set; }
    }

    private class OllamaToolCall
    {
        public OllamaFunctionCall? Function { get; set; }
    }

    private class OllamaFunctionCall
    {
        public string? Name { get; set; }
        public JsonElement? Arguments { get; set; }
    }
}
