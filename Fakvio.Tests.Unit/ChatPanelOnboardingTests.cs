using System.Net;
using System.Net.Http.Json;
using Bunit;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Chat;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the proactive onboarding welcome in <see cref="ChatPanel"/>
/// (issue #214).
///
/// What they pin down: an unfinished tenant sees the assistant say something the moment the
/// panel opens (instead of the "type a message" placeholder), a finished one does not, and the
/// greeting is seeded exactly once no matter how often the parent re-renders. The rest of the
/// conversation is a live model and is deliberately not exercised here — the greeting is the
/// only part that is composed text.
/// </summary>
public class ChatPanelOnboardingTests : BunitContext, IAsyncLifetime
{
    private readonly ChatBackendHandler _backend = new();

    // MudBlazor's PopoverService only supports async disposal; xunit disposes test classes
    // synchronously, so route disposal through IAsyncLifetime (same as ReadinessBannerTests).
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public ChatPanelOnboardingTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Localizer echoes the key, so assertions can target keys instead of translations.
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(
            ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);

        var httpClient = new HttpClient(_backend) { BaseAddress = new Uri("https://test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);
        Services.AddSingleton(factory);

        Services.AddSingleton(sp => new ChatApiService(
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ChatApiService>.Instance,
            sp.GetRequiredService<AuthenticationStateProvider>()));
        Services.AddSingleton(Substitute.For<IUiErrorHandler>());

        AddAuthorization().SetAuthorized("ucetni@example.cz");
    }

    [Fact]
    public void UnfinishedTenant_SeesTheAssistantGreetFirst_NotAnEmptyPanel()
    {
        var cut = Render<ChatPanel>(p => p.Add(
            c => c.OnboardingWelcome, "Chybí bankovní účet."));

        cut.WaitForAssertion(() =>
        {
            // Rendered as a normal assistant bubble — the greeting must look like the
            // assistant talking, not like a banner bolted onto the chat.
            cut.FindAll(".chat-bubble-assistant").Count.ShouldBe(1);
            cut.Markup.ShouldContain("Chybí bankovní účet.");
        });
    }

    [Fact]
    public void ReadyTenant_SeesTheUsualEmptyPanel_WithNoProactiveMessage()
    {
        var cut = Render<ChatPanel>();

        // No bubble at all: what stays is the standard empty-panel placeholder. (Its key,
        // Chat_Placeholder, is also the input's placeholder, so it is no evidence on its own —
        // the absence of an assistant bubble is.)
        cut.WaitForAssertion(() =>
        {
            // Waits for the provider list to arrive first — "no bubble yet" would otherwise
            // be true before the panel has even finished initializing.
            cut.Markup.ShouldNotContain("Chat_NoProviders_Title");
            cut.FindAll(".chat-bubble-assistant").ShouldBeEmpty();
        });
    }

    [Fact]
    public void Welcome_IsSeededOnce_EvenWhenTheParentRendersAgain()
    {
        // MainLayout re-renders on every drawer toggle and company-list change; a greeting
        // that piles up one bubble per render would be worse than no greeting at all.
        var cut = Render<ChatPanel>(p => p.Add(
            c => c.OnboardingWelcome, "Chybí bankovní účet."));

        cut.WaitForAssertion(() => cut.FindAll(".chat-bubble-assistant").Count.ShouldBe(1));

        cut.Render(p => p.Add(c => c.OnboardingWelcome, "Chybí bankovní účet."));

        cut.FindAll(".chat-bubble-assistant").Count.ShouldBe(1);
    }

    /// <summary>
    /// Answers the two calls <see cref="ChatPanel"/> makes while initializing. The provider
    /// list must not be empty — an empty one switches the panel to the "no AI provider
    /// configured" screen, which would hide the greeting for an unrelated reason.
    /// </summary>
    private sealed class ChatBackendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            HttpContent content = path switch
            {
                "/api/chat/providers" => JsonContent.Create(new List<string> { "Claude" }),
                "/api/chat/conversations" => JsonContent.Create(new List<ChatConversationListStub>()),
                _ => JsonContent.Create(new { })
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Empty stand-in for the conversation list payload — the tests need no history.</summary>
    private sealed class ChatConversationListStub;
}
