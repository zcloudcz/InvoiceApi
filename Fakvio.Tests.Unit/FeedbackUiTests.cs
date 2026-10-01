using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Fakvio.Contracts.Dto.Feedback;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Components.Layout;
using Fakvio.UI.Shared.Components.Pages;
using Blazored.LocalStorage;
using Microsoft.Extensions.Configuration;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Exercises actual rendered feedback forms against a controlled HTTP boundary.</summary>
public class FeedbackUiTests : BunitContext, IAsyncLifetime
{
    private readonly Backend _backend = new();
    private IDialogReference? _dialog;

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public FeedbackUiTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        var client = new HttpClient(_backend) { BaseAddress = new Uri("https://test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(client);
        Services.AddSingleton(factory);
        AddAuthorization().SetAuthorized("reporter@example.test");
        Services.AddSingleton(sp => new FeedbackApiService(factory, NullLogger<FeedbackApiService>.Instance,
            sp.GetRequiredService<AuthenticationStateProvider>()));
    }

    [Theory]
    [InlineData("cs-CZ", "Nahlásit chybu nebo nápad")]
    [InlineData("en-US", "Report a bug or idea")]
    public void Button_HasLocalizedAccessibleLabel(string culture, string label)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        var cut = Render<FeedbackButton>();
        cut.Find("button").GetAttribute("aria-label").ShouldBe(label);
    }

