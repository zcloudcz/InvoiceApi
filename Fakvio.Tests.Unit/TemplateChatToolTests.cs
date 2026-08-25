using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.ContentTemplate;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the five template chat tools (issue #225):
///   - ListInvoiceTemplatesTool      (read-only, paged)
///   - GetInvoiceTemplateTool        (read-only, detail)
///   - ListContentTemplatesTool      (read-only)
///   - GetContentTemplateTool        (read-only, detail)
///   - SetDefaultContentTemplateTool (confirmable write — the only write here)
///
/// Everything is mocked with NSubstitute — no database. What is verified is what the tools own:
/// parameter parsing, the arguments they hand to the services, the error paths, and that a
/// preview really writes nothing.
///
/// Junior note: the confirm gate itself lives in ChatToolExecutor and is tested in
/// <see cref="ChatToolExecutorTests"/>. Here the two halves are called directly —
/// <c>BuildPreviewAsync</c> is what the executor calls without <c>confirm: true</c>,
/// <c>ExecuteAsync</c> is what it calls with it.
/// </summary>
public class TemplateChatToolTests
{
    // ─── Shared builders ──────────────────────────────────────────────────

    private static InvoiceTemplateDto BuildInvoiceTemplate(
        long id = 5,
        string name = "Měsíční hosting",
        bool isActive = true)
        => new()
        {
            Id = id,
            Name = name,
            Description = "Hosting fakturovaný každý měsíc",
            DocumentType = EDocumentType.Invoice,
            IssuerId = 7,
            IssuerName = "Fakvio s.r.o.",
            ClientId = 12,
            ClientName = "ACME a.s.",
            DueDateOffsetDays = 14,
            CurrencyId = 1,
            CurrencyCode = "CZK",
            BankAccountNumber = "1234567890/0100",
            VariableSymbol = "2026001",
            NumberSequenceId = 3,
            NumberSequenceName = "FA-2026",
            UsageCount = 12,
            LastUsedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            IsActive = isActive,
            InvoiceItem =
            [
                new InvoiceItemDto
                {
                    Id = 1,
                    OrderIndex = 1,
                    Description = "Webhosting",
                    Quantity = 1,
                    Unit = "ks",
                    UnitPrice = 500m,
                    VatRatePercentage = 21m,
                    TotalWithVat = 605m
                }
            ]
        };

    private static ContentTemplateDto BuildContentTemplate(
        long id = 3,
        string name = "Modrá faktura",
        EContentTemplateType templateType = EContentTemplateType.InvoicePdf,
        string language = "cs",
        bool isDefault = false,
        bool isActive = true)
        => new()
        {
            Id = id,
            Name = name,
            TemplateType = templateType,
            Language = language,
            IsDefault = isDefault,
            IsActive = isActive,
            Subject = "Faktura {{DocumentNumber}}",
            Description = "Firemní vzhled",
            HtmlBody = new string('x', 4096),
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        };

    private static PagedResult<InvoiceTemplateDto> BuildPage(params InvoiceTemplateDto[] templates)
        => new([.. templates], templates.Length, 1, 10);

    /// <summary>
    /// An invoice template service stub that answers every paged query with the given page.
    /// All thirteen arguments are matched explicitly — NSubstitute would otherwise treat the
    /// C# default values as exact-match constraints.
    /// </summary>
    private static IInvoiceTemplateService BuildTemplateService(PagedResult<InvoiceTemplateDto>? page = null)
    {
        var service = Substitute.For<IInvoiceTemplateService>();
        service.GetTemplatesPagedAsync(
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<EDocumentType?>(), Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<long?>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns(page ?? BuildPage());
        return service;
    }

    private static ListInvoiceTemplatesTool BuildListInvoiceTemplatesTool(IInvoiceTemplateService service)
        => new(service, Substitute.For<ILogger<ListInvoiceTemplatesTool>>());

    private static GetInvoiceTemplateTool BuildGetInvoiceTemplateTool(IInvoiceTemplateService service)
        => new(service, Substitute.For<ILogger<GetInvoiceTemplateTool>>());

    private static ListContentTemplatesTool BuildListContentTemplatesTool(IContentTemplateService service)
        => new(service, Substitute.For<ILogger<ListContentTemplatesTool>>());

    private static GetContentTemplateTool BuildGetContentTemplateTool(IContentTemplateService service)
        => new(service, Substitute.For<ILogger<GetContentTemplateTool>>());

    private static SetDefaultContentTemplateTool BuildSetDefaultTool(IContentTemplateService service)
        => new(service, Substitute.For<ILogger<SetDefaultContentTemplateTool>>());

    // ─── list_invoice_templates ───────────────────────────────────────────

    [Fact]
    public void ListInvoiceTemplatesTool_IsReadOnlyAndTakesOnlyOptionalParameters()
    {
        var tool = BuildListInvoiceTemplatesTool(BuildTemplateService());

        tool.ToolName.ShouldBe("list_invoice_templates");
        tool.ShouldNotBeAssignableTo<IConfirmableChatTool>();
        tool.Parameters.ShouldAllBe(parameter => !parameter.IsRequired);
    }

    [Fact]
    public void ListInvoiceTemplatesTool_OffersEveryDocumentTypeAsAnAllowedValue()
    {
        var tool = BuildListInvoiceTemplatesTool(BuildTemplateService());

        var allowed = tool.Parameters.Single(parameter => parameter.Name == "document_type").AllowedValues;

        // Every offered value must map back onto the enum — an allowed value the tool cannot
        // parse would be advertised to the model and then silently ignored.
        allowed.ShouldNotBeNull();
        allowed.ShouldContain(nameof(EDocumentType.Invoice));
        allowed.ShouldContain(nameof(EDocumentType.CreditNote));
        allowed.ShouldAllBe(value => Enum.IsDefined(typeof(EDocumentType), value));
    }

    [Fact]
    public async Task ListInvoiceTemplatesTool_WithoutParameters_AsksForActiveTemplatesOnly()
    {
        var service = BuildTemplateService(BuildPage(BuildInvoiceTemplate()));
        var tool = BuildListInvoiceTemplatesTool(service);

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).GetTemplatesPagedAsync(
            1, 10, null, Arg.Any<string?>(), null, null, true, Arg.Any<long?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListInvoiceTemplatesTool_MapsEveryParameterOntoTheQuery()
    {
        var service = BuildTemplateService(BuildPage(BuildInvoiceTemplate()));
        var tool = BuildListInvoiceTemplatesTool(service);

        await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["page"] = "2",
            // Deliberately above the cap — the tool must clamp it to MaxPageSize (50).
            ["page_size"] = "500",
            ["search"] = " hosting ",
            ["document_type"] = "creditnote",
            ["category"] = "Hosting",
            ["include_inactive"] = "true"
        });

        await service.Received(1).GetTemplatesPagedAsync(
            2, 50, "hosting", Arg.Any<string?>(), EDocumentType.CreditNote, "Hosting", null,
            Arg.Any<long?>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task ListInvoiceTemplatesTool_NonPositivePage_FallsBackToTheFirstPage(string page)
    {
        var service = BuildTemplateService();
        var tool = BuildListInvoiceTemplatesTool(service);

        await tool.ExecuteAsync(new Dictionary<string, string> { ["page"] = page });

        await service.Received(1).GetTemplatesPagedAsync(
            1, 10, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EDocumentType?>(),
            Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<long?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListInvoiceTemplatesTool_WithoutMatches_SaysSoInsteadOfReturningAnEmptyBlock()
    {
        var tool = BuildListInvoiceTemplatesTool(BuildTemplateService());

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No invoice templates match");
        result.OutputText.ShouldContain("page 1/1");
    }

    [Fact]
    public async Task ListInvoiceTemplatesTool_ListsIdNameAndDocumentTypeForEachTemplate()
    {
        var service = BuildTemplateService(BuildPage(BuildInvoiceTemplate(id: 42, name: "Konzultace")));
        var tool = BuildListInvoiceTemplatesTool(service);

        var result = await tool.ExecuteAsync([]);

        result.OutputText.ShouldContain("ID=42");
        result.OutputText.ShouldContain("Konzultace");
        result.OutputText.ShouldContain("Invoice");
    }

    // ─── get_invoice_template ─────────────────────────────────────────────

    [Fact]
    public void GetInvoiceTemplateTool_IsReadOnlyAndRequiresTheId()
    {
        var tool = BuildGetInvoiceTemplateTool(BuildTemplateService());

        tool.ToolName.ShouldBe("get_invoice_template");
        tool.ShouldNotBeAssignableTo<IConfirmableChatTool>();
        tool.Parameters.Single().Name.ShouldBe("id");
        tool.Parameters.Single().IsRequired.ShouldBeTrue();
        tool.Parameters.Single().Type.ShouldBe(ChatToolParameterType.Integer);
    }

    [Fact]
    public async Task GetInvoiceTemplateTool_RendersTheHeaderAndTheLineItems()
    {
        var service = BuildTemplateService();
        service.GetTemplateByIdAsync(5, Arg.Any<CancellationToken>()).Returns(BuildInvoiceTemplate());
        var tool = BuildGetInvoiceTemplateTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "5" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Měsíční hosting");
        result.OutputText.ShouldContain("ACME a.s.");
        result.OutputText.ShouldContain("FA-2026");
        result.OutputText.ShouldContain("Webhosting");
        // Amounts are culture-independent — see the note on TemplateChatToolSupport.
        result.OutputText.ShouldContain("500 CZK");
        result.OutputText.ShouldContain("2026-05-01");
    }

    [Fact]
    public async Task GetInvoiceTemplateTool_UnknownId_FailsAndPointsAtTheListTool()
    {
        var service = BuildTemplateService();
        service.GetTemplateByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((InvoiceTemplateDto?)null);
        var tool = BuildGetInvoiceTemplateTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "999" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("999");
        result.ErrorMessage.ShouldContain("list_invoice_templates");
    }

    // ─── list_content_templates ───────────────────────────────────────────

    [Fact]
    public void ListContentTemplatesTool_OffersEveryContentTemplateTypeAsAnAllowedValue()
    {
        var tool = BuildListContentTemplatesTool(Substitute.For<IContentTemplateService>());

        var allowed = tool.Parameters.Single(parameter => parameter.Name == "template_type").AllowedValues;

        allowed.ShouldNotBeNull();
        allowed.ShouldContain(nameof(EContentTemplateType.InvoicePdf));
        allowed.ShouldContain(nameof(EContentTemplateType.InvoiceEmail));
        allowed.ShouldAllBe(value => Enum.IsDefined(typeof(EContentTemplateType), value));
    }

    [Fact]
    public async Task ListContentTemplatesTool_WithoutTypeFilter_ReadsAllActiveTemplates()
    {
        var service = Substitute.For<IContentTemplateService>();
        service.GetAllAsync(false, Arg.Any<CancellationToken>()).Returns([BuildContentTemplate(isDefault: true)]);
        var tool = BuildListContentTemplatesTool(service);

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("ID=3");
        result.OutputText.ShouldContain("DEFAULT");
        await service.Received(1).GetAllAsync(false, Arg.Any<CancellationToken>());
        await service.DidNotReceive().GetAllByTypeAsync(
            Arg.Any<EContentTemplateType>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListContentTemplatesTool_TypeFilter_IsParsedCaseInsensitively()
    {
        var service = Substitute.For<IContentTemplateService>();
        service.GetAllByTypeAsync(EContentTemplateType.InvoiceEmail, true, Arg.Any<CancellationToken>())
            .Returns([BuildContentTemplate(templateType: EContentTemplateType.InvoiceEmail)]);
        var tool = BuildListContentTemplatesTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["template_type"] = "invoiceemail",
            ["include_inactive"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).GetAllByTypeAsync(
            EContentTemplateType.InvoiceEmail, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListContentTemplatesTool_LanguageFilter_KeepsOnlyThatLanguage()
    {
        var service = Substitute.For<IContentTemplateService>();
        service.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(
        [
            BuildContentTemplate(id: 3, name: "Česká", language: "cs"),
            BuildContentTemplate(id: 4, name: "English", language: "en")
        ]);
        var tool = BuildListContentTemplatesTool(service);

        // Upper case on purpose: language codes arrive from the model in any casing.
        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["language"] = "EN" });

        result.OutputText.ShouldContain("English");
        result.OutputText.ShouldNotContain("Česká");
        result.OutputText.ShouldContain("total: 1");
    }

    [Fact]
    public async Task ListContentTemplatesTool_WithoutMatches_SaysSo()
    {
        var service = Substitute.For<IContentTemplateService>();
        service.GetAllAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns([]);
        var tool = BuildListContentTemplatesTool(service);

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No content templates match");
    }

    // ─── get_content_template ─────────────────────────────────────────────

    [Fact]
    public async Task GetContentTemplateTool_ReportsMetadataButNeverTheHtmlBody()
    {
        var service = Substitute.For<IContentTemplateService>();
        service.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(BuildContentTemplate(isDefault: true));
        var tool = BuildGetContentTemplateTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "3" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Modrá faktura");
        result.OutputText.ShouldContain("InvoicePdf");
        result.OutputText.ShouldContain("Faktura {{DocumentNumber}}");
        result.OutputText.ShouldContain("4096 characters");
        result.OutputText.ShouldContain("/content-templates");
        // The body must stay out of the prompt — chat cannot edit it and it costs context.
        result.OutputText.ShouldNotContain(new string('x', 100));
    }

    [Fact]
    public async Task GetContentTemplateTool_UnknownId_FailsAndPointsAtTheListTool()
    {
        var service = Substitute.For<IContentTemplateService>();
        service.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);
        var tool = BuildGetContentTemplateTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "77" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("77");
        result.ErrorMessage.ShouldContain("list_content_templates");
    }

    // ─── set_default_content_template ─────────────────────────────────────

    [Fact]
    public void SetDefaultContentTemplateTool_IsConfirmableAndDoesNotDeclareConfirm()
    {
        var tool = BuildSetDefaultTool(Substitute.For<IContentTemplateService>());

        tool.ShouldBeAssignableTo<IConfirmableChatTool>();
        // The confirm flag is appended centrally by the executor — a tool that declared it
        // itself would produce two definitions of the same parameter.
        tool.Parameters.ShouldNotContain(parameter => parameter.Name == ChatToolConfirmation.ParameterName);
        tool.Parameters.Single().Name.ShouldBe("id");
        tool.Parameters.Single().IsRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task SetDefaultContentTemplateTool_Preview_NamesTheReplacedDefaultAndWritesNothing()
    {
        var service = BuildContentServiceWithSiblings(
            target: BuildContentTemplate(id: 4, name: "Nová šablona"),
            siblings: [BuildContentTemplate(id: 3, name: "Stará šablona", isDefault: true)]);
        var tool = BuildSetDefaultTool(service);

        var result = await tool.BuildPreviewAsync(new Dictionary<string, string> { ["id"] = "4" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Nová šablona");
        result.OutputText.ShouldContain("Stará šablona");
        result.OutputText.ShouldContain("InvoicePdf");
        await service.DidNotReceive().UpdateAsync(
            Arg.Any<long>(), Arg.Any<UpdateContentTemplateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDefaultContentTemplateTool_Preview_WithoutAnyCurrentDefault_SaysThereIsNone()
    {
        var service = BuildContentServiceWithSiblings(
            target: BuildContentTemplate(id: 4),
            siblings: []);
        var tool = BuildSetDefaultTool(service);

        var result = await tool.BuildPreviewAsync(new Dictionary<string, string> { ["id"] = "4" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("no default template");
    }

    [Fact]
    public async Task SetDefaultContentTemplateTool_Preview_IgnoresTheDefaultOfAnotherLanguage()
    {
        // The default is scoped by (type, language): the English default must not be reported
        // as the one a Czech template would replace, because it is not the one that gets unset.
        var service = BuildContentServiceWithSiblings(
            target: BuildContentTemplate(id: 4, language: "cs"),
            siblings: [BuildContentTemplate(id: 9, name: "English default", language: "en", isDefault: true)]);
        var tool = BuildSetDefaultTool(service);

        var result = await tool.BuildPreviewAsync(new Dictionary<string, string> { ["id"] = "4" });

        result.OutputText.ShouldContain("no default template");
        result.OutputText.ShouldNotContain("English default");
    }

    [Fact]
    public async Task SetDefaultContentTemplateTool_SendsOnlyTheDefaultFlag()
    {
        var service = BuildContentServiceWithSiblings(
            target: BuildContentTemplate(id: 4, name: "Nová šablona"),
            siblings: []);
        service.UpdateAsync(4, Arg.Any<UpdateContentTemplateDto>(), Arg.Any<CancellationToken>())
            .Returns(BuildContentTemplate(id: 4, name: "Nová šablona", isDefault: true));
        var tool = BuildSetDefaultTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "4" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("is now the default template");
        await service.Received(1).UpdateAsync(
            4,
            Arg.Is<UpdateContentTemplateDto>(dto =>
                dto.IsDefault == true &&
                dto.Name == null && dto.HtmlBody == null && dto.Subject == null &&
                dto.TemplateType == null && dto.IsActive == null && dto.Language == null &&
                dto.Description == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDefaultContentTemplateTool_InactiveTemplate_IsRefusedAndNothingIsWritten()
    {
        var service = BuildContentServiceWithSiblings(
            target: BuildContentTemplate(id: 4, isActive: false),
            siblings: []);
        var tool = BuildSetDefaultTool(service);

        var preview = await tool.BuildPreviewAsync(new Dictionary<string, string> { ["id"] = "4" });
        var execution = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "4" });

        preview.IsSuccess.ShouldBeFalse();
        preview.ErrorMessage.ShouldContain("deactivated");
        execution.IsSuccess.ShouldBeFalse();
        await service.DidNotReceive().UpdateAsync(
            Arg.Any<long>(), Arg.Any<UpdateContentTemplateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDefaultContentTemplateTool_TemplateThatAlreadyIsTheDefault_IsRefused()
    {
        var service = BuildContentServiceWithSiblings(
            target: BuildContentTemplate(id: 4, isDefault: true),
            siblings: []);
        var tool = BuildSetDefaultTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "4" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("already is the default");
        await service.DidNotReceive().UpdateAsync(
            Arg.Any<long>(), Arg.Any<UpdateContentTemplateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetDefaultContentTemplateTool_UnknownId_Fails()
    {
        var service = Substitute.For<IContentTemplateService>();
        service.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);
        var tool = BuildSetDefaultTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "999" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("999");
    }

    [Fact]
    public async Task SetDefaultContentTemplateTool_WhenSaveReportsMissingTemplate_Fails()
    {
        // The preview and the write are not paired, so the template can disappear in between.
        var service = BuildContentServiceWithSiblings(target: BuildContentTemplate(id: 4), siblings: []);
        service.UpdateAsync(Arg.Any<long>(), Arg.Any<UpdateContentTemplateDto>(), Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);
        var tool = BuildSetDefaultTool(service);

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "4" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("no longer exists");
    }

    /// <summary>
    /// A content template service stub that reports one target template plus the other templates
    /// of the same type (the candidates for "the default this call would replace").
    /// </summary>
    private static IContentTemplateService BuildContentServiceWithSiblings(
        ContentTemplateDto target,
        List<ContentTemplateDto> siblings)
    {
        var service = Substitute.For<IContentTemplateService>();
        service.GetByIdAsync(target.Id, Arg.Any<CancellationToken>()).Returns(target);
        service.GetAllByTypeAsync(target.TemplateType, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(siblings);
        return service;
    }
}
