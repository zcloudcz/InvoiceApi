using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using Tizzani.MudBlazor.HtmlEditor;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit tests for the ONE shared "send by e-mail" dialog: prefill from the server defaults, toggling,
/// CC parsing and the exact request it posts. Rendered through a real MudDialogProvider (MudDialog only
/// renders inside a genuine dialog instance); the API is a canned HttpMessageHandler.
/// </summary>
public class SendInvoiceEmailDialogTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private sealed class Backend : HttpMessageHandler
    {
        public InvoiceEmailDefaultsDto? Defaults { get; set; }
        public bool DefaultsFail { get; set; }
        public HttpStatusCode SendStatus { get; set; } = HttpStatusCode.OK;
        public SendInvoiceEmailDto? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/email-defaults"))
            {
                return DefaultsFail
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(Defaults, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/send-email"))
            {
                Sent = await request.Content!.ReadFromJsonAsync<SendInvoiceEmailDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
                return new HttpResponseMessage(SendStatus)
                {
                    Content = JsonContent.Create(new { message = SendStatus == HttpStatusCode.OK ? "ok" : "Attachments are too large" })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private readonly Backend _backend = new();

    public SendInvoiceEmailDialogTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(new HttpClient(_backend) { BaseAddress = new Uri("https://test.local") });
        Services.AddSingleton(new FakvioService(factory, NullLogger<FakvioService>.Instance, Substitute.For<AuthenticationStateProvider>()));

        _backend.Defaults = StandardDefaults();
    }

    private static InvoiceEmailDefaultsDto StandardDefaults() => new()
    {
        Recipients =
        [
            new EmailRecipientOptionDto { Email = "invoices@client.cz", Label = "Accounting", IsInvoiceEmail = true },
            new EmailRecipientOptionDto { Email = "boss@client.cz", IsPrimary = true }
        ],
        AttachPdf = true,
        AttachIsdoc = false,
        AttachUbl = false,
        UblIsAutomatic = true,
        UblAutoWouldAttach = false,
        DefaultSubject = "Invoice INV1",
        DefaultHtmlBody = "<p>Dear client, invoice INV1 attached.</p>",
        StoredAttachments =
        [
            new FileAttachmentDto { Id = 41, OriginalFileName = "contract.pdf", FileSizeBytes = 2048 },
            new FileAttachmentDto { Id = 42, OriginalFileName = "photo.png", FileSizeBytes = 5 * 1024 * 1024 }
        ]
    };

    /// <summary>Opens the dialog the way pages do (static ShowAsync) and returns it plus the pending "was sent" result.</summary>
    private async Task<(IRenderedComponent<SendInvoiceEmailDialog> Dialog, Task<bool> Sent)> OpenAsync()
    {
        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        Task<bool>? sent = null;
        await provider.InvokeAsync(() =>
        {
            sent = SendInvoiceEmailDialog.ShowAsync(dialogService, "Title", 7, "INV1", "Client Ltd");
        });

        var dialog = provider.FindComponent<SendInvoiceEmailDialog>();
        // Wait until the defaults request finished and the form is shown.
        dialog.WaitForAssertion(() => dialog.FindComponents<MudAutocomplete<string>>().Count.ShouldBe(1));
        return (dialog, sent!);
    }

    private static IReadOnlyList<IRenderedComponent<MudCheckBox<bool>>> Boxes(IRenderedComponent<SendInvoiceEmailDialog> dialog) =>
        dialog.FindComponents<MudCheckBox<bool>>();

    private static IRenderedComponent<MudCheckBox<bool>> Box(IRenderedComponent<SendInvoiceEmailDialog> dialog, string labelPart) =>
        Boxes(dialog).Single(b => (b.Instance.Label ?? "").Contains(labelPart));

    private static Task Set(IRenderedComponent<SendInvoiceEmailDialog> dialog, IRenderedComponent<MudCheckBox<bool>> box, bool value) =>
        dialog.InvokeAsync(() => box.Instance.ValueChanged.InvokeAsync(value));

    private static IRenderedComponent<MudButton> SendButton(IRenderedComponent<SendInvoiceEmailDialog> dialog) =>
        dialog.FindComponents<MudButton>().Single(b => b.Instance.Color == Color.Primary);

    // ─── Prefill ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Prefill_BestContactIsTheRecipient_DefaultsAndSubjectAreApplied()
    {
        var (dialog, _) = await OpenAsync();

        dialog.FindComponent<MudAutocomplete<string>>().Instance.Value.ShouldBe("invoices@client.cz");
        Box(dialog, "EmailSend_AttachPdf").Instance.Value.ShouldBeTrue();
        Box(dialog, "EmailSend_AttachIsdoc").Instance.Value.ShouldBeFalse();
        Box(dialog, "EmailSend_AttachUbl").Instance.Value.ShouldBeFalse();
        dialog.Markup.ShouldContain("EmailSend_UblAutoOff"); // automatic + this buyer would not get UBL
        dialog.FindComponents<MudTextField<string>>().Any(t => t.Instance.Value == "Invoice INV1").ShouldBeTrue();
        // Stored files are listed, unchecked
        Box(dialog, "contract.pdf (").Instance.Label.ShouldContain("KB"); // size is shown (decimal separator is culture-specific)
        Box(dialog, "contract.pdf (").Instance.Value.ShouldBeFalse();
        Box(dialog, "photo.png (").Instance.Label.ShouldContain("MB");
        Box(dialog, "photo.png (").Instance.Value.ShouldBeFalse();
        // "save as default" is off
        Box(dialog, "EmailSend_SaveDefaults").Instance.Value.ShouldBeFalse();
    }

    [Fact]
    public async Task Prefill_UblAutomaticAndWouldAttach_ShowsTheOnHint_AndTheBoxIsTicked()
    {
        _backend.Defaults!.AttachUbl = true;
        _backend.Defaults.UblAutoWouldAttach = true;

        var (dialog, _) = await OpenAsync();

        Box(dialog, "EmailSend_AttachUbl").Instance.Value.ShouldBeTrue();
        dialog.Markup.ShouldContain("EmailSend_UblAutoOn");
    }

    [Fact]
    public async Task Prefill_ExplicitUblSetting_HasNoAutomaticHint()
    {
        _backend.Defaults!.UblIsAutomatic = false;

        var (dialog, _) = await OpenAsync();

        dialog.Markup.ShouldNotContain("EmailSend_UblAuto");
    }

    // ─── Sending ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_PostsTheChoices_AndClosesWithSuccess()
    {
        var (dialog, sent) = await OpenAsync();

        await Set(dialog, Box(dialog, "EmailSend_AttachPdf"), false);
        await Set(dialog, Box(dialog, "EmailSend_AttachIsdoc"), true);
        await Set(dialog, Box(dialog, "EmailSend_AttachUbl"), true);
        await Set(dialog, Box(dialog, "contract.pdf"), true);
        await Set(dialog, Box(dialog, "EmailSend_SaveDefaults"), true);
        await dialog.InvokeAsync(() => dialog.FindComponent<MudAutocomplete<string>>().Instance.ValueChanged.InvokeAsync("boss@client.cz"));
        var cc = dialog.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "EmailSend_Cc");
        await dialog.InvokeAsync(() => cc.Instance.ValueChanged.InvokeAsync("a@x.cz; b@x.cz, A@x.cz"));

        await dialog.InvokeAsync(() => SendButton(dialog).Find("button").Click());

        (await sent).ShouldBeTrue();
        var dto = _backend.Sent.ShouldNotBeNull();
        dto.RecipientEmail.ShouldBe("boss@client.cz");
        dto.CcEmails.ShouldBe(["a@x.cz", "b@x.cz"]); // split on ; and , and de-duplicated
        dto.AttachPdf.ShouldBe(false);
        dto.AttachIsdoc.ShouldBe(true);
        dto.AttachUbl.ShouldBe(true);
        dto.FileAttachmentIds.ShouldBe([41]);
        dto.SaveAttachmentDefaults.ShouldBeTrue();
        dto.Subject.ShouldBeNull(); // unchanged default subject is not sent as a custom one
    }

    [Fact]
    public async Task Send_EditedSubject_IsSent()
    {
        var (dialog, sent) = await OpenAsync();
        var subject = dialog.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "EmailSend_Subject");
        await dialog.InvokeAsync(() => subject.Instance.ValueChanged.InvokeAsync("My subject"));

        await dialog.InvokeAsync(() => SendButton(dialog).Find("button").Click());

        (await sent).ShouldBeTrue();
        _backend.Sent!.Subject.ShouldBe("My subject");
        _backend.Sent.CcEmails.ShouldBeNull();
        _backend.Sent.FileAttachmentIds.ShouldBeNull();
        _backend.Sent.SaveAttachmentDefaults.ShouldBeFalse();
    }

    // ─── Editable e-mail body ──────────────────────────────────────────────

    private static IRenderedComponent<MudHtmlEditor> Editor(IRenderedComponent<SendInvoiceEmailDialog> dialog) =>
        dialog.FindComponent<MudHtmlEditor>();

    /// <summary>Simulates the editor reporting new HTML (what Quill's text-change event does).</summary>
    private static Task EditorReports(IRenderedComponent<SendInvoiceEmailDialog> dialog, string html) =>
        dialog.InvokeAsync(() => Editor(dialog).Instance.HtmlChanged.InvokeAsync(html));

    [Fact]
    public async Task Body_EditorIsPrefilledWithTheRenderedTemplateBody()
    {
        var (dialog, _) = await OpenAsync();

        Editor(dialog).Instance.Html.ShouldBe("<p>Dear client, invoice INV1 attached.</p>");
        dialog.Markup.ShouldContain("EmailSend_BodyHint");
    }

    [Fact]
    public async Task Body_UntouchedEditor_SendsNull_EvenWhenTheEditorNormalisesTheHtmlOnLoad()
    {
        var (dialog, sent) = await OpenAsync();
        // Quill re-formats the template right after load and reports it: that is the baseline, not an edit.
        await EditorReports(dialog, "<p>Dear client, invoice INV1 attached.</p><p><br></p>");

        await dialog.InvokeAsync(() => SendButton(dialog).Find("button").Click());

        (await sent).ShouldBeTrue();
        _backend.Sent!.HtmlBody.ShouldBeNull(); // server re-renders the template and keeps its styling
    }

    [Fact]
    public async Task Body_EditedEditor_SendsTheEditorHtml()
    {
        var (dialog, sent) = await OpenAsync();
        await EditorReports(dialog, "<p>normalised</p>");
        await EditorReports(dialog, "<p>normalised, plus my sentence</p>");

        await dialog.InvokeAsync(() => SendButton(dialog).Find("button").Click());

        (await sent).ShouldBeTrue();
        _backend.Sent!.HtmlBody.ShouldBe("<p>normalised, plus my sentence</p>");
        _backend.Sent.Message.ShouldBeNull();
    }

    [Fact]
    public async Task Body_ResetToTemplate_RestoresSubjectAndBody_AndSendsNulls()
    {
        var (dialog, sent) = await OpenAsync();
        await EditorReports(dialog, "<p>normalised</p>");
        await EditorReports(dialog, "<p>edited</p>");
        var subject = dialog.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "EmailSend_Subject");
        await dialog.InvokeAsync(() => subject.Instance.ValueChanged.InvokeAsync("My subject"));

        var reset = dialog.FindComponents<MudButton>().Single(b => b.Markup.Contains("EmailSend_ResetToTemplate"));
        await dialog.InvokeAsync(() => reset.Find("button").Click());
        await EditorReports(dialog, "<p>normalised</p>"); // the editor echoes the text that was pushed back in

        dialog.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "EmailSend_Subject")
            .Instance.Value.ShouldBe("Invoice INV1");
        await dialog.InvokeAsync(() => SendButton(dialog).Find("button").Click());

        (await sent).ShouldBeTrue();
        _backend.Sent!.Subject.ShouldBeNull();
        _backend.Sent.HtmlBody.ShouldBeNull();
    }

    [Fact]
    public async Task Body_NoRenderedBody_HidesTheEditor_AndSendsNull()
    {
        _backend.Defaults!.DefaultHtmlBody = null;
        var (dialog, sent) = await OpenAsync();

        dialog.FindComponents<MudHtmlEditor>().ShouldBeEmpty();
        await dialog.InvokeAsync(() => SendButton(dialog).Find("button").Click());

        (await sent).ShouldBeTrue();
        _backend.Sent!.HtmlBody.ShouldBeNull();
    }

    [Fact]
    public async Task InvalidCc_ShowsAnErrorAndDisablesSend()
    {
        var (dialog, _) = await OpenAsync();
        SendButton(dialog).Instance.Disabled.ShouldBeFalse();

        var cc = dialog.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "EmailSend_Cc");
        await dialog.InvokeAsync(() => cc.Instance.ValueChanged.InvokeAsync("good@x.cz, broken"));

        dialog.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "EmailSend_Cc").Instance.Error.ShouldBeTrue();
        SendButton(dialog).Instance.Disabled.ShouldBeTrue();
    }

    [Fact]
    public async Task TooManyCc_DisablesSend()
    {
        var (dialog, _) = await OpenAsync();
        var cc = dialog.FindComponents<MudTextField<string>>().Single(t => t.Instance.Label == "EmailSend_Cc");

        await dialog.InvokeAsync(() => cc.Instance.ValueChanged.InvokeAsync(string.Join(",", Enumerable.Range(1, 11).Select(i => $"u{i}@x.cz"))));

        SendButton(dialog).Instance.Disabled.ShouldBeTrue();
    }

    [Fact]
    public async Task EmptyOrInvalidRecipient_DisablesSend()
    {
        var (dialog, _) = await OpenAsync();
        var to = dialog.FindComponent<MudAutocomplete<string>>();

        await dialog.InvokeAsync(() => to.Instance.TextChanged.InvokeAsync(""));
        SendButton(dialog).Instance.Disabled.ShouldBeTrue();

        await dialog.InvokeAsync(() => to.Instance.TextChanged.InvokeAsync("not-an-address"));
        SendButton(dialog).Instance.Disabled.ShouldBeTrue();

        await dialog.InvokeAsync(() => to.Instance.TextChanged.InvokeAsync("typed@free.cz")); // free text is allowed
        SendButton(dialog).Instance.Disabled.ShouldBeFalse();
    }

    [Fact]
    public async Task ServerRejection_KeepsTheDialogOpen_AndShowsTheServerMessage()
    {
        _backend.SendStatus = HttpStatusCode.BadRequest;
        var (dialog, sent) = await OpenAsync();

        await dialog.InvokeAsync(() => SendButton(dialog).Find("button").Click());

        sent.IsCompleted.ShouldBeFalse(); // dialog still open, the user can fix the input
        Services.GetRequiredService<ISnackbar>().ShouldNotBeNull();
        dialog.FindComponents<MudAutocomplete<string>>().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_ClosesWithoutSending()
    {
        var (dialog, sent) = await OpenAsync();

        var cancel = dialog.FindComponents<MudButton>().Single(b => b.Markup.Contains("Btn_Cancel"));
        await dialog.InvokeAsync(() => cancel.Find("button").Click());

        (await sent).ShouldBeFalse();
        _backend.Sent.ShouldBeNull();
    }

    // ─── Degraded mode ─────────────────────────────────────────────────────

    [Fact]
    public async Task DefaultsFailToLoad_WarnsHidesAttachmentChoices_AndSendsNoExplicitFlags()
    {
        _backend.DefaultsFail = true;
        var (dialog, sent) = await OpenAsync();

        dialog.Markup.ShouldContain("EmailSend_DefaultsFailed");
        Boxes(dialog).ShouldBeEmpty();
        dialog.FindComponents<MudHtmlEditor>().ShouldBeEmpty(); // nothing rendered -> the server decides the text

        var to = dialog.FindComponent<MudAutocomplete<string>>();
        await dialog.InvokeAsync(() => to.Instance.TextChanged.InvokeAsync("manual@client.cz"));
        await dialog.InvokeAsync(() => SendButton(dialog).Find("button").Click());

        (await sent).ShouldBeTrue();
        _backend.Sent!.RecipientEmail.ShouldBe("manual@client.cz");
        _backend.Sent.AttachPdf.ShouldBeNull();   // null = the server applies the client's own defaults
        _backend.Sent.AttachIsdoc.ShouldBeNull();
        _backend.Sent.AttachUbl.ShouldBeNull();
        _backend.Sent.SaveAttachmentDefaults.ShouldBeFalse();
    }

    [Fact]
    public async Task ClientWithoutContacts_StartsWithAnEmptyRecipient()
    {
        _backend.Defaults!.Recipients = [];

        var (dialog, _) = await OpenAsync();

        SendButton(dialog).Instance.Disabled.ShouldBeTrue();
    }
}

