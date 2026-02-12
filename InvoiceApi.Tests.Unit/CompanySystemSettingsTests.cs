using Shouldly;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Tests for CompanySystemSettings entity — validates EF Core configuration,
/// constraints, and relationships in the MasterDbContext.
///
/// CompanySystemSettings stores multi-tenant infrastructure config:
/// database name, connection string, provisioning status, etc.
/// Each company (Client with IsIssuer = true) gets exactly one record.
/// </summary>
public class CompanySystemSettingsTests : IDisposable
{
    private readonly MasterDbContext _context;

    public CompanySystemSettingsTests()
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new MasterDbContext(options);
    }

    /// <summary>
    /// Seeds a company (Client with IsIssuer = true) for FK relationship testing.
    /// </summary>
    private async Task<Client> SeedCompanyAsync(long id = 1)
    {
        var client = new Client
        {
            Id = id,
            CompanyName = $"Test Company {id}",
            RegistrationNumber = $"REG{id:D8}",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        };

        _context.Client.Add(client);
        await _context.SaveChangesAsync();
        return client;
    }

    [Fact]
    public async Task CanCreate_CompanySystemSettings_WithAllFields()
    {
        // Arrange
        await SeedCompanyAsync(1);

        var settings = new CompanySystemSettings
        {
            CompanyId = 1,
            DatabaseName = "invoiceapi_tenant_1",
            ConnectionString = null,
            IsProvisioned = true,
            IsActive = true,
            ProvisionedAt = DateTime.UtcNow,
            MaxUsers = 10,
            AdminNotes = "Premium tier"
        };

        // Act
        _context.CompanySystemSettings.Add(settings);
        await _context.SaveChangesAsync();

        // Assert
        var saved = await _context.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == 1);
        saved.ShouldNotBeNull();
        saved!.DatabaseName.ShouldBe("invoiceapi_tenant_1");
        saved.IsProvisioned.ShouldBeTrue();
        saved.IsActive.ShouldBeTrue();
        saved.MaxUsers.ShouldBe(10);
        saved.AdminNotes.ShouldBe("Premium tier");
    }

    [Fact]
    public async Task CompanySystemSettings_DefaultValues_AreCorrect()
    {
        // Arrange
        await SeedCompanyAsync(2);

        var settings = new CompanySystemSettings
        {
            CompanyId = 2,
            DatabaseName = "invoiceapi_tenant_2"
        };

        // Act
        _context.CompanySystemSettings.Add(settings);
        await _context.SaveChangesAsync();

        // Assert — check default values
        var saved = await _context.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == 2);
        saved.ShouldNotBeNull();
        saved!.IsActive.ShouldBeTrue(); // Default: active
        saved.IsProvisioned.ShouldBeFalse(); // Default: not provisioned
        saved.ConnectionString.ShouldBeNull(); // Default: use template
        saved.ProvisionedAt.ShouldBeNull(); // Default: not provisioned
        saved.MaxUsers.ShouldBeNull(); // Default: unlimited
        saved.AdminNotes.ShouldBeNull(); // Default: no notes
    }

    [Fact]
    public void CompanySystemSettings_CompanyId_HasUniqueIndex()
    {
        // InMemoryDatabase doesn't enforce unique indexes at runtime,
        // so we verify the configuration exists by inspecting the EF model metadata.
        // A real database WILL enforce this constraint.
        var entityType = _context.Model.FindEntityType(typeof(CompanySystemSettings));
        var companyIdIndex = entityType?.GetIndexes()
            .FirstOrDefault(i => i.Properties.Any(p => p.Name == "CompanyId"));

        companyIdIndex.ShouldNotBeNull();
        companyIdIndex!.IsUnique.ShouldBeTrue();
    }

    [Fact]
    public void CompanySystemSettings_HasCorrectIndexes()
    {
        // Verify that all expected indexes are configured
        var entityType = _context.Model.FindEntityType(typeof(CompanySystemSettings));
        entityType.ShouldNotBeNull();

        var indexes = entityType!.GetIndexes().ToList();

        // Should have indexes on: CompanyId (unique), IsProvisioned, IsActive
        indexes.ShouldContain(i =>
            i.Properties.Any(p => p.Name == "CompanyId") && i.IsUnique);
        indexes.ShouldContain(i =>
            i.Properties.Any(p => p.Name == "IsProvisioned"));
        indexes.ShouldContain(i =>
            i.Properties.Any(p => p.Name == "IsActive"));
    }

    [Fact]
    public void CompanySystemSettings_DatabaseName_IsRequired()
    {
        // Verify that DatabaseName is configured as required
        var entityType = _context.Model.FindEntityType(typeof(CompanySystemSettings));
        var databaseNameProp = entityType?.FindProperty("DatabaseName");

        databaseNameProp.ShouldNotBeNull();
        databaseNameProp!.IsNullable.ShouldBeFalse();
    }

    [Fact]
    public void CompanySystemSettings_HasForeignKeyToClient()
    {
        // Verify that CompanyId is a FK to Client
        var entityType = _context.Model.FindEntityType(typeof(CompanySystemSettings));
        var fks = entityType?.GetForeignKeys().ToList();

        fks.ShouldContain(fk =>
            fk.Properties.Any(p => p.Name == "CompanyId") &&
            fk.PrincipalEntityType.ClrType == typeof(Client));
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
