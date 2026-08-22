using System.Reflection;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the gate that keeps <see cref="DatabaseConnectivitySmokeTests"/> out of a plain
/// <c>dotnet test</c> run.
///
/// These tests are the regression guard for the whole point of issue #137: the unit suite must
/// stay green on a machine with no PostgreSQL and no <c>az login</c>. The gate itself is the
/// only thing standing between that and the chronic red state the suite had before — so the
/// gate needs coverage of its own, and it must never be gated (these tests touch no database).
/// </summary>
public sealed class DatabaseSmokeFactAttributeTests
{
    /// <summary>The only value that opens the gate.</summary>
    private const string EnabledValue = "1";

    [Fact]
    public void Skip_WhenSwitchIsAbsent_ReturnsReasonSoTheTestIsSkippedNotRun()
    {
        // Arrange — an absent variable is the default state of any dev machine and of CI
        using var _ = OverrideSmokeSwitch(null);
        var attribute = new DatabaseSmokeFactAttribute();

        // Act
        var skipReason = attribute.Skip;

        // Assert — a non-null Skip is what makes xUnit report the test as skipped
        skipReason.ShouldBe(DatabaseSmokeFactAttribute.DisabledReason);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("11")]
    public void Skip_WhenSwitchHasAnyValueOtherThanOne_KeepsTheGateClosed(string switchValue)
    {
        // Arrange — anything but the exact opt-in value must be treated as "not enabled",
        // otherwise a stray variable could silently turn the suite red again
        using var _ = OverrideSmokeSwitch(switchValue);
        var attribute = new DatabaseSmokeFactAttribute();

        // Act & Assert
        DatabaseSmokeFactAttribute.IsEnabled.ShouldBeFalse();
        attribute.Skip.ShouldBe(DatabaseSmokeFactAttribute.DisabledReason);
    }

    [Fact]
    public void Skip_WhenSwitchIsExactlyOne_IsNullSoTheTestActuallyRuns()
    {
        // Arrange
        using var _ = OverrideSmokeSwitch(EnabledValue);
        var attribute = new DatabaseSmokeFactAttribute();

        // Act & Assert — null Skip = xUnit executes the test
        DatabaseSmokeFactAttribute.IsEnabled.ShouldBeTrue();
        attribute.Skip.ShouldBeNull();
    }

    [Fact]
    public void Skip_WhenSwitchIsOnAndAReasonWasSetExplicitly_ReturnsThatReason()
    {
        // Arrange — the attribute overrides both the getter and the setter, so an explicitly
        // set reason (the normal FactAttribute feature) must still survive
        const string explicitReason = "Temporarily disabled while the schema is being rebuilt.";
        using var _ = OverrideSmokeSwitch(EnabledValue);
        var attribute = new DatabaseSmokeFactAttribute { Skip = explicitReason };

        // Act & Assert
        attribute.Skip.ShouldBe(explicitReason);
    }

    [Fact]
    public void DisabledReason_ExplainsHowToRunTheSmokeTests()
    {
        // Assert — the issue requires an actionable message, not a silent skip: the switch,
        // both ways of getting a reachable server, and every override a Docker run needs
        var reason = DatabaseSmokeFactAttribute.DisabledReason;

        reason.ShouldContain(DatabaseSmokeFactAttribute.EnableEnvironmentVariable);
        reason.ShouldContain("az login");
        reason.ShouldContain("docker compose up -d");
        reason.ShouldContain("ConnectionStrings__DefaultConnection");
        // Both auth-mode keys, because DatabaseOptions.Resolve throws when the pair disagrees
        reason.ShouldContain("Database__AuthMode");
        reason.ShouldContain("UseAzureAdAuthentication");
    }

    [Fact]
    public void EverySmokeTest_IsGatedByTheAttribute()
    {
        // Arrange — a plain [Fact] added to the smoke class later would reintroduce the
        // chronic red the gate exists to remove, so pin the whole class, not one method
        var testMethods = GetTestMethods(typeof(DatabaseConnectivitySmokeTests));

        // Assert — all four original connectivity assertions are still there, all gated
        testMethods.Count.ShouldBe(4);
        testMethods.ShouldAllBe(m => m.GetCustomAttribute<DatabaseSmokeFactAttribute>() != null);
    }

    /// <summary>
    /// Every method of <paramref name="testClass"/> that xUnit would treat as a test —
    /// i.e. carrying <see cref="FactAttribute"/> or any attribute derived from it
    /// (<see cref="TheoryAttribute"/> and <c>DatabaseSmokeFactAttribute</c> both are).
    /// </summary>
    private static List<MethodInfo> GetTestMethods(Type testClass) =>
        [.. testClass
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<FactAttribute>(inherit: true) is not null)];

    /// <summary>
    /// Sets the smoke switch for the duration of a single test and restores whatever the
    /// process had before. Environment variables are process-wide state, so a test that
    /// changes one must always put it back — otherwise the outcome depends on test order.
    /// </summary>
    private static IDisposable OverrideSmokeSwitch(string? value) =>
        new EnvironmentVariableScope(DatabaseSmokeFactAttribute.EnableEnvironmentVariable, value);

    /// <inheritdoc cref="OverrideSmokeSwitch"/>
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
    }
}
