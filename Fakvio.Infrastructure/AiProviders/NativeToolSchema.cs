using Fakvio.Application.Service;

namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// Translates a provider-agnostic <see cref="NativeToolDefinition"/> into the JSON Schema
/// object that every native tool-calling API expects under its "parameters" / "input_schema" key.
///
/// Why this exists: Claude, Ollama, OpenAI and Gemini all want the same shape
/// (<c>{ "type": "object", "properties": { ... }, "required": [ ... ] }</c>), and each of
/// them exists twice — once as a singleton provider and once as an ad-hoc, per-company one
/// (<c>CompanyAiSettingsResolver</c>). Eight hand-written copies of the same loop is eight
/// places to forget the <c>items</c> keyword; this is the single source of truth instead.
///
/// Junior note: the schema is only ever built from <c>IChatToolExecutor.GetToolDefinitions()</c>,
/// which derives it from <c>IChatTool.Parameters</c>. No provider re-derives types on its own —
/// that is exactly the duplication DEVGUIDE §4.7 rule 4 forbids.
/// </summary>
internal static class NativeToolSchema
{
    /// <summary>
    /// Plain JSON Schema with lower-case type keywords ("string", "object", "array").
    /// Used by Claude, Ollama and OpenAI, which all speak standard JSON Schema.
    /// </summary>
    public static Dictionary<string, object> BuildJsonSchema(NativeToolDefinition tool)
        => Build(tool, static keyword => keyword);

    /// <summary>
    /// Same schema with UPPER-CASE type keywords ("STRING", "OBJECT", "ARRAY").
    ///
    /// Gemini's <c>FunctionDeclaration.parameters</c> is an OpenAPI 3.0 <c>Schema</c> whose
    /// <c>type</c> is a protobuf enum; its JSON form is the enum member name, which is
    /// upper-case. Sending "string" is not guaranteed to be accepted, so we spell it the
    /// way the API reference documents it.
    /// </summary>
    public static Dictionary<string, object> BuildOpenApiSchema(NativeToolDefinition tool)
        => Build(tool, static keyword => keyword.ToUpperInvariant());

    private static Dictionary<string, object> Build(
        NativeToolDefinition tool,
        Func<string, string> typeKeyword)
    {
        var properties = new Dictionary<string, object>();

        foreach (var parameter in tool.Parameters)
        {
            var property = new Dictionary<string, object>
            {
                ["type"] = typeKeyword(parameter.Type),
                ["description"] = parameter.Description
            };

            // Closed list of accepted values — the API rejects anything else before we do.
            if (parameter.EnumValues is { Count: > 0 })
            {
                property["enum"] = parameter.EnumValues;
            }

            // An array without an element schema is rejected by strict function-calling APIs,
            // so "items" is mandatory whenever the parameter is an array.
            if (parameter.ArrayItemType is { Length: > 0 })
            {
                property["items"] = new Dictionary<string, object>
                {
                    ["type"] = typeKeyword(parameter.ArrayItemType)
                };
            }

            properties[parameter.Name] = property;
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = typeKeyword("object"),
            ["properties"] = properties
        };

        // Omitted rather than sent empty: an empty "required" array is legal but noisy,
        // and Claude's SDK already treated it that way before this helper existed.
        if (tool.Required is { Count: > 0 })
        {
            schema["required"] = tool.Required;
        }

        return schema;
    }
}
