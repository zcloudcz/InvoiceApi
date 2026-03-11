using Fakvio.Tests.Playwright.Infrastructure;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Settings;

/// <summary>
/// Tests for the My Company page (/my-company).
/// </summary>
[TestFixture]
public class MyCompanyTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/my-company", "h4");
    }

    [Test]
    public async Task MyCompany_PageLoads_ShowsHeading()
    {
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task MyCompany_HasInputFields()
    {
        var inputs = Page.Locator(".mud-input input");
        var count = await inputs.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "My Company page should have input fields for company details");
    }
}
