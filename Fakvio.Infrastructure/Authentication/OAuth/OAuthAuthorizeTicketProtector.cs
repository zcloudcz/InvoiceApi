using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Authentication.OAuth;

/// <summary>
/// Everything the consent screen needs, carried between <c>GET /oauth/authorize</c> and the
/// consent decision without a database row (ADR 0001, docs/adr/0001-mcp-oauth21.md §4.2:
/// "Žádná tabulka pro rozpracované požadavky").
/// </summary>
public sealed record OAuthAuthorizeTicketPayload(
    string ClientId,
    string ClientName,
    string RedirectUri,
    string CodeChallenge,
    string Scope,
    string Resource,
    string? State);

/// <summary>
/// Protects/unprotects the consent ticket using ASP.NET Core Data Protection's time-limited
/// protector (ADR §4.2) — the same key ring already persisted to PostgreSQL for password/2FA
/// secrets (DEVGUIDE §2.7), so this survives restarts without a dedicated table.
/// </summary>
public interface IOAuthAuthorizeTicketProtector
{
    string Protect(OAuthAuthorizeTicketPayload payload);

    /// <summary>Null on any failure — expired, tampered, or garbage input. Never throws.</summary>
    OAuthAuthorizeTicketPayload? Unprotect(string ticket);
}

public class OAuthAuthorizeTicketProtector : IOAuthAuthorizeTicketProtector
{
    /// <summary>10 minutes (ADR §4.2 "Životnosti" — consent ticket).</summary>
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Data Protection purpose string. Changing this invalidates every ticket issued before
    /// the change — acceptable (a ticket is a 10-minute, in-flight artifact, never persisted
    /// past that), but do not change it casually.
    /// </summary>
    private const string Purpose = "Fakvio.OAuth.AuthorizeTicket.v1";

    private readonly ITimeLimitedDataProtector _protector;
    private readonly ILogger<OAuthAuthorizeTicketProtector> _logger;

    public OAuthAuthorizeTicketProtector(IDataProtectionProvider dataProtectionProvider, ILogger<OAuthAuthorizeTicketProtector> logger)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _logger = logger;
    }

    public string Protect(OAuthAuthorizeTicketPayload payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return _protector.Protect(json, TicketLifetime);
    }

    public OAuthAuthorizeTicketPayload? Unprotect(string ticket)
    {
        try
        {
            var json = _protector.Unprotect(ticket);
            return JsonSerializer.Deserialize<OAuthAuthorizeTicketPayload>(json);
        }
        catch (Exception ex)
        {
            // Expired, tampered, or simply garbage — all the same to the caller (fail closed,
            // no detail leaked), but worth a log line to distinguish "ticket expired because
            // the user took too long" from "someone is poking at this endpoint".
            _logger.LogInformation(ex, "OAuth consent ticket could not be unprotected");
            return null;
        }
    }
}
