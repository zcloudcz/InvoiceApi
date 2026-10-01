using System.Text.Json.Serialization;

namespace Fakvio.Contracts.Dto.OAuth;

/// <summary>Successful <c>POST /oauth/token</c> response body (RFC 6749 §5.1). Snake_case wire names — see ADR 0001 §4.2.</summary>
public class OAuthTokenResponseDto
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;
}

/// <summary>RFC 6749 §5.2 error response — carries only the standard error code, never internal detail.</summary>
public class OAuthErrorResponseDto
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
}
