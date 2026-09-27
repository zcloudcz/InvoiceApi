using System.Text.Json;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for <see cref="TemplateTools"/> — no dedicated test file existed for this class before
/// W39, even though N2.5 (2026-W39) changed <c>CreateInvoiceFromTemplate</c> from an opaque JSON
/// string parameter to a typed <see cref="CreateInvoiceFromTemplateDto"/> (see the tool's own
/// remarks and <c>ToolDiscoveryTests.NoTool_TakesAnOpaqueJsonStringParameter</c>, which only
/// guards the SCHEMA shape of that change, never the runtime behavior below it).
/// </summary>
public class TemplateToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    [Fact]
    public async Task ListTemplates_ReturnsTemplatesFromApi()
    {
        _api.GetActiveTemplatesAsync(null, Arg.Any<CancellationToken>())
            .Returns([new InvoiceTemplateDto { Id = 1, Name = "Monthly Hosting" }]);

        var json = await TemplateTools.ListTemplates(_api);

        var doc = JsonDocument.Parse(json);
        doc.RootElement[0].GetProperty("name").GetString().ShouldBe("Monthly Hosting");
    }

    [Fact]
    public async Task ListTemplates_DocumentTypeFilter_IsForwardedToApi()
    {
        _api.GetActiveTemplatesAsync("CreditNote", Arg.Any<CancellationToken>()).Returns([]);

        await TemplateTools.ListTemplates(_api, "CreditNote");

        await _api.Received(1).GetActiveTemplatesAsync("CreditNote", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTemplate_Found_ReturnsTemplate()
    {
        _api.GetTemplateByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(new InvoiceTemplateDto { Id = 7, Name = "Standard Consulting" });

        var json = await TemplateTools.GetTemplate(_api, 7);

        JsonDocument.Parse(json).RootElement.GetProperty("name").GetString().ShouldBe("Standard Consulting");
    }

    [Fact]
    public async Task GetTemplate_NotFound_ReturnsErrorWithoutThrowing()
    {
        _api.GetTemplateByIdAsync(999, Arg.Any<CancellationToken>()).Returns((InvoiceTemplateDto?)null);

        var json = await TemplateTools.GetTemplate(_api, 999);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("999");
    }

    [Fact]
    public async Task CreateInvoiceFromTemplate_NullOptions_ReturnsErrorWithoutCallingApi()
    {
        var json = await TemplateTools.CreateInvoiceFromTemplate(_api, templateId: 1, options: null!);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("options is required");
        await _api.DidNotReceive().CreateInvoiceFromTemplateAsync(
            Arg.Any<long>(), Arg.Any<CreateInvoiceFromTemplateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoiceFromTemplate_ValidOptions_PassesTemplateIdAndOptionsThroughAndReturnsInvoice()
    {
        var options = new CreateInvoiceFromTemplateDto { ClientId = 5, AutoComplete = false };
        _api.CreateInvoiceFromTemplateAsync(3, options, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 42, Status = EInvoiceStatus.Draft });

        var json = await TemplateTools.CreateInvoiceFromTemplate(_api, templateId: 3, options: options);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(42);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Draft");
        await _api.Received(1).CreateInvoiceFromTemplateAsync(3, options, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Only reachable with AutoComplete = true — same structured TENANT_NOT_READY payload as
    /// InvoiceTools.CompleteInvoice (#342), deliberately bypassing the generic McpToolError
    /// catch-all so the MCP client can read the fix route instead of a flattened error string.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceFromTemplate_TenantNotReady_ReturnsStructuredErrorWithFixRoute()
    {
        var options = new CreateInvoiceFromTemplateDto { ClientId = 5, AutoComplete = true };
        var notReady = new TenantNotReadyApiException(
            "Tenant is not ready. Unresolved blocking issue(s): ISSUER_BANK_ACCOUNT_MISSING.",
            missingFields: ["BankAccount"],
            issues:
            [
                new ReadinessIssueDto
                {
                    Code = "ISSUER_BANK_ACCOUNT_MISSING",
                    Severity = EReadinessSeverity.Blocking,
                    MissingFields = ["BankAccount"],
                    FixRoute = "/my-company"
                }
            ]);
        _api.CreateInvoiceFromTemplateAsync(3, options, Arg.Any<CancellationToken>()).ThrowsAsync(notReady);

        var json = await TemplateTools.CreateInvoiceFromTemplate(_api, templateId: 3, options: options);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("TENANT_NOT_READY");
        doc.RootElement.GetProperty("missingFields")[0].GetString().ShouldBe("BankAccount");
        doc.RootElement.GetProperty("issues")[0].GetProperty("fixRoute").GetString().ShouldBe("/my-company");
    }

    [Fact]
    public async Task CreateInvoiceFromTemplate_ApiThrowsOtherError_ReturnsSanitizedError()
    {
        // Not TenantNotReadyApiException — must fall through to the generic McpToolError path
        // (#279) rather than leaking the raw exception message.
        var options = new CreateInvoiceFromTemplateDto { ClientId = 5 };
        _api.CreateInvoiceFromTemplateAsync(3, options, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Client with ID 5 not found."));

        var json = await TemplateTools.CreateInvoiceFromTemplate(_api, templateId: 3, options: options);

        JsonDocument.Parse(json).RootElement.TryGetProperty("code", out _).ShouldBeFalse(
            "a non-readiness error must not carry the TENANT_NOT_READY structured shape.");
    }
}
