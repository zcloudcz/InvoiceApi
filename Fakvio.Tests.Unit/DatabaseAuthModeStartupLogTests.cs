// ============================================================================
// DatabaseAuthModeStartupLogTests — unit tests for IServiceProvider.LogDatabaseAuthMode().
//
// The API host calls this right after Build() (Fakvio.API/Program.cs).
// It is the fallback the SELFHOST-DB runbook points operators at: the health endpoint that
// reports the same two values is SysAdmin-only, and signing in needs the master database —
// so when the database is the thing that is down, this log line is the only way to tell
// which auth mode the process resolved and which configuration key won.
//
// Pinned here: the line is emitted at Information level, under the exact log category and
// with the exact wording the runbook quotes, it names both values, and it never carries the
// connection string (which holds the password in Password mode).
// ============================================================================

using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class DatabaseAuthModeStartupLogTests
{
    // Password mode, with a password worth hunting for in the log output.
    private const string TestConnectionString =
        "Host=db.example.com;Database=fakvio;Username=fakvio;Password=fakvio_dev";

    // The log category SELFHOST-DB.md §3.4, ADMINGUIDE §13 and the #141 checklist all tell
    // the operator to filter Azure Log stream by. Duplicated here on purpose: a test that
    // read the production constant would follow it wherever it was renamed to, and the
    // renamed category would silently stop matching the filter printed in the runbook.
    private const string RunbookLogCategory = "Fakvio.Infrastructure.Database";

    // The line the runbook quotes verbatim, rendered for the configuration used below.
    private const string RunbookLogMessage =
        "Startup: database auth mode Password (source: Database:AuthMode)";

    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public DatabaseAuthModeStartupLogTests() =>
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(_logger);

    /// <summary>
    /// Builds the provider the hosts have after Build(): the resolved DatabaseOptions
    /// singleton (registered by AddDatabaseContexts) plus the host's logger factory.
    /// Resolve() is used rather than a hand-set instance so the test exercises the real
    /// precedence rules — AuthModeSource has no public setter for exactly that reason.
    /// </summary>
    private ServiceProvider BuildProvider(params (string Key, string Value)[] configuration)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = TestConnectionString
        };

        foreach (var (key, value) in configuration)
        {
            values[key] = value;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddSingleton(DatabaseOptions.Resolve(config));
        services.AddSingleton(_loggerFactory);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Asserts that exactly one Information line matching <paramref name="predicate"/> was
    /// written. Matching on the formatted state rather than on an exact string keeps the
    /// wording free to change while the two values stay mandatory.
    /// </summary>
    private void ShouldHaveLoggedInformation(Func<string, bool> predicate) =>
        _logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => predicate(state.ToString()!)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

    /// <summary>
    /// The whole point: after an App Settings change an operator must be able to read both
    /// the winning mode and the key it came from — with the database down and the health
    /// endpoint therefore unreachable. All three configuration shapes are covered.
    /// </summary>
    [Theory]
    [InlineData("Database:AuthMode", "AzureEntraId", "AzureEntraId", "Database:AuthMode")]
    [InlineData("Database:AuthMode", "Password", "Password", "Database:AuthMode")]
    [InlineData("UseAzureAdAuthentication", "true", "AzureEntraId", "UseAzureAdAuthentication (legacy)")]
    public void LogDatabaseAuthMode_NamesTheResolvedModeAndItsSource(
        string key, string value, string expectedMode, string expectedSource)
    {
        // Arrange
        using var provider = BuildProvider((key, value));

        // Act
        provider.LogDatabaseAuthMode();

        // Assert
        ShouldHaveLoggedInformation(message =>
            message.Contains(expectedMode) && message.Contains(expectedSource));
    }

    /// <summary>
    /// Nothing configured → the built-in default, still logged. A self-hosted deployment that
    /// never touches the key must be able to see what it ended up with.
    /// </summary>
    [Fact]
    public void LogDatabaseAuthMode_WithNoAuthModeConfigured_NamesTheDefault()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        provider.LogDatabaseAuthMode();

        // Assert
        ShouldHaveLoggedInformation(message =>
            message.Contains("Password") && message.Contains("default"));
    }

    /// <summary>
    /// The runbook does not say "look for a line about the auth mode" — it says filter the
    /// Azure Log stream by the category "Fakvio.Infrastructure.Database". A rename of the
    /// production constant is therefore a documentation break, not a refactoring, and the
    /// operator following #141's checklist would find nothing.
    /// </summary>
    [Fact]
    public void LogDatabaseAuthMode_LogsUnderTheCategoryTheRunbookFiltersBy()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        provider.LogDatabaseAuthMode();

        // Assert
        _loggerFactory.Received(1).CreateLogger(RunbookLogCategory);
    }

    /// <summary>
    /// SELFHOST-DB.md, ADMINGUIDE and DEVGUIDE all quote the rendered line character for
    /// character, so an operator can recognise it at a glance. Rewording the message template
    /// silently makes three documents wrong — this pins the exact text for one configuration.
    /// The looser Contains-based theory above still covers the remaining modes.
    /// </summary>
    [Fact]
    public void LogDatabaseAuthMode_EmitsTheExactLineTheRunbookQuotes()
    {
        // Arrange
        using var provider = BuildProvider(("Database:AuthMode", "Password"));

        // Act
        provider.LogDatabaseAuthMode();

        // Assert
        SingleLoggedMessage().ShouldBe(RunbookLogMessage);
    }

    /// <summary>
    /// The formatted text of the one and only log call the method made. Reading it back from
    /// the received call (rather than matching with Arg.Is) is what lets the assertion above
    /// report the actual wording when it drifts.
    /// </summary>
    private string SingleLoggedMessage() =>
        _logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => call.GetArguments()[2]!.ToString()!)
            .ShouldHaveSingleItem();

    /// <summary>
    /// The log sink is not a secret store: in Password mode the connection string carries the
    /// password, so neither it nor any part of it may be written. Only mode and source.
    /// </summary>
    [Fact]
    public void LogDatabaseAuthMode_NeverLogsTheConnectionString()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        provider.LogDatabaseAuthMode();

        // Assert — no log call whatsoever may mention the password, the host or the raw string.
        _logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Is<object>(state =>
                state.ToString()!.Contains("fakvio_dev") ||
                state.ToString()!.Contains("db.example.com") ||
                state.ToString()!.Contains(TestConnectionString)),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
