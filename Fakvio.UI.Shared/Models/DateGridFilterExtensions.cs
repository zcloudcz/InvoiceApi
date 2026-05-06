using MudBlazor;

namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Helpers for reading filter values out of a MudDataGrid <see cref="GridState{T}"/>.
/// Used by every page that has a server-side <c>LoadServerData</c> callback to translate
/// column filters into API query parameters.
///
/// Why a helper instead of inline LINQ on every page?
/// The lookup boilerplate (cast each FilterDefinition, check Value type, match the property)
/// is identical for every grid. Centralising it here means each page is one line per
/// filterable column.
/// </summary>
public static class DateGridFilterExtensions
{
    /// <summary>
    /// Returns the (from, to) date range for a column identified by <paramref name="key"/>.
    /// Both elements are null when:
    /// - No date filter is active for that column,
    /// - The user cleared the picker, or
    /// - The filter value is not a <see cref="DateFilterValue"/> (e.g. column uses a different filter type).
    ///
    /// The returned tuple maps directly to typical API parameter pairs like
    /// <c>IssueDateFrom</c> / <c>IssueDateTo</c>.
    /// </summary>
    /// <typeparam name="T">Row type of the grid.</typeparam>
    /// <param name="state">Current grid state passed to ServerData callback.</param>
    /// <param name="key">
    /// The same string passed as <c>FilterKey</c> on the <c>DateColumnFilter</c>
    /// instance for that column (e.g. "IssueDate", "DueDate").
    /// </param>
    public static (DateTime? From, DateTime? To) GetDateRange<T>(
        this GridState<T> state, string key)
    {
        if (state?.FilterDefinitions == null) return (null, null);

        // Walk all filter definitions and find the one whose Value is a
        // DateFilterValue with the matching Key. There can only be one
        // active filter per column at a time so FirstOrDefault is safe.
        foreach (var fd in state.FilterDefinitions)
        {
            if (fd.Value is DateFilterValue dfv && dfv.Key == key)
            {
                return dfv.ToRange();
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Returns the string filter value typed into the ColumnFilterRow for a given
    /// property name. Returns null when no filter is active for that column.
    ///
    /// MudDataGrid ColumnFilterRow stores a plain string in
    /// <c>FilterDefinition.Value</c> for string columns. The column is identified
    /// by its <c>Column.PropertyName</c> which MudBlazor derives from the
    /// <c>Property</c> expression on a <c>PropertyColumn</c> (e.g. "Level", "Source").
    ///
    /// Usage:
    ///   var levelFilter = state.GetStringFilter&lt;AppLogDto&gt;("Level");
    /// </summary>
    /// <typeparam name="T">Row type of the grid.</typeparam>
    /// <param name="state">Current grid state passed to ServerData callback.</param>
    /// <param name="propertyName">
    /// The property name as it appears in the grid state — matches the PropertyColumn
    /// Property expression field name (case-sensitive, e.g. "Level", "Source", "Message").
    /// </param>
    public static string? GetStringFilter<T>(this GridState<T> state, string propertyName)
    {
        if (state?.FilterDefinitions == null) return null;

        foreach (var fd in state.FilterDefinitions)
        {
            // MudDataGrid sets Column on the FilterDefinition; PropertyName comes from the
            // Property expression on PropertyColumn. Skip entries with no column or wrong name.
            if (fd.Column?.PropertyName == propertyName && fd.Value is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }

        return null;
    }
}
