using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.VatReport;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// VAT Report service — aggregates output VAT from issued invoices
/// and input VAT from received invoices for a given period.
/// Uses TaxableSupplyDate (DUZP) as the date criterion because
/// Czech VAT law requires reporting by DUZP, not by issue or payment date.
///
/// Also generates EPO DPHDP3 XML exports (VAT return — "Přiznání k DPH").
/// The generated XML is validated against the official MFČR XSD before returning.
/// </summary>
public class VatReportService : IVatReportService
{
    private readonly TenantDbContext _context;
    private readonly IEpoSchemaProvider _schemaProvider;
    private readonly ICurrencyService _currencyService;
    private readonly ILogger<VatReportService> _logger;

    // EPO date format: "D.M.RRRR" — day and month without leading zeros.
    // Example: 1 March 2026 → "1.3.2026" (NOT "01.03.2026").
    private const string EpoDateFormat = "d.M.yyyy";

    public VatReportService(
        TenantDbContext context,
        IEpoSchemaProvider schemaProvider,
        ICurrencyService currencyService,
        ILogger<VatReportService> logger)
    {
        _context = context;
        _schemaProvider = schemaProvider;
        _currencyService = currencyService;
        _logger = logger;
    }

    // =========================================================================
    // GetReportAsync
    // =========================================================================

    /// <inheritdoc />
    public async Task<VatReportDto> GetReportAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        _logger.LogInformation("Generating VAT report for period {From:yyyy-MM-dd} to {To:yyyy-MM-dd}", from, to);

