using System.Net;
using System.Text.Json;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>Tests for the MCP update_invoice tool: partial DTO mapping, items replacement, status guard, error mapping.</summary>
public class UpdateInvoiceToolTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    public UpdateInvoiceToolTests()
    {
        _api.GetInvoiceByIdAsync(5, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 5, DocumentNumber = "FV1", Status = EInvoiceStatus.Draft, IssuerId = 2, IssueDate = new DateTime(2026, 1, 10) });
        _api.UpdateInvoiceAsync(5, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 5, DocumentNumber = "FV1" });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" }, new() { Id = 3, Code = "EUR" } });
    }

    private static string Error(string json) => JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task OnlyProvidedFieldsAreSent_OthersStayNull()
    {
        var json = await InvoiceTools.UpdateInvoice(_api, 5, dueDate: "2026-02-01", variableSymbol: "123");

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(5);
        await _api.Received(1).UpdateInvoiceAsync(5, Arg.Is<UpdateInvoiceDto>(d =>
            d.DueDate == new DateTime(2026, 2, 1) && d.VariableSymbol == "123"
            && d.IssueDate == null && d.TaxableSupplyDate == null && d.ConstantSymbol == null
            && d.SpecificSymbol == null && d.PaymentMethod == null && d.BankAccountId == null
            && d.CurrencyId == null && d.Notes == null && d.ApplyOss == null && d.InvoiceItem == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllScalarFields_AreMapped()
    {
        await InvoiceTools.UpdateInvoice(_api, 5, issueDate: "2026-01-02", dueDate: "2026-01-20", taxableSupplyDate: "2026-01-01",
            variableSymbol: "9", constantSymbol: "308", specificSymbol: "7", paymentMethod: "cash", bankAccountId: 11,
            currency: "eur", notes: "hello", applyOss: false);

        await _api.Received(1).UpdateInvoiceAsync(5, Arg.Is<UpdateInvoiceDto>(d =>
            d.IssueDate == new DateTime(2026, 1, 2) && d.TaxableSupplyDate == new DateTime(2026, 1, 1)
            && d.ConstantSymbol == "308" && d.SpecificSymbol == "7" && d.PaymentMethod == EPaymentMethod.Cash
            && d.BankAccountId == 11 && d.CurrencyId == 3 && d.Notes == "hello" && d.ApplyOss == false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Items_ReplaceAll_AndVatRateResolvedForVatPayer()
    {
        _api.GetClientByIdAsync(2, Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 2, IsVatPayer = true });
        _api.GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VatRateDto> { new() { Id = 7, Rate = 21m } });
        var items = new List<CreateInvoiceItemDto> { new() { Description = "A", Quantity = 2, UnitPrice = 10, VatRatePercentage = 21 } };

        await InvoiceTools.UpdateInvoice(_api, 5, items: items);

        await _api.Received(1).UpdateInvoiceAsync(5, Arg.Is<UpdateInvoiceDto>(d =>
            d.InvoiceItem != null && d.InvoiceItem.Count == 1 && d.InvoiceItem[0].VatRateId == 7),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Items_NonVatPayer_SkipsRateLookup()
    {
        _api.GetClientByIdAsync(2, Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 2, IsVatPayer = false });
        var items = new List<CreateInvoiceItemDto> { new() { Description = "A", Quantity = 1, UnitPrice = 10 } };

        await InvoiceTools.UpdateInvoice(_api, 5, items: items);

        await _api.DidNotReceive().GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
        await _api.Received(1).UpdateInvoiceAsync(5, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyItemsList_IsRejected_BeforeAnyApiCall()
    {
        var json = await InvoiceTools.UpdateInvoice(_api, 5, items: []);

        Error(json).ShouldContain("at least one line");
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task NothingProvided_ReturnsError()
    {
        Error(await InvoiceTools.UpdateInvoice(_api, 5)).ShouldContain("Nothing to update");
    }

    [Theory]
    [InlineData("not-a-date", null, "Invalid issueDate")]
    [InlineData(null, "Bogus", "Unknown paymentMethod")]
    public async Task InvalidInput_ReturnsError_WithoutApiCall(string? issueDate, string? paymentMethod, string expected)
    {
        var json = await InvoiceTools.UpdateInvoice(_api, 5, issueDate: issueDate, paymentMethod: paymentMethod);

        Error(json).ShouldContain(expected);
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task UnknownCurrency_ReturnsError()
    {
        Error(await InvoiceTools.UpdateInvoice(_api, 5, currency: "XXX")).ShouldContain("Unknown currency");
    }

    [Theory]
    [InlineData(EInvoiceStatus.Paid)]
    [InlineData(EInvoiceStatus.Creditnoted)]
    public async Task NonEditableStatus_ReturnsClearError_AndDoesNotCallUpdate(EInvoiceStatus status)
    {
        _api.GetInvoiceByIdAsync(6, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 6, DocumentNumber = "FV6", Status = status });

        var msg = Error(await InvoiceTools.UpdateInvoice(_api, 6, notes: "x"));

        msg.ShouldContain($"not editable in status {status}");
        msg.ShouldContain("credit note");
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task NotFound_ReturnsError()
    {
        _api.GetInvoiceByIdAsync(9, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        Error(await InvoiceTools.UpdateInvoice(_api, 9, notes: "x")).ShouldContain("not found");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "validation_error")]
    [InlineData(HttpStatusCode.Conflict, "validation_error")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    public async Task ApiErrors_AreMappedToToolErrors(HttpStatusCode status, string expectedError)
    {
        _api.UpdateInvoiceAsync(5, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Throws(new FakvioApiException("api", status, safeMessage: "Cannot update paid invoices"));

        var root = JsonDocument.Parse(await InvoiceTools.UpdateInvoice(_api, 5, notes: "x")).RootElement;

        root.GetProperty("error").GetString().ShouldBe(expectedError);
        root.GetProperty("message").GetString().ShouldContain("Cannot update paid invoices");
    }

    [Fact]
    public async Task CompletedInvoice_ReturnsRevertHint_AndChangesNothing()
    {
        _api.GetInvoiceByIdAsync(7, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 7, DocumentNumber = "FV7", Status = EInvoiceStatus.Completed });

        var root = JsonDocument.Parse(await InvoiceTools.UpdateInvoice(_api, 7, notes: "x")).RootElement;

        root.GetProperty("status").GetString().ShouldBe("requires_revert_to_draft");
        root.GetProperty("invoiceId").GetInt64().ShouldBe(7);
        root.GetProperty("number").GetString().ShouldBe("FV7");
        root.GetProperty("message").GetString().ShouldContain("revert_invoice_to_draft");
        root.TryGetProperty("error", out _).ShouldBeFalse();
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task RevertInvoiceToDraft_CallsApi_AndReturnsInvoice()
    {
        _api.RevertInvoiceToDraftAsync(7, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 7, Status = EInvoiceStatus.Draft });

        var root = JsonDocument.Parse(await InvoiceTools.RevertInvoiceToDraft(_api, 7)).RootElement;

        root.GetProperty("id").GetInt64().ShouldBe(7);
        await _api.Received(1).RevertInvoiceToDraftAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevertInvoiceToDraft_NotFound_ReturnsError()
    {
        _api.RevertInvoiceToDraftAsync(9, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        Error(await InvoiceTools.RevertInvoiceToDraft(_api, 9)).ShouldContain("not found");
    }

    [Fact]
    public async Task RevertInvoiceToDraft_ApiRejection_IsMappedToValidationError()
    {
        _api.RevertInvoiceToDraftAsync(5, Arg.Any<CancellationToken>())
            .Throws(new FakvioApiException("api", HttpStatusCode.BadRequest, safeMessage: "Only completed invoices can be reverted to draft."));

        var root = JsonDocument.Parse(await InvoiceTools.RevertInvoiceToDraft(_api, 5)).RootElement;

        root.GetProperty("error").GetString().ShouldBe("validation_error");
        root.GetProperty("message").GetString().ShouldContain("Only completed");
    }
}
