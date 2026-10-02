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
/// Also generates EPO DPHDP3 XML exports (VAT return — "Přiznání k DPH") and
/// DPHKH1 exports (VAT control statement).
/// The generated XML is validated against the official MFČR XSD before returning.
///
/// EPO header fields (c_ufo, c_pracufo, contact info, authorized person) are read
/// from <see cref="CompanySystemSettings"/> in the master database.
/// If any required field is missing, <see cref="EpoHeaderIncompleteException"/> is thrown
/// and the controller converts it to HTTP 400 with code EPO_HEADER_INCOMPLETE.
/// </summary>
public class VatReportService : IVatReportService
{
    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly IEpoSchemaProvider _schemaProvider;
    private readonly ICurrencyService _currencyService;
    private readonly ILogger<VatReportService> _logger;

    // EPO date format: "D.M.RRRR" — day and month without leading zeros.
    // Example: 1 March 2026 → "1.3.2026" (NOT "01.03.2026").
    private const string EpoDateFormat = "d.M.yyyy";

    public VatReportService(
        TenantDbContext context,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        IEpoSchemaProvider schemaProvider,
        ICurrencyService currencyService,
        ILogger<VatReportService> logger)
    {
        _context = context;
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
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
                && item.Invoice.DocumentType == EDocumentType.Invoice
                && item.Invoice.OssCountryCode == null) // OSS invoices are not CZ VAT (DEVGUIDE §4.15)
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
                && i.DocumentType == EDocumentType.Invoice
                && i.OssCountryCode == null) // OSS invoices are not CZ VAT (DEVGUIDE §4.15)
            .CountAsync(ct);

        // Total revenue (before VAT) from issued invoices
        var totalRevenue = await _context.Invoice
            .AsNoTracking()
            .Where(i => !(i is InvoiceTemplate)
                && i.TaxableSupplyDate >= fromUtc
                && i.TaxableSupplyDate <= toUtc
                && i.Status != EInvoiceStatus.Draft
                && i.Status != EInvoiceStatus.Deleted
                && i.DocumentType == EDocumentType.Invoice
                && i.OssCountryCode == null) // OSS invoices are not CZ VAT (DEVGUIDE §4.15)
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

        // ── 3a. Load and validate EPO header settings (master DB) ────────────
        // Throws EpoHeaderIncompleteException when required fields (c_ufo, c_pracufo) are missing.
        var epoSettings = await LoadAndValidateEpoSettingsAsync(ct);

        // ── 3b. Load the issuer (our company) for VetaP ──────────────────────
        var issuer = await _context.Client
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsIssuer && c.IsActive, ct)
            ?? throw new InvalidOperationException(
                "No active issuer (IsIssuer=true) found in the tenant database. " +
                "Set up your company data before generating EPO exports.");

        // EPO filings are only valid for VAT payers — block early to avoid
        // generating a file the tax portal would reject.
        if (!issuer.IsVatPayer)
            throw new VatPayerRequiredException();

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
                && i.DocumentType == EDocumentType.Invoice
                && i.OssCountryCode == null) // OSS invoices are not CZ VAT (DEVGUIDE §4.15)
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
            epoSettings,
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
    // ExportEpoControlStatementAsync
    // =========================================================================

    /// <inheritdoc />
    public async Task<byte[]> ExportEpoControlStatementAsync(
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
            "Generating EPO DPHKH1 for year={Year}, period={Period}, type={Type} ({From:yyyy-MM-dd}..{To:yyyy-MM-dd})",
            year, period, type, periodFrom, periodTo);

        // ── 3a. Load and validate EPO header settings (master DB) ────────────
        // Throws EpoHeaderIncompleteException when required fields (c_ufo, c_pracufo) are missing.
        var epoSettings = await LoadAndValidateEpoSettingsAsync(ct);

        // ── 3b. Load the issuer (our company) for VetaP ──────────────────────
        var issuer = await _context.Client
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsIssuer && c.IsActive, ct)
            ?? throw new InvalidOperationException(
                "No active issuer (IsIssuer=true) found in the tenant database. " +
                "Set up your company data before generating EPO exports.");

        // EPO filings are only valid for VAT payers — block early to avoid
        // generating a file the tax portal would reject.
        if (!issuer.IsVatPayer)
            throw new VatPayerRequiredException();

        // ── 4. Load issued invoices with client navigation ───────────────────
        // Client.TaxNumber is needed for the A.4/A.5 split, and
        // Client navigation is needed to get TaxNumber from the relation.
        var issuedInvoices = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem)
            .Include(i => i.Client) // needed for TaxNumber (CZ DIČ check)
            .Where(i => !(i is InvoiceTemplate)
                && i.TaxableSupplyDate >= fromUtc
                && i.TaxableSupplyDate <= toUtc
                && i.Status != EInvoiceStatus.Draft
                && i.Status != EInvoiceStatus.Deleted
                && i.DocumentType == EDocumentType.Invoice
                && i.OssCountryCode == null) // OSS invoices are not CZ VAT (DEVGUIDE §4.15)
            .ToListAsync(ct);

        // ── 5. Load received invoices with supplier navigation ───────────────
        var receivedInvoices = await _context.ReceivedInvoice
            .AsNoTracking()
            .Include(r => r.Currency)
            .Include(r => r.Items)
            .Include(r => r.Supplier) // needed for TaxNumber (CZ DIČ check)
            .Where(r => r.TaxableSupplyDate >= fromUtc
                && r.TaxableSupplyDate <= toUtc
                && r.Status != EReceivedInvoiceStatus.Received
                && r.Status != EReceivedInvoiceStatus.Rejected
                && r.Status != EReceivedInvoiceStatus.Deleted)
            .ToListAsync(ct);

        // ── 6. Classify and aggregate issued invoices (A.4 / A.5) ────────────
        // A.4: total incl. VAT >= 10 000 CZK AND client has CZ VAT number → per-invoice row.
        // A.5: everything else → one aggregated summary row for the period.
        // DPHKH1 amounts use 2 decimal places (fractionDigits=2) unlike DPHDP3.

        var a4Rows = new List<KhRow>();  // individual rows for A.4
        // A.5 accumulators split by rate (standard = >= 20%, reduced = < 20%)
        var a5StdBase = 0m;
        var a5StdVat  = 0m;
        var a5RedBase = 0m;
        var a5RedVat  = 0m;

        foreach (var inv in issuedInvoices)
        {
            var currencyCode = inv.Currency?.Code ?? "CZK";
            var duzp = DateOnly.FromDateTime(inv.TaxableSupplyDate.GetValueOrDefault(DateTime.UtcNow));

            // Convert all invoice items to CZK to determine total incl. VAT.
            var totalWithVatCzk = 0m;
            var perItemCzk = new List<(decimal baseCzk, decimal vatCzk, decimal vatPct)>();

            foreach (var item in inv.InvoiceItem)
            {
                var baseCzk = await _currencyService.ConvertToCzkAsync(
                    item.TotalBeforeVat, currencyCode, duzp, ct);
                var vatCzk = await _currencyService.ConvertToCzkAsync(
                    item.VatAmount, currencyCode, duzp, ct);
                perItemCzk.Add((baseCzk, vatCzk, item.VatRatePercentage));
                totalWithVatCzk += baseCzk + vatCzk;
            }

            // Determine classification: A.4 requires CZ VAT number AND total >= 10 000 CZK.
            var clientTaxNumber = inv.Client?.TaxNumber;
            if (IsCzVatNumber(clientTaxNumber) && totalWithVatCzk >= 10_000m)
            {
                // A.4 — individual row. Use numeric part of DIČ (strip "CZ" prefix).
                var dicOdb = StripCzPrefix(clientTaxNumber!);
                var dppd   = inv.TaxableSupplyDate!.Value.ToString(EpoDateFormat);
                var docNum = inv.DocumentNumber ?? string.Empty;

                // Sum the per-item CZK amounts by rate.
                var rowStdBase = perItemCzk.Where(x => x.vatPct >= 20m).Sum(x => x.baseCzk);
                var rowStdVat  = perItemCzk.Where(x => x.vatPct >= 20m).Sum(x => x.vatCzk);
                var rowRedBase = perItemCzk.Where(x => x.vatPct is > 0m and < 20m).Sum(x => x.baseCzk);
                var rowRedVat  = perItemCzk.Where(x => x.vatPct is > 0m and < 20m).Sum(x => x.vatCzk);

                a4Rows.Add(new KhRow(dicOdb, docNum, dppd, rowStdBase, rowStdVat, rowRedBase, rowRedVat));
            }
            else
            {
                // A.5 — aggregate into summary totals.
                foreach (var (baseCzk, vatCzk, vatPct) in perItemCzk)
                {
                    if (vatPct >= 20m)
                    {
                        a5StdBase += baseCzk;
                        a5StdVat  += vatCzk;
                    }
                    else if (vatPct > 0m)
                    {
                        a5RedBase += baseCzk;
                        a5RedVat  += vatCzk;
                    }
                }
            }
        }

        // ── 7. Classify and aggregate received invoices (B.2 / B.3) ──────────
        var b2Rows = new List<KhRow>();  // individual rows for B.2
        var b3StdBase = 0m;
        var b3StdVat  = 0m;
        var b3RedBase = 0m;
        var b3RedVat  = 0m;

        foreach (var rec in receivedInvoices)
        {
            var currencyCode = rec.Currency?.Code ?? "CZK";
            var duzp = DateOnly.FromDateTime(rec.TaxableSupplyDate.GetValueOrDefault(DateTime.UtcNow));

            var totalWithVatCzk = 0m;
            var perItemCzk = new List<(decimal baseCzk, decimal vatCzk, decimal vatPct)>();

            foreach (var item in rec.Items)
            {
                var baseCzk = await _currencyService.ConvertToCzkAsync(
                    item.TotalBeforeVat, currencyCode, duzp, ct);
                var vatCzk = await _currencyService.ConvertToCzkAsync(
                    item.VatAmount, currencyCode, duzp, ct);
                perItemCzk.Add((baseCzk, vatCzk, item.VatRatePercentage));
                totalWithVatCzk += baseCzk + vatCzk;
            }

            var supplierTaxNumber = rec.Supplier?.TaxNumber;
            if (IsCzVatNumber(supplierTaxNumber) && totalWithVatCzk >= 10_000m)
            {
                // B.2 — individual row. dic_dod = numeric part of supplier's CZ DIČ.
                var dicDod = StripCzPrefix(supplierTaxNumber!);
                var dppd   = rec.TaxableSupplyDate!.Value.ToString(EpoDateFormat);
                var docNum = rec.DocumentNumber ?? string.Empty;

                var rowStdBase = perItemCzk.Where(x => x.vatPct >= 20m).Sum(x => x.baseCzk);
                var rowStdVat  = perItemCzk.Where(x => x.vatPct >= 20m).Sum(x => x.vatCzk);
                var rowRedBase = perItemCzk.Where(x => x.vatPct is > 0m and < 20m).Sum(x => x.baseCzk);
                var rowRedVat  = perItemCzk.Where(x => x.vatPct is > 0m and < 20m).Sum(x => x.vatCzk);

                b2Rows.Add(new KhRow(dicDod, docNum, dppd, rowStdBase, rowStdVat, rowRedBase, rowRedVat));
            }
            else
            {
                foreach (var (baseCzk, vatCzk, vatPct) in perItemCzk)
                {
                    if (vatPct >= 20m)
                    {
                        b3StdBase += baseCzk;
                        b3StdVat  += vatCzk;
                    }
                    else if (vatPct > 0m)
                    {
                        b3RedBase += baseCzk;
                        b3RedVat  += vatCzk;
                    }
                }
            }
        }

        // ── 8. Build the XML document ─────────────────────────────────────────
        var doc = BuildDphkh1Xml(
            year, period, type,
            periodFrom, periodTo,
            issuer,
            epoSettings,
            a4Rows,
            a5StdBase, a5StdVat, a5RedBase, a5RedVat,
            b2Rows,
            b3StdBase, b3StdVat, b3RedBase, b3RedVat);

        // ── 9. XSD validation ─────────────────────────────────────────────────
        var schemaSet = _schemaProvider.GetSchemaSet(EEpoFormType.ControlStatement, year);
        var xsdErrors = new List<string>();
        doc.Validate(schemaSet, (_, e) => xsdErrors.Add(e.Message));

        if (xsdErrors.Count > 0)
        {
            _logger.LogError(
                "Generated DPHKH1 XML failed XSD validation ({Count} error(s)): {Errors}",
                xsdErrors.Count, string.Join("; ", xsdErrors.Take(5)));

            throw new EpoValidationException("DPHKH1", xsdErrors);
        }

        // ── 10. Serialise to UTF-8 (no BOM) ──────────────────────────────────
        using var ms = new MemoryStream();
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
            "DPHKH1 generated: year={Year}, period={Period}, {Bytes} bytes, " +
            "A4rows={A4}, A5std={A5StdBase}/{A5StdVat}, A5red={A5RedBase}/{A5RedVat}, " +
            "B2rows={B2}, B3std={B3StdBase}/{B3StdVat}, B3red={B3RedBase}/{B3RedVat}",
            year, period, bytes.Length,
            a4Rows.Count, a5StdBase, a5StdVat, a5RedBase, a5RedVat,
            b2Rows.Count, b3StdBase, b3StdVat, b3RedBase, b3RedVat);

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
        EpoSettings epo,
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

        // Build VetaP — taxpayer identification (shared logic with DPHKH1).
        var vetaP = BuildVetaP(issuer, epo);

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

    /// <summary>
    /// Builds the EPO <c>VetaP</c> element — taxpayer identification section shared
    /// by both DPHDP3 and DPHKH1 forms.
    ///
    /// Required attributes: <c>c_ufo</c>, <c>c_pracufo</c>, <c>dic</c>, <c>typ_ds</c>.
    /// Optional attributes added when not null/empty: <c>zkrobchjm</c>, <c>c_telef</c>,
    /// <c>email</c>, <c>opr_jmeno</c>, <c>opr_prijmeni</c>.
    /// </summary>
    /// <param name="issuer">Issuer entity (our company) from the tenant DB.</param>
    /// <param name="epo">Validated EPO settings from CompanySystemSettings.</param>
    private static XElement BuildVetaP(Client issuer, EpoSettings epo)
    {
        // dic: EPO XSD requires the numeric part of DIČ only (strip "CZ" prefix).
        // Pattern: [0-9]{1,10}
        var rawDic = issuer.TaxNumber ?? issuer.RegistrationNumber ?? string.Empty;
        var dic = rawDic.StartsWith("CZ", StringComparison.OrdinalIgnoreCase)
            ? rawDic[2..]
            : rawDic;

        // c_ufo and c_pracufo come from CompanySystemSettings (validated by LoadAndValidateEpoSettingsAsync).
        var vetaP = new XElement("VetaP",
            new XAttribute("c_ufo",     epo.TaxOfficeCode.ToString()),
            new XAttribute("c_pracufo", epo.TaxOfficeBranchCode.ToString()),
            new XAttribute("dic",       dic),
            new XAttribute("typ_ds",    "P")); // "P" = právnická osoba (legal entity)

        // zkrobchjm — company name abbreviation (≤ 255 chars).
        if (!string.IsNullOrWhiteSpace(issuer.CompanyName))
            vetaP.Add(new XAttribute("zkrobchjm",
                issuer.CompanyName.Length > 255
                    ? issuer.CompanyName[..255]
                    : issuer.CompanyName));

        // c_telef — XSD maxLength=14; strip spaces (Czech phone "+420 123 456" → "+420123456").
        if (!string.IsNullOrWhiteSpace(epo.ContactPhone))
        {
            var phone = epo.ContactPhone.Replace(" ", "");
            vetaP.Add(new XAttribute("c_telef", phone.Length > 14 ? phone[..14] : phone));
        }

        if (!string.IsNullOrWhiteSpace(epo.ContactEmail))
            vetaP.Add(new XAttribute("email", epo.ContactEmail));

        // XSD uses opr_jmeno (first name, max 20) + opr_prijmeni (last name, max 36).
        // EpoAuthorizedPersonName stores the full name "Jan Novák"; split on last space.
        if (!string.IsNullOrWhiteSpace(epo.AuthorizedPersonName))
        {
            var fullName  = epo.AuthorizedPersonName.Trim();
            var lastSpace = fullName.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                var firstName = fullName[..lastSpace];
                var lastName  = fullName[(lastSpace + 1)..];
                vetaP.Add(new XAttribute("opr_jmeno",    firstName.Length > 20 ? firstName[..20] : firstName));
                vetaP.Add(new XAttribute("opr_prijmeni", lastName.Length  > 36 ? lastName[..36]  : lastName));
            }
            else
            {
                // Single-word name — put it all in opr_jmeno.
                vetaP.Add(new XAttribute("opr_jmeno", fullName.Length > 20 ? fullName[..20] : fullName));
            }
        }

        return vetaP;
    }

    /// <summary>
    /// Returns true when <paramref name="taxNumber"/> looks like a Czech VAT ID,
    /// i.e. starts with the two-letter prefix "CZ" (case-insensitive) followed
    /// by at least one digit.
    ///
    /// This is the gate for A.4 / B.2: a foreign VAT number or a missing number
    /// must fall through to the A.5 / B.3 aggregate row.
    /// </summary>
    private static bool IsCzVatNumber(string? taxNumber)
        => !string.IsNullOrWhiteSpace(taxNumber)
            && taxNumber.StartsWith("CZ", StringComparison.OrdinalIgnoreCase)
            && taxNumber.Length > 2;

    /// <summary>
    /// Strips the "CZ" country prefix from a Czech VAT number and returns
    /// the numeric part only — the format required by the DPHKH1 XSD
    /// (<c>dic_odb</c> / <c>dic_dod</c> pattern: <c>[0-9]{1,10}</c>).
    ///
    /// Example: "CZ12345678" → "12345678".
    /// </summary>
    private static string StripCzPrefix(string taxNumber)
        => taxNumber.StartsWith("CZ", StringComparison.OrdinalIgnoreCase)
            ? taxNumber[2..]
            : taxNumber;

    /// <summary>
    /// Formats a decimal amount for DPHKH1 XML attributes.
    /// DPHKH1 XSD allows fractionDigits=2 (unlike DPHDP3 which uses integers),
    /// so we format with exactly 2 decimal places using invariant culture
    /// (EPO portal expects "." as the decimal separator, not ",").
    /// </summary>
    private static string FormatKhAmount(decimal amount)
        => amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    // =========================================================================
    // Private helpers — EPO settings
    // =========================================================================

    /// <summary>
    /// Carries validated EPO header fields loaded from <see cref="CompanySystemSettings"/>.
    /// All properties are guaranteed non-null after construction (missing fields cause
    /// <see cref="EpoHeaderIncompleteException"/> before this record is created).
    /// </summary>
    /// <param name="TaxOfficeCode">c_ufo — Czech Financial Administration tax office code (1–999).</param>
    /// <param name="TaxOfficeBranchCode">c_pracufo — territorial branch code.</param>
    /// <param name="ContactPhone">Phone number of the filing person (optional — null is allowed).</param>
    /// <param name="ContactEmail">Email of the filing person (optional — null is allowed).</param>
    /// <param name="AuthorizedPersonName">Full name of the authorized signatory (optional).</param>
    private sealed record EpoSettings(
        int TaxOfficeCode,
        int TaxOfficeBranchCode,
        string? ContactPhone,
        string? ContactEmail,
        string? AuthorizedPersonName);

    /// <summary>
    /// Loads EPO header settings from <see cref="CompanySystemSettings"/> in the master database
    /// for the current tenant and validates that all required fields are present.
    ///
    /// Required fields: <c>EpoTaxOfficeCode</c>, <c>EpoTaxOfficeBranchCode</c>.
    /// Optional fields: <c>EpoContactPhone</c>, <c>EpoContactEmail</c>, <c>EpoAuthorizedPersonName</c>.
    ///
    /// Throws <see cref="EpoHeaderIncompleteException"/> if any required field is missing,
    /// or <see cref="InvalidOperationException"/> if the company has no settings record.
    /// </summary>
    private async Task<EpoSettings> LoadAndValidateEpoSettingsAsync(CancellationToken ct)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException(
                "No CompanyId is available for the current request. " +
                "Ensure the user is authenticated and assigned to a company.");

        var settings = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, ct)
            ?? throw new InvalidOperationException(
                $"No CompanySystemSettings found for company {companyId}. " +
                "Contact SysAdmin to set up company settings before generating EPO exports.");

        // Collect all missing required fields so the UI can show them all at once.
        var missing = new List<string>();
        if (!settings.EpoTaxOfficeCode.HasValue)    missing.Add(nameof(settings.EpoTaxOfficeCode));
        if (!settings.EpoTaxOfficeBranchCode.HasValue) missing.Add(nameof(settings.EpoTaxOfficeBranchCode));

        if (missing.Count > 0)
            throw new EpoHeaderIncompleteException(missing);

        return new EpoSettings(
            TaxOfficeCode:        settings.EpoTaxOfficeCode!.Value,
            TaxOfficeBranchCode:  settings.EpoTaxOfficeBranchCode!.Value,
            ContactPhone:         settings.EpoContactPhone,
            ContactEmail:         settings.EpoContactEmail,
            AuthorizedPersonName: settings.EpoAuthorizedPersonName);
    }

    /// <summary>
    /// Lightweight value record to carry per-invoice data for A.4 and B.2 rows.
    /// </summary>
    /// <param name="DicCounterparty">Numeric part of the counterparty's CZ DIČ (no "CZ" prefix).</param>
    /// <param name="DocumentNumber">Invoice document number used as c_evid_dd.</param>
    /// <param name="Dppd">Date of tax liability in EPO format "D.M.RRRR".</param>
    /// <param name="StdBase">Tax base at standard rate (≥ 20 %) in CZK.</param>
    /// <param name="StdVat">VAT amount at standard rate in CZK.</param>
    /// <param name="RedBase">Tax base at reduced rate (< 20 %) in CZK.</param>
    /// <param name="RedVat">VAT amount at reduced rate in CZK.</param>
    private sealed record KhRow(
        string DicCounterparty,
        string DocumentNumber,
        string Dppd,
        decimal StdBase,
        decimal StdVat,
        decimal RedBase,
        decimal RedVat);

    /// <summary>
    /// Builds the complete DPHKH1 XDocument according to the EPO schema.
    ///
    /// Structure:
    ///   Pisemnost
    ///     DPHKH1
    ///       VetaD   — period metadata (rok, mesic/ctvrt, khdph_forma, dokument, k_uladis)
    ///       VetaP   — taxpayer identification (dic, c_ufo, typ_ds, zkrobchjm)
    ///       VetaA1* — PDP outputs — intentionally EMPTY (TODO #4)
    ///       VetaA4* — output invoices ≥ 10 000 CZK incl. VAT with CZ DIČ, one row per invoice
    ///       VetaA5? — aggregate of all other output invoices
    ///       VetaB1* — PDP inputs — intentionally EMPTY (TODO #4)
    ///       VetaB2* — input invoices ≥ 10 000 CZK incl. VAT with CZ DIČ, one row per invoice
    ///       VetaB3? — aggregate of all other input invoices
    ///
    /// DPHKH1 uses decimal amounts with 2 fraction digits (vs. integer in DPHDP3).
    /// Standard rate (≥ 20 %) maps to @zakl_dane1 / @dan1.
    /// Reduced rate (< 20 %) maps to @zakl_dane2 / @dan2.
    /// </summary>
    private static XDocument BuildDphkh1Xml(
        int year, int period, EVatPeriodType type,
        DateTime periodFrom, DateTime periodTo,
        Client issuer,
        EpoSettings epo,
        IReadOnlyList<KhRow> a4Rows,
        decimal a5StdBase, decimal a5StdVat,
        decimal a5RedBase, decimal a5RedVat,
        IReadOnlyList<KhRow> b2Rows,
        decimal b3StdBase, decimal b3StdVat,
        decimal b3RedBase, decimal b3RedVat)
    {
        var today = DateTime.Today.ToString(EpoDateFormat);

        // Build VetaD — period descriptor for DPHKH1.
        // Key differences from DPHDP3:
        //   dokument = "KH1" (fixed per XSD)
        //   khdph_forma = "B" (řádné kontrolní hlášení)
        //   No dapdph_forma / typ_platce here.
        var vetaD = new XElement("VetaD",
            new XAttribute("dokument",   "KH1"), // fixed per XSD
            new XAttribute("k_uladis",   "DPH"), // fixed per XSD
            new XAttribute("rok",        year.ToString()),
            new XAttribute("khdph_forma","B"),   // "B" = řádné (ordinary filing)
            new XAttribute("d_poddp",    today));

        // Monthly → @mesic; Quarterly → @ctvrt. Never both.
        if (type == EVatPeriodType.Monthly)
            vetaD.Add(new XAttribute("mesic", period.ToString()));
        else
            vetaD.Add(new XAttribute("ctvrt", period.ToString()));

        // Build VetaP — taxpayer identification (shared logic with DPHDP3).
        var vetaP = BuildVetaP(issuer, epo);

        // Build DPHKH1 element — element order per XSD:
        // VetaD, VetaP, VetaA1*, VetaA2*, VetaA3*, VetaA4*, VetaA5?, VetaB1*, VetaB2*, VetaB3?, VetaC?
        var dphkh1 = new XElement("DPHKH1", vetaD, vetaP);

        // TODO #4 PDP — A.1/B.1 will be filled when EVatRegime is introduced.
        // For now, we emit no VetaA1 / VetaB1 elements (minOccurs=0, so omitting is valid).

        // VetaA4 — one element per qualifying output invoice.
        foreach (var row in a4Rows)
        {
            var vetaA4 = new XElement("VetaA4",
                new XAttribute("dic_odb",     row.DicCounterparty),
                new XAttribute("c_evid_dd",   row.DocumentNumber),
                new XAttribute("dppd",        row.Dppd),
                new XAttribute("kod_rezim_pl","0"),  // "0" = běžné plnění (ordinary supply)
                new XAttribute("zdph_44",     "N")); // "N" = not a bad-debt correction

            // Standard-rate amounts (optional in XSD — omit when zero).
            if (row.StdBase != 0m || row.StdVat != 0m)
            {
                vetaA4.Add(new XAttribute("zakl_dane1", FormatKhAmount(row.StdBase)));
                vetaA4.Add(new XAttribute("dan1",       FormatKhAmount(row.StdVat)));
            }

            // Reduced-rate amounts.
            if (row.RedBase != 0m || row.RedVat != 0m)
            {
                vetaA4.Add(new XAttribute("zakl_dane2", FormatKhAmount(row.RedBase)));
                vetaA4.Add(new XAttribute("dan2",       FormatKhAmount(row.RedVat)));
            }

            dphkh1.Add(vetaA4);
        }

        // VetaA5 — aggregate row for all other output invoices (minOccurs=0 in XSD).
        // Emit only when at least one amount is non-zero.
        if (a5StdBase != 0m || a5StdVat != 0m || a5RedBase != 0m || a5RedVat != 0m)
        {
            var vetaA5 = new XElement("VetaA5");

            if (a5StdBase != 0m || a5StdVat != 0m)
            {
                vetaA5.Add(new XAttribute("zakl_dane1", FormatKhAmount(a5StdBase)));
                vetaA5.Add(new XAttribute("dan1",       FormatKhAmount(a5StdVat)));
            }

            if (a5RedBase != 0m || a5RedVat != 0m)
            {
                vetaA5.Add(new XAttribute("zakl_dane2", FormatKhAmount(a5RedBase)));
                vetaA5.Add(new XAttribute("dan2",       FormatKhAmount(a5RedVat)));
            }

            dphkh1.Add(vetaA5);
        }

        // VetaB2 — one element per qualifying input invoice.
        foreach (var row in b2Rows)
        {
            var vetaB2 = new XElement("VetaB2",
                new XAttribute("dic_dod",  row.DicCounterparty),
                new XAttribute("c_evid_dd",row.DocumentNumber),
                new XAttribute("dppd",     row.Dppd),
                new XAttribute("pomer",    "N"), // "N" = no proportional deduction (§ 75)
                new XAttribute("zdph_44",  "N")); // "N" = not a bad-debt correction

            if (row.StdBase != 0m || row.StdVat != 0m)
            {
                vetaB2.Add(new XAttribute("zakl_dane1", FormatKhAmount(row.StdBase)));
                vetaB2.Add(new XAttribute("dan1",       FormatKhAmount(row.StdVat)));
            }

            if (row.RedBase != 0m || row.RedVat != 0m)
            {
                vetaB2.Add(new XAttribute("zakl_dane2", FormatKhAmount(row.RedBase)));
                vetaB2.Add(new XAttribute("dan2",       FormatKhAmount(row.RedVat)));
            }

            dphkh1.Add(vetaB2);
        }

        // VetaB3 — aggregate row for all other input invoices.
        if (b3StdBase != 0m || b3StdVat != 0m || b3RedBase != 0m || b3RedVat != 0m)
        {
            var vetaB3 = new XElement("VetaB3");

            if (b3StdBase != 0m || b3StdVat != 0m)
            {
                vetaB3.Add(new XAttribute("zakl_dane1", FormatKhAmount(b3StdBase)));
                vetaB3.Add(new XAttribute("dan1",       FormatKhAmount(b3StdVat)));
            }

            if (b3RedBase != 0m || b3RedVat != 0m)
            {
                vetaB3.Add(new XAttribute("zakl_dane2", FormatKhAmount(b3RedBase)));
                vetaB3.Add(new XAttribute("dan2",       FormatKhAmount(b3RedVat)));
            }

            dphkh1.Add(vetaB3);
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("Pisemnost", dphkh1));
    }
}
