namespace Fakvio.Domain.Enums;

/// <summary>
/// Discriminator for alias type in <see cref="Entities.MasterMailboxIndex"/>.
/// Determines which processor handles emails routed to this alias.
/// </summary>
public enum EMailboxType
{
    /// <summary>Bank payment notification emails → PaymentMatchingService.</summary>
    Payment = 1,

    /// <summary>Invoice emails (PDF/ISDOC attachments) → InvoiceEmailProcessor.</summary>
    Invoice = 2
}
