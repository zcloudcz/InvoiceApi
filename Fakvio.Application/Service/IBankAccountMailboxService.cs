using Fakvio.Contracts.Dto.PaymentMatching;

namespace Fakvio.Application.Service;

/// <summary>
/// Tenant-scoped service that manages BankAccountMailbox lifecycle —
/// activation, deactivation, and alias regeneration for a single bank account.
///
/// All operations are idempotent: calling Activate on an already-active mailbox
/// just refreshes ActiveFrom; calling Deactivate on an inactive one is a no-op.
/// </summary>
public interface IBankAccountMailboxService
{
    /// <summary>
    /// Returns the mailbox for a bank account, or null if none exists yet.
    /// Used by the BankAccount detail UI to decide whether to show "Activate"
    /// or the active-mailbox card.
    /// </summary>
    Task<BankAccountMailboxDto?> GetAsync(long bankAccountId, CancellationToken ct = default);

    /// <summary>
    /// Activates payment matching for a bank account.
    /// - Creates a new mailbox with a fresh alias if none exists.
    /// - Reactivates an existing (deactivated) mailbox.
    /// In both cases ActiveFrom is set to UtcNow so prior emails are ignored.
    /// </summary>
    Task<BankAccountMailboxDto> ActivateAsync(long bankAccountId, CancellationToken ct = default);

    /// <summary>
    /// Turns off auto-matching without touching the alias. Idempotent.
    /// </summary>
    Task<BankAccountMailboxDto> DeactivateAsync(long bankAccountId, CancellationToken ct = default);

    /// <summary>
    /// Rotates the alias (e.g., after a suspected leak). The previous alias is
    /// retired in MasterMailboxIndex — incoming emails to it will be ignored.
    /// Leaves IsActive as it was.
    /// </summary>
    Task<BankAccountMailboxDto> RegenerateAsync(long bankAccountId, CancellationToken ct = default);
}
