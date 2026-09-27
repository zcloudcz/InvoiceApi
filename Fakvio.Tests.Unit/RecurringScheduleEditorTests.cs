using Bunit;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for RecurringScheduleEditor — the reusable form used by both
/// "add" and "edit" flows on the invoice template detail page's recurring schedule panel.
/// </summary>
public class RecurringScheduleEditorTests : BunitContext, IAsyncLifetime
{
    // xUnit disposes test classes synchronously; MudBlazor's PopoverService only implements
    // IAsyncDisposable, so plain Dispose() throws — route disposal through IAsyncLifetime
    // instead (same reason as InvoicesTypeFilterTests).
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public RecurringScheduleEditorTests()
    {
        // MudSelect needs a MudPopoverProvider in the render tree — switch the guard off
        // instead of rendering a full layout (same pattern as InvoicesTypeFilterTests).
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLocalization(options => options.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static List<ClientDto> SampleClients() =>
    [
        new() { Id = 1, CompanyName = "Client A" },
        new() { Id = 2, CompanyName = "Client B" },
    ];

    [Fact]
    public void MonthlyFrequency_ShowsDayOfMonthField_NotDayOfWeek()
    {
        var model = new RecurringScheduleFormModel { Frequency = ERecurrenceFrequency.Monthly };

        var cut = Render<RecurringScheduleEditor>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Clients, SampleClients()));

        cut.Markup.ShouldContain("Den v měsíci");
        cut.Markup.ShouldNotContain("Den v týdnu");
    }

    [Fact]
    public void WeeklyFrequency_ShowsDayOfWeekField_NotDayOfMonth()
    {
        var model = new RecurringScheduleFormModel
        {
            Frequency = ERecurrenceFrequency.Weekly,
            DayOfWeekValue = DayOfWeek.Monday,
            DayOfMonthValue = null,
        };

        var cut = Render<RecurringScheduleEditor>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Clients, SampleClients()));

        cut.Markup.ShouldContain("Den v týdnu");
        cut.Markup.ShouldNotContain("Den v měsíci");
    }

    [Fact]
    public void ShowAutoSendFalse_HidesAutoSendSwitch()
    {
        var model = new RecurringScheduleFormModel();

        var cut = Render<RecurringScheduleEditor>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Clients, SampleClients())
            .Add(p => p.ShowAutoSend, false));

        cut.Markup.ShouldNotContain("Rovnou vystavit");
    }

    [Fact]
    public void ShowAutoSendTrue_RendersAutoSendSwitch()
    {
        var model = new RecurringScheduleFormModel();

        var cut = Render<RecurringScheduleEditor>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.Clients, SampleClients())
            .Add(p => p.ShowAutoSend, true));

        cut.Markup.ShouldContain("Rovnou vystavit");
    }
}
