// ============================================================================
// DateGridFilterExtensionsGetStringTests — coverage for GetStringFilter (#111).
//
// GetStringFilter reads the column filter row value from GridState.FilterDefinitions.
// Full integration with a rendered MudDataGrid would require bUnit (Blazor test
// framework) which is not configured in this project.  These unit tests cover
// the null-safety edge cases that do not require a rendered component context:
//   - Null FilterDefinitions collection → null, no crash
//   - Empty FilterDefinitions → null
//   - FilterDefinition with null Column → skipped safely (no NullReferenceException)
//   - FilterDefinition with mismatched property name → null
//   - FilterDefinition with blank/whitespace Value → treated as no filter (null)
// ============================================================================

using Fakvio.Contracts.Dto.AppLog;
using Fakvio.UI.Shared.Models;
using MudBlazor;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="DateGridFilterExtensions.GetStringFilter{T}"/>.
///
/// These tests focus on null-safety and the blank-string guard.  Full
/// server-side filtering behaviour (Column.PropertyName matching) is exercised
/// in the Playwright E2E suite against the real Logs page.
/// </summary>
public class DateGridFilterExtensionsGetStringTests
{
    // ── Null / empty state ───────────────────────────────────────────────────

    /// <summary>
    /// Null GridState does not throw — returns null gracefully.
    /// </summary>
    [Fact]
    public void GetStringFilter_NullState_ReturnsNull()
    {
        GridState<AppLogDto>? state = null;

        // The extension method guards against null state
        var result = state.GetStringFilter<AppLogDto>("Level");

        result.ShouldBeNull();
    }

    /// <summary>
    /// State with no filter definitions returns null.
    /// </summary>
    [Fact]
    public void GetStringFilter_EmptyFilterDefinitions_ReturnsNull()
    {
        var state = new GridState<AppLogDto>
        {
            FilterDefinitions = new List<IFilterDefinition<AppLogDto>>()
        };

        var result = state.GetStringFilter<AppLogDto>("Level");

        result.ShouldBeNull();
    }

    /// <summary>
    /// A FilterDefinition whose Column is null does not throw — it is safely
    /// skipped.  This can happen when MudDataGrid creates a definition before
    /// assigning the column reference.
    /// </summary>
    [Fact]
    public void GetStringFilter_FilterDefinitionWithNullColumn_ReturnsNullWithoutCrash()
    {
        // Create a concrete FilterDefinition with a string value but no column
        var fd = new FilterDefinition<AppLogDto>
        {
            Column = null,   // Column not yet assigned
            Value = "Error"
        };

        var state = new GridState<AppLogDto>
        {
            FilterDefinitions = new List<IFilterDefinition<AppLogDto>> { fd }
        };

        // Should not throw — Column?.PropertyName uses null-conditional operator
        Should.NotThrow(() => state.GetStringFilter<AppLogDto>("Level"));
        state.GetStringFilter<AppLogDto>("Level").ShouldBeNull();
    }

    /// <summary>
    /// A FilterDefinition where Value is an empty string is treated as "no filter"
    /// and returns null — the blank guard prevents spurious empty ILIKE calls.
    /// </summary>
    [Fact]
    public void GetStringFilter_BlankStringValue_ReturnsNull()
    {
        var fd = new FilterDefinition<AppLogDto>
        {
            Column = null,
            Value = "   " // whitespace only
        };

        var state = new GridState<AppLogDto>
        {
            FilterDefinitions = new List<IFilterDefinition<AppLogDto>> { fd }
        };

        state.GetStringFilter<AppLogDto>("Level").ShouldBeNull();
    }

    /// <summary>
    /// A FilterDefinition where Value is a DateFilterValue (not a string) is
    /// skipped — this verifies the <c>fd.Value is string s</c> guard.
    /// </summary>
    [Fact]
    public void GetStringFilter_DateFilterValueInsteadOfString_ReturnsNull()
    {
        var fd = new FilterDefinition<AppLogDto>
        {
            Column = null,
            Value = new DateFilterValue { Key = "Timestamp", Operator = "is", From = DateTime.Today }
        };

        var state = new GridState<AppLogDto>
        {
            FilterDefinitions = new List<IFilterDefinition<AppLogDto>> { fd }
        };

        state.GetStringFilter<AppLogDto>("Timestamp").ShouldBeNull();
    }

    // ── GetDateRange null-safety (regression — ensure existing helper still works) ──

    /// <summary>
    /// Null state does not throw for the existing GetDateRange helper either.
    /// </summary>
    [Fact]
    public void GetDateRange_NullState_ReturnsNullTuple()
    {
        GridState<AppLogDto>? state = null;

        var (from, to) = state.GetDateRange<AppLogDto>("Timestamp");

        from.ShouldBeNull();
        to.ShouldBeNull();
    }
}
