using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Selects how the application authenticates against PostgreSQL.
/// </summary>
public enum DatabaseAuthMode
{
    /// <summary>
    /// Standard username/password authentication, read from the connection string.
    /// This is the default (value 0) on purpose: "no configuration at all" behaves
    /// exactly like the legacy `GetValue&lt;bool&gt;("UseAzureAdAuthentication")` default of
    /// `false` — self-hosted PostgreSQL with a plain connection string.
    /// </summary>
    Password = 0,

    /// <summary>
    /// Microsoft Entra ID (Azure AD) token-based authentication, used against
    /// Azure Database for PostgreSQL Flexible Server. No password is stored anywhere —
    /// a short-lived access token is acquired via <c>DefaultAzureCredential</c> and
    /// refreshed periodically.
    /// </summary>
    AzureEntraId = 1
}

/// <summary>
/// Configuration model for database connectivity and authentication mode.
/// Bound from the "Database" configuration section (see <see cref="Resolve"/>).
///
/// This class intentionally lives in Fakvio.Infrastructure (not Fakvio.Application):
/// <see cref="Validate"/> parses the connection string with
/// <see cref="NpgsqlConnectionStringBuilder"/>, and Fakvio.Application has no
/// reference to Npgsql.
/// </summary>
public class DatabaseOptions
{
    /// <summary>
    /// Which authentication mode to use when connecting to PostgreSQL.
    /// </summary>
    public DatabaseAuthMode AuthMode { get; set; } = DatabaseAuthMode.Password;

    /// <summary>
    /// PostgreSQL connection string. When null, <see cref="Resolve"/> falls back to
    /// the "ConnectionStrings:DefaultConnection" configuration key.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// OAuth scope requested when acquiring an Entra ID access token for PostgreSQL.
    /// Kept configurable (rather than hardcoded) so sovereign clouds (Azure Government,
    /// Azure China, ...) can point at their own resource identifier without a code change.
    /// </summary>
    public string EntraIdTokenScope { get; set; } = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>
    /// How often (in minutes) a successfully acquired Entra ID token is refreshed.
    /// Azure AD tokens expire after 60 minutes; refreshing at 55 leaves a safety margin.
    /// </summary>
    public int TokenRefreshMinutes { get; set; } = 55;

    /// <summary>
    /// How often (in seconds) to retry acquiring an Entra ID token after a failed refresh.
    /// </summary>
    public int TokenFailureRetrySeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of automatic retries for transient PostgreSQL errors
    /// (EF Core's EnableRetryOnFailure).
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Maximum delay (in seconds) between automatic retries for transient PostgreSQL errors.
    /// </summary>
    public int MaxRetryDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Maximum pool size for the per-schema <c>NpgsqlDataSource</c> instances created by
    /// <see cref="INpgsqlDataSourceFactory.GetForSchema"/>. These sources only serve
    /// provisioning/migration work (not the request path), so a small cap keeps the
    /// total connection count under control on self-hosted PostgreSQL, where
    /// `max_connections` defaults to 100.
    /// </summary>
    public int SchemaDataSourceMaxPoolSize { get; set; } = 4;

    /// <summary>
    /// Escape hatch for connection strings that intentionally carry no password
    /// (`.pgpass` file, peer authentication, or a Unix domain socket). When false
    /// (the default), <see cref="Validate"/> rejects a <see cref="DatabaseAuthMode.Password"/>
    /// connection string with no password as a likely misconfiguration.
    /// </summary>
    public bool AllowPasswordlessConnectionString { get; set; }

    /// <summary>
    /// Diagnostic-only: where <see cref="AuthMode"/> came from. Exposed on the health
    /// endpoint so an Azure App Settings rollout (new key vs. legacy key) can be
    /// verified without guessing which config source won.
    /// </summary>
    public string AuthModeSource { get; private set; } = "default";

