using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// A newly provisioned tenant must receive Czech templates under "cs" AND the English variants under "en".
/// Provisioning copies the master ContentTemplate rows into the tenant schema; it used to drop the Language column,
/// so every copied row became "cs" and the English variants would never have been selected for English clients.
///
/// Real PostgreSQL for the tenant side (full migration chain + raw DELETE/ALTER SEQUENCE/INSERT copy); the master side
/// is InMemory and holds exactly the e-mail rows of the master seed. Skipped when FAKVIO_TEST_POSTGRES is not set.
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class TenantProvisioningEmailTemplatesTests
{
    private const long CompanyId = 4711L;

    [SkippableFact]
    public async Task ProvisionedTenant_GetsCzechTemplatesAndEnglishVariants_WithLanguagePreserved()
    {
        var connectionString = Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(connectionString), "Set FAKVIO_TEST_POSTGRES to a disposable PostgreSQL database.");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        var schema = ("test_emailprov_" + Guid.NewGuid().ToString("N"))[..40];
        var masterName = Guid.NewGuid().ToString();
        await using var factory = new NpgsqlDataSourceFactory(new DatabaseOptions
        {
            ConnectionString = connectionString!,
            AuthMode = DatabaseAuthMode.Password
        });

        try
        {
            // Arrange — master with the seeded e-mail templates (cs + en) and the minimum provisioning reads.
            await using (var master = CreateMaster(masterName))
            {
                SeedMaster(master, schema);
            }

            // Act
            await using (var master = CreateMaster(masterName))
            {
                var service = new TenantProvisioningService(
                    master, factory, factory.Root, NullLogger<TenantProvisioningService>.Instance);
                (await service.ProvisionTenantAsync(CompanyId)).ShouldBeTrue();
            }

            // Assert — read back from the tenant schema.
            await using var tenant = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
                .UseNpgsql(factory.GetForSchema(schema))
                .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
                .Options) { Schema = schema };
            var templates = await tenant.ContentTemplate.AsNoTracking().ToListAsync();

            EContentTemplateType[] localized =
            [
                EContentTemplateType.InvoiceEmail, EContentTemplateType.CreditNoteEmail, EContentTemplateType.AdvanceInvoiceEmail,
                EContentTemplateType.TaxReceiptForAdvanceEmail, EContentTemplateType.InvitationEmail, EContentTemplateType.ReminderEmail
            ];
            foreach (var type in localized)
            {
                foreach (var language in new[] { "cs", "en" })
                {
                    templates.Count(t => t.TemplateType == type && t.Language == language && t.IsDefault && t.IsActive)
                        .ShouldBe(1, $"{type}/{language}");
                }
            }

            templates.Single(t => t.TemplateType == EContentTemplateType.InvoiceEmail && t.Language == "cs")
                .Subject.ShouldBe(DefaultEmailTemplates.InvoiceCsSubject);
            templates.Single(t => t.TemplateType == EContentTemplateType.InvoiceEmail && t.Language == "en")
                .Subject.ShouldBe(DefaultEmailTemplates.InvoiceEnSubject);
            templates.Single(t => t.TemplateType == EContentTemplateType.ReminderEmail && t.Language == "cs")
                .Subject.ShouldBe(DefaultEmailTemplates.ReminderCsSubject);

            // The "any language" fallback (no language asked) keeps resolving to the Czech default: Czech rows come first.
            templates.Where(t => t.TemplateType == EContentTemplateType.InvoiceEmail).OrderBy(t => t.Id).First()
                .Language.ShouldBe("cs");
        }
        finally
        {
            factory.Evict(schema);
            await using var drop = factory.Root.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static MasterDbContext CreateMaster(string databaseName) =>
        new(new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(databaseName).Options);

    /// <summary>Company + settings + one VAT rate/currency (what provisioning reads) and the seeded e-mail templates.</summary>
    private static void SeedMaster(MasterDbContext master, string schema)
    {
        master.Client.Add(new Client
        {
            Id = CompanyId, CompanyName = "Šablony s.r.o.", RegistrationNumber = "47110000",
            IsIssuer = true, IsActive = true, IsVatPayer = true
        });
        master.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = CompanyId, SchemaName = schema, IsProvisioned = false, IsActive = false
        });
        master.VatRate.Add(new VatRate
        {
            Name = "Základní 21 %", Rate = 21m, ValidFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDefault = true, IsActive = true
        });
        master.Currency.Add(new Currency { Code = "CZK", Name = "Česká koruna", Symbol = "Kč", DecimalPlaces = 2, IsActive = true });

        // The master HasData rows for the e-mail types — exactly what a fresh master database contains.
        var seed = master.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ContentTemplate))!.GetSeedData();
        foreach (var row in seed.Where(r => (int)(EContentTemplateType)r["TemplateType"]! >= 10))
        {
            master.ContentTemplate.Add(new ContentTemplate
            {
                Id = (long)row["Id"]!,
                Name = (string)row["Name"]!,
                Subject = row.TryGetValue("Subject", out var subject) ? (string?)subject : null,
                HtmlBody = (string)row["HtmlBody"]!,
                TemplateType = (EContentTemplateType)row["TemplateType"]!,
                IsDefault = (bool)row["IsDefault"]!,
                IsActive = (bool)row["IsActive"]!,
                Language = (string)row["Language"]!,
                Description = row.TryGetValue("Description", out var d) ? (string?)d : null,
                CreatedAt = DateTime.UtcNow
            });
        }

        master.SaveChanges();
    }
}
