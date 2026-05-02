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
/// Unit tests for <see cref="AdvanceTaxReceiptService.IssueFromPaidProformaAsync"/>.
///
/// Covers:
///   - Creates DPP with correct fields (DocumentType, Status, dates, VS, OriginalInvoiceId)
///   - Proportional VAT split across multiple VAT rates
///   - Idempotence — second call returns existing DPP, does not create a second one
///   - Overpayment → DPP for full paid amount + HasAlert=true
///   - Exact payment → DPP for proforma total + HasAlert=false
///   - Mode=Disabled → caller responsibility (service always creates if called)
///   - Returns null when proforma not found
///   - Returns null when invoice is not a Proforma type
///   - Proforma paid after final invoice already exists (edge case — same logic applies)
/// </summary>
public class AdvanceTaxReceiptServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly INumberSequenceService _numberSequenceService;
    private readonly AdvanceTaxReceiptService _sut;

    // Seed IDs reused across tests.
    private long _issuerId;
    private long _clientId;
    private long _currencyId;
    private long _vatRate21Id;
    private long _vatRate12Id;

    public AdvanceTaxReceiptServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        _numberSequenceService = Substitute.For<INumberSequenceService>();
        // Default: return "DPP2026001" for any number generation call.
        _numberSequenceService
            .GenerateNextNumberForDocumentTypeAsync(
                EDocumentType.TaxReceiptForAdvance,
                Arg.Any<DateTime>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("DPP2026001");

        _sut = new AdvanceTaxReceiptService(
            _context,
            _numberSequenceService,
            Substitute.For<ILogger<AdvanceTaxReceiptService>>());

        Seed();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Seed helpers ────────────────────────────────────────────────────────

    /// <summary>Inserts minimal reference data that every test needs.</summary>
    private void Seed()
    {
        var issuer = new Client
        {
            CompanyName = "My Company s.r.o.",
            RegistrationNumber = "11111111",
            IsIssuer = true, IsActive = true, IsVatPayer = true,
            Address = new List<Address>(),
            Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(issuer);
        _context.SaveChanges();
        _issuerId = issuer.Id;

        var customer = new Client
        {
            CompanyName = "Customer A s.r.o.",
            RegistrationNumber = "22222222",
            IsIssuer = false, IsActive = true,
            Address = new List<Address>(),
            Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(customer);
        _context.SaveChanges();
        _clientId = customer.Id;

        var currency = new Currency
        {
            Code = "CZK", Name = "Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        };
        _context.Currency.Add(currency);
        _context.SaveChanges();
        _currencyId = currency.Id;

        var vatRate21 = new VatRate
        {
            Name = "21 %", Rate = 21m, IsDefault = true,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        };
        _context.VatRate.Add(vatRate21);
        _context.SaveChanges();
        _vatRate21Id = vatRate21.Id;

        var vatRate12 = new VatRate
        {
            Name = "12 %", Rate = 12m, IsDefault = false,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        };
        _context.VatRate.Add(vatRate12);
        _context.SaveChanges();
        _vatRate12Id = vatRate12.Id;
    }

    /// <summary>Creates a minimal Proforma invoice with configurable items.</summary>
    private Invoice SeedProforma(
        decimal unitPriceExVat = 1000m,
        decimal vatRate = 21m,
        long? vatRateId = null,
        string vs = "2026001",
        EInvoiceStatus status = EInvoiceStatus.Completed)
    {
        var item = new InvoiceItem
        {
            OrderIndex = 1,
            Description = "Service",
            Quantity = 1,
            Unit = "pcs",
            UnitPrice = unitPriceExVat,
            VatRateId = vatRateId,
            VatRatePercentage = vatRate,
            TotalBeforeVat = unitPriceExVat,
            VatAmount = Math.Round(unitPriceExVat * vatRate / 100, 2),
            TotalWithVat = unitPriceExVat + Math.Round(unitPriceExVat * vatRate / 100, 2),
            CreatedAt = DateTime.UtcNow
        };

        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = status,
            DocumentNumber = $"PF2026001",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId,
            ClientId = _clientId,
            CurrencyId = _currencyId,
            VariableSymbol = vs,
            TotalBeforeVat = item.TotalBeforeVat,
            TotalVat = item.VatAmount,
            TotalWithVat = item.TotalWithVat,
            InvoiceItem = new List<InvoiceItem> { item },
            CreatedAt = DateTime.UtcNow
        };

        _context.Invoice.Add(proforma);
        _context.SaveChanges();
        return proforma;
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueFromPaidProformaAsync_CreatesCorrectDocument()
    {
        // Arrange — proforma with 1 item, 21 % VAT, total = 1210 CZK
        var proforma = SeedProforma(unitPriceExVat: 1000m, vatRate: 21m, vatRateId: _vatRate21Id, vs: "2026001");
        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, 1210m, paymentDate);

        // Assert — DPP was created
        dppId.ShouldNotBeNull();

        var dpp = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .FirstAsync(i => i.Id == dppId);

        dpp.DocumentType.ShouldBe(EDocumentType.TaxReceiptForAdvance);
        dpp.Status.ShouldBe(EInvoiceStatus.Completed);
        dpp.OriginalInvoiceId.ShouldBe(proforma.Id);
        dpp.IssueDate.ShouldBe(paymentDate.Date);
        dpp.TaxableSupplyDate.ShouldBe(paymentDate.Date);
        dpp.VariableSymbol.ShouldBe("2026001");
        dpp.IssuerId.ShouldBe(_issuerId);
        dpp.ClientId.ShouldBe(_clientId);
        dpp.CurrencyId.ShouldBe(_currencyId);
        dpp.HasAlert.ShouldBeFalse();
        dpp.TotalWithVat.ShouldBe(1210m);
        dpp.DocumentNumber.ShouldBe("DPP2026001");
    }

    [Fact]
    public async Task IssueFromPaidProformaAsync_SingleVatRate_CorrectAmounts()
    {
        // Arrange — proforma 1000 + 21 % VAT = 1210 CZK
        var proforma = SeedProforma(1000m, 21m, _vatRate21Id);
        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, 1210m, paymentDate);

        // Assert — exactly 1 line item, amounts match proforma
        var dpp = await _context.Invoice.Include(i => i.InvoiceItem).FirstAsync(i => i.Id == dppId);
        dpp.InvoiceItem.Count.ShouldBe(1);
        var item = dpp.InvoiceItem.First();
        item.VatRatePercentage.ShouldBe(21m);
        item.TotalWithVat.ShouldBe(1210m);
        // TotalBeforeVat = 1210 / 1.21 = 1000
        item.TotalBeforeVat.ShouldBe(Math.Round(1210m / 1.21m, 2));
    }

    [Fact]
    public async Task IssueFromPaidProformaAsync_MultipleVatRates_ProportionalSplit()
    {
        // Arrange — proforma with TWO items: 1000 at 21 % + 500 at 12 %
        // Total = 1210 + 560 = 1770 CZK
        var item21 = new InvoiceItem
        {
            OrderIndex = 1, Description = "Service A", Quantity = 1, Unit = "pcs",
            UnitPrice = 1000m, VatRateId = _vatRate21Id, VatRatePercentage = 21m,
            TotalBeforeVat = 1000m, VatAmount = 210m, TotalWithVat = 1210m,
            CreatedAt = DateTime.UtcNow
        };
        var item12 = new InvoiceItem
        {
            OrderIndex = 2, Description = "Service B", Quantity = 1, Unit = "pcs",
            UnitPrice = 500m, VatRateId = _vatRate12Id, VatRatePercentage = 12m,
            TotalBeforeVat = 500m, VatAmount = 60m, TotalWithVat = 560m,
            CreatedAt = DateTime.UtcNow
        };
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma, Status = EInvoiceStatus.Completed,
            DocumentNumber = "PF2026002",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId, ClientId = _clientId, CurrencyId = _currencyId,
            VariableSymbol = "2026002",
            TotalBeforeVat = 1500m, TotalVat = 270m, TotalWithVat = 1770m,
            InvoiceItem = new List<InvoiceItem> { item21, item12 },
            CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act — pay full amount
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, 1770m, paymentDate);

        // Assert — 2 DPP items (one per VAT rate), totals sum to 1770
        var dpp = await _context.Invoice.Include(i => i.InvoiceItem).FirstAsync(i => i.Id == dppId);
        dpp.InvoiceItem.Count.ShouldBe(2);

        var total = dpp.InvoiceItem.Sum(i => i.TotalWithVat);
        total.ShouldBe(1770m);

        // Each item belongs to the correct VAT bucket
        dpp.InvoiceItem.ShouldAllBe(i => i.VatRatePercentage == 21m || i.VatRatePercentage == 12m);

        // Proportional amounts: 21 % item gets 1210/1770 of 1770 = 1210, 12 % item gets 560
        var dppItem21 = dpp.InvoiceItem.First(i => i.VatRatePercentage == 21m);
        var dppItem12 = dpp.InvoiceItem.First(i => i.VatRatePercentage == 12m);

        dppItem21.TotalWithVat.ShouldBe(1210m);
        dppItem12.TotalWithVat.ShouldBe(560m);
    }

    [Fact]
    public async Task IssueFromPaidProformaAsync_Idempotent_DoesNotCreateSecondDpp()
    {
        // Arrange
        var proforma = SeedProforma(1000m, 21m, _vatRate21Id);
        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act — call TWICE
        var dppId1 = await _sut.IssueFromPaidProformaAsync(proforma.Id, 1210m, paymentDate);
        var dppId2 = await _sut.IssueFromPaidProformaAsync(proforma.Id, 1210m, paymentDate);

        // Assert — same ID returned, only one DPP in DB
        dppId1.ShouldNotBeNull();
        dppId2.ShouldBe(dppId1);

        var dppCount = await _context.Invoice
            .CountAsync(i => i.OriginalInvoiceId == proforma.Id
                          && i.DocumentType == EDocumentType.TaxReceiptForAdvance
                          && i.Status != EInvoiceStatus.Deleted);
        dppCount.ShouldBe(1);
    }

    [Fact]
    public async Task IssueFromPaidProformaAsync_Overpayment_SetsHasAlertTrue()
    {
        // Arrange — proforma total = 1210 CZK, but payment = 1500 CZK (overpayment)
        var proforma = SeedProforma(1000m, 21m, _vatRate21Id);
        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, paidAmount: 1500m, paymentDate);

        // Assert — DPP issued for full 1500, HasAlert = true
        dppId.ShouldNotBeNull();
        var dpp = await _context.Invoice.Include(i => i.InvoiceItem).FirstAsync(i => i.Id == dppId);

        dpp.TotalWithVat.ShouldBe(1500m);
        dpp.HasAlert.ShouldBeTrue();
    }

    [Fact]
    public async Task IssueFromPaidProformaAsync_ExactPayment_HasAlertFalse()
    {
        // Arrange — exact payment = proforma total
        var proforma = SeedProforma(1000m, 21m, _vatRate21Id);
        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, paidAmount: 1210m, paymentDate);

        // Assert
        dppId.ShouldNotBeNull();
        var dpp = await _context.Invoice.FirstAsync(i => i.Id == dppId);
        dpp.HasAlert.ShouldBeFalse();
        dpp.TotalWithVat.ShouldBe(1210m);
    }

    [Fact]
    public async Task IssueFromPaidProformaAsync_ProformaNotFound_ReturnsNull()
    {
        // Act — nonexistent proforma ID
        var result = await _sut.IssueFromPaidProformaAsync(
            proformaInvoiceId: 99999,
            paidAmount: 1000m,
            paymentDate: DateTime.UtcNow);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task IssueFromPaidProformaAsync_NotProformaType_ReturnsNull()
    {
        // Arrange — regular invoice, not a Proforma
        var regularInvoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Paid,
            DocumentNumber = "INV2026001",
            IssueDate = DateTime.UtcNow.Date,
            TaxableSupplyDate = DateTime.UtcNow.Date,
            DueDate = DateTime.UtcNow.AddDays(14).Date,
            IssuerId = _issuerId,
            ClientId = _clientId,
            CurrencyId = _currencyId,
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m,
            InvoiceItem = new List<InvoiceItem>(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(regularInvoice);
        _context.SaveChanges();

        // Act
        var result = await _sut.IssueFromPaidProformaAsync(
            regularInvoice.Id, paidAmount: 1210m, paymentDate: DateTime.UtcNow);

        // Assert — regular invoices don't get DPP
        result.ShouldBeNull();
    }

    [Fact]
    public async Task IssueFromPaidProformaAsync_ProformaPaidAfterFinalInvoice_StillCreatesDpp()
    {
        // Arrange — edge case: proforma is paid after a final invoice was already issued.
        // The spec says: "Disabled = nothing, otherwise issue DPP; see response '6) podle nastavení'".
        // AdvanceTaxReceiptService does NOT check for existing final invoices — that is the
        // caller's responsibility (mode check). When called, the service always creates the DPP.
        var proforma = SeedProforma(1000m, 21m, _vatRate21Id);

        // Seed a final invoice that references the proforma
        var finalInvoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2026010",
            IssueDate = DateTime.UtcNow.Date,
            TaxableSupplyDate = DateTime.UtcNow.Date,
            DueDate = DateTime.UtcNow.AddDays(14).Date,
            IssuerId = _issuerId,
            ClientId = _clientId,
            CurrencyId = _currencyId,
            OriginalInvoiceId = proforma.Id,
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m,
            InvoiceItem = new List<InvoiceItem>(),
            CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(finalInvoice);
        _context.SaveChanges();

        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act — issue DPP even though a final invoice exists
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, 1210m, paymentDate);

        // Assert — DPP is created; the final invoice does NOT prevent it
        dppId.ShouldNotBeNull();
        var dpp = await _context.Invoice.FirstAsync(i => i.Id == dppId);
        dpp.DocumentType.ShouldBe(EDocumentType.TaxReceiptForAdvance);
    }

    [Fact]
    public async Task IssueFromPaidProformaAsync_NumberSequenceFallback_GeneratesDocNumber()
    {
        // Arrange — make the number sequence throw to exercise the fallback path
        _numberSequenceService
            .GenerateNextNumberForDocumentTypeAsync(
                EDocumentType.TaxReceiptForAdvance,
                Arg.Any<DateTime>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(
                new InvalidOperationException("No default sequence configured")));

        var proforma = SeedProforma(1000m, 21m, _vatRate21Id);

        // Act
        var dppId = await _sut.IssueFromPaidProformaAsync(
            proforma.Id, 1210m, new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc));

        // Assert — document number was generated via fallback (starts with "DPP")
        dppId.ShouldNotBeNull();
        var dpp = await _context.Invoice.FirstAsync(i => i.Id == dppId);
        dpp.DocumentNumber.ShouldStartWith("DPP");
    }
}
