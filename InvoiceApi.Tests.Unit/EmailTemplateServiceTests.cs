using InvoiceApi.Application.Dto.ContentTemplate;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Unit tests for ContentTemplateService — the unified content template management service.
/// Tests CRUD operations, type-based filtering, default-template handling,
/// soft-delete behaviour, and Handlebars-style placeholder rendering.
///
/// Each test gets its own in-memory database (via Guid.NewGuid()) so tests
/// never interfere with each other, even when run in parallel.
/// </summary>
public class ContentTemplateServiceTests : IDisposable
{
    // The in-memory tenant database context shared by the service under test
    private readonly TenantDbContext _context;

    // The in-memory master database context (required by multi-tenant service constructor)
    private readonly MasterDbContext _masterContext;

    // The service we are testing
    private readonly ContentTemplateService _service;

    /// <summary>
    /// Constructor runs before every single test method.
    /// It sets up a fresh in-memory database and seeds it with test data.
    /// </summary>
    public ContentTemplateServiceTests()
    {
        // Create an in-memory database with a unique name so each test is fully isolated
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        // Create a separate in-memory master database (required by multi-tenant constructor)
        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _masterContext = new MasterDbContext(masterOptions);

        // Mock ITenantResolver to simulate a tenant context — returns companyId 1L
        // so the service uses TenantDbContext (existing test behavior)
        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(1L);

        // Create a mock logger — we don't verify log calls, we just need the dependency satisfied
        var logger = Substitute.For<ILogger<ContentTemplateService>>();

        // Instantiate the service under test with real DbContexts + mock tenant resolver + mock logger
        _service = new ContentTemplateService(_context, _masterContext, tenantResolver, logger);

        // Seed the database with known test data (see SeedTestData for details)
        SeedTestData();
    }

