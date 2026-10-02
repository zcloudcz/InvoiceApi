namespace Fakvio.Application.Service;

/// <summary>
/// Stateless background-work service (DEVGUIDE §6.1) that sends due webhook deliveries and
/// retries failed ones with backoff. The thin BackgroundService wrapper is
/// <c>WebhookWorker</c> (Infrastructure); the PostgreSQL advisory lock guaranteeing only one
/// App Service replica runs a cycle at a time lives there too.
/// </summary>
public interface IWebhookDispatchService
{
    /// <summary>
    /// One pass for the given tenant: sends every due Pending delivery (HMAC-signed POST,
    /// SSRF-guarded, 10s timeout, no redirects), applies the retry backoff schedule on failure,
    /// and deletes terminal (Succeeded/Failed) deliveries older than 30 days.
    /// </summary>
    Task RunCycleAsync(CancellationToken ct = default);
}
