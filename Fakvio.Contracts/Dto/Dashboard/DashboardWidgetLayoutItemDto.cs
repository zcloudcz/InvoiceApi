namespace Fakvio.Contracts.Dto.Dashboard;

/// <summary>
/// One entry of a user's customized dashboard layout: whether a widget is shown and
/// where. Stored as a JSON array on <c>UserPreferences.DashboardLayoutJson</c>.
/// Unknown <see cref="Id"/> values (e.g. a widget removed in a later release) are
/// ignored by the merge logic — see <c>DashboardLayoutMerger</c> in Fakvio.UI.Shared.
/// </summary>
public class DashboardWidgetLayoutItemDto
{
    /// <summary>Widget id, matches <c>DashboardWidgetDefinition.Id</c> in the UI registry.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Whether the widget is shown on the dashboard.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Display order, ascending. Ties broken by registry order.</summary>
    public int Order { get; set; }
}
