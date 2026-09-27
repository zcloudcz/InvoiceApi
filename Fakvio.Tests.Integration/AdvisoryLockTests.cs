using System.Net.Sockets;
using Fakvio.Infrastructure.Service;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="AdvisoryLock"/> against a REAL PostgreSQL session.
///
/// Why this needs a real database: the whole point of the primitive is
/// <c>pg_try_advisory_lock</c>'s session-scoped, cross-connection exclusion semantics —
/// nothing in-process (a <c>SemaphoreSlim</c>, a static bool) can stand in for "does the
/// SERVER already have this key locked by a different session". This is the mechanism
/// <c>RecurringInvoiceWorker</c>, <c>ReminderWorker</c> and <c>ImapPollService</c> all rely
/// on to guarantee only one App Service replica runs a generation cycle at a time — without
/// a passing test for it, "no duplicate invoice generation across replicas" is an untested
/// claim about a primitive with zero direct coverage.
///
/// Skipped (not failed) when PostgreSQL is unreachable, same convention as
/// <c>ApiKeyDatabaseConstraintTests</c>.
/// </summary>
public class AdvisoryLockTests : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private const string ConnectionStringEnvVar = "FAKVIO_TEST_POSTGRES";

    private const string SkipReason =
        "PostgreSQL is not reachable — start it with 'docker compose up -d' " +
        "or point " + ConnectionStringEnvVar + " at another instance.";

    private readonly string _connectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;

    private NpgsqlDataSource? _dataSource;
    private bool _databaseAvailable;

    public async Task InitializeAsync()
    {
        _dataSource = NpgsqlDataSource.Create(_connectionString);
        _databaseAvailable = await CanReachPostgreSqlAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
    }

    private async Task<bool> CanReachPostgreSqlAsync()
    {
        try
        {
            await using var connection = await _dataSource!.OpenConnectionAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// A fresh key, generated per test rather than a fixed constant, so tests running in
    /// parallel (or a leaked lock from a previous failed run in the SAME process) can never
    /// contend with each other over the same advisory lock — only the two sessions each test
    /// itself opens ever compete for its key.
    /// </summary>
    private static long NewLockKey() => Random.Shared.NextInt64();

    // ─── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The core exclusion guarantee: while one session holds the key, a second session's
    /// attempt does not block and does not throw — it comes back empty-handed immediately.
    /// This is what stops a second App Service replica from also generating the same
    /// period's recurring invoice.
    /// </summary>
    [SkippableFact]
    public async Task TryAcquireAsync_KeyAlreadyHeldByAnotherSession_ReturnsNull()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);
        var key = NewLockKey();

        await using var holder = await AdvisoryLock.TryAcquireAsync(_dataSource!, key);
        holder.ShouldNotBeNull("the first session must win an uncontended key.");

        var contender = await AdvisoryLock.TryAcquireAsync(_dataSource!, key);

        contender.ShouldBeNull("a key already held by another session must not be acquirable.");
    }

    /// <summary>
    /// Once the holder disposes its handle, PostgreSQL frees the lock — the exact path a
    /// worker's <c>await using</c> takes at the end of a cycle. The next contender must then
    /// succeed instead of staying blocked forever.
    /// </summary>
    [SkippableFact]
    public async Task TryAcquireAsync_AfterHolderDisposes_KeyBecomesAcquirableAgain()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);
        var key = NewLockKey();

        var holder = await AdvisoryLock.TryAcquireAsync(_dataSource!, key);
        holder.ShouldNotBeNull();
        await holder!.DisposeAsync();

        await using var nextHolder = await AdvisoryLock.TryAcquireAsync(_dataSource!, key);

        nextHolder.ShouldNotBeNull("disposing the first handle must release the lock server-side.");
    }

    /// <summary>
    /// Different keys must never contend with each other — otherwise every worker sharing
    /// this primitive (recurring invoices, reminders, IMAP poll) would serialize on a single
    /// global lock instead of each having its own.
    /// </summary>
    [SkippableFact]
    public async Task TryAcquireAsync_DifferentKeys_BothAcquiredConcurrently()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await using var first = await AdvisoryLock.TryAcquireAsync(_dataSource!, NewLockKey());
        await using var second = await AdvisoryLock.TryAcquireAsync(_dataSource!, NewLockKey());

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
    }

    /// <summary>
    /// A crash that never runs the <c>await using</c> disposal must still free the lock —
    /// this is the "crash-safe" claim in the class remarks. Simulated here by closing the
    /// underlying connection directly (bypassing <see cref="AdvisoryLockHandle.DisposeAsync"/>
    /// entirely), which is what a killed process looks like to the server: the connection
    /// just drops.
    /// </summary>
    [SkippableFact]
    public async Task TryAcquireAsync_HolderConnectionDropsWithoutDispose_LockIsFreed()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);
        var key = NewLockKey();

        var holderConnection = await _dataSource!.OpenConnectionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", holderConnection))
        {
            cmd.Parameters.AddWithValue("k", key);
            var acquired = (bool)(await cmd.ExecuteScalarAsync())!;
            acquired.ShouldBeTrue();
        }

        // Simulates a killed process: the connection disappears without an orderly unlock.
        await holderConnection.CloseAsync();

        await using var nextHolder = await AdvisoryLock.TryAcquireAsync(_dataSource!, key);

        nextHolder.ShouldNotBeNull("PostgreSQL must free a session's advisory locks when its connection closes, even without an explicit unlock.");
    }
}
