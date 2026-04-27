using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Default implementation of <see cref="IPaymentMatchingSystemSettingsService"/>.
///
/// The settings table always has 0 or 1 rows. If empty on first GET we materialize
/// a default row so the caller can start editing from sensible defaults.
/// </summary>
public class PaymentMatchingSystemSettingsService : IPaymentMatchingSystemSettingsService
{
    private readonly MasterDbContext _master;
    private readonly ICredentialProtector _protector;
    private readonly ILogger<PaymentMatchingSystemSettingsService> _logger;

    public PaymentMatchingSystemSettingsService(
        MasterDbContext master,
        ICredentialProtector protector,
        ILogger<PaymentMatchingSystemSettingsService> logger)
    {
        _master = master;
        _protector = protector;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PaymentMatchingSystemSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var row = await LoadOrCreateAsync(ct);
        return ToDtoWithoutPassword(row);
    }

    /// <inheritdoc />
    public async Task<PaymentMatchingSystemSettingsDto> UpdateAsync(
        PaymentMatchingSystemSettingsDto dto,
        CancellationToken ct = default)
    {
        ValidateInterval(dto);

        var row = await LoadOrCreateAsync(ct);

        row.IsEnabled = dto.IsEnabled;
        row.ImapHost = dto.ImapHost.Trim();
        row.ImapPort = dto.ImapPort;
        row.ImapUseSsl = dto.ImapUseSsl;
        row.ImapUsername = dto.ImapUsername.Trim();
        row.ImapFolder = string.IsNullOrWhiteSpace(dto.ImapFolder) ? "INBOX" : dto.ImapFolder.Trim();
        row.ProcessedFolder = string.IsNullOrWhiteSpace(dto.ProcessedFolder) ? "Processed" : dto.ProcessedFolder.Trim();
        row.UnroutedFolder = string.IsNullOrWhiteSpace(dto.UnroutedFolder) ? "Unrouted" : dto.UnroutedFolder.Trim();
        row.InboundDomain = dto.InboundDomain.Trim();
        row.PollIntervalMinutes = dto.PollIntervalMinutes;
        row.InboundEmailRetentionDays = dto.InboundEmailRetentionDays;

        // Password is only updated when client explicitly sends a non-null value.
        // Null = keep existing encrypted value (common during "save other field" UX).
        if (dto.ImapPassword != null)
        {
            row.ImapPasswordEncrypted = _protector.Encrypt(dto.ImapPassword) ?? string.Empty;
        }

        await _master.SaveChangesAsync(ct);

        _logger.LogInformation("Payment matching settings updated (IsEnabled={IsEnabled})", row.IsEnabled);

        return ToDtoWithoutPassword(row);
    }

    /// <inheritdoc />
    public async Task<TestImapConnectionResult> TestConnectionAsync(
        PaymentMatchingSystemSettingsDto dto,
        CancellationToken ct = default)
    {
        // If the test request doesn't include a password, load the currently stored one.
        string? effectivePassword = dto.ImapPassword;
        if (effectivePassword == null)
        {
            var stored = await _master.PaymentMatchingSystemSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            effectivePassword = stored != null ? _protector.Decrypt(stored.ImapPasswordEncrypted) : null;
        }

        if (string.IsNullOrEmpty(effectivePassword))
        {
            return new TestImapConnectionResult
            {
                Success = false,
                ErrorMessage = "Password is required for test connection."
            };
        }

        try
        {
            using var client = new ImapClient();

            // SslOnConnect for port 993 (implicit TLS) or StartTls for 143.
            var sslOptions = dto.ImapUseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(dto.ImapHost, dto.ImapPort, sslOptions, ct);
            await client.AuthenticateAsync(dto.ImapUsername, effectivePassword, ct);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            var count = inbox.Count;

            await client.DisconnectAsync(true, ct);

            _logger.LogInformation("IMAP test connection OK: host={Host} inboxCount={Count}", dto.ImapHost, count);

            return new TestImapConnectionResult
            {
                Success = true,
                MessageCount = count,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IMAP test connection failed: host={Host}", dto.ImapHost);
            return new TestImapConnectionResult
            {
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private async Task<PaymentMatchingSystemSettings> LoadOrCreateAsync(CancellationToken ct)
    {
        var row = await _master.PaymentMatchingSystemSettings.FirstOrDefaultAsync(ct);
        if (row != null) return row;

        row = new PaymentMatchingSystemSettings
        {
            IsEnabled = false,
            ImapHost = string.Empty,
            ImapPort = 993,
            ImapUseSsl = true,
            ImapUsername = string.Empty,
            ImapPasswordEncrypted = string.Empty,
            ImapFolder = "INBOX",
            ProcessedFolder = "Processed",
            UnroutedFolder = "Unrouted",
            InboundDomain = "fakvio.cz",
            PollIntervalMinutes = 30,
            InboundEmailRetentionDays = 1825,
        };
        _master.PaymentMatchingSystemSettings.Add(row);
        await _master.SaveChangesAsync(ct);
        return row;
    }

    private static PaymentMatchingSystemSettingsDto ToDtoWithoutPassword(PaymentMatchingSystemSettings row) => new()
    {
        IsEnabled = row.IsEnabled,
        ImapHost = row.ImapHost,
        ImapPort = row.ImapPort,
        ImapUseSsl = row.ImapUseSsl,
        ImapUsername = row.ImapUsername,
        ImapPassword = null, // never return password to client
        ImapFolder = row.ImapFolder,
        ProcessedFolder = row.ProcessedFolder,
        UnroutedFolder = row.UnroutedFolder,
        InboundDomain = row.InboundDomain,
        PollIntervalMinutes = row.PollIntervalMinutes,
        InboundEmailRetentionDays = row.InboundEmailRetentionDays,
        LastRunAt = row.LastRunAt,
        LastRunStatus = row.LastRunStatus,
        LastRunProcessedCount = row.LastRunProcessedCount,
    };

    /// <summary>Validates numeric ranges called out in PLATBY-ZADANI.md §4.5.</summary>
    private static void ValidateInterval(PaymentMatchingSystemSettingsDto dto)
    {
        if (dto.PollIntervalMinutes is < 5 or > 1440)
            throw new ArgumentOutOfRangeException(
                nameof(dto.PollIntervalMinutes),
                "PollIntervalMinutes must be between 5 and 1440 (24 hours).");

        if (dto.InboundEmailRetentionDays is < 30 or > 36500)
            throw new ArgumentOutOfRangeException(
                nameof(dto.InboundEmailRetentionDays),
                "InboundEmailRetentionDays must be between 30 and 36500.");
    }
}
