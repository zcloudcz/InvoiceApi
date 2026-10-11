using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Fakvio.Infrastructure.Service.ChatTools;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// <see cref="EDocumentType.ReceivedInvoice"/> lives in the same enum (and table) as the issued types, so every
/// ISSUED write path must refuse it at its boundary - before anything is saved. Otherwise a supplier invoice
/// could be created through the issued rules (our number sequence, revenue, output VAT) and would then be
/// invisible to the issued read side (the API used to dereference null after the insert had committed).
/// </summary>
public class IssuedDocumentTypeBoundaryTests : IDisposable
{
    private readonly TenantDbContext _db;
    private readonly InvoiceService _invoices;

    public IssuedDocumentTypeBoundaryTests()
    {
        _db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        _db.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true });
        _db.Client.Add(new Client { Id = 1, CompanyName = "Us", RegistrationNumber = "11111111", IsIssuer = true, IsActive = true });
        _db.Client.Add(new Client { Id = 2, CompanyName = "Customer", RegistrationNumber = "22222222", IsActive = true });
        _db.SaveChanges();

        _invoices = new InvoiceService(_db, Substitute.For<INumberSequenceService>(),
            Substitute.For<ITenantReadinessService>(), NullLogger<InvoiceService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    [Fact]
    public void IssuedDocumentTypes_ListEverythingExceptReceivedInvoice()
    {
        IssuedDocumentTypes.Names.ShouldBe(["Invoice", "CreditNote", "Proforma", "TaxReceiptForAdvance"]);

        EDocumentType.ReceivedInvoice.IsIssued().ShouldBeFalse();
        ((EDocumentType)99).IsIssued().ShouldBeFalse();
        IssuedDocumentTypes.TryParse("receivedinvoice", out _).ShouldBeFalse();
        IssuedDocumentTypes.TryParse("5", out _).ShouldBeFalse(); // Enum.TryParse alone accepts raw numbers
        IssuedDocumentTypes.TryParse(" creditnote ", out var parsed).ShouldBeTrue();
        parsed.ShouldBe(EDocumentType.CreditNote);
    }

    [Fact]
    public async Task CreateImportedInvoice_WithReceivedType_IsRejected_AndNothingIsSaved()
    {
        var dto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.ReceivedInvoice, ClientId = 2, IssuerId = 1, CurrencyId = 1,
            InvoiceItem = [new CreateInvoiceItemDto { Description = "X", Quantity = 1, UnitPrice = 100 }]
        };

        // The issued pipeline (import, and with it copy of an issued document, template, final invoice, tax receipt)
        // refuses the received type. Only the unified CreateInvoiceAsync accepts it, through the received rules
        // (covered in UnifiedReceivedDocumentTests).
        await Should.ThrowAsync<InvalidOperationException>(() => _invoices.CreateImportedInvoiceAsync(dto));

        (await _db.Invoice.CountAsync()).ShouldBe(0);
        (await _db.InvoiceItem.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task CreateTemplate_WithReceivedType_IsRejected_AndNothingIsSaved()
    {
        var templates = new InvoiceTemplateService(_db, _invoices, NullLogger<InvoiceTemplateService>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() => templates.CreateTemplateAsync(
            new CreateInvoiceTemplateDto { Name = "T", DocumentType = EDocumentType.ReceivedInvoice, IssuerId = 1, CurrencyId = 1 }));

        (await _db.Set<InvoiceTemplate>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task CreateNumberSequence_WithReceivedType_IsRejected_AndNothingIsSaved()
    {
        using var master = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var sequences = new NumberSequenceService(_db, master, Substitute.For<ITenantResolver>(),
            NullLogger<NumberSequenceService>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() => sequences.CreateSequenceAsync(
            new CreateNumberSequenceDto { Name = "Received", DocumentType = EDocumentType.ReceivedInvoice, IsDefault = true, NumberSequenceFormatId = 1 }));

        (await _db.NumberSequence.CountAsync()).ShouldBe(0);
        // The create path seeds the default formats as its first write - the guard must run even before that.
        (await _db.NumberSequenceFormat.CountAsync()).ShouldBe(0);
    }

    // ─── Tools: no raw type numbers; number sequences neither accept nor offer the received type ───

    // Step 4: create_invoice now ACCEPTS the name "ReceivedInvoice" (it is THE document tool); the API applies the
    // received policy. A raw number is still refused, so only "5" remains here.
    [Theory]
    [InlineData("5")]
    public async Task McpCreateInvoice_WithRawNumericDocumentType_ReturnsErrorWithoutCallingApi(string documentType)
    {
        var api = Substitute.For<IFakvioApiClient>();
        var items = new List<CreateInvoiceItemDto> { new() { Description = "X", Quantity = 1, UnitPrice = 1 } };

        var json = await InvoiceTools.CreateInvoice(api, clientId: 1, items: items, documentType: documentType);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()!.ShouldContain("Unknown documentType");
        await api.DidNotReceive().CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task McpNumberSequenceTools_WithReceivedType_ReturnErrorThatDoesNotOfferIt()
    {
        var api = Substitute.For<IFakvioApiClient>();

        var create = await SettingsTools.CreateNumberSequence(api, name: "X", documentType: "ReceivedInvoice", numberSequenceFormatId: 1);
        var list = await SettingsTools.ListNumberSequences(api, documentType: "ReceivedInvoice");

        foreach (var json in new[] { create, list })
        {
            var error = JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()!;
            error.ShouldContain("Valid values: Invoice, CreditNote, Proforma, TaxReceiptForAdvance.");
        }
        await api.DidNotReceive().CreateNumberSequenceAsync(Arg.Any<CreateNumberSequenceDto>(), Arg.Any<CancellationToken>());
        await api.DidNotReceive().GetNumberSequencesAsync(Arg.Any<EDocumentType?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ChatTools_DoNotOfferReceivedTypeAsDocumentType()
    {
        IChatTool[] tools =
        [
            new CreateNumberSequenceTool(Substitute.For<INumberSequenceService>(), NullLogger<CreateNumberSequenceTool>.Instance),
            new ListInvoiceTemplatesTool(Substitute.For<IInvoiceTemplateService>(), NullLogger<ListInvoiceTemplatesTool>.Instance)
        ];

        foreach (var tool in tools)
        {
            var allowed = tool.Parameters.Single(p => p.Name == "document_type").AllowedValues!;
            allowed.ShouldBe(IssuedDocumentTypes.Names);
        }
    }
}
