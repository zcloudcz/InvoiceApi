using System.Text.Json;
using Fakvio.McpServer.Tools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for <see cref="McpToolError"/> — the single place every MCP tool's catch-all
/// exception handler routes through (issue #279).
///
/// Junior note: before this fix, each of the 37 tool methods did its own
/// <c>catch (Exception ex) { error = ex.Message }</c>. <c>ex.Message</c> can carry the raw
/// API error body (see <c>FakvioApiClient.EnsureSuccessAsync</c>) — stack traces, SQL
/// details, internal IDs — straight to an external AI client. This class asserts the fix
/// holds in the one place it now lives: the exception is logged with full detail
/// server-side, and the JSON handed back never contains <c>ex.Message</c>.
/// </summary>
public class McpToolErrorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public McpToolErrorTests()
    {
        // McpToolError.Logger is a shared static (tool methods are static — see the class
        // doc). Reset it before each test so tests don't leak substitute loggers into each
        // other via test execution order.
        McpToolError.Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    [Fact]
    public void ToJson_NeverIncludesTheExceptionMessage()
    {
        // Arrange: an exception message carrying something that must never reach the
        // AI client — e.g. the raw API error body EnsureSuccessAsync embeds.
        var ex = new InvalidOperationException(
            "Validation failed: ClientId is required. SQL: SELECT * FROM \"tenant_5\".\"Client\" WHERE ...");

        // Act
        var json = McpToolError.ToJson(ex);

        // Assert: stable public code + neutral text, nothing from ex.Message.
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().ShouldBe("internal_error");
        var message = root.GetProperty("message").GetString();
        message.ShouldNotContain("ClientId is required");
        message.ShouldNotContain("SQL");
        message.ShouldNotContain("tenant_5");
    }

    [Fact]
    public void ToJson_LogsTheFullExceptionServerSide()
    {
        // Arrange
        var logger = Substitute.For<ILogger>();
        McpToolError.Logger = logger;
        var ex = new InvalidOperationException("Validation failed: ClientId is required.");

        // Act
        McpToolError.ToJson(ex);

        // Assert — ILogger.Log received the exception object itself (not just its
        // message), at Error level, so the full detail (incl. stack trace) survives
        // server-side even though the AI client only sees the neutral JSON.
        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            ex,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
