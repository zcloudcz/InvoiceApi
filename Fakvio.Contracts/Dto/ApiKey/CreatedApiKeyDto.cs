namespace Fakvio.Contracts.Dto.ApiKey;

/// <summary>
/// Response of POST /api/api-key — the only place the raw key is ever returned.
/// It is not stored anywhere (the DB holds just a SHA-256 hash), so a user who
/// loses it must create a new key.
/// </summary>
public class CreatedApiKeyDto : ApiKeyDto
{
    /// <summary>
    /// The raw API key, e.g. "fak_live_xxxxxxxx…". Shown to the user exactly once.
    /// Never logged, never persisted.
    /// </summary>
    public string Key { get; set; } = string.Empty;
}