/// <summary>The three billing-settings selects (client + company) and the one-line summary used by the detail views.</summary>
public class EmailAttachmentDefaultsInputTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();

    public EmailAttachmentDefaultsInputTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(_localizer);
    }

    [Fact]
    public void RendersThreeSelects_PreselectedAndReportingChanges()
    {
        EEmailAttachMode? pdfChanged = null;
        var cut = Render<EmailAttachmentDefaultsInput>(ps => ps
            .Add(p => p.Pdf, EEmailAttachMode.No)
            .Add(p => p.Isdoc, EEmailAttachMode.Yes)
            .Add(p => p.Ubl, EEmailAttachMode.Inherit)
            .Add(p => p.PdfChanged, v => pdfChanged = v));

        var selects = cut.FindComponents<MudSelect<EEmailAttachMode>>();
        selects.Select(s => s.Instance.Value).ShouldBe([EEmailAttachMode.No, EEmailAttachMode.Yes, EEmailAttachMode.Inherit]);

        cut.InvokeAsync(() => selects[0].Instance.ValueChanged.InvokeAsync(EEmailAttachMode.Yes));
        pdfChanged.ShouldBe(EEmailAttachMode.Yes);
    }

    [Theory]
    [InlineData(false, "EmailAttach_HintClient")]
    [InlineData(true, "EmailAttach_HintCompany")]
    public void HintDependsOnTheLevel(bool isCompany, string expectedKey)
    {
        var cut = Render<EmailAttachmentDefaultsInput>(ps => ps.Add(p => p.IsCompany, isCompany));

        cut.Markup.ShouldContain(expectedKey);
    }

    [Fact]
    public void Summary_ListsOnlyConfiguredFields_AndIsNullWhenAllDefault()
    {
        EmailAttachmentDefaultsInput.Summary(_localizer, EEmailAttachMode.Inherit, EEmailAttachMode.Inherit, EEmailAttachMode.Inherit).ShouldBeNull();

        EmailAttachmentDefaultsInput.Summary(_localizer, EEmailAttachMode.No, EEmailAttachMode.Inherit, EEmailAttachMode.Yes)
            .ShouldBe("EmailAttach_Pdf: EEmailAttachMode_No, EmailAttach_Ubl: EEmailAttachMode_Yes");
    }
}
