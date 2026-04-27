using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Concrete implementation of <see cref="IBankAccountMailboxService"/>.
///
/// This service touches BOTH the tenant schema (BankAccountMailbox rows) and the
/// master schema (MasterMailboxIndex rows). That means we cannot use a single
/// transaction — we rely on eventual consistency:
///   1. Save the tenant row first; it is the canonical source.
///   2. Save the master index row second; on failure, the tenant row still exists
///      and a retry or a background consistency check can rebuild the index.
///
/// Junior note: Cross-schema transactions in PostgreSQL ARE possible, but EF Core
/// uses separate DbContext instances per schema — joining them into a single
/// TransactionScope is more trouble than it saves. The risk of a tenant row
/// without a corresponding master index row is handled by a health check (not yet implemented).
/// </summary>
public class BankAccountMailboxService : IBankAccountMailboxService
{
    private readonly TenantDbContext _tenant;
    private readonly MasterDbContext _master;
    private readonly IAliasGenerator _aliasGenerator;
    private readonly ITenantResolver _tenantResolver;
    private readonly ITenantDbContextFactory _tenantFactory;
    private readonly ILogger<BankAccountMailboxService> _logger;

    public BankAccountMailboxService(
        TenantDbContext tenant,
        MasterDbContext master,
        IAliasGenerator aliasGenerator,
        ITenantResolver tenantResolver,
        ITenantDbContextFactory tenantFactory,
        ILogger<BankAccountMailboxService> logger)
    {
        _tenant = tenant;
        _master = master;
        _aliasGenerator = aliasGenerator;
        _tenantResolver = tenantResolver;
        _tenantFactory = tenantFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BankAccountMailboxDto?> GetAsync(long bankAccountId, CancellationToken ct = default)
    {
        var mailbox = await _tenant.BankAccountMailbox
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.BankAccountId == bankAccountId, ct);

        if (mailbox == null) return null;

        return await BuildDtoAsync(mailbox, ct);
    }

    /// <inheritdoc />
    public async Task<BankAccountMailboxDto> ActivateAsync(long bankAccountId, CancellationToken ct = default)
    {
        // Confirm the bank account exists in this tenant before creating a mailbox.
        var account = await _tenant.BankAccount
            .FirstOrDefaultAsync(a => a.Id == bankAccountId, ct)
            ?? throw new InvalidOperationException($"BankAccount {bankAccountId} not found in current tenant.");

        var mailbox = await _tenant.BankAccountMailbox
            .FirstOrDefaultAsync(m => m.BankAccountId == bankAccountId, ct);

        var now = DateTime.UtcNow;

        if (mailbox == null)
        {
            // First-time activation — generate alias, insert tenant row + master index row.
            var alias = await GenerateUniqueAliasAsync(ct);

            mailbox = new BankAccountMailbox
            {
                BankAccountId = bankAccountId,
                InboundAlias = alias,
                IsActive = true,
                ActiveFrom = now,
            };
            _tenant.BankAccountMailbox.Add(mailbox);
            await _tenant.SaveChangesAsync(ct);

            await RegisterInMasterIndexAsync(alias, mailbox.Id, ct);

            _logger.LogInformation(
                "Mailbox activated (new): BankAccount={BankAccountId} Alias={Alias}",
                bankAccountId, alias);
        }
        else
        {
            // Reactivation — same alias, new ActiveFrom window.
            mailbox.IsActive = true;
            mailbox.ActiveFrom = now;
            mailbox.DeactivatedAt = null;
            await _tenant.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Mailbox activated (reused): BankAccount={BankAccountId} Alias={Alias} ActiveFrom={ActiveFrom}",
                bankAccountId, mailbox.InboundAlias, now);
        }

