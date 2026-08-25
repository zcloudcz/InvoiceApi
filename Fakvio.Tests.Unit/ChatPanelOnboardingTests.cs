using System.Net;
using System.Net.Http.Json;
using Bunit;
using Fakvio.Contracts.Dto.Chat;
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
    /// <summary>Stand-in for whatever ChatOnboarding.BuildWelcome composed — the panel only forwards it.</summary>
    private const string WelcomeText = "Chybí bankovní účet.";

    /// <summary>The one message of the saved conversation the history test opens.</summary>
    private const string SavedUserMessage = "Kolik mám nezaplacených faktur?";

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
            c => c.OnboardingWelcome, WelcomeText));

        cut.WaitForAssertion(() =>
        {
            // Rendered as a normal assistant bubble — the greeting must look like the
            // assistant talking, not like a banner bolted onto the chat.
            cut.FindAll(".chat-bubble-assistant").Count.ShouldBe(1);
            cut.Markup.ShouldContain(WelcomeText);
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
            c => c.OnboardingWelcome, WelcomeText));

        cut.WaitForAssertion(() => cut.FindAll(".chat-bubble-assistant").Count.ShouldBe(1));

        cut.Render(p => p.Add(c => c.OnboardingWelcome, WelcomeText));

        cut.FindAll(".chat-bubble-assistant").Count.ShouldBe(1);
    }

    [Fact]
    public void Welcome_ArrivesOnALaterRender_LikeInProduction()
    {
        // The path production always takes: MainLayout only sets the parameter once
        // GET /api/readiness has answered, so the panel is constructed without it first.
        // That is the whole reason the seeding lives in OnParametersSet rather than
        // OnInitializedAsync — a first render carrying the welcome never happens for real.
        var cut = Render<ChatPanel>();

        // Wait out initialization first, otherwise "no bubble yet" is trivially true.
        cut.WaitForAssertion(() => cut.Markup.ShouldNotContain("Chat_NoProviders_Title"));
        cut.FindAll(".chat-bubble-assistant").ShouldBeEmpty();

        cut.Render(p => p.Add(c => c.OnboardingWelcome, WelcomeText));

        cut.FindAll(".chat-bubble-assistant").Count.ShouldBe(1);
        cut.Markup.ShouldContain(WelcomeText);
    }

    [Fact]
    public async Task Welcome_IsNotSeededAgain_AfterTheUserStartsANewConversation()
    {
        var cut = Render<ChatPanel>(p => p.Add(
            c => c.OnboardingWelcome, WelcomeText));

        cut.WaitForAssertion(() => cut.FindAll(".chat-bubble-assistant").Count.ShouldBe(1));

        // "New conversation" empties _messages (ChatPanel.StartNewConversation), which is the
        // only other thing the seeding guard could read to tell "already greeted" apart from
        // "fresh panel". Without the _welcomeShown flag the greeting comes back here — inside
        // a conversation the user has just started on purpose.
        await cut.InvokeAsync(() => cut.Find("button[title='Chat_NewConversation']").Click());

        cut.FindAll(".chat-bubble-assistant").ShouldBeEmpty();

        // MainLayout re-renders on every drawer toggle and company-list change, and the
        // parameter is still set — that re-render is what would re-seed.
        cut.Render(p => p.Add(c => c.OnboardingWelcome, WelcomeText));

        cut.FindAll(".chat-bubble-assistant").ShouldBeEmpty();
    }

    [Fact]
    public async Task Welcome_IsNotSeededIntoAConversationTheUserOpened()
    {
        // The race production can actually lose: the panel is already open, the user picks a
        // saved conversation from the history, and only then does GET /api/readiness answer.
        // Seeding the greeting at that point would drop a "your setup is unfinished" bubble
        // into the middle of somebody else's dialogue, on top of the history they just loaded.
        var cut = Render<ChatPanel>();

        await OpenSavedConversationAsync(cut);

        cut.Render(p => p.Add(c => c.OnboardingWelcome, WelcomeText));

        cut.Markup.ShouldContain(SavedUserMessage);
        cut.Markup.ShouldNotContain(WelcomeText);
        cut.FindAll(".chat-bubble-assistant").ShouldBeEmpty();
    }

    /// <summary>
    /// Walks the panel the way the user does: history toggle → click the one saved
    /// conversation → its messages are on screen. Clicks go through InvokeAsync because a
    /// Find-then-Click outside the renderer's dispatcher is the shape behind flake #349.
    /// </summary>
    private static async Task OpenSavedConversationAsync(IRenderedComponent<ChatPanel> cut)
    {
        await cut.InvokeAsync(() => cut.Find("button[title='History']").Click());
        await cut.InvokeAsync(() => cut.Find(".mud-list-item").Click());

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(SavedUserMessage));
    }

    /// <summary>
    /// Answers the calls <see cref="ChatPanel"/> makes while initializing, plus the history
    /// endpoints the "already has messages" test walks through. The provider list must not be
    /// empty — an empty one switches the panel to the "no AI provider configured" screen,
    /// which would hide the greeting for an unrelated reason.
    /// </summary>
    private sealed class ChatBackendHandler : HttpMessageHandler
    {
        private const long SavedConversationId = 7;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            HttpContent content = path switch
            {
                "/api/chat/providers" => JsonContent.Create(new List<string> { "Claude" }),
                "/api/chat/conversations" => JsonContent.Create(new List<ChatConversationListDto>
                {
                    new() { Id = SavedConversationId, Title = "Faktury", MessageCount = 1 }
                }),
                // Detail of the one saved conversation, whatever id the panel asks for.
                _ when path.StartsWith("/api/chat/conversations/") => JsonContent.Create(
                    new ChatConversationDto
                    {
                        Id = SavedConversationId,
                        Title = "Faktury",
                        MessageCount = 1,
                        // A user message on purpose: it leaves the assistant-bubble count at
                        // zero, so a greeting seeded by mistake is the only thing that can
                        // raise it.
                        Messages = [new ChatMessageDto { Role = "User", Content = SavedUserMessage }]
                    }),
                _ => JsonContent.Create(new { })
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
