using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Creates and owns the <see cref="NpgsqlDataSource"/> instances used throughout the
/// application: one shared "root" data source (used for the request path — master and
/// tenant DbContexts), plus on-demand per-schema data sources with a tenant-specific
/// `search_path` (used only by provisioning and migration code).
///
/// Ownership rule: callers never dispose anything they get back from this factory
/// (<see cref="Root"/> or <see cref="GetForSchema"/>). The factory owns every
/// <see cref="NpgsqlDataSource"/> it creates and disposes them all in
/// <see cref="DisposeAsync"/>/<see cref="Dispose"/>.
/// </summary>
public interface INpgsqlDataSourceFactory
{
    /// <summary>
    /// The authentication mode this factory was built with (Password or AzureEntraId).
    /// </summary>
    DatabaseAuthMode AuthMode { get; }

    /// <summary>
    /// The shared data source used for the request path (MasterDbContext, TenantDbContext
    /// with schema resolved via EF Core's HasDefaultSchema, and every other consumer that
    /// needs a raw NpgsqlConnection).
    /// </summary>
    NpgsqlDataSource Root { get; }

    /// <summary>
    /// Returns a data source whose connection string has `search_path` pointing at the
    /// given tenant schema. Used by provisioning/migration code, where migration SQL
    /// references unqualified table names and therefore relies on `search_path` instead
    /// of EF Core's HasDefaultSchema.
    ///
    /// Data sources are cached per (schema, includePublicInSearchPath) pair and reused —
    /// repeated calls for the same schema/flag combination return the SAME instance.
    /// </summary>
    /// <param name="schemaName">Tenant schema name (sanitized internally via <see cref="SchemaNames.Sanitize"/>).</param>
    /// <param name="includePublicInSearchPath">
    /// Whether "public" should also be included in the search_path, after the tenant schema.
    /// This is NOT cosmetic:
    /// - Provisioning needs "public" in the path (shared extensions/functions) → "tenant_x", public.
    /// - Migration tooling must NOT include it: the migration DbContext is built without an
    ///   explicit Schema, resolving purely via search_path. With "public" present, a table
    ///   missing from a not-yet-migrated tenant schema would silently resolve to the MASTER
    ///   table instead of failing, and migration data would leak into "public".
    /// </param>
    NpgsqlDataSource GetForSchema(string schemaName, bool includePublicInSearchPath = true);

    /// <summary>
    /// Disposes and removes any cached per-schema data source(s) for the given schema
    /// (both the includePublic=true and includePublic=false variants, if present).
    /// Call this after a tenant schema is dropped, so a stale pooled connection can never
    /// be reused against a schema that no longer exists.
    /// </summary>
    void Evict(string schemaName);
}

/// <inheritdoc cref="INpgsqlDataSourceFactory"/>
public sealed class NpgsqlDataSourceFactory : INpgsqlDataSourceFactory, IAsyncDisposable, IDisposable
{
    private readonly DatabaseOptions _options;
    private readonly Func<CancellationToken, ValueTask<string>> _accessTokenProvider;

    // Lazy<T> with ExecutionAndPublication guarantees the value factory runs at most once
    // even under concurrent GetForSchema calls for the same key. A bare
    // ConcurrentDictionary.GetOrAdd(key, factory) does NOT give that guarantee — the
    // factory can run more than once under contention, and the losing NpgsqlDataSource
    // instances would never be disposed. That is exactly the per-tenant data source leak
    // this factory exists to fix.
    private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> _schemaSources = new();

    private bool _disposed;

    /// <inheritdoc />
    public DatabaseAuthMode AuthMode => _options.AuthMode;

    /// <inheritdoc />
    public NpgsqlDataSource Root { get; }

