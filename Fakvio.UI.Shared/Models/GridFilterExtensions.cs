using MudBlazor;

namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Helpers for reading enum and bool filter values out of a MudDataGrid <see cref="GridState{T}"/>.
/// Complements <see cref="DateGridFilterExtensions"/> (dates + strings) so that every
/// column type rendered by the ColumnFilterRow can be translated into API query parameters.
///
/// Convention: every server-side grid page calls one Get*Filter line per filterable column
/// inside its LoadServerData callback. See DEVGUIDE.md (FakvioGrid pattern) for details.
/// </summary>
public static class GridFilterExtensions
{
    /// <summary>
    /// Returns the enum value selected in the ColumnFilterRow for a given property name,
    /// or null when no filter is active for that column.
    ///
    /// MudDataGrid stores the selected value in <c>FilterDefinition.Value</c>. For enum
    /// PropertyColumns this is usually the boxed enum itself, but we also accept a string
    /// representation defensively — the stored type can differ when a FilterTemplate or
    /// cell template changes how the filter cell is rendered.
    /// </summary>
    /// <typeparam name="T">Row type of the grid.</typeparam>
    /// <typeparam name="TEnum">Enum type of the filtered column.</typeparam>
    /// <param name="state">Current grid state passed to the ServerData callback.</param>
    /// <param name="propertyName">PropertyColumn property name (e.g. "Status").</param>
    public static TEnum? GetEnumFilter<T, TEnum>(this GridState<T> state, string propertyName)
        where TEnum : struct, Enum
    {
        if (state?.FilterDefinitions == null) return null;

        foreach (var fd in state.FilterDefinitions)
        {
            if (fd.Column?.PropertyName != propertyName || fd.Value is null)
                continue;

            if (fd.Value is TEnum enumValue)
                return enumValue;

            // Defensive: some filter cell renderings store the value as string.
            if (fd.Value is string s && Enum.TryParse<TEnum>(s, ignoreCase: true, out var parsed))
                return parsed;
        }

        return null;
    }

    /// <summary>
    /// Returns the bool value selected in the ColumnFilterRow (tri-state select) for a given
    /// property name, or null when the filter is inactive / cleared ("all").
    /// </summary>
    /// <typeparam name="T">Row type of the grid.</typeparam>
    /// <param name="state">Current grid state passed to the ServerData callback.</param>
    /// <param name="propertyName">PropertyColumn property name (e.g. "IsActive").</param>
    public static bool? GetBoolFilter<T>(this GridState<T> state, string propertyName)
    {
        if (state?.FilterDefinitions == null) return null;

        foreach (var fd in state.FilterDefinitions)
        {
            if (fd.Column?.PropertyName != propertyName || fd.Value is null)
                continue;

            if (fd.Value is bool b)
                return b;

            // Defensive: string representation ("true"/"false") from a custom filter cell.
            if (fd.Value is string s && bool.TryParse(s, out var parsed))
                return parsed;
        }

        return null;
    }
}
