namespace Fakvio.Domain.Enums;

/// <summary>
/// Determines how often the system should automatically refresh CNB exchange rates.
/// Configured per company in CompanySystemSettings (ExchangeRateUpdateMode).
/// </summary>
public enum EExchangeRateUpdateMode
{
    /// <summary>
    /// Automatic updates are disabled.
    /// Rates can still be refreshed manually via the "Update from CNB" button.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Refresh rates every day (Monday–Friday, as CNB only publishes on working days).
    /// The auto-refresh worker checks at ~14:35 CET — after CNB publishes the daily list.
    /// </summary>
    Daily = 1,

    /// <summary>
    /// Refresh rates once a week on the configured day (ExchangeRateUpdateDayOfWeek).
    /// Defaults to Monday if DayOfWeek is not set.
    /// </summary>
    Weekly = 2,

    /// <summary>
    /// Refresh rates on the first working day of each month.
    /// Useful for companies that only invoice in foreign currencies occasionally.
    /// </summary>
    Monthly = 3
}
