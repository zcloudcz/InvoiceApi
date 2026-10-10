using System.Text.Json;
using Fakvio.Application.Service;
using Microsoft.AspNetCore.DataProtection;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Time-limited Data Protection tokens for signed download links - same pattern as
/// <c>OAuthAuthorizeTicketProtector</c>, with its own purpose string so a token minted for one
/// feature can never be accepted by another. The key ring is the one persisted to PostgreSQL
/// (see ServiceCollectionExtensions), so links survive restarts and work on every instance.
///
/// The token is deliberately NEVER logged anywhere - it is a bearer credential for tenant data.
/// </summary>
public class DownloadLinkProtector : IDownloadLinkProtector
{
    /// <summary>
    /// Data Protection purpose string. Changing it invalidates every outstanding link (they live
    /// 10 minutes, so that is harmless) - but do not change it casually.
    /// </summary>
    internal const string Purpose = "Fakvio.DownloadLink.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _lifetime;

    public DownloadLinkProtector(IDataProtectionProvider dataProtectionProvider, TimeSpan lifetime)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _lifetime = lifetime;
    }

    public string Protect(DownloadTokenPayload payload, out DateTime expiresAt)
    {
        expiresAt = DateTime.UtcNow + _lifetime;
        return _protector.Protect(JsonSerializer.Serialize(payload), _lifetime);
    }

    public DownloadTokenPayload? Unprotect(string token)
    {
        try
        {
            return JsonSerializer.Deserialize<DownloadTokenPayload>(_protector.Unprotect(token));
        }
        catch (Exception)
        {
            // Expired, tampered, wrong purpose or garbage all look the same to the caller (fail
            // closed, no detail leaked). Nothing is logged here: the input is attacker-controlled
            // and the token itself must never reach the logs.
            return null;
        }
    }
}
