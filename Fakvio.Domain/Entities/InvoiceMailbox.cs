using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Per-tenant email alias for receiving invoices (PDF/ISDOC attachments).
/// One mailbox per company — alias prefix "fak-" (e.g., "fak-a7b3x9k2mp").
///
/// Mirrors <see cref="BankAccountMailbox"/> pattern but without BankAccountId FK
/// (invoices are company-level, not bank-account-level).
///
/// Lifecycle: Activate → (optional Regenerate alias) → Deactivate.
/// Rows are never deleted — only deactivated.
/// </summary>
public class InvoiceMailbox : BaseEntity
{
    /// <summary>
    /// The inbound alias local-part (e.g., "fak-a7b3x9k2mp").
    /// Globally unique among non-retired entries in MasterMailboxIndex.
    /// </summary>
    public string InboundAlias { get; set; } = string.Empty;

    /// <summary>Whether this mailbox is currently accepting emails.</summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Emails older than this timestamp are ignored (prevents processing
    /// a backlog of old emails when activating a new mailbox).
    /// </summary>
    public DateTime ActiveFrom { get; set; }

    /// <summary>When the mailbox was deactivated (null if still active).</summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>Timestamp of the last email received on this alias.</summary>
    public DateTime? LastEmailReceivedAt { get; set; }

    /// <summary>Running count of emails received on this alias.</summary>
    public int EmailsReceivedCount { get; set; }
}
