// ============================================================================
// StartupState — what the host managed to do before it began serving requests.
//
// Both hosts (Fakvio.API and Fakvio.Functions) do their database bring-up in a
// background task and start accepting traffic immediately (see the comment block in
// Fakvio.Functions/Program.cs for why awaiting it kills the Functions worker). That
// leaves a window where the process is "up" but the database path is not usable yet,
// and every request that lands in it fails in a way that looks like an outage.
//
// This class is the one place that window is recorded, so the pipeline can answer
// "not yet" instead of failing, and /api/diagnostic/health can say what happened.
// ============================================================================

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Process-wide record of the startup sequence. Static on purpose: there is exactly one
/// startup per process, it happens before the DI container serves any request scope, and
/// both the Functions middleware pipeline and the API health endpoint have to read it.
/// </summary>
public static class StartupState
{
    /// <summary>
    /// True once the host has finished trying to make the database reachable.
    ///
    /// IMPORTANT — this is NOT a claim that the database works. It means "the bring-up
    /// step is over, so a failure from here on is a real failure and not a race". On the
    /// Functions host that step is the Tailscale tunnel (the connection string points at a
    /// loopback port that does not exist until the forwarder binds); on the API host there
    /// is no tunnel, so it is set immediately.
    ///
    /// Deliberately set even when bring-up FAILED: a gate that stays closed on failure
    /// would turn a broken tunnel into a total blackout with no error to diagnose. After
    /// the attempt, the normal error paths and the health endpoint take over.
    /// </summary>
    public static bool DatabaseReady { get; private set; }

    /// <summary>Outcome of the startup master-database migration: null = still running.</summary>
    public static bool? MigrationSucceeded { get; private set; }

    /// <summary>Message of the exception that failed the startup migration, if any.</summary>
    public static string? MigrationError { get; private set; }

    /// <summary>When the startup migration finished (UTC), successfully or not.</summary>
    public static DateTime? MigrationCompletedAt { get; private set; }

    /// <summary>
    /// Called by both hosts once the database bring-up attempt has concluded — from a
    /// <c>finally</c> block, so a failed tunnel opens the gate too (see <see cref="DatabaseReady"/>).
    /// </summary>
    public static void MarkDatabaseReady() => DatabaseReady = true;

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
        DatabaseReady = false;
        MigrationSucceeded = null;
        MigrationError = null;
        MigrationCompletedAt = null;
    }
}
