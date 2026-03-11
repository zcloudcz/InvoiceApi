using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Infrastructure;

/// <summary>
/// NUnit SetUpFixture that runs once before ALL test classes.
/// Verifies that the API and Blazor UI are running and reachable.
/// If either is down, tests fail fast with a clear message instead of
/// timing out on individual page loads.
/// </summary>
[SetUpFixture]
public class PlaywrightSetup
{
    private static readonly TestConfiguration Config = new();

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Check API health — any response (even 401) means the server is up
        await VerifyServiceAsync(http, $"{Config.ApiUrl}/swagger/index.html", "API", Config.ApiUrl);

        // Check Blazor UI — the WASM host should return 200 for the root page
        await VerifyServiceAsync(http, Config.UiUrl, "Blazor UI", Config.UiUrl);
    }

    /// <summary>
    /// Sends a GET request to verify the service is running.
    /// Accepts any HTTP status code as proof the server is alive.
    /// </summary>
    private static async Task VerifyServiceAsync(HttpClient http, string url, string name, string baseUrl)
    {
        try
        {
            var response = await http.GetAsync(url);
            TestContext.Out.WriteLine($"[Setup] {name} at {baseUrl} is running (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex)
        {
            Assert.Fail(
                $"{name} is not reachable at {baseUrl}. " +
                $"Start it before running Playwright tests.\n" +
                $"  API:  dotnet run --project Fakvio.API\n" +
                $"  UI:   dotnet run --project Fakvio.BlazorUI\n" +
                $"Error: {ex.Message}");
        }
    }
}
