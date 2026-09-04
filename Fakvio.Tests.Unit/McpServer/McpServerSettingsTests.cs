using Fakvio.McpServer.Configuration;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Regression tests for #257: the default API base URL must match the port <c>Fakvio.API</c>
/// actually listens on locally (the <c>https</c> profile in
/// <c>Fakvio.API/Properties/launchSettings.json</c>), so a developer who forgets to set
/// <c>FAKVIO_API_URL</c> gets a working default instead of a silent connection failure.
/// <para>
/// The tests drive <see cref="McpServerSettings.FromEnvironment"/> — the code the process really
/// runs at startup. Asserting <c>new McpServerSettings().ApiBaseUrl</c> instead would not do:
/// the fallback wins over the property initializer, so such a test stays green while the value
/// the server dials drifts back to the broken port. That is precisely the #257 failure mode.
/// </para>
/// </summary>
public class McpServerSettingsTests
{
    [Theory]
    // Unset → the local Fakvio.API https profile. Set → the caller's value, untouched (cloud
    // deploys and non-default ports depend on the override still winning).
    [InlineData(null, McpServerSettings.DefaultApiBaseUrl)]
    [InlineData("https://api.fakvio.cz", "https://api.fakvio.cz")]
    public void FromEnvironment_ResolvesApiBaseUrl(string? configured, string expected)
    {
        var previous = Environment.GetEnvironmentVariable(McpServerSettings.ApiUrlEnv);
        try
        {
            Environment.SetEnvironmentVariable(McpServerSettings.ApiUrlEnv, configured);

            McpServerSettings.FromEnvironment().ApiBaseUrl.ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServerSettings.ApiUrlEnv, previous);
        }
    }

    /// <summary>
    /// Pins the default itself. The theory above proves startup uses this constant; this proves
    /// the constant is the port <c>Fakvio.API</c> listens on. Changing the launch profile without
    /// changing this value (or the reverse) fails here.
    /// </summary>
    [Fact]
    public void DefaultApiBaseUrl_MatchesLocalApiHttpsPort()
    {
        McpServerSettings.DefaultApiBaseUrl.ShouldBe("https://localhost:7047");
    }

    /// <summary>
    /// The property initializer must reuse the same constant, so a settings object built by hand
    /// (tests, future callers) cannot carry a different default than startup does.
    /// </summary>
    [Fact]
    public void ApiBaseUrl_InitializerReusesTheDefaultConstant()
    {
        new McpServerSettings().ApiBaseUrl.ShouldBe(McpServerSettings.DefaultApiBaseUrl);
    }
}
