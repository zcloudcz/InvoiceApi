using System.Reflection;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Contract tests between <see cref="NavigateTool"/> and the pages that really exist in
/// <c>Fakvio.UI.Shared</c>.
///
/// Why reflection instead of a hand-copied list of routes: the tool used to offer six
/// hard-coded targets while the app had roughly forty pages, and nothing noticed. A list
/// copied into a test would go stale exactly the same way. Blazor puts the <c>@page</c>
/// directive on the compiled component as a <see cref="RouteAttribute"/> and
/// <c>@attribute [Authorize]</c> as an <see cref="AuthorizeAttribute"/>, so the real
/// routing table and the real authorization rules can be read straight off the assembly.
///
/// What these tests pin (issue #229):
/// - every target the tool offers opens a route that actually exists,
/// - every target points at a page a normal tenant user may open,
/// - every tenant-facing page is reachable through the tool.
///
/// Junior note: nothing here renders a component or starts a browser. The page classes are
/// only inspected for attributes, and the tool is executed against a mocked client service.
/// </summary>
public class NavigateToolRouteCatalogTests
{
    /// <summary>Role name used by the SysAdmin-only pages the assistant must not offer.</summary>
    private const string SysAdminRole = "SysAdmin";

    /// <summary>The one target with no fixed URL — its page id comes from resolving a client by name.</summary>
    private const string ClientDetailTarget = "client_detail";

    /// <summary>Any page class lives in the RCL, so one of them gives us the assembly to scan.</summary>
    private static readonly Assembly UiAssembly = typeof(Fakvio.UI.Shared.Components.Pages.Clients).Assembly;

    /// <summary>Every <c>@page</c> declared in the UI, paired with the page that declares it.</summary>
    private static readonly IReadOnlyList<PageRoute> UiRoutes = UiAssembly
        .GetTypes()
        .SelectMany(
            pageType => pageType.GetCustomAttributes<RouteAttribute>(),
            (pageType, route) => new PageRoute(route.Template, pageType))
        .ToList();

    // ─── The catalog matches reality ──────────────────────────────────────

    [Fact]
    public async Task EveryOfferedTarget_OpensARouteThatExists()
    {
        UiRoutes.ShouldNotBeEmpty("the UI assembly must expose its @page routes via RouteAttribute");

        foreach (var (target, url) in await NavigateEveryStaticTargetAsync())
        {
            FindPage(url).ShouldNotBeNull(
                $"target '{target}' navigates to '{url}', which is not an @page route of any UI page");
        }
    }

    [Fact]
    public async Task EveryOfferedTarget_OpensAPageATenantUserMayOpen()
    {
        // The assistant runs inside a tenant user's session. Offering /logs or /login would
        // either bounce the user off an authorization check or throw them out of the app.
        foreach (var (target, url) in await NavigateEveryStaticTargetAsync())
        {
            var page = FindPage(url)!;

            IsTenantFacing(page.PageType).ShouldBeTrue(
                $"target '{target}' opens '{url}' ({page.PageType.Name}), which is an anonymous " +
                "auth-flow page or a SysAdmin-only page — those are excluded by design");
        }
    }

