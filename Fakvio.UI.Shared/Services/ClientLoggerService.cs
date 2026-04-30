using System.Net.Http.Json;
using Fakvio.Contracts.Dto.AppLog;
using Microsoft.AspNetCore.Components;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Forwards UI-side errors to the server log (POST /api/logs/client → DatabaseLogger → AppLog table).
/// Without this, anything caught in the WASM client would only show in the browser console.
///
/// Design rules:
/// - Fire-and-forget: forwarding never blocks the call site and never throws.
///   A failure to log must NEVER break the user-visible flow that triggered it.
/// - No recursion: if the forward call itself fails (e.g., the server is down), we swallow it.
///   We do not call ourselves to "log the log failure" — that would be an infinite loop.
/// - Bypasses ApiClientBase to avoid circular logging (ApiClientBase posts errors here).
/// </summary>
public interface IClientLogger
{
    Task LogAsync(string level, string message, string? source = null, string? exception = null, string? correlationId = null);
    Task LogErrorAsync(Exception ex, string? source = null);
    Task LogWarningAsync(string message, string? source = null);
    Task LogInfoAsync(string message, string? source = null);
}

public class ClientLoggerService : IClientLogger
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NavigationManager _navigation;
    private readonly ILogger<ClientLoggerService> _localLogger;

    public ClientLoggerService(
        IHttpClientFactory httpClientFactory,
        NavigationManager navigation,
        ILogger<ClientLoggerService> localLogger)
    {
        _httpClientFactory = httpClientFactory;
        _navigation = navigation;
        _localLogger = localLogger;
    }

    public Task LogErrorAsync(Exception ex, string? source = null) =>
        LogAsync("Error", ex.Message, source, ex.ToString());

    public Task LogWarningAsync(string message, string? source = null) =>
        LogAsync("Warning", message, source);

    public Task LogInfoAsync(string message, string? source = null) =>
        LogAsync("Information", message, source);

    public async Task LogAsync(
        string level,
        string message,
        string? source = null,
        string? exception = null,
        string? correlationId = null)
    {
        // Mirror the entry to the local console first so it always shows up during development,
        // even if the network call below fails.
        _localLogger.Log(
            level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? LogLevel.Error :
            level.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? LogLevel.Warning :
            LogLevel.Information,
            "{Source}: {Message}", source ?? "UI", message);

        var dto = new ClientLogDto
        {
            Level = level,
            Message = message,
            Source = source,
            Exception = exception,
            Url = _navigation.Uri,
            CorrelationId = correlationId
        };

        try
        {
            // Use a dedicated HttpClient (named "InvoiceAPI") but bypass any pipeline that would
            // re-route errors back through us — we just POST and forget.
            var client = _httpClientFactory.CreateClient("InvoiceAPI");
            using var response = await client.PostAsJsonAsync("/api/logs/client", dto);
            // Discard the response — even non-success is intentionally ignored. We do not retry,
            // we do not surface the failure to the user, and we never throw from a logger.
        }
        catch
        {
            // Swallow ALL exceptions. A logger that throws is worse than a logger that drops messages.
        }
    }
}
