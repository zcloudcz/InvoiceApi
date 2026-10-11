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
        Root(await InvoiceTools.FindInvoiceByNumber(_api, "X", documentType: "Bogus")).GetProperty("error").GetString()!
            .ShouldContain("Valid values");
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
        Root(bad).GetProperty("error").GetString()!.ShouldContain("Unknown documentType");
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

    [Fact]
    public async Task UpdateInvoice_PaidReceivedInvoice_PointsToTheWayBack()
    {
        _api.GetInvoiceByIdAsync(78, Arg.Any<CancellationToken>()).Returns(new InvoiceDto
        {
            Id = 78, DocumentType = EDocumentType.ReceivedInvoice, DocumentNumber = "SUP-2", Status = EInvoiceStatus.Paid
        });

        var error = Root(await InvoiceTools.UpdateInvoice(_api, 78, notes: "x")).GetProperty("error").GetString()!;

        error.ShouldContain("mark_invoice_unpaid");
        error.ShouldContain("revert_invoice_to_draft");
        await _api.DidNotReceiveWithAnyArgs().UpdateInvoiceAsync(default, default!, default);
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
