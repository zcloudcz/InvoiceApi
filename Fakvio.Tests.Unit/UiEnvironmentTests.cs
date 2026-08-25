using Fakvio.UI.Shared.Services;
using Shouldly;
using Xunit;

namespace Fakvio.Tests.Unit;

/// <summary>The rule that colours the test app bar pink — pinned so "production" never turns pink.</summary>
public class UiEnvironmentTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("production", false)]
    [InlineData("Production", false)]
    [InlineData("test", true)]
    [InlineData("staging", true)]
    public void OnlyNamedNonProductionEnvironmentsArePink(string? name, bool expectedPink)
    {
        UiEnvironment.IsNonProduction(name).ShouldBe(expectedPink);
        (UiEnvironment.AppBarStyle(name) is not null).ShouldBe(expectedPink);
    }
}
