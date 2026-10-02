using System.Text;
using System.Xml.Linq;
using System.Xml.Schema;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="VatReportService.ExportEpoVatReturnAsync"/>.
///
/// Test categories:
///   1. Input validation — year/period range checks.
///   2. XSD validity — generated XML must pass the 2026 DPHDP3 schema.
///   3. Row mapping — Veta1 rows 1/2 (output VAT) and Veta4 rows 40/41/51 (input VAT).
///   4. Empty period — no invoices → document still valid (EPO requires filing even for zero periods).
///   5. Status exclusion — Draft/Deleted invoices must not appear.
///   6. EUR → CZK FX conversion via ICurrencyService.
///   7. Number format — integer amounts, EPO date format "D.M.RRRR".
///   8. Consistency — sum of row 51 equals GetReportAsync total input VAT.
///   9. EPO header — c_ufo/c_pracufo come from CompanySystemSettings (issue #39).
/// </summary>
public class EpoVatReturnExportTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly VatReportService _service;
    private readonly IEpoSchemaProvider _schemaProvider;
    private readonly ICurrencyService _currencyService;

    // CZK currency ID used across all seeded invoices.
    private const long CzkCurrencyId = 1;
    // EUR currency ID for FX tests.
    private const long EurCurrencyId = 2;

    // CompanyId used in both master CompanySystemSettings and tenant issuer.
    // VatReportService resolves EPO settings by this ID via ITenantResolver.
    private const long CompanyId = 2L;

    public EpoVatReturnExportTests()
    {
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(tenantOptions);

        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _masterContext = new MasterDbContext(masterOptions);

        // Real XSD provider — validates against the actual 2026 DPHDP3 schema.
        _schemaProvider = new EpoSchemaProvider(AppContext.BaseDirectory);

        // Default stub: ConvertToCzkAsync is a pass-through (all-CZK scenario).
        _currencyService = Substitute.For<ICurrencyService>();
        _currencyService
            .ConvertToCzkAsync(
                Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<decimal>(0)));

        // ITenantResolver stub: returns CompanyId so LoadAndValidateEpoSettingsAsync
        // can look up CompanySystemSettings in the master DB.
        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(CompanyId);

        var logger = Substitute.For<ILogger<VatReportService>>();
        _service = new VatReportService(
            _context, _masterContext, tenantResolver,
            _schemaProvider, _currencyService, logger);

        SeedBaseData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Seeds minimal data:
    ///  - CZK and EUR currencies in the tenant DB.
    ///  - Customer (IsIssuer=false) and Issuer (IsIssuer=true) in the tenant DB.
    ///  - CompanySystemSettings with EPO header fields in the master DB.
    ///
    /// EPO header values:
    ///   EpoTaxOfficeCode = 451  → VetaP/@c_ufo = "451"
    ///   EpoTaxOfficeBranchCode = 2017 → VetaP/@c_pracufo = "2017"
    /// </summary>
    private void SeedBaseData()
    {
        _context.Currency.AddRange(
            new Currency
            {
                Id = CzkCurrencyId, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
                DecimalPlaces = 2, SortOrder = 1, IsActive = true
            },
            new Currency
            {
                Id = EurCurrencyId, Code = "EUR", Name = "Euro", Symbol = "€",
                DecimalPlaces = 2, SortOrder = 2, IsActive = true
            });
        _context.SaveChanges();

        // Customer (regular client — receives invoices from us).
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Customer s.r.o.", RegistrationNumber = "11111111",
            IsIssuer = false, IsActive = true
        });
        _context.SaveChanges();

        // Issuer — our company (IsIssuer = true).
        // TaxNumber "CZ12345678" → EPO dic = "12345678" (strip "CZ").
        _context.Client.Add(new Client
        {
            Id = CompanyId,
            CompanyName = "Vzorová Firma s.r.o.",
            RegistrationNumber = "12345678",
            TaxNumber = "CZ12345678",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true
        });
        _context.SaveChanges();

        // CompanySystemSettings in master DB — contains EPO header fields (issue #39).
        // VatReportService reads these via ITenantResolver.GetCurrentCompanyId().
        _masterContext.CompanySystemSettings.Add(new CompanySystemSettings
        {
            Id = 1,
            CompanyId = CompanyId,
            SchemaName = "tenant_2",
            IsProvisioned = true,
            IsActive = true,
            EpoTaxOfficeCode = 451,       // c_ufo
            EpoTaxOfficeBranchCode = 2017 // c_pracufo
        });
        _masterContext.SaveChanges();
    }

    /// <summary>Seeds a CZK issued invoice with one line item.</summary>
    private void SeedIssuedInvoice(
        DateTime duzp,
        EInvoiceStatus status,
        decimal baseAmount,
        decimal vatPct,
        long currencyId = CzkCurrencyId,
        EDocumentType docType = EDocumentType.Invoice)
    {
        var invoice = new Invoice
        {
            DocumentType = docType,
            Status = status,
            DocumentNumber = $"INV-{Guid.NewGuid():N}",
            IssueDate = duzp,
            TaxableSupplyDate = duzp,
            DueDate = duzp.AddDays(14),
            ClientId = 1,
            IssuerId = CompanyId,
            Issuer = _context.Client.Find(CompanyId)!,
            CurrencyId = currencyId,
            TotalBeforeVat = baseAmount,
            TotalVat = baseAmount * (vatPct / 100m),
            TotalWithVat = baseAmount * (1 + vatPct / 100m),
            InvoiceItem = new List<InvoiceItem>()
        };

        invoice.InvoiceItem.Add(new InvoiceItem
        {
            OrderIndex = 1,
            Description = "Test item",
            Quantity = 1,
            UnitPrice = baseAmount,
            VatRatePercentage = vatPct,
            TotalBeforeVat = baseAmount,
            VatAmount = baseAmount * (vatPct / 100m),
            TotalWithVat = baseAmount * (1 + vatPct / 100m)
        });

        _context.Invoice.Add(invoice);
        _context.SaveChanges();
    }

    /// <summary>Seeds a CZK received invoice with one line item.</summary>
    private void SeedReceivedInvoice(
        DateTime duzp,
        EReceivedInvoiceStatus status,
        decimal baseAmount,
        decimal vatPct,
        long currencyId = CzkCurrencyId)
    {
        var received = new ReceivedInvoice
        {
            DocumentNumber = $"REC-{Guid.NewGuid():N}",
            Status = status,
            SupplierId = 1,
            IssueDate = duzp,
            ReceivedDate = duzp,
            TaxableSupplyDate = duzp,
            DueDate = duzp.AddDays(30),
            CurrencyId = currencyId,
            TotalBeforeVat = baseAmount,
            TotalVat = baseAmount * (vatPct / 100m),
            TotalWithVat = baseAmount * (1 + vatPct / 100m)
        };

        received.Items.Add(new ReceivedInvoiceItem
        {
            OrderIndex = 1,
            Description = "Test expense",
            Quantity = 1,
            UnitPrice = baseAmount,
            VatRatePercentage = vatPct,
            TotalBeforeVat = baseAmount,
            VatAmount = baseAmount * (vatPct / 100m),
            TotalWithVat = baseAmount * (1 + vatPct / 100m)
        });

        _context.ReceivedInvoice.Add(received);
        _context.SaveChanges();
    }

    /// <summary>Parses the byte[] result and validates it against the 2026 DPHDP3 XSD.</summary>
    private (XDocument doc, List<string> xsdErrors) ParseAndValidate(byte[] bytes)
    {
        var xml = Encoding.UTF8.GetString(bytes);
        var doc = XDocument.Parse(xml);
        var schemaSet = _schemaProvider.GetSchemaSet(EEpoFormType.VatReturn, 2026);
        var errors = new List<string>();
        doc.Validate(schemaSet, (_, e) => errors.Add(e.Message));
        return (doc, errors);
    }

    // =========================================================================
    // 1. Input validation
    // =========================================================================

    [Fact]
    public async Task ExportEpoVatReturnAsync_YearTooOld_ThrowsArgumentOutOfRange()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => _service.ExportEpoVatReturnAsync(2023, 1, EVatPeriodType.Monthly));
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_YearTooFarAhead_ThrowsArgumentOutOfRange()
    {
        var futureYear = DateTime.UtcNow.Year + 2;
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => _service.ExportEpoVatReturnAsync(futureYear, 1, EVatPeriodType.Monthly));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task ExportEpoVatReturnAsync_MonthlyPeriodOutOfRange_ThrowsArgumentOutOfRange(int period)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => _service.ExportEpoVatReturnAsync(2026, period, EVatPeriodType.Monthly));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task ExportEpoVatReturnAsync_QuarterlyPeriodOutOfRange_ThrowsArgumentOutOfRange(int period)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => _service.ExportEpoVatReturnAsync(2026, period, EVatPeriodType.Quarterly));
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_ValidMonthlyBoundary_DoesNotThrow()
    {
        // period=1 (January) and period=12 (December) are both valid.
        await Should.NotThrowAsync(() => _service.ExportEpoVatReturnAsync(2026, 1, EVatPeriodType.Monthly));
        await Should.NotThrowAsync(() => _service.ExportEpoVatReturnAsync(2026, 12, EVatPeriodType.Monthly));
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_ValidQuarterlyBoundary_DoesNotThrow()
    {
        // period=1 (Q1) and period=4 (Q4) are both valid.
        await Should.NotThrowAsync(() => _service.ExportEpoVatReturnAsync(2026, 1, EVatPeriodType.Quarterly));
        await Should.NotThrowAsync(() => _service.ExportEpoVatReturnAsync(2026, 4, EVatPeriodType.Quarterly));
    }

    // =========================================================================
    // 9. EPO header — c_ufo / c_pracufo from CompanySystemSettings (#39)
    // =========================================================================

    [Fact]
    public async Task ExportEpoVatReturnAsync_IssuerIsNotVatPayer_ThrowsVatPayerRequiredException()
    {
        // When the issuer has IsVatPayer = false, ExportEpoVatReturnAsync must throw
        // VatPayerRequiredException so the controller can return HTTP 403 VAT_PAYER_REQUIRED.
        // This test uses a fresh in-memory DB where the issuer has IsVatPayer = false.
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var tenantCtx = new TenantDbContext(tenantOptions);

        // Seed currency, customer, and non-VAT-payer issuer.
        tenantCtx.Currency.Add(new Currency
        {
            Id = CzkCurrencyId, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        tenantCtx.Client.Add(new Client
        {
            Id = 1, CompanyName = "Customer", RegistrationNumber = "11111111",
            IsIssuer = false, IsActive = true
        });
        // Issuer with IsVatPayer = false — the flag under test.
        tenantCtx.Client.Add(new Client
        {
            Id = CompanyId, CompanyName = "Non-VAT Issuer", RegistrationNumber = "22222222",
            IsIssuer = true, IsActive = true, IsVatPayer = false
        });
        await tenantCtx.SaveChangesAsync();

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(CompanyId);

        var logger = Substitute.For<ILogger<VatReportService>>();
        var service = new VatReportService(
            tenantCtx, _masterContext, tenantResolver,
            _schemaProvider, _currencyService, logger);

        await Should.ThrowAsync<VatPayerRequiredException>(
            () => service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly));
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_MissingEpoTaxOfficeCode_ThrowsEpoHeaderIncomplete()
    {
        // When CompanySystemSettings.EpoTaxOfficeCode is null the service must throw
        // EpoHeaderIncompleteException (not InvalidOperationException) so the controller
        // can return HTTP 400 with code EPO_HEADER_INCOMPLETE.
        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var masterCtx = new MasterDbContext(masterOptions);

        // Settings with EpoTaxOfficeCode missing but EpoTaxOfficeBranchCode set.
        masterCtx.CompanySystemSettings.Add(new CompanySystemSettings
        {
            Id = 99, CompanyId = CompanyId, SchemaName = "tenant_2",
            IsProvisioned = true, IsActive = true,
            EpoTaxOfficeCode = null,   // missing → must appear in MissingFields
            EpoTaxOfficeBranchCode = 2017
        });
        await masterCtx.SaveChangesAsync();

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(CompanyId);

        var logger = Substitute.For<ILogger<VatReportService>>();
        var service = new VatReportService(
            _context, masterCtx, tenantResolver,
            _schemaProvider, _currencyService, logger);

        var ex = await Should.ThrowAsync<EpoHeaderIncompleteException>(
            () => service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly));

        ex.MissingFields.ShouldContain("EpoTaxOfficeCode");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_MissingEpoTaxOfficeBranchCode_ThrowsEpoHeaderIncomplete()
    {
        // When EpoTaxOfficeBranchCode (c_pracufo) is null the service must throw
        // EpoHeaderIncompleteException with that field name.
        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var masterCtx = new MasterDbContext(masterOptions);

        masterCtx.CompanySystemSettings.Add(new CompanySystemSettings
        {
            Id = 99, CompanyId = CompanyId, SchemaName = "tenant_2",
            IsProvisioned = true, IsActive = true,
            EpoTaxOfficeCode = 451,
            EpoTaxOfficeBranchCode = null  // missing → must appear in MissingFields
        });
        await masterCtx.SaveChangesAsync();

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(CompanyId);

        var logger = Substitute.For<ILogger<VatReportService>>();
        var service = new VatReportService(
            _context, masterCtx, tenantResolver,
            _schemaProvider, _currencyService, logger);

        var ex = await Should.ThrowAsync<EpoHeaderIncompleteException>(
            () => service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly));

        ex.MissingFields.ShouldContain("EpoTaxOfficeBranchCode");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_BothRequiredFieldsMissing_ListsBothInException()
    {
        // When both required fields are missing, the exception must report both
        // so the UI can show the user all missing fields at once.
        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var masterCtx = new MasterDbContext(masterOptions);

        masterCtx.CompanySystemSettings.Add(new CompanySystemSettings
        {
            Id = 99, CompanyId = CompanyId, SchemaName = "tenant_2",
            IsProvisioned = true, IsActive = true,
            EpoTaxOfficeCode = null,
            EpoTaxOfficeBranchCode = null
        });
        await masterCtx.SaveChangesAsync();

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(CompanyId);

        var logger = Substitute.For<ILogger<VatReportService>>();
        var service = new VatReportService(
            _context, masterCtx, tenantResolver,
            _schemaProvider, _currencyService, logger);

        var ex = await Should.ThrowAsync<EpoHeaderIncompleteException>(
            () => service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly));

        ex.MissingFields.ShouldContain("EpoTaxOfficeCode");
        ex.MissingFields.ShouldContain("EpoTaxOfficeBranchCode");
        ex.MissingFields.Count.ShouldBe(2);
    }

    // =========================================================================
    // 2. XSD validity — generated XML must validate against 2026 DPHDP3 schema
    // =========================================================================

    [Fact]
    public async Task ExportEpoVatReturnAsync_EmptyPeriod_ValidatesAgainstXsd()
    {
        // No invoices → only VetaD + VetaP (Veta1 / Veta4 omitted).
        // XSD allows Veta1 minOccurs=0 — an empty period is legal.
        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);

        var (_, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty("An empty period should still produce XSD-valid DPHDP3 XML.");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_WithInvoices_ValidatesAgainstXsd()
    {
        // One standard-rate issued invoice + one reduced-rate received invoice.
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 5000m, 21m);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 2000m, 12m);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);

        var (_, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty("A non-empty period with rows 1/2/40/41/51 should be XSD-valid.");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_QuarterlyPeriod_ValidatesAgainstXsd()
    {
        // Quarterly periods use VetaD/@ctvrt instead of @mesic.
        var duzp = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Paid, 1000m, 21m);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 1, EVatPeriodType.Quarterly);

        var (_, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty("Quarterly DPHDP3 XML should be XSD-valid.");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_ReturnsUtf8Bytes()
    {
        // The returned bytes must be valid UTF-8 (no BOM).
        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);

        // BOM check: UTF-8 BOM is EF BB BF — EPO portal does not accept BOM.
        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF },
            "DPHDP3 bytes must NOT start with a UTF-8 BOM.");

        // Verify it is decodable as UTF-8.
        var xml = Encoding.UTF8.GetString(bytes);
        xml.ShouldContain("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.ShouldContain("<Pisemnost>");
        xml.ShouldContain("<DPHDP3>");
    }

    // =========================================================================
    // 3. Row mapping — output VAT (rows 1, 2) and input VAT (rows 40, 41, 51)
    // =========================================================================

    [Fact]
    public async Task ExportEpoVatReturnAsync_CreditNote_ReducesRows1And2InItsOwnPeriod()
    {
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 1000m, 21m);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 2000m, 12m);
        // Credit notes stored positive — forced negative. The April one must not leak into March.
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 400m, 21m, docType: EDocumentType.CreditNote);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 500m, 12m, docType: EDocumentType.CreditNote);
        SeedIssuedInvoice(duzp.AddMonths(1), EInvoiceStatus.Completed, 900m, 21m, docType: EDocumentType.CreditNote);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var veta1 = doc.Descendants("Veta1").Single();
        veta1.Attribute("obrat23")!.Value.ShouldBe("600");
        veta1.Attribute("dan23")!.Value.ShouldBe("126");
        veta1.Attribute("obrat5")!.Value.ShouldBe("1500");
        veta1.Attribute("dan5")!.Value.ShouldBe("180");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_CreditNoteWithMixedSignRows_UsesNegativeNet()
    {
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, -1000m, 21m, docType: EDocumentType.CreditNote);
        var cn = _context.Invoice.Include(i => i.InvoiceItem).Single();
        cn.InvoiceItem.Add(new InvoiceItem
        {
            OrderIndex = 2, Description = "Positive row", Quantity = 1, UnitPrice = 600m,
            VatRatePercentage = 21m, TotalBeforeVat = 600m, VatAmount = 126m, TotalWithVat = 726m
        });
        _context.SaveChanges();

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var veta1 = doc.Descendants("Veta1").Single();
        veta1.Attribute("obrat23")!.Value.ShouldBe("-400");
        veta1.Attribute("dan23")!.Value.ShouldBe("-84");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_EurCreditNote_IsConvertedAtItsOwnDuzpAndNegative()
    {
        // Only the credit note's own DUZP has a rate (25) — any other date would give 1:1.
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        _currencyService
            .ConvertToCzkAsync(Arg.Any<decimal>(), "EUR", DateOnly.FromDateTime(duzp), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<decimal>(0) * 25m));
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 100m, 21m, EurCurrencyId, EDocumentType.CreditNote);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var veta1 = doc.Descendants("Veta1").Single();
        veta1.Attribute("obrat23")!.Value.ShouldBe("-2500");
        veta1.Attribute("dan23")!.Value.ShouldBe("-525");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_StandardRateInvoice_MapsToRow1()
    {
        // 1 000 CZK base × 21% → obrat23=1000, dan23=210 in Veta1.
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 1000m, 21m);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var veta1 = doc.Descendants("Veta1").Single();
        veta1.Attribute("obrat23")!.Value.ShouldBe("1000");
        veta1.Attribute("dan23")!.Value.ShouldBe("210");
        // Reduced-rate attributes should be absent when there are no 12%-rate items.
        veta1.Attribute("obrat5").ShouldBeNull();
        veta1.Attribute("dan5").ShouldBeNull();
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_ReducedRateInvoice_MapsToRow2()
    {
        // 2 000 CZK base × 12% → obrat5=2000, dan5=240 in Veta1.
        var duzp = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Paid, 2000m, 12m);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var veta1 = doc.Descendants("Veta1").Single();
        veta1.Attribute("obrat5")!.Value.ShouldBe("2000");
        veta1.Attribute("dan5")!.Value.ShouldBe("240");
        veta1.Attribute("obrat23").ShouldBeNull();
        veta1.Attribute("dan23").ShouldBeNull();
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_InputVat_StandardRate_MapsToRow40()
    {
        // 3 000 CZK base × 21% received invoice → pln23=3000, odp_tuz23_nar=630 in Veta4.
        var duzp = new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 3000m, 21m);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var veta4 = doc.Descendants("Veta4").Single();
        veta4.Attribute("pln23")!.Value.ShouldBe("3000");
        veta4.Attribute("odp_tuz23_nar")!.Value.ShouldBe("630");
        veta4.Attribute("pln5").ShouldBeNull();
        veta4.Attribute("odp_tuz5_nar").ShouldBeNull();
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_InputVat_ReducedRate_MapsToRow41()
    {
        // 1 500 CZK base × 12% received invoice → pln5=1500, odp_tuz5_nar=180 in Veta4.
        var duzp = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Paid, 1500m, 12m);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var veta4 = doc.Descendants("Veta4").Single();
        veta4.Attribute("pln5")!.Value.ShouldBe("1500");
        veta4.Attribute("odp_tuz5_nar")!.Value.ShouldBe("180");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_Row51_IsSumOfRows40And41()
    {
        // Row 51 (odp_sum_nar) must equal the sum of row 40 VAT + row 41 VAT.
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 3000m, 21m); // row 40: 630
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Paid,     1500m, 12m); // row 41: 180

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var veta4 = doc.Descendants("Veta4").Single();
        var row40Vat = long.Parse(veta4.Attribute("odp_tuz23_nar")!.Value);
        var row41Vat = long.Parse(veta4.Attribute("odp_tuz5_nar")!.Value);
        var row51    = long.Parse(veta4.Attribute("odp_sum_nar")!.Value);

        row51.ShouldBe(row40Vat + row41Vat, "Row 51 (odp_sum_nar) must equal row40.vat + row41.vat.");
    }

    // =========================================================================
    // 4. Empty period — Veta1 / Veta4 must be absent
    // =========================================================================

    [Fact]
    public async Task ExportEpoVatReturnAsync_EmptyPeriod_Veta1Absent()
    {
        // With no invoices, the XML must NOT include Veta1 or Veta4
        // (EPO accepts an empty return; including zero-valued Veta1 is also valid,
        // but omitting it is the simpler and preferred approach).
        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("Veta1").ShouldBeEmpty();
        doc.Descendants("Veta4").ShouldBeEmpty();
    }

    // =========================================================================
    // 5. Status exclusion
    // =========================================================================

    [Fact]
    public async Task ExportEpoVatReturnAsync_DraftInvoice_IsExcluded()
    {
        // Draft invoice must be excluded — same logic as GetReportAsync.
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Draft,     5000m, 21m); // excluded
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 1000m, 21m); // included

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var veta1 = doc.Descendants("Veta1").Single();
        // Only the Completed invoice's base should appear (not 5000+1000=6000).
        veta1.Attribute("obrat23")!.Value.ShouldBe("1000");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_DeletedInvoice_IsExcluded()
    {
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Deleted,   9000m, 21m); // excluded
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed,  500m, 21m); // included

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var veta1 = doc.Descendants("Veta1").Single();
        veta1.Attribute("obrat23")!.Value.ShouldBe("500");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_ReceivedStatus_ExcludedFromInputVat()
    {
        // Received invoice in status Received/Rejected must not appear in input VAT.
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Received, 9000m, 21m); // excluded
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved,  500m, 21m); // included

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var veta4 = doc.Descendants("Veta4").Single();
        veta4.Attribute("pln23")!.Value.ShouldBe("500");
    }

    // =========================================================================
    // 6. EUR → CZK FX conversion
    // =========================================================================

    [Fact]
    public async Task ExportEpoVatReturnAsync_EurInvoice_IsConvertedToCzk()
    {
        // EUR invoice with base = 1 000 EUR at exchange rate 25 CZK/EUR → 25 000 CZK.
        // The currency service returns 25× the amount (simulating a 1:25 EUR/CZK rate).
        _currencyService
            .ConvertToCzkAsync(Arg.Any<decimal>(), "EUR", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<decimal>(0) * 25m));

        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 1000m, 21m, EurCurrencyId);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var veta1 = doc.Descendants("Veta1").Single();
        // base = 1000 EUR × 25 = 25 000 CZK; vat = 210 EUR × 25 = 5 250 CZK
        veta1.Attribute("obrat23")!.Value.ShouldBe("25000");
        veta1.Attribute("dan23")!.Value.ShouldBe("5250");
    }

    // =========================================================================
    // 7. Number format and date format
    // =========================================================================

    [Fact]
    public async Task ExportEpoVatReturnAsync_AmountsAreIntegers()
    {
        // VAT amounts in EPO must be whole crowns (no decimal separator).
        // Test: 333.33 CZK base at 21% → dan23 = 70 (round away from zero).
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 333.33m, 21m);

        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var veta1 = doc.Descendants("Veta1").Single();
        // Amount strings must not contain a decimal separator.
        veta1.Attribute("obrat23")!.Value.ShouldNotContain(".");
        veta1.Attribute("dan23")!.Value.ShouldNotContain(".");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_PeriodDates_UseEpoFormat()
    {
        // EPO dates must be "D.M.RRRR" — no leading zeroes on day or month.
        // For March 2026: zdobd_od = "1.3.2026", zdobd_do = "31.3.2026".
        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var vetaD = doc.Descendants("VetaD").Single();
        vetaD.Attribute("zdobd_od")!.Value.ShouldBe("1.3.2026");
        vetaD.Attribute("zdobd_do")!.Value.ShouldBe("31.3.2026");
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_MonthlyPeriod_HasMesicAttribute()
    {
        // Monthly period → VetaD/@mesic is set, @ctvrt is absent.
        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var vetaD = doc.Descendants("VetaD").Single();
        vetaD.Attribute("mesic")!.Value.ShouldBe("3");
        vetaD.Attribute("ctvrt").ShouldBeNull();
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_QuarterlyPeriod_HasCtvrAttribute()
    {
        // Quarterly period → VetaD/@ctvrt is set, @mesic is absent.
        var bytes = await _service.ExportEpoVatReturnAsync(2026, 1, EVatPeriodType.Quarterly);
        var (doc, _) = ParseAndValidate(bytes);

        var vetaD = doc.Descendants("VetaD").Single();
        vetaD.Attribute("ctvrt")!.Value.ShouldBe("1");
        vetaD.Attribute("mesic").ShouldBeNull();
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_VetaP_HasCorrectDic()
    {
        // VetaP/@dic must be the numeric part of DIČ (strip "CZ" prefix).
        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var vetaP = doc.Descendants("VetaP").Single();
        vetaP.Attribute("dic")!.Value.ShouldBe("12345678");  // stripped from "CZ12345678"
    }

    [Fact]
    public async Task ExportEpoVatReturnAsync_VetaP_HasCorrectCUfoAndCPracufo()
    {
        // VetaP/@c_ufo and @c_pracufo must come from CompanySystemSettings (issue #39).
        // Seeded values: EpoTaxOfficeCode=451, EpoTaxOfficeBranchCode=2017.
        var bytes = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var vetaP = doc.Descendants("VetaP").Single();
        vetaP.Attribute("c_ufo")!.Value.ShouldBe("451");
        vetaP.Attribute("c_pracufo")!.Value.ShouldBe("2017");
    }

    // =========================================================================
    // 8. Consistency with GetReportAsync
    // =========================================================================

    [Fact]
    public async Task ExportEpoVatReturnAsync_TotalsMatchGetReportAsync()
    {
        // Seed mixed invoices and received invoices.
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 10000m, 21m);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Paid,       5000m, 12m);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 3000m, 21m);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 2000m, 12m);

        // EPO export.
        var bytes  = await _service.ExportEpoVatReturnAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);
        errors.ShouldBeEmpty();

        // GetReportAsync for the same period (March 2026).
        var from = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);
        var report = await _service.GetReportAsync(from, to);

        // Row 1 (obrat23 + dan23) + Row 2 (obrat5 + dan5) must equal total output VAT.
        var veta1 = doc.Descendants("Veta1").Single();
        var xmlOutVat = long.Parse(veta1.Attribute("dan23")!.Value)
                      + long.Parse(veta1.Attribute("dan5")!.Value);

        var reportOutVat = (long)Math.Round(report.TotalOutputVat, MidpointRounding.AwayFromZero);
        xmlOutVat.ShouldBe(reportOutVat,
            "Total output VAT in DPHDP3 XML must match GetReportAsync.TotalOutputVat.");

        // Row 51 (odp_sum_nar) must equal total input VAT.
        var veta4 = doc.Descendants("Veta4").Single();
        var xmlInVat = long.Parse(veta4.Attribute("odp_sum_nar")!.Value);
        var reportInVat = (long)Math.Round(report.TotalInputVat, MidpointRounding.AwayFromZero);
        xmlInVat.ShouldBe(reportInVat,
            "Row 51 (odp_sum_nar) in DPHDP3 XML must match GetReportAsync.TotalInputVat.");
    }
}