        return await BuildDtoAsync(mailbox, ct);
    }

    /// <inheritdoc />
    public async Task<BankAccountMailboxDto> DeactivateAsync(long bankAccountId, CancellationToken ct = default)
    {
        var mailbox = await _tenant.BankAccountMailbox
            .FirstOrDefaultAsync(m => m.BankAccountId == bankAccountId, ct)
            ?? throw new InvalidOperationException(
                $"No mailbox exists for BankAccount {bankAccountId}. Nothing to deactivate.");

        if (mailbox.IsActive)
        {
            mailbox.IsActive = false;
            mailbox.DeactivatedAt = DateTime.UtcNow;
            await _tenant.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Mailbox deactivated: BankAccount={BankAccountId} Alias={Alias}",
                bankAccountId, mailbox.InboundAlias);
        }

        return await BuildDtoAsync(mailbox, ct);
    }

    /// <inheritdoc />
    public async Task<BankAccountMailboxDto> RegenerateAsync(long bankAccountId, CancellationToken ct = default)
    {
        var mailbox = await _tenant.BankAccountMailbox
            .FirstOrDefaultAsync(m => m.BankAccountId == bankAccountId, ct)
            ?? throw new InvalidOperationException(
                $"No mailbox exists for BankAccount {bankAccountId}. Activate it first.");

        var oldAlias = mailbox.InboundAlias;
        var newAlias = await GenerateUniqueAliasAsync(ct);

        // Retire the old master index row and insert the new one.
        var oldIndex = await _master.MasterMailboxIndex
            .FirstOrDefaultAsync(i => i.InboundAlias == oldAlias && !i.IsAliasRetired, ct);

        if (oldIndex != null)
        {
            oldIndex.IsAliasRetired = true;
        }

        mailbox.InboundAlias = newAlias;
        mailbox.ActiveFrom = DateTime.UtcNow; // treat regeneration as a new window
        await _tenant.SaveChangesAsync(ct);

        await RegisterInMasterIndexAsync(newAlias, mailbox.Id, ct);

        _logger.LogInformation(
            "Mailbox alias rotated: BankAccount={BankAccountId} Old={Old} New={New}",
            bankAccountId, oldAlias, newAlias);

        return await BuildDtoAsync(mailbox, ct);
    }

    // ─── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Retries alias generation until the candidate is not already present in
    /// MasterMailboxIndex among non-retired rows. With ~47 bits of entropy, the
    /// expected number of retries before 100k mailboxes exist is &lt;&lt;1; for safety
    /// we cap at 10 attempts and surface a hard error if exhausted.
    /// </summary>
    private async Task<string> GenerateUniqueAliasAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = _aliasGenerator.Generate();

            var collides = await _master.MasterMailboxIndex
                .AnyAsync(i => i.InboundAlias == candidate && !i.IsAliasRetired, ct);

            if (!collides) return candidate;
        }

        throw new InvalidOperationException(
            "Alias generator produced collisions 10 times in a row — investigate entropy source.");
    }

    private async Task RegisterInMasterIndexAsync(string alias, long tenantMailboxId, CancellationToken ct)
    {
        var schema = await _tenantFactory.ResolveSchemaAsync(
            _tenantResolver.GetCurrentCompanyId()
                ?? throw new InvalidOperationException("No tenant context."),
            ct);

        if (schema == null)
            throw new InvalidOperationException("Cannot register mailbox: tenant schema could not be resolved.");

        _master.MasterMailboxIndex.Add(new MasterMailboxIndex
        {
            InboundAlias = alias,
            TenantSchema = schema,
            TenantBankAccountMailboxId = tenantMailboxId,
            IsAliasRetired = false,
        });

        await _master.SaveChangesAsync(ct);
    }

    private async Task<BankAccountMailboxDto> BuildDtoAsync(BankAccountMailbox mailbox, CancellationToken ct)
    {
        // The inbound domain lives on the master settings row. Cache it per request
        // (these settings rarely change and the caller just inserted the mailbox).
        var settings = await _master.PaymentMatchingSystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        var domain = settings?.InboundDomain ?? "fakvio.cz";

        return new BankAccountMailboxDto
        {
            Id = mailbox.Id,
            BankAccountId = mailbox.BankAccountId,
            InboundAlias = mailbox.InboundAlias,
            FullEmailAddress = $"{mailbox.InboundAlias}@{domain}",
            IsActive = mailbox.IsActive,
            ActiveFrom = mailbox.ActiveFrom,
            DeactivatedAt = mailbox.DeactivatedAt,
            LastEmailReceivedAt = mailbox.LastEmailReceivedAt,
            EmailsReceivedCount = mailbox.EmailsReceivedCount,
        };
    }
}
