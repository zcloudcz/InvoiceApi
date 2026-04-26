using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Unique inbound email alias tied to a single BankAccount.
/// When activated, emails sent from the bank to this alias are pulled via IMAP,
/// parsed by AI, and matched against outstanding invoices.
///
/// Lifecycle (important — designed to NEVER delete the row):
///   - First activation  → row created, IsActive=true, ActiveFrom=UtcNow, alias generated
///   - Deactivation      → IsActive=false; DeactivatedAt set; alias preserved
///   - Reactivation      → IsActive=true;  ActiveFrom=UtcNow (new window starts now)
///   - Regenerate alias  → InboundAlias rotated; old entry kept in MasterMailboxIndex
///                         with IsAliasRetired=true so stale emails are ignored
///
/// The alias stays associated with the account FOREVER so past InboundEmail and
/// BankTransaction rows keep a valid FK. Regeneration creates a new alias without
/// removing the historical one.
/// </summary>
public class BankAccountMailbox : BaseEntity
{
    /// <summary>FK to the bank account this mailbox belongs to.</summary>
    public long BankAccountId { get; set; }

    /// <summary>Navigation to the owning bank account.</summary>
    public BankAccount BankAccount { get; set; } = null!;

    /// <summary>
    /// Random local part (10 chars, URL-safe base32 — roughly 50 bits of entropy).
    /// Full email address = $"{InboundAlias}@{PaymentMatchingSystemSettings.InboundDomain}".
    /// Unique per tenant schema AND registered globally in MasterMailboxIndex.
    /// </summary>
    public string InboundAlias { get; set; } = string.Empty;

    /// <summary>
    /// Whether the IMAP worker should pull emails to this alias.
    /// When false, arriving emails are NOT downloaded or stored at all.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Start of the currently active window.
    /// Set to UtcNow on every (re)activation. Emails whose message Date is older
    /// than this timestamp are skipped to prevent retroactive processing of
    /// messages that arrived while matching was off.
    /// </summary>
    public DateTime ActiveFrom { get; set; }

    /// <summary>When matching was last deactivated (null if never).</summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>Displayed in UI stats: "Last received …".</summary>
    public DateTime? LastEmailReceivedAt { get; set; }

    /// <summary>Running counter of processed emails (excludes deactivated-window ones).</summary>
    public int EmailsReceivedCount { get; set; }
}
