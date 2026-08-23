using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="GetReadinessTool"/> — the chat tool that tells the assistant what
/// the tenant still has to fill in before invoicing.
///
/// The tool owns no rules (they all live in <c>ITenantReadinessService</c>), so what is
/// worth testing is exactly the two things the tool does own: WHICH report it asks for,
/// and HOW the report is rendered for the model. A rendering that drops the fix route or
/// mislabels a warning as a blocker is how the assistant starts giving wrong advice.
///
/// Junior note: the service is substituted with NSubstitute — nothing touches a database.
/// </summary>
public class GetReadinessToolTests
{
    private readonly ITenantReadinessService _readinessService = Substitute.For<ITenantReadinessService>();
    private readonly GetReadinessTool _sut;

    public GetReadinessToolTests()
    {
        _sut = new GetReadinessTool(_readinessService, Substitute.For<ILogger<GetReadinessTool>>());
    }

    // ─── Schema ───────────────────────────────────────────────────────────

    [Fact]
    public void Tool_IsReadOnlyAndTakesNoParameters()
    {
        // The whole point of a parameterless tool: the model can always call it, and there
        // is nothing to validate or confirm before it runs.
        _sut.ToolName.ShouldBe("get_readiness");
        _sut.Parameters.ShouldBeEmpty();
    }

    // ─── Which report is requested ────────────────────────────────────────

    [Fact]
    public async Task Execute_AsksForTheWholeTenantReport_NotFilteredByIssuerOrDocumentType()
    {
        // The user is asking "what is missing?", not "can I issue this one document?".
        // Filtering by issuer would also need a database ID the model cannot know.
        // Consequence, deliberately accepted (review note on #205): the report may carry
        // issues of an INACTIVE issuer, which the Dashboard picker does not offer. The
        // report is presented as-is rather than second-guessing the service.
        _readinessService
            .GetReportAsync(Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto());

        await _sut.ExecuteAsync([]);

        await _readinessService.Received(1).GetReportAsync(
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_PassesTheCancellationTokenThrough()
    {
        using var cts = new CancellationTokenSource();
        _readinessService
            .GetReportAsync(Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto());

        await _sut.ExecuteAsync([], cts.Token);

        await _readinessService.Received(1).GetReportAsync(null, null, cts.Token);
    }

    // ─── Rendering ────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_CompleteSetup_SaysNothingIsMissing()
    {
        SetupReport();

        var result = await _sut.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("complete");
        result.OutputText.ShouldContain("invoices can be issued");
    }

    [Fact]
    public async Task Execute_BlockingIssue_RendersCodeSeverityFieldsAndFixRoute()
    {
        SetupReport(new ReadinessIssueDto
        {
            Code = ReadinessCodes.IssuerAddressIncomplete,
            Severity = EReadinessSeverity.Blocking,
            MissingFields = ["Street", "City"],
            FixRoute = "/my-company",
            IssuerId = 42,
            IssuerName = "ACME s.r.o."
        });

        var result = await _sut.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("[BLOCKING] ISSUER_ADDRESS_INCOMPLETE");
        result.OutputText.ShouldContain("(issuer: ACME s.r.o.)");
        result.OutputText.ShouldContain("Missing: Street, City");
        // The fix route is what lets the assistant follow up with `navigate` — without it
        // the answer degrades to "something is missing somewhere".
        result.OutputText.ShouldContain("Fix at: /my-company");
        result.OutputText.ShouldContain("must be fixed before an invoice can be completed");
    }

    [Fact]
    public async Task Execute_WarningOnly_SaysInvoicingIsStillPossible()
    {
        // IsReady is computed from the issues, so a warning-only report is "ready".
        // Getting this branch wrong makes the assistant refuse invoicing over an EPO setting.
        SetupReport(EpoWarning());

        var result = await _sut.ExecuteAsync([]);

        result.OutputText.ShouldContain("usable");
        result.OutputText.ShouldContain("[WARNING] EPO_HEADER_INCOMPLETE");
        result.OutputText.ShouldContain("Warnings do not block invoicing");
        result.OutputText.ShouldNotContain("BLOCKING");
    }

    [Fact]
    public async Task Execute_MixedIssues_CountsBlockersAndWarningsSeparately()
    {
        SetupReport(
            new ReadinessIssueDto
            {
                Code = ReadinessCodes.IssuerMissing,
                Severity = EReadinessSeverity.Blocking,
                MissingFields = ["Issuer"],
                FixRoute = "/my-company"
            },
            new ReadinessIssueDto
            {
                Code = ReadinessCodes.NumberSequenceMissing,
                Severity = EReadinessSeverity.Blocking,
                MissingFields = ["Invoice"],
                FixRoute = "/number-sequences"
            },
            EpoWarning());

        var result = await _sut.ExecuteAsync([]);

        result.OutputText.ShouldContain("2 blocking issue(s), 1 warning(s)");
    }

    [Fact]
    public async Task Execute_TenantWideIssue_OmitsTheIssuerLabel()
    {
        // Number sequences and EPO settings belong to the tenant, not to one issuer —
        // naming an issuer there would be a lie the model would repeat to the user.
        SetupReport(EpoWarning());

        var result = await _sut.ExecuteAsync([]);

        result.OutputText.ShouldNotContain("issuer:");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private void SetupReport(params ReadinessIssueDto[] issues)
        => _readinessService
            .GetReportAsync(Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto { Issues = [.. issues] });

    private static ReadinessIssueDto EpoWarning() => new()
    {
        Code = ReadinessCodes.EpoHeaderIncomplete,
        Severity = EReadinessSeverity.Warning,
        MissingFields = ["EpoTaxOfficeCode"],
        FixRoute = "/company-settings"
    };
}
