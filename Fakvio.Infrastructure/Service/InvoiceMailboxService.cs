using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.InvoiceEmail;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Manages the per-tenant invoice email mailbox ("fak-" alias).
/// One mailbox per company. Follows <see cref="BankAccountMailboxService"/> pattern
/// but without BankAccountId (invoices are company-level).
///
/// Cross-schema: tenant row first (canonical), then master index row.
/// </summary>
public class InvoiceMailboxService : IInvoiceMailboxService
{
    private readonly TenantDbContext _tenant;
    private readonly MasterDbContext _master;
    private readonly IAliasGenerator _aliasGenerator;
    private readonly ITenantResolver _tenantResolver;
    private readonly ITenantDbContextFactory _tenantFactory;
    private readonly ILogger<InvoiceMailboxService> _logger;

    private const string AliasPrefix = "fak-";

    public InvoiceMailboxService(
        TenantDbContext tenant,
        MasterDbContext master,
        IAliasGenerator aliasGenerator,
        ITenantResolver tenantResolver,
        ITenantDbContextFactory tenantFactory,
        ILogger<InvoiceMailboxService> logger)
    {
        _tenant = tenant;
        _master = master;
        _aliasGenerator = aliasGenerator;
        _tenantResolver = tenantResolver;
        _tenantFactory = tenantFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<InvoiceMailboxDto?> GetAsync(CancellationToken ct = default)
    {
        var mailbox = await _tenant.InvoiceMailbox
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        return mailbox == null ? null : await BuildDtoAsync(mailbox, ct);
    }

    /// <inheritdoc />
    public async Task<InvoiceMailboxDto> ActivateAsync(CancellationToken ct = default)
    {
        var mailbox = await _tenant.InvoiceMailbox.FirstOrDefaultAsync(ct);
        var now = DateTime.UtcNow;

        if (mailbox == null)
        {
            var alias = await GenerateUniqueAliasAsync(ct);

            mailbox = new InvoiceMailbox
            {
                InboundAlias = alias,
                IsActive = true,
                ActiveFrom = now,
            };
            _tenant.InvoiceMailbox.Add(mailbox);
            await _tenant.SaveChangesAsync(ct);

            await RegisterInMasterIndexAsync(alias, mailbox.Id, ct);

            _logger.LogInformation("Invoice mailbox activated (new): Alias={Alias}", alias);
        }
        else if (!mailbox.IsActive)
        {
            mailbox.IsActive = true;
            mailbox.ActiveFrom = now;
            mailbox.DeactivatedAt = null;
            await _tenant.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Invoice mailbox reactivated: Alias={Alias} ActiveFrom={ActiveFrom}",
                mailbox.InboundAlias, now);
        }

        return await BuildDtoAsync(mailbox, ct);
    }

    /// <inheritdoc />
    public async Task<InvoiceMailboxDto> DeactivateAsync(CancellationToken ct = default)
    {
        var mailbox = await _tenant.InvoiceMailbox.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No invoice mailbox exists. Activate it first.");

        if (mailbox.IsActive)
        {
            mailbox.IsActive = false;
            mailbox.DeactivatedAt = DateTime.UtcNow;
            await _tenant.SaveChangesAsync(ct);

            _logger.LogInformation("Invoice mailbox deactivated: Alias={Alias}", mailbox.InboundAlias);
        }

        return await BuildDtoAsync(mailbox, ct);
    }

    /// <inheritdoc />
    public async Task<InvoiceMailboxDto> RegenerateAliasAsync(CancellationToken ct = default)
    {
        var mailbox = await _tenant.InvoiceMailbox.FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No invoice mailbox exists. Activate it first.");

        var oldAlias = mailbox.InboundAlias;
        var newAlias = await GenerateUniqueAliasAsync(ct);

        var oldIndex = await _master.MasterMailboxIndex
            .FirstOrDefaultAsync(i => i.InboundAlias == oldAlias && !i.IsAliasRetired, ct);

        if (oldIndex != null)
            oldIndex.IsAliasRetired = true;

        mailbox.InboundAlias = newAlias;
        mailbox.ActiveFrom = DateTime.UtcNow;
        await _tenant.SaveChangesAsync(ct);

        await RegisterInMasterIndexAsync(newAlias, mailbox.Id, ct);

        _logger.LogInformation(
            "Invoice mailbox alias rotated: Old={Old} New={New}", oldAlias, newAlias);

        return await BuildDtoAsync(mailbox, ct);
    }

    // ─── Private helpers ─────────────────────────────────────────────────

    private async Task<string> GenerateUniqueAliasAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = _aliasGenerator.Generate(AliasPrefix);
            var collides = await _master.MasterMailboxIndex
                .AnyAsync(i => i.InboundAlias == candidate && !i.IsAliasRetired, ct);

            if (!collides) return candidate;
        }

        throw new InvalidOperationException(
            "Alias generator produced collisions 10 times in a row — investigate entropy source.");
    }

    private async Task RegisterInMasterIndexAsync(string alias, long invoiceMailboxId, CancellationToken ct)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException("No tenant context.");

        var schema = await _tenantFactory.ResolveSchemaAsync(companyId, ct)
            ?? throw new InvalidOperationException("Cannot register mailbox: tenant schema could not be resolved.");

        _master.MasterMailboxIndex.Add(new MasterMailboxIndex
        {
            InboundAlias = alias,
            TenantSchema = schema,
            MailboxType = EMailboxType.Invoice,
            TenantInvoiceMailboxId = invoiceMailboxId,
            IsAliasRetired = false,
        });

        await _master.SaveChangesAsync(ct);
    }

    private async Task<InvoiceMailboxDto> BuildDtoAsync(InvoiceMailbox mailbox, CancellationToken ct)
    {
        var settings = await _master.PaymentMatchingSystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        var domain = settings?.InboundDomain ?? "fakvio.cz";

        return new InvoiceMailboxDto
        {
            Id = mailbox.Id,
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
