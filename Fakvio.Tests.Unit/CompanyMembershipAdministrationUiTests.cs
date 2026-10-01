using System.Net;
using System.Net.Http.Json;
using Bunit;
using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

public sealed class CompanyMembershipAdministrationUiTests : BunitContext, IAsyncLifetime
{
    private readonly Backend _backend = new();
    public CompanyMembershipAdministrationUiTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
        AddAuthorization().SetAuthorized("admin@example.test");
        AddAuthorization().SetRoles("SysAdmin");
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(_backend) { BaseAddress = new("https://test.invalid") });
        Services.AddSingleton(factory); Services.AddSingleton<CompanyMembershipApiService>();
    }
    public Task InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private async Task<IRenderedComponent<UserCompanyMembershipDialog>> Open()
    {
        var provider = Render<MudDialogProvider>();
        await provider.InvokeAsync(() => Services.GetRequiredService<IDialogService>().ShowAsync<UserCompanyMembershipDialog>("Memberships",
            new DialogParameters<UserCompanyMembershipDialog> { { c => c.UserId, 7L } }));
        return provider.FindComponent<UserCompanyMembershipDialog>();
    }

    [Fact]
    public async Task ShowsAllCompanyNamesAndOnlyTenantRoles()
    {
        var cut = await Open();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Second company"));
        cut.Markup.ShouldContain("First company");
        cut.FindComponents<MudSelectItem<EUserRole>>().Select(x => x.Instance.Value)
            .ShouldBe(new[] { EUserRole.User, EUserRole.Admin, EUserRole.User, EUserRole.Admin });
        _backend.Paths.ShouldContain("/api/user/7/memberships");
    }

    [Fact]
    public async Task SaveUsesOneTargetAndBlocksRepeatedSubmissionWhilePending()
    {
        var cut = await Open();
        var selects = cut.FindComponents<MudSelect<EUserRole>>();
        await cut.InvokeAsync(() => selects[1].Instance.ValueChanged.InvokeAsync(EUserRole.User));
        _backend.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var buttons = cut.FindComponents<MudButton>();
        var pending = cut.InvokeAsync(() => buttons[1].Instance.OnClick.InvokeAsync());
        cut.WaitForAssertion(() => _backend.Writes.Count.ShouldBe(1));
        await cut.InvokeAsync(() => buttons[1].Instance.OnClick.InvokeAsync());
        _backend.Writes.Count.ShouldBe(1);
        _backend.Writes[0].Path.ShouldBe("/api/user/7/memberships/21");
        _backend.Writes[0].Input.Role.ShouldBe(EUserRole.User);
        _backend.Writes[0].Input.IsActive.ShouldBe(true);
        _backend.Gate.SetResult(); await pending;
    }

    [Fact]
    public async Task SavingOneCompanyPreservesOtherCompanyDraft()
    {
        var cut = await Open();
        await cut.InvokeAsync(() => cut.FindComponents<MudSelect<EUserRole>>()[0].Instance.ValueChanged.InvokeAsync(EUserRole.Admin));
        await cut.InvokeAsync(() => cut.FindComponents<MudSelect<EUserRole>>()[1].Instance.ValueChanged.InvokeAsync(EUserRole.User));
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>()[1].Instance.OnClick.InvokeAsync());
        cut.FindComponents<MudSelect<EUserRole>>()[0].Instance.Value.ShouldBe(EUserRole.Admin);
    }

    [Fact]
    public async Task LoadFailureShowsErrorAndRetryInsteadOfEmptySuccess()
    {
        _backend.Fail = true;
        var cut = await Open();
        cut.WaitForAssertion(() => cut.FindComponents<MudAlert>().Count.ShouldBe(1));
        _backend.Fail = false;
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>().First().Instance.OnClick.InvokeAsync());
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Second company"));
        cut.FindComponents<MudAlert>().ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveFailureKeepsInputVisibleForRetry()
    {
        var cut = await Open();
        await cut.InvokeAsync(() => cut.FindComponents<MudSelect<EUserRole>>()[1].Instance.ValueChanged.InvokeAsync(EUserRole.User));
        _backend.Fail = true;
        await cut.InvokeAsync(() => cut.FindComponents<MudButton>()[1].Instance.OnClick.InvokeAsync());
        cut.FindComponents<MudAlert>().Count.ShouldBe(1);
        cut.FindComponents<MudSelect<EUserRole>>()[1].Instance.Value.ShouldBe(EUserRole.User);
    }

    private sealed class Backend : HttpMessageHandler
    {
        public bool Fail;
        public TaskCompletionSource? Gate;
        public List<string> Paths { get; } = [];
        public List<(string Path, UpdateCompanyMembershipDto Input)> Writes { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            if (request.Method == HttpMethod.Put)
            {
                Writes.Add((path, (await request.Content!.ReadFromJsonAsync<UpdateCompanyMembershipDto>(ct))!));
                if (Gate is not null) await Gate.Task.WaitAsync(ct);
            }
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            var rows = new[] {
                new ManagedCompanyMembershipDto { CompanyId = 20, CompanyName = "First company", IsActive = true, IsCompanyActive = true, Role = EUserRole.User },
                new ManagedCompanyMembershipDto { CompanyId = 21, CompanyName = "Second company", IsActive = true, IsCompanyActive = true, Role = EUserRole.Admin }
            };
            return new(HttpStatusCode.OK) { Content = request.Method == HttpMethod.Get ? JsonContent.Create(rows) : JsonContent.Create(rows[1]) };
        }
    }
}
