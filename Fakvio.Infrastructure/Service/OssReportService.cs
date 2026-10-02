using System.Globalization;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.OssReport;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Quarterly EU OSS report. Reads OSS invoices (OssCountryCode != null) of the current tenant,
/// groups them per country + VAT rate and converts to EUR at the ECB rate of the last day of
/// the quarter. See DEVGUIDE §4.15.
/// </summary>
public class OssReportService : IOssReportService
{
    private readonly TenantDbContext _context;
    private readonly IEcbExchangeRateClient _ecb;

    public OssReportService(TenantDbContext context, IEcbExchangeRateClient ecb)
    {
        _context = context;
        _ecb = ecb;
    }

    public async Task<OssReportDto> GetReportAsync(int year, int quarter, CancellationToken ct = default)
    {
        if (quarter is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(quarter), "Quarter must be 1-4.");
        if (year is < 2021 or > 2100) throw new ArgumentOutOfRangeException(nameof(year), "Year out of range (OSS started in 2021).");

        var from = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        var to = from.AddMonths(3).AddDays(-1);
        var fromUtc = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc); // exclusive

        // Only real tax documents: invoices, advance tax receipts and credit notes. Drafts/deleted never count.
        var invoices = await _context.Invoice
            .AsNoTracking()
            .Where(i => i.OssCountryCode != null
                && i.TaxableSupplyDate >= fromUtc && i.TaxableSupplyDate < toUtc
                && i.Status != EInvoiceStatus.Draft && i.Status != EInvoiceStatus.Deleted
                && (i.DocumentType == EDocumentType.Invoice
                    || i.DocumentType == EDocumentType.CreditNote
                    || i.DocumentType == EDocumentType.TaxReceiptForAdvance))
            .Select(i => new
            {
                i.OssCountryCode,
                i.DocumentType,
                CurrencyCode = i.Currency.Code,
                Items = i.InvoiceItem.Where(x => !x.IsTextRow)
                    .Select(x => new { x.VatRatePercentage, x.TotalBeforeVat, x.VatAmount }).ToList()
            })
            .ToListAsync(ct);

        // 1) sum per (country, rate, currency) in the invoice currency. Credit notes always reduce
        //    the balance, whether the user typed their amounts as positive or as negative numbers.
        var sums = new Dictionary<(string Country, decimal Rate, string Currency), (decimal Base, decimal Vat)>();
        foreach (var inv in invoices)
        {
            var isCredit = inv.DocumentType == EDocumentType.CreditNote;
            foreach (var item in inv.Items)
            {
                var key = (inv.OssCountryCode!, item.VatRatePercentage, inv.CurrencyCode);
                sums.TryGetValue(key, out var cur);
                cur.Base += isCredit ? -Math.Abs(item.TotalBeforeVat) : item.TotalBeforeVat;
                cur.Vat += isCredit ? -Math.Abs(item.VatAmount) : item.VatAmount;
                sums[key] = cur;
            }
        }

        // 2) convert once per currency (ECB rate of the last day of the quarter) and merge to EUR.
        var rates = new Dictionary<string, decimal>();
        foreach (var currency in sums.Keys.Select(k => k.Currency).Distinct())
            rates[currency] = await _ecb.GetUnitsPerEurAsync(currency, to, ct);

        var lines = sums
            .GroupBy(kv => (kv.Key.Country, kv.Key.Rate))
            .Select(g => new OssReportLineDto
            {
                CountryCode = g.Key.Country,
                VatRate = g.Key.Rate,
                BaseEur = Math.Round(g.Sum(kv => kv.Value.Base / rates[kv.Key.Currency]), 2, MidpointRounding.AwayFromZero),
                VatEur = Math.Round(g.Sum(kv => kv.Value.Vat / rates[kv.Key.Currency]), 2, MidpointRounding.AwayFromZero)
            })
            .OrderBy(l => l.CountryCode).ThenBy(l => l.VatRate)
            .ToList();

        return new OssReportDto
        {
            Year = year,
            Quarter = quarter,
            PeriodFrom = from,
            PeriodTo = to,
            DocumentCount = invoices.Count,
            Lines = lines,
            TotalBaseEur = lines.Sum(l => l.BaseEur),
            TotalVatEur = lines.Sum(l => l.VatEur)
        };
    }

    public byte[] ToCsv(OssReportDto report)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("Country;VatRate;BaseEUR;VatEUR\r\n");
        foreach (var l in report.Lines)
            sb.Append(inv, $"{l.CountryCode};{l.VatRate:0.##};{l.BaseEur:0.00};{l.VatEur:0.00}\r\n");
        sb.Append(inv, $"TOTAL;;{report.TotalBaseEur:0.00};{report.TotalVatEur:0.00}\r\n");
        // UTF-8 with BOM so Excel opens it correctly.
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }
}
