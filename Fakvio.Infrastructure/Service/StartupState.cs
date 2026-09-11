// ============================================================================
// StartupState — what the host managed to do before it began serving requests.
//
// The API host applies the master-database migration synchronously in Program.cs, but
// tolerates a failure (logs it and keeps serving) so the process stays diagnosable instead
// of crash-looping behind the App Service startup probe. This class records the outcome so
// /api/diagnostic/health can say what happened.
// ============================================================================

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Process-wide record of the startup migration. Static on purpose: there is exactly one
/// startup per process, it happens before the DI container serves any request scope, and
/// the health endpoint has to read it.
/// </summary>
public static class StartupState
{
    /// <summary>Outcome of the startup master-database migration: null = still running.</summary>
    public static bool? MigrationSucceeded { get; private set; }

    /// <summary>Message of the exception that failed the startup migration, if any.</summary>
    public static string? MigrationError { get; private set; }

    /// <summary>When the startup migration finished (UTC), successfully or not.</summary>
    public static DateTime? MigrationCompletedAt { get; private set; }

    /// <summary>Records the outcome of the startup master-database migration.</summary>
    public static void MarkMigration(bool succeeded, string? error = null)
    {
        MigrationSucceeded = succeeded;
        MigrationError = error;
        MigrationCompletedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Resets everything. Only for tests — each test needs a clean slate because the state
    /// is static and therefore shared across every test in the same process.
    /// </summary>
    internal static void ResetForTests()
    {
        MigrationSucceeded = null;
        MigrationError = null;
        MigrationCompletedAt = null;
    }
}
