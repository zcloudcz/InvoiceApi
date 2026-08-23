namespace Fakvio.UI.Shared.Components.Chat;

/// <summary>
/// Turns the browser URL into the situational context the chat request carries
/// (<c>SendMessageRequest.CurrentRoute</c> / <c>OpenEntity</c>), so the assistant knows
/// where the user is standing when they write "opravit datum splatnosti".
///
/// Pure string functions on purpose — no NavigationManager, no state — so they are unit
/// testable without rendering the panel.
/// </summary>
public static class ChatSituation
{
    /// <summary>
    /// Cleans the app-relative URI for sending: drops the query string and the fragment and
    /// normalizes the leading slash.
    ///
    /// The query string is dropped deliberately: it carries grid filters and paging, which
    /// tell the assistant nothing, and it is the part of the URL most likely to contain
    /// values the user never meant to hand to a language model.
    /// </summary>
    /// <param name="relativeUri">
    /// Value of <c>NavigationManager.ToBaseRelativePath(NavigationManager.Uri)</c>.
    /// </param>
    /// <returns>The bare route (e.g. "invoices/edit/42"), or null when there is none.</returns>
    public static string? NormalizeRoute(string? relativeUri)
    {
        if (string.IsNullOrWhiteSpace(relativeUri))
            return null;

        var route = relativeUri.Split('?', '#')[0].Trim().Trim('/');
        return route.Length == 0 ? null : route;
    }

    /// <summary>
    /// Describes the record open on the given route as "&lt;section&gt; #&lt;id&gt;"
    /// (e.g. "invoices/edit/42" → "invoices #42").
    ///
    /// The rule is deliberately generic — the last segment being a number means "this page
    /// shows one record". No per-page table: a new detail page works without touching this,
    /// and the route → intent mapping stays owned by <c>NavigateTool</c> on the server.
    /// </summary>
    /// <param name="route">A route already passed through <see cref="NormalizeRoute"/>.</param>
    /// <returns>The label, or null when the page shows no single record (lists, dashboard).</returns>
    public static string? DescribeOpenEntity(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
            return null;

        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Needs at least a section and an id, and the id must really be an id.
        if (segments.Length < 2 || !long.TryParse(segments[^1], out var id))
            return null;

        return $"{segments[0]} #{id}";
    }
}
