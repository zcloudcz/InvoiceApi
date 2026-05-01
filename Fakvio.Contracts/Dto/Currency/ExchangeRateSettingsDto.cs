using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Currency;

/// <summary>
/// Read DTO for exchange rate auto-update settings of a company.
/// Returned by GET /api/currency/exchange-rates/settings.
/// </summary>
public class ExchangeRateSettingsDto
{
    /// <summary>How often to automatically refresh CNB exchange rates.</summary>
    public EExchangeRateUpdateMode UpdateMode { get; set; }

    /// <summary>
    /// For Weekly mode: the day of the week on which to refresh.
    /// Null means Monday. Ignored for other modes.
    /// </summary>
    public DayOfWeek? UpdateDayOfWeek { get; set; }

    /// <summary>Timestamp of the last successful automatic exchange rate refresh (UTC), or null if never run.</summary>
    public DateTime? LastRunAt { get; set; }
}
