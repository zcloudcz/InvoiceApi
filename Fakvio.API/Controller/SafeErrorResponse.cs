namespace Fakvio.API.Controller;

/// <summary>
/// Shared helper for building the error text a controller is allowed to send back to the client
/// after an unhandled exception.
///
/// Why this exists: an exception's own message/stack trace can expose internal class names,
/// file paths and configuration details (issue #156 — ChatController leaked an AI provider
/// resolution error; issue #174 — CompanyController leaked a full ex.ToString()). Any controller
/// catching a generic Exception should log the full exception via ILogger (DatabaseLogger picks
/// up the CorrelationId automatically and writes it to AppLog) and return only this text plus the
/// CorrelationId, so support can look the incident up without the client ever seeing internals.
/// </summary>
public static class SafeErrorResponse
{
    /// <summary>
    /// Reads the CorrelationId that <see cref="Fakvio.API.Middleware.CorrelationIdMiddleware"/>
    /// stores on every request. Falls back to "unknown" so a response is still produced if the
    /// middleware somehow did not run (e.g. a unit test hitting the controller directly).
    /// </summary>
    public static string GetCorrelationId(HttpContext httpContext)
        => httpContext.Items["CorrelationId"] as string ?? "unknown";

    /// <summary>
    /// Builds the only error text a client is ever allowed to see for an unexpected server error.
    /// </summary>
    public static string BuildSafeErrorMessage(string correlationId)
        => "An unexpected error occurred while processing your request. " +
           $"Please report this reference ID: {correlationId}";
}
