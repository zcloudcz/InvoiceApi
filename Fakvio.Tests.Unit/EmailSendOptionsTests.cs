using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// "Send invoice by e-mail" options: recipient fallback, CC, which generated files are attached
/// (client -> issuer -> system default, per-send override), stored invoice files, size limit and
/// "save as default for this client".
///
/// SMTP is stubbed through the <c>SendViaSMTPAsync</c> test seam, so these tests inspect the REAL
/// <see cref="MimeMessage"/> that would have been sent. Test data is written through a separate
/// <see cref="TenantDbContext"/> on the same in-memory database: the service loads everything with its
/// own context, so a missing Include cannot hide behind the change tracker.
/// </summary>
public class EmailSendOptionsTests : IDisposable
{
    private const long IssuerId = 1;
    private const long ClientId = 2;
    private const long InvoiceId = 1;

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly TenantDbContext _serviceContext;
    private readonly MasterDbContext _masterContext;
    private readonly IPdfExportService _pdf = Substitute.For<IPdfExportService>();
    private readonly IIsdocExportService _isdoc = Substitute.For<IIsdocExportService>();
    private readonly IUblExportService _ubl = Substitute.For<IUblExportService>();
    private readonly IFileAttachmentService _files = Substitute.For<IFileAttachmentService>();
    private readonly IContentTemplateService _templates = Substitute.For<IContentTemplateService>();
    private readonly CapturingEmailService _service;

    /// <summary>EmailService whose SMTP step records the finished message instead of connecting anywhere.</summary>
    private sealed class CapturingEmailService : EmailService
    {
        public MimeMessage? Sent { get; private set; }
        public Exception? FailWith { get; set; }

        public CapturingEmailService(
            TenantDbContext context, MasterDbContext master, IPdfExportService pdf, IFileAttachmentService files,
            IIsdocExportService isdoc, IUblExportService ubl, IContentTemplateService templates,
            ISystemConfigurationService systemConfig, ITenantResolver tenantResolver, IConfiguration configuration)
            : base(context, master, pdf, new DocumentFileNameService(context), files, isdoc, ubl, templates,
                systemConfig, Substitute.For<ICredentialProtector>(), tenantResolver, configuration,
                Substitute.For<ILogger<EmailService>>())
        {
        }

        internal override Task SendViaSMTPAsync(SmtpSettings smtp, MimeMessage message, CancellationToken ct)
        {
            if (FailWith != null)
                throw FailWith;
            Sent = message;
            return Task.CompletedTask;
        }
    }

