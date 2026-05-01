using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Currency;

/// <summary>
/// Request DTO for updating exchange rate auto-update settings.
/// Used by PUT /api/currency/exchange-rates/settings.
/// </summary>
public class UpdateExchangeRateSettingsDto
{
    /// <summary>
    /// New auto-update mode. Required — Off, Daily, Weekly, or Monthly.
    /// </summary>
    public EExchangeRateUpdateMode UpdateMode { get; set; }

    /// <summary>
    /// For Weekly mode: which day of the week to run the refresh.
    /// Null means Monday. Ignored for other modes.
    /// </summary>
    public DayOfWeek? UpdateDayOfWeek { get; set; }
}