    [Theory]
    [InlineData("cs-CZ")]
    [InlineData("en-US")]
    public void AllFeedbackResourcesExist(string culture)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        var localizer = Services.GetRequiredService<IStringLocalizer<SharedResource>>();
        foreach (var value in Enum.GetValues<EFeedbackType>())
            localizer[$"EFeedbackType_{value}"].ResourceNotFound.ShouldBeFalse();
        foreach (var value in Enum.GetValues<EFeedbackStatus>())
            localizer[$"EFeedbackStatus_{value}"].ResourceNotFound.ShouldBeFalse();
        foreach (var key in new[] { "Report", "Mine", "Inbox", "Subject", "Description", "Page", "Version",
                     "Send", "Sent", "Error", "Required", "Response", "Saved", "Open", "Refresh", "Created", "Status", "Type" })
            localizer[$"Feedback_{key}"].ResourceNotFound.ShouldBeFalse();
    }

    [Fact]
    public async Task Submission_SendsSafeContext_AndClosesWithReportId()
    {
        var cut = await OpenDialogAsync();
        await FillAsync(cut);
        await cut.FindComponents<MudButton>().Last().InvokeAsync(() => cut.FindComponents<MudButton>().Last().Instance.OnClick.InvokeAsync());
        cut.WaitForAssertion(() => _backend.PostCount.ShouldBe(1));
        _backend.Created!.Subject.ShouldBe("Test subject");
        _backend.Created.Page.ShouldBe("/invoices");
        _backend.Created.AppVersion.ShouldNotBeNullOrWhiteSpace();
        (await _dialog!.Result)!.Data.ShouldBe(12L);
    }

    [Fact]
    public async Task Submission_FailureKeepsFields_AndAllowsRetry()
    {
        _backend.Fail = true;
        var cut = await OpenDialogAsync();
        await FillAsync(cut);
        var submit = cut.FindComponents<MudButton>().Last();
        await cut.InvokeAsync(() => submit.Instance.OnClick.InvokeAsync());
        cut.FindComponents<MudTextField<string>>()[0].Instance.Value.ShouldBe("Test subject");
        cut.FindComponents<MudTextField<string>>()[1].Instance.Value.ShouldBe("Detailed description");
        cut.FindAll(".mud-alert").Count.ShouldBe(1);
        _dialog!.Result.IsCompleted.ShouldBeFalse();
        _backend.Fail = false;
        await cut.InvokeAsync(() => submit.Instance.OnClick.InvokeAsync());
        _backend.PostCount.ShouldBe(2);
        (await _dialog!.Result)!.Canceled.ShouldBeFalse();
    }

    [Fact]
    public async Task PendingSubmission_IgnoresRepeatedClicks()
    {
        _backend.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = await OpenDialogAsync();
        await FillAsync(cut);
        var submit = cut.FindComponents<MudButton>().Last();
        var first = cut.InvokeAsync(() => submit.Instance.OnClick.InvokeAsync());
        cut.WaitForAssertion(() => _backend.PostCount.ShouldBe(1));
        await cut.InvokeAsync(() => submit.Instance.OnClick.InvokeAsync());
        _backend.PostCount.ShouldBe(1);
        _backend.Pending.SetResult();
        await first;
    }

    [Fact]
    public void Detail_RendersEscapedTextAndPublicResponse()
    {
        _backend.Report.Description = "<script>alert('secret')</script>";
        _backend.Report.PublicResponse = "<b>Public reply</b>";
        var cut = Render<FeedbackDetail>(p => p.Add(x => x.Id, 12));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Public reply"));
        cut.FindAll("script").Count.ShouldBe(0);
        cut.FindAll("b").Count.ShouldBe(0);
        cut.Markup.ShouldContain("&lt;script&gt;");
        cut.FindComponents<EnumSelect<EFeedbackStatus>>().Count.ShouldBe(0);
    }

    [Fact]
    public async Task SysAdmin_CanSaveStatusAndPublicResponse()
    {
        AddAuthorization().SetRoles("SysAdmin");
        var cut = Render<FeedbackDetail>(p => p.Add(x => x.Id, 12).Add(x => x.Admin, true));
        cut.WaitForAssertion(() => cut.FindComponents<EnumSelect<EFeedbackStatus>>().Count.ShouldBe(1));
        await cut.InvokeAsync(() => cut.FindComponent<EnumSelect<EFeedbackStatus>>().Instance.ValueChanged.InvokeAsync(EFeedbackStatus.Resolved));
        await cut.InvokeAsync(() => cut.FindComponent<MudTextField<string>>().Instance.ValueChanged.InvokeAsync("Fixed"));
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().Last().Instance.OnClick.InvokeAsync());
        _backend.Updated!.Status.ShouldBe(EFeedbackStatus.Resolved);
        _backend.Updated.PublicResponse.ShouldBe("Fixed");
        _backend.LastPath.ShouldBe("/api/sysadmin/feedback/12");
    }

    [Fact]
    public void NonSysAdmin_DoesNotRenderStatusControls_EvenWithAdminParameter()
    {
        var cut = Render<FeedbackDetail>(p => p.Add(x => x.Id, 12).Add(x => x.Admin, true));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Example"));
        cut.FindComponents<EnumSelect<EFeedbackStatus>>().Count.ShouldBe(0);
    }

    [Theory]
    [InlineData("cs-CZ", "Účetní")]
    [InlineData("en-US", "Accountant")]
    public void AdminDisplayName_UsesAccountantWithoutChangingRole(string culture, string label)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        Services.GetRequiredService<IStringLocalizer<SharedResource>>()["User_RoleAdmin"].Value.ShouldBe(label);
        EUserRole.Admin.ToString().ShouldBe("Admin");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Header_OnlyAuthenticatedUsersSeeReportButton(bool authenticated)
    {
        if (!authenticated) AddAuthorization().SetNotAuthorized();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<AppStateService>();
        Services.AddSingleton(Substitute.For<IClientLogger>());
        Services.AddSingleton<CompanyApiService>();
        Services.AddSingleton<NotificationApiService>();
        ComponentFactories.AddStub<NavMenu>();
        var cut = Render<MainLayout>();
        cut.HasComponent<FeedbackButton>().ShouldBe(authenticated);
        // The raw JWT role must no longer be printed next to the account name.
        cut.FindAll(".appbar-username .mud-chip").Count.ShouldBe(0);
    }

    [Theory]
    [InlineData("Admin", false)]
    [InlineData("SysAdmin", true)]
    public async Task UserEditor_OnlySysAdminCanChooseSysAdminRole(string role, bool visible)
    {
        var authorization = AddAuthorization();
        authorization.SetAuthorized("reporter@example.test");
        authorization.SetRoles(role);
        Services.AddSingleton<UserApiService>();
        Services.AddSingleton<ClientApiService>();
        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddSingleton<GridStateService>();
        var preferences = Substitute.For<UserPreferencesState>();
        preferences.Preferences.Returns(new Fakvio.Contracts.Dto.User.UserPreferencesDto());
        preferences.EnsureLoadedAsync().Returns(Task.FromResult(new Fakvio.Contracts.Dto.User.UserPreferencesDto()));
        Services.AddSingleton(preferences);
        var provider = Render<MudDialogProvider>();
        var cut = Render<Users>();
        await cut.InvokeAsync(() => cut.FindComponent<ResponsiveButton>().Instance.OnClick.InvokeAsync());
        provider.WaitForAssertion(() =>
            provider.FindComponents<MudSelectItem<EUserRole>>()
                .Any(x => x.Instance.Value == EUserRole.SysAdmin).ShouldBe(visible));
        provider.FindComponents<MudSelectItem<EUserRole>>()
            .Any(x => x.Instance.Value == EUserRole.Admin).ShouldBeTrue();
    }
    [Theory]
    [InlineData("Admin", false)]
    [InlineData("SysAdmin", true)]
    public void Navigation_UserManagementAndGlobalFeedbackAreSysAdminOnly(string role, bool visible)
    {
        var authorization = AddAuthorization();
        authorization.SetAuthorized("reporter@example.test");
        authorization.SetRoles(role);
        Services.AddSingleton<AppStateService>();
        Services.AddSingleton<PaymentMatchingApiService>();
        var cut = Render<NavMenu>();
        cut.WaitForAssertion(() =>
        {
            cut.FindComponents<MudNavLink>().Count(x => x.Instance.Href == "/feedback").ShouldBe(1, cut.Markup);
            cut.FindComponents<MudNavLink>().Any(x => x.Instance.Href == "/users").ShouldBe(visible);
            cut.FindComponents<MudNavLink>().Any(x => x.Instance.Href == "/sysadmin/feedback").ShouldBe(visible);
        });
    }

    [Fact]
    public void UserManagementRoute_RequiresSysAdmin()
    {
        var authorization = typeof(Users).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        authorization.Roles.ShouldBe("SysAdmin");
    }
    private async Task<IRenderedComponent<FeedbackDialog>> OpenDialogAsync()
    {
        var provider = Render<MudDialogProvider>();
        await provider.InvokeAsync(async () => _dialog = await Services.GetRequiredService<IDialogService>()
            .ShowAsync<FeedbackDialog>("Report"));
        return provider.FindComponent<FeedbackDialog>();
    }
    private static async Task FillAsync(IRenderedComponent<FeedbackDialog> cut)
    {
        var inputs = cut.FindComponents<MudTextField<string>>();
        await cut.InvokeAsync(() => inputs[0].Instance.ValueChanged.InvokeAsync("Test subject"));
        await cut.InvokeAsync(() => inputs[1].Instance.ValueChanged.InvokeAsync("Detailed description"));
        await cut.InvokeAsync(() => inputs[2].Instance.ValueChanged.InvokeAsync("/invoices?token=private#secret"));
    }

    private sealed class Backend : HttpMessageHandler
    {
        public FeedbackDto Report { get; } = new() { Id = 12, Subject = "Example", Description = "Details", Status = EFeedbackStatus.New };
        public bool Fail { get; set; }
        public int PostCount { get; private set; }
        public CreateFeedbackDto? Created { get; private set; }
        public UpdateFeedbackStatusDto? Updated { get; private set; }
        public string? LastPath { get; private set; }
        public TaskCompletionSource? Pending { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            if (!LastPath.Contains("feedback")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) };
            if (request.Method == HttpMethod.Post)
            {
                PostCount++;
                Created = await request.Content!.ReadFromJsonAsync<CreateFeedbackDto>(ct);
                if (Pending is not null) await Pending.Task.WaitAsync(ct);
            }
            if (request.Method == HttpMethod.Patch)
            {
                Updated = await request.Content!.ReadFromJsonAsync<UpdateFeedbackStatusDto>(ct);
                Report.Status = Updated!.Status;
                Report.PublicResponse = Updated.PublicResponse;
            }
            return Fail ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Report) };
        }
    }
}

public class FeedbackPageContextTests
{
    [Theory]
    [InlineData("https://example.test/invoices?token=secret#private", "/invoices")]
    [InlineData("https://example.test/", "/")]
    [InlineData("invalid", null)]
    public void CurrentUri_ExcludesQueryAndFragment(string uri, string? expected) =>
        FeedbackPageContext.FromUri(uri).ShouldBe(expected);

    [Theory]
    [InlineData("/invoices?token=secret#private", "/invoices")]
    [InlineData("//evil.test", null)]
    [InlineData("https://evil.test", null)]
    [InlineData("/a\\b", null)]
    [InlineData("/a\nb", null)]
    [InlineData("", null)]
    public void EditablePath_RejectsUnsafeValues(string path, string? expected) =>
        FeedbackPageContext.NormalizePath(path).ShouldBe(expected);
}
