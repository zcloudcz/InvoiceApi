using System.Text;
using System.Text.RegularExpressions;
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
/// Unit tests for <see cref="VatReportService.ExportEpoControlStatementAsync"/>.
///
/// Test categories:
///   1. Input validation — year/period range checks (shared with DPHDP3, re-verified here).
///   2. XSD validity — generated XML must pass the 2026 DPHKH1 schema.
///   3. Section A.4 / A.5 — classification of output invoices by 10 000 CZK threshold and CZ DIČ.
///   4. Section B.2 / B.3 — classification of input (received) invoices.
///   5. Status exclusion — Draft/Deleted invoices must not appear.
///   6. FX conversion — EUR invoices are converted to CZK before classification.
///   7. Consistency — A.4 + A.5 totals match GetReportAsync output VAT.
///   8. A.1 / B.1 — absent when there are no reverse charge items.
/// </summary>
public class EpoControlStatementExportTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly VatReportService _service;
    private readonly IEpoSchemaProvider _schemaProvider;
    private readonly ICurrencyService _currencyService;

    private const long CzkCurrencyId = 1;
    private const long EurCurrencyId = 2;

    // Issuer seeded with ID=2 (same pattern as EpoVatReturnExportTests).
    // Also used as CompanyId in CompanySystemSettings (issue #39).
    private const long IssuerId = 2;
    // Regular customer with CZ VAT number — eligible for A.4.
    private const long CustomerCzId = 1;
    // Second customer — foreign or no VAT number, always goes to A.5.
    private const long CustomerForeignId = 3;

    public EpoControlStatementExportTests()
    {
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(tenantOptions);

        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _masterContext = new MasterDbContext(masterOptions);

        _schemaProvider = new EpoSchemaProvider(AppContext.BaseDirectory);

        // Default stub: pass-through (all-CZK scenario).
        _currencyService = Substitute.For<ICurrencyService>();
        _currencyService
            .ConvertToCzkAsync(
                Arg.Any<decimal>(), Arg.Any<string>(),
                Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<decimal>(0)));

        // ITenantResolver stub: returns IssuerId so the service resolves CompanySystemSettings
        // from the master DB for EPO header fields (issue #39).
        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns((long?)IssuerId);

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
    /// Seeds currencies, issuer, two customers (tenant DB) and CompanySystemSettings (master DB).
    ///
    /// EPO header values in CompanySystemSettings (issue #39):
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

        // Customer with Czech VAT number — qualifies for A.4 when >= 10 000 CZK incl. VAT.
        _context.Client.Add(new Client
        {
            Id = CustomerCzId,
            CompanyName = "Czech Customer s.r.o.",
            RegistrationNumber = "11111111",
            TaxNumber = "CZ11111111",
            IsIssuer = false,
            IsActive = true
        });
        _context.SaveChanges();

        // Issuer — our company.
        _context.Client.Add(new Client
        {
            Id = IssuerId,
            CompanyName = "Vzorová Firma s.r.o.",
            RegistrationNumber = "12345678",
            TaxNumber = "CZ12345678",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true
        });
        _context.SaveChanges();

        // Foreign customer — no CZ VAT number, always goes to A.5.
        _context.Client.Add(new Client
        {
            Id = CustomerForeignId,
            CompanyName = "SK Supplier a.s.",
            RegistrationNumber = "SK9999999",
            TaxNumber = "SK9999999",
            IsIssuer = false,
            IsActive = true
        });
        _context.SaveChanges();

        // CompanySystemSettings in master DB — EPO header fields (issue #39).
        // VatReportService reads these via ITenantResolver.GetCurrentCompanyId() = IssuerId.
        _masterContext.CompanySystemSettings.Add(new CompanySystemSettings
        {
            Id = 1,
            CompanyId = IssuerId,
            SchemaName = "tenant_2",
            IsProvisioned = true,
            IsActive = true,
            EpoTaxOfficeCode = 451,       // c_ufo
            EpoTaxOfficeBranchCode = 2017 // c_pracufo
        });
        _masterContext.SaveChanges();
    }

    /// <summary>Seeds an issued invoice with a single line item.</summary>
    /// <param name="totalWithVat">
    /// The total incl. VAT that controls the 10 000 CZK threshold.
    /// The base is back-calculated from vatPct so that base + vat = totalWithVat.
    /// </param>
    private void SeedIssuedInvoice(
        DateTime duzp,
        EInvoiceStatus status,
        decimal totalWithVat,
        decimal vatPct,
        long clientId = CustomerCzId,
        long currencyId = CzkCurrencyId,
        string? documentNumber = null,
        EDocumentType docType = EDocumentType.Invoice,
        long? originalInvoiceId = null)
    {
        // Back-calculate base from total-incl-vat and vatPct:
        //   totalWithVat = base * (1 + vatPct/100)  →  base = totalWithVat / (1 + vatPct/100)
        var divisor   = 1m + vatPct / 100m;
        var baseAmount = divisor == 0m ? totalWithVat : totalWithVat / divisor;
        var vatAmount  = totalWithVat - baseAmount;

        var invoice = new Invoice
        {
            DocumentType = docType,
            OriginalInvoiceId = originalInvoiceId,
            Status = status,
            DocumentNumber = documentNumber ?? $"INV-{Guid.NewGuid():N}",
            IssueDate = duzp,
            TaxableSupplyDate = duzp,
            DueDate = duzp.AddDays(14),
            ClientId = clientId,
            IssuerId = IssuerId,
            Issuer = _context.Client.Find((long)IssuerId)!,
            CurrencyId = currencyId,
            TotalBeforeVat = baseAmount,
            TotalVat = vatAmount,
            TotalWithVat = totalWithVat,
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
            VatAmount = vatAmount,
            TotalWithVat = totalWithVat
        });

        _context.Invoice.Add(invoice);
        _context.SaveChanges();
    }

    /// <summary>Seeds a received invoice with a single line item.</summary>
    private void SeedReceivedInvoice(
        DateTime duzp,
        EReceivedInvoiceStatus status,
        decimal totalWithVat,
        decimal vatPct,
        long supplierId = CustomerCzId,
        long currencyId = CzkCurrencyId,
        string? documentNumber = null)
    {
        var divisor   = 1m + vatPct / 100m;
        var baseAmount = divisor == 0m ? totalWithVat : totalWithVat / divisor;
        var vatAmount  = totalWithVat - baseAmount;

        var received = new ReceivedInvoice
        {
            DocumentNumber = documentNumber ?? $"REC-{Guid.NewGuid():N}",
            Status = status,
            SupplierId = supplierId,
            IssueDate = duzp,
            ReceivedDate = duzp,
            TaxableSupplyDate = duzp,
            DueDate = duzp.AddDays(30),
            CurrencyId = currencyId,
            TotalBeforeVat = baseAmount,
            TotalVat = vatAmount,
            TotalWithVat = totalWithVat
        };

        received.Items.Add(new ReceivedInvoiceItem
        {
            OrderIndex = 1,
            Description = "Test expense",
            Quantity = 1,
            UnitPrice = baseAmount,
            VatRatePercentage = vatPct,
            TotalBeforeVat = baseAmount,
            VatAmount = vatAmount,
            TotalWithVat = totalWithVat
        });

        _context.ReceivedInvoice.Add(received);
        _context.SaveChanges();
    }

    /// <summary>Parses the byte[] result and validates it against the 2026 DPHKH1 XSD.</summary>
    private (XDocument doc, List<string> xsdErrors) ParseAndValidate(byte[] bytes)
    {
        var xml = Encoding.UTF8.GetString(bytes);
        var doc = XDocument.Parse(xml);
        var schemaSet = _schemaProvider.GetSchemaSet(EEpoFormType.ControlStatement, 2026);
        var errors = new List<string>();
        doc.Validate(schemaSet, (_, e) => errors.Add(e.Message));
        return (doc, errors);
    }

    // =========================================================================
    // 1. Input validation
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_YearTooOld_ThrowsArgumentOutOfRange()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => _service.ExportEpoControlStatementAsync(2023, 1, EVatPeriodType.Monthly));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task ExportEpoControlStatementAsync_MonthlyPeriodOutOfRange_Throws(int period)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => _service.ExportEpoControlStatementAsync(2026, period, EVatPeriodType.Monthly));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task ExportEpoControlStatementAsync_QuarterlyPeriodOutOfRange_Throws(int period)
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => _service.ExportEpoControlStatementAsync(2026, period, EVatPeriodType.Quarterly));
    }

    // =========================================================================
    // 2. XSD validity
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_EmptyPeriod_ValidatesAgainstXsd()
    {
        // No invoices → VetaD + VetaP only, all data sections omitted.
        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);

        var (_, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty("An empty period should produce valid DPHKH1 XML.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_WithA4Row_ValidatesAgainstXsd()
    {
        // One high-value invoice with CZ VAT number → should generate a VetaA4 row.
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 12100m, 21m, CustomerCzId);

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);

        var (_, errors) = ParseAndValidate(bytes);
        errors.ShouldBeEmpty("DPHKH1 with a VetaA4 row must be XSD-valid.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_WithAllSections_ValidatesAgainstXsd()
    {
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        // A.4 — high-value CZ customer
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 12100m, 21m, CustomerCzId, documentNumber: "INV-A4");
        // A.5 — low-value (below threshold)
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 5000m,  21m, CustomerCzId, documentNumber: "INV-A5");
        // B.2 — high-value CZ supplier
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 11000m, 21m, CustomerCzId, documentNumber: "REC-B2");
        // B.3 — low-value supplier
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 3000m,  12m, CustomerCzId, documentNumber: "REC-B3");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);

        var (_, errors) = ParseAndValidate(bytes);
        errors.ShouldBeEmpty("Full DPHKH1 with A4/A5/B2/B3 must be XSD-valid.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_CreditNoteOfA4Invoice_GoesToA4NegativeEvenBelowLimit()
    {
        // Original (Feb) was A.4 (12 100 incl. VAT); the March credit note of 2 420 is below the limit
        // but follows the original into A.4 with its own document number and negative amounts.
        SeedIssuedInvoice(new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc), EInvoiceStatus.Completed, 12100m, 21m, documentNumber: "INV-ORIG");
        var originalId = _context.Invoice.Single(i => i.DocumentNumber == "INV-ORIG").Id;
        SeedIssuedInvoice(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), EInvoiceStatus.Completed, 2420m, 21m,
            documentNumber: "CN-1", docType: EDocumentType.CreditNote, originalInvoiceId: originalId);

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var a4 = doc.Descendants("VetaA4").Single();
        a4.Attribute("c_evid_dd")!.Value.ShouldBe("CN-1");
        a4.Attribute("zakl_dane1")!.Value.ShouldBe("-2000.00");
        a4.Attribute("dan1")!.Value.ShouldBe("-420.00");
        doc.Descendants("VetaA5").ShouldBeEmpty();
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_CreditNoteOfA5Invoice_GoesToA5Aggregate()
    {
        // Original 5 000 incl. VAT = A.5 → the (small) credit note is A.5 too and reduces its totals.
        SeedIssuedInvoice(new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc), EInvoiceStatus.Completed, 6050m, 21m, documentNumber: "INV-SMALL");
        var originalId = _context.Invoice.Single(i => i.DocumentNumber == "INV-SMALL").Id;
        SeedIssuedInvoice(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), EInvoiceStatus.Completed, 1210m, 21m,
            documentNumber: "CN-2", docType: EDocumentType.CreditNote, originalInvoiceId: originalId);

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaA4").ShouldBeEmpty();
        var a5 = doc.Descendants("VetaA5").Single();
        a5.Attribute("zakl_dane1")!.Value.ShouldBe("4000.00"); // 5000 - 1000
        a5.Attribute("dan1")!.Value.ShouldBe("840.00");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_QuarterlyPeriod_ValidatesAgainstXsd()
    {
        var duzp = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 5000m, 21m, CustomerCzId);

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 1, EVatPeriodType.Quarterly);

        var (_, errors) = ParseAndValidate(bytes);
        errors.ShouldBeEmpty("Quarterly DPHKH1 must be XSD-valid.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_ReturnsUtf8WithoutBom()
    {
        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);

        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF },
            "DPHKH1 bytes must NOT start with a UTF-8 BOM.");

        var xml = Encoding.UTF8.GetString(bytes);
        xml.ShouldContain("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.ShouldContain("<DPHKH1>");
    }

    // =========================================================================
    // 3. Section A.4 / A.5 — output invoice classification
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_ExactlyTenThousand_CzDic_GoesToA4()
    {
        // Acceptance criterion: invoice at exactly 10 000 CZK incl. VAT with CZ DIČ → A.4 (≥ threshold).
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 10_000m, 21m, CustomerCzId, documentNumber: "INV-EXACT");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var a4Elements = doc.Descendants("VetaA4").ToList();
        a4Elements.ShouldNotBeEmpty("Invoice at exactly 10 000 CZK incl. VAT with CZ DIČ must be in A.4.");
        doc.Descendants("VetaA5").ShouldBeEmpty("A.5 must be absent when A.4 receives the only invoice.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_BelowThreshold_CzDic_GoesToA5()
    {
        // Acceptance criterion: invoice at 9 999.99 CZK with CZ DIČ → A.5 (< threshold).
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 9_999.99m, 21m, CustomerCzId, documentNumber: "INV-BELOW");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaA4").ShouldBeEmpty("Invoice below 10 000 CZK must NOT be in A.4.");
        doc.Descendants("VetaA5").ShouldNotBeEmpty("Invoice below 10 000 CZK must be aggregated in A.5.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_NullTaxNumber_GoesToA5()
    {
        // Acceptance criterion: TaxNumber = NULL → A.5 regardless of amount.
        _context.Client.Add(new Client
        {
            Id = 10,
            CompanyName = "No-VAT Customer",
            RegistrationNumber = "99999998",
            TaxNumber = null,
            IsIssuer = false,
            IsActive = true
        });
        _context.SaveChanges();

        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 50_000m, 21m, clientId: 10, documentNumber: "INV-NOTAN");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaA4").ShouldBeEmpty("Invoice with NULL TaxNumber must NOT be in A.4.");
        doc.Descendants("VetaA5").ShouldNotBeEmpty("Invoice with NULL TaxNumber must be in A.5.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_ForeignTaxNumber_GoesToA5()
    {
        // Acceptance criterion: TaxNumber = "SK1234567890" (non-CZ prefix) → A.5.
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        // CustomerForeignId has TaxNumber = "SK9999999" — seeded in SeedBaseData.
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 50_000m, 21m, clientId: CustomerForeignId, documentNumber: "INV-SK");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaA4").ShouldBeEmpty("Invoice with non-CZ VAT number must NOT be in A.4.");
        doc.Descendants("VetaA5").ShouldNotBeEmpty("Invoice with non-CZ VAT number must be in A.5.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_CzDicAboveThreshold_GoesToA4()
    {
        // Acceptance criterion: TaxNumber = "CZ12345678" AND total >= 10 000 CZK → A.4.
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 15_000m, 21m, CustomerCzId, documentNumber: "INV-CZA4");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var a4 = doc.Descendants("VetaA4").Single();
        // dic_odb should be the numeric part of "CZ11111111" → "11111111"
        a4.Attribute("dic_odb")!.Value.ShouldBe("11111111");
        a4.Attribute("c_evid_dd")!.Value.ShouldBe("INV-CZA4");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_A4Row_HasCorrectAmounts()
    {
        // Invoice: total incl. VAT = 12 100 CZK at 21 %
        // → base = 10 000, vat = 2 100
        // → zakl_dane1 = "10000.00", dan1 = "2100.00"
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 12_100m, 21m, CustomerCzId, documentNumber: "INV-AMT");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var a4 = doc.Descendants("VetaA4").Single();
        // 12100 / 1.21 ≈ 10000; vat ≈ 2100
        var zakl = decimal.Parse(a4.Attribute("zakl_dane1")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        var dan  = decimal.Parse(a4.Attribute("dan1")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        (zakl + dan).ShouldBe(12_100m, tolerance: 0.02m, "Base + VAT should equal total incl. VAT.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_A5_AggregatesMultipleInvoices()
    {
        // Two below-threshold invoices → both aggregated into single A.5 row.
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        // 1 000 CZK base at 21%
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 1_210m, 21m, CustomerCzId, documentNumber: "INV-A5-1");
        // 2 000 CZK base at 21%
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 2_420m, 21m, CustomerCzId, documentNumber: "INV-A5-2");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        // Exactly one VetaA5 element.
        doc.Descendants("VetaA5").Count().ShouldBe(1, "A.5 must produce exactly one aggregated element.");
        doc.Descendants("VetaA4").ShouldBeEmpty("Low-value invoices must not appear in A.4.");
    }

    // =========================================================================
    // 4. Section B.2 / B.3 — received invoice classification
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_ReceivedAboveThreshold_CzDic_GoesToB2()
    {
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 11_000m, 21m, CustomerCzId, documentNumber: "REC-B2-1");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var b2 = doc.Descendants("VetaB2").Single();
        // dic_dod = numeric part of "CZ11111111" → "11111111"
        b2.Attribute("dic_dod")!.Value.ShouldBe("11111111");
        b2.Attribute("c_evid_dd")!.Value.ShouldBe("REC-B2-1");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_ReceivedBelowThreshold_GoesToB3()
    {
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 5_000m, 21m, CustomerCzId, documentNumber: "REC-B3-1");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaB2").ShouldBeEmpty("Low-value invoice must NOT be in B.2.");
        doc.Descendants("VetaB3").ShouldNotBeEmpty("Low-value invoice must be aggregated in B.3.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_ReceivedForeignSupplier_GoesToB3()
    {
        // Foreign supplier (TaxNumber = "SK9999999") → always B.3.
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 50_000m, 21m, CustomerForeignId, documentNumber: "REC-SK");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaB2").ShouldBeEmpty("Non-CZ supplier must NOT be in B.2.");
        doc.Descendants("VetaB3").ShouldNotBeEmpty("Non-CZ supplier must be in B.3.");
    }

    // =========================================================================
    // 5. Status exclusion
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_DraftInvoice_IsExcluded()
    {
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Draft,     50_000m, 21m, CustomerCzId, documentNumber: "INV-DRAFT");
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 12_100m, 21m, CustomerCzId, documentNumber: "INV-OK");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        // Only the Completed invoice should appear; the Draft one must be absent.
        var a4s = doc.Descendants("VetaA4").ToList();
        a4s.Count.ShouldBe(1, "Only one A.4 row — Draft invoice must be excluded.");
        a4s[0].Attribute("c_evid_dd")!.Value.ShouldBe("INV-OK");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_DeletedInvoice_IsExcluded()
    {
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Deleted,   50_000m, 21m, CustomerCzId, documentNumber: "INV-DEL");
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed,  5_000m, 21m, CustomerCzId, documentNumber: "INV-OK2");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaA4").ShouldBeEmpty("Deleted invoice must be excluded; the remaining one is below threshold.");
        doc.Descendants("VetaA5").ShouldNotBeEmpty("The valid invoice below threshold must appear in A.5.");
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_ReceivedStatus_ExcludedFromB2B3()
    {
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Received, 50_000m, 21m, CustomerCzId, documentNumber: "REC-RCVD");
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved,  5_000m, 21m, CustomerCzId, documentNumber: "REC-OK");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaB2").ShouldBeEmpty("'Received' status must be excluded; Approved invoice is below threshold.");
        doc.Descendants("VetaB3").ShouldNotBeEmpty("The Approved invoice must appear in B.3.");
    }

    // =========================================================================
    // 6. FX conversion
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_EurInvoice_ConvertedToCzkForClassification()
    {
        // EUR invoice: 1 000 EUR at 21 %, rate = 25 CZK/EUR.
        // Total incl. VAT in EUR = 1 210 EUR → in CZK = 30 250 CZK (≥ 10 000 → A.4).
        _currencyService
            .ConvertToCzkAsync(Arg.Any<decimal>(), "EUR", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<decimal>(0) * 25m));

        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        // totalWithVat in EUR = 1210 EUR; after conversion → 30 250 CZK
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 1_210m, 21m, CustomerCzId,
            currencyId: EurCurrencyId, documentNumber: "INV-EUR");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        // After FX the total is >= 10 000 CZK → must land in A.4, not A.5.
        doc.Descendants("VetaA4").ShouldNotBeEmpty("EUR invoice converted to CZK >= 10 000 must be in A.4.");
        doc.Descendants("VetaA5").ShouldBeEmpty();
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_EurInvoiceBelowThresholdAfterConversion_GoesToA5()
    {
        // EUR invoice: 100 EUR at 21 %, rate = 25 CZK/EUR.
        // Total incl. VAT in EUR = 121 EUR → in CZK = 3 025 CZK (< 10 000 → A.5).
        _currencyService
            .ConvertToCzkAsync(Arg.Any<decimal>(), "EUR", Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.ArgAt<decimal>(0) * 25m));

        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 121m, 21m, CustomerCzId,
            currencyId: EurCurrencyId, documentNumber: "INV-EUR-LOW");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaA4").ShouldBeEmpty("EUR invoice below 10 000 CZK after conversion must be in A.5.");
        doc.Descendants("VetaA5").ShouldNotBeEmpty();
    }

    // =========================================================================
    // 7. Consistency — A.4 + A.5 totals must equal GetReportAsync output VAT
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_TotalsMatchGetReportAsync()
    {
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        // A.4 candidate — CZ customer, above threshold
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 12_100m, 21m, CustomerCzId, documentNumber: "INV-1");
        // A.5 candidate — below threshold
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed,  5_000m, 21m, CustomerCzId, documentNumber: "INV-2");
        // A.5 candidate — foreign customer
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 15_000m, 21m, CustomerForeignId, documentNumber: "INV-3");

        var bytes  = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);
        errors.ShouldBeEmpty();

        // GetReportAsync for the same period.
        var from   = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var to     = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);
        var report = await _service.GetReportAsync(from, to);

        // Sum all VAT amounts from A.4 rows (dan1 + dan2 per row) + A.5 row (dan1 + dan2).
        decimal khOutputVat = 0m;
        foreach (var a4 in doc.Descendants("VetaA4"))
        {
            if (a4.Attribute("dan1") is { } d1) khOutputVat += decimal.Parse(d1.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (a4.Attribute("dan2") is { } d2) khOutputVat += decimal.Parse(d2.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var a5 in doc.Descendants("VetaA5"))
        {
            if (a5.Attribute("dan1") is { } d1) khOutputVat += decimal.Parse(d1.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (a5.Attribute("dan2") is { } d2) khOutputVat += decimal.Parse(d2.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Allow for a tiny floating-point rounding difference (< 1 CZK).
        (khOutputVat - report.TotalOutputVat).ShouldBeLessThan(1m,
            "Sum of A.4 + A.5 VAT must equal GetReportAsync.TotalOutputVat (within 1 CZK rounding).");
    }

    // =========================================================================
    // 8. A.1 / B.1 — must be absent
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_VetaA1AndB1_AreAbsent()
    {
        // Without reverse charge items A.1 and B.1 stay empty (see EpoReverseChargeAndSummaryTests for the filled case).
        // They must not appear in the output at all.
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 15_000m, 21m, CustomerCzId, documentNumber: "INV-X");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        doc.Descendants("VetaA1").ShouldBeEmpty("VetaA1 (PDP) must be absent without RC items.");
        doc.Descendants("VetaB1").ShouldBeEmpty("VetaB1 (PDP) must be absent without RC items.");
    }

    // =========================================================================
    // 9. VetaD / VetaP header attributes
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_VetaD_HasCorrectAttributes()
    {
        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var vetaD = doc.Descendants("VetaD").Single();
        vetaD.Attribute("dokument")!.Value.ShouldBe("KH1");
        vetaD.Attribute("k_uladis")!.Value.ShouldBe("DPH");
        vetaD.Attribute("rok")!.Value.ShouldBe("2026");
        vetaD.Attribute("khdph_forma")!.Value.ShouldBe("B");
        vetaD.Attribute("mesic")!.Value.ShouldBe("3");
        vetaD.Attribute("ctvrt").ShouldBeNull();
    }

    [Fact]
    public async Task ExportEpoControlStatementAsync_VetaP_HasCorrectDicAndEpoHeaderCodes()
    {
        // VetaP attributes come from:
        //   @dic      — Client.TaxNumber (stripped of "CZ" prefix)
        //   @c_ufo    — CompanySystemSettings.EpoTaxOfficeCode (issue #39)
        //   @c_pracufo — CompanySystemSettings.EpoTaxOfficeBranchCode (issue #39)
        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, _) = ParseAndValidate(bytes);

        var vetaP = doc.Descendants("VetaP").Single();
        vetaP.Attribute("dic")!.Value.ShouldBe("12345678");  // stripped from "CZ12345678"
        vetaP.Attribute("c_ufo")!.Value.ShouldBe("451");
        vetaP.Attribute("c_pracufo")!.Value.ShouldBe("2017");
        vetaP.Attribute("typ_ds")!.Value.ShouldBe("P");
    }

    // =========================================================================
    // 10. Quarterly VetaD header
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_QuarterlyVetaD_HasCtvrtNotMesic()
    {
        // Quarterly period 2 (Q2 = Apr–Jun) → VetaD must carry @ctvrt="2" and NO @mesic.
        var bytes = await _service.ExportEpoControlStatementAsync(2026, 2, EVatPeriodType.Quarterly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var vetaD = doc.Descendants("VetaD").Single();
        vetaD.Attribute("ctvrt")!.Value.ShouldBe("2");
        vetaD.Attribute("mesic").ShouldBeNull("Quarterly filing must not have @mesic.");
    }

    // =========================================================================
    // 11. Multiple A.4 rows in same period
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_TwoHighValueCzInvoices_GeneratesTwoA4Rows()
    {
        // Two qualifying invoices must each produce their own VetaA4 element.
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 12_100m, 21m, CustomerCzId, documentNumber: "INV-A4-1");
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 24_200m, 21m, CustomerCzId, documentNumber: "INV-A4-2");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var a4Rows = doc.Descendants("VetaA4").ToList();
        a4Rows.Count.ShouldBe(2, "Two qualifying invoices must generate two separate A.4 rows.");
        a4Rows.Select(e => e.Attribute("c_evid_dd")!.Value)
              .ShouldBe(new[] { "INV-A4-1", "INV-A4-2" }, ignoreOrder: true);
    }

    // =========================================================================
    // 12. A.5 is only the aggregate for sub-threshold invoices (not the A.4 ones)
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_MixedInvoices_A5ExcludesA4Amounts()
    {
        // A.4 invoice: 12 100 CZK at 21 % → base=10 000, vat=2 100.
        // A.5 invoice: 1 210 CZK at 21 % → base=1 000, vat=210.
        // A.5 aggregate zakl_dane1 must equal 1 000 (the sub-threshold base only).
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 12_100m, 21m, CustomerCzId, documentNumber: "INV-A4");
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed,  1_210m, 21m, CustomerCzId, documentNumber: "INV-A5");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaA4").Count().ShouldBe(1);

        var a5 = doc.Descendants("VetaA5").Single();
        var a5Base = decimal.Parse(a5.Attribute("zakl_dane1")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        // 1 210 / 1.21 ≈ 1 000; tolerance for rounding.
        a5Base.ShouldBeLessThan(1_001m, "A.5 base must NOT include the A.4 invoice amounts.");
        a5Base.ShouldBeGreaterThan(999m);
    }

    // =========================================================================
    // 13. B.2 amounts are correct (base + VAT)
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_B2Row_HasCorrectAmounts()
    {
        // Received invoice: 11 000 CZK total incl. VAT at 21 %.
        // → base ≈ 9 090.91, vat ≈ 1 909.09
        var duzp = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved, 11_000m, 21m, CustomerCzId, documentNumber: "REC-AMT");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var b2 = doc.Descendants("VetaB2").Single();
        var zakl = decimal.Parse(b2.Attribute("zakl_dane1")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        var dan  = decimal.Parse(b2.Attribute("dan1")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        (zakl + dan).ShouldBe(11_000m, tolerance: 0.02m, "Base + VAT must equal total incl. VAT.");
    }

    // =========================================================================
    // 14. Out-of-period invoices are excluded
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_InvoiceOutsidePeriod_IsExcluded()
    {
        // Invoice in February — should NOT appear in March export.
        var februaryDate = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(februaryDate, EInvoiceStatus.Completed, 50_000m, 21m, CustomerCzId, documentNumber: "INV-FEB");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaA4").ShouldBeEmpty("February invoice must not appear in March export.");
        doc.Descendants("VetaA5").ShouldBeEmpty("February invoice must not appear in March export.");
    }

    // =========================================================================
    // 15. Rejected received invoices are excluded
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_RejectedReceivedInvoice_IsExcluded()
    {
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        // Rejected invoice should be excluded; Approved one stays.
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Rejected, 50_000m, 21m, CustomerCzId, documentNumber: "REC-REJ");
        SeedReceivedInvoice(duzp, EReceivedInvoiceStatus.Approved,  5_000m, 21m, CustomerCzId, documentNumber: "REC-OK");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        doc.Descendants("VetaB2").ShouldBeEmpty("Rejected invoice must be excluded; Approved one is below threshold.");
        doc.Descendants("VetaB3").ShouldNotBeEmpty("Approved invoice must appear in B.3.");
        doc.Descendants("VetaB3").Count().ShouldBe(1);
    }

    // =========================================================================
    // 16. No issuer configured throws InvalidOperationException
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_NoIssuerConfigured_ThrowsInvalidOperationException()
    {
        // Remove the issuer from the DB so that the service cannot find it.
        var issuer = await _context.Client.FindAsync((long)IssuerId);
        _context.Client.Remove(issuer!);
        await _context.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly));
    }

    // =========================================================================
    // 17. A.4 row with mixed standard + reduced rate items
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_A4Row_WithReducedRateItem_HasBothRateAttributes()
    {
        // Seed an invoice with two items: one at 21 % (standard) and one at 12 % (reduced),
        // ensuring the total exceeds 10 000 CZK so it classifies as A.4.
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

        // Manual seed to control two line items at different rates.
        var stdBase = 8_000m;
        var stdVat  = stdBase * 0.21m;         // 1 680
        var redBase = 2_000m;
        var redVat  = redBase * 0.12m;         // 240
        var total   = stdBase + stdVat + redBase + redVat; // 11 920

        var invoice = new Invoice
        {
            DocumentType    = EDocumentType.Invoice,
            Status          = EInvoiceStatus.Completed,
            DocumentNumber  = "INV-MIXED",
            IssueDate       = duzp,
            TaxableSupplyDate = duzp,
            DueDate         = duzp.AddDays(14),
            ClientId        = CustomerCzId,
            IssuerId        = IssuerId,
            Issuer          = _context.Client.Find((long)IssuerId)!,
            CurrencyId      = CzkCurrencyId,
            TotalBeforeVat  = stdBase + redBase,
            TotalVat        = stdVat + redVat,
            TotalWithVat    = total,
            InvoiceItem     = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Standard-rate service", Quantity = 1,
                    UnitPrice = stdBase, VatRatePercentage = 21m,
                    TotalBeforeVat = stdBase, VatAmount = stdVat, TotalWithVat = stdBase + stdVat
                },
                new InvoiceItem
                {
                    OrderIndex = 2, Description = "Reduced-rate goods", Quantity = 1,
                    UnitPrice = redBase, VatRatePercentage = 12m,
                    TotalBeforeVat = redBase, VatAmount = redVat, TotalWithVat = redBase + redVat
                }
            }
        };
        _context.Invoice.Add(invoice);
        _context.SaveChanges();

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        var a4 = doc.Descendants("VetaA4").Single();
        // Both rate slots must be present.
        a4.Attribute("zakl_dane1").ShouldNotBeNull("Standard-rate base must be present.");
        a4.Attribute("dan1").ShouldNotBeNull("Standard-rate VAT must be present.");
        a4.Attribute("zakl_dane2").ShouldNotBeNull("Reduced-rate base must be present.");
        a4.Attribute("dan2").ShouldNotBeNull("Reduced-rate VAT must be present.");

        decimal.Parse(a4.Attribute("zakl_dane1")!.Value, System.Globalization.CultureInfo.InvariantCulture)
               .ShouldBe(stdBase, tolerance: 0.01m);
        decimal.Parse(a4.Attribute("dan1")!.Value, System.Globalization.CultureInfo.InvariantCulture)
               .ShouldBe(stdVat, tolerance: 0.01m);
        decimal.Parse(a4.Attribute("zakl_dane2")!.Value, System.Globalization.CultureInfo.InvariantCulture)
               .ShouldBe(redBase, tolerance: 0.01m);
        decimal.Parse(a4.Attribute("dan2")!.Value, System.Globalization.CultureInfo.InvariantCulture)
               .ShouldBe(redVat, tolerance: 0.01m);
    }

    // =========================================================================
    // 18. Non-VAT payer throws VatPayerRequiredException
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_IssuerIsNotVatPayer_ThrowsVatPayerRequiredException()
    {
        // When the issuer has IsVatPayer = false, the service must throw
        // VatPayerRequiredException so the controller returns HTTP 403 VAT_PAYER_REQUIRED.
        // Uses a fresh in-memory DB so the issuer flag change does not affect other tests.
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
            Id = IssuerId, CompanyName = "Non-VAT Issuer", RegistrationNumber = "22222222",
            IsIssuer = true, IsActive = true, IsVatPayer = false
        });
        await tenantCtx.SaveChangesAsync();

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns((long?)IssuerId);

        var logger = Substitute.For<ILogger<VatReportService>>();
        var service = new VatReportService(
            tenantCtx, _masterContext, tenantResolver,
            _schemaProvider, _currencyService, logger);

        await Should.ThrowAsync<VatPayerRequiredException>(
            () => service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly));
    }

    // =========================================================================
    // 19. Zero-VAT-only invoice does not pollute A.5 / B.3
    // =========================================================================

    [Fact]
    public async Task ExportEpoControlStatementAsync_ZeroVatInvoice_DoesNotAppearInA5()
    {
        // An invoice whose items all carry 0 % VAT contributes nothing to A.5
        // because the classification skips 0 % items (same rule as DPHDP3).
        var duzp = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        SeedIssuedInvoice(duzp, EInvoiceStatus.Completed, 5_000m, 0m, CustomerCzId, documentNumber: "INV-ZEROVAT");

        var bytes = await _service.ExportEpoControlStatementAsync(2026, 3, EVatPeriodType.Monthly);
        var (doc, errors) = ParseAndValidate(bytes);

        errors.ShouldBeEmpty();
        // No A.4 (below threshold or zero VAT — classification passes no items).
        // No A.5 — zero-VAT items are explicitly excluded from both A.4 and A.5.
        doc.Descendants("VetaA4").ShouldBeEmpty("Zero-VAT invoice must not appear in A.4.");
        doc.Descendants("VetaA5").ShouldBeEmpty("Zero-VAT invoice must not appear in A.5 — 0 % items are excluded.");
    }
}
