namespace Fakvio.UI.Shared.Components.Shared;

/// <summary>
/// Tells "the user really edited the text" apart from "the rich-text editor just re-formatted it on load".
/// </summary>
/// <remarks>
/// Junior note: Quill (the editor behind MudHtmlEditor) never keeps HTML exactly as given - it rewrites it into
/// its own normalised form and reports that through the change event right after it is initialised. So the
/// first value we hear about is NOT an edit; it is the editor's own version of the untouched text. We remember
/// it as the baseline, and only a later, different value counts as a real edit.
/// This matters because an unedited body must be sent as "null" (the server then re-renders the template and
/// keeps its original styling), while an edited body is sent as the editor's HTML.
/// Kept as a plain class (no Blazor, no JS) so the rule is unit-testable on its own.
/// </remarks>
public sealed class EditorEditTracker
{
    private string? _baseline;

    /// <summary>The latest HTML the editor reported (null until it reports anything).</summary>
    public string? Current { get; private set; }

    /// <summary>True when the latest HTML differs from the editor's initial (normalised) HTML.</summary>
    public bool IsEdited => _baseline is not null && Current is not null && !string.Equals(Current, _baseline, StringComparison.Ordinal);

    /// <summary>Call from the editor's change event. The very first call establishes the baseline.</summary>
    public void OnChanged(string? html)
    {
        html ??= string.Empty;
        _baseline ??= html;
        Current = html;
    }

    /// <summary>
    /// Forgets any edit (the user chose "reset to template"). The editor will report its normalised
    /// text again, which equals the baseline, so the tracker stays "not edited".
    /// </summary>
    public void Reset() => Current = _baseline;
}
