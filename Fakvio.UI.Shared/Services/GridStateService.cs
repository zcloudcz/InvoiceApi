using Blazored.LocalStorage;
using System.Text.Json;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Persists MudDataGrid column state (order, visibility, width) per grid per user.
/// Uses browser localStorage via Blazored.LocalStorage so state survives page reloads.
///
/// Each grid is identified by a unique string key (e.g., "invoices", "clients").
/// The state is stored as JSON under the key "gridState_{gridKey}".
///
/// Usage from a Blazor page:
///   var state = await GridStateService.LoadAsync("invoices");
///   // Apply state to columns...
///   await GridStateService.SaveAsync("invoices", state);
/// </summary>
public class GridStateService
{
    private readonly ILocalStorageService _localStorage;

    // Prefix for all grid state keys in localStorage — avoids collision with other stored data
    private const string KeyPrefix = "gridState_";

    public GridStateService(ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
    }

    /// <summary>
    /// Loads the saved column state for a specific grid.
    /// Returns null if no state has been saved yet (first visit).
    /// </summary>
    /// <param name="gridKey">Unique identifier for the grid (e.g., "invoices", "clients").</param>
    public async Task<GridColumnState[]?> LoadAsync(string gridKey)
    {
        try
        {
            var json = await _localStorage.GetItemAsStringAsync(KeyPrefix + gridKey);
            if (string.IsNullOrEmpty(json))
                return null;

            return JsonSerializer.Deserialize<GridColumnState[]>(json);
        }
        catch
        {
            // If stored data is corrupted or schema changed, ignore and start fresh
            return null;
        }
    }

    /// <summary>
    /// Saves the current column state for a specific grid.
    /// Called whenever the user reorders, resizes, or hides/shows columns.
    /// </summary>
    /// <param name="gridKey">Unique identifier for the grid.</param>
    /// <param name="columns">Array of column states to persist.</param>
    public async Task SaveAsync(string gridKey, GridColumnState[] columns)
    {
        var json = JsonSerializer.Serialize(columns);
        await _localStorage.SetItemAsStringAsync(KeyPrefix + gridKey, json);
    }

    /// <summary>
    /// Clears saved state for a specific grid (reset to defaults).
    /// </summary>
    public async Task ClearAsync(string gridKey)
    {
        await _localStorage.RemoveItemAsync(KeyPrefix + gridKey);
    }
}

/// <summary>
/// Represents the persisted state of a single DataGrid column.
/// Identified by Title (column header text) since MudDataGrid doesn't expose a stable column ID.
/// </summary>
public class GridColumnState
{
    /// <summary>
    /// Column identifier — uses the column's Title property for matching.
    /// </summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Display order (0-based). Columns are sorted by this value when restoring state.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Whether the column is hidden. Requires Hideable="true" on the column.
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Column width in CSS units (e.g., "150px"). Null means auto-width.
    /// Captured after user resizes a column.
    /// </summary>
    public string? Width { get; set; }
}
