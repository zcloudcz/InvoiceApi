namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>
/// UI-facing representation of a BankAccountMailbox.
///
/// <see cref="FullEmailAddress"/> is built server-side by combining InboundAlias
/// with PaymentMatchingSystemSettings.InboundDomain — the UI never has to know the domain.
/// </summary>
public class BankAccountMailboxDto
{
    public long Id { get; set; }

    /// <summary>FK to BankAccount this mailbox belongs to.</summary>
    public long BankAccountId { get; set; }

    /// <summary>Alias local-part only (e.g., "pay-7f3k9p2aqr").</summary>
    public string InboundAlias { get; set; } = string.Empty;

    /// <summary>Full email address as the user should type it in their bank (e.g., "pay-xxx@pay.fakvio.cz").</summary>
    public string FullEmailAddress { get; set; } = string.Empty;

    /// <summary>Whether auto-matching is currently ON for this account.</summary>
    public bool IsActive { get; set; }

    /// <summary>Current ActiveFrom cutoff — emails older than this are skipped.</summary>
    public DateTime ActiveFrom { get; set; }

    /// <summary>When matching was last turned off (null if never).</summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>When the last email was processed successfully.</summary>
    public DateTime? LastEmailReceivedAt { get; set; }

    /// <summary>Running counter for the UI stats card.</summary>
    public int EmailsReceivedCount { get; set; }
}
