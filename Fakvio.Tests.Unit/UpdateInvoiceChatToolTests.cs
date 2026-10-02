using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Unit tests for the chat update_invoice tool: parsing, validation, status guard, preview vs execute.</summary>
public class UpdateInvoiceChatToolTests
{
    private readonly IInvoiceService _invoices = Substitute.For<IInvoiceService>();
    private readonly IClientService _clients = Substitute.For<IClientService>();
    private readonly ICurrencyService _currencies = Substitute.For<ICurrencyService>();
    private readonly IVatRateService _vatRates = Substitute.For<IVatRateService>();
    private readonly IReverseChargeCodeService _rcCodes = Substitute.For<IReverseChargeCodeService>();
    private readonly UpdateInvoiceTool _tool;

    public UpdateInvoiceChatToolTests()
    {
        _tool = new UpdateInvoiceTool(_invoices, _clients, _currencies, _vatRates, _rcCodes,
            Substitute.For<ILogger<UpdateInvoiceTool>>());
        Given(EInvoiceStatus.Draft);
        _clients.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 1, IsVatPayer = true });
        _vatRates.GetDefaultStandardRateAsync(Arg.Any<CancellationToken>()).Returns(new VatRateDto { Id = 4, Rate = 21m });
        _currencies.GetCurrencyByCodeAsync("EUR", Arg.Any<CancellationToken>()).Returns(new CurrencyDto { Id = 3, Code = "EUR" });
        _invoices.UpdateInvoiceAsync(42, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 42, DocumentNumber = "FV1", ClientName = "Alza", CurrencyCode = "CZK" });
    }

    private void Given(EInvoiceStatus status, string? ossCountry = null) =>
        _invoices.GetInvoiceByIdAsync(42, Arg.Any<CancellationToken>()).Returns(new InvoiceDto
        { Id = 42, DocumentNumber = "FV1", ClientName = "Alza", CurrencyCode = "CZK", Status = status, OssCountryCode = ossCountry });

    private static Dictionary<string, string> P(params (string, string)[] kv)
    {
        var d = new Dictionary<string, string> { ["id"] = "42" };
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    [Fact]
    public void Tool_IsConfirmable() => (_tool is IConfirmableChatTool).ShouldBeTrue();

    [Fact]
    public async Task Execute_MapsOnlyProvidedFields()
    {
        var result = await _tool.ExecuteAsync(P(("due_date", "2026-05-01"), ("variable_symbol", "55"), ("payment_method", "cash"),
            ("bank_account_id", "11"), ("currency", "eur"), ("notes", " hi ")));

        result.IsSuccess.ShouldBeTrue();
        await _invoices.Received(1).UpdateInvoiceAsync(42, Arg.Is<UpdateInvoiceDto>(d =>
            d.DueDate == new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) && d.VariableSymbol == "55"
            && d.PaymentMethod == EPaymentMethod.Cash && d.BankAccountId == 11 && d.CurrencyId == 3 && d.Notes == "hi"
            && d.IssueDate == null && d.TaxableSupplyDate == null && d.ConstantSymbol == null && d.InvoiceItem == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_Items_ReplaceAllWithDefaultVatRate()
    {
        var result = await _tool.ExecuteAsync(P(("items", "[{\"description\":\"Web\",\"quantity\":2,\"unit_price\":100}]")));

        result.IsSuccess.ShouldBeTrue();
        await _invoices.Received(1).UpdateInvoiceAsync(42, Arg.Is<UpdateInvoiceDto>(d =>
            d.InvoiceItem != null && d.InvoiceItem.Count == 1 && d.InvoiceItem[0].VatRateId == 4
            && d.InvoiceItem[0].Quantity == 2 && d.InvoiceItem[0].UnitPrice == 100),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_WritesNothing_AndListsChanges()
    {
        var result = await _tool.BuildPreviewAsync(P(("notes", "x"), ("items", "[{\"description\":\"A\",\"unit_price\":1}]")));

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("notes replaced");
        result.OutputText.ShouldContain("ALL line items replaced");
        await _invoices.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Theory]
    [InlineData("issue_date", "31.31.2026", "Invalid issue_date")]
    [InlineData("variable_symbol", "12ab", "only digits")]
    [InlineData("variable_symbol", "12345678901", "only digits")]
    [InlineData("payment_method", "Bitcoin", "Unknown payment_method")]
    [InlineData("bank_account_id", "abc", "must be a number")]
    [InlineData("currency", "XXX", "not found")]
    [InlineData("items", "not json", "Could not parse items")]
    [InlineData("items", "[{\"unit_price\":5}]", "missing 'description'")]
    [InlineData("items", "[]", "non-empty")]
    public async Task InvalidInput_FailsWithoutWriting(string key, string value, string expected)
    {
        var result = await _tool.ExecuteAsync(P((key, value)));

        result.IsSuccess.ShouldBeFalse();
        (result.ErrorMessage ?? result.OutputText).ShouldContain(expected);
        await _invoices.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task NothingToChange_Fails()
    {
        (await _tool.ExecuteAsync(P())).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData(EInvoiceStatus.Paid)]
    [InlineData(EInvoiceStatus.Creditnoted)]
    public async Task NonEditableStatus_Fails(EInvoiceStatus status)
    {
        Given(status);

        var result = await _tool.ExecuteAsync(P(("notes", "x")));

        result.IsSuccess.ShouldBeFalse();
        (result.ErrorMessage ?? result.OutputText).ShouldContain(status.ToString());
        await _invoices.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task OssInvoice_ItemsRefused()
    {
        Given(EInvoiceStatus.Draft, ossCountry: "DE");

        var result = await _tool.ExecuteAsync(P(("items", "[{\"description\":\"A\",\"unit_price\":1}]")));

        result.IsSuccess.ShouldBeFalse();
        (result.ErrorMessage ?? result.OutputText).ShouldContain("OSS");
    }

    [Fact]
    public async Task ServiceBusinessError_IsReportedAsFailure()
    {
        _invoices.UpdateInvoiceAsync(42, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns<InvoiceDto?>(_ => throw new InvalidOperationException("Cannot update paid invoices"));

        var result = await _tool.ExecuteAsync(P(("notes", "x")));

        result.IsSuccess.ShouldBeFalse();
        (result.ErrorMessage ?? result.OutputText).ShouldContain("Cannot update paid invoices");
    }

    [Fact]
    public async Task CompletedInvoice_RequiresRevert_AndWritesNothing()
    {
        Given(EInvoiceStatus.Completed);

        var preview = await _tool.BuildPreviewAsync(P(("notes", "x")));
        var execute = await _tool.ExecuteAsync(P(("notes", "x")));

        foreach (var result in new[] { preview, execute })
        {
            result.IsSuccess.ShouldBeFalse();
            (result.ErrorMessage ?? result.OutputText).ShouldContain("REQUIRES_REVERT_TO_DRAFT");
            (result.ErrorMessage ?? result.OutputText).ShouldContain("revert_invoice_to_draft");
        }
        await _invoices.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    // ─── revert_invoice_to_draft ──────────────────────────────────────────

    private RevertInvoiceToDraftTool RevertTool() => new(_invoices, Substitute.For<ILogger<RevertInvoiceToDraftTool>>());

    [Fact]
    public void RevertTool_IsConfirmable() => (RevertTool() is IConfirmableChatTool).ShouldBeTrue();

    [Fact]
    public async Task Revert_Completed_PreviewWritesNothing_ExecuteReverts()
    {
        Given(EInvoiceStatus.Completed);
        _invoices.RevertToDraftAsync(42, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 42, DocumentNumber = "FV1", ClientName = "Alza", CurrencyCode = "CZK", Status = EInvoiceStatus.Draft });

        var preview = await RevertTool().BuildPreviewAsync(P());
        preview.IsSuccess.ShouldBeTrue();
        await _invoices.DidNotReceiveWithAnyArgs().RevertToDraftAsync(default, default);

        var result = await RevertTool().ExecuteAsync(P());
        result.IsSuccess.ShouldBeTrue();
        await _invoices.Received(1).RevertToDraftAsync(42, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EInvoiceStatus.Draft)]
    [InlineData(EInvoiceStatus.Paid)]
    [InlineData(EInvoiceStatus.Creditnoted)]
    public async Task Revert_NonCompleted_Fails(EInvoiceStatus status)
    {
        Given(status);

        var result = await RevertTool().ExecuteAsync(P());

        result.IsSuccess.ShouldBeFalse();
        await _invoices.DidNotReceiveWithAnyArgs().RevertToDraftAsync(default, default);
    }
}
