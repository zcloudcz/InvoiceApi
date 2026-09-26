using System.ComponentModel;
using System.Text.Json;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tool for the tenant readiness report — "is the company setup complete enough
/// to issue invoices, and if not, what is missing?".
///
/// Junior note: like every MCP tool here, this one owns no logic. It calls
/// <c>GET /api/readiness</c> through <see cref="IFakvioApiClient"/>, so the rules
/// (DEVGUIDE §4.12) stay in one service on the server and the MCP answer can never
/// drift from what the chat assistant or the UI banner says.
/// </summary>
[McpServerToolType]
public static class ReadinessTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Returns everything the tenant still has to fill in before it can invoice safely.
    /// </summary>
    [McpServerTool(Title = "Check setup readiness", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Check whether the company setup is complete enough to issue invoices. " +
        "Returns isReady plus every missing setting with its code, severity (Blocking or Warning), " +
        "the empty fields, the issuer it belongs to, and the fixRoute — the app page where the user fixes it. " +
        "An empty issues array means nothing is missing. Read-only.")]
    public static async Task<string> GetReadiness(
        IFakvioApiClient api,
        [Description("Optional issuer ID — check only this issuer instead of every issuer of the company")] long? issuerId = null,
        CancellationToken ct = default)
    {
        try
        {
            var report = await api.GetReadinessAsync(issuerId, ct);

            // Null only happens for an explicit issuerId the tenant does not have.
            if (report == null)
                return JsonSerializer.Serialize(
                    new { error = $"Issuer with ID {issuerId} not found." }, JsonOptions);

            return JsonSerializer.Serialize(report, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}
