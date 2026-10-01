using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class CompanyMembershipUiTests : BunitContext, IAsyncLifetime
{
    private readonly Backend _backend = new();
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public CompanyMembershipUiTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        JSInterop.Mode = JSRuntimeMode.Loose;
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(_backend) { BaseAddress = new Uri("https://test.invalid") });
        Services.AddSingleton(factory);
        Services.AddSingleton<CompanyMembershipApiService>();
        AddAuthorization().SetAuthorized("member@example.test");
    }

    [Theory]
    [InlineData("cs-CZ", "Přidat společnost")]
    [InlineData("en-US", "Add company")]
    public void CompanyLabelsResolve(string culture, string expected)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        Services.GetRequiredService<IStringLocalizer<SharedResource>>()["CompanyMembership_Add"].Value.ShouldBe(expected);
    }

    [Fact]
    public async Task AddCompany_AmbiguousFailureRetryReusesOperationIdAndInput()
    {
        var provider = Render<MudDialogProvider>();
        await provider.InvokeAsync(() => Services.GetRequiredService<IDialogService>().ShowAsync<AddCompanyDialog>("Add"));
        var cut = provider.FindComponent<AddCompanyDialog>();
        var fields = cut.FindComponent<CompanyIdentityFields>();
        await cut.InvokeAsync(() => fields.Instance.CompanyNameChanged.InvokeAsync("Existing identity company"));
        await cut.InvokeAsync(() => fields.Instance.RegistrationNumberChanged.InvokeAsync("12345678"));
        _backend.Fail = true;
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().Last().Instance.OnClick.InvokeAsync());
        _backend.Operations.Count.ShouldBe(1);
        fields.Instance.CompanyName.ShouldBe("Existing identity company");
        _backend.Fail = false;
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().Last().Instance.OnClick.InvokeAsync());
        _backend.Operations.Count.ShouldBe(2);
        _backend.Operations.Distinct().Count().ShouldBe(1);
        _backend.Operations[0].ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task AddCompany_FailedProvisioningUsesRetryEndpoint()
    {
        _backend.Provisioned = false;
        var provider = Render<MudDialogProvider>();
        await provider.InvokeAsync(() => Services.GetRequiredService<IDialogService>().ShowAsync<AddCompanyDialog>("Add"));
        var cut = provider.FindComponent<AddCompanyDialog>();
        var fields = cut.FindComponent<CompanyIdentityFields>();
        await cut.InvokeAsync(() => fields.Instance.CompanyNameChanged.InvokeAsync("Company"));
        await cut.InvokeAsync(() => fields.Instance.RegistrationNumberChanged.InvokeAsync("12345678"));
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().Last().Instance.OnClick.InvokeAsync());
        cut.FindAll(".mud-alert").ShouldNotBeEmpty();
        _backend.Provisioned = true;
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().First().Instance.OnClick.InvokeAsync());
        _backend.Paths.ShouldContain("/api/my-companies/7/retry-provisioning");
        _backend.Operations.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Acceptance_UsesLoggedInIdentityAndClearsFragmentAfterSuccess()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/company-invitations/accept#secret-token");
        var cut = Render<AcceptCompanyInvitation>();
        await cut.InvokeAsync(() => cut.FindComponent<MudButton>().Instance.OnClick.InvokeAsync());
        _backend.AcceptedToken.ShouldBe("secret-token");
        Services.GetRequiredService<NavigationManager>().Uri.ShouldNotContain("secret-token");
        cut.FindComponents<MudTextField<string>>().Count.ShouldBe(0);
    }

    [Fact]
    public void AnonymousAcceptance_DoesNotSubmitOrExposeToken()
    {
        AddAuthorization().SetNotAuthorized();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/company-invitations/accept#secret-token");
        var cut = Render<AcceptCompanyInvitation>();
        cut.FindComponents<MudTextField<string>>().Count.ShouldBe(0);
        _backend.Paths.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvitationLoginReturn_UsesOnlyFixedLocalFragmentRoute()
    {
        JSInterop.Setup<string?>("sessionStorage.getItem", "fakvio.companyInvitation").SetResult("https://evil.test/?secret=1");
        var navigation = Services.GetRequiredService<NavigationManager>();
        (await CompanyInvitationReturnStorage.TryResumeAsync(JSInterop.JSRuntime, navigation)).ShouldBeTrue();
        new Uri(navigation.Uri).AbsolutePath.ShouldBe("/company-invitations/accept");
        new Uri(navigation.Uri).Query.ShouldBeEmpty();
        JSInterop.Invocations.Any(x => x.Identifier == "sessionStorage.removeItem").ShouldBeTrue();
    }

    [Fact]
    public async Task AnonymousInvitation_LoginStoresTokenOnlyInTabStorage()
    {
        AddAuthorization().SetNotAuthorized();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/company-invitations/accept#secret-token");
        var cut = Render<AcceptCompanyInvitation>();
        await cut.InvokeAsync(() => cut.FindComponent<MudButton>().Instance.OnClick.InvokeAsync());
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/login");
        JSInterop.Invocations.Any(x => x.Identifier == "sessionStorage.setItem"
            && x.Arguments.Any(value => Equals(value, "secret-token"))).ShouldBeTrue();
    }
    private sealed class Backend : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public bool Provisioned { get; set; } = true;
        public List<Guid> Operations { get; } = [];
        public List<string> Paths { get; } = [];
        public string? AcceptedToken { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/api/my-companies" && request.Method == HttpMethod.Post)
                Operations.Add((await request.Content!.ReadFromJsonAsync<CreateMyCompanyDto>(ct))!.OperationId);
            if (path.EndsWith("/invitations/accept"))
                AcceptedToken = (await request.Content!.ReadFromJsonAsync<AcceptCompanyInvitationDto>(ct))!.Token;
            return Fail ? new(HttpStatusCode.ServiceUnavailable) :
                new(HttpStatusCode.OK) { Content = JsonContent.Create(new CompanyMembershipDto { CompanyId = 7, CompanyName = "Company", IsProvisioned = Provisioned }) };
        }
    }
}
