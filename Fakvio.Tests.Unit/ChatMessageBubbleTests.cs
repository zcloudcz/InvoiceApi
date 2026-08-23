using Bunit;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for <see cref="ChatMessageBubble"/>.
///
/// The point of these tests is the split introduced in issue #161: assistant answers are
/// markdown and must render as HTML, while user messages stay literal text. A unit test on
/// MarkdownRenderer alone cannot catch a bubble that forgets to call it.
/// </summary>
public class ChatMessageBubbleTests : BunitContext, IAsyncLifetime
{
    // MudBlazor's PopoverService only supports async disposal; xunit v2 disposes
    // test classes synchronously, so route disposal through IAsyncLifetime.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public ChatMessageBubbleTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Localizer returns the key itself — the bubble only shows role captions.
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(
            ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);
    }

    private IRenderedComponent<ChatMessageBubble> RenderBubble(string role, string content) =>
        Render<ChatMessageBubble>(parameters => parameters
            .Add(p => p.Message, new ChatMessageDto
            {
                Role = role,
                Content = content,
                CreatedAt = DateTime.UtcNow
            }));

    [Fact]
    public void AssistantMessage_MarkdownList_RendersAsHtmlList()
    {
        var bubble = RenderBubble("Assistant", "Máte:\n- 2 faktury\n- 1 dobropis");

        var markup = bubble.Markup;
        markup.ShouldContain("<ul>");
        markup.ShouldContain("<li>2 faktury</li>");
        // The raw markdown dash must not survive as literal text.
        markup.ShouldNotContain("- 2 faktury");
    }

    [Fact]
    public void AssistantMessage_Bold_RendersStrong()
    {
        var bubble = RenderBubble("Assistant", "Stav: **uhrazeno**");

        bubble.Markup.ShouldContain("<strong>uhrazeno</strong>");
    }

    [Fact]
    public void AssistantMessage_RawHtml_IsEscaped()
    {
        var bubble = RenderBubble("Assistant", "<script>alert('xss')</script>");

        bubble.Markup.ShouldNotContain("<script>");
    }

    [Fact]
    public void UserMessage_MarkdownCharacters_StayLiteral()
    {
        // A user typing an identifier with asterisks or underscores must see it back
        // unchanged — user input is deliberately NOT run through the markdown renderer.
        var bubble = RenderBubble("User", "faktura **FAK_2026_001**");

        var markup = bubble.Markup;
        markup.ShouldNotContain("<strong>");
        markup.ShouldContain("**FAK_2026_001**");
    }

    [Fact]
    public void UserMessage_UsesUserBubbleClass()
    {
        // Bubble geometry/colors now come from app.css classes instead of inline styles.
        var bubble = RenderBubble("User", "ahoj");

        bubble.Markup.ShouldContain("chat-bubble-user");
        bubble.Markup.ShouldContain("chat-bubble-text");
    }

    [Fact]
    public void AssistantMessage_UsesAssistantBubbleClass()
    {
        var bubble = RenderBubble("Assistant", "ahoj");

        bubble.Markup.ShouldContain("chat-bubble-assistant");
        bubble.Markup.ShouldContain("markdown-body");
    }
}
