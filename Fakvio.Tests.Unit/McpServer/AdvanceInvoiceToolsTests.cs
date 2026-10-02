using System.Text.Json;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>MCP tools of the advance workflow: proforma creation, final invoice, DPP, remaining advance.</summary>
public class AdvanceInvoiceToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    private static InvoiceDto Proforma() => new()
    {
        Id = 5,
        DocumentType = EDocumentType.Proforma,
        InvoiceItem =
        [
            new InvoiceItemDto
            {
                OrderIndex = 1, Description = "Consulting", Quantity = 2, Unit = "h",
                UnitPrice = 500m, VatRateId = 10, VatRatePercentage = 21
            }
        ]
    };

    [Fact]
    public async Task IssueFinalInvoice_CopiesProformaItems_AndPassesDeduction()
    {
        _api.GetInvoiceByIdAsync(5, Arg.Any<CancellationToken>()).Returns(Proforma());
        _api.IssueFinalInvoiceAsync(5, Arg.Any<IssueFinalInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 6 });

        var json = await InvoiceTools.IssueFinalInvoice(_api, 5, deductionAmount: 300m);

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(6);
        await _api.Received(1).IssueFinalInvoiceAsync(5, Arg.Is<IssueFinalInvoiceDto>(d =>
            d.DeductionAmount == 300m &&
            d.InvoiceItem.Count == 1 &&
            d.InvoiceItem[0].Description == "Consulting" &&
            d.InvoiceItem[0].Quantity == 2 &&
            d.InvoiceItem[0].VatRateId == 10), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IssueFinalInvoice_NotAProforma_ReturnsErrorWithoutWriting()
    {
        _api.GetInvoiceByIdAsync(5, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 5, DocumentType = EDocumentType.Invoice });

        var json = await InvoiceTools.IssueFinalInvoice(_api, 5);

        JsonDocument.Parse(json).RootElement.TryGetProperty("error", out _).ShouldBeTrue();
        await _api.DidNotReceive().IssueFinalInvoiceAsync(
            Arg.Any<long>(), Arg.Any<IssueFinalInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IssueTaxReceipt_CallsApi()
    {
        _api.IssueTaxReceiptAsync(5, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 7 });

        var json = await InvoiceTools.IssueTaxReceipt(_api, 5);

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(7);
    }

    [Fact]
    public async Task GetRemainingAdvance_ReturnsAmount()
    {
        _api.GetRemainingAdvanceAsync(5, Arg.Any<CancellationToken>()).Returns(810m);

        var json = await InvoiceTools.GetRemainingAdvance(_api, 5);

        JsonDocument.Parse(json).RootElement.GetProperty("remainingAdvance").GetDecimal().ShouldBe(810m);
    }

    [Fact]
    public async Task CreateInvoice_AcceptsProformaDocumentType()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 11, IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns([new CurrencyDto { Id = 1, Code = "CZK" }]);
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 9 });

        await InvoiceTools.CreateInvoice(_api, 10, [new CreateInvoiceItemDto { Description = "x", Quantity = 1, UnitPrice = 1 }],
            documentType: "Proforma");

        await _api.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(d => d.DocumentType == EDocumentType.Proforma), Arg.Any<CancellationToken>());
    }
}
