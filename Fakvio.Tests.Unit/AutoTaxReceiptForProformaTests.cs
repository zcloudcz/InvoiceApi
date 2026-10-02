using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
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
/// Auto-issuance of the tax receipt for advance payment (DPP) when a Proforma is paid.
/// Covers the manual mark-paid path and the bank payment matching path (both call
/// <see cref="InvoiceService.TryAutoIssueTaxReceiptAsync"/>), idempotency, partial payments,
/// non-VAT-payer issuers and the tenant opt-out setting.
/// </summary>
public class AutoTaxReceiptForProformaTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly ITenantReadinessService _readiness = Substitute.For<ITenantReadinessService>();
    private int _seq = 100;

    public AutoTaxReceiptForProformaTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);

        var numbers = Substitute.For<INumberSequenceService>();
        numbers.GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => (_seq++).ToString());

        _service = new InvoiceService(
            _context, numbers, _readiness,
            Substitute.For<ILogger<InvoiceService>>());

        _context.Client.Add(new Client { Id = 10, CompanyName = "Customer", RegistrationNumber = "C1", IsActive = true });
        _context.Client.Add(new Client { Id = 11, CompanyName = "Issuer", RegistrationNumber = "C2", IsIssuer = true, IsActive = true, IsVatPayer = true });
        _context.Currency.Add(new Currency { Id = 10, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true });
        _context.VatRate.Add(new VatRate { Id = 10, Name = "21%", Rate = 21, IsDefault = true, ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>Completed (unpaid) proforma for 1210 CZK incl. 21% VAT.</summary>
    private Invoice AddProforma(decimal total = 1210m, EVatRegime regime = EVatRegime.Standard)
    {
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = $"PF-{_seq++}",
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            ClientId = 10, IssuerId = 11, CurrencyId = 10,
            TotalBeforeVat = total, TotalVat = 0m, TotalWithVat = total,
            InvoiceItem =
            [
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Consulting", Quantity = 1, Unit = "pcs",
                    UnitPrice = total, VatRateId = 10, VatRatePercentage = 21, VatRegime = regime,
                    ReverseChargeCodeId = regime == EVatRegime.ReverseCharge ? 1 : null,
                    TotalBeforeVat = total, VatAmount = 0m, TotalWithVat = total
                }
            ]
        };
        _context.Invoice.Add(proforma);
        _context.SaveChanges();
        return proforma;
    }

    private Task<List<Invoice>> Receipts(long proformaId) =>
        _context.Invoice.AsNoTracking()
            .Where(i => i.OriginalInvoiceId == proformaId && i.DocumentType == EDocumentType.TaxReceiptForAdvance)
            .ToListAsync();

    // ── manual mark-paid ────────────────────────────────────────────────

    [Fact]
    public async Task MarkAsPaid_Proforma_IssuesCompletedTaxReceiptForFullAmount()
    {
        var proforma = AddProforma();

        await _service.MarkAsPaidAsync(proforma.Id);

        var receipts = await Receipts(proforma.Id);
        receipts.Count.ShouldBe(1);
        receipts[0].TotalWithVat.ShouldBe(1210m);
        receipts[0].TotalVat.ShouldBe(210m);
        receipts[0].Status.ShouldBe(EInvoiceStatus.Completed);
        receipts[0].DocumentNumber.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task MarkAsPaid_Proforma_SetsPaidAmountSoFinalInvoiceCanDeductIt()
    {
        var proforma = AddProforma();

        await _service.MarkAsPaidAsync(proforma.Id);

        (await _service.GetRemainingAdvanceAsync(proforma.Id)).ShouldBe(1210m);
    }

    [Fact]
    public async Task MarkAsPaid_RegularInvoice_IssuesNothing()
    {
        var invoice = AddProforma();
        invoice.DocumentType = EDocumentType.Invoice;
        _context.SaveChanges();

        await _service.MarkAsPaidAsync(invoice.Id);

        (await Receipts(invoice.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task MarkAsPaid_NonVatPayerIssuer_IssuesNothing()
    {
        _context.Client.Find(11L)!.IsVatPayer = false;
        _context.SaveChanges();
        var proforma = AddProforma();

        await _service.MarkAsPaidAsync(proforma.Id);

        (await Receipts(proforma.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task MarkAsPaid_AutoIssueDisabled_IssuesNothing_ButManualIssueStillWorks()
    {
        _context.Client.Find(11L)!.AutoIssueTaxReceiptForAdvance = false;
        _context.SaveChanges();
        var proforma = AddProforma();

        await _service.MarkAsPaidAsync(proforma.Id);
        (await Receipts(proforma.Id)).ShouldBeEmpty();

        var manual = await _service.IssueTaxReceiptForPaidProformaAsync(proforma.Id, null, null);
        manual.ShouldNotBeNull();
        manual!.TotalWithVat.ShouldBe(1210m);
    }

    // ── idempotency / partial payments ──────────────────────────────────

    [Fact]
    public async Task IssueTaxReceipt_CalledTwice_DoesNotDoubleCover()
    {
        var proforma = AddProforma();
        await _service.MarkAsPaidAsync(proforma.Id);

        var again = await _service.IssueTaxReceiptForPaidProformaAsync(proforma.Id, null, null);
        await _service.TryAutoIssueTaxReceiptAsync(proforma.Id, 1210m, null);

        again.ShouldBeNull();
        (await Receipts(proforma.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PartialPayments_EachIssueReceiptForItsOwnAmount_AndNeverExceedPaid()
    {
        var proforma = AddProforma();
        proforma.PaidAmount = 400m;
        proforma.Status = EInvoiceStatus.PartiallyPaid;
        _context.SaveChanges();

        await _service.TryAutoIssueTaxReceiptAsync(proforma.Id, 400m, null);

        proforma.PaidAmount = 1210m;
        _context.SaveChanges();
        await _service.TryAutoIssueTaxReceiptAsync(proforma.Id, 810m, null);
        // Replay of the second payment must be a no-op (clamped to what is not covered yet).
        await _service.TryAutoIssueTaxReceiptAsync(proforma.Id, 810m, null);

        var receipts = (await Receipts(proforma.Id)).OrderBy(r => r.Id).ToList();
        receipts.Select(r => r.TotalWithVat).ShouldBe([400m, 810m]);
        receipts.Sum(r => r.TotalWithVat).ShouldBe(1210m);
    }

    [Fact]
    public async Task IssueTaxReceipt_NonProforma_Throws()
    {
        var invoice = AddProforma();
        invoice.DocumentType = EDocumentType.Invoice;
        _context.SaveChanges();

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.IssueTaxReceiptForPaidProformaAsync(invoice.Id, null, null));
    }

    [Fact]
    public async Task TryAutoIssue_NeverThrows_WhenProformaMissing()
    {
        await _service.TryAutoIssueTaxReceiptAsync(99999, 100m, null);
    }

    // ── bank payment matching path ──────────────────────────────────────

    [Fact]
    public async Task PaymentMatching_ManualMatchOfProforma_IssuesReceiptForMatchedAmount()
    {
        var proforma = AddProforma();
        var tx = new BankTransaction
        {
            Amount = 500m, TransactionDate = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc),
            MatchStatus = EMatchStatus.Unmatched
        };
        _context.BankTransaction.Add(tx);
        _context.SaveChanges();

        var matcher = new PaymentMatchingService(
            _context, Substitute.For<INotificationService>(), _service,
            Substitute.For<ILogger<PaymentMatchingService>>());

        await matcher.ManualMatchAsync(tx.Id, proforma.Id, 500m, null, null);

        var receipts = await Receipts(proforma.Id);
        receipts.Count.ShouldBe(1);
        receipts[0].TotalWithVat.ShouldBe(500m);
        receipts[0].TaxableSupplyDate.ShouldBe(tx.TransactionDate);
    }

    // ── review fixes ────────────────────────────────────────────────────

    [Theory]
    [InlineData("100.00")]
    [InlineData("1.00")]
    [InlineData("33.33")]
    [InlineData("1210.00")]
    [InlineData("999.99")]
    public async Task Receipt_ForAnyAmount_NeverExceedsPaid_AndLeavesNoStrayDpp(string amount)
    {
        var total = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        var proforma = AddProforma(total);

        await _service.MarkAsPaidAsync(proforma.Id);

        var receipts = await Receipts(proforma.Id);
        receipts.Count.ShouldBe(1);
        // 100.00 at 21% is not reachable with one row (82.64 -> 99.99, 82.65 -> 100.01): we take
        // the lower neighbour, and the leftover cent must NOT turn into a second 0.01 DPP later.
        receipts[0].TotalWithVat.ShouldBeLessThanOrEqualTo(total);
        receipts[0].TotalWithVat.ShouldBeGreaterThanOrEqualTo(total - 0.01m);
        (await _service.IssueTaxReceiptForPaidProformaAsync(proforma.Id, null, null)).ShouldBeNull();
    }

    [Fact]
    public async Task IssueTaxReceipt_DustBelowOneCrown_IsNotCovered()
    {
        var proforma = AddProforma(100m);
        proforma.PaidAmount = 100m;
        _context.SaveChanges();
        await _service.IssueTaxReceiptForPaidProformaAsync(proforma.Id, null, null);
        proforma.PaidAmount = 100.01m; // only 0.01 more than covered
        _context.SaveChanges();

        (await _service.IssueTaxReceiptForPaidProformaAsync(proforma.Id, null, null)).ShouldBeNull();
    }

    [Fact]
    public async Task IssueTaxReceipt_WhenCompletionFails_LeavesNoDraftAndCanBeRetried()
    {
        var proforma = AddProforma();
        proforma.PaidAmount = 1210m;
        _context.SaveChanges();
        _readiness.EnsureReadyAsync(Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("not ready"));

        await _service.TryAutoIssueTaxReceiptAsync(proforma.Id, 1210m, null);

        (await Receipts(proforma.Id)).ShouldAllBe(r => r.Status == EInvoiceStatus.Deleted);

        _readiness.EnsureReadyAsync(Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var retry = await _service.IssueTaxReceiptForPaidProformaAsync(proforma.Id, null, null);
        retry.ShouldNotBeNull();
        retry!.TotalWithVat.ShouldBe(1210m);
    }

    [Fact]
    public async Task MarkPaid_ThenManualMatch_DoesNotIssueSecondReceipt()
    {
        var proforma = AddProforma();
        await _service.MarkAsPaidAsync(proforma.Id);
        var tx = new BankTransaction
        {
            Amount = 1210m, TransactionDate = DateTime.UtcNow, MatchStatus = EMatchStatus.Unmatched
        };
        _context.BankTransaction.Add(tx);
        _context.SaveChanges();
        var matcher = new PaymentMatchingService(
            _context, Substitute.For<INotificationService>(), _service,
            Substitute.For<ILogger<PaymentMatchingService>>());

        await Should.ThrowAsync<InvalidOperationException>(
            () => matcher.ManualMatchAsync(tx.Id, proforma.Id, 1210m, null, null));

        (await Receipts(proforma.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReverseChargeProforma_ReceiptHasNoVat()
    {
        var proforma = AddProforma(1000m, EVatRegime.ReverseCharge);

        await _service.MarkAsPaidAsync(proforma.Id);

        var receipt = (await Receipts(proforma.Id)).Single();
        receipt.TotalVat.ShouldBe(0m);
        receipt.TotalWithVat.ShouldBe(1000m);
        var items = await _context.InvoiceItem.AsNoTracking().Where(i => i.InvoiceId == receipt.Id).ToListAsync();
        items.ShouldAllBe(i => i.VatRegime == EVatRegime.ReverseCharge && i.ReverseChargeCodeId == 1);
    }

    [Fact]
    public async Task CreateTaxReceipt_ExceedingUncoveredAdvance_IsRejected()
    {
        var proforma = AddProforma();
        await _service.MarkAsPaidAsync(proforma.Id); // auto DPP covers everything

        var dto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.TaxReceiptForAdvance, ClientId = 10, IssuerId = 11, CurrencyId = 10,
            OriginalInvoiceId = proforma.Id,
            InvoiceItem = [new CreateInvoiceItemDto { Description = "dup", Quantity = 1, UnitPrice = 1000m, VatRateId = 10 }]
        };

        await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateInvoiceAsync(dto));
    }

    [Fact]
    public async Task Receipt_IsIssuedToday_WithPaymentDateAsDuzp()
    {
        var proforma = AddProforma();
        var paidOn = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);

        await _service.MarkAsPaidAsync(proforma.Id, paidOn);

        var receipt = (await Receipts(proforma.Id)).Single();
        receipt.TaxableSupplyDate.ShouldBe(paidOn);
        receipt.IssueDate!.Value.Date.ShouldBe(DateTime.UtcNow.Date);
    }

    [Fact]
    public async Task UpdateTaxReceipt_ItemsAboveTheAdvance_AreRejected()
    {
        var proforma = AddProforma();
        await _service.MarkAsPaidAsync(proforma.Id);
        var receipt = (await Receipts(proforma.Id)).Single();
        // Draft again so it can be edited (as the UI allows for a draft DPP).
        var tracked = await _context.Invoice.FirstAsync(i => i.Id == receipt.Id);
        tracked.Status = EInvoiceStatus.Draft;
        _context.SaveChanges();

        var tooMuch = new UpdateInvoiceDto
        {
            InvoiceItem = [new CreateInvoiceItemDto { Description = "x", Quantity = 1, UnitPrice = 5000m, VatRateId = 10 }]
        };

        await Should.ThrowAsync<InvalidOperationException>(() => _service.UpdateInvoiceAsync(receipt.Id, tooMuch));
    }
}
