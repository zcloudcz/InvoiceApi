using Bunit;
using Fakvio.UI.Shared.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for ResponsiveButton — the toolbar primary-action button
/// that collapses to icon-only on phones (label hidden via .btn-responsive CSS).
/// </summary>
public class ResponsiveButtonTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public ResponsiveButtonTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ResponsiveButton> RenderButton(
        Action<ComponentParameterCollectionBuilder<ResponsiveButton>>? extra = null)
    {
        return Render<ResponsiveButton>(ps =>
        {
            ps.Add(p => p.Label, "Nová faktura");
            ps.Add(p => p.StartIcon, Icons.Material.Filled.Add);
            extra?.Invoke(ps);
        });
    }

    [Fact]
    public void RendersResponsiveClassLabelSpanAndTooltip()
    {
        var cut = RenderButton();

        var button = cut.Find("button");
        button.ClassList.ShouldContain("btn-responsive");
        // Label lives in the span the CSS hides on phones
        cut.Find("span.btn-responsive-label").TextContent.ShouldBe("Nová faktura");
        // Label doubles as tooltip for the icon-only state
        button.GetAttribute("title").ShouldBe("Nová faktura");
        // StartIcon is present (svg icon rendered)
        cut.FindAll("svg").Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void OnClick_Propagates()
    {
        var clicked = false;
        var cut = RenderButton(ps => ps.Add(p => p.OnClick,
            Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(
                this, () => clicked = true)));

        cut.Find("button").Click();

        clicked.ShouldBeTrue();
    }

    [Fact]
    public void ExtraClass_AppendedAfterResponsiveClass()
    {
        var cut = RenderButton(ps => ps.Add(p => p.Class, "mb-4"));

        var button = cut.Find("button");
        button.ClassList.ShouldContain("btn-responsive");
        button.ClassList.ShouldContain("mb-4");
    }
}
