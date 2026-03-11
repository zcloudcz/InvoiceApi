namespace Fakvio.Tests.Playwright.Infrastructure;

/// <summary>
/// Centralized configuration for Playwright E2E tests.
/// Reads URLs and credentials from environment variables with sensible defaults
/// for local development (API on 5237, UI on 5145).
/// </summary>
public class TestConfiguration
{
    /// <summary>Blazor WASM UI base URL (no trailing slash).</summary>
    public string UiUrl { get; } = Environment.GetEnvironmentVariable("FAKVIO_UI_URL") ?? "http://localhost:5145";

    /// <summary>REST API base URL (no trailing slash).</summary>
    public string ApiUrl { get; } = Environment.GetEnvironmentVariable("FAKVIO_API_URL") ?? "http://localhost:5237";

    /// <summary>SysAdmin email seeded in the database.</summary>
    public string AdminEmail { get; } = Environment.GetEnvironmentVariable("FAKVIO_ADMIN_EMAIL") ?? "admin@zcloud.cz";

    /// <summary>SysAdmin password.</summary>
    public string AdminPassword { get; } = Environment.GetEnvironmentVariable("FAKVIO_ADMIN_PASSWORD") ?? "Invoice123";

    /// <summary>Whether to run browsers in headless mode. Default: true.</summary>
    public bool Headless { get; } = Environment.GetEnvironmentVariable("FAKVIO_HEADLESS") != "false";

    /// <summary>Slow-mo in milliseconds for debugging (0 = no delay).</summary>
    public float SlowMo { get; } = float.TryParse(Environment.GetEnvironmentVariable("FAKVIO_SLOW_MO"), out var v) ? v : 0;

    /// <summary>
    /// Maximum time in ms to wait for Blazor WASM initial load (downloads .NET runtime).
    /// First page load can take 10-30 seconds depending on network/cache.
    /// </summary>
    public int BlazorLoadTimeout { get; } = 30_000;
}
