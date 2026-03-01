using System.Net;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Custom exception thrown by ApiClientBase when the API returns a non-success HTTP status code.
/// Contains the HTTP status code so callers can differentiate between 401, 403, 404, 500 etc.
///
/// Why this exists:
/// Previously, ApiClientBase silently returned null/default for ALL non-success responses,
/// causing every error (401 Unauthorized, 403 Forbidden, 500 Server Error) to display
/// as "Nenalezeno" (Not Found) in the UI. With ApiException, callers can now:
/// - Show the actual error message from the API
/// - Differentiate between error types (unauthorized vs not found vs server error)
/// - Take specific actions per status code (e.g., redirect to login on 401)
/// </summary>
public class ApiException : Exception
{
    /// <summary>
    /// The HTTP status code returned by the API (e.g., 401, 403, 404, 500).
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// The API endpoint that returned the error (e.g., "/api/company/123").
    /// Useful for logging and debugging which call failed.
    /// </summary>
    public string Endpoint { get; }

    /// <summary>
    /// Creates a new ApiException with the given status code, message, and endpoint.
    /// The message is extracted from the API response body (JSON "message" or "title" field).
    /// </summary>
    public ApiException(HttpStatusCode statusCode, string message, string endpoint)
        : base(message)
    {
        StatusCode = statusCode;
        Endpoint = endpoint;
    }
}
