// ============================================================================
// CompanyControllerUpdateSettingsTests — coverage for issue #184.
//
// CompanyController.UpdateSettings assigned MaxUsers and AdminNotes unconditionally
// (settings.MaxUsers = dto.MaxUsers; settings.AdminNotes = dto.AdminNotes;) while every
// other field on the same DTO (SMTP, AI, EPO) used the partial-update pattern
// (if (dto.X != null) settings.X = dto.X;). /my-company only edits SMTP/AI/EPO sections and
// never sends MaxUsers/AdminNotes — so every save from there silently zeroed both, even though
// they are edited elsewhere (SysAdmin's /company-settings).
//
// Expected behaviour after the fix (issue #184): a request that omits MaxUsers/AdminNotes
// leaves the stored values unchanged, for all three sections (SMTP, AI, EPO), matching how
// every other field on this DTO already behaves.
// ============================================================================

using AresService;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.CompanySettings;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class CompanyControllerUpdateSettingsTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly CompanyController _controller;

    public CompanyControllerUpdateSettingsTests()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new MasterDbContext(options);

        // Pass-through fake — the real ICredentialProtector needs ASP.NET Core Data Protection
        // key ring set up via DI, which is unnecessary ceremony for a test about which fields
        // get overwritten, not about encryption itself.
        var credentialProtector = Substitute.For<ICredentialProtector>();
        credentialProtector.Encrypt(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>());

        _controller = new CompanyController(
            Substitute.For<IAresService>(),
            Substitute.For<ITenantProvisioningService>(),
            Substitute.For<IEmailService>(),
            credentialProtector,
            _context,
            Substitute.For<ILogger<CompanyController>>());
    }

    public void Dispose() => _context.Dispose();

    /// <summary>
    /// Seeds a company with MaxUsers/AdminNotes already set by SysAdmin — the state
    /// /my-company's SMTP/AI/EPO saves must not disturb.
    /// </summary>
    private async Task<long> SeedCompanyWithSettingsAsync()
    {
        var company = new Client
        {
            CompanyName = "Test Company s.r.o.", RegistrationNumber = "12345678",
            IsIssuer = true, IsActive = true
        };
        _context.Client.Add(company);
        await _context.SaveChangesAsync();

        _context.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = company.Id,
            SchemaName = $"tenant_{company.Id}",
            MaxUsers = 5,
            AdminNotes = "SysAdmin note: negotiated a 5-user cap on 2026-01-15.",
            IsProvisioned = true,
            IsActive = true
        });
        await _context.SaveChangesAsync();

        return company.Id;
    }

    /// <summary>
    /// Regression test for issue #184, SMTP section: saving SMTP settings from /my-company
    /// (which never sends MaxUsers/AdminNotes) must not touch either field.
    /// </summary>
    [Fact]
    public async Task UpdateSettings_SmtpOnly_DoesNotChangeMaxUsersOrAdminNotes()
    {
        var companyId = await SeedCompanyWithSettingsAsync();

        var result = await _controller.UpdateSettings(companyId, new UpdateCompanySystemSettingsDto
        {
            SmtpHost = "smtp.example.com",
            SmtpPort = 587,
            SmtpSenderEmail = "invoices@example.com"
        });

        var dto = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<CompanySystemSettingsDto>();
        dto.MaxUsers.ShouldBe(5);
        dto.AdminNotes.ShouldBe("SysAdmin note: negotiated a 5-user cap on 2026-01-15.");
    }

    /// <summary>Same guarantee for the AI section.</summary>
    [Fact]
    public async Task UpdateSettings_AiOnly_DoesNotChangeMaxUsersOrAdminNotes()
    {
        var companyId = await SeedCompanyWithSettingsAsync();

        var result = await _controller.UpdateSettings(companyId, new UpdateCompanySystemSettingsDto
        {
            AiDefaultProvider = "Claude",
            AiClaudeModel = "claude-sonnet-5"
        });

        var dto = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<CompanySystemSettingsDto>();
        dto.MaxUsers.ShouldBe(5);
        dto.AdminNotes.ShouldBe("SysAdmin note: negotiated a 5-user cap on 2026-01-15.");
    }

    /// <summary>Same guarantee for the EPO section.</summary>
    [Fact]
    public async Task UpdateSettings_EpoOnly_DoesNotChangeMaxUsersOrAdminNotes()
    {
        var companyId = await SeedCompanyWithSettingsAsync();

        var result = await _controller.UpdateSettings(companyId, new UpdateCompanySystemSettingsDto
        {
            EpoTaxOfficeCode = 451,
            EpoTaxOfficeBranchCode = 2017,
            EpoContactEmail = "tax@example.com"
        });

        var dto = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<CompanySystemSettingsDto>();
        dto.MaxUsers.ShouldBe(5);
        dto.AdminNotes.ShouldBe("SysAdmin note: negotiated a 5-user cap on 2026-01-15.");
    }

    /// <summary>
    /// The field is still genuinely updatable — /company-settings (SysAdmin) DOES send it, and
    /// that must keep working exactly as before.
    /// </summary>
    [Fact]
    public async Task UpdateSettings_WithMaxUsersAndAdminNotes_UpdatesThem()
    {
        var companyId = await SeedCompanyWithSettingsAsync();

        var result = await _controller.UpdateSettings(companyId, new UpdateCompanySystemSettingsDto
        {
            MaxUsers = 12,
            AdminNotes = "Raised to 12 users on renewal."
        });

        var dto = result.Result.ShouldBeOfType<OkObjectResult>().Value
            .ShouldBeOfType<CompanySystemSettingsDto>();
        dto.MaxUsers.ShouldBe(12);
        dto.AdminNotes.ShouldBe("Raised to 12 users on renewal.");
    }
}
