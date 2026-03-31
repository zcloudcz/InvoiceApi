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
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<TenantProvisioningService> _logger;

    /// <summary>
    /// Constructor with dependency injection.
    /// NpgsqlDataSource is the shared connection factory that handles both
    /// Azure AD token auth and password auth transparently.
    /// All raw NpgsqlConnection instances MUST come from _dataSource.OpenConnectionAsync()
    /// — never from "new NpgsqlConnection(connectionString)" — to ensure Azure AD tokens are used.
    /// </summary>
    public TenantProvisioningService(
        MasterDbContext masterContext,
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        ILogger<TenantProvisioningService> logger)
    {
        _masterContext = masterContext;
        _configuration = configuration;
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ProvisionTenantAsync(long companyId, CancellationToken cancellationToken = default)
    {
        // Track which step we're on so that if an exception is thrown,
        // the caller (and logs) know exactly WHERE provisioning failed.
        var currentStep = "Init";

        try
        {
            _logger.LogInformation("Starting provisioning for company {CompanyId}", companyId);

            // ── Step 1: Load CompanySystemSettings ──────────────────────────
            currentStep = "Step 1: Load CompanySystemSettings";
            _logger.LogInformation("[Provision:{CompanyId}] {Step}", companyId, currentStep);

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

            // ── Step 2: Load company (issuer) data from master DB ───────────
            currentStep = "Step 2: Load company issuer data";
            _logger.LogInformation("[Provision:{CompanyId}] {Step}", companyId, currentStep);

            // AsSplitQuery: Address and Contact are both collection navigations — prevents cartesian explosion.
            var company = await _masterContext.Client
                .AsSplitQuery()
                .Include(c => c.Address)
                .Include(c => c.Contact)
                .FirstOrDefaultAsync(c => c.Id == companyId && c.IsIssuer, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Company with ID {companyId} not found or is not marked as issuer.");

            // ── Step 3: Create the PostgreSQL schema ────────────────────────
            currentStep = $"Step 3: Create schema '{settings.SchemaName}'";
            _logger.LogInformation("[Provision:{CompanyId}] {Step}", companyId, currentStep);

            await CreateSchemaAsync(settings.SchemaName, cancellationToken);

            // ── Step 4: Apply EF Core migrations ────────────────────────────
            currentStep = $"Step 4: Apply migrations to schema '{settings.SchemaName}'";
            _logger.LogInformation("[Provision:{CompanyId}] {Step}", companyId, currentStep);

            using var tenantContext = CreateTenantContext(settings.SchemaName);
            await tenantContext.Database.MigrateAsync(cancellationToken);

            // ── Step 5: Copy code tables from master DB ─────────────────────
            currentStep = $"Step 5: Copy code tables to schema '{settings.SchemaName}'";
            _logger.LogInformation("[Provision:{CompanyId}] {Step}", companyId, currentStep);

            await CopyCodeTablesAsync(tenantContext, settings.SchemaName, cancellationToken);

            // ── Step 6: Create issuer in tenant schema ──────────────────────
            currentStep = $"Step 6: Create issuer in schema '{settings.SchemaName}'";
            _logger.LogInformation("[Provision:{CompanyId}] {Step}", companyId, currentStep);

            await CreateIssuerInTenantAsync(tenantContext, company, cancellationToken);

            // ── Step 7: Create default number sequences ─────────────────────
            currentStep = $"Step 7: Create default number sequences in schema '{settings.SchemaName}'";
            _logger.LogInformation("[Provision:{CompanyId}] {Step}", companyId, currentStep);

            await CreateDefaultNumberSequencesAsync(tenantContext, cancellationToken);

            // ── Step 8: Mark as provisioned in master DB ────────────────────
            currentStep = "Step 8: Mark as provisioned in master DB";
            _logger.LogInformation("[Provision:{CompanyId}] {Step}", companyId, currentStep);

            settings.IsProvisioned = true;
            settings.IsActive = true;
            settings.ProvisionedAt = DateTime.UtcNow;
            await _masterContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "[Provision:{CompanyId}] ALL STEPS COMPLETE → schema '{SchemaName}' is ready",
                companyId, settings.SchemaName);

            return true;
        }
        catch (Exception ex)
        {
            // Log the exact step where provisioning failed — this is the key diagnostic info.
            // Without this, Azure logs just show a generic "provisioning failed" with a stack trace
            // that doesn't clearly indicate which step (schema creation, migration, seed, etc.) broke.
            _logger.LogError(ex,
                "[Provision:{CompanyId}] FAILED at '{CurrentStep}'. Exception: {ExceptionType}: {Message}",
                companyId, currentStep, ex.GetType().Name, ex.Message);

            // If the exception wraps an inner exception (common with EF Core / Npgsql),
            // log that too — the real cause is often buried in the InnerException.
            if (ex.InnerException != null)
            {
                _logger.LogError(
                    "[Provision:{CompanyId}] Inner exception: {InnerType}: {InnerMessage}",
                    companyId, ex.InnerException.GetType().Name, ex.InnerException.Message);
            }

            // Re-throw with step info prepended so callers (AuthService, Controller) see it too
            throw new InvalidOperationException(
                $"Tenant provisioning failed at '{currentStep}' for company {companyId}: {ex.Message}", ex);
        }
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

        // Reconcile migration history before applying new migrations.
        // This handles the case where old migrations were squashed into InitTenant
        // but existing tenants still have old migration names in __EFMigrationsHistory.
        await ReconcileMigrationHistoryAsync(tenantContext, settings.SchemaName, cancellationToken);

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

                // Reconcile migration history before applying new migrations.
                // Without this, MigrateAsync would try to apply InitTenant on existing tenants
                // (whose tables already exist from old squashed migrations), causing it to fail
                // and preventing new migrations (like AddChatTables) from being applied.
                await ReconcileMigrationHistoryAsync(tenantContext, settings.SchemaName, cancellationToken);

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

        foreach (var settings in tenants)
        {
            try
            {
                // Drop the entire schema with CASCADE to remove all objects inside it.
                // Use NpgsqlDataSource for connection — handles Azure AD token auth.
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

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
        // Use NpgsqlDataSource to get a connection — supports Azure AD token auth automatically.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

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
        // Use NpgsqlDataSource for connection — handles Azure AD token auth transparently.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

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
    /// Reconciles migration history for existing tenants that were provisioned with older migrations
    /// that have since been squashed into InitTenant.
    ///
    /// Problem:
    /// When old migrations are squashed/reset into a single InitTenant migration, existing tenants
    /// have the old migration names in their __EFMigrationsHistory table, but the code only knows
    /// about InitTenant. When MigrateAsync runs, it sees InitTenant as "pending" and tries to
    /// create ALL tables — but they already exist → PostgreSQL throws "relation already exists"
    /// → the entire migration fails → new migrations (AddChatTables) never get applied.
    ///
    /// Fix:
    /// Before running MigrateAsync, check if InitTenant is already in the history.
    /// If not (meaning old migrations exist), clear the old entries and insert InitTenant.
    /// This tells EF Core "InitTenant was already applied" so it skips it and only applies
    /// truly new migrations (like AddChatTables).
    ///
    /// Junior note: __EFMigrationsHistory is a table EF Core uses to track which migrations
    /// have been applied to a database. Each row has a MigrationId (e.g., "20260317094659_InitTenant")
    /// and a ProductVersion (e.g., "10.0.0").
    /// </summary>
    private async Task ReconcileMigrationHistoryAsync(
        TenantDbContext tenantContext,
        string schemaName,
        CancellationToken cancellationToken)
    {
        var safeName = SanitizeSchemaName(schemaName);
        const string initTenantMigrationId = "20260317094659_InitTenant";

        try
        {
            // Check if the __EFMigrationsHistory table exists in this tenant schema.
            // If it doesn't exist, this is a fresh tenant — no reconciliation needed
            // (MigrateAsync will create it and apply InitTenant normally).
            var connection = tenantContext.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken);

            await using var checkTableCmd = (NpgsqlCommand)connection.CreateCommand();
            checkTableCmd.CommandText = $@"
                SELECT EXISTS (
                    SELECT 1 FROM information_schema.tables
                    WHERE table_schema = '{safeName}'
                    AND table_name = '__EFMigrationsHistory'
                )";
            var historyTableExists = (bool)(await checkTableCmd.ExecuteScalarAsync(cancellationToken))!;

            if (!historyTableExists)
            {
                _logger.LogDebug(
                    "No __EFMigrationsHistory in schema '{SchemaName}' — fresh tenant, skipping reconciliation",
                    schemaName);
                await connection.CloseAsync();
                return;
            }

            // Check if InitTenant is already recorded as applied.
            // If it is, the history is consistent — no reconciliation needed.
            await using var checkCmd = (NpgsqlCommand)connection.CreateCommand();
            checkCmd.CommandText = $@"
                SELECT COUNT(*) FROM ""{safeName}"".""__EFMigrationsHistory""
                WHERE ""MigrationId"" = '{initTenantMigrationId}'";
            var initTenantCount = (long)(await checkCmd.ExecuteScalarAsync(cancellationToken))!;

            if (initTenantCount > 0)
            {
                _logger.LogDebug(
                    "InitTenant already in migration history for schema '{SchemaName}' — no reconciliation needed",
                    schemaName);
                await connection.CloseAsync();
                return;
            }

            // InitTenant is NOT in history but the tenant has tables (it was provisioned with
            // older migrations). Clear old entries and insert InitTenant to mark it as applied.
            _logger.LogInformation(
                "Reconciling migration history for schema '{SchemaName}': " +
                "replacing old migration entries with InitTenant",
                schemaName);

            // Remove all old migration entries (they reference migrations that no longer exist in code).
            await using var deleteCmd = (NpgsqlCommand)connection.CreateCommand();
            deleteCmd.CommandText = $@"DELETE FROM ""{safeName}"".""__EFMigrationsHistory""";
            var deletedCount = await deleteCmd.ExecuteNonQueryAsync(cancellationToken);

            // Insert InitTenant as "already applied" so MigrateAsync skips it.
            await using var insertCmd = (NpgsqlCommand)connection.CreateCommand();
            insertCmd.CommandText = $@"
                INSERT INTO ""{safeName}"".""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ('{initTenantMigrationId}', '10.0.0')";
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation(
                "Migration history reconciled for schema '{SchemaName}': removed {DeletedCount} old entries, " +
                "inserted InitTenant. New migrations will now be applied correctly.",
                schemaName, deletedCount);

            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            // Don't fail the entire migration if reconciliation fails — log and let MigrateAsync try.
            // It might still work if the history is actually consistent.
            _logger.LogWarning(ex,
                "Migration history reconciliation failed for schema '{SchemaName}' — " +
                "MigrateAsync will attempt to proceed anyway",
                schemaName);
        }
    }

    /// <summary>
    /// Creates a new TenantDbContext configured for a specific schema.
    /// Used during provisioning and migration operations.
    ///
    /// IMPORTANT: Migration files are generated WITHOUT explicit schema names (no `schema:` parameter).
    /// This means MigrateAsync() would normally create tables in the default "public" schema.
    /// To redirect migrations to the correct tenant schema, we set PostgreSQL's `search_path`
    /// on the underlying NpgsqlDataSource connection string. This tells PostgreSQL to resolve
    /// all unqualified table references to the tenant schema instead of "public".
    ///
    /// The search_path approach is cleaner than hardcoding schema names in migration files,
    /// because it allows the SAME migration to be applied to ANY tenant schema dynamically.
    /// </summary>
    private TenantDbContext CreateTenantContext(string schemaName)
    {
        var safeName = SanitizeSchemaName(schemaName);

        // Build a new NpgsqlDataSource with search_path pointing to the tenant schema.
        // This ensures that MigrateAsync() CREATE TABLE statements (which have no explicit schema)
        // are created in the tenant schema, not in "public".
        // We also include "public" in the search_path as a fallback for shared extensions/functions.
        var connStringBuilder = new NpgsqlConnectionStringBuilder(_dataSource.ConnectionString)
        {
            SearchPath = $"\"{safeName}\", public"
        };

        // Create a per-tenant NpgsqlDataSource — needed for search_path override.
        // Azure AD token auth is inherited from the connection string (no password needed).
        var useAzureAd = _configuration.GetValue<bool>("UseAzureAdAuthentication");
        var tenantDataSource = useAzureAd
            ? CreateAzureDataSourceFromConnectionString(connStringBuilder.ToString())
            : new NpgsqlDataSourceBuilder(connStringBuilder.ToString()).Build();

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(tenantDataSource, b =>
            {
                b.MigrationsAssembly("Fakvio.Infrastructure");
                b.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
                // Set the migrations history table in the tenant schema.
                // Without this, all tenants share one __EFMigrationsHistory in "public",
                // causing conflicts (tenant_1 migration marks as applied → tenant_2 skips it).
                b.MigrationsHistoryTable("__EFMigrationsHistory", safeName);
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
    /// Creates an NpgsqlDataSource with Azure AD token auth from a connection string.
    /// Similar to ServiceCollectionExtensions.CreateAzureDataSource but accepts
    /// a custom connection string (with modified search_path for tenant schema).
    /// </summary>
    private static NpgsqlDataSource CreateAzureDataSourceFromConnectionString(string connectionString)
    {
        var credential = new Azure.Identity.DefaultAzureCredential();
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

        dataSourceBuilder.UsePeriodicPasswordProvider(
            async (_, cancellationToken) =>
            {
                var tokenRequest = new Azure.Core.TokenRequestContext(
                    ["https://ossrdbms-aad.database.windows.net/.default"]);
                var token = await credential.GetTokenAsync(tokenRequest, cancellationToken);
                return token.Token;
            },
            successRefreshInterval: TimeSpan.FromMinutes(55),
            failureRefreshInterval: TimeSpan.FromSeconds(10));

        return dataSourceBuilder.Build();
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

        // Pre-fetch all code table data from master DB.
        // Only tables that have records in master will be cleared and re-seeded.
        // If master has no records for a table, the migration-seeded defaults are preserved.
        var vatRates = await _masterContext.VatRate
            .AsNoTracking()
            .Where(v => v.IsActive)
            .ToListAsync(cancellationToken);

        var currencies = await _masterContext.Currency
            .AsNoTracking()
            .Where(c => c.IsActive)
            .ToListAsync(cancellationToken);

        var formats = await _masterContext.NumberSequenceFormat
            .AsNoTracking()
            .Where(f => f.IsActive)
            .ToListAsync(cancellationToken);

        var templates = await _masterContext.ContentTemplate
            .AsNoTracking()
            .Where(t => t.IsActive)
            .ToListAsync(cancellationToken);

        _logger.LogInformation(
            "Code tables from master: {VatRates} VAT rates, {Currencies} currencies, " +
            "{Formats} number sequence formats, {Templates} content templates",
            vatRates.Count, currencies.Count, formats.Count, templates.Count);

        // Only clear and re-seed tables that have records in master.
        // This prevents destroying migration-seeded defaults when master hasn't been migrated yet.

        if (vatRates.Count > 0)
        {
            await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"VatRate\"", cancellationToken);
            await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"VatRate_Id_seq\" RESTART WITH 1", cancellationToken);

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
        }
        else
        {
            _logger.LogWarning("Master DB has no active VAT rates — keeping migration-seeded defaults in tenant");
        }

        if (currencies.Count > 0)
        {
            await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"Currency\"", cancellationToken);
            await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"Currency_Id_seq\" RESTART WITH 1", cancellationToken);

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
        }
        else
        {
            _logger.LogWarning("Master DB has no active currencies — keeping migration-seeded defaults in tenant");
        }

        if (formats.Count > 0)
        {
            // NumberSequence references NumberSequenceFormat via FK, so delete sequences first.
            await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"NumberSequence\"", cancellationToken);
            await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"NumberSequenceFormat\"", cancellationToken);
            await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"NumberSequence_Id_seq\" RESTART WITH 1", cancellationToken);
            await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"NumberSequenceFormat_Id_seq\" RESTART WITH 1", cancellationToken);

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
        }
        else
        {
            _logger.LogWarning("Master DB has no active number sequence formats — keeping migration-seeded defaults in tenant");
        }

        if (templates.Count > 0)
        {
            await tenantContext.Database.ExecuteSqlRawAsync($"DELETE FROM \"{safeName}\".\"ContentTemplate\"", cancellationToken);
            await tenantContext.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE IF EXISTS \"{safeName}\".\"ContentTemplate_Id_seq\" RESTART WITH 1", cancellationToken);

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
        }
        else
        {
            _logger.LogWarning("Master DB has no active content templates — keeping migration-seeded defaults in tenant");
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