    /// <summary>
    /// Creates a new factory for the given (already-resolved and validated) options.
    /// </summary>
    /// <param name="options">Resolved database options. Callers should call <see cref="DatabaseOptions.Validate"/> first.</param>
    /// <param name="accessTokenProvider">
    /// Test seam: overrides how an Entra ID access token is acquired. Left null in production,
    /// where a real <see cref="DefaultAzureCredential"/> is used. Tests pass a delegate here to
    /// assert token acquisition never happens in <see cref="DatabaseAuthMode.Password"/> mode
    /// (the repository has no InternalsVisibleTo, so a public constructor parameter is the
    /// only available seam).
    /// </param>
    public NpgsqlDataSourceFactory(
        DatabaseOptions options,
        Func<CancellationToken, ValueTask<string>>? accessTokenProvider = null)
    {
        _options = options;

        // Build the token provider closure over a SINGLE Lazy<DefaultAzureCredential>:
        // - In Password mode, the closure is stored but never invoked, so the credential
        //   object (which would otherwise probe IMDS/CLI/VS credentials) is never constructed.
        // - In AzureEntraId mode, every data source built by this factory (Root and every
        //   per-schema source) shares the SAME credential instance and its token cache,
        //   instead of each data source starting its own independent refresh timer.
        if (accessTokenProvider != null)
        {
            _accessTokenProvider = accessTokenProvider;
        }
        else
        {
            var lazyCredential = new Lazy<DefaultAzureCredential>(() => new DefaultAzureCredential());
            _accessTokenProvider = async ct =>
            {
                var tokenRequest = new TokenRequestContext([_options.EntraIdTokenScope]);
                var token = await lazyCredential.Value.GetTokenAsync(tokenRequest, ct);
                return token.Token;
            };
        }

        // Root is built EAGERLY, in the constructor — preserving today's semantics that a
        // bad connection string fails fast at startup, not on the first request.
        Root = BuildDataSource(_options.ConnectionString!);
    }

    /// <summary>
    /// Resolves options from configuration, validates them, and constructs a factory.
    /// This single entry point is what unifies design-time factories, Fakvio.MigrationTool,
    /// and the connectivity smoke test on the same code path.
    /// </summary>
    public static NpgsqlDataSourceFactory Create(
        IConfiguration configuration,
        string sectionName = "Database",
        string connectionStringName = "DefaultConnection")
    {
        var options = DatabaseOptions.Resolve(configuration, sectionName, connectionStringName);
        options.Validate();
        return new NpgsqlDataSourceFactory(options);
    }

    /// <inheritdoc />
    public NpgsqlDataSource GetForSchema(string schemaName, bool includePublicInSearchPath = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var safeName = SchemaNames.Sanitize(schemaName);
        var key = $"{safeName}|{includePublicInSearchPath}";

        var lazy = _schemaSources.GetOrAdd(key, _ => new Lazy<NpgsqlDataSource>(
            () => BuildSchemaDataSource(safeName, includePublicInSearchPath),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    /// <inheritdoc />
    public void Evict(string schemaName)
    {
        var safeName = SchemaNames.Sanitize(schemaName);

        foreach (var includePublic in new[] { true, false })
        {
            var key = $"{safeName}|{includePublic}";
            if (_schemaSources.TryRemove(key, out var lazy) && lazy.IsValueCreated)
            {
                lazy.Value.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds a per-schema data source: same connection string as <see cref="Root"/>, but with
    /// `search_path` overridden to point at the tenant schema, and a capped connection pool
    /// (these sources only serve provisioning/migration, never the request path).
    /// </summary>
    private NpgsqlDataSource BuildSchemaDataSource(string safeSchemaName, bool includePublicInSearchPath)
    {
        // Built from _options.ConnectionString (NOT Root.ConnectionString, which Npgsql
        // normalizes and which may drop fields we still need, e.g. a password).
        var builder = new NpgsqlConnectionStringBuilder(_options.ConnectionString!)
        {
            SearchPath = includePublicInSearchPath ? $"\"{safeSchemaName}\", public" : $"\"{safeSchemaName}\"",
            MaxPoolSize = _options.SchemaDataSourceMaxPoolSize,
            MinPoolSize = 0,
            ConnectionIdleLifetime = 30
        };

        return BuildDataSource(builder.ToString());
    }

    /// <summary>
    /// The single place where Password vs. AzureEntraId is decided. In Password mode this is
    /// a plain data source build; in AzureEntraId mode it additionally wires up periodic
    /// Entra ID token acquisition in place of a static password.
    /// </summary>
    private NpgsqlDataSource BuildDataSource(string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

        if (_options.AuthMode == DatabaseAuthMode.AzureEntraId)
        {
            // UsePeriodicPasswordProvider replaces the static password with a dynamically
            // acquired Azure AD access token, refreshed on the schedule from DatabaseOptions.
            dataSourceBuilder.UsePeriodicPasswordProvider(
                async (_, cancellationToken) => await _accessTokenProvider(cancellationToken),
                successRefreshInterval: TimeSpan.FromMinutes(_options.TokenRefreshMinutes),
                failureRefreshInterval: TimeSpan.FromSeconds(_options.TokenFailureRetrySeconds));
        }

        return dataSourceBuilder.Build();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var lazy in _schemaSources.Values)
        {
            if (lazy.IsValueCreated)
            {
                await lazy.Value.DisposeAsync();
            }
        }
        _schemaSources.Clear();

        await Root.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var lazy in _schemaSources.Values)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Dispose();
            }
        }
        _schemaSources.Clear();

        Root.Dispose();

        GC.SuppressFinalize(this);
    }
}
