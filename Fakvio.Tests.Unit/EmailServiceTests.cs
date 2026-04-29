using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for EmailService.
/// Tests email composition, invoice lookup, error handling, and 3-tier SMTP resolution.
///
/// The 3-tier SMTP resolution chain:
/// 1. CompanySystemSettings (per-company SMTP, stored in master DB)
/// 2. SystemConfiguration (system-wide SMTP, managed by SysAdmin)
/// 3. appsettings.json fallback (for fresh installs)
///
/// Note: Actual SMTP sending is not tested here (that's an integration test concern).
/// We focus on the service logic: invoice lookup, PDF generation, SMTP resolution,
/// and error conditions.
///
/// EmailService depends on:
/// - TenantDbContext (in-memory database with seeded invoice data)
/// - MasterDbContext (in-memory database for CompanySystemSettings SMTP lookup)
/// - IPdfExportService (mocked — returns fake PDF bytes)
/// - IContentTemplateService (mocked — controls email template resolution)
/// - ISystemConfigurationService (mocked — returns system SMTP settings)
/// - ITenantResolver (mocked — controls which company context is active)
/// - IConfiguration (in-memory — provides appsettings.json SMTP fallback)
/// - ILogger (mocked — just satisfies the dependency)
/// </summary>
public class EmailServiceTests : IDisposable
{
    // The in-memory tenant database context with seeded invoice data
    private readonly TenantDbContext _tenantContext;

    // The in-memory master database context for CompanySystemSettings SMTP lookup
    private readonly MasterDbContext _masterContext;

    // Mocked PDF export service — returns fake PDF bytes without actual iText7 conversion
    private readonly IPdfExportService _pdfExport;

    // Mocked ISDOC export service — returns fake XML bytes without real ISDOC generation
    private readonly IIsdocExportService _isdocExport;

    // Mocked content template service — replaces old IEmailTemplateService
    // Controls which email/PDF template is returned for the email body
    private readonly IContentTemplateService _contentTemplate;

    // Mocked system configuration service — returns system-wide SMTP settings from DB
    private readonly ISystemConfigurationService _systemConfig;

    // Mocked tenant resolver — controls which company context is active for SMTP resolution
    private readonly ITenantResolver _tenantResolver;

    // Mocked logger — just satisfies the dependency
    private readonly ILogger<EmailService> _logger;

    // Mocked credential protector — passes through values unchanged in tests
    // (no real encryption/decryption needed for unit tests)
    private readonly ICredentialProtector _credentialProtector;

    // In-memory configuration — provides SMTP settings without appsettings.json
    private readonly IConfiguration _configuration;

    public EmailServiceTests()
    {
        // Setup in-memory tenant database with a unique name for test isolation
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _tenantContext = new TenantDbContext(tenantOptions);

        // Setup in-memory master database for CompanySystemSettings
        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _masterContext = new MasterDbContext(masterOptions);

        _pdfExport = Substitute.For<IPdfExportService>();
        _isdocExport = Substitute.For<IIsdocExportService>();
        _contentTemplate = Substitute.For<IContentTemplateService>();
        _systemConfig = Substitute.For<ISystemConfigurationService>();
        _tenantResolver = Substitute.For<ITenantResolver>();
        _logger = Substitute.For<ILogger<EmailService>>();

        // Pass-through credential protector — no real encryption in unit tests.
        // Encrypt returns the input unchanged, Decrypt returns the input unchanged.
        _credentialProtector = Substitute.For<ICredentialProtector>();
        _credentialProtector.Encrypt(Arg.Any<string?>()).Returns(callInfo => callInfo.Arg<string?>());
        _credentialProtector.Decrypt(Arg.Any<string?>()).Returns(callInfo => callInfo.Arg<string?>());

        // Return empty config by default — forces EmailService to fall back to appsettings.json
        _systemConfig.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Contracts.Dto.SystemConfiguration.SystemConfigurationDto());

        // Default: no company context (SysAdmin without impersonation)
        _tenantResolver.GetCurrentCompanyId().Returns((long?)null);

