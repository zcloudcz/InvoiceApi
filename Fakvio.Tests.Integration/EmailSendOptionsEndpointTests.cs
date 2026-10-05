using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MimeKit;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// "Send invoice by e-mail" end to end through the HTTP pipeline: CC, recipient fallback, attachment choice,
/// stored invoice files, "save as default" and the dialog's defaults endpoint.
///
/// The real <see cref="EmailService"/> runs (real PDF / ISDOC export, real file-name service, real EF queries);
/// only the SMTP step is replaced (a subclass records the finished <see cref="MimeMessage"/>) and blob storage is
/// an in-memory dictionary. Test data is seeded through a SEPARATE DI scope from the request, so the service has
/// to load contacts / billing settings itself (no hidden change-tracker fix-up).
/// </summary>
public class EmailSendOptionsEndpointTests : IClassFixture<EmailSendOptionsEndpointTests.MailFactory>
{
    /// <summary>Every message the stubbed SMTP step "sent", shared by the whole factory.</summary>
    public sealed class SentMailSink
    {
        public ConcurrentQueue<MimeMessage> Messages { get; } = new();
    }

    /// <summary>EmailService whose SMTP step records the message instead of connecting anywhere.</summary>
    public sealed class RecordingEmailService : EmailService
    {
        private readonly SentMailSink _sink;

        public RecordingEmailService(
            TenantDbContext context, MasterDbContext masterContext, IPdfExportService pdf, IDocumentFileNameService names,
            IFileAttachmentService files, IIsdocExportService isdoc, IUblExportService ubl, IContentTemplateService templates,
            ISystemConfigurationService systemConfig, ICredentialProtector protector, ITenantResolver tenantResolver,
            IConfiguration configuration, ILogger<EmailService> logger, SentMailSink sink)
            : base(context, masterContext, pdf, names, files, isdoc, ubl, templates, systemConfig, protector, tenantResolver,
                configuration, logger)
        {
            _sink = sink;
        }

