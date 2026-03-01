using System.Text.RegularExpressions;
using Fakvio.Application.QrPayment;
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
/// Unit tests for the QR Faktura (SIND) and QR Platba (SPD) implementation.
///
/// Tests cover:
/// - CRC32 checksum computation
/// - SIND string building (QR Faktura format)
/// - SPD integration (QR Platba + QR Faktura combined)
/// - QrPaymentService (end-to-end from invoice to QR code)
///
/// Reference: https://www.kdpcr.cz/informace/qr-faktura
/// </summary>
public class QrPaymentTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly IPayliboClient _payliboClient;

    public QrPaymentTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);

        // Mock paylibo client — returns empty by default (tests focus on IBAN/SIND strategies)
        _payliboClient = Substitute.For<IPayliboClient>();
        _payliboClient.CreateQrPaymentImageAsync(Arg.Any<PayliboQrOptions>())
            .Returns(Array.Empty<byte>());

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds a minimal invoice with related entities for QR code generation tests.
    /// </summary>
    private void SeedTestData()
    {
        var currency = new Currency
        {
            Id = 1, Code = "CZK", Name = "Česká koruna", Symbol = "Kč",
            IsActive = true, DecimalPlaces = 2
        };
        _context.Currency.Add(currency);
        _context.SaveChanges();

        var vatRate21 = new VatRate
        {
            Id = 1, Name = "DPH 21%", Rate = 21m, IsActive = true, IsDefault = true,
            ValidFrom = new DateTime(2013, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        var vatRate12 = new VatRate
        {
            Id = 2, Name = "DPH 12%", Rate = 12m, IsActive = true, IsReduced = true, IsDefault = true,
            ValidFrom = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _context.VatRate.Add(vatRate21);
        _context.VatRate.Add(vatRate12);
        _context.SaveChanges();

        // Issuer (your company)
        var issuer = new Client
        {
            Id = 1, CompanyName = "Test Issuer s.r.o.", RegistrationNumber = "12345678",
            TaxNumber = "CZ12345678", IsIssuer = true, IsActive = true, IsVatPayer = true
        };
        _context.Client.Add(issuer);
        _context.SaveChanges();

        // Client (recipient)
        var client = new Client
        {
            Id = 2, CompanyName = "Test Client a.s.", RegistrationNumber = "98765432",
            TaxNumber = "CZ98765432", IsIssuer = false, IsActive = true, IsVatPayer = true
        };
        _context.Client.Add(client);
        _context.SaveChanges();

        // Invoice with IBAN (for combined QR Platba+F)
        var invoice = new Invoice
        {
            Id = 1,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2026001",
            IssueDate = new DateTime(2026, 2, 11, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 2, 25, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 2, 11, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = 1,
            ClientId = 2,
            CurrencyId = 1,
            VariableSymbol = "2026001",
            IBAN = "CZ5855000000001265098001",
            SWIFT = "RZBCCZPP",
            BankAccountNumber = "1265098001/5500",
            TotalBeforeVat = 5000m,
            TotalVat = 850m,
            TotalWithVat = 5850m,
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    Id = 1, InvoiceId = 1, OrderIndex = 1,
                    Description = "Consulting services", Quantity = 10, Unit = "hours",
                    UnitPrice = 300m, VatRateId = 1, VatRatePercentage = 21m,
                    TotalBeforeVat = 3000m, VatAmount = 630m, TotalWithVat = 3630m
                },
                new InvoiceItem
                {
                    Id = 2, InvoiceId = 1, OrderIndex = 2,
                    Description = "Software license", Quantity = 1, Unit = "pcs",
                    UnitPrice = 2000m, VatRateId = 2, VatRatePercentage = 12m,
                    TotalBeforeVat = 2000m, VatAmount = 240m, TotalWithVat = 2240m
                }
            }
        };
        _context.Invoice.Add(invoice);
        _context.SaveChanges();

        // Invoice without IBAN (for QR Faktura only)
        var invoice2 = new Invoice
        {
            Id = 2,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2026002",
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = 1,
            ClientId = 2,
            CurrencyId = 1,
            VariableSymbol = "2026002",
            TotalBeforeVat = 1000m,
            TotalVat = 0m,
            TotalWithVat = 1000m,
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    Id = 3, InvoiceId = 2, OrderIndex = 1,
                    Description = "Non-taxable service", Quantity = 1, Unit = "pcs",
                    UnitPrice = 1000m, VatRatePercentage = 0m,
                    TotalBeforeVat = 1000m, VatAmount = 0m, TotalWithVat = 1000m
                }
            }
        };
        _context.Invoice.Add(invoice2);
        _context.SaveChanges();

        // Invoice with Czech bank account but NO IBAN (for paylibo API fallback — Strategy 2)
        var invoice3 = new Invoice
        {
            Id = 10,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2026010",
            IssueDate = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 3, 24, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = 1,
            ClientId = 2,
            CurrencyId = 1,
            VariableSymbol = "2026010",
            BankAccountNumber = "1342333010/3030", // Czech domestic format, no IBAN
            TotalBeforeVat = 2000m,
            TotalVat = 420m,
            TotalWithVat = 2420m,
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    Id = 10, InvoiceId = 10, OrderIndex = 1,
                    Description = "Paylibo test service", Quantity = 1, Unit = "pcs",
                    UnitPrice = 2000m, VatRateId = 1, VatRatePercentage = 21m,
                    TotalBeforeVat = 2000m, VatAmount = 420m, TotalWithVat = 2420m
                }
            }
        };
        _context.Invoice.Add(invoice3);
        _context.SaveChanges();
    }

    // ─── CRC32 Tests ─────────────────────────────────────────────────────────

    [Fact]
    public void Crc32_KnownInput_ReturnsCorrectHash()
    {
        // "123456789" is a well-known CRC32 test vector
        var result = Crc32Calculator.Compute("123456789");
        result.ShouldBe("CBF43926");
    }

    [Fact]
    public void Crc32_EmptyString_ReturnsZero()
    {
        var result = Crc32Calculator.Compute("");
        result.ShouldBe("00000000");
    }

    [Fact]
    public void Crc32_ReturnsEightCharacterHex()
    {
        var result = Crc32Calculator.Compute("test");
        result.Length.ShouldBe(8);
        Regex.IsMatch(result, "^[0-9A-F]{8}$").ShouldBeTrue();
    }

    // ─── SindBuilder Tests ───────────────────────────────────────────────────

    [Fact]
    public void SindBuilder_RequiredFieldsOnly_BuildsCorrectString()
    {
        var builder = new SindBuilder()
            .SetDocumentId("INV2026001")
            .SetIssueDate(new DateTime(2026, 2, 11))
            .SetAmount(5850m);

        var result = builder.Build();

        // Should start with SIND header
        result.ShouldStartWith("SID*1.0*");
        // Should contain required attributes
        result.ShouldContain("AM:5850.00*");
        result.ShouldContain("DD:20260211*");
        result.ShouldContain("ID:INV2026001*");
        // Should end with CRC32 (no trailing * after CRC32)
        Regex.IsMatch(result, @"CRC32:[0-9A-F]{8}$").ShouldBeTrue();
    }

    [Fact]
    public void SindBuilder_AlphabeticalKeyOrder()
    {
        // Keys in SIND must be sorted alphabetically (canonical form)
        var builder = new SindBuilder()
            .SetDocumentId("INV001")
            .SetAmount(100m)
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetVariableSymbol("123");

        var sindWithoutCrc = builder.BuildWithoutCrc();

        // After "SID*1.0*", keys should be in order: AM, DD, ID, VS
        var attrPart = sindWithoutCrc.Replace("SID*1.0*", "");
        var keys = attrPart.Split('*', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Split(':')[0])
            .ToList();

        keys.ShouldBe(keys.OrderBy(x => x).ToList());
    }

    [Fact]
    public void SindBuilder_WithAccount_FormatsIbanAndSwift()
    {
        var builder = new SindBuilder()
            .SetDocumentId("TEST")
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetAmount(100m)
            .SetAccount("CZ5855000000001265098001", "RZBCCZPP");

        var result = builder.BuildWithoutCrc();
        result.ShouldContain("ACC:CZ5855000000001265098001+RZBCCZPP*");
    }

    [Fact]
    public void SindBuilder_WithIbanOnly_NoSwift()
    {
        var builder = new SindBuilder()
            .SetDocumentId("TEST")
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetAmount(100m)
            .SetAccount("CZ5855000000001265098001");

        var result = builder.BuildWithoutCrc();
        result.ShouldContain("ACC:CZ5855000000001265098001*");
        result.ShouldNotContain("+");
    }

    [Fact]
    public void SindBuilder_NullValuesIgnored()
    {
        var builder = new SindBuilder()
            .SetDocumentId("TEST")
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetAmount(100m)
            .SetVariableSymbol(null)
            .SetIssuerTaxNumber(null);

        var result = builder.BuildWithoutCrc();
        result.ShouldNotContain("VS:");
        result.ShouldNotContain("VII:");
    }

    [Fact]
    public void SindBuilder_VatBreakdown_SetsCorrectKeys()
    {
        var builder = new SindBuilder()
            .SetDocumentId("TEST")
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetAmount(6000m)
            .SetStandardVat(3000m, 630m)
            .SetReducedVat1(2000m, 240m)
            .SetNonTaxableAmount(500m);

        var result = builder.BuildWithoutCrc();
        result.ShouldContain("TB0:3000.00*");
        result.ShouldContain("T0:630.00*");
        result.ShouldContain("TB1:2000.00*");
        result.ShouldContain("T1:240.00*");
        result.ShouldContain("NTB:500.00*");
    }

    [Fact]
    public void SindBuilder_CrcIsConsistent()
    {
        // Building the same SIND twice should produce identical CRC
        var builder1 = new SindBuilder().SetDocumentId("TEST").SetIssueDate(new DateTime(2026, 1, 1)).SetAmount(100m);
        var builder2 = new SindBuilder().SetDocumentId("TEST").SetIssueDate(new DateTime(2026, 1, 1)).SetAmount(100m);

        builder1.Build().ShouldBe(builder2.Build());
    }

    // ─── Simple SPD (BuildSimpleSpdString) Tests ────────────────────────────

    [Fact]
    public void BuildSimpleSpdString_ValidInput_ReturnsCorrectFormat()
    {
        // Test the standard case: all optional fields provided.
        // The result should be a clean SPD string with payment data only — no X-INV.
        var result = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ5855000000001265098001",
            swift: "RZBCCZPP",
            amount: 5850m,
            currencyCode: "CZK",
            dueDate: new DateTime(2026, 2, 25),
            variableSymbol: "2026001",
            message: "INV2026001");

        // Must start with SPD header
        result.ShouldStartWith("SPD*1.0*");

        // Must contain all payment attributes (alphabetical order)
        result.ShouldContain("ACC:CZ5855000000001265098001+RZBCCZPP");
        result.ShouldContain("AM:5850.00");
        result.ShouldContain("CC:CZK");
        result.ShouldContain("DT:20260225");
        result.ShouldContain("MSG:INV2026001");
        result.ShouldContain("X-VS:2026001");

        // Must NOT contain X-INV (no embedded invoice data)
        result.ShouldNotContain("X-INV");

        // Must NOT end with trailing *
        result.ShouldNotEndWith("*");
    }

    [Fact]
    public void BuildSimpleSpdString_NullOptionalFields_OmitsAttributes()
    {
        // When optional fields are null, they should be omitted from the SPD string.
        // Only ACC and AM are always present.
        var result = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ5855000000001265098001",
            swift: null,
            amount: 100m,
            currencyCode: null,
            dueDate: null,
            variableSymbol: null,
            message: null);

        // Should contain only ACC and AM
        result.ShouldStartWith("SPD*1.0*");
        result.ShouldContain("ACC:CZ5855000000001265098001");
        result.ShouldContain("AM:100.00");

        // Optional attributes should be absent (use *KEY: prefix to avoid matching substrings like ACC:)
        result.ShouldNotContain("*CC:");
        result.ShouldNotContain("*DT:");
        result.ShouldNotContain("*MSG:");
        result.ShouldNotContain("*X-VS:");
        result.ShouldNotContain("+"); // No SWIFT appended
    }

    [Fact]
    public void BuildSimpleSpdString_SanitizesIbanWhitespace()
    {
        // Users often paste IBANs with spaces or dashes — the method must clean them.
        var result = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ58 5500-0000 0012-6509 8001",
            swift: null,
            amount: 500m,
            currencyCode: "CZK",
            dueDate: null,
            variableSymbol: null,
            message: null);

        // IBAN should be stripped of spaces and dashes
        result.ShouldContain("ACC:CZ5855000000001265098001");
        result.ShouldNotContain(" ");
        result.ShouldNotContain("-");
    }

    [Fact]
    public void BuildSimpleSpdString_MessageTruncatedAt60Chars()
    {
        // SPD spec limits MSG to 60 characters — verify truncation.
        var longMessage = new string('A', 80); // 80 chars, exceeds 60 limit

        var result = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ5855000000001265098001",
            swift: null,
            amount: 100m,
            currencyCode: null,
            dueDate: null,
            variableSymbol: null,
            message: longMessage);

        // Extract the MSG value from the SPD string
        var msgStart = result.IndexOf("MSG:") + 4;
        var msgEnd = result.IndexOf('*', msgStart);
        var msgValue = msgEnd >= 0 ? result[msgStart..msgEnd] : result[msgStart..];

        msgValue.Length.ShouldBe(60);
    }

    [Fact]
    public void BuildSimpleSpdString_AttributesInAlphabeticalOrder()
    {
        // SPD spec requires alphabetical attribute ordering.
        var result = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ5855000000001265098001",
            swift: "RZBCCZPP",
            amount: 5850m,
            currencyCode: "CZK",
            dueDate: new DateTime(2026, 2, 25),
            variableSymbol: "2026001",
            message: "INV2026001");

        // Extract attribute keys from the SPD string (skip "SPD*1.0*" prefix)
        var attrPart = result.Replace("SPD*1.0*", "");
        var keys = attrPart.Split('*')
            .Where(s => s.Contains(':'))
            .Select(a => a.Split(':')[0])
            .ToList();

        // Keys must be in alphabetical order
        keys.ShouldBe(keys.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    // ─── Legacy BuildSpdWithInvoice Tests (kept for backward compatibility) ───

    [Fact]
    public void BuildSpdWithInvoice_SharedKeysMovedToSpd()
    {
        // Verifies the legacy combined SPD+SIND method still works correctly.
        var attributes = new Dictionary<string, string>
        {
            ["ID"] = "INV001",
            ["DD"] = "20260211",
            ["AM"] = "5850.00",
            ["ACC"] = "CZ5855000000001265098001",
            ["CC"] = "CZK",
            ["DT"] = "20260225",
            ["VS"] = "2026001",
            ["VII"] = "CZ12345678"
        };

        var result = SpdIntegrator.BuildSpdWithInvoice(attributes);

        result.ShouldStartWith("SPD*1.0*");
        result.ShouldContain("ACC:CZ5855000000001265098001");
        result.ShouldContain("AM:5850.00");
        result.ShouldContain("X-VS:2026001");
        result.ShouldContain("X-INV:");
    }

    // ─── QrPaymentService Tests (end-to-end) ────────────────────────────────

    [Fact]
    public async Task GenerateSindStringAsync_ValidInvoice_ReturnsSindString()
    {
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        var result = await service.GenerateSindStringAsync(1);

        result.ShouldStartWith("SID*1.0*");
        result.ShouldContain("ID:INV2026001*");
        result.ShouldContain("AM:5850.00*");
        result.ShouldContain("DD:20260211*");
        result.ShouldContain("VS:2026001*");
        result.ShouldContain("ACC:CZ5855000000001265098001+RZBCCZPP*");
        result.ShouldContain("VII:CZ12345678*");
        result.ShouldContain("INI:12345678*");
        result.ShouldContain("VIR:CZ98765432*");
        result.ShouldContain("INR:98765432*");
        // CRC32 is the last token — no trailing * (per SIND spec)
        Regex.IsMatch(result, @"CRC32:[0-9A-F]{8}$").ShouldBeTrue();
    }

    [Fact]
    public async Task GenerateSindStringAsync_NonExistentInvoice_ThrowsKeyNotFoundException()
    {
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        var act = () => service.GenerateSindStringAsync(999);
        await Should.ThrowAsync<KeyNotFoundException>(act);
    }

    [Fact]
    public async Task GenerateSpdWithInvoiceAsync_WithIban_ReturnsSimpleSpdString()
    {
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        var result = await service.GenerateSpdWithInvoiceAsync(1);

        // Should be simple SPD format (QR Platba) — no X-INV
        result.ShouldStartWith("SPD*1.0*");
        result.ShouldContain("ACC:CZ5855000000001265098001+RZBCCZPP");
        result.ShouldContain("AM:5850.00");
        result.ShouldContain("CC:CZK");
        result.ShouldContain("DT:20260225");
        result.ShouldContain("MSG:INV2026001");
        result.ShouldContain("X-VS:2026001");

        // Must NOT contain X-INV (simple SPD, no embedded invoice data)
        result.ShouldNotContain("X-INV");
        result.ShouldNotEndWith("*");
    }

    [Fact]
    public async Task GenerateSpdWithInvoiceAsync_WithoutIban_FallsBackToSind()
    {
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        // Invoice 2 has no IBAN
        var result = await service.GenerateSpdWithInvoiceAsync(2);

        // Should fall back to SIND format (QR Faktura only)
        result.ShouldStartWith("SID*1.0*");
        result.ShouldContain("ID:INV2026002*");
        result.ShouldNotContain("SPD*");
    }

    [Fact]
    public async Task GenerateQrCodeImageAsync_ValidInvoice_ReturnsPngBytes()
    {
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        var result = await service.GenerateQrCodeImageAsync(1, pixelsPerModule: 5);

        // PNG files start with the 8-byte PNG signature: 137 80 78 71 13 10 26 10
        result.ShouldNotBeNull();
        result.Length.ShouldBeGreaterThan(0);
        result[0].ShouldBe((byte)0x89); // PNG signature byte 1
        result[1].ShouldBe((byte)0x50); // 'P'
        result[2].ShouldBe((byte)0x4E); // 'N'
        result[3].ShouldBe((byte)0x47); // 'G'
    }

    [Fact]
    public async Task GenerateQrCodeImageAsync_NonExistentInvoice_ThrowsKeyNotFoundException()
    {
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        var act = () => service.GenerateQrCodeImageAsync(999);
        await Should.ThrowAsync<KeyNotFoundException>(act);
    }

    [Fact]
    public async Task GenerateSindStringAsync_VatBreakdown_GroupsByRate()
    {
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        // Invoice 1 has items at 21% (3000 base, 630 tax) and 12% (2000 base, 240 tax)
        var result = await service.GenerateSindStringAsync(1);

        result.ShouldContain("TB0:3000.00*");  // Standard rate base (21%)
        result.ShouldContain("T0:630.00*");     // Standard rate tax
        result.ShouldContain("TB1:2000.00*");   // Reduced rate base (12%)
        result.ShouldContain("T1:240.00*");     // Reduced rate tax
    }

    [Fact]
    public async Task GenerateSindStringAsync_ZeroVatInvoice_SetsNtb()
    {
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        // Invoice 2 has only 0% VAT items
        var result = await service.GenerateSindStringAsync(2);

        result.ShouldContain("NTB:1000.00*");
        result.ShouldNotContain("TB0:");
        result.ShouldNotContain("T0:");
    }

    [Fact]
    public async Task GenerateSindStringAsync_CreditNote_SetsDocumentType1()
    {
        // Add a credit note to test DocumentType mapping
        var creditNote = new Invoice
        {
            Id = 3,
            DocumentType = EDocumentType.CreditNote,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "CN2026001",
            IssueDate = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = 1,
            ClientId = 2,
            CurrencyId = 1,
            TotalWithVat = 500m,
            TotalBeforeVat = 500m,
            TotalVat = 0m,
            InvoiceItem = new List<InvoiceItem>()
        };
        _context.Invoice.Add(creditNote);
        _context.SaveChanges();

        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        var result = await service.GenerateSindStringAsync(3);

        // Credit note → TD:1 (corrective document)
        result.ShouldContain("TD:1*");
    }

    // ─── Paylibo API Strategy Tests (Strategy 2: Czech bank account, no IBAN) ──

    [Fact]
    public async Task GenerateQrCodeImageAsync_CzechBankAccount_NoIban_CallsPayliboApi()
    {
        // Invoice 10 has BankAccountNumber="1342333010/3030" but no IBAN.
        // QrPaymentService should use Strategy 2 — call paylibo API.
        var fakePngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG signature
        _payliboClient.CreateQrPaymentImageAsync(Arg.Any<PayliboQrOptions>())
            .Returns(fakePngBytes);

        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        var result = await service.GenerateQrCodeImageAsync(10, pixelsPerModule: 5);

        // Should return the paylibo PNG bytes
        result.ShouldBe(fakePngBytes);
        // Verify paylibo was called with correct bank account details
        await _payliboClient.Received(1).CreateQrPaymentImageAsync(
            Arg.Is<PayliboQrOptions>(o =>
                o.accountNumber == "1342333010" &&
                o.bankCode == "3030" &&
                o.accountPrefix == null &&
                o.amount == 2420m &&
                o.currency == "CZK" &&
                o.vs == "2026010"));
    }

    [Fact]
    public async Task GenerateQrCodeImageAsync_PayliboFails_FallsBackToSind()
    {
        // When paylibo API returns empty (failure), service should fall back to SIND QR Faktura.
        _payliboClient.CreateQrPaymentImageAsync(Arg.Any<PayliboQrOptions>())
            .Returns(Array.Empty<byte>());

        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        var result = await service.GenerateQrCodeImageAsync(10, pixelsPerModule: 5);

        // Should fall back to local QR generation (SIND) — returns valid PNG
        result.ShouldNotBeNull();
        result.Length.ShouldBeGreaterThan(0);
        result[0].ShouldBe((byte)0x89); // PNG signature
    }

    [Fact]
    public async Task GenerateQrCodeImageAsync_WithIban_DoesNotCallPaylibo()
    {
        // Invoice 1 has IBAN — should use Strategy 1 (local SPD), never call paylibo.
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        await service.GenerateQrCodeImageAsync(1, pixelsPerModule: 5);

        // Paylibo should NOT be called when IBAN is available
        await _payliboClient.DidNotReceive().CreateQrPaymentImageAsync(Arg.Any<PayliboQrOptions>());
    }

    [Fact]
    public async Task GenerateSpdWithInvoiceAsync_CzechBankAccountNoIban_FallsToSind()
    {
        // GenerateSpdWithInvoiceAsync does NOT use paylibo — only IBAN or SIND fallback.
        // Invoice 10 has BankAccountNumber but no IBAN → SIND fallback.
        var logger = Substitute.For<ILogger<QrPaymentService>>();
        var service = new QrPaymentService(_context, _payliboClient, logger);

        var result = await service.GenerateSpdWithInvoiceAsync(10);

        // Should return SIND (QR Faktura) format since there's no IBAN
        result.ShouldStartWith("SID*1.0*");
        result.ShouldContain("ID:INV2026010*");
    }

    // ─── ParseCzechBankAccount Tests ──────────────────────────────────────

    [Fact]
    public void PayliboQrOptions_ToString_FormatsQueryString()
    {
        // Verify the reflection-based ToString builds correct query parameters.
        var options = new PayliboQrOptions
        {
            accountNumber = "1342333010",
            bankCode = "3030",
            amount = 2420.50m,
            currency = "CZK",
            vs = "2026010",
            date = new DateOnly(2026, 3, 24),
            message = "INV2026010",
            size = 200,
            branding = false,
            compress = false
        };

        var queryString = options.ToString();

        queryString.ShouldContain("accountNumber=1342333010");
        queryString.ShouldContain("bankCode=3030");
        queryString.ShouldContain("amount=2420.50");   // Invariant culture (dot separator)
        queryString.ShouldContain("currency=CZK");
        queryString.ShouldContain("vs=2026010");
        queryString.ShouldContain("date=2026-03-24");   // ISO 8601
        queryString.ShouldContain("message=INV2026010");
    }

    [Fact]
    public void PayliboQrOptions_ToString_OmitsNullValues()
    {
        // Null properties should not appear in the query string at all.
        var options = new PayliboQrOptions
        {
            accountNumber = "1342333010",
            bankCode = "3030",
            amount = 100m
            // accountPrefix, vs, ks, ss, date, message, identifier are all null
        };

        var queryString = options.ToString();

        queryString.ShouldContain("accountNumber=1342333010");
        // Use &KEY= or start-of-string KEY= patterns to avoid matching substrings
        // (e.g., "ss=" would match "compress=False" — must check "ss=" as standalone param)
        queryString.ShouldNotContain("accountPrefix=");
        queryString.ShouldNotContain("&vs=");
        queryString.ShouldNotContain("&ks=");
        queryString.ShouldNotContain("&ss=");
        queryString.ShouldNotStartWith("vs=");
        queryString.ShouldNotStartWith("ks=");
        queryString.ShouldNotStartWith("ss=");
        queryString.ShouldNotContain("identifier=");
        queryString.ShouldNotContain("&date=");
        queryString.ShouldNotContain("&message=");
    }

    // ─── Bug Fix Regression Tests ─────────────────────────────────────────

    [Fact]
    public void SetAccount_WithSpacesInIban_CleansWhitespace()
    {
        // Users often enter IBANs with spaces (e.g., copied from bank statements).
        // The ACC field must contain a clean IBAN without any whitespace or dashes.
        var builder = new SindBuilder()
            .SetDocumentId("TEST")
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetAmount(100m)
            .SetAccount("CZ58 5500 0000 0012 6509 8001", "RZBCCZPP");

        var result = builder.BuildWithoutCrc();

        // Spaces should be stripped — IBAN must be continuous
        result.ShouldContain("ACC:CZ5855000000001265098001+RZBCCZPP*");
        result.ShouldNotContain(" ");
    }

    [Fact]
    public void BuildSimpleSpdString_NoTrailingAsterisk()
    {
        // Per the official SPD spec, the output must NOT end with a trailing *.
        // Example from spec: SPD*1.0*ACC:CZ...123*AM:450.00*CC:CZK*MSG:PLATBA*X-VS:123
        var result = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ5855000000001265098001",
            swift: null,
            amount: 100m,
            currencyCode: "CZK",
            dueDate: null,
            variableSymbol: null,
            message: null);

        result.ShouldStartWith("SPD*1.0*");
        // Must NOT end with * — strict banking app parsers reject trailing *
        result.ShouldNotEndWith("*");
    }

    [Fact]
    public void SindBuild_NoTrailingAsteriskAfterCrc()
    {
        // The complete SIND string must NOT end with * after the CRC32 checksum.
        var builder = new SindBuilder()
            .SetDocumentId("INV001")
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetAmount(100m);

        var result = builder.Build();

        // Should end with CRC32 hex value, no trailing *
        Regex.IsMatch(result, @"CRC32:[0-9A-F]{8}$").ShouldBeTrue();
        result.ShouldNotEndWith("*");
    }

    [Fact]
    public void SetAccount_WithDashesInIban_CleansDashes()
    {
        // Some systems format IBANs with dashes — these must also be stripped.
        var builder = new SindBuilder()
            .SetDocumentId("TEST")
            .SetIssueDate(new DateTime(2026, 1, 1))
            .SetAmount(100m)
            .SetAccount("CZ58-5500-0000-0012-6509-8001");

        var result = builder.BuildWithoutCrc();

        result.ShouldContain("ACC:CZ5855000000001265098001*");
        result.ShouldNotContain("-");
    }
}
