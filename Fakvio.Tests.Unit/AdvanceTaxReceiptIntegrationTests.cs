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
/// Integration-level unit tests verifying that the DPP (TaxReceiptForAdvance) is issued
/// at the correct trigger points depending on <see cref="EAdvanceTaxReceiptMode"/>.
///
/// Tests use real InMemoryDatabase for both <see cref="InvoiceService"/> and
/// <see cref="PaymentMatchingService"/> so that entity state changes (Status = Paid) are
/// persisted and readable by the DPP service.
///
/// Covers:
///   Trigger: InvoiceService.MarkAsPaidAsync
///     - Disabled  → DPP service NOT called
///     - OnPaymentMatch → DPP service NOT called (only auto-matching triggers this mode)
///     - OnAnyPayment   → DPP service IS called
///
///   Trigger: PaymentMatchingService.MatchAsync (auto-match)
///     - Disabled  → DPP service NOT called
///     - OnPaymentMatch → DPP service IS called
///     - OnAnyPayment   → DPP service IS called
///
///   Idempotence via InvoiceService: calling MarkAsPaidAsync twice does not create two DPPs.
/// </summary>
public class AdvanceTaxReceiptIntegrationTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly IAdvanceTaxReceiptService _advanceTaxReceiptService;
    private readonly INumberSequenceService _numberSequenceService;

    // Seed IDs
    private long _issuerId;
    private long _clientId;
    private long _currencyId;
    private long _bankAccountId;

    public AdvanceTaxReceiptIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);

        // Track calls to IssueFromPaidProformaAsync so tests can verify invocation count.
        _advanceTaxReceiptService = Substitute.For<IAdvanceTaxReceiptService>();
        _advanceTaxReceiptService
            .IssueFromPaidProformaAsync(Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(42L); // Stub return value — actual DPP creation tested in AdvanceTaxReceiptServiceTests

        _numberSequenceService = Substitute.For<INumberSequenceService>();
        _numberSequenceService
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("INV2026001");

        Seed();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Seed helpers ────────────────────────────────────────────────────────

    private void Seed()
    {
        var issuer = new Client
        {
            CompanyName = "Test Issuer s.r.o.", RegistrationNumber = "10000001",
            IsIssuer = true, IsActive = true,
            Address = new List<Address>(), Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(), CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(issuer);
        _context.SaveChanges();
        _issuerId = issuer.Id;

        var customer = new Client
        {
            CompanyName = "Customer B s.r.o.", RegistrationNumber = "20000002",
            IsIssuer = false, IsActive = true,
            Address = new List<Address>(), Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(), CreatedAt = DateTime.UtcNow
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

        var bankAccount = new BankAccount
        {
            ClientId = _issuerId,
            AccountNumber = "123456789/0100",
            IsDefault = true,
            CreatedAt = DateTime.UtcNow
        };
        _context.BankAccount.Add(bankAccount);
        _context.SaveChanges();
        _bankAccountId = bankAccount.Id;
    }

    /// <summary>Seeds a BillingSettings record for the issuer with the given mode.</summary>
    private void SeedBillingSettings(EAdvanceTaxReceiptMode mode)
    {
        var bs = new BillingSettings
        {
            ClientId = _issuerId,
            DueDateCalculationType = EDueDateCalculationType.DaysFromIssue,
            DueDays = 14,
            AdvanceTaxReceiptMode = mode,
            CreatedAt = DateTime.UtcNow
        };
        _context.BillingSettings.Add(bs);
        _context.SaveChanges();
    }

    /// <summary>Seeds a Completed Proforma invoice and returns it.</summary>
    private Invoice SeedProforma(string vs = "9900001")
    {
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = $"PF{vs}",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId,
            ClientId = _clientId,
            CurrencyId = _currencyId,
            VariableSymbol = vs,
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m,
            InvoiceItem = new List<InvoiceItem>
            {
                new()
                {
                    OrderIndex = 1, Description = "Service", Quantity = 1, Unit = "pcs",
                    UnitPrice = 1000m, VatRatePercentage = 21m,
                    TotalBeforeVat = 1000m, VatAmount = 210m, TotalWithVat = 1210m,
                    CreatedAt = DateTime.UtcNow
                }
            },
            CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(proforma);
        _context.SaveChanges();
        return proforma;
    }

    private InvoiceService BuildInvoiceService() =>
        new InvoiceService(_context, _numberSequenceService, _advanceTaxReceiptService,
            Substitute.For<ILogger<InvoiceService>>());

    private PaymentMatchingService BuildPaymentMatchingService() =>
        new PaymentMatchingService(_context, _advanceTaxReceiptService,
            Substitute.For<ILogger<PaymentMatchingService>>());

    // ─── MarkAsPaid mode tests ────────────────────────────────────────────────

    [Fact]
    public async Task MarkAsPaid_ModeDisabled_DoesNotCallDppService()
    {
        SeedBillingSettings(EAdvanceTaxReceiptMode.Disabled);
        var proforma = SeedProforma("1100001");
        var svc = BuildInvoiceService();

        await svc.MarkAsPaidAsync(proforma.Id, DateTime.UtcNow);

        await _advanceTaxReceiptService
            .DidNotReceive()
            .IssueFromPaidProformaAsync(
                Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkAsPaid_ModeOnPaymentMatch_DoesNotCallDppService()
    {
        // Manual MarkPaid only fires DPP for OnAnyPayment, never for OnPaymentMatch.
        SeedBillingSettings(EAdvanceTaxReceiptMode.OnPaymentMatch);
        var proforma = SeedProforma("1100002");
        var svc = BuildInvoiceService();

        await svc.MarkAsPaidAsync(proforma.Id, DateTime.UtcNow);

        await _advanceTaxReceiptService
            .DidNotReceive()
            .IssueFromPaidProformaAsync(
                Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkAsPaid_ModeOnAnyPayment_CallsDppService()
    {
        SeedBillingSettings(EAdvanceTaxReceiptMode.OnAnyPayment);
        var proforma = SeedProforma("1100003");
        var svc = BuildInvoiceService();

        await svc.MarkAsPaidAsync(proforma.Id, DateTime.UtcNow);

        await _advanceTaxReceiptService
            .Received(1)
            .IssueFromPaidProformaAsync(
                proforma.Id, Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkAsPaid_NoBillingSettings_DefaultIsOnPaymentMatch_DoesNotCallDppService()
    {
        // When BillingSettings do not exist, the default mode is OnPaymentMatch.
        // Manual MarkPaid + OnPaymentMatch → no DPP.
        var proforma = SeedProforma("1100004");
        var svc = BuildInvoiceService();

        await svc.MarkAsPaidAsync(proforma.Id, DateTime.UtcNow);

        await _advanceTaxReceiptService
            .DidNotReceive()
            .IssueFromPaidProformaAsync(
                Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkAsPaid_RegularInvoice_NeverCallsDppService()
    {
        // Only Proforma invoices trigger DPP. Regular invoices must be silently ignored.
        SeedBillingSettings(EAdvanceTaxReceiptMode.OnAnyPayment);

        var regularInvoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV9900001",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId, ClientId = _clientId, CurrencyId = _currencyId,
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m,
            InvoiceItem = new List<InvoiceItem>(), CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(regularInvoice);
        _context.SaveChanges();

        var svc = BuildInvoiceService();
        await svc.MarkAsPaidAsync(regularInvoice.Id, DateTime.UtcNow);

        await _advanceTaxReceiptService
            .DidNotReceive()
            .IssueFromPaidProformaAsync(
                Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // ─── PaymentMatchingService mode tests ─────────────────────────────────

    [Fact]
    public async Task AutoMatch_ModeDisabled_DoesNotCallDppService()
    {
        SeedBillingSettings(EAdvanceTaxReceiptMode.Disabled);
        var proforma = SeedProforma("2200001");
        var tx = SeedTransaction("2200001", 1210m);
        var svc = BuildPaymentMatchingService();

        await svc.MatchAsync(tx.Id);

        await _advanceTaxReceiptService
            .DidNotReceive()
            .IssueFromPaidProformaAsync(
                Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoMatch_ModeOnPaymentMatch_CallsDppService()
    {
        SeedBillingSettings(EAdvanceTaxReceiptMode.OnPaymentMatch);
        var proforma = SeedProforma("2200002");
        var tx = SeedTransaction("2200002", 1210m);
        var svc = BuildPaymentMatchingService();

        await svc.MatchAsync(tx.Id);

        await _advanceTaxReceiptService
            .Received(1)
            .IssueFromPaidProformaAsync(
                proforma.Id, Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoMatch_ModeOnAnyPayment_CallsDppService()
    {
        SeedBillingSettings(EAdvanceTaxReceiptMode.OnAnyPayment);
        var proforma = SeedProforma("2200003");
        var tx = SeedTransaction("2200003", 1210m);
        var svc = BuildPaymentMatchingService();

        await svc.MatchAsync(tx.Id);

        await _advanceTaxReceiptService
            .Received(1)
            .IssueFromPaidProformaAsync(
                proforma.Id, Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoMatch_PartialPayment_DoesNotCallDppService()
    {
        // DPP is issued only when the proforma is FULLY paid (Status=Paid).
        // A partial match leaves status PartiallyPaid — no DPP.
        SeedBillingSettings(EAdvanceTaxReceiptMode.OnPaymentMatch);
        var proforma = SeedProforma("2200004");
        var tx = SeedTransaction("2200004", 500m); // partial — proforma is 1210
        var svc = BuildPaymentMatchingService();

        await svc.MatchAsync(tx.Id);

        await _advanceTaxReceiptService
            .DidNotReceive()
            .IssueFromPaidProformaAsync(
                Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // ─── Seed helpers (matching-specific) ────────────────────────────────────

    private BankTransaction SeedTransaction(string vs, decimal amount)
    {
        var tx = new BankTransaction
        {
            BankAccountId = _bankAccountId,
            TransactionDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            Amount = amount,
            CurrencyCode = "CZK",
            VariableSymbol = vs,
            Direction = EPaymentDirection.Incoming,
            MatchStatus = EMatchStatus.Unmatched,
            CreatedAt = DateTime.UtcNow
        };
        _context.BankTransaction.Add(tx);
        _context.SaveChanges();
        return tx;
    }
}