    public EmailSendOptionsTests()
    {
        _serviceContext = new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(_dbName).Options);
        _masterContext = new MasterDbContext(
            new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var systemConfig = Substitute.For<ISystemConfigurationService>();
        systemConfig.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Contracts.Dto.SystemConfiguration.SystemConfigurationDto());
        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns((long?)null);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SmtpSettings:Host"] = "smtp.test.com",
            ["SmtpSettings:Port"] = "587",
            ["SmtpSettings:SenderEmail"] = "invoices@test.com",
            ["SmtpSettings:SenderName"] = "TestApp",
            ["SmtpSettings:UseSsl"] = "true"
        }).Build();

        _templates
            .GetDefaultByTypeAsync(Arg.Any<EContentTemplateType>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Contracts.Dto.ContentTemplate.ContentTemplateDto?)null);
        _pdf.GenerateInvoicePdfAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });
        _isdoc.ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns("<Invoice/>"u8.ToArray());
        _ubl.ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns("<UBL/>"u8.ToArray());

        _service = new CapturingEmailService(
            _serviceContext, _masterContext, _pdf, _files, _isdoc, _ubl, _templates, systemConfig, tenantResolver, config);

        using var seed = NewContext();
        seed.Currency.Add(new Currency { Id = 1, Code = "CZK", Symbol = "Kc", Name = "Czech Koruna", IsActive = true });
        seed.Client.Add(new Client { Id = IssuerId, CompanyName = "Issuer", RegistrationNumber = "111", IsIssuer = true, IsActive = true });
        seed.Client.Add(new Client { Id = ClientId, CompanyName = "Client", RegistrationNumber = "222", IsActive = true });
        seed.Invoice.Add(NewInvoice(InvoiceId, ClientId));
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _serviceContext.Dispose();
        _masterContext.Dispose();
    }

    private TenantDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(_dbName).Options);

    private static Invoice NewInvoice(long id, long clientId) => new()
    {
        Id = id, DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Completed,
        DocumentNumber = $"INV{id}", IssueDate = new DateTime(2026, 3, 5), DueDate = new DateTime(2026, 3, 19),
        IssuerId = IssuerId, ClientId = clientId, CurrencyId = 1, TotalWithVat = 1210,
        InvoiceItem = new List<InvoiceItem>()
    };

    /// <summary>Makes the client a Slovak buyer (UBL "automatic" rule = attach).</summary>
    private void MakeClientSlovak()
    {
        using var db = NewContext();
        db.Address.Add(new Address { ClientId = ClientId, Country = "SK", City = "Bratislava", Street = "Hlavna 1", PostalCode = "81101", IsPrimary = true });
        db.SaveChanges();
    }

    private void SetBillingSettings(long clientId, EEmailAttachMode pdf = EEmailAttachMode.Inherit,
        EEmailAttachMode isdoc = EEmailAttachMode.Inherit, EEmailAttachMode ubl = EEmailAttachMode.Inherit)
    {
        using var db = NewContext();
        db.BillingSettings.Add(new BillingSettings { ClientId = clientId, EmailAttachPdf = pdf, EmailAttachIsdoc = isdoc, EmailAttachUbl = ubl });
        db.SaveChanges();
    }

    private void AddContact(EContactType type, string value, bool primary = false, string? label = null)
    {
        using var db = NewContext();
        db.Contact.Add(new Contact { ClientId = ClientId, ContactType = type, ContactValue = value, IsPrimary = primary, Label = label });
        db.SaveChanges();
    }

    private static List<string> AttachmentNames(MimeMessage message) =>
        message.Attachments.OfType<MimePart>().Select(a => a.FileName).ToList();

    private BillingSettings? StoredClientSettings()
    {
        using var db = NewContext();
        return db.BillingSettings.AsNoTracking().FirstOrDefault(b => b.ClientId == ClientId);
    }

    // ─── Default attachments (client -> issuer -> system default) ──────────────

    [Fact]
    public async Task NothingConfigured_AttachesPdfAndIsdocButNoUblForAFarBuyer()
    {
        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com");

        AttachmentNames(_service.Sent!).ShouldBe(["Faktura_INV1.pdf", "Faktura_INV1.isdoc"]);
        await _ubl.DidNotReceive().ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClientSettingNo_SkipsThatFileEntirely_WithoutGeneratingIt()
    {
        SetBillingSettings(ClientId, pdf: EEmailAttachMode.No);

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com");

        AttachmentNames(_service.Sent!).ShouldBe(["Faktura_INV1.isdoc"]);
        await _pdf.DidNotReceive().GenerateInvoicePdfAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClientInherits_TheIssuersCompanyWideSettingApplies()
    {
        SetBillingSettings(IssuerId, isdoc: EEmailAttachMode.No);

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com");

        AttachmentNames(_service.Sent!).ShouldBe(["Faktura_INV1.pdf"]);
    }

    [Fact]
    public async Task ClientYes_BeatsTheIssuersNo()
    {
        SetBillingSettings(IssuerId, isdoc: EEmailAttachMode.No);
        SetBillingSettings(ClientId, isdoc: EEmailAttachMode.Yes);

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com");

        AttachmentNames(_service.Sent!).ShouldContain("Faktura_INV1.isdoc");
    }

    [Fact]
    public async Task RequestOverride_BeatsTheClientDefault()
    {
        SetBillingSettings(ClientId, pdf: EEmailAttachMode.Yes, isdoc: EEmailAttachMode.Yes);

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(AttachPdf: false, AttachIsdoc: false));

        AttachmentNames(_service.Sent!).ShouldBeEmpty();
    }

    // ─── UBL: automatic / explicit on / explicit off ───────────────────────────

    [Fact]
    public async Task Ubl_Automatic_AttachedForASlovakBuyer()
    {
        MakeClientSlovak();

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com");

        AttachmentNames(_service.Sent!).ShouldContain("Faktura_INV1.xml");
    }

    [Fact]
    public async Task Ubl_ExplicitFalse_OverridesTheAutomaticRuleForASlovakBuyer()
    {
        MakeClientSlovak();

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com", options: new SendInvoiceEmailOptions(AttachUbl: false));

        AttachmentNames(_service.Sent!).ShouldNotContain("Faktura_INV1.xml");
        await _ubl.DidNotReceive().ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ubl_ExplicitTrue_IsHonouredForABuyerWhoIsNotPeppolReachable()
    {
        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com", options: new SendInvoiceEmailOptions(AttachUbl: true));

        AttachmentNames(_service.Sent!).ShouldContain("Faktura_INV1.xml");
    }

    [Fact]
    public async Task Ubl_ClientSettingNo_WinsOverTheAutomaticRule()
    {
        MakeClientSlovak();
        SetBillingSettings(ClientId, ubl: EEmailAttachMode.No);

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com");

        AttachmentNames(_service.Sent!).ShouldNotContain("Faktura_INV1.xml");
    }

    [Fact]
    public async Task Ubl_ExplicitTrue_ButExportFails_FailsTheSendInsteadOfSilentlyDroppingIt()
    {
        _ubl.ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("boom"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com", options: new SendInvoiceEmailOptions(AttachUbl: true)));

        ex.Message.ShouldContain("UBL");
        _service.Sent.ShouldBeNull();
    }

    [Fact]
    public async Task Ubl_AutomaticAndExportFails_IsSkippedAndTheEmailStillGoesOut()
    {
        MakeClientSlovak();
        _ubl.ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("boom"));

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com");

        AttachmentNames(_service.Sent!).ShouldBe(["Faktura_INV1.pdf", "Faktura_INV1.isdoc"]);
    }

    // ─── Recipient + CC ────────────────────────────────────────────────────────

    [Fact]
    public async Task BlankRecipient_UsesTheInvoiceEmailContactBeforeAGeneralEmail()
    {
        AddContact(EContactType.Email, "general@client.cz", primary: true);
        AddContact(EContactType.InvoiceEmail, "invoices@client.cz");

        await _service.SendInvoiceEmailAsync(InvoiceId, "  ");

        _service.Sent!.To.Mailboxes.Single().Address.ShouldBe("invoices@client.cz");
    }

    [Fact]
    public async Task BlankRecipient_FallsBackToThePrimaryEmailContact()
    {
        AddContact(EContactType.Email, "second@client.cz");
        AddContact(EContactType.Email, "main@client.cz", primary: true);
        AddContact(EContactType.Phone, "+420123456789", primary: true);

        await _service.SendInvoiceEmailAsync(InvoiceId, string.Empty);

        _service.Sent!.To.Mailboxes.Single().Address.ShouldBe("main@client.cz");
    }

    [Fact]
    public async Task BlankRecipient_ClientWithoutAnyEmailContact_IsAClearError()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _service.SendInvoiceEmailAsync(InvoiceId, string.Empty));

        ex.Message.ShouldContain("No recipient");
        _service.Sent.ShouldBeNull();
    }

    [Fact]
    public async Task Cc_IsAddedToTheMessage_AndNeverRepeatsTheToAddress()
    {
        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(CcEmails: ["boss@example.com", "TO@example.com", "boss@example.com", "acc@example.com"]));

        _service.Sent!.Cc.Mailboxes.Select(m => m.Address).ShouldBe(["boss@example.com", "acc@example.com"]);
        _service.Sent.To.Mailboxes.Single().Address.ShouldBe("to@example.com");
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("Name <a@b.cz>")]
    [InlineData("a@b.cz\r\nBcc: evil@x.cz")]
    public async Task Cc_InvalidAddress_RejectsTheWholeSendAndNothingIsSent(string bad)
    {
        await Should.ThrowAsync<ArgumentException>(() => _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(CcEmails: ["ok@example.com", bad])));

        _service.Sent.ShouldBeNull();
        await _pdf.DidNotReceive().GenerateInvoicePdfAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cc_MoreThanTen_IsRejected()
    {
        var many = Enumerable.Range(1, 11).Select(i => $"u{i}@example.com").ToList();

        await Should.ThrowAsync<ArgumentException>(() => _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(CcEmails: many)));
    }

    // ─── Stored invoice files, extra files, size limit ─────────────────────────

    [Fact]
    public async Task StoredFiles_AreAttachedWithTheirOriginalNames_AndCollidingNamesAreMadeUnique()
    {
        _files.DownloadSelectedAsync("Invoice", InvoiceId, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<(byte[] Content, FileAttachmentDto Meta)>
            {
                (new byte[] { 9, 9 }, new FileAttachmentDto { Id = 10, OriginalFileName = "contract.pdf" }),
                // Same name as the generated PDF: must not overwrite it in the recipient's mail client.
                (new byte[] { 8, 8 }, new FileAttachmentDto { Id = 11, OriginalFileName = "Faktura_INV1.pdf" })
            });

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(FileAttachmentIds: [10, 11]));

        AttachmentNames(_service.Sent!).ShouldBe(
            ["Faktura_INV1.pdf", "Faktura_INV1.isdoc", "contract.pdf", "Faktura_INV1 (2).pdf"]);
    }

    [Fact]
    public async Task StoredFile_OfAnotherInvoice_IsRejectedAndNothingIsSent()
    {
        _files.DownloadSelectedAsync("Invoice", InvoiceId, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new ArgumentException("Attachment id(s) 77 do not belong to this invoice."));

        await Should.ThrowAsync<ArgumentException>(() => _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(FileAttachmentIds: [77])));

        _service.Sent.ShouldBeNull();
        await _pdf.DidNotReceive().GenerateInvoicePdfAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoFileIds_NeverTouchesTheStorageService()
    {
        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com");

        await _files.DidNotReceiveWithAnyArgs().DownloadSelectedAsync(default!, default, default!, default, default);
    }

    [Fact]
    public async Task TotalAttachmentsOverTwentyMegabytes_AreRejectedWithAClearMessage()
    {
        var elevenMb = new byte[11 * 1024 * 1024];

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(Attachments:
                [new OutgoingEmailAttachment("a.zip", elevenMb), new OutgoingEmailAttachment("b.zip", elevenMb)])));

        ex.Message.ShouldContain("20 MB");
        _service.Sent.ShouldBeNull();
    }

    // ─── "Save as default for this client" ─────────────────────────────────────

    [Fact]
    public async Task SaveAttachmentDefaults_CreatesTheClientsBillingSettings_WhenTheChoiceDiffersFromTheDefault()
    {
        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(AttachPdf: false, AttachIsdoc: true, AttachUbl: true, SaveAttachmentDefaults: true));

        var saved = StoredClientSettings();
        saved.ShouldNotBeNull();
        saved.EmailAttachPdf.ShouldBe(EEmailAttachMode.No);       // differs from the effective default (yes)
        saved.EmailAttachIsdoc.ShouldBe(EEmailAttachMode.Inherit); // merely confirms the default: keeps following the company
        saved.EmailAttachUbl.ShouldBe(EEmailAttachMode.Yes);       // differs from "automatic" (no for a far buyer)
    }

    [Fact]
    public async Task SaveAttachmentDefaults_UpdatesExistingExplicitValues_AndLeavesUnsentFieldsAlone()
    {
        SetBillingSettings(ClientId, pdf: EEmailAttachMode.No, isdoc: EEmailAttachMode.No);

        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(AttachPdf: true, SaveAttachmentDefaults: true));

        var saved = StoredClientSettings()!;
        saved.EmailAttachPdf.ShouldBe(EEmailAttachMode.Yes);
        saved.EmailAttachIsdoc.ShouldBe(EEmailAttachMode.No); // not part of the request
    }

    [Fact]
    public async Task SaveAttachmentDefaults_Off_ChangesNothing()
    {
        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(AttachPdf: false));

        StoredClientSettings().ShouldBeNull();
    }

    [Fact]
    public async Task SaveAttachmentDefaults_IsNotApplied_WhenTheSendFails()
    {
        _service.FailWith = new InvalidOperationException("smtp down");

        await Should.ThrowAsync<InvalidOperationException>(() => _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(AttachPdf: false, SaveAttachmentDefaults: true)));

        StoredClientSettings().ShouldBeNull();
    }

    // ─── GetSendDefaultsAsync (dialog prefill) ─────────────────────────────────

    [Fact]
    public async Task Defaults_ListContactsBestFirst_WithTheResolvedAttachmentFlags()
    {
        AddContact(EContactType.Email, "general@client.cz", primary: true, label: "Office");
        AddContact(EContactType.InvoiceEmail, "invoices@client.cz");
        AddContact(EContactType.Phone, "+420123456789", primary: true);
        SetBillingSettings(ClientId, pdf: EEmailAttachMode.No);
        MakeClientSlovak();
        _files.GetByEntityAsync("Invoice", InvoiceId, Arg.Any<CancellationToken>())
            .Returns(new List<FileAttachmentDto> { new() { Id = 5, OriginalFileName = "contract.pdf", FileSizeBytes = 2048 } });

        var defaults = await _service.GetSendDefaultsAsync(InvoiceId);

        defaults.Recipients.Select(r => r.Email).ShouldBe(["invoices@client.cz", "general@client.cz"]);
        defaults.Recipients[0].IsInvoiceEmail.ShouldBeTrue();
        defaults.Recipients[1].Label.ShouldBe("Office");
        defaults.AttachPdf.ShouldBeFalse();
        defaults.AttachIsdoc.ShouldBeTrue();
        defaults.UblIsAutomatic.ShouldBeTrue();
        defaults.UblAutoWouldAttach.ShouldBeTrue();
        defaults.AttachUbl.ShouldBeTrue();
        defaults.StoredAttachments.Select(a => a.Id).ShouldBe([5]);
        defaults.DefaultSubject.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Defaults_ExplicitUblSetting_IsNotAutomatic()
    {
        SetBillingSettings(ClientId, ubl: EEmailAttachMode.No);
        MakeClientSlovak();

        var defaults = await _service.GetSendDefaultsAsync(InvoiceId);

        defaults.UblIsAutomatic.ShouldBeFalse();
        defaults.AttachUbl.ShouldBeFalse();
        defaults.UblAutoWouldAttach.ShouldBeTrue();
    }

    [Fact]
    public async Task Defaults_UnknownInvoice_ThrowsKeyNotFound()
    {
        await Should.ThrowAsync<KeyNotFoundException>(() => _service.GetSendDefaultsAsync(999));
    }

    // ─── Editable body (full HTML override) ────────────────────────────────────

    [Fact]
    public async Task HtmlBodyOverride_IsTheBodyOfTheSentMessage_AndPlaceholdersAreFilled()
    {
        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com",
            options: new SendInvoiceEmailOptions(
                Subject: "Re: {{InvoiceNumber}}",
                HtmlBody: "<p>Hello, invoice {{InvoiceNumber}} attached.</p>",
                Message: "ignored intro"));

        _service.Sent!.Subject.ShouldBe("Re: INV1");
        _service.Sent.HtmlBody.ShouldBe("<p>Hello, invoice INV1 attached.</p>");
    }

    [Fact]
    public async Task NoHtmlBodyOverride_SendsTheRenderedTemplate()
    {
        await _service.SendInvoiceEmailAsync(InvoiceId, "to@example.com");

        _service.Sent!.HtmlBody.ShouldContain("INV1"); // built-in fallback text, placeholders resolved
    }

    [Fact]
    public async Task Defaults_ReturnTheRenderedBody_FromTheSameRenderAsTheSubject()
    {
        var template = new Contracts.Dto.ContentTemplate.ContentTemplateDto { Id = 9 };
        _templates.GetDefaultByTypeAsync(Arg.Any<EContentTemplateType>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(template);
        _templates.RenderTemplateAsync(9, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(("Subject INV", "<p>Body for INV1</p>"));

        var defaults = await _service.GetSendDefaultsAsync(InvoiceId);

        defaults.DefaultSubject.ShouldBe("Subject INV");
        defaults.DefaultHtmlBody.ShouldBe("<p>Body for INV1</p>");
        await _templates.Received(1).RenderTemplateAsync(9, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Defaults_WithoutATemplate_FallBackToTheBuiltInBodyWithPlaceholdersResolved()
    {
        var defaults = await _service.GetSendDefaultsAsync(InvoiceId);

        defaults.DefaultHtmlBody.ShouldNotBeNullOrWhiteSpace();
        defaults.DefaultHtmlBody.ShouldContain("INV1");
        defaults.DefaultHtmlBody.ShouldNotContain("{{");
    }

    // ─── Message structure ─────────────────────────────────────────────────────

    [Fact]
    public void BuildInvoiceMessage_WithoutGeneratedFiles_HasOnlyTheExtras()
    {
        var message = EmailService.BuildInvoiceMessage(
            "Sender", "from@x.cz", "to@x.cz", "Subject", "<p>Hi</p>",
            pdfBytes: null, pdfFileName: null, isdocBytes: null, isdocFileName: null,
            extraAttachments: [new OutgoingEmailAttachment("note.txt", [1])], ccEmails: ["cc@x.cz"]);

        AttachmentNames(message).ShouldBe(["note.txt"]);
        message.Cc.Mailboxes.Single().Address.ShouldBe("cc@x.cz");
    }
}
