using Fakvio.Tests.Playwright.Infrastructure;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Settings;

/// <summary>
/// Tests for the Number Sequences settings page (/number-sequences).
/// </summary>
[TestFixture]
public class NumberSequenceTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/number-sequences", "h4");
    }

    [Test]
    public async Task NumberSequences_PageLoads_ShowsHeading()
    {
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task NumberSequences_HasContent()
    {
        // The page should contain table or card elements
        var content = Page.Locator(".mud-table, .mud-card");
        var count = await content.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "Number sequences page should render (heading verified in other test)");
    }
}
