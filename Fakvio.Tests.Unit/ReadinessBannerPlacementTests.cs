using System.Net;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Components.Shared;
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

/// <summary>
/// Placement tests for <see cref="ReadinessBanner"/> (issue #215).
///
/// <see cref="ReadinessBannerTests"/> proves the banner behaves correctly when rendered; it says
/// nothing about whether any page actually shows it. Delete the one-line <c>&lt;ReadinessBanner /&gt;</c>
/// from the dashboard or the invoice detail and every behaviour test stays green — these tests
/// are the ones that go red, which is the acceptance criterion "shown on dashboard and invoice detail".
///
/// The banner itself is stubbed out here on purpose: this file is about placement, so it must not
/// need the readiness HTTP plumbing (and must not fail when that plumbing changes).
/// </summary>
public class ReadinessBannerPlacementTests : BunitContext, IAsyncLifetime
{
    private const long InvoiceId = 7;
    private const long IssuerId = 42;

    private readonly PageBackendHandler _backend = new();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public ReadinessBannerPlacementTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(Substitute.For<ISnackbar>());

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(
            ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);

        var httpClient = new HttpClient(_backend) { BaseAddress = new Uri("https://test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);
        Services.AddSingleton(factory);

        // Every page-level API service the two hosts inject. The stub backend answers 404 to all
        // of them except the invoice itself — the pages treat that as "nothing to show" and still
        // render, which is exactly the minimal environment a placement test needs.
        AddApiService(sp => new DashboardApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<DashboardApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new TaxApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<TaxApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new ReminderApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ReminderApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new PaymentMatchingApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<PaymentMatchingApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new AlertApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<AlertApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new FakvioService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<FakvioService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new ClientApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ClientApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new CurrencyApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<CurrencyApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new InvoiceTemplateApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<InvoiceTemplateApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new ContentTemplateApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ContentTemplateApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));
        AddApiService(sp => new NumberSequenceApiService(sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<NumberSequenceApiService>.Instance, sp.GetRequiredService<AuthenticationStateProvider>()));

        // Placement, not behaviour — the banner and the heavy editors are stubs.
        ComponentFactories.AddStub<ReadinessBanner>();
        ComponentFactories.AddStub<InvoiceItemEditor>();
        ComponentFactories.AddStub<InvoicePaymentsPanel>();
        ComponentFactories.AddStub<FileAttachmentManager>();

        AddAuthorization().SetAuthorized("ucetni@example.cz");
    }

    private void AddApiService<T>(Func<IServiceProvider, T> factory) where T : class
        => Services.AddSingleton(factory);

    [Fact]
    public void Dashboard_HostsTheReadinessBanner_SoANewTenantLearnsWhatIsMissingOnFirstLogin()
    {
        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
            cut.HasComponent<Stub<ReadinessBanner>>().ShouldBeTrue(
                "the dashboard must host the readiness banner"));
    }

    [Fact]
    public void DraftInvoice_HostsTheReadinessBanner_SoTheRefusalIsSeenBeforeTheIssueClick()
    {
        _backend.Invoice = NewInvoice(EInvoiceStatus.Draft);

        var cut = RenderInvoiceDetail();

        var banner = cut.FindComponents<Stub<ReadinessBanner>>().ShouldHaveSingleItem();

        // Scoped to this document's issuer — a second, unrelated company with incomplete
        // settings must not raise a false alarm on this invoice.
        banner.Instance.Parameters[nameof(ReadinessBanner.IssuerId)].ShouldBe(IssuerId);
    }

    [Fact]
    public void IssuedInvoice_HasNoReadinessBanner_BecauseNothingIsAboutToBeRefused()
    {
        _backend.Invoice = NewInvoice(EInvoiceStatus.Completed);

        var cut = RenderInvoiceDetail();

        cut.FindComponents<Stub<ReadinessBanner>>().ShouldBeEmpty();
    }

    [Fact]
    public void DraftWithoutIssuer_PassesNullIssuerId_SoTheWholeTenantIsChecked()
    {
        // A draft created before an issuer was picked carries IssuerId = 0. Forwarded as-is that
        // would ask the API about a company id that cannot exist; the page maps it to null, which
        // the banner reads as "check every issuer of the tenant".
        _backend.Invoice = NewInvoice(EInvoiceStatus.Draft, issuerId: 0);

        var cut = RenderInvoiceDetail();

        var banner = cut.FindComponents<Stub<ReadinessBanner>>().ShouldHaveSingleItem();
        banner.Instance.Parameters[nameof(ReadinessBanner.IssuerId)].ShouldBeNull();
    }

    [Fact]
    public void DraftCreditNote_HostsTheReadinessBanner_SoBothDocumentTypesBehaveTheSame()
    {
        // CLAUDE.md requires invoices and credit notes to share one UI. The placement is gated on
        // status alone, and this test is what turns a later DocumentType condition into a failure
        // instead of a silently one-sided feature.
        _backend.Invoice = NewInvoice(EInvoiceStatus.Draft, documentType: EDocumentType.CreditNote);

        var cut = RenderInvoiceDetail();

        cut.FindComponents<Stub<ReadinessBanner>>().ShouldHaveSingleItem();
    }

    /// <summary>Renders /invoices/{id} and waits until the invoice load has finished.</summary>
    private IRenderedComponent<InvoiceDetail> RenderInvoiceDetail()
    {
        var cut = Render<InvoiceDetail>(p => p.Add(c => c.Id, InvoiceId));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain(_backend.Invoice!.IssuerName));
        return cut;
    }

    private static InvoiceDto NewInvoice(
        EInvoiceStatus status,
        long issuerId = IssuerId,
        EDocumentType documentType = EDocumentType.Invoice) => new()
    {
        Id = InvoiceId,
        DocumentNumber = "2026-0001",
        DocumentType = documentType,
        Status = status,
        IssuerId = issuerId,
        IssuerName = "Testovací s.r.o."
    };

    /// <summary>
    /// Stub backend: serves the one invoice the detail page loads and answers 404 to everything
    /// else. Both pages wrap their optional loads in try/catch, so 404 degrades to an empty page
    /// rather than an exception.
    /// </summary>
    private sealed class PageBackendHandler : HttpMessageHandler
    {
        public InvoiceDto? Invoice { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (Invoice != null && path.EndsWith($"/api/invoice/{InvoiceId}", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Invoice)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
