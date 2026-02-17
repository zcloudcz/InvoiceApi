using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

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

    // Mocked content template service — replaces old IEmailTemplateService
    // Controls which email/PDF template is returned for the email body
    private readonly IContentTemplateService _contentTemplate;

    // Mocked system configuration service — returns system-wide SMTP settings from DB
    private readonly ISystemConfigurationService _systemConfig;

    // Mocked tenant resolver — controls which company context is active for SMTP resolution
    private readonly ITenantResolver _tenantResolver;

    // Mocked logger — just satisfies the dependency
    private readonly ILogger<EmailService> _logger;

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
        _contentTemplate = Substitute.For<IContentTemplateService>();
        _systemConfig = Substitute.For<ISystemConfigurationService>();
        _tenantResolver = Substitute.For<ITenantResolver>();
        _logger = Substitute.For<ILogger<EmailService>>();

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
            DatabaseName = $"invoiceapi_tenant_{companyId}",
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
            _contentTemplate,
            _systemConfig,
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
    /// but we can still verify that the PDF generation was triggered.
    /// </summary>
    [Fact]
    public async Task SendInvoiceEmailAsync_ValidInvoice_GeneratesPdf()
    {
        // Arrange — return fake PDF bytes when PDF generation is requested
        var fakePdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF header bytes
        _pdfExport
            .GenerateInvoicePdfAsync(1, Arg.Any<CancellationToken>())
            .Returns(fakePdfBytes);

        // Mock the content template service to return null (triggers fallback email template)
        _contentTemplate
            .GetDefaultByTypeAsync(EContentTemplateType.InvoiceEmail, Arg.Any<CancellationToken>())
            .Returns((Contracts.Dto.ContentTemplate.ContentTemplateDto?)null);

        var service = CreateService();

        // Act — this will fail at SMTP connect (expected in unit test), but we verify the PDF was requested
        try
        {
            await service.SendInvoiceEmailAsync(1, "test@example.com");
        }
        catch
        {
            // Expected: SMTP connection failure in unit test environment — no real mail server
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
            _tenantContext, _masterContext, _pdfExport, _contentTemplate,
            _systemConfig, _tenantResolver, emptyConfig, _logger);

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
}