    /// <summary>
    /// Resolves a <see cref="DatabaseOptions"/> instance from configuration, applying the
    /// precedence rules described below. Does not call <see cref="Validate"/> — callers
    /// decide when to fail fast.
    ///
    /// Connection string precedence:
    ///   "{sectionName}:ConnectionString" → "ConnectionStrings:{connectionStringName}" → throw
    ///
    /// Auth mode precedence:
    ///   "{sectionName}:AuthMode" → "UseAzureAdAuthentication" (legacy bool, primary section only) → Password
    ///
    /// <paramref name="sectionName"/> is parametrized because Fakvio.MigrationTool needs a
    /// second instance for the source database ("SourceDatabase" / "SourceConnection"). The
    /// legacy "UseAzureAdAuthentication" key is global, so it must only apply to the primary
    /// "Database" section — otherwise a source DB using password auth could accidentally pick
    /// up an Azure AD flag meant for the target DB.
    /// </summary>
    /// <param name="configuration">Application configuration root.</param>
    /// <param name="sectionName">Configuration section to bind from. Defaults to "Database".</param>
    /// <param name="connectionStringName">
    /// Key under "ConnectionStrings" to fall back to when the section has no
    /// explicit ConnectionString. Defaults to "DefaultConnection".
    /// </param>
    public static DatabaseOptions Resolve(
        IConfiguration configuration,
        string sectionName = "Database",
        string connectionStringName = "DefaultConnection")
    {
        var section = configuration.GetSection(sectionName);
        var defaults = new DatabaseOptions();

        // Bind property-by-property rather than IConfiguration.Bind(options): Bind() would
        // try to convert "AuthMode" to the enum immediately and throw .NET's generic
        // enum-conversion error on an invalid value, before we get a chance to raise our own
        // descriptive message below (see ParseAuthMode). AuthMode is therefore handled
        // separately, after this initializer.
        var options = new DatabaseOptions
        {
            ConnectionString = section["ConnectionString"],
            EntraIdTokenScope = section.GetValue("EntraIdTokenScope", defaults.EntraIdTokenScope)!,
            TokenRefreshMinutes = section.GetValue("TokenRefreshMinutes", defaults.TokenRefreshMinutes),
            TokenFailureRetrySeconds = section.GetValue("TokenFailureRetrySeconds", defaults.TokenFailureRetrySeconds),
            MaxRetryCount = section.GetValue("MaxRetryCount", defaults.MaxRetryCount),
            MaxRetryDelaySeconds = section.GetValue("MaxRetryDelaySeconds", defaults.MaxRetryDelaySeconds),
            SchemaDataSourceMaxPoolSize = section.GetValue("SchemaDataSourceMaxPoolSize", defaults.SchemaDataSourceMaxPoolSize),
            AllowPasswordlessConnectionString = section.GetValue("AllowPasswordlessConnectionString", defaults.AllowPasswordlessConnectionString)
        };

        // Connection string: explicit section value wins, otherwise fall back to the
        // classic ConnectionStrings:{name} key used everywhere else in the app.
        options.ConnectionString ??= configuration.GetConnectionString(connectionStringName);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                $"Database connection string not configured. Set '{sectionName}:ConnectionString' or " +
                $"'ConnectionStrings:{connectionStringName}' (environment variable " +
                $"ConnectionStrings__{connectionStringName}).");
        }

        // Auth mode: explicit "{sectionName}:AuthMode" always wins if present.
        var authModeRaw = section["AuthMode"];
        if (!string.IsNullOrWhiteSpace(authModeRaw))
        {
            options.AuthMode = ParseAuthMode(authModeRaw, $"{sectionName}:AuthMode");
            options.AuthModeSource = $"{sectionName}:AuthMode";

            // The legacy bool key is global configuration, so it can conflict with an
            // explicit new-style value. Conflicts are only checked on the PRIMARY section —
            // a "SourceDatabase" section (MigrationTool) never reads the legacy key at all,
            // so it cannot conflict with it either.
            if (sectionName == "Database")
            {
                var legacyRaw = configuration["UseAzureAdAuthentication"];
                if (!string.IsNullOrWhiteSpace(legacyRaw) && bool.TryParse(legacyRaw, out var legacyBool))
                {
                    var legacyMode = legacyBool ? DatabaseAuthMode.AzureEntraId : DatabaseAuthMode.Password;
                    if (legacyMode != options.AuthMode)
                    {
                        throw new InvalidOperationException(
                            $"Conflicting database auth mode configuration: 'Database:AuthMode' = " +
                            $"'{options.AuthMode}' but legacy 'UseAzureAdAuthentication' = '{legacyBool}' " +
                            $"({legacyMode}). Delete the legacy 'UseAzureAdAuthentication' key once " +
                            "'Database:AuthMode' is confirmed working.");
                    }
                    // Agreeing pair — passes silently. This is the expected state during
                    // an Azure rollout where both keys are briefly present (see runbook).
                }
            }
        }
        else if (sectionName == "Database" && bool.TryParse(configuration["UseAzureAdAuthentication"], out var legacyOnly))
        {
            options.AuthMode = legacyOnly ? DatabaseAuthMode.AzureEntraId : DatabaseAuthMode.Password;
            options.AuthModeSource = "UseAzureAdAuthentication (legacy)";
        }
        else
        {
            options.AuthMode = DatabaseAuthMode.Password;
            options.AuthModeSource = "default";
        }

        return options;
    }

    /// <summary>
    /// Parses the "AuthMode" configuration value case-insensitively, throwing a descriptive
    /// error (naming both allowed values) when the value is not recognized.
    /// </summary>
    private static DatabaseAuthMode ParseAuthMode(string raw, string keyName)
    {
        if (Enum.TryParse<DatabaseAuthMode>(raw, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"Invalid value '{raw}' for '{keyName}'. Allowed values: " +
            $"'{DatabaseAuthMode.Password}' (username/password connection string) or " +
            $"'{DatabaseAuthMode.AzureEntraId}' (Microsoft Entra ID token authentication).");
    }

    /// <summary>
    /// Fail-fast validation, following the same idiom as the connection-string check in
    /// <c>ServiceCollectionExtensions.AddDatabaseContexts</c>: throw with an actionable
    /// message at startup rather than let PostgreSQL/Npgsql fail later with an opaque error.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                "Database connection string not configured. Set 'ConnectionStrings__DefaultConnection' " +
                "or 'Database:ConnectionString'.");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Database connection string is not a valid PostgreSQL connection string " +
                $"('ConnectionStrings:DefaultConnection' / 'Database:ConnectionString'): {ex.Message}", ex);
        }

        if (AuthMode == DatabaseAuthMode.AzureEntraId)
        {
            // A password baked into the connection string alongside Entra ID auth is always
            // a mistake — Npgsql would otherwise fail later with an opaque NotSupportedException
            // when UsePeriodicPasswordProvider collides with a static password.
            if (!string.IsNullOrEmpty(builder.Password))
            {
                throw new InvalidOperationException(
                    "Database auth mode is 'AzureEntraId' but the connection string contains a " +
                    "'Password'. Remove it — the access token is provided at runtime instead." +
                    AuthModeOrigin());
            }

            // Entra ID authentication requires a username matching the Entra principal
            // (e.g. a managed identity name or "user@tenant.onmicrosoft.com").
            if (string.IsNullOrEmpty(builder.Username))
            {
                throw new InvalidOperationException(
                    "Database auth mode is 'AzureEntraId' but the connection string has no 'Username'. " +
                    "Set it to the Entra ID principal name (managed identity or AAD user)." +
                    AuthModeOrigin());
            }
        }
        else
        {
            // Password mode: require an actual password unless the caller explicitly opted
            // into a passwordless connection (.pgpass file, peer auth, or a Unix domain socket).
            var isUnixSocket = !string.IsNullOrEmpty(builder.Host) && builder.Host.StartsWith('/');
            if (string.IsNullOrEmpty(builder.Password) &&
                string.IsNullOrEmpty(builder.Passfile) &&
                !AllowPasswordlessConnectionString &&
                !isUnixSocket)
            {
                throw new InvalidOperationException(
                    "Database auth mode is 'Password' but the connection string has no 'Password' and no " +
                    "'Passfile'. Either set a password, point 'Passfile' at a .pgpass file, connect via a " +
                    "Unix domain socket, or set 'AllowPasswordlessConnectionString' if this is intentional." +
                    AuthModeOrigin());
            }
        }

        ValidatePositive(TokenRefreshMinutes, nameof(TokenRefreshMinutes));
        ValidatePositive(TokenFailureRetrySeconds, nameof(TokenFailureRetrySeconds));
        ValidatePositive(MaxRetryCount, nameof(MaxRetryCount));
        ValidatePositive(MaxRetryDelaySeconds, nameof(MaxRetryDelaySeconds));
        ValidatePositive(SchemaDataSourceMaxPoolSize, nameof(SchemaDataSourceMaxPoolSize));
    }

    /// <summary>
    /// Suffix appended to every auth-mode validation error: names the configuration key the
    /// mode came from. Without it a startup failure only says WHAT is wrong, not WHICH key to
    /// edit — and during the Azure rollout the mode can come from three different places
    /// ("Database:AuthMode", the legacy bool, or the built-in default).
    /// </summary>
    private string AuthModeOrigin() =>
        $" Auth mode '{AuthMode}' was resolved from '{AuthModeSource}'.";

    private static void ValidatePositive(int value, string keyName)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException(
                $"Database configuration value '{keyName}' must be greater than zero, but was '{value}'.");
        }
    }
}
