namespace Fakvio.Contracts.Dto.ExchangeRate;

/// <summary>Official ČNB exchange rate (see Domain.Entities.ExchangeRate).</summary>
public class ExchangeRateDto
{
    /// <summary>ISO 4217 code, e.g. "EUR".</summary>
    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>Units of the currency the <see cref="Rate"/> is quoted for (1, 100 or 1000).</summary>
    public int Amount { get; set; } = 1;

    /// <summary>CZK for <see cref="Amount"/> units, as published by ČNB.</summary>
    public decimal Rate { get; set; }

    /// <summary>CZK for exactly one unit (<c>Rate / Amount</c>) — what invoices store and exports use.</summary>
    public decimal RatePerUnit { get; set; }

    /// <summary>Date the fixing was declared for (the last fixing on or before the requested date).</summary>
    public DateOnly ValidFor { get; set; }
}

/// <summary>Request body of the SysAdmin backfill endpoint.</summary>
public class ExchangeRateBackfillDto
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
}
