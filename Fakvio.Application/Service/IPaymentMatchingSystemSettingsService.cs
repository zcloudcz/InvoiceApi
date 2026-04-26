using Fakvio.Contracts.Dto.PaymentMatching;

namespace Fakvio.Application.Service;

/// <summary>
/// SysAdmin-scoped service for reading and writing the single-row
/// PaymentMatchingSystemSettings in the master database.
///
/// Never returns the encrypted password — GET always sets
/// <see cref="PaymentMatchingSystemSettingsDto.ImapPassword"/> to null.
/// </summary>
public interface IPaymentMatchingSystemSettingsService
{
    /// <summary>
    /// Gets the current settings. Creates a default row on first call if the
    /// table is empty so callers don't have to handle null.
    /// </summary>
    Task<PaymentMatchingSystemSettingsDto> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves the settings. When <paramref name="dto"/>.ImapPassword is non-null,
    /// the value is encrypted with Data Protection API and persisted. When null,
    /// the existing encrypted password is preserved.
    /// Enforces: PollIntervalMinutes ∈ [5, 1440], RetentionDays ∈ [30, 36500].
    /// </summary>
    Task<PaymentMatchingSystemSettingsDto> UpdateAsync(
        PaymentMatchingSystemSettingsDto dto,
        CancellationToken ct = default);

    /// <summary>
    /// Attempts an IMAP login against the provided (or stored) credentials to verify they work.
    /// When <paramref name="dto"/>.ImapPassword is null the currently stored encrypted password is used.
    /// </summary>
    Task<TestImapConnectionResult> TestConnectionAsync(
        PaymentMatchingSystemSettingsDto dto,
        CancellationToken ct = default);
}
