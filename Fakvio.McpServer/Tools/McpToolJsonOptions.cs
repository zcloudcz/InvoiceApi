using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// The <see cref="JsonSerializerOptions"/> the SDK uses to deserialize a tool call's arguments
/// and to generate the JSON schema an AI client sees in <c>tools/list</c> — passed explicitly to
/// <c>WithToolsFromAssembly()</c> in <see cref="McpServerRegistration"/> (N2.5).
///
/// Junior note: every tool class also keeps its own private <c>JsonOptions</c> with the same
/// shape (camelCase, case-insensitive, string enums) for serializing ITS OWN output — that one
/// is unrelated and stays where it is. This shared instance only matters for typed DTO
/// PARAMETERS (e.g. <c>ClientTools.CreateClient(CreateClientDto client, …)</c>): without it, the
/// SDK falls back to <see cref="JsonSerializerOptions.Default"/>, which expects PascalCase
/// property names and integer enum values — an AI client sending
/// <c>{"paymentMethod": "BankTransfer"}</c> (camelCase, string enum) would fail to deserialize.
/// </summary>
public static class McpToolJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        // The SDK's schema generator and reflection-based (de)serialization both need a
        // TypeInfoResolver — without it, .NET refuses to use this instance at all ("must
        // specify a TypeInfoResolver setting before being marked as read-only"). Our tool DTOs
        // are plain reflectable POCOs (no source-generated JsonSerializerContext), so the
        // default reflection-based resolver is the correct (and only applicable) choice here.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}
