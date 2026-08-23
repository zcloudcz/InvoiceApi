using System.Text.Json;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Converts a JSON object of tool arguments — as produced by any AI provider — into the
/// Dictionary&lt;string, string&gt; that IChatTool.ExecuteAsync consumes.
///
/// It lives in exactly one place on purpose. The text-based flow
/// (<see cref="ChatToolExecutor.ParseToolCall"/>) and every native flow (Claude, Ollama)
/// must agree on what a value means, otherwise the same model answer behaves differently
/// depending on which provider the tenant happens to have configured.
/// </summary>
internal static class ToolArgumentReader
{
    /// <summary>
    /// Reads the properties of <paramref name="argumentsObject"/> into a name → value map.
    ///
    /// Rules:
    /// <list type="bullet">
    /// <item>JSON strings are unwrapped — no surrounding quotes, escapes resolved.</item>
    /// <item>Numbers, booleans and arrays keep their raw JSON text. The central validation
    /// in <see cref="ChatToolExecutor"/> parses them back, and tools expect e.g. invoice
    /// items as raw JSON.</item>
    /// <item>A JSON <c>null</c> literal is dropped entirely — see the note below.</item>
    /// <item>Anything that is not a JSON object yields an empty map instead of throwing.</item>
    /// </list>
    ///
    /// Junior note on the dropped nulls: models routinely send <c>"iban": null</c> for a
    /// parameter they have no value for. Storing that would mean storing the four-character
    /// text "null" — <c>GetRawText()</c> returns the literal as written. That text is not
    /// blank and it is a valid string, so it passes both the required-parameter check and
    /// the type check, and ends up written to the database as if the user had typed it.
    /// Omitting the key keeps "no value" indistinguishable from "parameter not sent".
    /// </summary>
    public static Dictionary<string, string> ReadArguments(JsonElement argumentsObject)
    {
        var arguments = new Dictionary<string, string>();

        if (argumentsObject.ValueKind != JsonValueKind.Object)
            return arguments;

        foreach (var property in argumentsObject.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            arguments[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return arguments;
    }
}
