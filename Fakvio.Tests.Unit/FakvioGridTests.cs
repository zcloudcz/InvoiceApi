using System.Linq.Expressions;
using Blazored.LocalStorage;
using Bunit;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Models;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the universal FakvioGrid wrapper and the
/// GridFilterExtensions helpers (string/enum/bool column filter extraction).
///
/// The filter helpers are tested through a rendered grid because
/// FilterDefinition.Column must be a real rendered column — its PropertyName
/// is derived from the PropertyColumn Property expression at render time.
/// </summary>
public class FakvioGridTests : BunitContext, IAsyncLifetime
{
    // MudBlazor's PopoverService only supports async disposal; xunit v2 disposes
    // test classes synchronously, so route disposal through IAsyncLifetime.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    /// <summary>Minimal row type exercising string, enum, and bool columns.</summary>
    private class Row
    {
        public string Name { get; set; } = "";
        public TestStatus Status { get; set; }
        public bool IsActive { get; set; }
    }

    private enum TestStatus { Draft = 0, Sent = 1 }

    public FakvioGridTests()
    {
        // MudDataGrid needs MudBlazor services; JS calls (resize observer, scroll
        // manager, localStorage persistence) are irrelevant here — loose mode
        // answers them all with defaults.
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddSingleton<GridStateService>();

        // Localizer returns the key itself — good enough for asserting presence.
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(
            ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);
    }

    /// <summary>Column fragment with one string, one enum, and one bool column.</summary>
    private static RenderFragment TestColumns() => builder =>
    {
        builder.OpenComponent<PropertyColumn<Row, string>>(0);
        builder.AddComponentParameter(1, "Property", (Expression<Func<Row, string>>)(x => x.Name));
        builder.AddComponentParameter(2, "Title", "Name");
        builder.CloseComponent();

        builder.OpenComponent<PropertyColumn<Row, TestStatus>>(3);
        builder.AddComponentParameter(4, "Property", (Expression<Func<Row, TestStatus>>)(x => x.Status));
        builder.AddComponentParameter(5, "Title", "Status");
        builder.CloseComponent();

        builder.OpenComponent<PropertyColumn<Row, bool>>(6);
        builder.AddComponentParameter(7, "Property", (Expression<Func<Row, bool>>)(x => x.IsActive));
        builder.AddComponentParameter(8, "Title", "Active");
        builder.CloseComponent();
    };

    private IRenderedComponent<FakvioGrid<Row>> RenderGrid(
        IEnumerable<Row>? items = null,
        Func<GridState<Row>, Task<GridData<Row>>>? serverData = null)
    {
        // MudDataGrid requires a MudPopoverProvider in the render tree
        // (column menus / filter popovers), so render both together.
        var root = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();

            builder.OpenComponent<FakvioGrid<Row>>(1);
            builder.AddComponentParameter(2, nameof(FakvioGrid<Row>.GridKey), "test-grid");
            builder.AddComponentParameter(3, nameof(FakvioGrid<Row>.Columns), TestColumns());
            if (items is not null)
                builder.AddComponentParameter(4, nameof(FakvioGrid<Row>.Items), items);
            if (serverData is not null)
                builder.AddComponentParameter(5, nameof(FakvioGrid<Row>.ServerData), serverData);
            builder.CloseComponent();
        });

        return root.FindComponent<FakvioGrid<Row>>();
    }

    // ── Rendering & defaults ─────────────────────────────────────────

    [Fact]
    public void RendersForwardedColumns()
    {
        var cut = RenderGrid(items: new[] { new Row { Name = "Alpha" } });

        // Column headers from the forwarded fragment must appear in the markup
        cut.Markup.ShouldContain("Name");
        cut.Markup.ShouldContain("Status");
        cut.Markup.ShouldContain("Active");
        cut.Markup.ShouldContain("Alpha"); // row content rendered
    }

    [Fact]
    public void AppliesStandardGridDefaults()
    {
        var cut = RenderGrid(items: Array.Empty<Row>());
        var grid = cut.Instance.Grid;

        grid.ShouldNotBeNull();
        grid!.FilterMode.ShouldBe(DataGridFilterMode.ColumnFilterRow);
        grid.SortMode.ShouldBe(SortMode.Single);
        grid.Filterable.ShouldBeTrue();
        grid.Hideable.ShouldBeTrue();
        grid.DragDropColumnReordering.ShouldBeTrue();
        grid.Dense.ShouldBeTrue();
    }

    [Fact]
    public void BothServerDataAndItems_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RenderGrid(
            items: Array.Empty<Row>(),
            serverData: _ => Task.FromResult(new GridData<Row>())));
    }

    [Fact]
    public async Task ReloadAsync_InClientMode_DoesNotThrow()
    {
        var cut = RenderGrid(items: Array.Empty<Row>());

        // Client mode has no ServerData — reload must be a safe no-op
        await cut.Instance.ReloadAsync();
    }

    [Fact]
    public void EmptyGrid_ShowsLocalizedNoRecordsDefault()
    {
        var cut = RenderGrid(items: Array.Empty<Row>());

        cut.Markup.ShouldContain("Common_NoRecords");
    }

    // ── GridFilterExtensions (through rendered columns) ──────────────

    /// <summary>
    /// Builds a GridState whose FilterDefinition points at a real rendered column,
    /// mirroring exactly what MudDataGrid hands to the ServerData callback.
    /// </summary>
    private static GridState<Row> StateWithFilter(
        IRenderedComponent<FakvioGrid<Row>> cut, string columnTitle, object? value)
    {
        var column = cut.Instance.Grid!.RenderedColumns.Single(c => c.Title == columnTitle);
        return new GridState<Row>
        {
            FilterDefinitions = new List<IFilterDefinition<Row>>
            {
                new FilterDefinition<Row> { Column = column, Value = value }
            }
        };
    }

    [Fact]
    public void GetStringFilter_ReturnsTypedValue()
    {
        var cut = RenderGrid(items: Array.Empty<Row>());
        var state = StateWithFilter(cut, "Name", "abc");

        state.GetStringFilter("Name").ShouldBe("abc");
        state.GetStringFilter("Status").ShouldBeNull(); // different column
    }

    [Fact]
    public void GetEnumFilter_BoxedEnumValue_Returned()
    {
        var cut = RenderGrid(items: Array.Empty<Row>());
        var state = StateWithFilter(cut, "Status", TestStatus.Sent);

        state.GetEnumFilter<Row, TestStatus>("Status").ShouldBe(TestStatus.Sent);
    }

    [Fact]
    public void GetEnumFilter_StringRepresentation_ParsedDefensively()
    {
        var cut = RenderGrid(items: Array.Empty<Row>());
        var state = StateWithFilter(cut, "Status", "sent");

        state.GetEnumFilter<Row, TestStatus>("Status").ShouldBe(TestStatus.Sent);
    }

    [Fact]
    public void GetEnumFilter_NoFilter_ReturnsNull()
    {
        var state = new GridState<Row> { FilterDefinitions = new List<IFilterDefinition<Row>>() };

        state.GetEnumFilter<Row, TestStatus>("Status").ShouldBeNull();
    }

    [Fact]
    public void GetBoolFilter_BoolAndStringValues_Returned()
    {
        var cut = RenderGrid(items: Array.Empty<Row>());

        StateWithFilter(cut, "Active", true).GetBoolFilter("IsActive").ShouldBe(true);
        StateWithFilter(cut, "Active", "false").GetBoolFilter("IsActive").ShouldBe(false);
        StateWithFilter(cut, "Active", null).GetBoolFilter("IsActive").ShouldBeNull();
    }
}