    [Fact]
    public async Task EveryTenantFacingPage_IsOfferedByTheTool()
    {
        var offered = (await NavigateEveryStaticTargetAsync())
            .Select(entry => PathOf(entry.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Routes with a path parameter (/invoices/{Id:long}, …) need an entity resolved first.
        // Only client resolution exists today, and client_detail covers it — see the dedicated
        // test below.
        var missing = UiRoutes
            .Where(route => IsTenantFacing(route.PageType) && !route.Template.Contains('{'))
            .Select(route => route.Template)
            .Where(template => !offered.Contains(template))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList();

        missing.ShouldBeEmpty(
            "every tenant-facing page must be reachable through the navigate tool — add a target " +
            "to NavigateTool.Routes for each route listed here. The only pages this test lets " +
            "through are the ones filtered above: anonymous or SysAdmin-only pages, and routes " +
            "with a path parameter");
    }

    [Fact]
    public async Task ClientDetail_IsOffered_AndTargetsTheParameterisedClientRoute()
    {
        var tool = CreateTool(new ClientDto { Id = 7, CompanyName = "Firma XYZ" });

        AllowedTargets(tool).ShouldContain(ClientDetailTarget);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["target"] = ClientDetailTarget,
            ["client_name"] = "Firma XYZ"
        });

        result.UiAction!.Url.ShouldBe("/clients/7");

        // The id is substituted by the tool, so the route can only be matched by its shape.
        UiRoutes
            .Where(route => IsTenantFacing(route.PageType))
            .Select(route => route.Template)
            .ShouldContain(
                template => template.StartsWith("/clients/{", StringComparison.OrdinalIgnoreCase),
                "client_detail builds /clients/{id}, so that route must exist in the UI");
    }

    // ─── Test helpers ─────────────────────────────────────────────────────

    /// <summary>One <c>@page</c> route and the page component that declares it.</summary>
    private sealed record PageRoute(string Template, Type PageType);

    /// <summary>
    /// Runs the tool once per offered target (all but <c>client_detail</c>, which needs a name)
    /// and returns target → URL. Going through <c>ExecuteAsync</c> instead of reading a private
    /// catalog keeps the production surface unchanged and exercises the real mapping.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> NavigateEveryStaticTargetAsync()
    {
        var tool = CreateTool(client: null);
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var target in AllowedTargets(tool).Where(target => target != ClientDetailTarget))
        {
            var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["target"] = target });

            result.IsSuccess.ShouldBeTrue($"target '{target}' must navigate, but failed: {result.ErrorMessage}");
            result.UiAction.ShouldNotBeNull($"target '{target}' must return a navigate action");
            result.UiAction.Url.ShouldNotBeNullOrWhiteSpace();

            urls[target] = result.UiAction.Url!;
        }

        urls.ShouldNotBeEmpty();
        return urls;
    }

    private static IReadOnlyList<string> AllowedTargets(IChatTool tool)
        => tool.Parameters.Single(parameter => parameter.Name == "target").AllowedValues
            ?? throw new InvalidOperationException("the navigate tool must advertise its targets as a closed list");

    /// <summary>Query string is navigation state, not part of the route — strip it before matching.</summary>
    private static string PathOf(string url)
    {
        var queryStart = url.IndexOf('?');
        return queryStart < 0 ? url : url[..queryStart];
    }

    private static PageRoute? FindPage(string url)
        => UiRoutes.FirstOrDefault(route => route.Template.Equals(PathOf(url), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a normal tenant user can open the page: it requires authentication (so the
    /// anonymous auth-flow and error pages drop out) and is not restricted to SysAdmin alone
    /// (so /logs, /companies, /system-settings, /sysadmin/* drop out). "Admin,SysAdmin" pages
    /// stay in — Admin is a tenant role.
    /// </summary>
    private static bool IsTenantFacing(Type pageType)
    {
        var authorize = pageType.GetCustomAttribute<AuthorizeAttribute>();

        if (authorize is null)
            return false;

        if (string.IsNullOrWhiteSpace(authorize.Roles))
            return true;

        return authorize.Roles
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(role => !role.Equals(SysAdminRole, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A tool over a mocked client service. When <paramref name="client"/> is null the search
    /// returns nothing — static targets never search, so that stays unused.
    /// </summary>
    private static NavigateTool CreateTool(ClientDto? client)
    {
        var clientService = Substitute.For<IClientService>();
        var matches = client is null ? new List<ClientDto>() : [client];

        clientService
            .GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(matches, matches.Count, 1, 5));

        return new NavigateTool(clientService, Substitute.For<ILogger<NavigateTool>>());
    }
}
