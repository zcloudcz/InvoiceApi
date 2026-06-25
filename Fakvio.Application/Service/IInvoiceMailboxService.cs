using Fakvio.Contracts.Dto.InvoiceEmail;

namespace Fakvio.Application.Service;

/// <summary>
/// Manages the per-tenant invoice email mailbox ("fak-" alias).
/// One mailbox per company — handles activation, deactivation, and alias regeneration.
/// </summary>
public interface IInvoiceMailboxService
{
    /// <summary>Returns the current mailbox for the tenant (null if never activated).</summary>
    Task<InvoiceMailboxDto?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Activates the invoice mailbox — generates a "fak-" alias and registers it
    /// in MasterMailboxIndex. Idempotent (returns existing if already active).
    /// </summary>
    Task<InvoiceMailboxDto> ActivateAsync(CancellationToken ct = default);

    /// <summary>Deactivates the mailbox. Emails sent to the alias will be ignored.</summary>
    Task<InvoiceMailboxDto> DeactivateAsync(CancellationToken ct = default);

    /// <summary>
    /// Generates a new alias, retiring the old one. The old alias stops routing;
    /// the new alias takes over immediately.
    /// </summary>
    Task<InvoiceMailboxDto> RegenerateAliasAsync(CancellationToken ct = default);
}
