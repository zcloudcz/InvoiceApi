namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>
/// SysAdmin-facing representation of the payment matching configuration.
///
/// IMPORTANT: <see cref="ImapPassword"/> is WRITE-ONLY from the client's side:
///   - GET returns null (the encrypted value is never sent back over the wire)
///   - PUT with null means "keep existing password" — caller can send other fields
///     without having to re-enter the password each time
///   - PUT with non-null means "replace password with this value" → server encrypts and stores
/// </summary>
public class PaymentMatchingSystemSettingsDto
{
    public bool IsEnabled { get; set; }

    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;
    public string ImapUsername { get; set; } = string.Empty;

    /// <summary>Null on GET, and null-means-unchanged on PUT.</summary>
    public string? ImapPassword { get; set; }

    public string ImapFolder { get; set; } = "INBOX";
    public string ProcessedFolder { get; set; } = "Processed";
    public string UnroutedFolder { get; set; } = "Unrouted";

    public string InboundDomain { get; set; } = "pay.fakvio.cz";

    /// <summary>Minutes between IMAP pull runs. Default 30.</summary>
    public int PollIntervalMinutes { get; set; } = 30;

    public int InboundEmailRetentionDays { get; set; } = 1825;

    // ─── Read-only worker status (server-filled on GET) ────────────────────

    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public int LastRunProcessedCount { get; set; }
}

/// <summary>Result of the SysAdmin "Test connection" action.</summary>
public class TestImapConnectionResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int? MessageCount { get; set; } // number of messages visible in INBOX
}
