using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Base class for API client services.
/// Provides common HTTP operations for consuming the Fakvio.
/// Supports impersonation — when SysAdmin selects a company, the X-Company-Id header is sent
/// so the API filters data for that company.
///
/// Error handling strategy:
/// - 401 Unauthorized → handled globally by UnauthorizedRedirectHandler (DelegatingHandler)
///   which redirects to /login. This method never sees 401 because the handler intercepts it.
/// - 403/404/500/etc → throws ApiException with the HTTP status code and extracted error message.
///   Callers (pages) can catch ApiException and show the actual error instead of "Not Found".
/// - Previously, all non-success responses silently returned null/default, causing every error
///   to display as "Nenalezeno". Now errors are properly propagated.
/// </summary>
public abstract class ApiClientBase
{
    protected readonly HttpClient _httpClient;
    protected readonly ILogger _logger;
    private readonly AuthenticationStateProvider? _authStateProvider;

    /// <summary>
    /// Optional remote-log forwarder. When set, all errors caught in this base class are
    /// also POSTed to /api/logs/client so they reach the server-side AppLog table.
    /// Set via property injection in <see cref="WithClientLogger"/> to keep existing
    /// constructors backwards-compatible (subclasses don't need to know about it).
    /// </summary>
    private IClientLogger? _clientLogger;

    protected ApiClientBase(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClient = httpClientFactory.CreateClient("InvoiceAPI");
        _logger = logger;
    }

    /// <summary>
    /// Constructor that also accepts AuthenticationStateProvider for impersonation support.
    /// Subclasses that need impersonation should use this constructor.
    /// </summary>
    protected ApiClientBase(IHttpClientFactory httpClientFactory, ILogger logger, AuthenticationStateProvider authStateProvider)
        : this(httpClientFactory, logger)
    {
        _authStateProvider = authStateProvider;
    }

    /// <summary>
    /// Wires up the remote logger so HTTP failures get forwarded to the server log.
    /// Called from a DI helper or the consuming page; if not called, behavior is unchanged
    /// and errors only land in the local logger (browser console).
    /// </summary>
    public ApiClientBase WithClientLogger(IClientLogger clientLogger)
    {
        _clientLogger = clientLogger;
        return this;
    }

    /// <summary>
    /// Sends an error to the server log (best-effort, never throws).
    /// Centralized so we don't sprinkle null checks all over the catch blocks.
    /// </summary>
    private void ForwardToServerLog(string level, string message, string? exception, string source)
    {
        if (_clientLogger == null)
            return; // Logger not wired — silently degrade to local-only logging.

        // Fire-and-forget: don't await. The IClientLogger contract guarantees no throw.
        _ = _clientLogger.LogAsync(level, message, source, exception);
    }

    /// <summary>
    /// Adds JWT Bearer token and impersonation header (X-Company-Id) to outgoing requests.
    /// Called before each API request to ensure proper authentication and company context.
    /// </summary>
    protected async Task AddAuthorizationHeaderAsync()
    {
        var customProvider = _authStateProvider as CustomAuthenticationStateProvider;
        if (customProvider != null)
        {
            var token = await customProvider.GetTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            // If SysAdmin is impersonating a company, send the company ID as a custom header
            var impersonatedCompanyId = await customProvider.GetImpersonatedCompanyIdAsync();
            if (impersonatedCompanyId.HasValue)
            {
                _httpClient.DefaultRequestHeaders.Remove("X-Company-Id");
                _httpClient.DefaultRequestHeaders.Add("X-Company-Id", impersonatedCompanyId.Value.ToString());
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Remove("X-Company-Id");
            }
        }
    }

