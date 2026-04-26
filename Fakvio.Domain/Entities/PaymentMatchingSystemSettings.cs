using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// System-wide configuration for the payment matching feature,
/// stored in the MASTER schema (owned by SysAdmin — one row, Id=1).
///
/// Contains the IMAP credentials for the central mailbox and scheduling
/// parameters. The password is stored encrypted via ASP.NET Core Data
/// Protection (same pattern used for 2FA secrets).
/// </summary>
public class PaymentMatchingSystemSettings : BaseEntity
{
    /// <summary>
    /// Master kill-switch for the entire feature.
    /// When false, the IMAP worker exits immediately without connecting.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>IMAP server host (e.g., "imap.gmail.com", "outlook.office365.com").</summary>
    public string ImapHost { get; set; } = string.Empty;

    /// <summary>IMAP port (default 993 = SSL).</summary>
    public int ImapPort { get; set; } = 993;

    /// <summary>Whether to use TLS/SSL for the connection (recommended: true).</summary>
    public bool ImapUseSsl { get; set; } = true;

    /// <summary>IMAP username (typically the mailbox email address).</summary>
    public string ImapUsername { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted IMAP password. Written by the service after encrypting via
    /// IDataProtector; never returned from GET endpoints.
    /// </summary>
    public string ImapPasswordEncrypted { get; set; } = string.Empty;

    /// <summary>IMAP folder to read (default "INBOX").</summary>
    public string ImapFolder { get; set; } = "INBOX";

    /// <summary>
    /// Name of the IMAP folder where processed emails are moved after ingest.
    /// Default "Processed" — auto-created if missing. Keeps INBOX clean for debugging.
    /// </summary>
    public string ProcessedFolder { get; set; } = "Processed";

    /// <summary>
    /// Name of the IMAP folder where unroutable emails are moved (unknown alias).
    /// Default "Unrouted" — SysAdmin can inspect these for misconfigured bank notifications.
    /// </summary>
    public string UnroutedFolder { get; set; } = "Unrouted";

    /// <summary>
    /// Public-facing domain used to build aliases
    /// (e.g., if set to "pay.fakvio.cz" the full address is "pay-xxx@pay.fakvio.cz").
    /// </summary>
    public string InboundDomain { get; set; } = "pay.fakvio.cz";

    /// <summary>
    /// Worker poll interval in minutes. Default 30 (per user requirement).
    /// Enforced min 5, max 1440 by the service layer.
    /// </summary>
    public int PollIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// How long to keep InboundEmail rows before cleanup (default 5 years).
    /// Honours Czech accounting retention requirements.
    /// </summary>
    public int InboundEmailRetentionDays { get; set; } = 1825;

    /// <summary>When the worker last finished a run (null if never).</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Outcome of the last run — "Success", "Failed: …", etc.</summary>
    public string? LastRunStatus { get; set; }

    /// <summary>Count of emails processed in the last run (for the SysAdmin status panel).</summary>
    public int LastRunProcessedCount { get; set; }
}
