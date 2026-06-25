namespace Fakvio.Contracts.Dto.InvoiceEmail;

/// <summary>
/// Read-model for the tenant's invoice email mailbox.
/// </summary>
public class InvoiceMailboxDto
{
    public long Id { get; set; }

    /// <summary>Alias local-part (e.g., "fak-a7b3x9k2mp").</summary>
    public string InboundAlias { get; set; } = string.Empty;

    /// <summary>Full email address (e.g., "fak-a7b3x9k2mp@fakvio.cz").</summary>
    public string FullEmailAddress { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public DateTime ActiveFrom { get; set; }
    public DateTime? DeactivatedAt { get; set; }
    public DateTime? LastEmailReceivedAt { get; set; }
    public int EmailsReceivedCount { get; set; }
}
