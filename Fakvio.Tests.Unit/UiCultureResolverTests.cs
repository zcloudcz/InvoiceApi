using Fakvio.UI.Shared.Services;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="UiCultureResolver"/> - stored user culture wins,
/// otherwise the browser language decides (English browsers only, default Czech).
/// </summary>
public class UiCultureResolverTests
{
    [Theory]
    [InlineData("en-US", "cs-CZ", "en-US")]
    [InlineData("cs-CZ", "en-GB", "cs-CZ")]
    [InlineData("de-DE", "en-GB", "en-US")]   // unsupported stored value is ignored
    [InlineData(null, "en-GB", "en-US")]
    [InlineData(null, "en-US", "en-US")]
    [InlineData(null, "cs", "cs-CZ")]
    [InlineData(null, "sk-SK", "cs-CZ")]
    [InlineData(null, null, "cs-CZ")]
    [InlineData("", "", "cs-CZ")]
    public void Resolve_ReturnsExpectedCulture(string? stored, string? browser, string expected)
    {
        UiCultureResolver.Resolve(stored, browser).ShouldBe(expected);
    }
}
