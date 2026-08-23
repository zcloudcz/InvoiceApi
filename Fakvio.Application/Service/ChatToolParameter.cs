namespace Fakvio.Application.Service;

/// <summary>
/// JSON Schema data type of a chat tool parameter.
///
/// The AI provider receives the schema and enforces it, so the model cannot send
/// a number where an array is expected. Before this existed, every parameter was
/// declared as "string" and numbers/booleans/arrays arrived escaped inside strings.
///
/// Junior note: these names map 1:1 onto the JSON Schema "type" keyword used by
/// OpenAI / Anthropic / Gemini / Ollama function calling — see
/// <see cref="ChatToolParameterTypeExtensions.ToJsonSchemaType"/>.
/// </summary>
public enum ChatToolParameterType
{
    /// <summary>Plain text — the default for names, codes, dates (YYYY-MM-DD), Base64 blobs.</summary>
    String,

    /// <summary>Decimal number (amount, unit price, VAT rate).</summary>
    Number,

    /// <summary>Whole number (page, limit, record id).</summary>
    Integer,

    /// <summary>True / false flag.</summary>
    Boolean,

    /// <summary>
    /// Array of JSON objects — used for invoice line items.
    /// Deliberately narrower than a generic "array": every array parameter we have
    /// today is an array of objects. If an array of scalars is ever needed, add a
    /// new member here plus one line in <see cref="ChatToolParameterTypeExtensions"/>
    /// — that keeps the schema honest instead of silently mis-declaring the element type.
    /// </summary>
    ObjectArray
}

/// <summary>
/// One parameter of a chat tool, described as data instead of prose.
///
/// This is the single source of truth for a parameter: the native tool-calling
/// schema (<c>NativeToolDefinition</c>), the text-based system-prompt instructions,
/// and the central pre-execution validation are all generated from it.
///
/// Junior note: <c>required</c> on Name / Type / Description means the compiler
/// refuses to build a parameter that forgets them — a broken schema fails at build
/// time, not silently at runtime in front of a customer.
/// </summary>
public sealed record ChatToolParameter
{
    /// <summary>
    /// Parameter name exactly as the AI must send it, snake_case
    /// (e.g. "registration_number"). Must be unique within one tool.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Data type. Drives both the JSON Schema sent to the model and the
    /// central value validation done before the tool runs.
    /// </summary>
    public required ChatToolParameterType Type { get; init; }

    /// <summary>
    /// What the parameter means, written for the model (short, imperative).
    /// Shown in the schema description and in the text-based tool instructions.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// True when the tool cannot run without this parameter.
    /// Missing required parameters are rejected centrally, before the tool executes.
    /// </summary>
    public bool IsRequired { get; init; }

    /// <summary>
    /// Optional closed list of accepted values (JSON Schema "enum").
    /// Only meaningful for <see cref="ChatToolParameterType.String"/>.
    /// Matching is case-insensitive, because models are inconsistent about casing.
    /// </summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }
}

/// <summary>
/// Maps <see cref="ChatToolParameterType"/> onto JSON Schema keywords.
/// Kept next to the enum so a new member cannot be added without deciding how it serializes.
/// </summary>
public static class ChatToolParameterTypeExtensions
{
    /// <summary>
    /// JSON Schema "type" value for this parameter type.
    /// </summary>
    public static string ToJsonSchemaType(this ChatToolParameterType type) => type switch
    {
        ChatToolParameterType.String => "string",
        ChatToolParameterType.Number => "number",
        ChatToolParameterType.Integer => "integer",
        ChatToolParameterType.Boolean => "boolean",
        ChatToolParameterType.ObjectArray => "array",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped chat tool parameter type.")
    };

    /// <summary>
    /// JSON Schema "items.type" for array parameters, or null for scalars.
    /// Providers must emit it — an array without an element schema is rejected
    /// by strict function-calling APIs.
    /// </summary>
    public static string? ToJsonSchemaItemType(this ChatToolParameterType type) => type switch
    {
        ChatToolParameterType.ObjectArray => "object",
        _ => null
    };
}