        internal override Task SendViaSMTPAsync(SmtpSettings smtp, MimeMessage message, CancellationToken ct)
        {
            _sink.Messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    public class MailFactory : FakvioFactory
    {
        public SentMailSink Sink { get; } = new();
        public ReceivedInvoiceAttachmentEndpointTests.InMemoryFileStorage FileStorage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // SMTP settings only need to exist: tier 3 (appsettings) is enough for ResolveSmtpSettingsAsync.
            builder.UseSetting("SmtpSettings:Host", "smtp.invalid");
            builder.UseSetting("SmtpSettings:Port", "587");
            builder.UseSetting("SmtpSettings:SenderEmail", "invoices@test.invalid");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Sink);
                services.RemoveAll<IEmailService>();
                services.AddScoped<IEmailService, RecordingEmailService>();
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(FileStorage);
            });
        }
    }

    private readonly MailFactory _factory;

    private const long CompanyId = 8950L;
    private const long IssuerId = 8950L;
    private const long CustomerWithContacts = 8951L;
    private const long CustomerWithoutContacts = 8952L;
    private const long CustomerForSave = 8953L;
    private const long CustomerForReset = 8954L;
    private const long InvoiceMain = 8951L;
    private const long InvoiceOther = 8952L;
    private const long InvoiceNoContacts = 8953L;
    private const long InvoiceSave = 8954L;
    private const long InvoiceReset = 8955L;
    private const long StoredOwn = 8961L;
    private const long StoredOfOtherInvoice = 8962L;

    public EmailSendOptionsEndpointTests(MailFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        Seed();
    }

    private void Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        if (!masterDb.Client.Any(c => c.Id == CompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = CompanyId, CompanyName = "Mail Issuer", RegistrationNumber = "EM000001", IsIssuer = true, IsActive = true
            });
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId, SchemaName = $"tenant_em_{CompanyId}", IsProvisioned = true, IsActive = true
            });
            masterDb.SaveChanges();
        }

        if (tenantDb.Client.Any(c => c.Id == IssuerId))
            return; // already seeded by an earlier test in this class

        tenantDb.Client.Add(new Client { Id = IssuerId, CompanyName = "Mail Issuer", RegistrationNumber = "EM000001", IsIssuer = true, IsActive = true });
        tenantDb.Client.Add(new Client
        {
            Id = CustomerWithContacts, CompanyName = "Customer A", RegistrationNumber = "EM000002", IsActive = true,
            Contact =
            [
                new Contact { ContactType = EContactType.Email, ContactValue = "general@customer-a.cz", IsPrimary = true, Label = "Office" },
                new Contact { ContactType = EContactType.InvoiceEmail, ContactValue = "invoices@customer-a.cz" }
            ]
        });
        tenantDb.Client.Add(new Client { Id = CustomerWithoutContacts, CompanyName = "Customer B", RegistrationNumber = "EM000003", IsActive = true });
        tenantDb.Client.Add(new Client
        {
            Id = CustomerForSave, CompanyName = "Customer C", RegistrationNumber = "EM000004", IsActive = true,
            Contact = [new Contact { ContactType = EContactType.Email, ContactValue = "c@customer-c.cz", IsPrimary = true }]
        });
        tenantDb.Client.Add(new Client
        {
            Id = CustomerForReset, CompanyName = "Customer D", RegistrationNumber = "EM000005", IsActive = true,
            Contact = [new Contact { ContactType = EContactType.Email, ContactValue = "d@customer-d.cz", IsPrimary = true }],
            BillingSettings = new BillingSettings { EmailAttachPdf = EEmailAttachMode.No, EmailAttachUbl = EEmailAttachMode.Yes }
        });
        tenantDb.SaveChanges();

        AddInvoice(tenantDb, InvoiceMain, CustomerWithContacts, "EM1");
        AddInvoice(tenantDb, InvoiceOther, CustomerWithContacts, "EM2");
        AddInvoice(tenantDb, InvoiceNoContacts, CustomerWithoutContacts, "EM3");
        AddInvoice(tenantDb, InvoiceSave, CustomerForSave, "EM4");
        AddInvoice(tenantDb, InvoiceReset, CustomerForReset, "EM5");

        tenantDb.FileAttachment.Add(new FileAttachment
        {
            Id = StoredOwn, EntityName = "Invoice", RecordId = InvoiceMain, OriginalFileName = "contract.pdf",
            ContentType = "application/pdf", FileSizeBytes = 4, BlobPath = "8950/own.pdf"
        });
        tenantDb.FileAttachment.Add(new FileAttachment
        {
            Id = StoredOfOtherInvoice, EntityName = "Invoice", RecordId = InvoiceOther, OriginalFileName = "secret.pdf",
            ContentType = "application/pdf", FileSizeBytes = 4, BlobPath = "8950/other.pdf"
        });
        tenantDb.SaveChanges();
        _factory.FileStorage.Seed("8950/own.pdf", [1, 2, 3, 4]);
        _factory.FileStorage.Seed("8950/other.pdf", [9, 9, 9, 9]);
    }

    private static void AddInvoice(TenantDbContext db, long id, long clientId, string number) =>
        db.Invoice.Add(new Invoice
        {
            Id = id, DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Completed, DocumentNumber = number,
            IssueDate = new DateTime(2026, 3, 5), DueDate = new DateTime(2026, 3, 19),
            IssuerId = IssuerId, ClientId = clientId, CurrencyId = 1
        });

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    /// <summary>The message sent for exactly this test: every test uses its own invoice number in the subject.</summary>
    private MimeMessage? SentFor(string subjectMarker) =>
        _factory.Sink.Messages.LastOrDefault(m => (m.Subject ?? "").Contains(subjectMarker));

    private static List<string> Names(MimeMessage m) => m.Attachments.OfType<MimePart>().Select(a => a.FileName).ToList();

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, long invoiceId, SendInvoiceEmailDto dto, string marker)
    {
        dto.Subject = marker; // lets the test find its own message in the shared sink
        return await client.PostAsJsonAsync($"/api/invoice/{invoiceId}/send-email", dto);
    }

    private BillingSettings? StoredSettings(long clientId)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TenantDbContext>().BillingSettings
            .AsNoTracking().FirstOrDefault(b => b.ClientId == clientId);
    }

    // ─── POST /api/invoice/{id}/send-email ─────────────────────────────────────

    [Fact]
    public async Task Send_WithCcAndStoredFile_DeliversThemAlongsideTheGeneratedFiles()
    {
        var client = await AuthClientAsync();

        var response = await SendAsync(client, InvoiceMain, new SendInvoiceEmailDto
        {
            RecipientEmail = "to@customer-a.cz",
            CcEmails = ["boss@customer-a.cz", "acc@customer-a.cz"],
            FileAttachmentIds = [StoredOwn]
        }, "cc-and-stored");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var mail = SentFor("cc-and-stored").ShouldNotBeNull();
        mail.To.Mailboxes.Single().Address.ShouldBe("to@customer-a.cz");
        mail.Cc.Mailboxes.Select(m => m.Address).ShouldBe(["boss@customer-a.cz", "acc@customer-a.cz"]);
        Names(mail).ShouldBe(["Faktura_EM1.pdf", "Faktura_EM1.isdoc", "contract.pdf"]);

        using var scope = _factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<TenantDbContext>().Invoice.AsNoTracking().FirstAsync(i => i.Id == InvoiceMain);
        invoice.IsSentByEmail.ShouldBeTrue();
    }

    [Fact]
    public async Task Send_AttachmentChoices_AreHonoured()
    {
        var client = await AuthClientAsync();

        var response = await SendAsync(client, InvoiceMain, new SendInvoiceEmailDto
        {
            RecipientEmail = "to@customer-a.cz", AttachPdf = false, AttachIsdoc = true, AttachUbl = false
        }, "choices");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Names(SentFor("choices")!).ShouldBe(["Faktura_EM1.isdoc"]);
    }

    [Fact]
    public async Task Send_FileOfAnotherInvoice_Returns400_AndNothingIsSent()
    {
        var client = await AuthClientAsync();

        var response = await SendAsync(client, InvoiceMain, new SendInvoiceEmailDto
        {
            RecipientEmail = "to@customer-a.cz", FileAttachmentIds = [StoredOwn, StoredOfOtherInvoice]
        }, "foreign-file");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(StoredOfOtherInvoice.ToString());
        SentFor("foreign-file").ShouldBeNull();
    }

    [Fact]
    public async Task Send_InvalidCc_Returns400_AndNothingIsSent()
    {
        var client = await AuthClientAsync();

        var response = await SendAsync(client, InvoiceMain, new SendInvoiceEmailDto
        {
            RecipientEmail = "to@customer-a.cz", CcEmails = ["fine@x.cz", "not an address"]
        }, "bad-cc");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        SentFor("bad-cc").ShouldBeNull();
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("Jan <to@customer-a.cz>")]
    [InlineData("to@customer-a.cz\r\nBcc: evil@x.cz")]
    public async Task Send_InvalidRecipient_Returns400_AndNothingIsSent(string recipient)
    {
        var client = await AuthClientAsync();

        var response = await SendAsync(client, InvoiceMain, new SendInvoiceEmailDto
        {
            RecipientEmail = recipient
        }, "bad-to");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        SentFor("bad-to").ShouldBeNull();
    }

    [Fact]
    public async Task Send_WithoutRecipient_UsesTheClientsInvoiceEmailContact()
    {
        var client = await AuthClientAsync();

        var response = await SendAsync(client, InvoiceMain, new SendInvoiceEmailDto(), "default-recipient");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        SentFor("default-recipient")!.To.Mailboxes.Single().Address.ShouldBe("invoices@customer-a.cz");
    }

    [Fact]
    public async Task Send_WithoutRecipient_ClientHasNoEmail_Returns400WithAClearMessage()
    {
        var client = await AuthClientAsync();

        var response = await SendAsync(client, InvoiceNoContacts, new SendInvoiceEmailDto(), "no-contact");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("No recipient");
        SentFor("no-contact").ShouldBeNull();
    }

    [Fact]
    public async Task Send_SaveAttachmentDefaults_PersistsOnTheClient_AndShowsUpInTheDefaultsEndpoint()
    {
        var client = await AuthClientAsync();

        var response = await SendAsync(client, InvoiceSave, new SendInvoiceEmailDto
        {
            RecipientEmail = "c@customer-c.cz", AttachPdf = false, AttachIsdoc = true, SaveAttachmentDefaults = true
        }, "save-defaults");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var stored = StoredSettings(CustomerForSave).ShouldNotBeNull();
        stored.EmailAttachPdf.ShouldBe(EEmailAttachMode.No);
        stored.EmailAttachIsdoc.ShouldBe(EEmailAttachMode.Inherit); // confirmed the default only

        var defaults = await client.GetFromJsonAsync<InvoiceEmailDefaultsDto>($"/api/invoice/{InvoiceSave}/email-defaults");
        defaults!.AttachPdf.ShouldBeFalse();
        defaults.AttachIsdoc.ShouldBeTrue();
    }

    // ─── GET /api/invoice/{id}/email-defaults ──────────────────────────────────

    [Fact]
    public async Task Defaults_HaveTheShapeTheDialogNeeds()
    {
        var client = await AuthClientAsync();

        var response = await client.GetAsync($"/api/invoice/{InvoiceMain}/email-defaults");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var defaults = (await response.Content.ReadFromJsonAsync<InvoiceEmailDefaultsDto>()).ShouldNotBeNull();
        defaults.Recipients.Select(r => r.Email).ShouldBe(["invoices@customer-a.cz", "general@customer-a.cz"]);
        defaults.Recipients[0].IsInvoiceEmail.ShouldBeTrue();
        defaults.Recipients[1].IsPrimary.ShouldBeTrue();
        defaults.Recipients[1].Label.ShouldBe("Office");
        defaults.AttachPdf.ShouldBeTrue();
        defaults.AttachIsdoc.ShouldBeTrue();
        defaults.AttachUbl.ShouldBeFalse();
        defaults.UblIsAutomatic.ShouldBeTrue();
        defaults.UblAutoWouldAttach.ShouldBeFalse();
        defaults.StoredAttachments.Select(a => (a.Id, a.OriginalFileName)).ShouldBe([(StoredOwn, "contract.pdf")]);
        defaults.DefaultSubject.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Defaults_UnknownInvoice_Returns404()
    {
        var client = await AuthClientAsync();

        (await client.GetAsync("/api/invoice/987654/email-defaults")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Defaults_FollowTheClientSettingsEditedThroughTheClientApi_IncludingReset()
    {
        var client = await AuthClientAsync();

        // Seeded: PDF = No, UBL = Yes
        var before = await client.GetFromJsonAsync<InvoiceEmailDefaultsDto>($"/api/invoice/{InvoiceReset}/email-defaults");
        before!.AttachPdf.ShouldBeFalse();
        before.AttachUbl.ShouldBeTrue();
        before.UblIsAutomatic.ShouldBeFalse();

        // Partial update: only PDF is sent (Inherit = reset); UBL must be kept.
        var update = await client.PutAsJsonAsync($"/api/client/{CustomerForReset}", new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto { EmailAttachPdf = EEmailAttachMode.Inherit }
        });
        update.StatusCode.ShouldBe(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        var after = await client.GetFromJsonAsync<InvoiceEmailDefaultsDto>($"/api/invoice/{InvoiceReset}/email-defaults");
        after!.AttachPdf.ShouldBeTrue();    // back to the system default
        after.AttachUbl.ShouldBeTrue();     // untouched
    }

    // ─── Bulk send ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkSend_UsesEachClientsDefaultContact_AndReportsAClientWithoutEmail()
    {
        var client = await AuthClientAsync();
        var before = _factory.Sink.Messages.Count;

        var response = await client.PostAsJsonAsync("/api/invoice/bulk/send-email",
            new BulkOperationRequest { InvoiceIds = [InvoiceOther, InvoiceNoContacts] });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = (await response.Content.ReadFromJsonAsync<BulkOperationResult>()).ShouldNotBeNull();
        result.SuccessCount.ShouldBe(1);
        result.FailedCount.ShouldBe(1);
        result.Errors.Single().InvoiceId.ShouldBe(InvoiceNoContacts);

        var sent = _factory.Sink.Messages.Skip(before).Single();
        sent.To.Mailboxes.Single().Address.ShouldBe("invoices@customer-a.cz");
        Names(sent).ShouldBe(["Faktura_EM2.pdf", "Faktura_EM2.isdoc"]); // client defaults, no per-send overrides
    }
}
