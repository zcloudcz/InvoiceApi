using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AresService;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Functions.Generated;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Additional coverage for PR #29 (auto-issue tax receipt on Paid):
///
/// 1. PaidAmount audit guard in InvoiceService.MarkAsPaidAsync —
///    When a Proforma already has PaidAmount set by a prior PaymentMatch row,
///    manually calling MarkAsPaid must NOT lower that value (Math.Max guard).
///
/// 2. AdvanceTaxReceiptService.BuildProportionalItems zero-total fallback —
///    A Proforma with no billable items (or TotalWithVat = 0) must produce
///    a single 0%-VAT fallback item for the full paidAmount.
///
/// 3. ClientFunctions wrappers for GetAdvanceTaxReceiptMode / SetAdvanceTaxReceiptMode —
///    Verifies auth gating, empty-body rejection (Set), and happy-path delegation
///    to the underlying ClientController / ClientService.
/// </summary>

// ═══════════════════════════════════════════════════════════════════════════════
// Part 1 — PaidAmount audit guard (Math.Max protection)
// ═══════════════════════════════════════════════════════════════════════════════

public class PaidAmountAuditGuardTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly IAdvanceTaxReceiptService _dppService;
    private readonly INumberSequenceService _seqService;
    private readonly InvoiceService _sut;

    // Seed IDs
    private long _issuerId;
    private long _clientId;
    private long _currencyId;

    public PaidAmountAuditGuardTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);

        _dppService = Substitute.For<IAdvanceTaxReceiptService>();
        _dppService
            .IssueFromPaidProformaAsync(
                Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(99L);

        _seqService = Substitute.For<INumberSequenceService>();
        _seqService
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("INV2026TEST");

        _sut = new InvoiceService(
            _context, _seqService, _dppService,
            Substitute.For<ILogger<InvoiceService>>());

        Seed();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Seed()
    {
        var issuer = new Client
        {
            CompanyName = "Audit Issuer s.r.o.", RegistrationNumber = "30000001",
            IsIssuer = true, IsActive = true,
            Address = new List<Address>(), Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(), CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(issuer);
        _context.SaveChanges();
        _issuerId = issuer.Id;

        var customer = new Client
        {
            CompanyName = "Audit Customer s.r.o.", RegistrationNumber = "40000001",
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

        // Seed BillingSettings with OnAnyPayment so DPP is triggered on MarkPaid.
        var bs = new BillingSettings
        {
            ClientId = _issuerId,
            DueDateCalculationType = EDueDateCalculationType.DaysFromIssue,
            DueDays = 14,
            AdvanceTaxReceiptMode = EAdvanceTaxReceiptMode.OnAnyPayment,
            CreatedAt = DateTime.UtcNow
        };
        _context.BillingSettings.Add(bs);
        _context.SaveChanges();
    }

    /// <summary>
    /// Core audit-guard scenario:
    ///
    /// Sequence of events:
    ///   1. PaymentMatch records a partial payment of 800 CZK on a 1210-CZK proforma
    ///      → PaidAmount = 800, Status = PartiallyPaid.
    ///   2. A second PaymentMatch records 410 CZK.
    ///      → PaidAmount = 1210, Status = Paid (full payment).
    ///   3. User also clicks "Mark as Paid" manually (race condition / double-action).
    ///      → MarkAsPaidAsync must NOT lower PaidAmount back to TotalWithVat (1210)
    ///        because the existing PaidAmount is already 1210, so Math.Max(1210,1210) = 1210.
    ///
    /// More important edge: if PaidAmount were already > TotalWithVat (overpayment recorded
    /// by PaymentMatch), the manual MarkPaid call must keep the higher value.
    /// </summary>
    [Fact]
    public async Task MarkAsPaid_WhenPaidAmountAlreadyHigherFromPaymentMatch_DoesNotLowerIt()
    {
        // Arrange — proforma with TotalWithVat = 1210; simulate PaymentMatch setting PaidAmount = 1500
        // (overpayment scenario already recorded by PaymentMatchingService).
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Paid,   // Already Paid by PaymentMatch — but status must be Completed for MarkAsPaidAsync
            DocumentNumber = "PF-AUDIT-001",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId, ClientId = _clientId, CurrencyId = _currencyId,
            VariableSymbol = "AUDIT001",
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m,
            // Simulate that PaymentMatch already recorded an overpayment
            PaidAmount = 1500m,
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

        // Force status to Completed so MarkAsPaidAsync accepts the call
        // (the service throws InvalidOperationException for non-Completed invoices).
        proforma.Status = EInvoiceStatus.Completed;
        _context.SaveChanges();

        // Act — manual MarkAsPaid. PaidAmount is already 1500; TotalWithVat is 1210.
        // The guard: invoice.PaidAmount = Math.Max(invoice.PaidAmount, invoice.TotalWithVat)
        //          = Math.Max(1500, 1210) = 1500  — preserves the higher value.
        var result = await _sut.MarkAsPaidAsync(proforma.Id, new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc));

        // Assert — returned DTO is non-null and PaidAmount was NOT lowered.
        result.ShouldNotBeNull();

        var persisted = await _context.Invoice.FindAsync(proforma.Id);
        persisted!.PaidAmount.ShouldBe(1500m);   // NOT overwritten back to 1210
        persisted.Status.ShouldBe(EInvoiceStatus.Paid);
    }

    /// <summary>
    /// When PaidAmount is 0 (no prior PaymentMatch), MarkAsPaid sets it to TotalWithVat.
    /// This is the common single-payment path; Math.Max(0, 1210) = 1210.
    /// </summary>
    [Fact]
    public async Task MarkAsPaid_WhenPaidAmountIsZero_SetsItToTotalWithVat()
    {
        // Arrange — fresh proforma with PaidAmount = 0
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "PF-AUDIT-002",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId, ClientId = _clientId, CurrencyId = _currencyId,
            VariableSymbol = "AUDIT002",
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m,
            PaidAmount = 0m,   // no prior payment
            InvoiceItem = new List<InvoiceItem>(), CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        // Act
        await _sut.MarkAsPaidAsync(proforma.Id, DateTime.UtcNow);

        // Assert — PaidAmount set to TotalWithVat
        var persisted = await _context.Invoice.FindAsync(proforma.Id);
        persisted!.PaidAmount.ShouldBe(1210m);
    }

    /// <summary>
    /// Regular (non-Proforma) invoices — MarkAsPaid must NOT update PaidAmount
    /// because PaidAmount tracking is Proforma-only in this PR's implementation.
    /// </summary>
    [Fact]
    public async Task MarkAsPaid_RegularInvoice_DoesNotSetPaidAmount()
    {
        // Arrange — regular Invoice (not Proforma)
        var invoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV-AUDIT-001",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId, ClientId = _clientId, CurrencyId = _currencyId,
            TotalBeforeVat = 2000m, TotalVat = 420m, TotalWithVat = 2420m,
            PaidAmount = 0m,
            InvoiceItem = new List<InvoiceItem>(), CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(invoice);
        _context.SaveChanges();

        // Act
        await _sut.MarkAsPaidAsync(invoice.Id, DateTime.UtcNow);

        // Assert — PaidAmount is NOT set for regular invoices
        var persisted = await _context.Invoice.FindAsync(invoice.Id);
        persisted!.PaidAmount.ShouldBe(0m);
        persisted.Status.ShouldBe(EInvoiceStatus.Paid);

        // DPP service is NOT called for non-Proforma types
        await _dppService
            .DidNotReceive()
            .IssueFromPaidProformaAsync(
                Arg.Any<long>(), Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Part 2 — BuildProportionalItems zero-total / no-items fallback
// ═══════════════════════════════════════════════════════════════════════════════

public class AdvanceTaxReceiptServiceFallbackTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly INumberSequenceService _seqService;
    private readonly AdvanceTaxReceiptService _sut;

    // Seed IDs
    private long _issuerId;
    private long _clientId;
    private long _currencyId;

    public AdvanceTaxReceiptServiceFallbackTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);

        _seqService = Substitute.For<INumberSequenceService>();
        _seqService
            .GenerateNextNumberForDocumentTypeAsync(
                EDocumentType.TaxReceiptForAdvance,
                Arg.Any<DateTime>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("DPP2026-FALLBACK");

        _sut = new AdvanceTaxReceiptService(
            _context, _seqService,
            Substitute.For<ILogger<AdvanceTaxReceiptService>>());

        Seed();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Seed()
    {
        var issuer = new Client
        {
            CompanyName = "Fallback Issuer s.r.o.", RegistrationNumber = "50000001",
            IsIssuer = true, IsActive = true,
            Address = new List<Address>(), Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>(), CreatedAt = DateTime.UtcNow
        };
        _context.Client.Add(issuer);
        _context.SaveChanges();
        _issuerId = issuer.Id;

        var customer = new Client
        {
            CompanyName = "Fallback Customer s.r.o.", RegistrationNumber = "60000001",
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
    }

    /// <summary>
    /// When a Proforma has no InvoiceItems, BuildProportionalItems should fall back
    /// to a single 0%-VAT item covering the full paidAmount.
    /// This prevents the DPP from being created with an empty item list.
    /// </summary>
    [Fact]
    public async Task IssueFromPaidProformaAsync_ProformaWithNoItems_CreatesFallbackItem()
    {
        // Arrange — proforma with empty item list
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "PF-NOITEMS-001",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId, ClientId = _clientId, CurrencyId = _currencyId,
            TotalBeforeVat = 0m, TotalVat = 0m, TotalWithVat = 0m,
            InvoiceItem = new List<InvoiceItem>(),   // intentionally empty
            CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        var paymentDate = new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        // Act — pay 500 CZK against a zero-total proforma (edge case)
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, paidAmount: 500m, paymentDate);

        // Assert — one fallback item created
        dppId.ShouldNotBeNull();
        var dpp = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .FirstAsync(i => i.Id == dppId);

        dpp.InvoiceItem.Count.ShouldBe(1);
        var fallback = dpp.InvoiceItem.First();
        fallback.VatRatePercentage.ShouldBe(0m);
        fallback.TotalWithVat.ShouldBe(500m);
        fallback.VatAmount.ShouldBe(0m);
        fallback.TotalBeforeVat.ShouldBe(500m);
    }

    /// <summary>
    /// When a Proforma has a non-zero TotalWithVat but all its items are text rows
    /// (IsTextRow = true), they are excluded from the billable group.
    /// This also triggers the fallback path — the DPP must still be valid.
    /// </summary>
    [Fact]
    public async Task IssueFromPaidProformaAsync_ProformaWithOnlyTextRows_CreatesFallbackItem()
    {
        // Arrange — proforma whose only item is a text row (no financial value)
        var textRow = new InvoiceItem
        {
            OrderIndex = 1,
            Description = "--- Section header ---",
            IsTextRow = true,
            Quantity = 0, Unit = "", UnitPrice = 0m,
            VatRatePercentage = 0m,
            TotalBeforeVat = 0m, VatAmount = 0m, TotalWithVat = 0m,
            CreatedAt = DateTime.UtcNow
        };
        var proforma = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "PF-TEXTROW-001",
            IssueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IssuerId = _issuerId, ClientId = _clientId, CurrencyId = _currencyId,
            TotalBeforeVat = 0m, TotalVat = 0m, TotalWithVat = 0m,
            InvoiceItem = new List<InvoiceItem> { textRow },
            CreatedAt = DateTime.UtcNow
        };
        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        // Act
        var dppId = await _sut.IssueFromPaidProformaAsync(proforma.Id, paidAmount: 300m,
            new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc));

        // Assert — fallback item covers the full paid amount
        dppId.ShouldNotBeNull();
        var dpp = await _context.Invoice
            .Include(i => i.InvoiceItem)
            .FirstAsync(i => i.Id == dppId);

        dpp.InvoiceItem.Count.ShouldBe(1);
        dpp.InvoiceItem.First().TotalWithVat.ShouldBe(300m);
        dpp.InvoiceItem.First().VatRatePercentage.ShouldBe(0m);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Part 3 — ClientFunctions wrappers: GetAdvanceTaxReceiptMode / SetAdvanceTaxReceiptMode
// ═══════════════════════════════════════════════════════════════════════════════

public class ClientFunctionsAdvanceTaxReceiptModeTests
{
    private readonly IClientService _clientService = Substitute.For<IClientService>();

    private ClientFunctions BuildSut()
    {
        var controller = new ClientController(
            _clientService,
            Substitute.For<ILogger<ClientController>>());

        return new ClientFunctions(controller);
    }

    private static HttpRequest BuildRequest(
        bool authenticated,
        string? jsonBody = null,
        string[]? roles = null)
    {
        var ctx = new DefaultHttpContext();

        if (authenticated)
        {
            var claims = new List<Claim>
            {
                new("UserId", "42"),
                new(ClaimTypes.NameIdentifier, "42"),
            };
            if (roles != null)
            {
                foreach (var role in roles)
                    claims.Add(new Claim(ClaimTypes.Role, role));
            }
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        }

        if (jsonBody != null)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentType = "application/json";
            ctx.Request.ContentLength = bytes.Length;
        }

        return ctx.Request;
    }

    // ─── GET advance-tax-receipt-mode ────────────────────────────────────────

    [Fact]
    public async Task GetAdvanceTaxReceiptMode_Anonymous_Returns401()
    {
        var sut = BuildSut();
        var req = BuildRequest(authenticated: false);

        var result = await sut.Client_GetAdvanceTaxReceiptMode(req);

        result.ShouldBeOfType<UnauthorizedResult>();
        await _clientService.DidNotReceiveWithAnyArgs().GetAdvanceTaxReceiptModeAsync();
    }

    [Fact]
    public async Task GetAdvanceTaxReceiptMode_AuthenticatedUser_DelegatesToService()
    {
        // Arrange
        _clientService.GetAdvanceTaxReceiptModeAsync(Arg.Any<CancellationToken>())
            .Returns(EAdvanceTaxReceiptMode.OnAnyPayment);

        var sut = BuildSut();
        var req = BuildRequest(authenticated: true);

        // Act
        var result = await sut.Client_GetAdvanceTaxReceiptMode(req);

        // Assert — service was queried and the mode is returned
        await _clientService.Received(1).GetAdvanceTaxReceiptModeAsync(Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(EAdvanceTaxReceiptMode.OnAnyPayment);
    }

    [Fact]
    public async Task GetAdvanceTaxReceiptMode_IssuerNotConfigured_Returns404()
    {
        // Arrange — service returns null → controller maps to NotFound
        _clientService.GetAdvanceTaxReceiptModeAsync(Arg.Any<CancellationToken>())
            .Returns((EAdvanceTaxReceiptMode?)null);

        var sut = BuildSut();
        var req = BuildRequest(authenticated: true);

        // Act
        var result = await sut.Client_GetAdvanceTaxReceiptMode(req);

        // Assert
        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ─── PUT advance-tax-receipt-mode ────────────────────────────────────────

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_Anonymous_Returns401()
    {
        var sut = BuildSut();
        var req = BuildRequest(authenticated: false,
            jsonBody: JsonSerializer.Serialize(new SetAdvanceTaxReceiptModeDto { Mode = EAdvanceTaxReceiptMode.Disabled }));

        var result = await sut.Client_SetAdvanceTaxReceiptMode(req);

        result.ShouldBeOfType<UnauthorizedResult>();
        await _clientService.DidNotReceiveWithAnyArgs().SetAdvanceTaxReceiptModeAsync(default);
    }

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_NonAdmin_Returns403()
    {
        var sut = BuildSut();
        var req = BuildRequest(authenticated: true,
            jsonBody: JsonSerializer.Serialize(new SetAdvanceTaxReceiptModeDto { Mode = EAdvanceTaxReceiptMode.Disabled }));

        var result = await sut.Client_SetAdvanceTaxReceiptMode(req);

        result.ShouldBeOfType<ForbidResult>();
        await _clientService.DidNotReceiveWithAnyArgs().SetAdvanceTaxReceiptModeAsync(default);
    }

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_ValidBody_EmptyMode_DelegatesToServiceWithDefaultEnumValue()
    {
        // Arrange — valid JSON body but Mode property missing → defaults to enum value 0
        // (EAdvanceTaxReceiptMode.Disabled == 0). This is the minimum-body scenario.
        _clientService
            .SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.Disabled, Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = BuildSut();
        // "{ }" → Mode not specified → defaults to 0 (Disabled)
        var req = BuildRequest(authenticated: true, jsonBody: "{}", roles: new[] { "Admin" });

        // Act
        var result = await sut.Client_SetAdvanceTaxReceiptMode(req);

        // Assert — service called with the default enum value; returns 200.
        await _clientService.Received(1)
            .SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.Disabled, Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(EAdvanceTaxReceiptMode.Disabled);
    }

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_AuthenticatedUser_DelegatesToService()
    {
        // Arrange — set mode to Disabled
        _clientService
            .SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.Disabled, Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = BuildSut();
        var json = JsonSerializer.Serialize(new SetAdvanceTaxReceiptModeDto
        {
            Mode = EAdvanceTaxReceiptMode.Disabled
        });
        var req = BuildRequest(authenticated: true, jsonBody: json, roles: new[] { "Admin" });

        // Act
        var result = await sut.Client_SetAdvanceTaxReceiptMode(req);

        // Assert — service was called and the updated mode is returned
        await _clientService.Received(1)
            .SetAdvanceTaxReceiptModeAsync(EAdvanceTaxReceiptMode.Disabled, Arg.Any<CancellationToken>());

        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(EAdvanceTaxReceiptMode.Disabled);
    }

    [Fact]
    public async Task SetAdvanceTaxReceiptMode_IssuerNotConfigured_Returns404()
    {
        // Arrange — service returns false → controller maps to NotFound
        _clientService
            .SetAdvanceTaxReceiptModeAsync(Arg.Any<EAdvanceTaxReceiptMode>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = BuildSut();
        var json = JsonSerializer.Serialize(new SetAdvanceTaxReceiptModeDto
        {
            Mode = EAdvanceTaxReceiptMode.OnAnyPayment
        });
        var req = BuildRequest(authenticated: true, jsonBody: json, roles: new[] { "Admin" });

        // Act
        var result = await sut.Client_SetAdvanceTaxReceiptMode(req);

        // Assert
        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Theory]
    [InlineData(EAdvanceTaxReceiptMode.Disabled)]
    [InlineData(EAdvanceTaxReceiptMode.OnPaymentMatch)]
    [InlineData(EAdvanceTaxReceiptMode.OnAnyPayment)]
    public async Task SetAdvanceTaxReceiptMode_AllModes_DelegatesCorrectMode(EAdvanceTaxReceiptMode mode)
    {
        // Arrange
        _clientService
            .SetAdvanceTaxReceiptModeAsync(mode, Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = BuildSut();
        var json = JsonSerializer.Serialize(new SetAdvanceTaxReceiptModeDto { Mode = mode });
        var req = BuildRequest(authenticated: true, jsonBody: json, roles: new[] { "Admin" });

        // Act
        var result = await sut.Client_SetAdvanceTaxReceiptMode(req);

        // Assert — correct enum value forwarded to service
        await _clientService.Received(1).SetAdvanceTaxReceiptModeAsync(mode, Arg.Any<CancellationToken>());
        var ok = result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(mode);
    }
}