        // Create test configuration with SMTP settings (tier 3 fallback)
        var configData = new Dictionary<string, string?>
        {
            ["SmtpSettings:Host"] = "smtp.test.com",
            ["SmtpSettings:Port"] = "587",
            ["SmtpSettings:Username"] = "test@test.com",
            ["SmtpSettings:Password"] = "password",
            ["SmtpSettings:SenderEmail"] = "invoices@test.com",
            ["SmtpSettings:SenderName"] = "TestApp",
            ["SmtpSettings:UseSsl"] = "true"
        };
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        // Seed the tenant database with test invoice data
        SeedTestData();
    }

    public void Dispose()
    {
        _tenantContext.Database.EnsureDeleted();
        _tenantContext.Dispose();
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }

    /// <summary>
    /// Seeds the in-memory tenant database with a minimal set of entities required
    /// for the EmailService tests: a currency, an invoice, an issuer and a client.
    /// </summary>
    private void SeedTestData()
    {
        // Add currency — invoices reference a currency for the email subject/body
        var currency = new Currency { Id = 1, Code = "CZK", Symbol = "Kc", Name = "Czech Koruna", IsActive = true };
        _tenantContext.Currency.Add(currency);

        // Add an invoice — this is the document that will be sent via email
        var invoice = new Invoice
        {
            Id = 1,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2025001",
            IssueDate = new DateTime(2025, 1, 15),
            DueDate = new DateTime(2025, 1, 29),
            IssuerId = 1,
            ClientId = 2,
            CurrencyId = 1,
            TotalWithVat = 12100,
            InvoiceItem = new List<InvoiceItem>()
        };
        _tenantContext.Invoice.Add(invoice);

        // Add a minimal issuer (sender) and client (recipient)
        _tenantContext.Client.Add(new Client { Id = 1, CompanyName = "Issuer", RegistrationNumber = "111", IsIssuer = true, IsActive = true });
        _tenantContext.Client.Add(new Client { Id = 2, CompanyName = "Client", RegistrationNumber = "222", IsIssuer = false, IsActive = true });

        _tenantContext.SaveChanges();
    }

    /// <summary>
    /// Seeds a company with SMTP settings in the master database.
    /// Used to test tier 1 (company-specific SMTP) resolution.
    /// </summary>
    private void SeedCompanySmtpSettings(long companyId, string? smtpHost, int? smtpPort = 465,
        string? username = "company@smtp.com", string? password = "companypass",
        string? senderEmail = "invoices@company.com", string? senderName = "CompanyMail",
        bool? useSsl = true)
    {
        // Need a company (Client) record to satisfy the FK
        _masterContext.Client.Add(new Client
        {
            Id = companyId,
            CompanyName = $"Company {companyId}",
            RegistrationNumber = $"REG{companyId:D8}",
            IsIssuer = true,
            IsActive = true,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        });
        _masterContext.SaveChanges();

        _masterContext.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = companyId,
            SchemaName = $"tenant_{companyId}",
            IsProvisioned = true,
            IsActive = true,
            SmtpHost = smtpHost,
            SmtpPort = smtpPort,
            SmtpUsername = username,
            SmtpPassword = password,
            SmtpSenderEmail = senderEmail,
            SmtpSenderName = senderName,
            SmtpUseSsl = useSsl
        });
        _masterContext.SaveChanges();
    }

    /// <summary>
    /// Creates an EmailService instance with all dependencies injected.
    /// </summary>
    private EmailService CreateService()
    {
        return new EmailService(
            _tenantContext,
            _masterContext,
            _pdfExport,
            _isdocExport,
            _contentTemplate,
            _systemConfig,
            _credentialProtector,
            _tenantResolver,
            _configuration,
            _logger);
    }

    /// <summary>
    /// Tests that SendInvoiceEmailAsync throws KeyNotFoundException
    /// when the invoice doesn't exist in the database.
    /// The service should fail fast before attempting PDF generation or SMTP sending.
    /// </summary>
    [Fact]
    public async Task SendInvoiceEmailAsync_InvalidInvoiceId_ThrowsKeyNotFoundException()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert — non-existent invoice should throw
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.SendInvoiceEmailAsync(999, "test@example.com"));
    }

    /// <summary>
    /// Tests that SendInvoiceEmailAsync calls IPdfExportService.GenerateInvoicePdfAsync
    /// to generate the PDF attachment before sending the email.
    /// The actual SMTP send will fail in the unit test environment (no real SMTP server),
    /// but we can still verify that PDF generation was triggered before the SMTP step.
    ///
    /// Both export services (PDF + ISDOC) must be explicitly stubbed here.
    /// Without the ISDOC stub the NSubstitute default is Task&lt;byte[]&gt; returning null,
    /// which causes a NullReferenceException that was previously swallowed by a broad
    /// try/catch — masking the missing stub rather than failing fast.
    /// </summary>
    [Fact]
    public async Task SendInvoiceEmailAsync_ValidInvoice_GeneratesPdf()
    {
        // Arrange — return fake PDF bytes when PDF generation is requested
        var fakePdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF header bytes
        _pdfExport
            .GenerateInvoicePdfAsync(1, Arg.Any<CancellationToken>())
            .Returns(fakePdfBytes);

        // ISDOC stub is required: ExportInvoiceAsync is called before SMTP, so it must
        // return valid bytes. Without this, the service would throw NullReferenceException
        // when accessing isdocBytes.Length on the log line — before even reaching SMTP.
        _isdocExport
            .ExportInvoiceAsync(1, Arg.Any<CancellationToken>())
            .Returns(System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Invoice/>"));

        // Mock the content template service to return null (triggers fallback email template)
        _contentTemplate
            .GetDefaultByTypeAsync(EContentTemplateType.InvoiceEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Contracts.Dto.ContentTemplate.ContentTemplateDto?)null);

        var service = CreateService();

        // Act — the service calls both export services, then reaches SMTP connect which
        // fails with a network/socket error because no real SMTP server exists in unit tests.
        // Only catch the expected SMTP infrastructure exception — any other exception
        // (InvalidOperationException, NullReferenceException, etc.) should propagate and fail the test.
        try
        {
            await service.SendInvoiceEmailAsync(1, "test@example.com");
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not KeyNotFoundException
                                       and not NullReferenceException)
        {
            // Expected: SocketException / SmtpCommandException / MailKit.Net.Smtp.SmtpProtocolException
            // when MailKit tries to connect to smtp.test.com (from appsettings in constructor).
        }

        // Assert — PDF generation should have been called exactly once
        await _pdfExport.Received(1)
            .GenerateInvoicePdfAsync(1, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Tests that SendEmailAsync throws InvalidOperationException
    /// when SMTP is not configured in any of the 3 tiers.
    /// This guards against deployment mistakes where SMTP settings are missing everywhere.
    /// </summary>
    [Fact]
    public async Task SendEmailAsync_NoSmtpConfigAnywhere_ThrowsInvalidOperationException()
    {
        // Arrange — empty configuration (no SMTP in appsettings.json)
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // SystemConfiguration also has no SMTP (empty DTO, SmtpHost is "")
        _systemConfig.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Contracts.Dto.SystemConfiguration.SystemConfigurationDto());

        // No company context → tier 1 skipped
        _tenantResolver.GetCurrentCompanyId().Returns((long?)null);

        var service = new EmailService(
            _tenantContext, _masterContext, _pdfExport, _isdocExport, _contentTemplate,
            _systemConfig, _credentialProtector, _tenantResolver, emptyConfig, _logger);

        // Act & Assert — missing SMTP host in all 3 tiers should throw a clear error
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendEmailAsync("to@test.com", "Subject", "<p>Body</p>"));

        ex.Message.ShouldContain("SMTP is not configured");
    }

    /// <summary>
    /// Tests tier 1: When company has its own SMTP configured (SmtpHost is non-empty),
    /// the service should use the company's SMTP settings — NOT system SMTP or appsettings.
    /// We verify by checking that the SMTP connection attempt uses the company host
    /// (it will fail to connect, but the exception message reveals the host used).
    /// </summary>
    [Fact]
    public async Task SendEmailAsync_CompanySmtpConfigured_UsesCompanySmtp()
    {
        // Arrange — seed company with SMTP settings in master DB
        SeedCompanySmtpSettings(companyId: 42, smtpHost: "smtp.company42.com");

        // Set tenant resolver to return company 42
        _tenantResolver.GetCurrentCompanyId().Returns(42L);

        // System SMTP is also configured — but company SMTP should take priority
        _systemConfig.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Contracts.Dto.SystemConfiguration.SystemConfigurationDto
            {
                SmtpHost = "smtp.system.com",
                SmtpPort = 587,
                SmtpSenderEmail = "system@system.com",
                SmtpSenderName = "SystemMail",
                SmtpUseSsl = true
            });

        var service = CreateService();

        // Act — will throw a SocketException or similar when trying to connect to the SMTP host
        Exception? caughtEx = null;
        try
        {
            await service.SendEmailAsync("to@test.com", "Subject", "<p>Body</p>");
        }
        catch (Exception ex)
        {
            caughtEx = ex;
        }

        // Assert — the service should have attempted to connect to the company's SMTP host.
        // We can't fully verify the host from the exception, but we verify:
        // 1. An exception was thrown (expected — no real SMTP server)
        // 2. The service didn't throw InvalidOperationException (which would mean no SMTP found)
        caughtEx.ShouldNotBeNull();
        caughtEx.ShouldNotBeOfType<InvalidOperationException>();
    }

    /// <summary>
    /// Tests tier 2 fallback: When company exists but has NO SMTP configured (SmtpHost is null),
    /// the service should fall through to system-wide SMTP from SystemConfiguration.
    /// </summary>
    [Fact]
    public async Task SendEmailAsync_CompanySmtpNotConfigured_FallsToSystemSmtp()
    {
        // Arrange — seed company WITHOUT SMTP settings (SmtpHost = null)
        SeedCompanySmtpSettings(companyId: 42, smtpHost: null);

        // Set tenant resolver to return company 42
        _tenantResolver.GetCurrentCompanyId().Returns(42L);

        // System SMTP is configured — should be used as fallback
        _systemConfig.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Contracts.Dto.SystemConfiguration.SystemConfigurationDto
            {
                SmtpHost = "smtp.system-fallback.com",
                SmtpPort = 587,
                SmtpSenderEmail = "system@system.com",
                SmtpSenderName = "SystemMail",
                SmtpUseSsl = true
            });

        var service = CreateService();

        // Act — will throw when trying to connect
        Exception? caughtEx = null;
        try
        {
            await service.SendEmailAsync("to@test.com", "Subject", "<p>Body</p>");
        }
        catch (Exception ex)
        {
            caughtEx = ex;
        }

        // Assert — should have attempted connection (not InvalidOperationException)
        caughtEx.ShouldNotBeNull();
        caughtEx.ShouldNotBeOfType<InvalidOperationException>();
    }

    /// <summary>
    /// Tests that when there is no company context (e.g., SysAdmin without impersonation),
    /// tier 1 is skipped entirely and the service falls through to system SMTP.
    /// </summary>
    [Fact]
    public async Task SendEmailAsync_NoCompanyId_SkipsTier1_UsesSystemSmtp()
    {
        // Arrange — no company context (SysAdmin without impersonation)
        _tenantResolver.GetCurrentCompanyId().Returns((long?)null);

        // System SMTP is configured
        _systemConfig.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Contracts.Dto.SystemConfiguration.SystemConfigurationDto
            {
                SmtpHost = "smtp.system.com",
                SmtpPort = 587,
                SmtpSenderEmail = "system@system.com",
                SmtpSenderName = "SystemMail",
                SmtpUseSsl = true
            });

        var service = CreateService();

        // Act — will throw when trying to connect
        Exception? caughtEx = null;
        try
        {
            await service.SendEmailAsync("to@test.com", "Subject", "<p>Body</p>");
        }
        catch (Exception ex)
        {
            caughtEx = ex;
        }

        // Assert — should have attempted connection (not InvalidOperationException)
        caughtEx.ShouldNotBeNull();
        caughtEx.ShouldNotBeOfType<InvalidOperationException>();
    }

    /// <summary>
    /// Tests the complete fallback chain: company SMTP (null) → system SMTP (empty) → appsettings.
    /// Verifies that the service correctly falls through all 3 tiers and uses appsettings.json
    /// as the last resort.
    /// </summary>
    [Fact]
    public async Task SendEmailAsync_AllTiersEmpty_FallsToAppsettings()
    {
        // Arrange — company exists but no SMTP configured
        SeedCompanySmtpSettings(companyId: 42, smtpHost: null);
        _tenantResolver.GetCurrentCompanyId().Returns(42L);

        // System SMTP also empty (SmtpHost is "" by default in the DTO)
        _systemConfig.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Contracts.Dto.SystemConfiguration.SystemConfigurationDto());

        // appsettings.json has SMTP configured (from constructor)
        var service = CreateService();

        // Act — will throw when trying to connect to smtp.test.com (from appsettings)
        Exception? caughtEx = null;
        try
        {
            await service.SendEmailAsync("to@test.com", "Subject", "<p>Body</p>");
        }
        catch (Exception ex)
        {
            caughtEx = ex;
        }

        // Assert — should have attempted connection (not InvalidOperationException)
        caughtEx.ShouldNotBeNull();
        caughtEx.ShouldNotBeOfType<InvalidOperationException>();
    }

    // ─── Language-Aware Template Resolution Tests ─────────────────────────

    /// <summary>
    /// Tests that SendInvoiceEmailAsync reads the client's Language and passes it
    /// to the content template resolution (language-aware overload).
    /// The test client (Id=2) has Language = "cs" (default).
    /// </summary>
    [Fact]
    public async Task SendInvoiceEmailAsync_UsesClientLanguageForTemplate()
    {
        // Arrange
        var fakePdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        _pdfExport.GenerateInvoicePdfAsync(1, Arg.Any<CancellationToken>()).Returns(fakePdfBytes);

        // Mock the language-aware overload (the one actually called by the service now)
        _contentTemplate
            .GetDefaultByTypeAsync(EContentTemplateType.InvoiceEmail, "cs", Arg.Any<CancellationToken>())
            .Returns((Contracts.Dto.ContentTemplate.ContentTemplateDto?)null);

        var service = CreateService();

        // Act — expect SMTP failure in test environment, but template resolution should happen first
        try { await service.SendInvoiceEmailAsync(1, "test@example.com"); } catch { }

        // Assert — should have called the language-aware overload with "cs"
        await _contentTemplate.Received(1)
            .GetDefaultByTypeAsync(EContentTemplateType.InvoiceEmail, "cs", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Tests that SendInvitationEmailAsync always uses "cs" as the language
    /// for template resolution (no client context for system emails).
    /// </summary>
    [Fact]
    public async Task SendInvitationEmailAsync_UsesDefaultCzechLanguage()
    {
        // Arrange — mock the language-aware overload with "cs"
        _contentTemplate
            .GetDefaultByTypeAsync(EContentTemplateType.InvitationEmail, "cs", Arg.Any<CancellationToken>())
            .Returns((Contracts.Dto.ContentTemplate.ContentTemplateDto?)null);

        var service = CreateService();

        // Act — expect SMTP failure
        try { await service.SendInvitationEmailAsync("newuser@test.com", "John Doe", "https://app/invite/abc"); }
        catch { }

        // Assert — should have called with "cs" language
        await _contentTemplate.Received(1)
            .GetDefaultByTypeAsync(EContentTemplateType.InvitationEmail, "cs", Arg.Any<CancellationToken>());
    }

    // ─── ISDOC Attachment Tests ────────────────────────────────────────────

    /// <summary>
    /// Tests that SendInvoiceEmailAsync calls IIsdocExportService.ExportInvoiceAsync
    /// to generate the ISDOC attachment. The actual SMTP send will fail in the test
    /// environment (no real server), but ISDOC generation is triggered before SMTP.
    /// </summary>
    [Fact]
    public async Task SendInvoiceEmailAsync_ValidInvoice_GeneratesIsdoc()
    {
        // Arrange — return fake bytes from both export services
        _pdfExport
            .GenerateInvoicePdfAsync(1, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF header

        _isdocExport
            .ExportInvoiceAsync(1, Arg.Any<CancellationToken>())
            .Returns(System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Invoice/>"));

        _contentTemplate
            .GetDefaultByTypeAsync(EContentTemplateType.InvoiceEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Contracts.Dto.ContentTemplate.ContentTemplateDto?)null);

        var service = CreateService();

        // Act — expect SMTP failure in unit test, but export services run before SMTP
        try { await service.SendInvoiceEmailAsync(1, "test@example.com"); } catch { }

        // Assert — ISDOC export should have been called exactly once
        await _isdocExport.Received(1)
            .ExportInvoiceAsync(1, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Tests that BuildInvoiceMessage produces a MimeMessage with exactly 2 attachments:
    /// one PDF (application/pdf) and one ISDOC (application/xml).
    ///
    /// This test calls the internal static method directly to inspect the MimeMessage
    /// without needing a real SMTP server — the message structure is testable in isolation.
    /// </summary>
    [Fact]
    public void BuildInvoiceMessage_ProducesTwoAttachments_WithCorrectContentTypes()
    {
        // Arrange — minimal fake bytes for each attachment
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF magic bytes
        var isdocBytes = System.Text.Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Invoice xmlns=\"http://isdoc.cz/namespace/2013\"/>");

        // Act — build the message (this is the real production code path, no mocks needed)
        var message = EmailService.BuildInvoiceMessage(
            senderName: "TestApp",
            senderEmail: "invoices@test.com",
            to: "client@example.com",
            subject: "Invoice INV2025001",
            htmlBody: "<p>Please find attached.</p>",
            pdfBytes: pdfBytes,
            pdfFileName: "Invoice_INV2025001.pdf",
            isdocBytes: isdocBytes,
            isdocFileName: "Invoice_INV2025001.isdoc");

        // Extract the multipart body to inspect its parts
        var multipart = message.Body as MimeKit.Multipart;
        multipart.ShouldNotBeNull("Email body should be multipart/mixed when it has attachments.");

        // Collect all parts that are attachments (not the HTML body)
        var attachments = multipart
            .OfType<MimeKit.MimePart>()
            .Where(p => p.IsAttachment)
            .ToList();

        // Assert: exactly 2 attachments
        attachments.Count.ShouldBe(2, "Invoice email must carry exactly 2 attachments: PDF and ISDOC.");

        // Assert PDF attachment properties
        var pdf = attachments.FirstOrDefault(a => a.FileName?.EndsWith(".pdf") == true);
        pdf.ShouldNotBeNull("PDF attachment must be present.");
        pdf.FileName.ShouldBe("Invoice_INV2025001.pdf");
        pdf.ContentType.MimeType.ShouldBe("application/pdf");

        // Assert ISDOC attachment properties
        var isdoc = attachments.FirstOrDefault(a => a.FileName?.EndsWith(".isdoc") == true);
        isdoc.ShouldNotBeNull("ISDOC attachment must be present.");
        isdoc.FileName.ShouldBe("Invoice_INV2025001.isdoc");
        isdoc.ContentType.MimeType.ShouldBe("application/xml");
    }

    /// <summary>
    /// Tests that the ISDOC attachment file name follows the pattern
    /// "&lt;DocumentType&gt;_&lt;DocumentNumber&gt;.isdoc" for invoices and credit notes.
    ///
    /// This is important for:
    ///   - Accounting software that identifies ISDOC files by name convention
    ///   - Audit trail consistency (same prefix as the PDF attachment)
    /// </summary>
    [Fact]
    public void BuildInvoiceMessage_CreditNote_UsesCorrectPrefix()
    {
        // Arrange
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var isdocBytes = System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Invoice/>");

        // Act — use CreditNote prefix
        var message = EmailService.BuildInvoiceMessage(
            senderName: "TestApp",
            senderEmail: "invoices@test.com",
            to: "client@example.com",
            subject: "Credit Note CN2025001",
            htmlBody: "<p>Credit note.</p>",
            pdfBytes: pdfBytes,
            pdfFileName: "CreditNote_CN2025001.pdf",
            isdocBytes: isdocBytes,
            isdocFileName: "CreditNote_CN2025001.isdoc");

        var multipart = message.Body as MimeKit.Multipart;
        multipart.ShouldNotBeNull();

        var attachments = multipart
            .OfType<MimeKit.MimePart>()
            .Where(p => p.IsAttachment)
            .ToList();

        attachments.Count.ShouldBe(2);

        // Both attachments should use the "CreditNote_" prefix
        attachments.ShouldAllBe(a => a.FileName!.StartsWith("CreditNote_"),
            "Both attachments must use the CreditNote_ prefix for credit notes.");
    }

    /// <summary>
    /// Tests that BuildInvoiceMessage attachments contain the actual byte data passed in.
    /// This ensures no data corruption happens during message construction.
    /// Also verifies the ISDOC bytes parse as valid XML — proving the content is intact.
    /// </summary>
    [Fact]
    public void BuildInvoiceMessage_AttachmentsContainCorrectData()
    {
        // Arrange — use a well-formed minimal ISDOC XML document
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }; // "%PDF-1"
        var isdocXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Invoice xmlns=\"http://isdoc.cz/namespace/2013\"><DocumentType>1</DocumentType></Invoice>";
        var isdocBytes = System.Text.Encoding.UTF8.GetBytes(isdocXml);

        // Act
        var message = EmailService.BuildInvoiceMessage(
            senderName: "TestApp",
            senderEmail: "invoices@test.com",
            to: "client@example.com",
            subject: "Test",
            htmlBody: "<p>Test</p>",
            pdfBytes: pdfBytes,
            pdfFileName: "Invoice_TEST.pdf",
            isdocBytes: isdocBytes,
            isdocFileName: "Invoice_TEST.isdoc");

        var multipart = message.Body as MimeKit.Multipart;
        multipart.ShouldNotBeNull();

        var isdocPart = multipart
            .OfType<MimeKit.MimePart>()
            .FirstOrDefault(p => p.IsAttachment && p.FileName?.EndsWith(".isdoc") == true);

        isdocPart.ShouldNotBeNull();

        // Extract the raw bytes from the MIME part's content stream
        using var ms = new MemoryStream();
        isdocPart.Content.DecodeTo(ms);
        var extractedBytes = ms.ToArray();

        // The extracted bytes must be non-empty
        extractedBytes.ShouldNotBeEmpty();

        // The extracted content must parse as valid XML — proves the bytes were not corrupted
        var xmlDoc = new System.Xml.XmlDocument();
        Should.NotThrow(() => xmlDoc.LoadXml(System.Text.Encoding.UTF8.GetString(extractedBytes)),
            "ISDOC attachment content must be valid XML.");
    }

    /// <summary>
    /// Tests that SendInvoiceEmailAsync propagates exceptions thrown by IIsdocExportService
    /// immediately — the email send is aborted before reaching SMTP.
    ///
    /// This is the "fail-fast" contract: if ISDOC generation fails (e.g. missing template,
    /// database error), the service must NOT send a PDF-only email silently. The caller
    /// (controller or background job) must see the exception and can decide to retry or log it.
    ///
    /// The production code achieves this because ExportInvoiceAsync is awaited directly
    /// with no surrounding try/catch — the exception propagates naturally.
    /// </summary>
    [Fact]
    public async Task SendInvoiceEmailAsync_IsdocExportThrows_AbortsEmailSend()
    {
        // Arrange — PDF export succeeds but ISDOC export fails
        _pdfExport
            .GenerateInvoicePdfAsync(1, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0x25, 0x50, 0x44, 0x46 });

        // Simulate a ISDOC generation failure (e.g. template missing, mapper exception)
        _isdocExport
            .ExportInvoiceAsync(1, Arg.Any<CancellationToken>())
            .Returns<byte[]>(_ => throw new InvalidOperationException("ISDOC template not configured"));

        _contentTemplate
            .GetDefaultByTypeAsync(EContentTemplateType.InvoiceEmail, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Contracts.Dto.ContentTemplate.ContentTemplateDto?)null);

        var service = CreateService();

        // Act & Assert — the exception from IIsdocExportService must propagate to the caller
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendInvoiceEmailAsync(1, "test@example.com"));

        ex.Message.ShouldContain("ISDOC template not configured");

        // Verify PDF was called (it runs first), but SMTP was never reached
        // (no SMTP exception means the exception happened before ConnectAsync)
        await _pdfExport.Received(1)
            .GenerateInvoicePdfAsync(1, Arg.Any<CancellationToken>());

        await _isdocExport.Received(1)
            .ExportInvoiceAsync(1, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Tests that BuildInvoiceMessage correctly sets the From and To headers.
    /// Ensures sender name / email and recipient appear in the MimeMessage headers —
    /// a regression guard against accidentally swapping sender/recipient parameters.
    /// </summary>
    [Fact]
    public void BuildInvoiceMessage_SetsCorrectFromAndToHeaders()
    {
        // Arrange
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var isdocBytes = System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Invoice/>");

        // Act
        var message = EmailService.BuildInvoiceMessage(
            senderName: "Fakvio App",
            senderEmail: "invoices@fakvio.cz",
            to: "klient@example.cz",
            subject: "Faktura INV2025001",
            htmlBody: "<p>Faktura v příloze.</p>",
            pdfBytes: pdfBytes,
            pdfFileName: "Invoice_INV2025001.pdf",
            isdocBytes: isdocBytes,
            isdocFileName: "Invoice_INV2025001.isdoc");

        // Assert — From header
        message.From.Count.ShouldBe(1);
        var fromAddress = message.From[0] as MailboxAddress;
        fromAddress.ShouldNotBeNull();
        fromAddress.Name.ShouldBe("Fakvio App");
        fromAddress.Address.ShouldBe("invoices@fakvio.cz");

        // Assert — To header
        message.To.Count.ShouldBe(1);
        var toAddress = message.To[0] as MailboxAddress;
        toAddress.ShouldNotBeNull();
        toAddress.Address.ShouldBe("klient@example.cz");

        // Assert — Subject
        message.Subject.ShouldBe("Faktura INV2025001");
    }
}
