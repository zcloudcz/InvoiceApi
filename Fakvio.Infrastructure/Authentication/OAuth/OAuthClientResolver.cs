using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fakvio.Application.Service;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fakvio.Infrastructure.Authentication.OAuth;

/// <summary>
/// Fetches and validates Client ID Metadata Documents (CIMD), per ADR 0001
/// (docs/adr/0001-mcp-oauth21.md) §4.6.
///
/// Every step in <see cref="ResolveAsync"/> is a place T7 (SSRF) or T1 (phishing consent) could
/// be defeated, so read it in order:
/// 1. <paramref name="clientId"/> shape — https, has a path, no fragment/userinfo/query, port 443 only.
/// 2. Host allowlist (<c>McpOAuth:TrustedClientHosts</c>) — early access is closed to unknown hosts.
/// 3. Cache (positive AND negative) — also caps the fetch to one concurrent request per client_id.
/// 4. The fetch itself — <see cref="SsrfSafeConnect"/> via a dedicated named HttpClient, no
///    redirects, 5s timeout, 64 KB response cap.
/// 5. Document validation — client_id echoes the URL, redirect_uris present and well-formed,
///    and the client supports the public-client "none" token endpoint authentication method.
///
/// Anything that fails after step 2 is cached as a NEGATIVE result for
/// <see cref="NegativeCacheDuration"/> — repeatedly hammering a broken/hostile client_id must not
/// turn this resolver into a way to make the API re-fetch and re-parse attacker input on every
/// single authorize request.
/// </summary>
public class OAuthClientResolver : IOAuthClientResolver
{
    /// <summary>Registered name of the dedicated HttpClient (see DI registration).</summary>
    public const string HttpClientName = "OAuthClientMetadata";

    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinPositiveCache = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxPositiveCache = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly McpOAuthOptions _options;
    private readonly ILogger<OAuthClientResolver> _logger;

    /// <summary>
    /// One semaphore per client_id, so a burst of authorize requests for the same never-seen
    /// client collapses to a single outbound fetch instead of hammering the client's server
    /// (ADR §4.6: "max 1 souběžný fetch per client_id").
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchLocks = new();

    public OAuthClientResolver(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<McpOAuthOptions> options,
        ILogger<OAuthClientResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OAuthClientDocument?> ResolveAsync(string clientId, CancellationToken ct = default)
    {
        if (!TryValidateClientIdShape(clientId, out var uri, out var shapeError))
        {
            Reject(clientId, shapeError!);
            return null;
        }

        if (!IsTrustedHost(uri!.Host))
        {
            Reject(clientId, $"host '{uri.Host}' is not on the trusted client allowlist");
            return null;
        }

        var cacheKey = CacheKey(clientId);
        if (_cache.TryGetValue(cacheKey, out CacheEntry? cached))
            return cached!.Document; // Document is null for a cached negative result.

        var gate = _fetchLocks.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another caller may have populated the cache while we waited for the gate.
            if (_cache.TryGetValue(cacheKey, out cached))
                return cached!.Document;

            var (document, positiveCacheDuration, rejectReason) = await FetchAndValidateAsync(clientId, uri, ct);

            if (document is null)
            {
                Reject(clientId, rejectReason!);
                _cache.Set(cacheKey, new CacheEntry(null), NegativeCacheDuration);
                return null;
            }

            _cache.Set(cacheKey, new CacheEntry(document), positiveCacheDuration);
            return document;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// ADR §4.6: "client_id musí být https URL s cestou, bez fragmentu, userinfo a query; port jen 443."
    /// </summary>
    private static bool TryValidateClientIdShape(string clientId, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;

        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var parsed))
        {
            error = "client_id is not an absolute URL";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "client_id must use https";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "client_id must not contain userinfo";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "client_id must not contain a fragment";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.Query))
        {
            error = "client_id must not contain a query string";
            return false;
        }

        if (parsed.AbsolutePath is "" or "/")
        {
            error = "client_id must have a non-root path";
            return false;
        }

        if (!parsed.IsDefaultPort && parsed.Port != 443)
        {
            error = "client_id must use the default https port (443)";
            return false;
        }