        // Ensure UTC kind for PostgreSQL timestamp comparison
        var fromUtc = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc);

        // ── Output VAT (from issued invoices) ───────────────────────────────
        // Only count Completed, Paid, or Creditnoted invoices (not Draft/Deleted).
        // Filter by TaxableSupplyDate (DUZP) — the legally relevant date for VAT.
        // Exclude InvoiceTemplate entities (TPH discriminator).
        var outputItems = await _context.InvoiceItem
            .AsNoTracking()
            .Where(item => item.Invoice != null
                && !(item.Invoice is InvoiceTemplate)
                && item.Invoice.TaxableSupplyDate >= fromUtc
                && item.Invoice.TaxableSupplyDate <= toUtc
                && item.Invoice.Status != EInvoiceStatus.Draft
                && item.Invoice.Status != EInvoiceStatus.Deleted
                && item.Invoice.DocumentType == EDocumentType.Invoice)
            .GroupBy(item => item.VatRatePercentage)
            .Select(g => new
            {
                VatRatePercentage = g.Key,
                BaseAmount = g.Sum(i => i.TotalBeforeVat),
                VatAmount = g.Sum(i => i.VatAmount),
                ItemCount = g.Count()
            })
            .ToListAsync(ct);

        // Count of issued invoices in this period
        var issuedCount = await _context.Invoice
            .AsNoTracking()
            .Where(i => !(i is InvoiceTemplate)
                && i.TaxableSupplyDate >= fromUtc
                && i.TaxableSupplyDate <= toUtc
                && i.Status != EInvoiceStatus.Draft
                && i.Status != EInvoiceStatus.Deleted
                && i.DocumentType == EDocumentType.Invoice)
            .CountAsync(ct);

        // Total revenue (before VAT) from issued invoices
        var totalRevenue = await _context.Invoice
            .AsNoTracking()
            .Where(i => !(i is InvoiceTemplate)
                && i.TaxableSupplyDate >= fromUtc
                && i.TaxableSupplyDate <= toUtc
                && i.Status != EInvoiceStatus.Draft
                && i.Status != EInvoiceStatus.Deleted
                && i.DocumentType == EDocumentType.Invoice)
            .SumAsync(i => (decimal?)i.TotalBeforeVat ?? 0, ct);

        // ── Input VAT (from received invoices) ──────────────────────────────
        // Only count Approved or Paid received invoices (not Received/Rejected/Deleted).
        // Filter by TaxableSupplyDate (DUZP).
        var inputItems = await _context.ReceivedInvoiceItem
            .AsNoTracking()
            .Where(item => item.ReceivedInvoice != null
                && item.ReceivedInvoice.TaxableSupplyDate >= fromUtc
                && item.ReceivedInvoice.TaxableSupplyDate <= toUtc
                && item.ReceivedInvoice.Status != EReceivedInvoiceStatus.Received
                && item.ReceivedInvoice.Status != EReceivedInvoiceStatus.Rejected
                && item.ReceivedInvoice.Status != EReceivedInvoiceStatus.Deleted)
            .GroupBy(item => item.VatRatePercentage)
            .Select(g => new
            {
                VatRatePercentage = g.Key,
                BaseAmount = g.Sum(i => i.TotalBeforeVat),
                VatAmount = g.Sum(i => i.VatAmount),
                ItemCount = g.Count()
            })
            .ToListAsync(ct);

        // Count of received invoices in this period
        var receivedCount = await _context.ReceivedInvoice
            .AsNoTracking()
            .Where(r => r.TaxableSupplyDate >= fromUtc
                && r.TaxableSupplyDate <= toUtc
                && r.Status != EReceivedInvoiceStatus.Received
                && r.Status != EReceivedInvoiceStatus.Rejected
                && r.Status != EReceivedInvoiceStatus.Deleted)
            .CountAsync(ct);

        // Total expenses (before VAT) from received invoices
        var totalExpenses = await _context.ReceivedInvoice
            .AsNoTracking()
            .Where(r => r.TaxableSupplyDate >= fromUtc
                && r.TaxableSupplyDate <= toUtc
                && r.Status != EReceivedInvoiceStatus.Received
                && r.Status != EReceivedInvoiceStatus.Rejected
                && r.Status != EReceivedInvoiceStatus.Deleted)
            .SumAsync(r => (decimal?)r.TotalBeforeVat ?? 0, ct);

        // ── Build report ─────────────────────────────────────────────────────
        var totalOutputVat = outputItems.Sum(x => x.VatAmount);
        var totalInputVat = inputItems.Sum(x => x.VatAmount);

        var report = new VatReportDto
        {
            PeriodFrom = from,
            PeriodTo = to,
            OutputVat = outputItems.Select(x => new VatReportLineDto
            {
                VatRatePercentage = x.VatRatePercentage,
                VatRateLabel = $"DPH {x.VatRatePercentage}%",
                BaseAmount = x.BaseAmount,
                VatAmount = x.VatAmount,
                ItemCount = x.ItemCount
            }).OrderByDescending(x => x.VatRatePercentage).ToList(),

            InputVat = inputItems.Select(x => new VatReportLineDto
            {
                VatRatePercentage = x.VatRatePercentage,
                VatRateLabel = $"DPH {x.VatRatePercentage}%",
                BaseAmount = x.BaseAmount,
                VatAmount = x.VatAmount,
                ItemCount = x.ItemCount
            }).OrderByDescending(x => x.VatRatePercentage).ToList(),

            TotalOutputVat = totalOutputVat,
            TotalInputVat = totalInputVat,
            TaxLiability = totalOutputVat - totalInputVat,
            TotalRevenue = totalRevenue,
            TotalExpenses = totalExpenses,
            Profit = totalRevenue - totalExpenses,
            IssuedInvoiceCount = issuedCount,
            ReceivedInvoiceCount = receivedCount
        };

        _logger.LogInformation(
            "VAT report generated: Output={OutputVat:N2}, Input={InputVat:N2}, Liability={Liability:N2}",
            report.TotalOutputVat, report.TotalInputVat, report.TaxLiability);

        return report;
    }

    // =========================================================================
    // ExportEpoVatReturnAsync
    // =========================================================================

    /// <inheritdoc />
    public async Task<byte[]> ExportEpoVatReturnAsync(
        int year,
        int period,
        EVatPeriodType type,
        CancellationToken ct = default)
    {
        // ── 1. Input validation ──────────────────────────────────────────────
        ValidatePeriodArgs(year, period, type);

        // ── 2. Resolve date range for the requested period ───────────────────
        var (periodFrom, periodTo) = ResolvePeriodDates(year, period, type);
        var fromUtc = DateTime.SpecifyKind(periodFrom, DateTimeKind.Utc);
        var toUtc   = DateTime.SpecifyKind(periodTo.AddDays(1).AddTicks(-1), DateTimeKind.Utc);

        _logger.LogInformation(
            "Generating EPO DPHDP3 for year={Year}, period={Period}, type={Type} ({From:yyyy-MM-dd}..{To:yyyy-MM-dd})",
            year, period, type, periodFrom, periodTo);

        // ── 3. Load the issuer (our company) for VetaP ───────────────────────
        var issuer = await _context.Client
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsIssuer && c.IsActive, ct)
            ?? throw new InvalidOperationException(
                "No active issuer (IsIssuer=true) found in the tenant database. " +
                "Set up your company data before generating EPO exports.");

        // ── 4. Load issued invoices for the period ───────────────────────────
        // Same filter as GetReportAsync: exclude Draft, Deleted.
        // We need CurrencyCode for FX conversion, so include Currency navigation.
        var issuedInvoices = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem)
            .Where(i => !(i is InvoiceTemplate)
                && i.TaxableSupplyDate >= fromUtc
                && i.TaxableSupplyDate <= toUtc
                && i.Status != EInvoiceStatus.Draft
                && i.Status != EInvoiceStatus.Deleted
                && i.DocumentType == EDocumentType.Invoice)
            .ToListAsync(ct);

        // ── 5. Load received invoices for the period ─────────────────────────
        var receivedInvoices = await _context.ReceivedInvoice
            .AsNoTracking()
            .Include(r => r.Currency)
            .Include(r => r.Items)
            .Where(r => r.TaxableSupplyDate >= fromUtc
                && r.TaxableSupplyDate <= toUtc
                && r.Status != EReceivedInvoiceStatus.Received
                && r.Status != EReceivedInvoiceStatus.Rejected
                && r.Status != EReceivedInvoiceStatus.Deleted)
            .ToListAsync(ct);

        // ── 6. Aggregate output VAT per rate bucket ──────────────────────────
        // EPO distinguishes two rate tiers: standard (23 % / 21 %) and reduced (12 % / 5 %).
        // In the XSD, attributes are named "obrat23" / "dan23" (standard) and
        // "obrat5" / "dan5" (reduced) — the suffix reflects historical rates
        // (was 23 %/5 %, now 21 %/12 %), but the attribute names are unchanged.
        var outStdBase = 0m; // row 1: base  at standard rate
        var outStdVat  = 0m; // row 1: VAT   at standard rate
        var outRedBase = 0m; // row 2: base  at reduced rate
        var outRedVat  = 0m; // row 2: VAT   at reduced rate

        foreach (var inv in issuedInvoices)
        {
            var currencyCode = inv.Currency?.Code ?? "CZK";
            // TaxableSupplyDate is nullable — fall back to today's date if not set
            // (should not happen in practice; invoices without DUZP are excluded by the WHERE clause).
            var duzp = DateOnly.FromDateTime(inv.TaxableSupplyDate.GetValueOrDefault(DateTime.UtcNow));

            foreach (var item in inv.InvoiceItem)
            {
                // Convert item amounts to CZK (no-op for CZK invoices).
                var baseCzk = await _currencyService.ConvertToCzkAsync(
                    item.TotalBeforeVat, currencyCode, duzp, ct);
                var vatCzk = await _currencyService.ConvertToCzkAsync(
                    item.VatAmount, currencyCode, duzp, ct);

                // EPO standard rate bucket: >= 20 % (currently 21 %).
                // EPO reduced rate bucket:  < 20 % (currently 12 %).
                if (item.VatRatePercentage >= 20m)
                {
                    outStdBase += baseCzk;
                    outStdVat  += vatCzk;
                }
                else if (item.VatRatePercentage > 0m)
                {
                    outRedBase += baseCzk;
                    outRedVat  += vatCzk;
                }
                // 0 % / exempt: not reported in Veta1.
            }
        }

        // ── 7. Aggregate input VAT per rate bucket ───────────────────────────
        var inStdBase = 0m; // row 40: base  at standard rate
        var inStdVat  = 0m; // row 40: VAT   at standard rate
        var inRedBase = 0m; // row 41: base  at reduced rate
        var inRedVat  = 0m; // row 41: VAT   at reduced rate

        foreach (var rec in receivedInvoices)
        {
            var currencyCode = rec.Currency?.Code ?? "CZK";
            // TaxableSupplyDate is nullable on ReceivedInvoice as well.
            var duzp = DateOnly.FromDateTime(rec.TaxableSupplyDate.GetValueOrDefault(DateTime.UtcNow));

            foreach (var item in rec.Items)
            {
                var baseCzk = await _currencyService.ConvertToCzkAsync(
                    item.TotalBeforeVat, currencyCode, duzp, ct);
                var vatCzk = await _currencyService.ConvertToCzkAsync(
                    item.VatAmount, currencyCode, duzp, ct);

                if (item.VatRatePercentage >= 20m)
                {
                    inStdBase += baseCzk;
                    inStdVat  += vatCzk;
                }
                else if (item.VatRatePercentage > 0m)
                {
                    inRedBase += baseCzk;
                    inRedVat  += vatCzk;
                }
            }
        }

        // Row 51 = sum of all deductible input VAT (rows 40 + 41).
        var inSumVat = inStdVat + inRedVat;

        // EPO requires integer amounts (whole CZK, no decimals).
        // Round using MidpointRounding.AwayFromZero (standard Czech accounting rounding).
        var outStdBaseI = RoundToCzk(outStdBase);
        var outStdVatI  = RoundToCzk(outStdVat);
        var outRedBaseI = RoundToCzk(outRedBase);
        var outRedVatI  = RoundToCzk(outRedVat);
        var inStdBaseI  = RoundToCzk(inStdBase);
        var inStdVatI   = RoundToCzk(inStdVat);
        var inRedBaseI  = RoundToCzk(inRedBase);
        var inRedVatI   = RoundToCzk(inRedVat);
        var inSumVatI   = RoundToCzk(inSumVat);

        // ── 8. Build the XML document ─────────────────────────────────────────
        var doc = BuildDphdp3Xml(
            year, period, type,
            periodFrom, periodTo,
            issuer,
            hasOutputVat: outStdBaseI != 0 || outRedBaseI != 0,
            outStdBaseI, outStdVatI,
            outRedBaseI, outRedVatI,
            hasInputVat: inStdBaseI != 0 || inRedBaseI != 0,
            inStdBaseI, inStdVatI,
            inRedBaseI, inRedVatI,
            inSumVatI);

        // ── 9. XSD validation ─────────────────────────────────────────────────
        var schemaSet = _schemaProvider.GetSchemaSet(EEpoFormType.VatReturn, year);
        var xsdErrors = new List<string>();
        doc.Validate(schemaSet, (_, e) => xsdErrors.Add(e.Message));

        if (xsdErrors.Count > 0)
        {
            // This indicates a mapping bug — the generated XML does not conform to the schema.
            // Surface all errors so the developer can diagnose immediately.
            _logger.LogError(
                "Generated DPHDP3 XML failed XSD validation ({Count} error(s)): {Errors}",
                xsdErrors.Count, string.Join("; ", xsdErrors.Take(5)));

            throw new EpoValidationException("DPHDP3", xsdErrors);
        }

        // ── 10. Serialise to UTF-8 (no BOM) ──────────────────────────────────
        using var ms = new MemoryStream();
        // XmlWriterSettings.Encoding = UTF8 without BOM (new UTF8Encoding(false)).
        // EPO portal rejects files that start with BOM bytes (EF BB BF).
        var xmlSettings = new XmlWriterSettings
        {
            Encoding    = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent      = true,
            IndentChars = "  "
        };

        using (var writer = XmlWriter.Create(ms, xmlSettings))
        {
            doc.WriteTo(writer);
        }

        var bytes = ms.ToArray();

        _logger.LogInformation(
            "DPHDP3 generated: year={Year}, period={Period}, {Bytes} bytes, " +
            "outStd={OutStdBase}/{OutStdVat}, outRed={OutRedBase}/{OutRedVat}, " +
            "inStd={InStdBase}/{InStdVat}, inRed={InRedBase}/{InRedVat}, inSum={InSumVat}",
            year, period, bytes.Length,
            outStdBaseI, outStdVatI, outRedBaseI, outRedVatI,
            inStdBaseI, inStdVatI, inRedBaseI, inRedVatI, inSumVatI);

        return bytes;
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    /// <summary>
    /// Validates that year and period are within the allowed ranges defined by the issue.
    /// Year must be in [2024, currentYear+1].
    /// Period must be [1,12] for Monthly or [1,4] for Quarterly.
    /// </summary>
    private static void ValidatePeriodArgs(int year, int period, EVatPeriodType type)
    {
        var currentYear = DateTime.UtcNow.Year;

        if (year < 2024 || year > currentYear + 1)
            throw new ArgumentOutOfRangeException(
                nameof(year),
                $"Year must be in [2024, {currentYear + 1}]. Provided: {year}.");

        var maxPeriod = type == EVatPeriodType.Monthly ? 12 : 4;
        if (period < 1 || period > maxPeriod)
            throw new ArgumentOutOfRangeException(
                nameof(period),
                $"Period must be in [1, {maxPeriod}] for {type} reporting. Provided: {period}.");
    }

    /// <summary>
    /// Returns the first and last day of the reporting period (month or quarter).
    /// Both dates are at midnight (00:00:00) — UTC conversion happens at the call site.
    /// </summary>
    private static (DateTime From, DateTime To) ResolvePeriodDates(int year, int period, EVatPeriodType type)
    {
        if (type == EVatPeriodType.Monthly)
        {
            var from = new DateTime(year, period, 1);
            var to   = from.AddMonths(1).AddDays(-1);
            return (from, to);
        }
        else // Quarterly
        {
            // Q1 = Jan–Mar, Q2 = Apr–Jun, Q3 = Jul–Sep, Q4 = Oct–Dec.
            var startMonth = (period - 1) * 3 + 1;
            var from = new DateTime(year, startMonth, 1);
            var to   = from.AddMonths(3).AddDays(-1);
            return (from, to);
        }
    }

    /// <summary>
    /// Rounds a decimal CZK amount to the nearest whole crown using
    /// MidpointRounding.AwayFromZero (standard Czech accounting rounding).
    /// Returns a long because EPO XSD enforces totalDigits=14, fractionDigits=0.
    /// </summary>
    private static long RoundToCzk(decimal amount)
        => (long)Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Builds the complete DPHDP3 XDocument according to the EPO schema.
    ///
    /// Structure:
    ///   Pisemnost
    ///     DPHDP3
    ///       VetaD   — period metadata (rok, mesic/ctvrt, dapdph_forma, …)
    ///       VetaP   — taxpayer identification (dic, c_ufo, typ_ds, …)
    ///       Veta1?  — output VAT rows 1 (standard) and 2 (reduced)  [omitted if empty]
    ///       Veta4?  — input VAT rows 40/41 + row 51 total           [omitted if empty]
    ///
    /// All attribute values are strings; numeric ones are formatted without decimal separators
    /// (EPO XSD: fractionDigits=0) and dates use "D.M.RRRR" format.
    /// </summary>
    private static XDocument BuildDphdp3Xml(
        int year, int period, EVatPeriodType type,
        DateTime periodFrom, DateTime periodTo,
        Client issuer,
        bool hasOutputVat,
        long outStdBase, long outStdVat,
        long outRedBase, long outRedVat,
        bool hasInputVat,
        long inStdBase, long inStdVat,
        long inRedBase, long inRedVat,
        long inSumVat)
    {
        // EPO date format: no leading zeros on day or month (e.g. "1.3.2026").
        var dateFrom = periodFrom.ToString(EpoDateFormat);
        var dateTo   = periodTo.ToString(EpoDateFormat);
        var today    = DateTime.Today.ToString(EpoDateFormat);

        // Build VetaD — period descriptor.
        var vetaD = new XElement("VetaD",
            new XAttribute("rok",          year.ToString()),
            new XAttribute("dapdph_forma", "B"),           // "B" = řádné přiznání
            new XAttribute("dokument",     "DP3"),         // fixed per XSD
            new XAttribute("k_uladis",     "DPH"),         // fixed per XSD
            new XAttribute("typ_platce",   "P"),           // "P" = Plátce daně § 6
            new XAttribute("d_poddp",      today),
            new XAttribute("zdobd_od",     dateFrom),
            new XAttribute("zdobd_do",     dateTo));

        // Monthly → @mesic; Quarterly → @ctvrt. Never both.
        if (type == EVatPeriodType.Monthly)
            vetaD.Add(new XAttribute("mesic", period.ToString()));
        else
            vetaD.Add(new XAttribute("ctvrt", period.ToString()));

        // Build VetaP — taxpayer identification.
        // dic: numeric part of DIČ only (strip "CZ" prefix required by EPO XSD pattern [0-9]{1,10}).
        var rawDic = issuer.TaxNumber ?? issuer.RegistrationNumber ?? string.Empty;
        var dic = rawDic.StartsWith("CZ", StringComparison.OrdinalIgnoreCase)
            ? rawDic[2..]
            : rawDic;

        // c_ufo is required — throw a clear error if not configured.
        if (!issuer.EpoTaxOfficeCode.HasValue)
            throw new InvalidOperationException(
                $"Issuer '{issuer.CompanyName}' (ID={issuer.Id}) has no EpoTaxOfficeCode configured. " +
                "Set the tax office code (c_ufo) in Company Settings before generating EPO exports.");

        var vetaP = new XElement("VetaP",
            new XAttribute("c_ufo",  issuer.EpoTaxOfficeCode.Value.ToString()),
            new XAttribute("dic",    dic),
            new XAttribute("typ_ds", "P")); // "P" = právnická osoba (legal entity)

        // Optionally add company name (zkrobchjm ≤ 255 chars).
        if (!string.IsNullOrWhiteSpace(issuer.CompanyName))
            vetaP.Add(new XAttribute("zkrobchjm",
                issuer.CompanyName.Length > 255
                    ? issuer.CompanyName[..255]
                    : issuer.CompanyName));

        // Build DPHDP3 element — sequence order per XSD: VetaD, VetaP, Veta1?, Veta4?
        var dphdp3 = new XElement("DPHDP3", vetaD, vetaP);

        // Veta1 — output VAT. Omitted when the period has no output-VAT invoices.
        if (hasOutputVat)
        {
            var veta1 = new XElement("Veta1");

            if (outStdBase != 0 || outStdVat != 0)
            {
                // Row 1: standard-rate domestic supply (formerly 23 %, now 21 %).
                veta1.Add(new XAttribute("obrat23", outStdBase.ToString()));
                veta1.Add(new XAttribute("dan23",   outStdVat.ToString()));
            }

            if (outRedBase != 0 || outRedVat != 0)
            {
                // Row 2: reduced-rate domestic supply (formerly 5 %, now 12 %).
                veta1.Add(new XAttribute("obrat5", outRedBase.ToString()));
                veta1.Add(new XAttribute("dan5",   outRedVat.ToString()));
            }

            dphdp3.Add(veta1);
        }

        // Veta4 — input VAT. Omitted when the period has no deductible input-VAT.
        if (hasInputVat)
        {
            var veta4 = new XElement("Veta4");

            if (inStdBase != 0 || inStdVat != 0)
            {
                // Row 40: standard-rate domestic input (deductible from tax authority).
                veta4.Add(new XAttribute("pln23",         inStdBase.ToString()));
                veta4.Add(new XAttribute("odp_tuz23_nar", inStdVat.ToString()));
            }

            if (inRedBase != 0 || inRedVat != 0)
            {
                // Row 41: reduced-rate domestic input.
                veta4.Add(new XAttribute("pln5",         inRedBase.ToString()));
                veta4.Add(new XAttribute("odp_tuz5_nar", inRedVat.ToString()));
            }

            // Row 51: total deductible VAT (sum of rows 40 and 41).
            veta4.Add(new XAttribute("odp_sum_nar", inSumVat.ToString()));

            dphdp3.Add(veta4);
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("Pisemnost", dphdp3));
    }
}
