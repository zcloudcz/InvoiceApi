using Npgsql;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Cross-process singleton primitive backed by a PostgreSQL session-level
/// advisory lock (<c>pg_try_advisory_lock</c>).
///
/// Semantics:
///   - The lock is held for the lifetime of the underlying connection (session).
///   - Disposing the returned handle closes the connection → PostgreSQL
///     automatically releases the lock. This is crash-safe — even if the process
///     is killed, the server detects the broken connection and frees the lock.
///   - <see cref="TryAcquireAsync"/> is non-blocking: it returns null immediately
///     when another session already owns the lock. Callers who care about
///     waiting should poll on a timer instead of blocking a worker.
///
/// Why session-level and not transaction-level? Our workers don't run inside a
/// single transaction — they do many independent SaveChanges calls during a
/// cycle — so we need a primitive scoped to the connection, not the tx.
///
/// IMPORTANT: This API takes an <see cref="NpgsqlDataSource"/>, NOT a raw connection
/// string. The DI-registered NpgsqlDataSource handles Azure AD / Managed Identity
/// token acquisition transparently. Bypassing it with <c>new NpgsqlConnection(...)</c>
/// fails in Azure with "no password provided" because connection strings under
/// Entra ID auth have no embedded password — the token is supplied by the data source.
/// </summary>
public static class AdvisoryLock
{
    /// <summary>
    /// Attempts to acquire a session-level advisory lock identified by <paramref name="key"/>.
    /// Returns a disposable handle that releases the lock when disposed, or null
    /// if the lock is already held by another session.
    /// </summary>
    public static async Task<AdvisoryLockHandle?> TryAcquireAsync(
        NpgsqlDataSource dataSource, long key, CancellationToken ct = default)
    {
        // OpenConnectionAsync uses the configured data source — this picks up
        // Azure AD tokens / Managed Identity in production hosts where the
        // connection string has no embedded password.
        var conn = await dataSource.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn);
            cmd.Parameters.AddWithValue("k", key);
            var acquired = (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);

            if (!acquired)
            {
                // Close immediately so we don't hold an idle pool connection.
                await conn.DisposeAsync();
                return null;
            }

            return new AdvisoryLockHandle(conn, key);
        }
        catch
        {
            // Any failure (network hiccup, bad credentials, …) → release connection and bubble up.
            await conn.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// RAII handle for an acquired advisory lock. Disposing releases it.
/// Also explicitly calls <c>pg_advisory_unlock</c> for symmetry and prompt
/// release — the connection Dispose would do it anyway, but explicit
/// unlock makes the intent obvious in server logs.
/// </summary>
public sealed class AdvisoryLockHandle : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly long _key;
    private bool _disposed;

    internal AdvisoryLockHandle(NpgsqlConnection connection, long key)
    {
        _connection = connection;
        _key = key;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", _connection);
            cmd.Parameters.AddWithValue("k", _key);
            await cmd.ExecuteScalarAsync();
        }
        catch
        {
            // Swallow — connection Dispose below will free the lock server-side regardless.
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}