        uri = parsed;
        return true;
    }

    private bool IsTrustedHost(string host)
        => _options.TrustedClientHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Performs the actual network fetch and validates the returned document. Isolated from
    /// <see cref="ResolveAsync"/> so the caching/locking logic above stays readable.
    /// </summary>
    private async Task<(OAuthClientDocument? Document, TimeSpan CacheDuration, string? RejectReason)>
        FetchAndValidateAsync(string clientId, Uri uri, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = FetchTimeout;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (SsrfBlockedException ex)
        {
            return (null, default, $"SSRF guard blocked the fetch: {ex.Message}");
        }
        // SocketsHttpHandler.ConnectCallback exceptions (including SsrfBlockedException thrown
        // from SsrfSafeConnect) are surfaced wrapped in an HttpRequestException — unwrap one
        // level so an SSRF block is logged as such instead of a generic "fetch failed".
        catch (HttpRequestException ex) when (ex.InnerException is SsrfBlockedException inner)
        {
            return (null, default, $"SSRF guard blocked the fetch: {inner.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, default, $"fetch failed: {ex.Message}");
        }

        using (response)
        {
            // AllowAutoRedirect=false on the handler means a redirect surfaces here as an
            // ordinary 3xx response instead of being followed — IsSuccessStatusCode rejects it
            // like any other non-2xx status (ADR §4.6: redirects are a hard error, not followed).
            if (!response.IsSuccessStatusCode)
                return (null, default, $"CIMD endpoint returned {(int)response.StatusCode}");

            // Codex review finding: enforce the JSON content type the CIMD spec requires,
            // rather than trying to parse whatever came back regardless — a host that answers
            // any GET with an HTML/error page would otherwise get exactly as far into this
            // resolver as one serving a genuine CIMD document.
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return (null, default, $"CIMD endpoint returned Content-Type '{mediaType ?? "(none)"}', expected application/json");

            byte[] body;
            try
            {
                body = await ReadWithLimitAsync(response, ct);
            }
            catch (InvalidOperationException ex)
            {
                return (null, default, ex.Message);
            }

            OAuthClientDocumentJson? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<OAuthClientDocumentJson>(body, JsonOptions);
            }
            catch (JsonException)
            {
                return (null, default, "CIMD document is not valid JSON");
            }

            if (parsed is null)
                return (null, default, "CIMD document is empty");

            var validationError = Validate(clientId, parsed);
            if (validationError is not null)
                return (null, default, validationError);

            var document = new OAuthClientDocument(parsed.ClientId!, parsed.ClientName ?? parsed.ClientId!, parsed.RedirectUris!);
            var cacheDuration = ResolveCacheDuration(response.Headers.CacheControl);

            return (document, cacheDuration, null);
        }
    }

    private static string? Validate(string requestedClientId, OAuthClientDocumentJson doc)
    {
        // Must equal the URL we fetched — otherwise a client could redirect trust from one
        // identity to another simply by publishing a document that claims to be someone else.
        if (!string.Equals(doc.ClientId, requestedClientId, StringComparison.Ordinal))
            return "CIMD document's client_id does not match the requested URL";

        if (string.IsNullOrWhiteSpace(doc.ClientName))
            return "CIMD document is missing client_name";

        if (doc.RedirectUris is null || doc.RedirectUris.Count == 0)
            return "CIMD document has no redirect_uris";

        foreach (var redirectUri in doc.RedirectUris)
        {
            if (!IsAcceptableRedirectUri(redirectUri))
                return $"CIMD document has an invalid redirect_uri '{redirectUri}'";
        }

        // Fakvio is a public client authorization server: it supports PKCE but does not
        // authenticate clients at the token endpoint. Prefer the current plural CIMD field,
        // which lists every method the client supports. Keep the older singular field as a
        // fallback for existing clients (such as Claude) that do not publish the plural field.
        if (doc.TokenEndpointAuthMethodsSupported.ValueKind != JsonValueKind.Undefined)
        {
            if (doc.TokenEndpointAuthMethodsSupported.ValueKind != JsonValueKind.Array)
                return "CIMD document has malformed token_endpoint_auth_methods_supported";

            var methods = doc.TokenEndpointAuthMethodsSupported.EnumerateArray().ToArray();
            if (methods.Length == 0 || methods.Any(method =>
                    method.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(method.GetString())))
                return "CIMD document has malformed token_endpoint_auth_methods_supported";

            if (!methods.Any(method => string.Equals(method.GetString(), "none", StringComparison.Ordinal)))
                return "CIMD document does not support the required public-client auth method 'none'";
        }
        else if (doc.TokenEndpointAuthMethod is not (null or "none"))
        {
            return $"CIMD document requests unsupported token_endpoint_auth_method '{doc.TokenEndpointAuthMethod}'";
        }

        return null;
    }

    /// <summary>https always allowed; http only for loopback (127.0.0.1, [::1], localhost) per RFC 8252 §7.3.</summary>
    private static bool IsAcceptableRedirectUri(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;

        return uri.Scheme == Uri.UriSchemeHttp
               && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static TimeSpan ResolveCacheDuration(CacheControlHeaderValue? cacheControl)
    {
        if (cacheControl?.MaxAge is { } maxAge)
        {
            if (maxAge < MinPositiveCache) return MinPositiveCache;
            if (maxAge > MaxPositiveCache) return MaxPositiveCache;
            return maxAge;
        }

        return MinPositiveCache;
    }

    private static async Task<byte[]> ReadWithLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidOperationException($"CIMD document exceeds the {MaxResponseBytes / 1024} KB size limit");

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private void Reject(string clientId, string reason)
        => _logger.LogWarning("OAuth.ClientRejected: client_id {ClientId} rejected — {Reason}", clientId, reason);

    private static string CacheKey(string clientId) => $"oauth-client:{clientId}";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record CacheEntry(OAuthClientDocument? Document);

    /// <summary>Wire shape of a CIMD document (draft-ietf-oauth-client-id-metadata-document-00).</summary>
    private sealed class OAuthClientDocumentJson
    {
        [JsonPropertyName("client_id")]
        public string? ClientId { get; set; }

        [JsonPropertyName("client_name")]
        public string? ClientName { get; set; }

        [JsonPropertyName("redirect_uris")]
        public List<string>? RedirectUris { get; set; }

        [JsonPropertyName("token_endpoint_auth_method")]
        public string? TokenEndpointAuthMethod { get; set; }

        [JsonPropertyName("token_endpoint_auth_methods_supported")]
        public JsonElement TokenEndpointAuthMethodsSupported { get; set; }
    }
}
