namespace Fakvio.Contracts.Dto.AppLog;

/// <summary>
/// DTO posted by the WASM client to forward UI-side errors to the server log.
/// Reaches the server via POST /api/logs/client, which re-logs through ILogger so the entry
/// flows through the standard DatabaseLogger pipeline into the AppLog table.
///
/// Captures all UI errors that would otherwise stay in the browser console only:
/// - HTTP failures observed by ApiClientBase (network, 4xx, 5xx)
/// - Unhandled component exceptions caught by ErrorBoundary
/// - Explicit warnings from page code
/// </summary>
public class ClientLogDto
{
    /// <summary>Severity — one of: Trace, Debug, Information, Warning, Error, Critical.</summary>
    public string Level { get; set; } = "Error";

    /// <summary>Human-readable message; required.</summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Logical source — typically the page or component name (e.g., "InvoiceDetail",
    /// "ApiClientBase.PostAsync"). Becomes the AppLog.Source category prefix.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Full exception details (ToString) if available.</summary>
    public string? Exception { get; set; }

    /// <summary>The page URL the user was on when the error occurred.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Optional CorrelationId from the failing request, when known.
    /// CorrelationIdHandler already attaches one per request; this is for the rare cases the
    /// caller can stamp it explicitly (e.g., manual logs not tied to an HTTP call).
    /// </summary>
    public string? CorrelationId { get; set; }
}
