namespace Fakvio.Application.Service;

/// <summary>
/// A chat tool whose execution changes data and therefore must be confirmed by the user first.
///
/// How the conversational confirm works (issue #212, story #149):
/// 1. The model calls the tool WITHOUT the <c>confirm</c> parameter.
/// 2. <c>ChatToolExecutor</c> does not run <see cref="IChatTool.ExecuteAsync"/> at all —
///    it calls <see cref="BuildPreviewAsync"/> and returns that text ("I will change X → Y").
///    Nothing is written.
/// 3. The user agrees in the next chat message.
/// 4. The model repeats the very same call with <c>confirm: true</c> — only now does
///    <see cref="IChatTool.ExecuteAsync"/> run.
///
/// Implementations therefore never check the flag themselves: the gate lives in the executor,
/// so it cannot be forgotten in a single tool. See DEVGUIDE §4.7.
///
/// Junior note: the <c>confirm</c> parameter is NOT declared in <see cref="IChatTool.Parameters"/>.
/// The executor appends it to the schema of every confirmable tool
/// (<see cref="ChatToolConfirmation.EffectiveParameters"/>), so the prompt text, the native
/// JSON Schema and the validation all describe it identically — and a tool author cannot
/// word it differently by accident.
/// </summary>
public interface IConfirmableChatTool : IChatTool
{
    /// <summary>
    /// Describes — in plain language for the user — what <see cref="IChatTool.ExecuteAsync"/>
    /// would do with these exact parameters. Must not change any data.
    ///
    /// Return <see cref="ChatToolResult.Failure"/> when the preview itself cannot be produced
    /// (record not found, parameters that the schema cannot validate). The failure is reported
    /// to the model as usual, and no confirmation is offered.
    ///
    /// Do not add "confirm?" boilerplate to the text — the executor appends
    /// <see cref="ChatToolConfirmation.PreviewSuffix"/> to every successful preview.
    /// </summary>
    /// <param name="parameters">The same parameters that would be passed to ExecuteAsync.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default);
}

/// <summary>
/// Everything the <c>confirm</c> gate knows about itself: the reserved parameter name, its
/// schema, how a confirmation is recognised and what a preview tells the model.
///
/// One place on purpose — the executor, the prompt generator and the tests must all agree
/// on the same spelling; a second copy is how "confirmed" silently stops meaning confirmed.
/// </summary>
public static class ChatToolConfirmation
{
    /// <summary>
    /// Reserved parameter name. No tool may declare it itself — the executor rejects that
    /// at startup, because two definitions of the same name would produce a broken schema.
    /// </summary>
    public const string ParameterName = "confirm";

    /// <summary>
    /// Appended to every successful preview, so the model always learns two things:
    /// nothing happened yet, and how to make it happen.
    /// </summary>
    public const string PreviewSuffix =
        "\n\nNOTHING HAS BEEN CHANGED YET. Show this preview to the user, ask for approval, " +
        "and only if the user approves, repeat the exact same tool call with \"confirm\": true.";

    /// <summary>
    /// Schema of the confirm flag, appended to every confirmable tool's parameter list.
    /// Optional on purpose: a required flag would force the model to send it on the first
    /// call too, which is exactly the blind write this mechanism exists to prevent.
    /// </summary>
    public static readonly ChatToolParameter Parameter = new()
    {
        Name = ParameterName,
        Type = ChatToolParameterType.Boolean,
        Description =
            "Set to true ONLY after the user has explicitly approved the change. " +
            "Omit it on the first call — the tool then returns a preview and changes nothing.",
        IsRequired = false
    };

    /// <summary>
    /// The parameter schema the model is shown for this tool: its own parameters, plus the
    /// confirm flag when the tool is confirmable.
    /// </summary>
    public static IReadOnlyList<ChatToolParameter> EffectiveParameters(IChatTool tool)
        => tool is IConfirmableChatTool
            ? [.. tool.Parameters, Parameter]
            : tool.Parameters;

    /// <summary>
    /// True only for an explicit boolean true. Anything else — missing, blank, "1", "yes",
    /// garbage — counts as NOT confirmed, so an unparsable value can never authorise a write.
    /// </summary>
    public static bool IsConfirmed(IReadOnlyDictionary<string, string> parameters)
        => parameters.TryGetValue(ParameterName, out var raw)
           && bool.TryParse(raw, out var confirmed)
           && confirmed;
}