    /// <summary>
    /// Extracts a user-friendly error message from an API error response.
    /// API returns JSON like {"message":"Client with ID 0 not found"} — we extract the "message" field.
    /// Falls back to the raw content if the response is not JSON or doesn't contain "message".
    /// Empty body is a distinct signal (unhandled exception in Production, proxy failure, etc.) —
    /// the caller appends the HTTP status code so the user sees something actionable.
    /// </summary>
    private static string ExtractErrorMessage(string errorContent)
    {
        if (string.IsNullOrWhiteSpace(errorContent))
            return string.Empty; // Caller substitutes a status-code-based fallback.

        try
        {
            using var doc = JsonDocument.Parse(errorContent);
            // Try "message" first (our standard API error format), then "title" (ASP.NET ProblemDetails)
            if (doc.RootElement.TryGetProperty("message", out var messageProp))
                return messageProp.GetString() ?? errorContent;
            if (doc.RootElement.TryGetProperty("title", out var titleProp))
                return titleProp.GetString() ?? errorContent;
        }
        catch (JsonException)
        {
            // Not JSON (e.g., the ASP.NET developer exception HTML page). Truncate so it fits in a snackbar.
            return errorContent.Length > 300 ? errorContent[..300] + "…" : errorContent;
        }

        return errorContent;
    }

    /// <summary>
    /// Centralized error response handler. Called by all HTTP methods when the API returns
    /// a non-success status code. Logs the error and throws ApiException with the status code
    /// and extracted error message.
    ///
    /// Note: 401 Unauthorized is already intercepted by UnauthorizedRedirectHandler
    /// at the HttpClient pipeline level, so this method typically handles 403, 404, 500 etc.
    /// </summary>
    private async Task HandleErrorResponseAsync(
        HttpResponseMessage response, string httpMethod, string endpoint)
    {
        var errorContent = await response.Content.ReadAsStringAsync();
        _logger.LogWarning("{HttpMethod} {Endpoint} failed with status {StatusCode}: {Error}",
            httpMethod, endpoint, response.StatusCode, errorContent);

        // Forward to server log so the failure is captured in AppLog even if the source server
        // didn't (e.g., 4xx originating in a different service, network 502 from a gateway,
        // or a server-side log that didn't fire because the request never reached it).
        // 5xx is logged as Error; 4xx as Warning to mirror server-side conventions.
        var serverLogLevel = (int)response.StatusCode >= 500 ? "Error" : "Warning";
        ForwardToServerLog(
            serverLogLevel,
            $"{httpMethod} {endpoint} failed: HTTP {(int)response.StatusCode} {response.StatusCode}",
            errorContent,
            "ApiClientBase");

        // 403 gets a fixed message; every other status extracts from the body and falls back
        // to "HTTP {code} {reason}" when the body is empty — which is the common symptom of
        // an unhandled 500 in Production (no ExceptionHandler middleware) or a proxy failure.
        var message = response.StatusCode == HttpStatusCode.Forbidden
            ? "Access denied. You don't have permission for this action."
            : ExtractErrorMessage(errorContent);

        if (string.IsNullOrEmpty(message))
        {
            message = $"HTTP {(int)response.StatusCode} {response.StatusCode} (empty response body)";
        }

        throw new ApiException(response.StatusCode, message, endpoint);
    }

    /// <summary>
    /// Logs a transport-level exception (network failure, deserialization error, timeout, etc.)
    /// to BOTH the local console logger AND the server-side AppLog (best-effort). Non-throwing.
    /// </summary>
    private void LogClientException(Exception ex, string httpMethod, string endpoint)
    {
        _logger.LogError(ex, "Error during {HttpMethod} {Endpoint}", httpMethod, endpoint);
        ForwardToServerLog(
            "Error",
            $"Exception during {httpMethod} {endpoint}: {ex.Message}",
            ex.ToString(),
            "ApiClientBase");
    }

    /// <summary>
    /// Performs GET request and deserializes response.
    /// Throws ApiException on non-success status codes (403, 404, 500, etc.).
    /// 401 is handled globally by UnauthorizedRedirectHandler → redirects to /login.
    /// </summary>
    protected async Task<T?> GetAsync<T>(string endpoint)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("GET {Endpoint}", endpoint);
            var response = await _httpClient.GetAsync(endpoint);

            if (response.IsSuccessStatusCode)
            {
                // 204 No Content — nothing to deserialize, return default (null for reference types).
                if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                    return default;

                return await response.Content.ReadFromJsonAsync<T>();
            }

