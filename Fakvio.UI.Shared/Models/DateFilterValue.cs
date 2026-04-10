namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Structured value stored on a MudDataGrid <c>FilterDefinition.Value</c> by
/// the <c>DateColumnFilter</c> component. Contains the chosen operator and
/// the date(s) the user picked.
///
/// Why not just use <c>DateTime?</c>?
/// Server-side <c>LoadServerData</c> callbacks need the operator AND both dates
/// (for range mode). A plain <c>DateTime?</c> only carries one value with no
/// operator metadata. By boxing them into this small object the server-side
/// callback can read them back via <c>DateGridFilterExtensions.GetDateRange()</c>
/// and forward them to the API as named parameters
/// (e.g. <c>IssueDateFrom</c> + <c>IssueDateTo</c>).
///
/// For client-side grids the <c>DateColumnFilter</c> additionally sets a
/// <c>FilterFunction</c> on the same <c>FilterDefinition</c>, so this object
/// is read by server-side grids only — client-side grids never inspect it.
/// </summary>
public class DateFilterValue
{
    /// <summary>
    /// Stable string identifier of the column the filter belongs to
    /// (e.g. "IssueDate", "DueDate", "Timestamp"). The page's
    /// <c>LoadServerData</c> uses it to route the filter to the correct
    /// API parameter.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Operator chosen by the user. Matches the constants in
    /// <c>DateColumnFilter</c>: "is", "gt", "on or after", "lt",
    /// "on or before", "between".
    /// </summary>
    public string Operator { get; set; } = "is";

    /// <summary>
    /// Primary date — for single-value operators this is the only date,
    /// for "between" it's the lower bound (Od).
    /// </summary>
    public DateTime? From { get; set; }

    /// <summary>
    /// Upper bound for "between" mode (Do). Null for single-value operators.
    /// </summary>
    public DateTime? To { get; set; }

    /// <summary>
    /// Converts the operator + dates to an inclusive (from, to) range that maps
    /// directly to typical API parameters like "IssueDateFrom" / "IssueDateTo".
    ///
    /// Operator semantics:
    /// - "is"             → exact day:           [d, d]
    /// - "gt"             → strictly after:      [d+1, ∞)
    /// - "on or after"    → ≥:                   [d, ∞)
    /// - "lt"             → strictly before:     (∞, d-1]
    /// - "on or before"   → ≤:                   (∞, d]
    /// - "between"        → range:               [from, to]
    ///
    /// Returns (null, null) if no primary date is set (i.e. filter is empty).
    /// </summary>
    public (DateTime? From, DateTime? To) ToRange()
    {
        if (!From.HasValue) return (null, null);

        var d = From.Value.Date;

        return Operator switch
        {
            "is" => (d, d),
            "gt" => (d.AddDays(1), null),
            "on or after" => (d, null),
            "lt" => (null, d.AddDays(-1)),
            "on or before" => (null, d),
            "between" => (d, To?.Date),
            _ => (d, d)
        };
    }
}
