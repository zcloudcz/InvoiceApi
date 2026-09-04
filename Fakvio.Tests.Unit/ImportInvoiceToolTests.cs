using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ImportInvoiceTool"/>'s date handling (issue #301).
///
/// Before the fix, "issue_date"/"due_date"/"taxable_supply_date" went through a private
/// TryParseExact copy that returned null on ANY unreadable value — same as a value the
/// user never provided. A dictated date the model garbled would silently vanish and the
/// invoice would be created with a default date instead, with no signal to the model or
/// the user that anything was wrong. This is a bigger problem here than in a list filter:
/// the result is a persisted financial document with the wrong date, not just a widened
/// search. Fixed by routing through the shared ChatToolDates helper, which fails loudly
/// on "present but unreadable" — consistent with every other chat tool that writes a date
/// (CreateReceivedInvoiceTool, CreateVatRateTool, ...).
///
/// Only the two received-invoice paths are exercised here (issued-invoice creation shares
/// the same date-parsing step, so it is not a separate code path worth re-testing).
/// </summary>
public class ImportInvoiceToolTests
{
    private const string MyCompanyIco = "11111111";
    private const string SupplierIco = "87654321";

    private readonly IReceivedInvoiceService _receivedInvoiceService = Substitute.For<IReceivedInvoiceService>();
    private readonly IClientService _clientService = Substitute.For<IClientService>();
    private readonly ICurrencyService _currencyService = Substitute.For<ICurrencyService>();
    private readonly IVatRateService _vatRateService = Substitute.For<IVatRateService>();
    private readonly ImportInvoiceTool _tool;

    public ImportInvoiceToolTests()
    {
        // In-memory TenantDbContext seeded with the tenant's own company (IsIssuer = true) —
        // ImportInvoiceTool.GetMyCompanyIcoAsync reads this directly, not through IClientService.
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var tenantContext = new TenantDbContext(options) { Schema = "tenant_1" };
        tenantContext.Client.Add(new Client
        {
            CompanyName = "My Company s.r.o.",
            RegistrationNumber = MyCompanyIco,
            IsIssuer = true,
            IsActive = true
        });
        tenantContext.SaveChanges();

        // The counterparty (supplier) the invoice is received from.
        _clientService.GetAllClientsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientDto>
            {
                new() { Id = 42, RegistrationNumber = SupplierIco, CompanyName = "Alza.cz", IsActive = true }
            });

        _currencyService.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK", IsActive = true } });

        _vatRateService.GetAllVatRatesAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Contracts.Dto.VatRate.VatRateDto>());

        _tool = new ImportInvoiceTool(
            Substitute.For<IInvoiceService>(),
            _receivedInvoiceService,
            _clientService,
            _currencyService,
            _vatRateService,
            tenantContext,
            Substitute.For<ILogger<ImportInvoiceTool>>());
    }

    private static Dictionary<string, string> BaseParameters(string issueDate) => new()
    {
        ["issuer_ico"] = SupplierIco,
        ["issuer_name"] = "Alza.cz",
        ["recipient_ico"] = MyCompanyIco,
        ["recipient_name"] = "My Company s.r.o.",
        ["document_number"] = "INV-2026-001",
        ["issue_date"] = issueDate,
        ["items"] = """[{"description":"Notebook","unitPrice":10000,"vatRate":21}]"""
    };

    /// <summary>
    /// Issue #301 (AC2) — "15.3.2026" (single-digit month) used to be rejected here even though
    /// #271 already fixed the same shape in list_invoices / get_vat_report.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SingleDigitIssueDate_IsAcceptedAndPersisted()
    {
        _receivedInvoiceService.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto { Id = 7, DocumentNumber = "INV-2026-001" });

        var result = await _tool.ExecuteAsync(BaseParameters("15.3.2026"));

        result.IsSuccess.ShouldBeTrue();
        await _receivedInvoiceService.Received(1).CreateAsync(
            Arg.Is<CreateReceivedInvoiceDto>(dto =>
                dto.IssueDate == new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Issue #301 (AC4) — decided behaviour: a present-but-unreadable date fails the import
    /// instead of silently becoming "no date" (which used to persist the invoice with a
    /// default date the user never dictated). No invoice must be created in this case.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnreadableIssueDate_ReturnsFailure_DoesNotCreateInvoice()
    {
        var result = await _tool.ExecuteAsync(BaseParameters("2026-03"));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("issue_date");
        await _receivedInvoiceService.DidNotReceive()
            .CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Issue #301 (reviewer nit) — the three date parameters are chained with short-circuit
    /// OR (<c>||</c>): "issue_date" is the first operand and was already covered above, but an
    /// unreadable "due_date" (2nd operand) or "taxable_supply_date" (3rd operand) never ran in
    /// any prior test. A copy-paste slip in the chain (e.g. checking "issue_date" twice instead
    /// of "due_date") would have gone undetected. Only "due_date" is set here — "issue_date" is
    /// absent (allowed, "no date"), which isolates the 2nd operand from the 1st.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnreadableDueDate_ReturnsFailure_DoesNotCreateInvoice()
    {
        var parameters = BaseParameters("15.3.2026");
        parameters.Remove("issue_date");
        parameters["due_date"] = "2026-03";

        var result = await _tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("due_date");
        await _receivedInvoiceService.DidNotReceive()
            .CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Same coverage gap as <see cref="ExecuteAsync_UnreadableDueDate_ReturnsFailure_DoesNotCreateInvoice"/>
    /// for the 3rd (last) operand of the chain. "issue_date" and "due_date" are both absent so
    /// only "taxable_supply_date" can be the source of the failure.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnreadableTaxableSupplyDate_ReturnsFailure_DoesNotCreateInvoice()
    {
        var parameters = BaseParameters("15.3.2026");
        parameters.Remove("issue_date");
        parameters["taxable_supply_date"] = "2026-03";

        var result = await _tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("taxable_supply_date");
        await _receivedInvoiceService.DidNotReceive()
            .CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A missing date is still "no date" (not an error) — only a PRESENT but unreadable value
    /// is rejected. The invoice import must still succeed without one, same as before the fix.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MissingIssueDate_StillSucceedsWithNullDate()
    {
        var parameters = BaseParameters("15.3.2026");
        parameters.Remove("issue_date");

        _receivedInvoiceService.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto { Id = 8, DocumentNumber = "INV-2026-001" });

        var result = await _tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeTrue();
        await _receivedInvoiceService.Received(1).CreateAsync(
            Arg.Is<CreateReceivedInvoiceDto>(dto => dto.IssueDate == null),
            Arg.Any<CancellationToken>());
    }
}
