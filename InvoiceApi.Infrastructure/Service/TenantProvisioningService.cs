using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// Service responsible for provisioning, activating, deactivating, and migrating tenant databases.
///
/// Provisioning creates a new SQL Server database for a company, applies the TenantDbContext schema,
/// copies code tables (VatRate, Currency, etc.) from the master database, and marks the company
/// as provisioned in CompanySystemSettings.
///
/// Connection string is built by replacing the Database name in the master connection string
/// with the tenant-specific DatabaseName from CompanySystemSettings.
/// </summary>
public class TenantProvisioningService : ITenantProvisioningService
{
    private readonly MasterDbContext _masterContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        MasterDbContext masterContext,
        IConfiguration configuration,
        ILogger<TenantProvisioningService> logger)
    {
        _masterContext = masterContext;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ProvisionTenantAsync(long companyId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting provisioning for company {CompanyId}", companyId);

        // 1. Load CompanySystemSettings from master DB
        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
            throw new InvalidOperationException(
                $"CompanySystemSettings not found for company {companyId}. " +
                "Create the settings record first before provisioning.");

        // Allow re-provisioning: if already provisioned, log a warning and continue.
        // This makes the entire flow idempotent — safe to re-run after a partial failure
        // (e.g., DB created but tables not seeded, or code tables inserted partially).
        if (settings.IsProvisioned)
        {
            _logger.LogWarning(
                "Company {CompanyId} is already marked as provisioned (database: {DatabaseName}). " +
                "Re-running provisioning to ensure all data is consistent.",
                companyId, settings.DatabaseName);
        }

        // 2. Load the company (issuer) data from master DB — will be copied to tenant DB.
        // AsSplitQuery: Address and Contact are both collection navigations — prevents cartesian explosion.
        var company = await _masterContext.Client
            .AsSplitQuery()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .FirstOrDefaultAsync(c => c.Id == companyId && c.IsIssuer, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Company with ID {companyId} not found or is not marked as issuer.");

        // 3. Build the tenant connection string
        var connectionString = BuildTenantConnectionString(settings);

        // 4. Create the PostgreSQL database
        await CreateDatabaseAsync(settings.DatabaseName, cancellationToken);
        _logger.LogInformation("Created database '{DatabaseName}' for company {CompanyId}",
            settings.DatabaseName, companyId);

        // 5. Apply TenantDbContext migrations to the new database
        using var tenantContext = CreateTenantContext(connectionString);
        await tenantContext.Database.MigrateAsync(cancellationToken);
        _logger.LogInformation("Applied migrations to tenant database '{DatabaseName}'",
            settings.DatabaseName);

        // 6. Copy code tables from master DB to tenant DB
        await CopyCodeTablesAsync(tenantContext, cancellationToken);
        _logger.LogInformation("Copied code tables to tenant database '{DatabaseName}'",
            settings.DatabaseName);

        // 7. Create the issuer (company) record in the tenant DB
        await CreateIssuerInTenantAsync(tenantContext, company, cancellationToken);
        _logger.LogInformation("Created issuer in tenant database '{DatabaseName}'",
            settings.DatabaseName);

        // 8. Create default number sequences for Invoice and CreditNote
        await CreateDefaultNumberSequencesAsync(tenantContext, cancellationToken);
        _logger.LogInformation("Created default number sequences in tenant database '{DatabaseName}'",
            settings.DatabaseName);

        // 9. Mark as provisioned in master DB
        settings.IsProvisioned = true;
        settings.IsActive = true;
        settings.ProvisionedAt = DateTime.UtcNow;
        settings.ConnectionString = connectionString;
        await _masterContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Provisioning complete for company {CompanyId} → database '{DatabaseName}'",
            companyId, settings.DatabaseName);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ActivateTenantAsync(long companyId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Activating tenant for company {CompanyId}", companyId);

        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
            return false;

        if (!settings.IsProvisioned)
            throw new InvalidOperationException(
                $"Company {companyId} is not provisioned. Provision first before activating.");

        settings.IsActive = true;
        await _masterContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Tenant activated for company {CompanyId}", companyId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeactivateTenantAsync(long companyId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deactivating tenant for company {CompanyId}", companyId);

        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
            return false;

        settings.IsActive = false;
        await _masterContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Tenant deactivated for company {CompanyId}", companyId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> MigrateTenantAsync(long companyId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Migrating tenant database for company {CompanyId}", companyId);

        var settings = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.IsProvisioned, cancellationToken);

        if (settings == null)
            return false;

        var connectionString = BuildTenantConnectionString(settings);
        using var tenantContext = CreateTenantContext(connectionString);
        await tenantContext.Database.MigrateAsync(cancellationToken);

        _logger.LogInformation("Migrated tenant database '{DatabaseName}' for company {CompanyId}",
            settings.DatabaseName, companyId);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> MigrateAllTenantsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting migration of all active tenant databases");

        // Get all provisioned and active tenants from master DB
        var tenants = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .Where(s => s.IsProvisioned && s.IsActive)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Found {Count} active tenants to migrate", tenants.Count);

        var successCount = 0;

        foreach (var settings in tenants)
        {
            try
            {
                var connectionString = BuildTenantConnectionString(settings);
                using var tenantContext = CreateTenantContext(connectionString);
                await tenantContext.Database.MigrateAsync(cancellationToken);
                successCount++;

                _logger.LogInformation("Migrated tenant '{DatabaseName}' (company {CompanyId})",
                    settings.DatabaseName, settings.CompanyId);
            }
            catch (Exception ex)
            {
                // Log error but continue with other tenants — one failure shouldn't block all
                _logger.LogError(ex, "Failed to migrate tenant '{DatabaseName}' (company {CompanyId})",
                    settings.DatabaseName, settings.CompanyId);
            }
        }

        _logger.LogInformation("Tenant migration complete: {Success}/{Total} successful",
            successCount, tenants.Count);

        return successCount;
    }


    /// <summary>
    /// Deletes all tenant databases that are marked as provisioned in the master DB.
    /// </summary>
    public async Task DeleteAllTenantDbs(CancellationToken cancellationToken = default)
    {
#if !DEBUG
        _logger.LogWarning("DeleteAllTenantDbs is a destructive operation and should only be run in development environments. " +
            "Aborting to prevent accidental data loss.");
        return;
#endif
        _logger.LogInformation("Starting deletion of all tenant databases");
        // Get all provisioned tenants from master DB
        var tenants = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        _logger.LogInformation("Found {Count} provisioned tenants to delete", tenants.Count);
        foreach (var settings in tenants)
        {
            try
            {
                var connectionString = BuildTenantConnectionString(settings);
                using var tenantContext = CreateTenantContext(connectionString);
                await tenantContext.Database.EnsureDeletedAsync(cancellationToken);

                _logger.LogInformation("Deleted tenant database '{DatabaseName}' for company {CompanyId}",
                    settings.DatabaseName, settings.CompanyId);
            }
            catch (Exception ex)
            {
                // Log error but continue with other tenants — one failure shouldn't block all
                _logger.LogError(ex, "Failed to delete tenant database '{DatabaseName}' (company {CompanyId})",
                    settings.DatabaseName, settings.CompanyId);
            }
        }
        _logger.LogInformation("Tenant database deletion complete");
    }

    #region Private helpers

    /// <summary>
    /// Builds the connection string for a tenant database.
    /// If CompanySystemSettings has a custom ConnectionString, uses that directly.
    /// Otherwise, takes the master connection string and replaces the Database (Initial Catalog).
    /// </summary>
    private string BuildTenantConnectionString(CompanySystemSettings settings)
    {
        // If a custom connection string is configured, use it as-is
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
            return settings.ConnectionString;

        // Build from master connection string template, replacing the database name
        var masterConnectionString = _configuration.GetConnectionString("MasterConnection")
            ?? throw new InvalidOperationException("MasterConnection string not configured.");

        // SQL Server uses SqlConnectionStringBuilder with InitialCatalog property
        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = settings.DatabaseName
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Creates a new TenantDbContext connected to the specified connection string.
    /// Used during provisioning and migration operations.
    /// </summary>
    private static TenantDbContext CreateTenantContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(connectionString, b =>
                b.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null))
            .Options;

        return new TenantDbContext(options);
    }

    /// <summary>
    /// Creates the SQL Server database using a low-level connection.
    /// Connects to the "master" system database to execute CREATE DATABASE.
    /// </summary>
    private async Task CreateDatabaseAsync(string databaseName, CancellationToken cancellationToken)
    {
        var masterConnectionString = _configuration.GetConnectionString("MasterConnection")
            ?? throw new InvalidOperationException("MasterConnection string not configured.");

        // Connect to the "master" system database to execute CREATE DATABASE
        // (SQL Server equivalent of PostgreSQL's "postgres" system database)
        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Use parameterized-safe approach — database names can't use SQL parameters,
        // so we sanitize by only allowing alphanumeric + underscore characters
        var safeName = SanitizeDatabaseName(databaseName);

        // Check if database already exists using SQL Server's DB_ID() function (idempotent)
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = $"SELECT DB_ID('{safeName}')";
        var exists = await checkCmd.ExecuteScalarAsync(cancellationToken);

        // DB_ID returns null (DBNull) if the database does not exist
        if (exists == null || exists == DBNull.Value)
        {
            await using var createCmd = connection.CreateCommand();
            createCmd.CommandText = $"CREATE DATABASE [{safeName}]";
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("SQL Server database '{DatabaseName}' created", safeName);
        }
        else
        {
            _logger.LogInformation("SQL Server database '{DatabaseName}' already exists, skipping creation", safeName);
        }
    }

    /// <summary>
    /// Sanitizes a database name to prevent SQL injection.
    /// Only allows alphanumeric characters and underscores.
    /// </summary>
    private static string SanitizeDatabaseName(string name)
    {
        var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (string.IsNullOrEmpty(sanitized))
            throw new InvalidOperationException($"Invalid database name: '{name}' — must contain alphanumeric characters.");
        return sanitized;
    }

    /// <summary>
    /// Copies code tables from master DB to the newly provisioned tenant DB.
    /// These are "seed" copies — the tenant can later customize them independently.
    ///
    /// IDEMPOTENT: Deletes existing records before inserting to avoid duplicate key violations.
    /// This is essential because TenantDbContext.SeedData() (via HasData) may have already
    /// inserted some records during MigrateAsync(), and re-inserting would cause unique
    /// constraint violations (e.g., Currency.Code must be unique).
    /// Delete order respects FK constraints: child tables first, then parent tables.
    /// </summary>
    private async Task CopyCodeTablesAsync(TenantDbContext tenantContext, CancellationToken cancellationToken)
    {
        // Delete existing records in FK-safe order (children before parents).
        // NumberSequence references NumberSequenceFormat, so it must be deleted first.
        // Using raw SQL because EF change tracker doesn't support efficient bulk deletes.
        _logger.LogInformation("Clearing existing code tables in tenant DB for idempotent re-seeding");
        await tenantContext.Database.ExecuteSqlRawAsync("DELETE FROM [NumberSequence]", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync("DELETE FROM [NumberSequenceFormat]", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync("DELETE FROM [ContentTemplate]", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync("DELETE FROM [Currency]", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync("DELETE FROM [VatRate]", cancellationToken);

        // Reseed identity counters so IDs start from 1 (cleaner for new tenants).
        // CHECKIDENT with RESEED 0 makes the next identity value = 1.
        await tenantContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[NumberSequence]', RESEED, 0)", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[NumberSequenceFormat]', RESEED, 0)", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[ContentTemplate]', RESEED, 0)", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[Currency]', RESEED, 0)", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync("DBCC CHECKIDENT ('[VatRate]', RESEED, 0)", cancellationToken);

        // Copy VatRates (all active rates from master)
        var vatRates = await _masterContext.VatRate
            .AsNoTracking()
            .Where(v => v.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var rate in vatRates)
        {
            tenantContext.VatRate.Add(new VatRate
            {
                Name = rate.Name,
                Rate = rate.Rate,
                ValidFrom = rate.ValidFrom,
                ValidTo = rate.ValidTo,
                IsReduced = rate.IsReduced,
                IsDefault = rate.IsDefault,
                IsActive = rate.IsActive,
                CreatedAt = DateTime.UtcNow
            });
        }

        // Copy Currencies (all active currencies from master)
        var currencies = await _masterContext.Currency
            .AsNoTracking()
            .Where(c => c.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var currency in currencies)
        {
            tenantContext.Currency.Add(new Currency
            {
                Code = currency.Code,
                Name = currency.Name,
                Symbol = currency.Symbol,
                DecimalPlaces = currency.DecimalPlaces,
                SortOrder = currency.SortOrder,
                DisplayFormat = currency.DisplayFormat,
                IsActive = currency.IsActive,
                CreatedAt = DateTime.UtcNow
            });
        }

        // Copy NumberSequenceFormats (all active formats from master)
        var formats = await _masterContext.NumberSequenceFormat
            .AsNoTracking()
            .Where(f => f.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var format in formats)
        {
            tenantContext.NumberSequenceFormat.Add(new NumberSequenceFormat
            {
                Name = format.Name,
                FormatPattern = format.FormatPattern,
                CounterDigits = format.CounterDigits,
                ResetsYearly = format.ResetsYearly,
                ResetsMonthly = format.ResetsMonthly,
                IsActive = format.IsActive,
                CreatedAt = DateTime.UtcNow
            });
        }

        // Copy ContentTemplates (all active + default templates from master)
        var templates = await _masterContext.ContentTemplate
            .AsNoTracking()
            .Where(t => t.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var template in templates)
        {
            tenantContext.ContentTemplate.Add(new ContentTemplate
            {
                Name = template.Name,
                Subject = template.Subject,
                HtmlBody = template.HtmlBody,
                TemplateType = template.TemplateType,
                IsDefault = template.IsDefault,
                IsActive = template.IsActive,
                Description = template.Description,
                CreatedAt = DateTime.UtcNow
            });
        }

        await tenantContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the issuer (company) record in the tenant database.
    /// Copies the company data from master DB, including addresses and contacts.
    /// The tenant DB will have its own copy of the issuer for invoice generation.
    ///
    /// IDEMPOTENT: Checks if an issuer with the same RegistrationNumber already exists.
    /// If found, skips insertion — the existing issuer record is kept as-is.
    /// </summary>
    private static async Task CreateIssuerInTenantAsync(
        TenantDbContext tenantContext, Client masterCompany, CancellationToken cancellationToken)
    {
        // Check if an issuer already exists in this tenant (by RegistrationNumber)
        var existingIssuer = await tenantContext.Client
            .AnyAsync(c => c.IsIssuer && c.RegistrationNumber == masterCompany.RegistrationNumber, cancellationToken);

        if (existingIssuer)
        {
            // Issuer already exists — safe to skip (idempotent re-provisioning)
            return;
        }

        var tenantIssuer = new Client
        {
            CompanyName = masterCompany.CompanyName,
            TradingName = masterCompany.TradingName,
            RegistrationNumber = masterCompany.RegistrationNumber,
            TaxNumber = masterCompany.TaxNumber,
            IsVatPayer = masterCompany.IsVatPayer,
            IsIssuer = true,
            IsActive = true,
            LastAresFetchDate = masterCompany.LastAresFetchDate,
            CreatedAt = DateTime.UtcNow
        };

        // Copy addresses
        if (masterCompany.Address != null)
        {
            foreach (var addr in masterCompany.Address)
            {
                tenantIssuer.Address.Add(new Address
                {
                    AddressType = addr.AddressType,
                    Street = addr.Street,
                    City = addr.City,
                    PostalCode = addr.PostalCode,
                    Country = addr.Country,
                    AddressLine2 = addr.AddressLine2,
                    IsPrimary = addr.IsPrimary,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        // Copy contacts
        if (masterCompany.Contact != null)
        {
            foreach (var contact in masterCompany.Contact)
            {
                tenantIssuer.Contact.Add(new Contact
                {
                    ContactType = contact.ContactType,
                    ContactValue = contact.ContactValue,
                    Label = contact.Label,
                    IsPrimary = contact.IsPrimary,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        tenantContext.Client.Add(tenantIssuer);
        await tenantContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Creates default number sequences for Invoice and CreditNote in the tenant DB.
    /// Uses the first active NumberSequenceFormat as the format template.
    ///
    /// IDEMPOTENT: Checks if default sequences already exist before inserting.
    /// If CopyCodeTablesAsync already cleared and re-seeded NumberSequence,
    /// this method safely adds only missing defaults.
    /// </summary>
    private static async Task CreateDefaultNumberSequencesAsync(
        TenantDbContext tenantContext, CancellationToken cancellationToken)
    {
        // Find the first active format in the tenant DB (copied from master).
        // OrderBy(Id): deterministic ordering — avoids EF warning when predicate could match multiple rows.
        var defaultFormat = await tenantContext.NumberSequenceFormat
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync(f => f.IsActive, cancellationToken);

        if (defaultFormat == null)
        {
            // No formats available — skip sequence creation (admin can add later)
            return;
        }

        // Only create default Invoice sequence if one doesn't already exist
        var hasInvoiceSeq = await tenantContext.NumberSequence
            .AnyAsync(s => s.DocumentType == EDocumentType.Invoice && s.IsDefault, cancellationToken);

        if (!hasInvoiceSeq)
        {
            tenantContext.NumberSequence.Add(new NumberSequence
            {
                Name = "Default Invoice",
                DocumentType = EDocumentType.Invoice,
                Prefix = "INV-",
                Suffix = null,
                CurrentNumber = 0,
                IsDefault = true,
                IsActive = true,
                NumberSequenceFormatId = defaultFormat.Id,
                CreatedAt = DateTime.UtcNow
            });
        }

        // Only create default CreditNote sequence if one doesn't already exist
        var hasCreditNoteSeq = await tenantContext.NumberSequence
            .AnyAsync(s => s.DocumentType == EDocumentType.CreditNote && s.IsDefault, cancellationToken);

        if (!hasCreditNoteSeq)
        {
            tenantContext.NumberSequence.Add(new NumberSequence
            {
                Name = "Default Credit Note",
                DocumentType = EDocumentType.CreditNote,
                Prefix = "CN-",
                Suffix = null,
                CurrentNumber = 0,
                IsDefault = true,
                IsActive = true,
                NumberSequenceFormatId = defaultFormat.Id,
                CreatedAt = DateTime.UtcNow
            });
        }

        await tenantContext.SaveChangesAsync(cancellationToken);
    }

    #endregion
}
