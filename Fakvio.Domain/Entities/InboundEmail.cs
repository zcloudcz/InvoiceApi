using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Permanent archive of every inbound email that we actually processed
/// (matched an active mailbox AND passed the ActiveFrom filter).
/// Kept for: audit, reparse, overpayment review, forensic investigation.
///
/// NOT stored: emails to inactive mailboxes, emails whose Date is older than
/// the current ActiveFrom, emails to unknown aliases (those stay on the IMAP
/// server in an "unrouted" folder for SysAdmin).
/// </summary>
public class InboundEmail : BaseEntity
{
    /// <summary>FK to the mailbox (alias) that received the email.</summary>
    public long BankAccountMailboxId { get; set; }

    /// <summary>Navigation to the mailbox.</summary>
    public BankAccountMailbox BankAccountMailbox { get; set; } = null!;

    /// <summary>RFC 5322 Message-ID header — used together with InternalDate for dedupe.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>IMAP UID within the processed folder (for provenance; not globally unique).</summary>
    public string? ImapUid { get; set; }

    /// <summary>IMAP INTERNALDATE — when the server accepted the email.</summary>
    public DateTime ServerReceivedAt { get; set; }

    /// <summary>From: header address.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>From: display name if present.</summary>
    public string? FromDisplayName { get; set; }

    /// <summary>To: address as delivered to our mailbox (= the resolved alias).</summary>
    public string ToAddress { get; set; } = string.Empty;

    /// <summary>Subject: header.</summary>
    public string? Subject { get; set; }

    /// <summary>Date: header — bank's claimed send time (may differ from ServerReceivedAt).</summary>
    public DateTime? EmailDate { get; set; }

    /// <summary>Plain-text body, UTF-8. May be truncated if original exceeded 1 MB.</summary>
    public string? TextBody { get; set; }

    /// <summary>HTML body, UTF-8. May be truncated if original exceeded 1 MB.</summary>
    public string? HtmlBody { get; set; }

    /// <summary>True if TextBody or HtmlBody was truncated due to size.</summary>
    public bool BodyTruncated { get; set; }

    /// <summary>SHA-256 idempotency key — unique per (BankAccountMailboxId).</summary>
    public string DeduplicationHash { get; set; } = string.Empty;

    /// <summary>Parsing lifecycle state.</summary>
    public EParseStatus ParseStatus { get; set; } = EParseStatus.Pending;

    /// <summary>Exception message from last parse attempt (null on success).</summary>
    public string? ParseError { get; set; }

    /// <summary>Number of times the parser tried (retry counter).</summary>
    public int ParseAttempts { get; set; }

    /// <summary>FK to the resulting BankTransaction once parsed successfully.</summary>
    public long? BankTransactionId { get; set; }

    /// <summary>Navigation to the resulting BankTransaction.</summary>
    public BankTransaction? BankTransaction { get; set; }
}
