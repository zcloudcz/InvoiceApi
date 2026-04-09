using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Verifies Google reCAPTCHA v3 tokens.
/// reCAPTCHA v3 is invisible — it scores user behavior (0.0 = bot, 1.0 = human)
/// without showing any challenge. The frontend gets a token from Google's JS API,
/// sends it to our backend, and we verify it with Google's siteverify endpoint.
///
/// When SecretKey is not configured (empty/null), verification is SKIPPED — this
/// allows local development without reCAPTCHA keys.
/// </summary>
public interface ICaptchaService
{
    /// <summary>
    /// Verifies a reCAPTCHA token with Google's API.
    /// Returns true if the token is valid and the score is above the threshold,
    /// or if reCAPTCHA is not configured (dev mode).
    /// </summary>
    Task<bool> VerifyAsync(string? token);
}

/// <inheritdoc />
public class CaptchaService : ICaptchaService
{
    private readonly HttpClient _httpClient;
    private readonly string _secretKey;
    private readonly ILogger<CaptchaService> _logger;

    // Minimum score to consider the user legitimate (0.0-1.0).
    // 0.5 is a reasonable default — adjust based on traffic patterns.
    private const double MinScore = 0.5;

    public CaptchaService(HttpClient httpClient, IConfiguration configuration, ILogger<CaptchaService> logger)
    {
        _httpClient = httpClient;
        _secretKey = configuration["Recaptcha:SecretKey"] ?? "";
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string? token)
    {
        // If no secret key is configured, skip verification (local dev mode).
        // This means you can develop locally without reCAPTCHA keys.
        if (string.IsNullOrEmpty(_secretKey))
        {
            _logger.LogWarning("reCAPTCHA secret key not configured — skipping verification");
            return true;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("Empty reCAPTCHA token received");
            return false;
        }

        try
        {
            // Call Google's siteverify API — server-to-server, secret key never exposed to client.
            var response = await _httpClient.PostAsync(
                $"https://www.google.com/recaptcha/api/siteverify?secret={_secretKey}&response={token}",
                null);

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

            _logger.LogInformation("reCAPTCHA passed with score: {Score}", result.Score);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "reCAPTCHA verification error");
            // On error, allow the request (fail open) — rate limiting still protects us
            return true;
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
        public string[]? ErrorCodes { get; set; }
    }
}
