namespace Fakvio.Contracts.Dto.SystemConfiguration;

/// <summary>
/// Overall credential health report returned by
/// GET /api/system-configuration/credential-health.
///
/// "Healthy" means all encrypted fields in the system can be decrypted
/// with the current Data Protection key ring.
/// If any field is corrupt (decryption failed — typically after a key-ring loss),
/// Healthy = false and Issues lists each affected field.
/// </summary>
public class CredentialHealthDto
{
    /// <summary>
    /// True when every encrypted credential field can be decrypted correctly.
    /// False when at least one field is corrupt.
    /// </summary>
    public bool Healthy { get; init; }

    /// <summary>
    /// List of encrypted fields that could not be decrypted.
    /// Empty when Healthy = true.
    /// </summary>
    public List<CredentialHealthIssueDto> Issues { get; init; } = [];
}

/// <summary>
/// Describes a single corrupt credential field.
/// The UI uses Entity + Field to tell the admin where to go and re-save their password.
/// </summary>
public class CredentialHealthIssueDto
{
    /// <summary>
    /// Name of the entity/table that owns the field
    /// (e.g., "SystemConfiguration", "CompanySystemSettings", "PaymentMatchingSystemSettings").
    /// </summary>
    public string Entity { get; init; } = string.Empty;

    /// <summary>
    /// Database column / logical field name (e.g., "SmtpPasswordEncrypted", "AiClaudeApiKey").
    /// </summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>
    /// For CompanySystemSettings rows: the CompanyId of the affected tenant.
    /// Null for global settings (SystemConfiguration, PaymentMatchingSystemSettings).
    /// </summary>
    public long? CompanyId { get; init; }

    /// <summary>
    /// Health status string — currently always "corrupt".
    /// Kept as string (not bool) so future states (e.g., "legacy-plaintext") can be added
    /// without a breaking DTO change.
    /// </summary>
    public string Status { get; init; } = "corrupt";
}
