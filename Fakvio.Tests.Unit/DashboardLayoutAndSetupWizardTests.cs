using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Models;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Pure-logic tests for the modular dashboard layout merge and the setup wizard redirect decision.
/// </summary>
public class DashboardLayoutAndSetupWizardTests
{
    private static readonly DashboardWidgetDefinition[] Registry =
    [
        new("a", "A", true, 12),
        new("b", "B", true, 6),
        new("c", "C", false, 6),
    ];

    // ─── DashboardLayoutMerger ────────────────────────────────────────────────

    [Fact]
    public void Merge_NullSaved_ReturnsRegistryDefaults()
    {
        var result = DashboardLayoutMerger.Merge(Registry, null);

        result.Select(r => r.Id).ShouldBe(["a", "b", "c"]);
        result.Select(r => r.Visible).ShouldBe([true, true, false]);
        result.Select(r => r.Order).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void Merge_UsesSavedOrderAndVisibility()
    {
        var saved = new List<DashboardWidgetLayoutItemDto>
        {
            new() { Id = "c", Visible = true, Order = 0 },
            new() { Id = "a", Visible = false, Order = 1 },
            new() { Id = "b", Visible = true, Order = 2 },
        };

        var result = DashboardLayoutMerger.Merge(Registry, saved);

        result.Select(r => r.Id).ShouldBe(["c", "a", "b"]);
        result.Single(r => r.Id == "a").Visible.ShouldBeFalse();
        result.Single(r => r.Id == "c").Visible.ShouldBeTrue();
    }

    [Fact]
    public void Merge_IgnoresUnknownIdsAndDuplicates()
    {
        var saved = new List<DashboardWidgetLayoutItemDto>
        {
            new() { Id = "gone", Visible = true, Order = 0 },
            new() { Id = "b", Visible = false, Order = 1 },
            new() { Id = "b", Visible = true, Order = 2 },
        };

        var result = DashboardLayoutMerger.Merge(Registry, saved);

        result.Count.ShouldBe(3);
        result.ShouldNotContain(r => r.Id == "gone");
        result.Single(r => r.Id == "b").Visible.ShouldBeFalse("first duplicate wins");
    }

    [Fact]
    public void Merge_NewWidgetsAreAppendedWithDefaultVisibility()
    {
        // The user saved a layout before widget "c" (hidden by default) and "b" existed.
        var saved = new List<DashboardWidgetLayoutItemDto> { new() { Id = "a", Visible = false, Order = 0 } };

        var result = DashboardLayoutMerger.Merge(Registry, saved);

        result.Select(r => r.Id).ShouldBe(["a", "b", "c"]);
        result[0].Visible.ShouldBeFalse();
        result[1].Visible.ShouldBeTrue();
        result[2].Visible.ShouldBeFalse();
    }

    [Fact]
    public void Merge_OrderTiesFallBackToRegistryOrder()
    {
        var saved = new List<DashboardWidgetLayoutItemDto>
        {
            new() { Id = "c", Visible = true, Order = 5 },
            new() { Id = "a", Visible = true, Order = 5 },
        };

        DashboardLayoutMerger.Merge(Registry, saved).Select(r => r.Id).ShouldBe(["a", "c", "b"]);
    }

    [Fact]
    public void Registry_IdsAreUnique()
    {
        DashboardWidgetRegistry.All.Select(w => w.Id).Distinct().Count().ShouldBe(DashboardWidgetRegistry.All.Count);
    }

    // ─── SetupWizardPolicy ────────────────────────────────────────────────────

    private static ReadinessReportDto Report(params (string Code, EReadinessSeverity Severity)[] issues) =>
        new() { Issues = issues.Select(i => new ReadinessIssueDto { Code = i.Code, Severity = i.Severity }).ToList() };

    [Fact]
    public void ShouldRedirect_MissingBankAccount_NotDismissed_ReturnsTrue()
    {
        var report = Report((ReadinessCodes.IssuerBankAccountMissing, EReadinessSeverity.Blocking));

        SetupWizardPolicy.ShouldRedirect(report, new UserPreferencesDto(), isAdmin: true).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRedirect_PlainUser_ReturnsFalse()
    {
        var report = Report((ReadinessCodes.IssuerBankAccountMissing, EReadinessSeverity.Blocking));

        SetupWizardPolicy.ShouldRedirect(report, new UserPreferencesDto(), isAdmin: false).ShouldBeFalse();
    }

    [Fact]
    public void Merge_NullIdItem_IsSkipped()
    {
        var saved = new List<DashboardWidgetLayoutItemDto> { new() { Id = null!, Visible = true, Order = 0 } };

        DashboardLayoutMerger.Merge(Registry, saved).Count.ShouldBe(3);
    }

    [Fact]
    public void ShouldRedirect_Dismissed_ReturnsFalse()
    {
        var report = Report((ReadinessCodes.IssuerMissing, EReadinessSeverity.Blocking));
        var prefs = new UserPreferencesDto { SetupWizardDismissedAt = DateTime.UtcNow };

        SetupWizardPolicy.ShouldRedirect(report, prefs, isAdmin: true).ShouldBeFalse();
    }

    [Fact]
    public void ShouldRedirect_OnlyNumberSequenceOrEpoIssues_ReturnsFalse()
    {
        var report = Report(
            (ReadinessCodes.NumberSequenceMissing, EReadinessSeverity.Blocking),
            (ReadinessCodes.EpoHeaderIncomplete, EReadinessSeverity.Warning));

        SetupWizardPolicy.ShouldRedirect(report, new UserPreferencesDto(), isAdmin: true).ShouldBeFalse();
    }

    [Fact]
    public void ShouldRedirect_ReadyTenant_ReturnsFalse()
    {
        SetupWizardPolicy.ShouldRedirect(new ReadinessReportDto(), new UserPreferencesDto(), isAdmin: true).ShouldBeFalse();
    }

    [Fact]
    public void StepDone_ReflectsMatchingReadinessCodes()
    {
        var report = Report(
            (ReadinessCodes.IssuerAddressIncomplete, EReadinessSeverity.Blocking),
            (ReadinessCodes.NumberSequenceMissing, EReadinessSeverity.Blocking));

        SetupWizardPolicy.CompanyStepDone(report).ShouldBeFalse();
        SetupWizardPolicy.BankStepDone(report).ShouldBeTrue();
        SetupWizardPolicy.BillingStepDone(report).ShouldBeFalse();
    }
}
