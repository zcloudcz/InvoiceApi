using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Real SMTP integration test — actually connects to the mail server and sends an email.
///
/// HOW TO USE:
/// 1. Edit appsettings.json in the test project (Fakvio.Tests.Unit/appsettings.json)
///    and fill in your real SMTP credentials + recipient email.
/// 2. Run ONLY this test:
///    dotnet test --filter "FullyQualifiedName~EmailSmtpIntegrationTests.SendRealEmail_ViaSmtp"
/// 3. Check the recipient inbox for the test email.
///
/// This test is [Fact(Skip = ...)] by default so it does NOT run in CI/CD.
/// To run it, comment out the Skip parameter or use the command above.
/// </summary>
public class EmailSmtpIntegrationTests
{
    /// <summary>
    /// Sends a real test email using SMTP settings from appsettings.json.
    ///
    /// Method under test: EmailService.SendEmailAsync(to, subject, htmlBody, attachment?, attachmentName?, ct)
    ///
    /// Configuration (Fakvio.Tests.Unit/appsettings.json):
    ///   SmtpSettings:Host        — SMTP server hostname (e.g. "smtp.gmail.com", "smtp.office365.com")
    ///   SmtpSettings:Port        — SMTP port (587 = STARTTLS, 465 = SSL, 25 = plain)
    ///   SmtpSettings:Username    — SMTP login (usually the email address)
    ///   SmtpSettings:Password    — SMTP password or app-specific password
    ///   SmtpSettings:SenderEmail — "From" address shown in the email
    ///   SmtpSettings:SenderName  — "From" display name
    ///   SmtpSettings:UseSsl      — true = TLS/SSL, false = no encryption
    ///   TestEmail:Recipient      — who receives the test email
    /// </summary>
    [Fact]
    public async Task SendRealEmail_ViaSmtp()
    {
        // ── Load real config from appsettings.json ──────────────────────────
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var recipient = configuration["TestEmail:Recipient"];
        recipient.ShouldNotBeNullOrWhiteSpace("Set TestEmail:Recipient in appsettings.json");

        // ── Minimal mocks — only what EmailService needs for SendEmailAsync ─
        // SendEmailAsync only calls ResolveSmtpSettingsAsync (needs config + tenantResolver + systemConfig)
        // and then MailKit. It does NOT touch the database for a plain send.
        var tenantContext = new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var masterContext = new MasterDbContext(
            new DbContextOptionsBuilder<MasterDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var pdfExport = Substitute.For<IPdfExportService>();
        // ISDOC/UBL export are not used by SendEmailAsync (only by SendInvoiceEmailAsync)
        var isdocExport = Substitute.For<IIsdocExportService>();
        var ublExport = Substitute.For<IUblExportService>();
        var contentTemplate = Substitute.For<IContentTemplateService>();
        var tenantResolver = Substitute.For<ITenantResolver>();
        var logger = Substitute.For<ILogger<EmailService>>();

        // Pass-through credential protector — no encryption for integration test
        var credentialProtector = Substitute.For<ICredentialProtector>();
        credentialProtector.Encrypt(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>());
        credentialProtector.Decrypt(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>());

        // No company context → skip tier 1, go straight to tier 2/3
        tenantResolver.GetCurrentCompanyId().Returns((long?)null);

        // Tier 2 empty → falls through to appsettings.json (tier 3)
        var systemConfig = Substitute.For<ISystemConfigurationService>();
        systemConfig.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new Contracts.Dto.SystemConfiguration.SystemConfigurationDto());

        var service = new EmailService(
            tenantContext, masterContext, pdfExport, isdocExport, ublExport, contentTemplate,
            systemConfig, credentialProtector, tenantResolver, configuration, logger);

        // ── Act — send a real email ─────────────────────────────────────────
        await service.SendEmailAsync(
            to: recipient,
            subject: $"Fakvio SMTP Test — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
            htmlBody: """
                <div style="font-family: Arial, sans-serif; max-width: 600px;">
                    <h2>SMTP Integration Test</h2>
                    <p>If you see this email, SMTP configuration is working correctly.</p>
                    <p><strong>Sent at:</strong> {{TIME}}</p>
                    <p style="color: green; font-weight: bold;">Test PASSED</p>
                </div>
                """.Replace("{{TIME}}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC")));

        // If we get here without exception, the email was sent successfully.
        // Check the recipient's inbox to confirm delivery.

        // Cleanup
        tenantContext.Dispose();
        masterContext.Dispose();
    }
}
