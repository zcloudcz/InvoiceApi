using System.Text.Json.Serialization;

namespace Fakvio.Contracts.Dto.OAuth;

/// <summary>
/// RFC 8414 Authorization Server Metadata, served at
/// <c>/.well-known/oauth-authorization-server</c> per ADR 0001 (docs/adr/0001-mcp-oauth21.md) §4.1.
/// Property names are the exact wire names the spec (and every OAuth client) expects —
/// snake_case, not the app's usual camelCase convention — hence the explicit
/// <see cref="JsonPropertyNameAttribute"/> on every property.
/// </summary>
public class OAuthAuthorizationServerMetadataDto
{
    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = string.Empty;

    [JsonPropertyName("authorization_endpoint")]
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("token_endpoint")]
    public string TokenEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("revocation_endpoint")]
    public string RevocationEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("response_types_supported")]
    public string[] ResponseTypesSupported { get; set; } = ["code"];

    [JsonPropertyName("grant_types_supported")]
    public string[] GrantTypesSupported { get; set; } = ["authorization_code", "refresh_token"];

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public string[] TokenEndpointAuthMethodsSupported { get; set; } = ["none"];

    [JsonPropertyName("revocation_endpoint_auth_methods_supported")]
    public string[] RevocationEndpointAuthMethodsSupported { get; set; } = ["none"];

    [JsonPropertyName("code_challenge_methods_supported")]
    public string[] CodeChallengeMethodsSupported { get; set; } = ["S256"];

    [JsonPropertyName("scopes_supported")]
    public string[] ScopesSupported { get; set; } = ["read", "write"];

    [JsonPropertyName("client_id_metadata_document_supported")]
    public bool ClientIdMetadataDocumentSupported { get; set; } = true;

    [JsonPropertyName("authorization_response_iss_parameter_supported")]
    public bool AuthorizationResponseIssParameterSupported { get; set; } = true;
}
