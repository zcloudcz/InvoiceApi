using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using Npgsql;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Concrete implementation of <see cref="IImapPollService"/>. All IMAP heavy lifting,
/// alias routing, tenant scope creation, and the PostgreSQL advisory lock live here
/// so that the BackgroundService, the Functions TimerTrigger, and the SysAdmin HTTP
/// endpoint share exactly the same code path.
///
/// Concurrency: <see cref="AdvisoryLock"/> guarantees that only ONE cycle runs at a
/// time across the entire deployment (multi-replica, multi-host). Callers that lose
/// the lock get back <c>Skipped=true</c> and a benign reason, never an exception.
/// </summary>
public class ImapPollService : IImapPollService
{
    /// <summary>
    /// Stable 64-bit key for the PostgreSQL advisory lock. Derived from the ASCII
    /// bytes of "FAKVIOPY" so the value is deterministic across deploys and
    /// distinct from locks used by other parts of the app (or future features).
    /// </summary>
    private const long AdvisoryLockKey = 0x46414B56494F5059L; // "FAKVIOPY"

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ImapPollService> _logger;

    public ImapPollService(
        IServiceScopeFactory scopeFactory,
        NpgsqlDataSource dataSource,
        ILogger<ImapPollService> logger)
    {
        _scopeFactory = scopeFactory;
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ImapPollCycleResult> RunCycleAsync(CancellationToken ct = default)
    {
        // Each cycle uses its own DI scope so DbContexts don't bleed between invocations.
        using var scope = _scopeFactory.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ICredentialProtector>();

        var settings = await master.PaymentMatchingSystemSettings.FirstOrDefaultAsync(ct);
        if (settings == null || !settings.IsEnabled)
        {
            _logger.LogDebug("Payment matching disabled or unconfigured — skipping cycle");
            return new ImapPollCycleResult(true, "feature disabled", 0, settings?.PollIntervalMinutes ?? 30);
        }

        var password = protector.Decrypt(settings.ImapPasswordEncrypted);
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(settings.ImapHost) || string.IsNullOrEmpty(settings.ImapUsername))
        {
            _logger.LogWarning("IMAP settings incomplete — skipping cycle");
            return new ImapPollCycleResult(true, "settings incomplete", 0, settings.PollIntervalMinutes);
        }

        // Cross-process singleton lock. If another replica is mid-cycle, we exit early.
        // Use the DI-registered NpgsqlDataSource so Azure AD / Managed Identity tokens
        // are honoured (raw NpgsqlConnection from the connection string would fail in
        // production where the string has no embedded password).
        await using var lockSession = await AdvisoryLock.TryAcquireAsync(_dataSource, AdvisoryLockKey, ct);
        if (lockSession == null)
        {
            _logger.LogInformation(
                "ImapPoll cycle skipped: another instance holds the advisory lock (key={Key:X})",
                AdvisoryLockKey);
            return new ImapPollCycleResult(true, "lock held by other instance", 0, settings.PollIntervalMinutes);
        }

        var processedCount = 0;
        var runStatus = "Success";

        try
        {
            using var client = new ImapClient();
            var ssl = settings.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, ssl, ct);
            await client.AuthenticateAsync(settings.ImapUsername, password, ct);

            var inbox = await GetOrCreateFolderAsync(client, settings.ImapFolder, ct);
            var processedFolder = await GetOrCreateFolderAsync(client, settings.ProcessedFolder, ct);
            var unroutedFolder = await GetOrCreateFolderAsync(client, settings.UnroutedFolder, ct);

            await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

            // SearchQuery.NotSeen — only untouched messages.
            var uids = await inbox.SearchAsync(SearchQuery.NotSeen, ct);
            _logger.LogInformation("ImapPoll cycle: {Count} unread messages", uids.Count);

            foreach (var uid in uids)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var message = await inbox.GetMessageAsync(uid, ct);
                    var outcome = await HandleMessageAsync(message, uid, inbox, processedFolder, unroutedFolder, scope, ct);
                    if (outcome == HandleOutcome.Processed)
                    {
                        processedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process IMAP UID {Uid} — leaving it unread for retry", uid);
                }
            }

            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            runStatus = $"Failed: {ex.Message}";
            _logger.LogError(ex, "ImapPoll cycle failed");
        }

        // Persist worker status so the SysAdmin UI shows it.
        var tracked = await master.PaymentMatchingSystemSettings.FirstAsync(ct);
        tracked.LastRunAt = DateTime.UtcNow;
        tracked.LastRunStatus = runStatus;
        tracked.LastRunProcessedCount = processedCount;
        await master.SaveChangesAsync(ct);

        return new ImapPollCycleResult(false, null, processedCount, tracked.PollIntervalMinutes);
    }

    /// <summary>Outcome of processing one message — used to update counters.</summary>
    private enum HandleOutcome
    {
        Processed,
        Skipped,
        Unrouted,
    }

    /// <summary>
    /// Resolves the recipient alias to a tenant, opens the tenant scope, and invokes
    /// the processor. Moves the message on the IMAP server to the right folder.
    /// </summary>
    private async Task<HandleOutcome> HandleMessageAsync(
        MimeMessage message,
        UniqueId uid,
        IMailFolder inbox,
        IMailFolder processedFolder,
        IMailFolder unroutedFolder,
        IServiceScope outerScope,
        CancellationToken ct)
    {
        var master = outerScope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantFactory = outerScope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();

        // Pick the alias from the To: / Delivered-To headers.
        var toAddress = message.To.Mailboxes.FirstOrDefault()?.Address
            ?? message.Headers["Delivered-To"];
        var alias = ExtractLocalPart(toAddress);

        if (string.IsNullOrWhiteSpace(alias))
        {
            _logger.LogInformation("Message UID {Uid} has no routable To: header — moving to unrouted", uid);
            await inbox.MoveToAsync(uid, unroutedFolder, ct);
            return HandleOutcome.Unrouted;
        }

        var index = await master.MasterMailboxIndex
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.InboundAlias == alias && !i.IsAliasRetired, ct);

        if (index == null)
        {
            _logger.LogInformation("Unknown or retired alias '{Alias}' — moving UID {Uid} to unrouted", alias, uid);
            await inbox.MoveToAsync(uid, unroutedFolder, ct);
            return HandleOutcome.Unrouted;
        }

        // Resolve the tenant's schema and create a tenant-scoped DbContext.
        using var tenantScope = outerScope.ServiceProvider.CreateScope();
        var companyId = await ResolveCompanyIdFromSchemaAsync(master, index.TenantSchema, ct);
        var tenantCtx = (TenantDbContext)await tenantFactory.CreateContextForCompanyAsync(companyId, ct);

        var parser = tenantScope.ServiceProvider.GetRequiredService<IBankEmailParser>();
        var matcher = new PaymentMatchingService(
            tenantCtx,
            tenantScope.ServiceProvider.GetRequiredService<ILogger<PaymentMatchingService>>());
        var processor = new InboundEmailProcessor(
            tenantCtx,
            parser,
            matcher,
            tenantScope.ServiceProvider.GetRequiredService<ILogger<InboundEmailProcessor>>());

        var payload = new InboundEmailPayload(
            BankAccountMailboxId: index.TenantBankAccountMailboxId,
            MessageId: message.MessageId ?? string.Empty,
            ImapUid: uid.Id.ToString(),
            ServerReceivedAt: DateTime.UtcNow,
            FromAddress: message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
            FromDisplayName: message.From.Mailboxes.FirstOrDefault()?.Name,
            ToAddress: toAddress ?? string.Empty,
            Subject: message.Subject,
            EmailDate: message.Date != default ? message.Date.UtcDateTime : null,
            TextBody: message.TextBody,
            HtmlBody: message.HtmlBody);

        await processor.ProcessAsync(payload, companyId, ct);

        await inbox.MoveToAsync(uid, processedFolder, ct);
        return HandleOutcome.Processed;
    }

    // ─── Low-level helpers ─────────────────────────────────────────────────

    /// <summary>Opens an IMAP folder, creating it under the top-level namespace if it doesn't exist.</summary>
    private static async Task<IMailFolder> GetOrCreateFolderAsync(ImapClient client, string name, CancellationToken ct)
    {
        // "INBOX" is always the root inbox in IMAP — no need to create.
        if (string.Equals(name, "INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return client.Inbox;
        }

        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        try
        {
            return await personal.GetSubfolderAsync(name, ct);
        }
        catch (FolderNotFoundException)
        {
            return await personal.CreateAsync(name, isMessageFolder: true, ct);
        }
    }

    /// <summary>Extracts "alias" from "alias@domain" (case-insensitive). Returns empty on malformed input.</summary>
    internal static string ExtractLocalPart(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var at = address.IndexOf('@');
        return at <= 0 ? string.Empty : address[..at].ToLowerInvariant();
    }

    /// <summary>
    /// Looks up the CompanyId for a tenant schema by querying CompanySystemSettings.
    /// Cached per call — the map is small and changes rarely.
    /// </summary>
    private static async Task<long> ResolveCompanyIdFromSchemaAsync(MasterDbContext master, string schema, CancellationToken ct)
    {
        var row = await master.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.SchemaName == schema, ct)
            ?? throw new InvalidOperationException($"No CompanySystemSettings for schema '{schema}'.");
        return row.CompanyId;
    }
}
