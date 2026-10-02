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
// The fix splits the contract by audience:
// - UpdateSettings (UpdateCompanySystemSettingsDto) no longer carries the pair at all, so an
//   SMTP/AI/EPO save cannot reach it.
// - UpdateAdminSettings (UpdateCompanyAdminSettingsDto) writes the pair exactly as sent, so the
//   SysAdmin can still lift a limit (MaxUsers = null) and clear the notes. A "null = keep" guard
//   on the shared DTO could not express "set to unlimited" for an int?.
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
    private const int SeededMaxUsers = 5;
    private const string SeededAdminNotes = "SysAdmin note: negotiated a 5-user cap on 2026-01-15.";

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
            MaxUsers = SeededMaxUsers,
            AdminNotes = SeededAdminNotes,
            IsProvisioned = true,
            IsActive = true
        });
        await _context.SaveChangesAsync();

        return company.Id;
    }

    /// <summary>
    /// Reads the row back from the store (AsNoTracking bypasses the entity the controller
    /// mutated), so the assertions prove the values were saved, not just set in memory.
    /// </summary>
    private Task<CompanySystemSettings> LoadStoredAsync(long companyId) =>
        _context.CompanySystemSettings.AsNoTracking().SingleAsync(s => s.CompanyId == companyId);

    // ─── /my-company sections: must not touch the SysAdmin pair ──────────────

    /// <summary>
    /// Regression test for issue #184, SMTP section: saving SMTP settings from /my-company
    /// (which never sends MaxUsers/AdminNotes) must save SMTP and not touch either field.
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

        result.Result.ShouldBeOfType<OkObjectResult>();
        var stored = await LoadStoredAsync(companyId);
        stored.SmtpHost.ShouldBe("smtp.example.com");
        stored.MaxUsers.ShouldBe(SeededMaxUsers);
        stored.AdminNotes.ShouldBe(SeededAdminNotes);
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

        result.Result.ShouldBeOfType<OkObjectResult>();
        var stored = await LoadStoredAsync(companyId);
        stored.AiClaudeModel.ShouldBe("claude-sonnet-5");
        stored.MaxUsers.ShouldBe(SeededMaxUsers);
        stored.AdminNotes.ShouldBe(SeededAdminNotes);
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

        result.Result.ShouldBeOfType<OkObjectResult>();
        var stored = await LoadStoredAsync(companyId);
        stored.EpoTaxOfficeCode.ShouldBe(451);
        stored.MaxUsers.ShouldBe(SeededMaxUsers);
        stored.AdminNotes.ShouldBe(SeededAdminNotes);
    }

    // ─── /company-settings dialog: SysAdmin pair, written as sent ────────────

    /// <summary>The SysAdmin can still change the limit and the notes.</summary>
    [Fact]
    public async Task UpdateAdminSettings_WithValues_UpdatesThem()
    {
        var companyId = await SeedCompanyWithSettingsAsync();

        var result = await _controller.UpdateAdminSettings(companyId, new UpdateCompanyAdminSettingsDto
        {
            MaxUsers = 12,
            AdminNotes = "Raised to 12 users on renewal."
        });

        result.Result.ShouldBeOfType<OkObjectResult>();
        var stored = await LoadStoredAsync(companyId);
        stored.MaxUsers.ShouldBe(12);
        stored.AdminNotes.ShouldBe("Raised to 12 users on renewal.");
    }

    /// <summary>
    /// Review finding on PR #415: an emptied "Max users" field sends null, which means
    /// "unlimited". A "null = keep" guard left the 5-user cap in place while the dialog
    /// reported success.
    /// </summary>
    [Fact]
    public async Task UpdateAdminSettings_MaxUsersNull_LiftsTheLimit()
    {
        var companyId = await SeedCompanyWithSettingsAsync();

        var result = await _controller.UpdateAdminSettings(companyId, new UpdateCompanyAdminSettingsDto
        {
            MaxUsers = null,
            AdminNotes = SeededAdminNotes
        });

        result.Result.ShouldBeOfType<OkObjectResult>();
        var stored = await LoadStoredAsync(companyId);
        stored.MaxUsers.ShouldBeNull();
        stored.AdminNotes.ShouldBe(SeededAdminNotes);
    }

    /// <summary>An emptied notes field (MudBlazor sends "") clears the stored note.</summary>
    [Fact]
    public async Task UpdateAdminSettings_EmptyAdminNotes_ClearsThem()
    {
        var companyId = await SeedCompanyWithSettingsAsync();

        var result = await _controller.UpdateAdminSettings(companyId, new UpdateCompanyAdminSettingsDto
        {
            MaxUsers = SeededMaxUsers,
            AdminNotes = ""
        });

        result.Result.ShouldBeOfType<OkObjectResult>();
        var stored = await LoadStoredAsync(companyId);
        stored.AdminNotes.ShouldBe("");
        stored.MaxUsers.ShouldBe(SeededMaxUsers);
    }
}
