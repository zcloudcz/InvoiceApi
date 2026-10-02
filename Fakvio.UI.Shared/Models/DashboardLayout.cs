using Fakvio.Contracts.Dto.Dashboard;

namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Static description of one dashboard widget. The registry below is the single source of
/// truth for which widgets exist; the user only stores visibility + order per id.
/// </summary>
/// <param name="Id">Stable id persisted in <c>UserPreferences.DashboardLayoutJson</c>. Never rename it.</param>
/// <param name="TitleKey">Localization key shown in the "Upravit nástěnku" dialog.</param>
/// <param name="DefaultVisible">Whether the widget is shown until the user changes it.</param>
/// <param name="Width">MudBlazor grid columns (1-12) at the md breakpoint; 12 = full row, 6 = half.</param>
public sealed record DashboardWidgetDefinition(string Id, string TitleKey, bool DefaultVisible, int Width);

/// <summary>
/// All tenant dashboard widgets in their default order. To add a widget: add a record here,
/// a component in Components/Dashboard and one case in Home.razor (see DEVGUIDE §7).
/// </summary>
public static class DashboardWidgetRegistry
{
    public static readonly IReadOnlyList<DashboardWidgetDefinition> All = new DashboardWidgetDefinition[]
    {
        new("kpis", "DashboardWidget_Kpis", true, 12),
        new("taxInsurance", "DashboardWidget_TaxInsurance", true, 12),
        new("revenueByMonth", "DashboardWidget_RevenueByMonth", true, 6),
        new("incomeVsExpense", "DashboardWidget_IncomeVsExpense", true, 6),
        new("receivablesAging", "DashboardWidget_ReceivablesAging", true, 6),
        new("invoicesByStatus", "DashboardWidget_InvoicesByStatus", true, 6),
        new("topClients", "DashboardWidget_TopClients", true, 6),
        new("overdue", "DashboardWidget_Overdue", true, 12),
        new("recentInvoices", "DashboardWidget_RecentInvoices", true, 12),
        new("reminders", "DashboardWidget_Reminders", true, 12),
    };
}

/// <summary>
/// Merges a user's saved layout with the widget registry. Pure function, so it is unit tested
/// without rendering anything.
/// </summary>
public static class DashboardLayoutMerger
{
    /// <summary>
    /// Returns exactly one entry per registry widget, ordered for display and renumbered 0..n-1.
    /// Rules: unknown saved ids are ignored; duplicate saved ids keep the first; widgets the user
    /// has never seen (added in a later release) are appended in registry order with their default
    /// visibility; ties in saved Order fall back to registry order. Null saved = all defaults.
    /// </summary>
    public static List<DashboardWidgetLayoutItemDto> Merge(
        IReadOnlyList<DashboardWidgetDefinition> registry,
        IEnumerable<DashboardWidgetLayoutItemDto>? saved)
    {
        var known = registry.Select((w, index) => (w.Id, index)).ToDictionary(x => x.Id, x => x.index);

        var fromSaved = (saved ?? [])
            .Where(s => known.ContainsKey(s.Id))
            .GroupBy(s => s.Id)
            .Select(g => g.First())
            .OrderBy(s => s.Order)
            .ThenBy(s => known[s.Id])
            .Select(s => new DashboardWidgetLayoutItemDto { Id = s.Id, Visible = s.Visible })
            .ToList();

        var seen = fromSaved.Select(s => s.Id).ToHashSet();
        var missing = registry
            .Where(w => !seen.Contains(w.Id))
            .Select(w => new DashboardWidgetLayoutItemDto { Id = w.Id, Visible = w.DefaultVisible });

        var result = fromSaved.Concat(missing).ToList();
        for (var i = 0; i < result.Count; i++)
            result[i].Order = i;
        return result;
    }
}
