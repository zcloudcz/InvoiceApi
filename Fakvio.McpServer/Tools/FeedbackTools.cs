using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Fakvio.Contracts.Dto.Feedback;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// Feedback tools delegate to the authenticated API. The server derives ownership and checks
/// scopes/roles; an MCP argument can never select another submitter or bypass SysAdmin policy.
/// </summary>
[McpServerToolType]
public static class FeedbackTools
{
    [McpServerTool(Name = "submit_feedback", Title = "Submit feedback", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Submit a Bug, Idea, or Observation. Subject: 1-200 characters; description: 1-10000. Optional page must be a local path; queries/fragments are removed by the API. Ownership comes from your authenticated user and company. Requires write scope.")]
    public static Task<string> SubmitFeedback(IFakvioApiClient api,
        [Description("Report type, subject, description, optional local page and appVersion.")] CreateFeedbackDto feedback,
        CancellationToken ct = default)
        => ExecuteAsync(() => api.CreateFeedbackAsync(feedback, ct), ct);

    [McpServerTool(Name = "list_feedback", Title = "List my feedback", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("List your own reports in the authenticated company, newest first. Requires read scope.")]
    public static Task<string> ListFeedback(IFakvioApiClient api,
        [Description("Page number, starting at 1.")] int page = 1,
        [Description("Results per page, 1-100.")] int pageSize = 25,
        EFeedbackType? type = null, EFeedbackStatus? status = null, CancellationToken ct = default)
        => ExecuteAsync(() => api.GetFeedbackAsync(new FeedbackFilterDto { Page = page, PageSize = pageSize, Type = type, Status = status }, ct), ct);

    [McpServerTool(Name = "get_feedback", Title = "Get my feedback", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Get one of your reports in the authenticated company, including status and public response. Inaccessible reports return not_found. Requires read scope.")]
    public static Task<string> GetFeedback(IFakvioApiClient api, long id, CancellationToken ct = default)
        => ExecuteAsync(() => api.GetFeedbackByIdAsync(id, ct), ct);

    [McpServerTool(Name = "list_admin_feedback", Title = "List feedback inbox", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("List the global feedback inbox. Requires SysAdmin authorization and read scope accepted by the API; read/write scopes alone do not grant the role.")]
    public static Task<string> ListAdminFeedback(IFakvioApiClient api,
        [Description("Page number, starting at 1.")] int page = 1,
        [Description("Results per page, 1-100.")] int pageSize = 25,
        EFeedbackType? type = null, EFeedbackStatus? status = null, CancellationToken ct = default)
        => ExecuteAsync(() => api.GetAdminFeedbackAsync(new FeedbackFilterDto { Page = page, PageSize = pageSize, Type = type, Status = status }, ct), ct, true);

    [McpServerTool(Name = "get_admin_feedback", Title = "Get feedback inbox report", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Get a global inbox report. Requires API SysAdmin authorization and read scope.")]
    public static Task<string> GetAdminFeedback(IFakvioApiClient api, long id, CancellationToken ct = default)
        => ExecuteAsync(() => api.GetAdminFeedbackByIdAsync(id, ct), ct, true);

    [McpServerTool(Name = "update_feedback_status", Title = "Respond to feedback", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false),
     Description("Set feedback status (New, InProgress, Resolved, Declined) and optional publicResponse (at most 10000 characters). Requires API SysAdmin authorization and write scope.")]
    public static Task<string> UpdateFeedbackStatus(IFakvioApiClient api, long id, UpdateFeedbackStatusDto update, CancellationToken ct = default)
        => ExecuteAsync(() => api.UpdateFeedbackStatusAsync(id, update, ct), ct, true);

    /// <summary>Keep tool errors consistent and let caller cancellation reach the MCP transport.</summary>
    private static async Task<string> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken ct, bool admin = false)
    {
        try
        {
            return JsonSerializer.Serialize(await operation(), McpToolJsonOptions.Default);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (FakvioApiException ex) when (admin && ex.StatusCode == HttpStatusCode.Forbidden)
        {
            // More API-key scopes cannot grant SysAdmin. Avoid suggesting an ineffective upgrade.
            return JsonSerializer.Serialize(new { error = "forbidden", message = "This operation requires SysAdmin authorization and the appropriate read/write scope accepted by the API." });
        }
        catch (FakvioApiException ex) when (ex.StatusCode.HasValue)
        {
            // The shared error formatter logs exceptions. Remove the raw response body from
            // the logged exception because a validation response might repeat submitted text.
            return McpToolError.ToJson(new FakvioApiException("Feedback API request failed.", ex.StatusCode.Value, ex.SafeMessage));
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}
