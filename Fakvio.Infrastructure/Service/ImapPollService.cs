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
///
/// Alias routing is delegated to <see cref="InboundAliasRouter"/> which walks a
/// multi-header fallback chain (Delivered-To → X-Original-To → Envelope-To →
/// Received-for → To → Cc) to handle catch-all / forwarded / relayed mail correctly.
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

    // InboundAliasRouter is stateless — one instance per ImapPollService is fine.
    private readonly InboundAliasRouter _aliasRouter;

    public ImapPollService(
        IServiceScopeFactory scopeFactory,
        NpgsqlDataSource dataSource,
        ILogger<ImapPollService> logger,
        ILogger<InboundAliasRouter> aliasRouterLogger)
    {
        _scopeFactory = scopeFactory;
        _dataSource = dataSource;
        _logger = logger;
        _aliasRouter = new InboundAliasRouter(aliasRouterLogger);
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

        // Reconcile: ensure every active BankAccountMailbox has a MasterMailboxIndex entry.
        // Fixes missing entries from pre-migration activations or failed RegisterInMasterIndexAsync calls.
        await ReconcileMasterMailboxIndexAsync(master, scope, ct);

        var processedCount = 0;
        var runStatus = "Success";

        try
        {
            using var client = new ImapClient();
            var ssl = settings.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

            _logger.LogDebug(
                "ImapPoll connecting to {Host}:{Port} SSL={Ssl} as {User}",
                settings.ImapHost, settings.ImapPort, settings.ImapUseSsl, settings.ImapUsername);

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
        catch (MailKit.Security.AuthenticationException ex)
        {
            // Log host/port/username to make credential mismatches easy to diagnose without
            // exposing the password. Common causes:
            //   1. Data Protection key ring was regenerated (app restart without persistent keys)
            //      → CredentialProtector.Decrypt returned raw ciphertext instead of the password.
            //   2. Wrong username / password set in SysAdmin settings.
            //   3. Provider requires an App Password (Google, Microsoft) but regular password used.
            runStatus = $"Failed: {ex.Message}";
            _logger.LogError(ex,
                "ImapPoll authentication failed — host={Host} port={Port} ssl={Ssl} user={User}. " +
                "If credentials are correct, check that Data Protection keys are persisted to the database " +
                "(PersistKeysToDbContext). A restart without persistent keys causes the stored password " +
                "to become unreadable.",
                settings.ImapHost, settings.ImapPort, settings.ImapUseSsl, settings.ImapUsername);
        }
        catch (Exception ex)
        {
            runStatus = $"Failed: {ex.Message}";
            _logger.LogError(ex,
                "ImapPoll cycle failed — host={Host} port={Port} user={User}",
                settings.ImapHost, settings.ImapPort, settings.ImapUsername);
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

        // Load InboundDomain from settings for the optional domain-part check.
        var settings = await master.PaymentMatchingSystemSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var inboundDomain = settings?.InboundDomain;

        // Delegate alias resolution to InboundAliasRouter — walks the multi-header
        // fallback chain (Delivered-To → X-Original-To → Envelope-To → Received-for
        // → To → Cc) so catch-all / forwarded / relayed mail is correctly routed.
        var resolution = await _aliasRouter.ResolveAsync(message, master, inboundDomain, ct);

        if (resolution is null)
        {
            _logger.LogInformation(
                "Message UID {Uid} could not be routed to any active alias — moving to unrouted", uid);
            await inbox.MoveToAsync(uid, unroutedFolder, ct);
            return HandleOutcome.Unrouted;
        }

        var index = resolution.MasterIndexEntry;

        // Resolve the tenant's schema and create a tenant-scoped DbContext.
        using var tenantScope = outerScope.ServiceProvider.CreateScope();
        var companyId = await ResolveCompanyIdFromSchemaAsync(master, index.TenantSchema, ct);
        var tenantCtx = (TenantDbContext)await tenantFactory.CreateContextForCompanyAsync(companyId, ct);

        var toAddress = message.To.Mailboxes.FirstOrDefault()?.Address
            ?? $"{resolution.MatchedAlias}@{inboundDomain ?? "fakvio.cz"}";

        var messageId = message.MessageId ?? string.Empty;
        var fromAddress = message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty;
        var fromName = message.From.Mailboxes.FirstOrDefault()?.Name;
        var emailDate = message.Date != default ? message.Date.UtcDateTime : (DateTime?)null;

        // Route to the correct processor based on alias type
        if (index.MailboxType == Domain.Enums.EMailboxType.Invoice && index.TenantInvoiceMailboxId.HasValue)
        {
            // Set schema on the DI-scoped TenantDbContext so ALL services resolved from
            // tenantScope (IReceivedInvoiceService, IClientService, etc.) use the correct
            // tenant schema. Without this, only the explicitly-created tenantCtx has schema
            // set, but DI-resolved services get their own TenantDbContext with Schema=null
            // → queries hit "public" schema → "relation does not exist".
            var scopedTenantCtx = tenantScope.ServiceProvider.GetRequiredService<TenantDbContext>();
            scopedTenantCtx.Schema = tenantCtx.Schema;

            var invoiceProcessor = new InvoiceEmailProcessor(
                scopedTenantCtx,
                tenantScope.ServiceProvider.GetRequiredService<IIsdocImportParser>(),
                tenantScope.ServiceProvider.GetRequiredService<IUblImportParser>(),
                tenantScope.ServiceProvider.GetRequiredService<IInvoiceEmailClassifier>(),
                tenantScope.ServiceProvider.GetRequiredService<IInvoiceImportService>(),
                tenantScope.ServiceProvider.GetRequiredService<IClientService>(),
                tenantScope.ServiceProvider.GetRequiredService<IReceivedInvoiceService>(),
                tenantScope.ServiceProvider.GetRequiredService<IInvoiceService>(),
                tenantScope.ServiceProvider.GetRequiredService<INotificationService>(),
                tenantScope.ServiceProvider.GetRequiredService<ILogger<InvoiceEmailProcessor>>(),
                tenantScope.ServiceProvider.GetService<IFileAttachmentService>());

            var invoicePayload = new InvoiceEmailPayload(
                InvoiceMailboxId: index.TenantInvoiceMailboxId.Value,
                MessageId: messageId,
                ImapUid: uid.Id.ToString(),
                ServerReceivedAt: DateTime.UtcNow,
                FromAddress: fromAddress,
                FromDisplayName: fromName,
                ToAddress: toAddress,
                Subject: message.Subject,
                EmailDate: emailDate,
                TextBody: message.TextBody,
                HtmlBody: message.HtmlBody);

            var attachments = ExtractAttachments(message);

            await invoiceProcessor.ProcessAsync(invoicePayload, attachments, companyId, ct);
        }
        else
        {
            // Payment email → existing InboundEmailProcessor path
            var parser = tenantScope.ServiceProvider.GetRequiredService<IBankEmailParser>();
            var matcher = new PaymentMatchingService(
                tenantCtx,
                tenantScope.ServiceProvider.GetRequiredService<INotificationService>(),
                tenantScope.ServiceProvider.GetRequiredService<ILogger<PaymentMatchingService>>(),
                tenantScope.ServiceProvider.GetService<IWebhookPublisher>());
            var processor = new InboundEmailProcessor(
                tenantCtx,
                parser,
                matcher,
                tenantScope.ServiceProvider.GetRequiredService<ILogger<InboundEmailProcessor>>());

            var payload = new InboundEmailPayload(
                BankAccountMailboxId: index.TenantBankAccountMailboxId,
                MessageId: messageId,
                ImapUid: uid.Id.ToString(),
                ServerReceivedAt: DateTime.UtcNow,
                FromAddress: fromAddress,
                FromDisplayName: fromName,
                ToAddress: toAddress,
                Subject: message.Subject,
                EmailDate: emailDate,
                TextBody: message.TextBody,
                HtmlBody: message.HtmlBody);

            await processor.ProcessAsync(payload, companyId, ct);
        }

        await inbox.MoveToAsync(uid, processedFolder, ct);
        return HandleOutcome.Processed;
    }

    // ─── Low-level helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Extracts PDF and ISDOC attachments from a MIME message for invoice processing.
    /// </summary>
    private static List<EmailAttachment> ExtractAttachments(MimeMessage message)
    {
        var attachments = new List<EmailAttachment>();

        foreach (var part in message.BodyParts)
        {
            if (part is not MimePart mimePart || mimePart.Content == null)
                continue;

            var fileName = mimePart.FileName;
            if (string.IsNullOrEmpty(fileName))
                continue;

            var isPdf = mimePart.ContentType.MimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                     || fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            var isIsdoc = fileName.EndsWith(".isdoc", StringComparison.OrdinalIgnoreCase)
                       || fileName.EndsWith(".isdocx", StringComparison.OrdinalIgnoreCase);

            if (!isPdf && !isIsdoc)
                continue;

            using var ms = new MemoryStream();
            mimePart.Content.DecodeTo(ms);
            attachments.Add(new EmailAttachment(
                FileName: fileName,
                ContentType: mimePart.ContentType.MimeType,
                Content: ms.ToArray()));
        }

        return attachments;
    }

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

    /// <summary>
    /// Scans all active tenants for BankAccountMailbox rows that are missing
    /// a corresponding MasterMailboxIndex entry and creates them.
    /// Uses raw SQL via the shared NpgsqlDataSource (Azure AD token-aware)
    /// to avoid creating per-tenant DbContexts that lack token auth.
    /// </summary>
    private async Task ReconcileMasterMailboxIndexAsync(
        MasterDbContext master,
        IServiceScope scope,
        CancellationToken ct)
    {
        var tenants = await master.CompanySystemSettings
            .AsNoTracking()
            .Where(s => s.IsProvisioned && s.IsActive)
            .ToListAsync(ct);

        var reconciled = 0;

        foreach (var tenant in tenants)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT ""Id"", ""InboundAlias""
                    FROM ""{tenant.SchemaName}"".""BankAccountMailbox""
                    WHERE ""IsActive"" = true";

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var mbxId = reader.GetInt64(0);
                    var alias = reader.GetString(1);

                    var exists = await master.MasterMailboxIndex
                        .AnyAsync(i => i.InboundAlias == alias && !i.IsAliasRetired, ct);

                    if (!exists)
                    {
                        master.MasterMailboxIndex.Add(new MasterMailboxIndex
                        {
                            InboundAlias = alias,
                            TenantSchema = tenant.SchemaName,
                            TenantBankAccountMailboxId = mbxId,
                            IsAliasRetired = false,
                        });
                        reconciled++;

                        _logger.LogWarning(
                            "Reconciled missing MasterMailboxIndex: alias='{Alias}' schema='{Schema}' mbxId={MbxId}",
                            alias, tenant.SchemaName, mbxId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to reconcile mailboxes for tenant '{Schema}' — skipping",
                    tenant.SchemaName);
            }
        }

        if (reconciled > 0)
        {
            await master.SaveChangesAsync(ct);
            _logger.LogInformation("MasterMailboxIndex reconciliation: {Count} entries added", reconciled);
        }
    }
}
