using System.Net;
using System.Net.Http.Json;
using Bunit;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Notification;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Notification;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Exercises recovery at the HTTP boundary, using the actual forms and services.</summary>
public class AccountUxRecoveryTests : BunitContext, IAsyncLifetime
{
    private readonly Backend _backend = new();
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public AccountUxRecoveryTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(_backend) { BaseAddress = new Uri("https://test.local") });
        Services.AddSingleton(factory);
        Services.AddSingleton<NotificationApiService>();
        Services.AddSingleton<TwoFactorApiService>();
        Services.AddSingleton<AuthApiService>();
        Services.AddSingleton<CompanyMembershipApiService>();
        Services.AddSingleton<CompanyApiService>();
        AddAuthorization().SetAuthorized("owner@example.test");
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private string L(string key) => Services.GetRequiredService<IStringLocalizer<SharedResource>>()[key].Value;

    [Fact]
    public async Task NotificationLoadFailure_ShowsRetryInsteadOfEmpty_ThenRecovers()
    {
        _backend.Fail = true;
        var cut = Render<Notifications>();
        cut.WaitForAssertion(() => cut.FindComponents<MudAlert>().Single().Instance.Severity.ShouldBe(Severity.Error));
        cut.FindAll(".mud-progress-linear").ShouldBeEmpty();
        _backend.Fail = false;
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().Last().Instance.OnClick.InvokeAsync());
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Payment arrived"));
    }

    [Fact]
    public async Task NotificationFailedRead_KeepsUnreadItemAndList()
    {
        var cut = Render<Notifications>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Payment arrived"));
        _backend.Fail = true;
        await cut.InvokeAsync(() => cut.FindComponent<MudListItem<NotificationDto>>().Instance.OnClick.InvokeAsync());
        cut.FindAll(".notification-unread").Count.ShouldBe(1);
        cut.FindComponents<MudAlert>().Single().Instance.Severity.ShouldBe(Severity.Error);
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().First().Instance.OnClick.InvokeAsync());
        cut.FindAll(".notification-unread").Count.ShouldBe(1);
    }

    [Fact]
    public async Task NotificationBell_FailedDismissalPreservesBadge_AndHasAccessibleName()
    {
        var popovers = Render<MudPopoverProvider>();
        var cut = Render<NotificationBell>();
        cut.Find("button").GetAttribute("aria-label").ShouldBe(L("Notification_Title"));
        cut.WaitForAssertion(() => cut.FindComponent<MudBadge>().Instance.Content.ShouldBe(1));
        await cut.InvokeAsync(() => cut.FindComponent<MudIconButton>().Instance.OnClick.InvokeAsync());
        _backend.Fail = true;
        await popovers.InvokeAsync(() => popovers.FindComponents<MudButton>().First().Instance.OnClick.InvokeAsync());
        cut.FindComponent<MudBadge>().Instance.Content.ShouldBe(1);
        popovers.FindComponents<MudAlert>().Single().Instance.Severity.ShouldBe(Severity.Error);
    }

    [Fact]
    public async Task TwoFactorStatusFailure_HasRetry_AndRecovers()
    {
        _backend.Fail = true;
        var cut = Render<TwoFactorSettings>();
        cut.WaitForAssertion(() => cut.FindComponents<MudAlert>().Single().Instance.Severity.ShouldBe(Severity.Error));
        _backend.Fail = false;
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().First().Instance.OnClick.InvokeAsync());
        cut.WaitForAssertion(() => cut.FindComponents<MudButton>().Any(b => b.Markup.Contains(L("TwoFactor_SetupTotp"))).ShouldBeTrue());
    }

    [Fact]
    public async Task TotpManualKey_CopyUsesClipboard()
    {
        var provider = Render<MudDialogProvider>();
        var cut = Render<TwoFactorSettings>();
        cut.WaitForAssertion(() => cut.FindComponents<MudButton>().Any(b => b.Markup.Contains(L("TwoFactor_SetupTotp"))).ShouldBeTrue());
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().First(b => b.Markup.Contains(L("TwoFactor_SetupTotp"))).Instance.OnClick.InvokeAsync());
        provider.WaitForAssertion(() => provider.FindComponents<MudTextField<string>>().Any(f => f.Instance.ReadOnly).ShouldBeTrue());
        var key = provider.FindComponents<MudTextField<string>>().Single(f => f.Instance.ReadOnly);
        await provider.InvokeAsync(() => key.Instance.OnAdornmentClick.InvokeAsync());
        JSInterop.VerifyInvoke("navigator.clipboard.writeText").Arguments[0].ShouldBe("MANUALKEY");
    }

    [Fact]
    public async Task RegistrationEmailFailure_IsPersistentlyVisible()
    {
        AddAuthorization().SetNotAuthorized();
        var cut = Render<Register>();
        var inputs = cut.FindComponents<MudTextField<string>>();
        foreach (var (index, value) in new[] { (0, "owner@example.test"), (1, "First"), (2, "Last"), (3, "Acme"), (4, "12345678") })
            await cut.InvokeAsync(() => inputs[index].Instance.ValueChanged.InvokeAsync(value));
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().Last().Instance.OnClick.InvokeAsync());
        cut.WaitForAssertion(() => cut.FindComponents<MudAlert>().Single().Instance.Severity.ShouldBe(Severity.Warning));
        cut.Find(".mud-alert").TextContent.ShouldContain(L("Register_SuccessNoEmail"));
        cut.FindAll("input").ShouldBeEmpty();
    }

    [Fact]
    public async Task InvitationMissingCompany_ValidatesFieldWithoutPosting()
    {
        var provider = Render<MudDialogProvider>();
        await provider.InvokeAsync(() => Services.GetRequiredService<IDialogService>().ShowAsync<InviteCompanyMemberDialog>("Invite"));
        var cut = provider.FindComponent<InviteCompanyMemberDialog>();
        await cut.InvokeAsync(() => cut.FindComponent<MudTextField<string>>().Instance.ValueChanged.InvokeAsync("owner@example.test"));
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().Last().Instance.OnClick.InvokeAsync());
        cut.FindComponent<MudSelect<long?>>().Instance.HasErrors.ShouldBeTrue();
        _backend.InvitationPosts.ShouldBe(0);
    }

    [Fact]
    public async Task InvitationEmptyCompany_ShowsPlaceholderInsteadOfNumericZero()
    {
        var provider = Render<MudDialogProvider>();
        await provider.InvokeAsync(() => Services.GetRequiredService<IDialogService>().ShowAsync<InviteCompanyMemberDialog>("Invite"));
        var cut = provider.FindComponent<InviteCompanyMemberDialog>();
        var selector = cut.FindComponent<MudSelect<long?>>();
        selector.Instance.Value.ShouldBeNull();
        selector.Find("input").GetAttribute("value").ShouldBeNullOrEmpty();
        selector.Find("input").GetAttribute("placeholder").ShouldBe(L("CompanyMembership_CompanyRequired"));
        await cut.InvokeAsync(() => selector.Instance.ValueChanged.InvokeAsync(1L));
        selector.Instance.Value.ShouldBe(1L);
        await cut.InvokeAsync(() => cut.FindComponent<MudTextField<string>>().Instance.ValueChanged.InvokeAsync("owner@example.test"));
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().Last().Instance.OnClick.InvokeAsync());
        _backend.InvitationPosts.ShouldBe(1);
        _backend.InvitationCompanyId.ShouldBe(1);
    }

    private sealed class Backend : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public int InvitationPosts { get; private set; }
        public long InvitationCompanyId { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/my-companies/invitations")
            {
                InvitationPosts++;
                InvitationCompanyId = (await request.Content!.ReadFromJsonAsync<Fakvio.Contracts.Dto.CompanyMembership.InviteCompanyMemberDto>(ct))!.CompanyId;
            }
            if (Fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var notification = new NotificationDto { Id = 1, Title = "Payment arrived", Message = "Invoice paid", RelatedEntityType = "Invoice", RelatedEntityId = 2 };
            object body = path switch
            {
                "/api/notification/unread-count" => 1,
                "/api/notification/dashboard" => new NotificationDashboardDto { UnreadCount = 1, RecentNotifications = [notification] },
                "/api/notification" => new PagedResult<NotificationDto>([notification], 1, 1, 20),
                "/api/twofactor/status" => new Fakvio.UI.Shared.Models.TwoFactorStatusDto(),
                "/api/twofactor/totp/setup" => new Fakvio.UI.Shared.Models.TotpSetupResponse { ManualEntryKey = "MANUALKEY" },
                "/api/auth/register" => new Fakvio.UI.Shared.Models.RegisterResponse { EmailSent = false },
                "/api/company" => new List<ClientDto> { new() { Id = 1, CompanyName = "Acme" } },
                _ => new { }
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}
