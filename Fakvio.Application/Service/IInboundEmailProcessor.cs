namespace Fakvio.Application.Service;

/// <summary>
/// Raw payload handed to the inbound email pipeline once the IMAP worker has
/// resolved the alias to a tenant.
/// </summary>
public record InboundEmailPayload(
    long BankAccountMailboxId,
    string MessageId,
    string? ImapUid,
    DateTime ServerReceivedAt,
    string FromAddress,
    string? FromDisplayName,
    string ToAddress,
    string? Subject,
    DateTime? EmailDate,
    string? TextBody,
    string? HtmlBody);

/// <summary>
/// Ingests a single inbound email: persists the archive row, invokes the AI
/// parser, creates a BankTransaction, and hands it to the matcher.
///
/// Tenant-scoped (runs against the TenantDbContext resolved for the alias's tenant).
/// </summary>
public interface IInboundEmailProcessor
{
    /// <summary>
    /// Processes one email end-to-end.
    /// Returns the ParseStatus so the caller can report metrics (Parsed / NeedsReview / Failed).
    /// Idempotent: a duplicate email with the same DeduplicationHash is a no-op.
    /// </summary>
    Task<Domain.Enums.EParseStatus> ProcessAsync(InboundEmailPayload payload, long? companyId, CancellationToken ct = default);
}
