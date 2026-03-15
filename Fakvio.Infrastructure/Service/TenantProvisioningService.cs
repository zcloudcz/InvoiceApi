using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Service responsible for provisioning, activating, deactivating, and migrating tenant schemas.
///
/// Architecture: Single PostgreSQL database with schema-per-tenant isolation.
/// Provisioning creates a new schema (e.g., "tenant_42") in the shared PostgreSQL database,
/// applies the TenantDbContext migrations to that schema, copies code tables (VatRate, Currency, etc.)
/// from the master "public" schema, and marks the company as provisioned in CompanySystemSettings.
///
/// All tenants share the same database connection; isolation is achieved via PostgreSQL schemas.
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
        // (e.g., schema created but tables not seeded, or code tables inserted partially).
        if (settings.IsProvisioned)
        {
            _logger.LogWarning(
                "Company {CompanyId} is already marked as provisioned (schema: {SchemaName}). " +
                "Re-running provisioning to ensure all data is consistent.",
                companyId, settings.SchemaName);
        }

        // 2. Load the company (issuer) data from master DB — will be copied to tenant schema.
        // AsSplitQuery: Address and Contact are both collection navigations — prevents cartesian explosion.
        var company = await _masterContext.Client
            .AsSplitQuery()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .FirstOrDefaultAsync(c => c.Id == companyId && c.IsIssuer, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Company with ID {companyId} not found or is not marked as issuer.");

        // 3. Create the PostgreSQL schema
        await CreateSchemaAsync(settings.SchemaName, cancellationToken);
        _logger.LogInformation("Created schema '{SchemaName}' for company {CompanyId}",
            settings.SchemaName, companyId);

        // 4. Apply TenantDbContext migrations to the new schema
        using var tenantContext = CreateTenantContext(settings.SchemaName);
        await tenantContext.Database.MigrateAsync(cancellationToken);
        _logger.LogInformation("Applied migrations to tenant schema '{SchemaName}'",
            settings.SchemaName);

        // 5. Copy code tables from master DB to tenant schema
        await CopyCodeTablesAsync(tenantContext, settings.SchemaName, cancellationToken);
        _logger.LogInformation("Copied code tables to tenant schema '{SchemaName}'",
            settings.SchemaName);

        // 6. Create the issuer (company) record in the tenant schema
        await CreateIssuerInTenantAsync(tenantContext, company, cancellationToken);
        _logger.LogInformation("Created issuer in tenant schema '{SchemaName}'",
            settings.SchemaName);

        // 7. Create default number sequences for Invoice and CreditNote
        await CreateDefaultNumberSequencesAsync(tenantContext, cancellationToken);
        _logger.LogInformation("Created default number sequences in tenant schema '{SchemaName}'",
            settings.SchemaName);

        // 8. Mark as provisioned in master DB
        settings.IsProvisioned = true;
        settings.IsActive = true;
        settings.ProvisionedAt = DateTime.UtcNow;
        await _masterContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Provisioning complete for company {CompanyId} → schema '{SchemaName}'",
            companyId, settings.SchemaName);

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
        _logger.LogInformation("Migrating tenant schema for company {CompanyId}", companyId);

        var settings = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId && s.IsProvisioned, cancellationToken);

        if (settings == null)
            return false;

        using var tenantContext = CreateTenantContext(settings.SchemaName);
        await tenantContext.Database.MigrateAsync(cancellationToken);

        // Re-apply permissions after migration — EF Core may have created new tables/sequences
        // that the current user needs access to (ALTER DEFAULT PRIVILEGES covers future objects,
        // but GRANT ALL ON ALL TABLES covers newly created objects from this migration run).
        await EnsureSchemaPermissionsAsync(settings.SchemaName, cancellationToken);

        _logger.LogInformation("Migrated tenant schema '{SchemaName}' for company {CompanyId}",
            settings.SchemaName, companyId);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> MigrateAllTenantsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting migration of all active tenant schemas");

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
                using var tenantContext = CreateTenantContext(settings.SchemaName);
                await tenantContext.Database.MigrateAsync(cancellationToken);
                successCount++;

                _logger.LogInformation("Migrated tenant schema '{SchemaName}' (company {CompanyId})",
                    settings.SchemaName, settings.CompanyId);
            }
            catch (Exception ex)
            {
                // Log error but continue with other tenants — one failure shouldn't block all
                _logger.LogError(ex, "Failed to migrate tenant schema '{SchemaName}' (company {CompanyId})",
                    settings.SchemaName, settings.CompanyId);
            }
        }

        _logger.LogInformation("Tenant migration complete: {Success}/{Total} successful",
            successCount, tenants.Count);

        return successCount;
    }

    /// <summary>
    /// Drops all tenant schemas that are marked as provisioned in the master DB.
    /// WARNING: This is a DESTRUCTIVE operation — all tenant data will be permanently lost.
    /// </summary>
    public async Task DeleteAllTenantDbs(CancellationToken cancellationToken = default)
    {
#if !DEBUG
        _logger.LogWarning("DeleteAllTenantDbs is a destructive operation and should only be run in development environments. " +
            "Aborting to prevent accidental data loss.");
        return;
#endif
        _logger.LogInformation("Starting deletion of all tenant schemas");

        var tenants = await _masterContext.CompanySystemSettings
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Found {Count} provisioned tenants to delete", tenants.Count);

        var connectionString = GetConnectionString();

        foreach (var settings in tenants)
        {
            try
            {
                // Drop the entire schema with CASCADE to remove all objects inside it
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                var safeName = SanitizeSchemaName(settings.SchemaName);
                await using var dropCmd = connection.CreateCommand();
                dropCmd.CommandText = $"DROP SCHEMA IF EXISTS \"{safeName}\" CASCADE";
                await dropCmd.ExecuteNonQueryAsync(cancellationToken);

                _logger.LogInformation("Dropped tenant schema '{SchemaName}' for company {CompanyId}",
                    settings.SchemaName, settings.CompanyId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to drop tenant schema '{SchemaName}' (company {CompanyId})",
                    settings.SchemaName, settings.CompanyId);
            }
        }

        _logger.LogInformation("Tenant schema deletion complete");
    }

    #region Private helpers

    /// <summary>
    /// Ensures schema permissions are set for the current database user.
    /// Opens its own connection — can be called from anywhere (e.g., after MigrateAsync).
    /// IDEMPOTENT: safe to call multiple times.
    /// </summary>
    private async Task EnsureSchemaPermissionsAsync(string schemaName, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var safeName = SanitizeSchemaName(schemaName);
        await GrantSchemaPermissionsAsync(connection, safeName, cancellationToken);
    }

    /// <summary>
    /// Creates a PostgreSQL schema for a tenant and grants full permissions.
    /// Connects to the shared database and executes CREATE SCHEMA + GRANT statements.
    ///
    /// PERMISSIONS: After creating the schema, we grant ALL privileges on the schema
    /// to the current database user (CURRENT_USER) and set ALTER DEFAULT PRIVILEGES
    /// so that any future tables, sequences, and functions created in this schema
    /// will automatically inherit full permissions. This is critical for Azure PostgreSQL
    /// where the application may connect via Entra ID (managed identity) — without
    /// default privileges, newly created objects in tenant schemas would not be accessible.
    ///
    /// IDEMPOTENT: Checks information_schema.schemata before creating.
    /// If the schema already exists, permissions are still applied (safe to re-run).
    /// </summary>
    private async Task CreateSchemaAsync(string schemaName, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Sanitize schema name to prevent SQL injection (identifiers can't use parameters)
        var safeName = SanitizeSchemaName(schemaName);

        // Check if schema already exists using information_schema (idempotent)
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT schema_name FROM information_schema.schemata WHERE schema_name = @name";
        checkCmd.Parameters.AddWithValue("name", safeName);
        var exists = await checkCmd.ExecuteScalarAsync(cancellationToken);

        if (exists == null)
        {
            // Schema doesn't exist — create it
            await using var createCmd = connection.CreateCommand();
            createCmd.CommandText = $"CREATE SCHEMA \"{safeName}\"";
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("PostgreSQL schema '{SchemaName}' created", safeName);
        }
        else
        {
            _logger.LogInformation("PostgreSQL schema '{SchemaName}' already exists, skipping creation", safeName);
        }

        // Grant full permissions on the schema to the current user.
        // This ensures the application user can create/alter/drop objects in this schema,
        // even if the schema was created by a different role (e.g., admin vs. managed identity).
        await GrantSchemaPermissionsAsync(connection, safeName, cancellationToken);
    }

    /// <summary>
    /// Grants full permissions on a tenant schema to the current database user.
    ///
    /// WHY THIS IS NEEDED:
    /// In Azure PostgreSQL with Entra ID authentication, the application may connect
    /// as a managed identity or AAD user. When schemas are created, the owner has full access,
    /// but if the connecting identity changes (e.g., rotating from one managed identity to another,
    /// or switching from admin to app identity), the new identity won't have access to existing schemas.
    ///
    /// ALTER DEFAULT PRIVILEGES ensures that any objects (tables, sequences, functions) created
    /// in this schema IN THE FUTURE will automatically grant ALL privileges to the current user.
    /// This is the PostgreSQL equivalent of "give this role access to everything, now and forever."
    ///
    /// IDEMPOTENT: All GRANT and ALTER DEFAULT PRIVILEGES statements are safe to re-run.
    /// PostgreSQL silently ignores duplicate grants.
    /// </summary>
    private async Task GrantSchemaPermissionsAsync(
        NpgsqlConnection connection,
        string safeName,
        CancellationToken cancellationToken)
    {
        // Get the current database user name — this is the role we're granting to.
        // On Azure PostgreSQL with Entra ID, this returns the AAD principal name.
        // On local Docker PostgreSQL, this returns the password-auth username (e.g., "fakvio").
        await using var userCmd = connection.CreateCommand();
        userCmd.CommandText = "SELECT CURRENT_USER";
        var currentUser = (string)(await userCmd.ExecuteScalarAsync(cancellationToken))!;

        // GRANT USAGE + CREATE on the schema itself — allows the user to access and create objects.
        // GRANT ALL is shorthand for USAGE + CREATE on schemas.
        await using var grantSchemaCmd = connection.CreateCommand();
        grantSchemaCmd.CommandText = $"GRANT ALL ON SCHEMA \"{safeName}\" TO \"{currentUser}\"";
        await grantSchemaCmd.ExecuteNonQueryAsync(cancellationToken);

        // GRANT ALL on all EXISTING tables in the schema — covers re-provisioning scenarios
        // where tables already exist but a new user needs access.
        await using var grantTablesCmd = connection.CreateCommand();
        grantTablesCmd.CommandText = $"GRANT ALL ON ALL TABLES IN SCHEMA \"{safeName}\" TO \"{currentUser}\"";
        await grantTablesCmd.ExecuteNonQueryAsync(cancellationToken);

        // GRANT ALL on all EXISTING sequences — needed for auto-increment (SERIAL/IDENTITY) columns.
        // Without this, INSERT operations would fail with "permission denied for sequence".
        await using var grantSeqCmd = connection.CreateCommand();
        grantSeqCmd.CommandText = $"GRANT ALL ON ALL SEQUENCES IN SCHEMA \"{safeName}\" TO \"{currentUser}\"";
        await grantSeqCmd.ExecuteNonQueryAsync(cancellationToken);

        // ALTER DEFAULT PRIVILEGES — this is the critical part for FUTURE objects.
        // Any tables created in this schema after this point will automatically grant ALL to the user.
        // This covers EF Core migrations that create new tables when the app is updated.
        await using var defaultTablesCmd = connection.CreateCommand();
        defaultTablesCmd.CommandText =
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{safeName}\" GRANT ALL ON TABLES TO \"{currentUser}\"";
        await defaultTablesCmd.ExecuteNonQueryAsync(cancellationToken);

        // Same for sequences — EF Core migrations may create new sequences for identity columns.
        await using var defaultSeqCmd = connection.CreateCommand();
        defaultSeqCmd.CommandText =
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{safeName}\" GRANT ALL ON SEQUENCES TO \"{currentUser}\"";
        await defaultSeqCmd.ExecuteNonQueryAsync(cancellationToken);

        // Same for functions — stored procedures or functions created by future migrations.
        await using var defaultFuncCmd = connection.CreateCommand();
        defaultFuncCmd.CommandText =
            $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{safeName}\" GRANT ALL ON FUNCTIONS TO \"{currentUser}\"";
        await defaultFuncCmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Granted full permissions on schema '{SchemaName}' to user '{User}' (including default privileges for future objects)",
            safeName, currentUser);
    }

    /// <summary>
    /// Creates a new TenantDbContext configured for a specific schema.
    /// Used during provisioning and migration operations.
    /// </summary>
    private TenantDbContext CreateTenantContext(string schemaName)
    {
        var connectionString = GetConnectionString();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connectionString, b =>
            {
                b.MigrationsAssembly("Fakvio.Infrastructure");
                b.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
            })
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            // Downgrade PendingModelChangesWarning from Throw → Log so MigrateAsync()
            // doesn't fail when the code model is slightly ahead of the last migration.
            .ConfigureWarnings(w =>
                w.Log(RelationalEventId.PendingModelChangesWarning))
            .Options;

        var context = new TenantDbContext(options);
        context.Schema = schemaName;
        return context;
    }

    /// <summary>
    /// Sanitizes a schema name to prevent SQL injection.
    /// Only allows lowercase alphanumeric characters and underscores.
    /// PostgreSQL schema names must start with a letter or underscore.
    /// </summary>
    private static string SanitizeSchemaName(string name)
    {
        var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()).ToLowerInvariant();
        if (string.IsNullOrEmpty(sanitized))
            throw new InvalidOperationException($"Invalid schema name: '{name}' — must contain alphanumeric characters.");
        return sanitized;
    }

    /// <summary>
    /// Gets the shared PostgreSQL connection string from configuration.
    /// </summary>
    private string GetConnectionString()
    {
        return _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection string not configured.");
    }

    /// <summary>
    /// Copies code tables from master DB ("public" schema) to the newly provisioned tenant schema.
    /// These are "seed" copies — the tenant can later customize them independently.
    ///
    /// IDEMPOTENT: Deletes existing records before inserting to avoid duplicate key violations.
    /// This is essential because TenantDbContext.SeedData() (via HasData) may have already
    /// inserted some records during MigrateAsync(), and re-inserting would cause unique
    /// constraint violations (e.g., Currency.Code must be unique).
    /// Delete order respects FK constraints: child tables first, then parent tables.
    /// </summary>
    private async Task CopyCodeTablesAsync(TenantDbContext tenantContext, string schemaName, CancellationToken cancellationToken)
    {
        var safeName = SanitizeSchemaName(schemaName);

        // Delete existing records in FK-safe order (children before parents).
        // NumberSequence references NumberSequenceFormat, so it must be deleted first.
        // Using raw SQL because EF change tracker doesn't support efficient bulk deletes.
        // PostgreSQL uses double-quoted identifiers for schema-qualified table names.
        _logger.LogInformation("Clearing existing code tables in tenant schema '{SchemaName}' for idempotent re-seeding", schemaName);
        await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"NumberSequence\"", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"NumberSequenceFormat\"", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"ContentTemplate\"", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"Currency\"", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"VatRate\"", cancellationToken);

        // Reset PostgreSQL sequences so IDs start from 1 (cleaner for new tenants).
        // PostgreSQL uses ALTER SEQUENCE ... RESTART WITH 1 instead of DBCC CHECKIDENT.
        // Note: sequence names follow the convention "{Table}_{Column}_seq" by default.
        await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"NumberSequence_Id_seq\" RESTART WITH 1", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"NumberSequenceFormat_Id_seq\" RESTART WITH 1", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"ContentTemplate_Id_seq\" RESTART WITH 1", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"Currency_Id_seq\" RESTART WITH 1", cancellationToken);
        await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"VatRate_Id_seq\" RESTART WITH 1", cancellationToken);

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
    /// Creates the issuer (company) record in the tenant schema.
    /// Copies the company data from master DB, including addresses and contacts.
    /// The tenant schema will have its own copy of the issuer for invoice generation.
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
    /// Creates default number sequences for Invoice and CreditNote in the tenant schema.
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
