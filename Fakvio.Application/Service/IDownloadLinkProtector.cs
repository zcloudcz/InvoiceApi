using Fakvio.Contracts.Dto.DownloadLink;

namespace Fakvio.Application.Service;

/// <summary>
/// What a signed download token carries: WHO minted it (tenant + user) and WHAT it may download.
/// The tenant id lives inside the token because the anonymous download endpoint has no JWT.
/// </summary>
public sealed record DownloadTokenPayload(long CompanyId, long UserId, CreateDownloadLinkDto Parameters);

/// <summary>
/// Creates and validates the opaque, time-limited tokens used in <c>GET /api/download/{token}</c>.
/// Backed by ASP.NET Core Data Protection (authenticated encryption), so a token cannot be read,
/// forged or modified without the key ring, and it expires on its own.
/// </summary>
public interface IDownloadLinkProtector
{
    /// <summary>Protects the payload; <paramref name="expiresAt"/> is the UTC moment the token stops working.</summary>
    string Protect(DownloadTokenPayload payload, out DateTime expiresAt);

    /// <summary>Null on any failure - expired, tampered, minted for another purpose, or garbage. Never throws.</summary>
    DownloadTokenPayload? Unprotect(string token);
}