    /// <summary>
    /// Dispose runs after every single test method.
    /// It removes the in-memory database and releases the DbContext.
    /// </summary>
    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }

    /// <summary>
    /// Seeds the in-memory database with four content templates covering different
    /// template types, default flags, and active/inactive states.
    ///
    /// Id=1: InvoiceEmail, default, active, with subject + body containing placeholders
    /// Id=2: InvitationEmail, default, active, with subject + body containing placeholders
    /// Id=3: InvoiceEmail, NOT default, INACTIVE (for testing inactive filtering)
    /// Id=4: InvoicePdf, default, active, Subject is NULL (PDF templates have no subject)
    /// </summary>
    private void SeedTestData()
    {
        _context.ContentTemplate.AddRange(
            new ContentTemplate
            {
                Id = 1,
                Name = "Invoice Email Template",
                Subject = "Invoice {{InvoiceNumber}}",
                HtmlBody = "<p>Invoice {{InvoiceNumber}} for {{CompanyName}}</p>",
                TemplateType = EContentTemplateType.InvoiceEmail,
                IsDefault = true,
                IsActive = true,
                Description = "Default email template for invoices"
            },
            new ContentTemplate
            {
                Id = 2,
                Name = "Invitation Email Template",
                Subject = "Welcome {{FullName}}",
                HtmlBody = "<p>Hello {{FullName}}, click <a href='{{InvitationLink}}'>here</a> to join</p>",
                TemplateType = EContentTemplateType.InvitationEmail,
                IsDefault = true,
                IsActive = true,
                Description = "Default invitation email"
            },
            new ContentTemplate
            {
                Id = 3,
                Name = "Archived Invoice Email",
                Subject = "Old Invoice",
                HtmlBody = "<p>Old template body</p>",
                TemplateType = EContentTemplateType.InvoiceEmail,
                IsDefault = false,
                IsActive = false, // Inactive — should be excluded by default queries
                Description = "Archived template for testing inactive filter"
            },
            new ContentTemplate
            {
                Id = 4,
                Name = "Invoice PDF Template",
                Subject = null, // PDF templates do not have an email subject
                HtmlBody = "<html>{{DocumentNumber}}</html>",
                TemplateType = EContentTemplateType.InvoicePdf,
                IsDefault = true,
                IsActive = true,
                Description = "Default PDF template for invoices"
            }
        );
        _context.SaveChanges();
    }

    // ========================================================================
    // GetAllAsync tests
    // ========================================================================

    /// <summary>
    /// By default, GetAllAsync should exclude inactive templates (IsActive == false).
    /// We seeded 4 templates but Id=3 is inactive, so we expect 3 results.
    /// </summary>
    [Fact]
    public async Task GetAllAsync_ExcludesInactive_ByDefault()
    {
        // Act — call without parameters (includeInactive defaults to false)
        var result = await _service.GetAllAsync();

        // Assert — only the 3 active templates should be returned
        result.Count.ShouldBe(3);
        result.ShouldAllBe(t => t.IsActive);
    }

    /// <summary>
    /// When includeInactive is true, GetAllAsync should return ALL templates,
    /// including the ones with IsActive == false.
    /// </summary>
    [Fact]
    public async Task GetAllAsync_IncludesInactive_WhenRequested()
    {
        // Act — explicitly request inactive templates
        var result = await _service.GetAllAsync(includeInactive: true);

        // Assert — all 4 seeded templates should be returned
        result.Count.ShouldBe(4);
    }

    // ========================================================================
    // GetAllByTypeAsync tests
    // ========================================================================

    /// <summary>
    /// GetAllByTypeAsync should only return templates that match the requested type.
    /// For InvoiceEmail, we have Id=1 (active) and Id=3 (inactive, excluded by default).
    /// </summary>
    [Fact]
    public async Task GetAllByTypeAsync_ReturnsOnlyMatchingType()
    {
        // Act — get only InvoiceEmail templates (excludes inactive by default)
        var result = await _service.GetAllByTypeAsync(EContentTemplateType.InvoiceEmail);

        // Assert — only Id=1 should come back (Id=3 is inactive)
        result.Count.ShouldBe(1);
        result.ShouldAllBe(t => t.TemplateType == EContentTemplateType.InvoiceEmail);
        result[0].Name.ShouldBe("Invoice Email Template");
    }

    // ========================================================================
    // GetByIdAsync tests
    // ========================================================================

    /// <summary>
    /// GetByIdAsync should return the correct template when a valid ID is provided.
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsTemplate()
    {
        // Act
        var result = await _service.GetByIdAsync(1);

        // Assert — verify the returned DTO matches our seed data
        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Invoice Email Template");
        result.TemplateType.ShouldBe(EContentTemplateType.InvoiceEmail);
        result.IsDefault.ShouldBeTrue();
        result.IsActive.ShouldBeTrue();
    }

    /// <summary>
    /// GetByIdAsync should return null when the ID does not exist in the database.
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        // Act
        var result = await _service.GetByIdAsync(999);

        // Assert
        result.ShouldBeNull();
    }

    // ========================================================================
    // GetDefaultByTypeAsync tests
    // ========================================================================

    /// <summary>
    /// GetDefaultByTypeAsync should return the default template for a given type.
    /// For InvoiceEmail, Id=1 is the default.
    /// </summary>
    [Fact]
    public async Task GetDefaultByTypeAsync_ReturnsDefaultForType()
    {
        // Act
        var result = await _service.GetDefaultByTypeAsync(EContentTemplateType.InvoiceEmail);

        // Assert
        result.ShouldNotBeNull();
        result!.IsDefault.ShouldBeTrue();
        result.TemplateType.ShouldBe(EContentTemplateType.InvoiceEmail);
        result.Name.ShouldBe("Invoice Email Template");
    }

    /// <summary>
    /// GetDefaultByTypeAsync should return null when no default template
    /// is configured for the requested type (e.g., ReminderEmail has no seed data).
    /// </summary>
    [Fact]
    public async Task GetDefaultByTypeAsync_NoDefault_ReturnsNull()
    {
        // Act — ReminderEmail was not seeded
        var result = await _service.GetDefaultByTypeAsync(EContentTemplateType.ReminderEmail);

        // Assert
        result.ShouldBeNull();
    }

    // ========================================================================
    // CreateAsync tests
    // ========================================================================

    /// <summary>
    /// CreateAsync should persist a new template and return it as a DTO with a valid ID.
    /// </summary>
    [Fact]
    public async Task CreateAsync_ValidDto_ReturnsNewTemplate()
    {
        // Arrange — prepare the DTO for a new ReminderEmail template
        var createDto = new CreateContentTemplateDto
        {
            Name = "Reminder Email",
            Subject = "Payment Reminder for {{InvoiceNumber}}",
            HtmlBody = "<p>Please pay invoice {{InvoiceNumber}}</p>",
            TemplateType = EContentTemplateType.ReminderEmail,
            IsDefault = true,
            Description = "Default reminder email"
        };

        // Act
        var result = await _service.CreateAsync(createDto);

        // Assert — the returned DTO should have an auto-generated ID and match input values
        result.ShouldNotBeNull();
        result.Id.ShouldBeGreaterThan(0);
        result.Name.ShouldBe("Reminder Email");
        result.Subject.ShouldBe("Payment Reminder for {{InvoiceNumber}}");
        result.TemplateType.ShouldBe(EContentTemplateType.ReminderEmail);
        result.IsDefault.ShouldBeTrue();
        result.IsActive.ShouldBeTrue();
        result.Description.ShouldBe("Default reminder email");
    }

    /// <summary>
    /// When creating a new template marked as default, the previous default
    /// for the same TemplateType should be unset (IsDefault = false).
    /// This ensures only one default template per type at any given time.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NewDefault_UnsetsOldDefault()
    {
        // Arrange — create a new InvoiceEmail default (Id=1 is currently the default)
        var createDto = new CreateContentTemplateDto
        {
            Name = "New Invoice Email Default",
            Subject = "New Invoice",
            HtmlBody = "<p>New default template</p>",
            TemplateType = EContentTemplateType.InvoiceEmail,
            IsDefault = true
        };

        // Act
        var newTemplate = await _service.CreateAsync(createDto);

        // Assert — the old default (Id=1) should no longer be the default
        var oldTemplate = await _service.GetByIdAsync(1);
        oldTemplate!.IsDefault.ShouldBeFalse();

        // The newly created template should be the new default
        newTemplate.IsDefault.ShouldBeTrue();
    }

    // ========================================================================
    // UpdateAsync tests
    // ========================================================================

    /// <summary>
    /// UpdateAsync should apply partial updates — only the fields provided in the DTO
    /// should change; everything else should remain as it was.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ValidUpdate_ReturnsUpdatedTemplate()
    {
        // Arrange — update only the Name and Subject of template Id=1
        var updateDto = new UpdateContentTemplateDto
        {
            Name = "Updated Invoice Template",
            Subject = "Updated Subject {{InvoiceNumber}}"
        };

        // Act
        var result = await _service.UpdateAsync(1, updateDto);

        // Assert — updated fields should reflect the new values
        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Updated Invoice Template");
        result.Subject.ShouldBe("Updated Subject {{InvoiceNumber}}");

        // Fields NOT in the update should remain unchanged
        result.TemplateType.ShouldBe(EContentTemplateType.InvoiceEmail);
        result.HtmlBody.ShouldContain("{{InvoiceNumber}}");
    }

    /// <summary>
    /// UpdateAsync should return null when the template ID does not exist.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_NonExistentId_ReturnsNull()
    {
        // Act
        var result = await _service.UpdateAsync(999, new UpdateContentTemplateDto { Name = "Does Not Matter" });

        // Assert
        result.ShouldBeNull();
    }

    // ========================================================================
    // DeleteAsync tests
    // ========================================================================

    /// <summary>
    /// DeleteAsync should perform a soft delete — set IsActive to false
    /// but keep the record in the database.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_ExistingId_SoftDeletes()
    {
        // Act — soft-delete template Id=1
        var deleted = await _service.DeleteAsync(1);

        // Assert — delete should succeed
        deleted.ShouldBeTrue();

        // The template should still exist in the database but be inactive
        var template = await _service.GetByIdAsync(1);
        template.ShouldNotBeNull();
        template!.IsActive.ShouldBeFalse();
    }

    /// <summary>
    /// DeleteAsync should return false when the template ID does not exist.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_NonExistentId_ReturnsFalse()
    {
        // Act
        var result = await _service.DeleteAsync(999);

        // Assert
        result.ShouldBeFalse();
    }

    // ========================================================================
    // RenderTemplateAsync tests
    // ========================================================================

    /// <summary>
    /// RenderTemplateAsync should replace all Handlebars-style placeholders
    /// ({{Key}}) in both subject and body with values from the dictionary.
    /// </summary>
    [Fact]
    public async Task RenderTemplateAsync_ReplacesPlaceholders()
    {
        // Arrange — provide values for all placeholders in template Id=1
        var placeholders = new Dictionary<string, string>
        {
            ["InvoiceNumber"] = "INV2026001",
            ["CompanyName"] = "TestCorp"
        };

        // Act — render template Id=1 (subject: "Invoice {{InvoiceNumber}}", body has both placeholders)
        var (subject, htmlBody) = await _service.RenderTemplateAsync(1, placeholders);

        // Assert — both subject and body should have their placeholders replaced
        subject.ShouldBe("Invoice INV2026001");
        htmlBody.ShouldBe("<p>Invoice INV2026001 for TestCorp</p>");
    }

    /// <summary>
    /// When a placeholder in the template has no matching key in the dictionary,
    /// it should be left as-is (e.g., {{CompanyName}} stays if not provided).
    /// This is the "unknown placeholder" behaviour.
    /// </summary>
    [Fact]
    public async Task RenderTemplateAsync_UnknownPlaceholders_LeftAsIs()
    {
        // Arrange — provide only InvoiceNumber, but NOT CompanyName
        var placeholders = new Dictionary<string, string>
        {
            ["InvoiceNumber"] = "INV001"
            // Note: CompanyName is intentionally NOT provided
        };

        // Act
        var (_, htmlBody) = await _service.RenderTemplateAsync(1, placeholders);

        // Assert — the known placeholder should be replaced, the unknown one stays
        htmlBody.ShouldContain("INV001");
        htmlBody.ShouldContain("{{CompanyName}}");
    }

    /// <summary>
    /// RenderTemplateAsync should throw KeyNotFoundException when the template ID
    /// does not exist in the database.
    /// </summary>
    [Fact]
    public async Task RenderTemplateAsync_NonExistentId_ThrowsKeyNotFoundException()
    {
        // Act & Assert — rendering a non-existent template should throw
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.RenderTemplateAsync(999, new Dictionary<string, string>()));
    }
}
