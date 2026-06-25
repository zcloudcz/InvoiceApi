using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Archive of an inbound email received on a tenant's invoice mailbox ("fak-" alias).
/// Stores email metadata, body, processing status, and links to the created invoice.
///
/// Mirrors <see cref="InboundEmail"/> pattern but targets invoice import instead of
/// payment matching. Separate entity to avoid polluting the payment pipeline.
/// </summary>
public class InboundInvoiceEmail : BaseEntity
{
    /// <summary>FK to the tenant's invoice mailbox that received this email.</summary>
    public long InvoiceMailboxId { get; set; }

    /// <summary>Navigation to the parent mailbox.</summary>
    public InvoiceMailbox InvoiceMailbox { get; set; } = null!;

    // ─── Email metadata ──────────────────────────────────────────────────

    /// <summary>RFC 5322 Message-ID header.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>IMAP UID (server-assigned, unique within folder).</summary>
    public string? ImapUid { get; set; }

    /// <summary>Server-side received timestamp (IMAP INTERNALDATE).</summary>
    public DateTime ServerReceivedAt { get; set; }

    /// <summary>From: header email address.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>From: header display name (may be null).</summary>
    public string? FromDisplayName { get; set; }

    /// <summary>Resolved alias address (the "fak-" address).</summary>
    public string ToAddress { get; set; } = string.Empty;

    /// <summary>Subject: header.</summary>
    public string? Subject { get; set; }

    /// <summary>Date: header value.</summary>
    public DateTime? EmailDate { get; set; }

    // ─── Body ────────────────────────────────────────────────────────────

    /// <summary>Plain text body (truncated to 1 MB max).</summary>
    public string? TextBody { get; set; }

    /// <summary>HTML body (truncated to 1 MB max).</summary>
    public string? HtmlBody { get; set; }

    /// <summary>True if the body was truncated due to size limits.</summary>
    public bool BodyTruncated { get; set; }

    // ─── Deduplication ───────────────────────────────────────────────────

    /// <summary>
    /// SHA-256 hash for idempotent ingestion.
    /// Computed from (MailboxId | MessageId | ImapUid | ServerReceivedAt).
    /// </summary>
    public string DeduplicationHash { get; set; } = string.Empty;

    // ─── Processing status ───────────────────────────────────────────────

    /// <summary>Current processing status.</summary>
    public EInvoiceEmailStatus Status { get; set; } = EInvoiceEmailStatus.Pending;

    /// <summary>
    /// Classified direction — null until classification completes.
    /// Received = supplier sent us an invoice. Issued = our own invoice copy.
    /// </summary>
    public EInvoiceDirection? Direction { get; set; }

    /// <summary>AI confidence in the direction classification (0.0–1.0).</summary>
    public decimal? ClassificationConfidence { get; set; }

    /// <summary>Error message when Status is Failed.</summary>
    public string? StatusError { get; set; }

    /// <summary>Number of processing attempts (for retry tracking).</summary>
    public int ProcessAttempts { get; set; }

    // ─── Result links ────────────────────────────────────────────────────

    /// <summary>FK to created ReceivedInvoice (when Direction = Received).</summary>
    public long? ReceivedInvoiceId { get; set; }

    /// <summary>Navigation to created received invoice.</summary>
    public ReceivedInvoice? ReceivedInvoice { get; set; }

    /// <summary>FK to created Invoice (when Direction = Issued).</summary>
    public long? InvoiceId { get; set; }

    /// <summary>Navigation to created invoice.</summary>
    public Invoice? Invoice { get; set; }

    // ─── Attachment metadata ─────────────────────────────────────────────

    /// <summary>Number of attachments found in the email.</summary>
    public int AttachmentCount { get; set; }

    /// <summary>Whether any PDF attachment was found.</summary>
    public bool HasPdf { get; set; }

    /// <summary>Whether any ISDOC XML attachment was found.</summary>
    public bool HasIsdoc { get; set; }
}
