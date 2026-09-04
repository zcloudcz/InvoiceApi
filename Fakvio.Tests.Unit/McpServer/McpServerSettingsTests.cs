using Fakvio.McpServer.Configuration;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Regression test for #257: the default <see cref="McpServerSettings.ApiBaseUrl"/> must
/// match the port <c>Fakvio.API</c> actually listens on locally (the <c>https</c> profile in
/// <c>Fakvio.API/Properties/launchSettings.json</c>), so a developer who forgets to set
/// <c>FAKVIO_API_URL</c> gets a working default instead of a silent connection failure.
/// </summary>
public class McpServerSettingsTests
{
    [Fact]
    public void ApiBaseUrl_DefaultsToLocalApiHttpsPort()
    {
        var settings = new McpServerSettings();

        settings.ApiBaseUrl.ShouldBe("https://localhost:7047");
    }
}