            await HandleErrorResponseAsync(response, "GET", endpoint);
            return default; // Unreachable — HandleErrorResponseAsync always throws
        }
        catch (ApiException)
        {
            throw; // Re-throw API exceptions as-is
        }
        catch (Exception ex)
        {
            LogClientException(ex, "GET", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs POST request with body.
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<TResponse?> PostAsync<TRequest, TResponse>(string endpoint, TRequest data)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("POST {Endpoint}", endpoint);
            var response = await _httpClient.PostAsJsonAsync(endpoint, data);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TResponse>();
            }

            await HandleErrorResponseAsync(response, "POST", endpoint);
            return default; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "POST", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs PUT request with body.
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<TResponse?> PutAsync<TRequest, TResponse>(string endpoint, TRequest data)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("PUT {Endpoint}", endpoint);
            var response = await _httpClient.PutAsJsonAsync(endpoint, data);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TResponse>();
            }

            await HandleErrorResponseAsync(response, "PUT", endpoint);
            return default; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "PUT", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs DELETE request.
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<bool> DeleteAsync(string endpoint)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("DELETE {Endpoint}", endpoint);
            var response = await _httpClient.DeleteAsync(endpoint);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            await HandleErrorResponseAsync(response, "DELETE", endpoint);
            return false; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "DELETE", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs GET request and returns raw byte array (e.g., for PDF downloads).
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<byte[]?> GetBytesAsync(string endpoint)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("GET (bytes) {Endpoint}", endpoint);
            var response = await _httpClient.GetAsync(endpoint);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync();
            }

            await HandleErrorResponseAsync(response, "GET (bytes)", endpoint);
            return null; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "GET (bytes)", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs GET request and returns raw string content (e.g., rendered HTML).
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<string?> GetStringAsync(string endpoint)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("GET (string) {Endpoint}", endpoint);
            var response = await _httpClient.GetAsync(endpoint);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }

            await HandleErrorResponseAsync(response, "GET (string)", endpoint);
            return null; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "GET (string)", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs POST request without a request body and deserializes the response.
    /// Useful for action endpoints like /complete or /mark-paid that only need the URL.
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<TResponse?> PostWithoutBodyAsync<TResponse>(string endpoint)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("POST (no body) {Endpoint}", endpoint);
            var response = await _httpClient.PostAsync(endpoint, null);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<TResponse>();
            }

            await HandleErrorResponseAsync(response, "POST (no body)", endpoint);
            return default; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "POST (no body)", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs POST request with body and returns only a success boolean (no deserialization).
    /// Useful for fire-and-forget actions like sending emails where we only care about success/failure.
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<bool> PostBoolAsync<TRequest>(string endpoint, TRequest data)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("POST (bool) {Endpoint}", endpoint);
            var response = await _httpClient.PostAsJsonAsync(endpoint, data);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            await HandleErrorResponseAsync(response, "POST (bool)", endpoint);
            return false; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "POST (bool)", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs POST request without a body and returns only a success boolean.
    /// Useful for action endpoints like /provision or /migrate that only need the URL.
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<bool> PostWithoutBodyBoolAsync(string endpoint)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("POST (no body, bool) {Endpoint}", endpoint);
            var response = await _httpClient.PostAsync(endpoint, null);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            await HandleErrorResponseAsync(response, "POST (no body, bool)", endpoint);
            return false; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "POST (no body, bool)", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs PUT request without a body and returns only a success boolean.
    /// Useful for action endpoints like /activate or /deactivate that only need the URL.
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<bool> PutWithoutBodyBoolAsync(string endpoint)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("PUT (no body, bool) {Endpoint}", endpoint);
            var response = await _httpClient.PutAsync(endpoint, null);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            await HandleErrorResponseAsync(response, "PUT (no body, bool)", endpoint);
            return false; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "PUT (no body, bool)", endpoint);
            throw;
        }
    }

    /// <summary>
    /// Performs PUT request with body and returns only a success boolean (no deserialization).
    /// Useful for update endpoints like /change-password where the response body isn't needed.
    /// Throws ApiException on non-success status codes.
    /// </summary>
    protected async Task<bool> PutBoolAsync<TRequest>(string endpoint, TRequest data)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("PUT (bool) {Endpoint}", endpoint);
            var response = await _httpClient.PutAsJsonAsync(endpoint, data);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            await HandleErrorResponseAsync(response, "PUT (bool)", endpoint);
            return false; // Unreachable
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogClientException(ex, "PUT (bool)", endpoint);
            throw;
        }
    }
}
