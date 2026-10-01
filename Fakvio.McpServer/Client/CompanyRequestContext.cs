using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Fakvio.McpServer.Client;

/// <summary>
/// A company choice belongs to one tool invocation, not a pooled client or MCP connection.
/// AsyncLocal follows that invocation through awaits and keeps concurrent callers isolated.
/// The API still validates the credential's explicit grant and the user's live membership.
/// </summary>
public static class CompanyRequestContext
{
    private static readonly AsyncLocal<long?> Selection = new();
    public static long? CompanyId => Selection.Value;

    public static async Task<T> RunAsync<T>(long? companyId, Func<Task<T>> operation)
    {
        var previous = Selection.Value;
        Selection.Value = companyId;
        try { return await operation(); }
        finally { Selection.Value = previous; }
    }

    public static void ConfigureFilters(IMcpRequestFilterBuilder filters)
    {
        filters.AddListToolsFilter(next => async (context, ct) =>
        {
            var result = await next(context, ct);
            // Clone descriptors: changing shared SDK descriptors would race with another list call.
            result.Tools = result.Tools.Select(tool =>
            {
                var clone = JsonSerializer.Deserialize<Tool>(JsonSerializer.Serialize(tool))!;
                var schema = JsonNode.Parse(clone.InputSchema.GetRawText())!.AsObject();
                var properties = schema["properties"] as JsonObject ?? new JsonObject();
                if (properties.ContainsKey("companyId")) throw new InvalidOperationException("A tool business argument conflicts with the reserved companyId context parameter.");
                properties["companyId"] = new JsonObject
                {
                    ["type"] = "integer", ["minimum"] = 1,
                    ["description"] = "Optional company for this call only. Must be explicitly granted to your credential and an active membership. Omit for the credential's original default company. OAuth tokens must always omit this parameter."
                };
                schema["properties"] = properties;
                clone.InputSchema = JsonSerializer.SerializeToElement(schema);
                return clone;
            }).ToList();
            return result;
        });
        filters.AddCallToolFilter(next => async (context, ct) =>
        {
            long? companyId = null;
            if (context.Params?.Arguments is { } arguments && arguments.TryGetValue("companyId", out var value))
            {
                if (!value.TryGetInt64(out var parsed) || parsed <= 0)
                    throw new ArgumentException("companyId must be a positive integer.");
                companyId = parsed;
                // companyId is middleware metadata, not an argument of each individual method.
                context.Params.Arguments = arguments.Where(x => x.Key != "companyId")
                    .ToDictionary(x => x.Key, x => x.Value);
            }
            return await RunAsync(companyId, () => next(context, ct).AsTask());
        });
    }
}
