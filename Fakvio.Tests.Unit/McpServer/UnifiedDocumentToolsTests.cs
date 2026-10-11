using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Step 4 of the document unification: the invoice tools are THE document tools for every type, including a
/// received (supplier) invoice. The API (PR #537) does the type work; these tests pin what the MCP layer must do:
/// pass the type scope through, validate the model's type, add the missing transitions, refuse issued-only
/// operations for a received document up front, and keep the legacy received tools working.
///
/// Junior note: <see cref="IFakvioApiClient"/> is mocked, so "the API refuses" is simulated by a
/// <see cref="FakvioApiException"/> with the 400 + message the real controller returns.
/// </summary>
public class UnifiedDocumentToolsTests
{
    private const long ReceivedId = 77;
    private const long IssuedId = 5;

    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    public UnifiedDocumentToolsTests()
    {
        _api.GetInvoiceByIdAsync(ReceivedId, Arg.Any<CancellationToken>()).Returns(new InvoiceDto
        {
            Id = ReceivedId, DocumentType = EDocumentType.ReceivedInvoice, DocumentNumber = "SUP-1", Status = EInvoiceStatus.Draft, IssuerId = 1
        });
        _api.GetInvoiceByIdAsync(IssuedId, Arg.Any<CancellationToken>()).Returns(new InvoiceDto
        {
            Id = IssuedId, DocumentType = EDocumentType.Invoice, DocumentNumber = "FV1", Status = EInvoiceStatus.Completed
        });
        _api.CreateDownloadLinkAsync(Arg.Any<CreateDownloadLinkDto>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadLinkDto { Url = "https://api.example/api/download/tok", ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
    }

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    private static FakvioApiException Api400(string message) => new("api", HttpStatusCode.BadRequest, safeMessage: message);

    // ── list / find: the type scope ──────────────────────────────────────────

    [Theory]
    [InlineData("ReceivedInvoice", EDocumentType.ReceivedInvoice)]
    [InlineData("receivedinvoice", EDocumentType.ReceivedInvoice)]
    [InlineData("Proforma", EDocumentType.Proforma)]
    [InlineData("TaxReceiptForAdvance", EDocumentType.TaxReceiptForAdvance)]
    public async Task ListInvoices_AcceptsEveryDocumentTypeName(string text, EDocumentType expected)
    {
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<InvoiceDto>());

        await InvoiceTools.ListInvoices(_api, documentType: text);

        await _api.Received(1).GetInvoicesPagedAsync(Arg.Is<InvoiceFilterDto>(f => f.DocumentType == expected), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListInvoices_WithoutType_LeavesTheScopeToTheApiDefault()
    {
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<InvoiceDto>());

        await InvoiceTools.ListInvoices(_api);

        await _api.Received(1).GetInvoicesPagedAsync(Arg.Is<InvoiceFilterDto>(f => f.DocumentType == null), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Nonsense")]
    [InlineData("5")]
    public async Task ListInvoices_UnknownType_ReturnsErrorListingValidNames_WithoutCallingApi(string text)
    {
        var error = Root(await InvoiceTools.ListInvoices(_api, documentType: text)).GetProperty("error").GetString()!;

        error.ShouldContain("Unknown documentType");
        foreach (var name in Enum.GetNames<EDocumentType>())
            error.ShouldContain(name);
        // A model that meant "all" must learn that leaving the parameter out is the answer.
        error.ShouldContain("Omit the parameter for all issued documents");
        await _api.DidNotReceiveWithAnyArgs().GetInvoicesPagedAsync(default!, default);
    }

    [Fact]
    public async Task FindInvoiceByNumber_PassesTheTypeScope()
    {
        var supplierInvoice = new InvoiceDto { Id = ReceivedId, DocumentType = EDocumentType.ReceivedInvoice, DocumentNumber = "SUP-9" };
        _api.GetInvoiceByDocumentNumberAsync("SUP-9", Arg.Any<CancellationToken>(), EDocumentType.ReceivedInvoice).Returns(supplierInvoice);

        var root = Root(await InvoiceTools.FindInvoiceByNumber(_api, "SUP-9", documentType: "ReceivedInvoice"));

        root.GetProperty("id").GetInt64().ShouldBe(ReceivedId);
    }

    [Fact]
    public async Task FindInvoiceByNumber_UnknownType_ReturnsError_WithoutCallingApi()
    {
        var error = Root(await InvoiceTools.FindInvoiceByNumber(_api, "X", documentType: "Bogus")).GetProperty("error").GetString()!;
        error.ShouldContain("Valid values");
        error.ShouldContain("Omit the parameter for all issued documents");
        await _api.DidNotReceiveWithAnyArgs().GetInvoiceByDocumentNumberAsync(default!, default, default);
    }

    [Fact]
    public async Task GetOverdueInvoices_PassesTheTypeScope_AndRejectsUnknownOnes()
    {
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>()).Returns(new PagedResult<InvoiceDto>());

        await ReportingTools.GetOverdueInvoices(_api, documentType: "ReceivedInvoice");
        var bad = await ReportingTools.GetOverdueInvoices(_api, documentType: "Bogus");

        await _api.Received(1).GetInvoicesPagedAsync(
            Arg.Is<InvoiceFilterDto>(f => f.DocumentType == EDocumentType.ReceivedInvoice && f.IsOverdue == true && f.Status == EInvoiceStatus.Completed),
            Arg.Any<CancellationToken>());
        var error = Root(bad).GetProperty("error").GetString()!;
        error.ShouldContain("Unknown documentType");
        error.ShouldContain("Omit the parameter for all issued documents");
    }

    [Fact]
    public async Task CreateInvoice_UnknownType_DoesNotSuggestOmitting_BecauseOmittedMeansInvoiceThere()
    {
        var error = Root(await InvoiceTools.CreateInvoice(_api, clientId: 1, items: OneItem(), documentType: "Bogus")).GetProperty("error").GetString()!;

        error.ShouldContain("Unknown documentType");
        error.ShouldNotContain("Omit the parameter");
    }

    // ── create / update with a received invoice ──────────────────────────────

    private static List<CreateInvoiceItemDto> OneItem() => [new() { Description = "Hosting", Quantity = 1, UnitPrice = 100 }];

    [Fact]
    public async Task CreateInvoice_ReceivedInvoice_PassesSupplierNumberAndReceivedDate_AndLetsTheServerPickTheIssuer()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 1, IsVatPayer = false });
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 90 });

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 42, items: OneItem(), documentType: "ReceivedInvoice",
            variableSymbol: "INV-2026-0042", documentNumber: "SUP-2026-9", receivedDate: "2026-03-05");

        Root(json).GetProperty("id").GetInt64().ShouldBe(90);
        await _api.Received(1).CreateInvoiceAsync(Arg.Is<CreateInvoiceDto>(d =>
            d.DocumentType == EDocumentType.ReceivedInvoice && d.ClientId == 42 && d.IssuerId == 0
            && d.CustomDocumentNumber == "SUP-2026-9" && d.ReceivedDate == new DateTime(2026, 3, 5)
            && d.VariableSymbol == "INV-2026-0042"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_ReceivedInvoice_IssuedOnlyInputs_AreForwardedAndTheApiMessageIsSurfaced()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 1 });
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Throws(Api400("BankAccountId cannot be set on a received invoice."));

        var root = Root(await InvoiceTools.CreateInvoice(_api, clientId: 42, items: OneItem(), documentType: "ReceivedInvoice",
            bankAccountId: 3, originalInvoiceId: 8, applyOss: true));

        root.GetProperty("error").GetString().ShouldBe("validation_error");
        root.GetProperty("message").GetString().ShouldBe("BankAccountId cannot be set on a received invoice.");
        // The tool must not pre-empt the API: the issued-only fields reach it as sent, and no OSS lookup is made.
        await _api.Received(1).CreateInvoiceAsync(Arg.Is<CreateInvoiceDto>(d =>
            d.BankAccountId == 3 && d.OriginalInvoiceId == 8 && d.ApplyOss), Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().GetOssCountryAsync(default, default, default, default);
    }

    [Fact]
    public async Task CreateInvoice_DocumentNumber_IsRefusedForIssuedTypes_WithoutCallingApi()
    {
        var error = Root(await InvoiceTools.CreateInvoice(_api, clientId: 1, items: OneItem(), documentType: "Invoice", documentNumber: "X-1"))
            .GetProperty("error").GetString()!;

        error.ShouldContain("only for documentType='ReceivedInvoice'");
        await _api.DidNotReceiveWithAnyArgs().CreateInvoiceAsync(default!, default);
    }

    [Fact]
    public async Task UpdateInvoice_ReceivedInvoice_PassesDocumentNumberReceivedDateAndSupplierSymbol()
    {
        _api.UpdateInvoiceAsync(ReceivedId, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId });

        var json = await InvoiceTools.UpdateInvoice(_api, ReceivedId, documentNumber: "SUP-NEW", receivedDate: "2026-04-01",
            variableSymbol: "INV-2026-0042");

        Root(json).GetProperty("id").GetInt64().ShouldBe(ReceivedId);
        await _api.Received(1).UpdateInvoiceAsync(ReceivedId, Arg.Is<UpdateInvoiceDto>(d =>
            d.DocumentNumber == "SUP-NEW" && d.ReceivedDate == new DateTime(2026, 4, 1) && d.VariableSymbol == "INV-2026-0042"
            && d.ExpectedStatus == EInvoiceStatus.Draft), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateInvoice_IssuedDocument_StillRequiresDigitsOnlyVariableSymbol()
    {
        _api.GetInvoiceByIdAsync(6, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 6, DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Draft });

        var error = Root(await InvoiceTools.UpdateInvoice(_api, 6, variableSymbol: "INV-1")).GetProperty("error").GetString()!;

        error.ShouldContain("only digits");
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task UpdateInvoice_ReceivedInvoice_OnlyNumberAndDateCountAsSomethingToChange()
    {
        // documentNumber / receivedDate alone must not trip the "Nothing to update" guard.
        _api.UpdateInvoiceAsync(ReceivedId, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId });

        (await InvoiceTools.UpdateInvoice(_api, ReceivedId, documentNumber: "A")).ShouldNotContain("Nothing to update");
        (await InvoiceTools.UpdateInvoice(_api, ReceivedId, receivedDate: "2026-01-01")).ShouldNotContain("Nothing to update");
    }

    // PartiallyPaid matters: it used to get the issued-side advice "issue a credit note" - for a SUPPLIER's document.
    [Theory]
    [InlineData(EInvoiceStatus.Paid)]
    [InlineData(EInvoiceStatus.PartiallyPaid)]
    public async Task UpdateInvoice_PaidReceivedInvoice_PointsToTheWayBack(EInvoiceStatus status)
    {
        _api.GetInvoiceByIdAsync(78, Arg.Any<CancellationToken>()).Returns(new InvoiceDto
        {
            Id = 78, DocumentType = EDocumentType.ReceivedInvoice, DocumentNumber = "SUP-2", Status = status
        });

        var error = Root(await InvoiceTools.UpdateInvoice(_api, 78, notes: "x")).GetProperty("error").GetString()!;

        error.ShouldContain("mark_invoice_unpaid");
        error.ShouldContain("revert_invoice_to_draft");
        error.ShouldNotContain("credit note");
        error.ShouldNotContain("CreditNote");
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task UpdateInvoice_PartiallyPaidIssuedInvoice_StillGetsTheCreditNoteAdvice()
    {
        _api.GetInvoiceByIdAsync(6, Arg.Any<CancellationToken>()).Returns(new InvoiceDto
        {
            Id = 6, DocumentType = EDocumentType.Invoice, DocumentNumber = "FV2", Status = EInvoiceStatus.PartiallyPaid
        });

        Root(await InvoiceTools.UpdateInvoice(_api, 6, notes: "x")).GetProperty("error").GetString()!.ShouldContain("credit note");
    }

    [Fact]
    public async Task UpdateInvoice_ApprovedReceivedInvoice_AsksForRevert_WithoutIssuedOrEmailWording()
    {
        _api.GetInvoiceByIdAsync(79, Arg.Any<CancellationToken>()).Returns(new InvoiceDto
        {
            Id = 79, DocumentType = EDocumentType.ReceivedInvoice, DocumentNumber = "SUP-3", Status = EInvoiceStatus.Completed
        });

        var root = Root(await InvoiceTools.UpdateInvoice(_api, 79, notes: "x"));

        root.GetProperty("status").GetString().ShouldBe("requires_revert_to_draft");
        var message = root.GetProperty("message").GetString()!;
        message.ShouldContain("approved");
        message.ShouldContain("revert_invoice_to_draft");
        // We never issued or e-mailed a supplier's document.
        message.ShouldNotContain("issued");
        message.ShouldNotContain("e-mail");
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    // ── received invoice: the supplier's VAT percentage is not checked against OUR rate table ──

    /// <summary>A VAT-paying tenant whose rate table knows only 21 %: the setup in which the bug showed.</summary>
    private void GivenVatPayerIssuerWithOnly21Percent()
    {
        var issuer = new ClientDto { Id = 1, IsVatPayer = true };
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(issuer);
        _api.GetClientByIdAsync(1, Arg.Any<CancellationToken>()).Returns(issuer);
        _api.GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VatRateDto> { new() { Id = 10, Rate = 21, Name = "Standard" } });
    }

    private static List<CreateInvoiceItemDto> OneItemAt19Percent() =>
        [new() { Description = "Hotel Berlin", Quantity = 1, UnitPrice = 100, VatRatePercentage = 19 }];

    [Fact]
    public async Task CreateInvoice_ReceivedInvoice_VatPayerIssuer_ForeignVatPercentageIsForwardedAsGiven()
    {
        GivenVatPayerIssuerWithOnly21Percent();
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 91 });

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 42, items: OneItemAt19Percent(), documentType: "ReceivedInvoice");

        Root(json).GetProperty("id").GetInt64().ShouldBe(91);
        await _api.Received(1).CreateInvoiceAsync(Arg.Is<CreateInvoiceDto>(d =>
            d.InvoiceItem.Single().VatRatePercentage == 19 && d.InvoiceItem.Single().VatRateId == null), Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().GetActiveVatRatesAsync(default, default);
    }

    [Fact]
    public async Task CreateInvoice_IssuedInvoice_VatPayerIssuer_UnknownVatPercentageIsStillRefused()
    {
        // The other half of the rule: for a document WE issue the rate table still decides.
        GivenVatPayerIssuerWithOnly21Percent();

        var error = Root(await InvoiceTools.CreateInvoice(_api, clientId: 42, items: OneItemAt19Percent())).GetProperty("error").GetString()!;

        error.ShouldContain("No active VAT rate matches 19");
        await _api.DidNotReceiveWithAnyArgs().CreateInvoiceAsync(default!, default);
    }

    [Fact]
    public async Task UpdateInvoice_ReceivedInvoice_VatPayerIssuer_ForeignVatPercentageIsForwardedAsGiven()
    {
        GivenVatPayerIssuerWithOnly21Percent();
        _api.UpdateInvoiceAsync(ReceivedId, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId });

        var json = await InvoiceTools.UpdateInvoice(_api, ReceivedId, items: OneItemAt19Percent());

        Root(json).GetProperty("id").GetInt64().ShouldBe(ReceivedId);
        await _api.Received(1).UpdateInvoiceAsync(ReceivedId, Arg.Is<UpdateInvoiceDto>(d =>
            d.InvoiceItem!.Single().VatRatePercentage == 19 && d.InvoiceItem!.Single().VatRateId == null), Arg.Any<CancellationToken>());
        await _api.DidNotReceiveWithAnyArgs().GetActiveVatRatesAsync(default, default);
    }

    // ── create_invoice carries everything the legacy create_received_invoice does ──

    [Fact]
    public async Task CreateInvoice_ReceivedInvoice_SendsTheSameFieldsAsTheLegacyTool()
    {
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" }, new() { Id = 2, Code = "EUR" } });
        GivenVatPayerIssuerWithOnly21Percent();

        // The same supplier invoice, once through each tool. Every field the legacy DTO has is filled in.
        var legacyInput = new CreateReceivedInvoiceDto
        {
            DocumentNumber = "SUP-2026-9",
            SupplierId = 42,
            IssueDate = new DateTime(2026, 3, 1),
            ReceivedDate = new DateTime(2026, 3, 5),
            DueDate = new DateTime(2026, 3, 15),
            TaxableSupplyDate = new DateTime(2026, 2, 28),
            VariableSymbol = "INV-2026-0042",
            ExchangeRate = 25.125m,
            PaymentMethod = EPaymentMethod.BankTransfer,
            BankAccountNumber = "123456789/0100",
            IBAN = "DE89370400440532013000",
            SWIFT = "COBADEFFXXX",
            Notes = "Paid from the EUR account",
            Items =
            [
                new()
                {
                    OrderIndex = 1, Description = "Hotel Berlin", Quantity = 2, Unit = "night", UnitPrice = 100, VatRatePercentage = 19,
                    ProductCode = "HTL", Notes = "room 12"
                }
            ]
        };
        List<CreateInvoiceItemDto> unifiedItems =
        [
            new()
            {
                OrderIndex = 1, Description = "Hotel Berlin", Quantity = 2, Unit = "night", UnitPrice = 100, VatRatePercentage = 19,
                ProductCode = "HTL", Notes = "room 12"
            }
        ];

        CreateReceivedInvoiceDto? legacy = null;
        CreateInvoiceDto? unified = null;
        _api.CreateReceivedInvoiceAsync(Arg.Do<CreateReceivedInvoiceDto>(d => legacy = d), Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto { Id = 1 });
        _api.CreateInvoiceAsync(Arg.Do<CreateInvoiceDto>(d => unified = d), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 1 });

        await ReceivedInvoiceTools.CreateReceivedInvoice(_api, legacyInput, currency: "EUR");
        await InvoiceTools.CreateInvoice(_api, clientId: 42, items: unifiedItems, documentType: "ReceivedInvoice", currency: "EUR",
            issueDate: "2026-03-01", dueDate: "2026-03-15", variableSymbol: "INV-2026-0042", paymentMethod: "BankTransfer",
            notes: "Paid from the EUR account", documentNumber: "SUP-2026-9", receivedDate: "2026-03-05",
            taxableSupplyDate: "2026-02-28", exchangeRate: 25.125m,
            bankAccountNumber: "123456789/0100", iban: "DE89370400440532013000", swift: "COBADEFFXXX");

        legacy.ShouldNotBeNull();
        unified.ShouldNotBeNull();

        // Field by field, in the order of the mapping the API's legacy facade does (ReceivedInvoiceService.CreateAsync):
        // if both tools send the same values here, both end up as the same row.
        unified.DocumentType.ShouldBe(EDocumentType.ReceivedInvoice);
        unified.ClientId.ShouldBe(legacy.SupplierId);
        unified.CustomDocumentNumber.ShouldBe(legacy.DocumentNumber);
        unified.IssueDate.ShouldBe(legacy.IssueDate);
        unified.ReceivedDate.ShouldBe(legacy.ReceivedDate);
        unified.DueDate.ShouldBe(legacy.DueDate);
        unified.TaxableSupplyDate.ShouldBe(legacy.TaxableSupplyDate);
        unified.VariableSymbol.ShouldBe(legacy.VariableSymbol);
        unified.CurrencyId.ShouldBe(legacy.CurrencyId);
        unified.CurrencyId.ShouldBe(2);
        unified.ExchangeRate.ShouldBe(legacy.ExchangeRate);
        unified.PaymentMethod.ShouldBe(legacy.PaymentMethod);
        unified.BankAccountNumber.ShouldBe(legacy.BankAccountNumber);
        unified.IBAN.ShouldBe(legacy.IBAN);
        unified.SWIFT.ShouldBe(legacy.SWIFT);
        unified.Notes.ShouldBe(legacy.Notes);

        var item = unified.InvoiceItem.ShouldHaveSingleItem();
        var legacyItem = legacy.Items.ShouldHaveSingleItem();
        item.OrderIndex.ShouldBe(legacyItem.OrderIndex);
        item.Description.ShouldBe(legacyItem.Description);
        item.Quantity.ShouldBe(legacyItem.Quantity);
        item.Unit.ShouldBe(legacyItem.Unit);
        item.UnitPrice.ShouldBe(legacyItem.UnitPrice);
        item.VatRateId.ShouldBe(legacyItem.VatRateId);
        item.VatRatePercentage.ShouldBe(legacyItem.VatRatePercentage);
        item.VatRegime.ShouldBe(legacyItem.VatRegime);
        item.ReverseChargeCodeId.ShouldBe(legacyItem.ReverseChargeCodeId);
        item.ProductCode.ShouldBe(legacyItem.ProductCode);
        item.Notes.ShouldBe(legacyItem.Notes);

        // Nothing issued-only sneaks in on top of the legacy field set.
        unified.IssuerId.ShouldBe(0);
        unified.BankAccountId.ShouldBeNull();
        unified.OriginalInvoiceId.ShouldBeNull();
        unified.ApplyOss.ShouldBeFalse();
    }

    [Fact]
    public void CreateInvoice_ParityTestCoversEveryPropertyOfTheLegacyDto()
    {
        // Guard for the parity test above: when someone adds a property to the legacy DTO, this fails and reminds
        // them to give create_invoice a way to carry it (and to extend the parity test).
        typeof(CreateReceivedInvoiceDto).GetProperties().Select(p => p.Name).Order().ShouldBe(new[]
        {
            "BankAccountNumber", "CurrencyId", "DocumentNumber", "DueDate", "ExchangeRate", "IBAN", "IssueDate", "Items", "Notes",
            "PaymentMethod", "ReceivedDate", "SWIFT", "SupplierId", "TaxableSupplyDate", "VariableSymbol"
        }.Order());
    }

    [Fact]
    public async Task CreateInvoice_SupplierBankDetails_AreRefusedForIssuedTypes_WithoutCallingApi()
    {
        // An issued document may only point at one of OUR stored accounts (bankAccountId): free text would put
        // an arbitrary account number on our own invoice.
        foreach (var json in new[]
                 {
                     await InvoiceTools.CreateInvoice(_api, clientId: 1, items: OneItem(), bankAccountNumber: "1/0100"),
                     await InvoiceTools.CreateInvoice(_api, clientId: 1, items: OneItem(), documentType: "Proforma", iban: "CZ00"),
                     await InvoiceTools.CreateInvoice(_api, clientId: 1, items: OneItem(), documentType: "CreditNote", swift: "X")
                 })
        {
            var error = Root(json).GetProperty("error").GetString()!;
            error.ShouldContain("only for a received invoice");
            error.ShouldContain("bankAccountId");
        }

        await _api.DidNotReceiveWithAnyArgs().CreateInvoiceAsync(default!, default);
    }

    /// <summary>
    /// Some MCP clients send '' for optional strings the model left unset. On create that must mean "not sent",
    /// otherwise an ordinary issued invoice is refused with a message about received invoices.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_IssuedInvoice_EmptyBankStrings_AreTreatedAsNotSent()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 1, IsVatPayer = false });
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 93 });

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: OneItem(), bankAccountNumber: "", iban: " ", swift: "");

        Root(json).TryGetProperty("error", out _).ShouldBeFalse();
        await _api.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(d => d.BankAccountNumber == null && d.IBAN == null && d.SWIFT == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_IssuedInvoice_ForwardsTaxableSupplyDateAndExchangeRate_AndLeavesThemEmptyWhenOmitted()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 1, IsVatPayer = false });
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 92 });

        await InvoiceTools.CreateInvoice(_api, clientId: 42, items: OneItem(), taxableSupplyDate: "2026-02-28", exchangeRate: 25.5m);
        // Omitted = exactly what the tool sent before these parameters existed (the API defaults both).
        await InvoiceTools.CreateInvoice(_api, clientId: 43, items: OneItem());

        await _api.Received(1).CreateInvoiceAsync(Arg.Is<CreateInvoiceDto>(d =>
            d.ClientId == 42 && d.TaxableSupplyDate == new DateTime(2026, 2, 28) && d.ExchangeRate == 25.5m), Arg.Any<CancellationToken>());
        await _api.Received(1).CreateInvoiceAsync(Arg.Is<CreateInvoiceDto>(d =>
            d.ClientId == 43 && d.TaxableSupplyDate == null && d.ExchangeRate == null
            && d.BankAccountNumber == null && d.IBAN == null && d.SWIFT == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_InvalidTaxableSupplyDate_ReturnsError_WithoutCallingApi()
    {
        Root(await InvoiceTools.CreateInvoice(_api, clientId: 1, items: OneItem(), taxableSupplyDate: "soon"))
            .GetProperty("error").GetString()!.ShouldContain("Invalid taxableSupplyDate");
        await _api.DidNotReceiveWithAnyArgs().CreateInvoiceAsync(default!, default);
    }

    [Fact]
    public async Task UpdateInvoice_ReceivedInvoice_PassesSupplierBankDetailsAndExchangeRate()
    {
        _api.UpdateInvoiceAsync(ReceivedId, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId });

        await InvoiceTools.UpdateInvoice(_api, ReceivedId, exchangeRate: 25.125m,
            bankAccountNumber: "123456789/0100", iban: "DE89370400440532013000", swift: "");

        // "" is sent as-is: it CLEARS the stored value (null = keep), like the symbols.
        await _api.Received(1).UpdateInvoiceAsync(ReceivedId, Arg.Is<UpdateInvoiceDto>(d =>
            d.ExchangeRate == 25.125m && d.BankAccountNumber == "123456789/0100" && d.IBAN == "DE89370400440532013000" && d.SWIFT == ""
            && d.ExpectedStatus == EInvoiceStatus.Draft), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateInvoice_ExchangeRateOrBankDetailAlone_CountAsSomethingToChange()
    {
        _api.UpdateInvoiceAsync(ReceivedId, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId });

        (await InvoiceTools.UpdateInvoice(_api, ReceivedId, exchangeRate: 25m)).ShouldNotContain("Nothing to update");
        (await InvoiceTools.UpdateInvoice(_api, ReceivedId, iban: "")).ShouldNotContain("Nothing to update");
    }

    [Fact]
    public async Task UpdateInvoice_SupplierBankDetails_AreRefusedForAnIssuedDocument_WithoutCallingApi()
    {
        _api.GetInvoiceByIdAsync(6, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 6, DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Draft });

        var error = Root(await InvoiceTools.UpdateInvoice(_api, 6, iban: "CZ6508000000192000145399")).GetProperty("error").GetString()!;

        error.ShouldContain("only for a received invoice");
        error.ShouldContain("bankAccountId");
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    [Fact]
    public async Task UpdateInvoice_IssuedDraft_ForwardsExchangeRate()
    {
        _api.GetInvoiceByIdAsync(6, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 6, DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Draft });
        _api.UpdateInvoiceAsync(6, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 6 });

        await InvoiceTools.UpdateInvoice(_api, 6, exchangeRate: 24.9m);

        await _api.Received(1).UpdateInvoiceAsync(6, Arg.Is<UpdateInvoiceDto>(d =>
            d.ExchangeRate == 24.9m && d.BankAccountNumber == null && d.IBAN == null && d.SWIFT == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateInvoice_IssuedDocument_VariableSymbolWithTrailingNewline_IsRefused()
    {
        // "$" in a .NET regex also matches before a final "\n", so "123\n" used to pass; the rule is anchored with \z now.
        _api.GetInvoiceByIdAsync(6, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 6, DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Draft });

        Root(await InvoiceTools.UpdateInvoice(_api, 6, variableSymbol: "123\n")).GetProperty("error").GetString()!.ShouldContain("only digits");
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
    }

    // ── descriptions: what a model reads must be true for a received invoice too ──

    private static string ParameterDescription(string method, string parameter) =>
        typeof(InvoiceTools).GetMethod(method)!.GetParameters().Single(p => p.Name == parameter)
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Theory]
    [InlineData("issueDate")]
    [InlineData("dueDate")]
    [InlineData("variableSymbol")]
    [InlineData("paymentMethod")]
    public void CreateInvoice_DefaultedParameters_SayThatAReceivedInvoiceStoresThemAsGiven(string parameter)
    {
        var description = ParameterDescription(nameof(InvoiceTools.CreateInvoice), parameter);

        description.ShouldContain("Issued:");
        description.ShouldContain("Received:");
        description.ShouldContain("stored as given");
    }

    [Fact]
    public void CreateInvoice_DueDate_WarnsThatAReceivedInvoiceWithoutItIsNeverOverdue() =>
        ParameterDescription(nameof(InvoiceTools.CreateInvoice), "dueDate").ShouldContain("never shows up as overdue");

    [Fact]
    public void RevertInvoiceToDraft_Description_HasAReceivedVariant()
    {
        var description = typeof(InvoiceTools).GetMethod(nameof(InvoiceTools.RevertInvoiceToDraft))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.ShouldContain("For an ISSUED document");
        description.ShouldContain("For a RECEIVED invoice");
        description.ShouldContain("nothing is sent to anyone");
    }

    // ── transitions with a received id: straight pass-through, API errors surfaced ──

    [Fact]
    public async Task Complete_MarkPaid_Revert_Delete_PassTheReceivedIdThrough()
    {
        _api.CompleteInvoiceAsync(ReceivedId, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId, Status = EInvoiceStatus.Completed });
        _api.MarkInvoiceAsPaidAsync(ReceivedId, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId, Status = EInvoiceStatus.Paid });
        _api.RevertInvoiceToDraftAsync(ReceivedId, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId, Status = EInvoiceStatus.Draft });

        Root(await InvoiceTools.CompleteInvoice(_api, ReceivedId)).GetProperty("status").GetString().ShouldBe("Completed");
        Root(await InvoiceTools.MarkInvoicePaid(_api, ReceivedId)).GetProperty("status").GetString().ShouldBe("Paid");
        Root(await InvoiceTools.RevertInvoiceToDraft(_api, ReceivedId)).GetProperty("status").GetString().ShouldBe("Draft");
        Root(await InvoiceTools.DeleteInvoice(_api, ReceivedId)).GetProperty("success").GetBoolean().ShouldBeTrue();
        await _api.Received(1).DeleteInvoiceAsync(ReceivedId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Transitions_SurfaceTheApiMessage()
    {
        _api.MarkInvoiceAsPaidAsync(ReceivedId, Arg.Any<CancellationToken>()).Throws(Api400("Only approved invoices can be paid."));
        _api.DeleteInvoiceAsync(ReceivedId, Arg.Any<CancellationToken>()).Throws(Api400("Only drafts can be deleted."));

        Root(await InvoiceTools.MarkInvoicePaid(_api, ReceivedId)).GetProperty("message").GetString().ShouldBe("Only approved invoices can be paid.");
        Root(await InvoiceTools.DeleteInvoice(_api, ReceivedId)).GetProperty("message").GetString().ShouldBe("Only drafts can be deleted.");
    }

    // ── the two new transitions ──────────────────────────────────────────────

    [Fact]
    public async Task MarkInvoiceUnpaid_ReturnsTheUpdatedDocument()
    {
        _api.MarkInvoiceAsUnpaidAsync(ReceivedId, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId, Status = EInvoiceStatus.Completed });

        var root = Root(await InvoiceTools.MarkInvoiceUnpaid(_api, ReceivedId));

        root.GetProperty("status").GetString().ShouldBe("Completed");
    }

    [Fact]
    public async Task MarkInvoiceUnpaid_NotFound_And_ApiRejection()
    {
        _api.MarkInvoiceAsUnpaidAsync(1, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);
        _api.MarkInvoiceAsUnpaidAsync(2, Arg.Any<CancellationToken>()).Throws(Api400("Only Paid invoices can be marked as unpaid."));

        Root(await InvoiceTools.MarkInvoiceUnpaid(_api, 1)).GetProperty("error").GetString().ShouldContain("not found");
        Root(await InvoiceTools.MarkInvoiceUnpaid(_api, 2)).GetProperty("message").GetString().ShouldBe("Only Paid invoices can be marked as unpaid.");
    }

    [Fact]
    public async Task RestoreInvoice_ReturnsTheDraft_NotFound_And_ApiRejection()
    {
        _api.RestoreInvoiceAsync(ReceivedId, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = ReceivedId, Status = EInvoiceStatus.Draft });
        _api.RestoreInvoiceAsync(1, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);
        _api.RestoreInvoiceAsync(2, Arg.Any<CancellationToken>()).Throws(Api400("Only deleted invoices can be restored."));

        Root(await InvoiceTools.RestoreInvoice(_api, ReceivedId)).GetProperty("status").GetString().ShouldBe("Draft");
        Root(await InvoiceTools.RestoreInvoice(_api, 1)).GetProperty("error").GetString().ShouldContain("not found");
        Root(await InvoiceTools.RestoreInvoice(_api, 2)).GetProperty("message").GetString().ShouldBe("Only deleted invoices can be restored.");
    }

    [Theory]
    [InlineData(nameof(InvoiceTools.MarkInvoiceUnpaid), true, false)]
    [InlineData(nameof(InvoiceTools.RestoreInvoice), false, false)]
    public void NewTools_DeclareTheirSideEffectsTruthfully(string method, bool destructive, bool idempotent)
    {
        var attribute = typeof(InvoiceTools).GetMethod(method)!.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>()!;

        attribute.ReadOnly.ShouldBeFalse();
        attribute.Destructive.ShouldBe(destructive);
        attribute.Idempotent.ShouldBe(idempotent);
        attribute.OpenWorld.ShouldBeFalse();
    }

    // ── issued-only operations: a received document is refused up front ──────

    private static void ShouldBeTheIssuedOnlyRefusal(string json)
    {
        var root = Root(json);
        root.GetProperty("error").GetString().ShouldBe("validation_error");
        // The same wording as IssuedDocumentOnlyAttribute (HTTP 400 { message }).
        root.GetProperty("message").GetString().ShouldBe($"Document {ReceivedId} is a received invoice. This operation is available only for issued documents.");
        root.TryGetProperty("downloadUrl", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExportInvoicePdf_ReceivedId_FailsUpFront_WithoutMintingALink()
    {
        ShouldBeTheIssuedOnlyRefusal(await InvoiceTools.ExportInvoicePdf(_api, ReceivedId));
        await _api.DidNotReceiveWithAnyArgs().CreateDownloadLinkAsync(default!, default);
    }

    [Fact]
    public async Task ExportInvoiceIsdoc_ReceivedId_FailsUpFront_WithoutMintingALink()
    {
        ShouldBeTheIssuedOnlyRefusal(await InvoiceTools.ExportInvoiceIsdoc(_api, ReceivedId));
        await _api.DidNotReceiveWithAnyArgs().CreateDownloadLinkAsync(default!, default);
    }

    [Fact]
    public async Task ExportInvoiceUbl_ReceivedId_FailsUpFront_WithoutMintingALink()
    {
        ShouldBeTheIssuedOnlyRefusal(await InvoiceTools.ExportInvoiceUbl(_api, ReceivedId));
        await _api.DidNotReceiveWithAnyArgs().CreateDownloadLinkAsync(default!, default);
    }

    [Fact]
    public async Task ExportInvoicesZip_AnyReceivedId_FailsUpFront_WithoutMintingALink()
    {
        ShouldBeTheIssuedOnlyRefusal(await InvoiceTools.ExportInvoicesZip(_api, [IssuedId, ReceivedId], "pdf"));
        await _api.DidNotReceiveWithAnyArgs().CreateDownloadLinkAsync(default!, default);
    }

    [Fact]
    public async Task IssuedExports_StillReturnALink()
    {
        // No change for issued documents: single exports and the ZIP still mint a link.
        Root(await InvoiceTools.ExportInvoicePdf(_api, IssuedId)).GetProperty("downloadUrl").GetString().ShouldNotBeNullOrEmpty();
        Root(await InvoiceTools.ExportInvoicesZip(_api, [IssuedId], "pdf")).GetProperty("downloadUrl").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task IssueFinalInvoice_ReceivedId_ReturnsTheIssuedOnlyRefusal()
    {
        ShouldBeTheIssuedOnlyRefusal(await InvoiceTools.IssueFinalInvoice(_api, ReceivedId));
    }

    [Fact]
    public async Task SendInvoiceEmail_ReceivedId_SurfacesTheApiRefusal()
    {
        _api.SendInvoiceEmailAsync(ReceivedId, Arg.Any<SendInvoiceEmailDto>(), Arg.Any<CancellationToken>())
            .Throws(Api400($"Document {ReceivedId} is a received invoice. This operation is available only for issued documents."));

        var root = Root(await InvoiceTools.SendInvoiceEmail(_api, ReceivedId));

        root.GetProperty("message").GetString().ShouldContain("available only for issued documents");
    }

    // ── legacy received tools: only the descriptions changed ─────────────────

    private static string DescriptionOf(string method) =>
        typeof(ReceivedInvoiceTools).GetMethod(method)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Theory]
    [InlineData(nameof(ReceivedInvoiceTools.ListReceivedInvoices), "list_invoices")]
    [InlineData(nameof(ReceivedInvoiceTools.GetReceivedInvoice), "get_invoice")]
    [InlineData(nameof(ReceivedInvoiceTools.CreateReceivedInvoice), "create_invoice")]
    [InlineData(nameof(ReceivedInvoiceTools.ApproveReceivedInvoice), "complete_invoice")]
    [InlineData(nameof(ReceivedInvoiceTools.MarkReceivedInvoicePaid), "mark_invoice_paid")]
    [InlineData(nameof(ReceivedInvoiceTools.DeleteReceivedInvoice), "delete_invoice")]
    public void DuplicatingReceivedTools_AreMarkedLegacy_WithAPointerToTheDocumentTool(string method, string replacement)
    {
        var description = DescriptionOf(method);

        description.ShouldStartWith("LEGACY");
        description.ShouldContain(replacement);
    }

    [Theory]
    [InlineData(nameof(ReceivedInvoiceTools.UploadReceivedInvoiceAttachment))]
    [InlineData(nameof(ReceivedInvoiceTools.ExportReceivedInvoiceIsdoc))]
    [InlineData(nameof(ReceivedInvoiceTools.DownloadReceivedInvoicesAttachments))]
    public void ReceivedToolsWithoutADocumentCounterpart_StayFirstClass(string method)
    {
        DescriptionOf(method).ShouldNotContain("LEGACY");
    }
}
