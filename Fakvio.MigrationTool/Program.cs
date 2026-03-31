using Fakvio.MigrationTool;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Fakvio Data Migration Tool
///
/// Migrates data from a single PostgreSQL database (the legacy single-DB schema)
/// to the multi-tenant schema-per-tenant architecture (MasterDbContext + per-tenant TenantDbContext).
///
/// Architecture: Single PostgreSQL database with schema-based isolation.
/// Master data lives in the "public" schema, each tenant gets "tenant_{companyId}" schema.
///
/// Usage:
///   dotnet run --project Fakvio.MigrationTool
///   dotnet run --project Fakvio.MigrationTool -- --dry-run
///
/// Configuration:
///   - appsettings.json: SourceConnection (existing single DB), DefaultConnection (shared DB with public schema)
///   - Migration:DryRun: true = log what would happen without writing data
///   - Migration:SkipProvisionedCompanies: true = skip companies already provisioned
///   - Migration:TenantSchemaPrefix: schema name prefix (default: "tenant_")
///
/// Prerequisites:
///   - Source database must be accessible (the existing single PostgreSQL DB)
///   - PostgreSQL user must have CREATE SCHEMA privilege on the target database
///   - Public schema (master) will be used automatically
/// </summary>

// Npgsql 10.x strictly requires DateTimeKind.Utc for "timestamp with time zone" columns.
// Legacy data may contain non-UTC DateTimes — this switch prevents ArgumentException during migration.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// Parse command-line arguments
var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
var verifyOnly = args.Contains("--verify", StringComparer.OrdinalIgnoreCase);

// Build configuration from appsettings.json + environment variables
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

// Override DryRun from command line if specified
if (dryRun)
    configuration["Migration:DryRun"] = "true";

// Set up console logging with color output
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .SetMinimumLevel(LogLevel.Information)
        .AddConsole(options =>
        {
            options.TimestampFormat = "[HH:mm:ss] ";
        });
});

var logger = loggerFactory.CreateLogger<DataMigrationService>();

// Banner
Console.WriteLine("╔═══════════════════════════════════════════════════╗");
Console.WriteLine("║  Fakvio Data Migration Tool                   ║");
Console.WriteLine("║  Single-DB → Multi-Tenant Architecture            ║");
Console.WriteLine("╚═══════════════════════════════════════════════════╝");
Console.WriteLine();

if (verifyOnly)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("  VERIFY MODE — checking data integrity only");
    Console.ResetColor();
    Console.WriteLine();
}
else if (dryRun)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  DRY RUN MODE — no data will be written");
    Console.ResetColor();
    Console.WriteLine();
}

Console.WriteLine($"  Source DB: {MaskConnectionString(configuration.GetConnectionString("SourceConnection") ?? "")}");
Console.WriteLine($"  Target DB: {MaskConnectionString(configuration.GetConnectionString("DefaultConnection") ?? "")}");
Console.WriteLine();

// Confirm before proceeding (unless DRY RUN)
if (!dryRun)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Write("  This will modify databases. Continue? (y/N): ");
    Console.ResetColor();
    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
    if (answer != "y")
    {
        Console.WriteLine("  Migration cancelled.");
        return 1;
    }
    Console.WriteLine();
}

// Execute migration or verification
try
{
    if (verifyOnly)
    {
        // Verification-only mode — checks data integrity without modifying anything
        var verifierLogger = loggerFactory.CreateLogger<DataIntegrityVerifier>();
        var verifier = new DataIntegrityVerifier(configuration, verifierLogger);
        var verified = await verifier.VerifyAsync();
        return verified ? 0 : 1;
    }
    else
    {
        // Full migration mode
        var migrationService = new DataMigrationService(configuration, logger);
        var success = await migrationService.MigrateAsync();

        if (success)
        {
            // Auto-verify after successful migration
            Console.WriteLine();
            var verifierLogger = loggerFactory.CreateLogger<DataIntegrityVerifier>();
            var verifier = new DataIntegrityVerifier(configuration, verifierLogger);
            await verifier.VerifyAsync();
        }

        return success ? 0 : 1;
    }
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Migration failed with unhandled exception");
    return 2;
}

/// <summary>
/// Masks the password in a connection string for safe console output.
/// </summary>
static string MaskConnectionString(string connectionString)
{
    if (string.IsNullOrEmpty(connectionString)) return "(not configured)";

    // Simple masking: replace Password=xxx with Password=***
    var parts = connectionString.Split(';');
    for (var i = 0; i < parts.Length; i++)
    {
        if (parts[i].TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase))
        {
            var eqIndex = parts[i].IndexOf('=');
            if (eqIndex >= 0)
                parts[i] = parts[i][..(eqIndex + 1)] + "***";
        }
    }
    return string.Join(';', parts);
}
