using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Verifies Google reCAPTCHA v3 tokens.
/// reCAPTCHA v3 is invisible — it scores user behavior (0.0 = bot, 1.0 = human)
/// without showing any challenge. The frontend gets a token from Google's JS API,
/// sends it to our backend, and we verify it with Google's siteverify endpoint.
///
/// The gate FAILS CLOSED (issue #200): anything that prevents a positive verification
/// — a transport error, an HTTP error from Google, a missing secret key — rejects the
/// request. This repo has no rate limiting, so an open gate means no protection at all.
///
/// Local development and tests, which have no reCAPTCHA keys, switch the gate off
/// EXPLICITLY with "Recaptcha:Enabled": false. That is deliberate: an environment that
/// merely forgot to configure the secret key must not silently end up unprotected.
/// </summary>
public interface ICaptchaService
{
    /// <summary>
    /// Verifies a reCAPTCHA token with Google's API.
    /// Returns true only if the token is valid, scores above the threshold, and was
    /// issued for <paramref name="expectedAction"/> on one of our own hosts.
    /// Returns true without any check when the gate is explicitly disabled in config.
    /// </summary>
    /// <param name="token">Token from grecaptcha.execute(), sent in the X-Captcha-Token header.</param>
    /// <param name="expectedAction">
    /// The action the calling endpoint expects — the same string the frontend passed to
    /// grecaptcha.execute() (for example "login", "register", "ares"). A token is bound
    /// to its action, so comparing it stops a token minted on one page from being
    /// replayed against a different endpoint.
    /// </param>
    Task<bool> VerifyAsync(string? token, string expectedAction);
}

/// <inheritdoc />
public class CaptchaService : ICaptchaService
{
    private readonly HttpClient _httpClient;
    private readonly string _secretKey;
    private readonly bool _enabled;
    private readonly string[] _allowedHostnames;
    private readonly ILogger<CaptchaService> _logger;

    // Minimum score to consider the user legitimate (0.0-1.0).
    // 0.5 is a reasonable default — adjust based on traffic patterns.
    private const double MinScore = 0.5;

    private const string SiteVerifyUrl = "https://www.google.com/recaptcha/api/siteverify";

    public CaptchaService(HttpClient httpClient, IConfiguration configuration, ILogger<CaptchaService> logger)
    {
        _httpClient = httpClient;
        _secretKey = configuration["Recaptcha:SecretKey"] ?? "";

        // Secure by default: the gate is on unless an environment turns it off on purpose.
        _enabled = configuration.GetValue("Recaptcha:Enabled", true);

        // Hosts our site key is used on. Empty = the hostname check is skipped; see VerifyAsync.
        _allowedHostnames = configuration.GetSection("Recaptcha:AllowedHostnames").Get<string[]>() ?? [];

        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string? token, string expectedAction)
    {
        // Explicit local-development / test escape hatch — no outbound call at all.
        if (!_enabled)
        {
            _logger.LogWarning("reCAPTCHA is disabled by configuration — skipping verification");
            return true;
        }

        // Enabled but unusable: reject rather than wave everyone through. A missing key
        // in production is a deployment mistake, and pretending the gate is there is worse
        // than a visible outage.
        if (string.IsNullOrEmpty(_secretKey))
        {
            _logger.LogError(
                "reCAPTCHA is enabled but Recaptcha:SecretKey is not configured — rejecting the request. " +
                "Set the key, or set Recaptcha:Enabled to false for environments that run without it.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Empty reCAPTCHA token received");
            return false;
        }

        try
        {
            // Call Google's siteverify API — server-to-server, secret key never exposed to client.
            //
            // The parameters go in a form body, which is the shape Google documents, and NOT
            // in the query string. The token arrives from the X-Captcha-Token header, so it is
            // fully attacker-controlled: concatenated into a URL, a plain '&' in it would append
            // a second "secret"/"response" pair. Whichever pair Google then honours, the caller
            // gets to choose the secret their own token is verified against — and the answer
            // would carry whatever action they asked for, defeating the check below.
            // FormUrlEncodedContent escapes both values, so the token can only ever be one value
            // of one field. Keeping the secret out of the URL also keeps it out of logs and proxies.
            using var form = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("secret", _secretKey),
                new KeyValuePair<string, string>("response", token)
            ]);

            var response = await _httpClient.PostAsync(SiteVerifyUrl, form);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("reCAPTCHA API returned {StatusCode}", response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<RecaptchaResponse>();

            if (result is null || !result.Success)
            {
                _logger.LogWarning("reCAPTCHA verification failed. Errors: [{Errors}]",
                    string.Join(", ", result?.ErrorCodes ?? Array.Empty<string>()));
                return false;
            }

            // reCAPTCHA v3 score: 1.0 = very likely human, 0.0 = very likely bot
            if (result.Score < MinScore)
            {
                _logger.LogWarning("reCAPTCHA score too low: {Score} (min: {MinScore})", result.Score, MinScore);
                return false;
            }

            // The token carries the action it was issued for. Without this comparison a
            // token obtained on the public registration form would also open the login
            // endpoint and the anonymous ARES proxy. Ordinal on purpose — the action is
            // an exact string agreed between the Blazor page and the endpoint.
            if (!string.Equals(result.Action, expectedAction, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "reCAPTCHA action mismatch: token was issued for {ActualAction}, endpoint expects {ExpectedAction}",
                    result.Action, expectedAction);
                return false;
            }

            // The token also carries the host it was issued on. When no host list is
            // configured the check is skipped — the reCAPTCHA admin console already binds
            // the site key to a domain list, so this is defense in depth, not the only
            // barrier. Everything above still applies, so this is NOT the old fail-open path.
            if (_allowedHostnames.Length > 0 &&
                !_allowedHostnames.Contains(result.Hostname, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning("reCAPTCHA hostname not allowed: {Hostname}", result.Hostname);
                return false;
            }

            _logger.LogInformation("reCAPTCHA passed with score: {Score}", result.Score);
            return true;
        }
        catch (Exception ex)
        {
            // Fail closed (issue #200): an unverifiable token is a rejected token.
            _logger.LogError(ex, "reCAPTCHA verification error — rejecting the request");
            return false;
        }
    }

    /// <summary>
    /// Matches the JSON response from Google's siteverify endpoint.
    /// </summary>
    private class RecaptchaResponse
    {
        public bool Success { get; set; }
        public double Score { get; set; }
        public string? Action { get; set; }
        public string? Hostname { get; set; }

        // Google spells this field "error-codes"; the hyphen means the default
        // case-insensitive name matching never binds it (the log stayed empty).
        [JsonPropertyName("error-codes")]
        public string[]? ErrorCodes { get; set; }
    }
}
