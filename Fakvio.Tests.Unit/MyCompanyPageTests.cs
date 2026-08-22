using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the /my-company page.
///
/// Why a whole-page render test: a Razor component parameter that does not exist on the
/// target component still COMPILES — the Razor generator emits it as a plain string name
/// and Blazor only rejects it at render time with an InvalidOperationException. A green
/// build therefore proves nothing about the page actually opening. Issue #145 shipped
/// exactly such a bug (Class="mt-3" on EnumSelect, which has no Class parameter), and it
/// slipped through 1900+ green tests because MyCompany.razor had no render test at all.
///
/// These two tests render the page, switch it to edit mode, and thereby exercise both
/// branches of the Admin-only AuthorizeView around the advance-tax-receipt-mode select.
/// </summary>
public class MyCompanyPageTests : BunitContext, IAsyncLifetime
{
    // MudBlazor's PopoverService only supports async disposal; xunit v2 disposes test
    // classes synchronously, so route disposal through IAsyncLifetime (same as FakvioGridTests).
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    /// <summary>The mode the fake API reports for the issuer — deliberately NOT the CLR default.</summary>
    private const EAdvanceTaxReceiptMode ServerMode = EAdvanceTaxReceiptMode.OnAnyPayment;

    public MyCompanyPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();

        // Localizer returns the resource key itself — enough to locate buttons by text.
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(
            ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);

        // One fake HttpClient serves every API service the page and its children inject.
        var httpClient = new HttpClient(new StubHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        Services.AddSingleton(factory);

        // API services injected by MyCompany.razor and by the child components it renders
        // (FolderPicker, InvoiceMailboxCard, RecognizedCounterpartyEditor).
        Services.AddSingleton<ClientApiService>();
        Services.AddSingleton<CompanySettingsApiService>();
        Services.AddSingleton<CloudStorageApiService>();
        Services.AddSingleton<InvoiceMailboxApiService>();
        Services.AddSingleton<RecognizedCounterpartyApiService>();
    }

    /// <summary>
    /// Minimal fake API. Only the two calls MyCompany.LoadCompany() needs succeed; every
    /// other endpoint answers 404, which the page's auxiliary loaders already swallow.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            var (status, json) = path switch
            {
                "/api/client/issuer/advance-tax-receipt-mode"
                    => (HttpStatusCode.OK, ((int)ServerMode).ToString()),
                "/api/client/issuer"
                    => (HttpStatusCode.OK, JsonSerializer.Serialize(
                        new ClientDto
                        {
                            Id = 1,
                            CompanyName = "Test Company s.r.o.",
                            RegistrationNumber = "12345678",
                            IsIssuer = true,
                            IsActive = true
                        },
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))),
                _ => (HttpStatusCode.NotFound, "{}")
            };

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Renders /my-company for a user in the given roles and clicks "Edit".
    /// MudSelect renders into a popover, so the page needs a MudPopoverProvider sibling.
    /// </summary>
    private IRenderedComponent<MyCompany> RenderInEditMode(params string[] roles)
    {
        var authContext = AddAuthorization();
        authContext.SetAuthorized("test-user");
        authContext.SetRoles(roles);

        var root = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();

            builder.OpenComponent<MyCompany>(1);
            builder.CloseComponent();
        });

        var page = root.FindComponent<MyCompany>();

        // The Edit button only appears once LoadCompany() has resolved (_loading == false).
        page.WaitForAssertion(() =>
            page.FindAll("button").Any(b => b.TextContent.Contains("Btn_Edit")).ShouldBeTrue());

        page.FindAll("button").First(b => b.TextContent.Contains("Btn_Edit")).Click();

        return page;
    }

    [Fact]
    public void EditMode_AsAdmin_RendersAdvanceTaxReceiptModeSelect()
    {
        var page = RenderInEditMode("Admin");

        var select = page.FindComponent<EnumSelect<EAdvanceTaxReceiptMode>>();
        select.Instance.Value.ShouldBe(ServerMode);
        select.Instance.Disabled.ShouldBeFalse();
    }

    [Fact]
    public void EditMode_AsNonAdmin_RendersAdvanceTaxReceiptModeSelectDisabled()
    {
        var page = RenderInEditMode("User");

        var select = page.FindComponent<EnumSelect<EAdvanceTaxReceiptMode>>();
        select.Instance.Value.ShouldBe(ServerMode);
        select.Instance.Disabled.ShouldBeTrue();
    }
}
