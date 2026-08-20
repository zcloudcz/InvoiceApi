using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Shared plumbing for the EF Core design-time factories (<see cref="MasterDesignTimeFactory"/>
/// and <see cref="TenantDesignTimeFactory"/>). It is the "composition root" of the
/// <c>dotnet ef</c> process: it builds the configuration those factories run on and owns the
/// single <see cref="NpgsqlDataSource"/> they hand to EF Core.
///
/// Why this exists as its own type: both design-time factories need exactly the same
/// configuration + data source, and duplicating that logic in two files is how the two
/// factories drifted apart in the first place (DRY).
///
/// Why a data source and not a plain connection string: only the data source built by
/// <see cref="NpgsqlDataSourceFactory"/> knows how to authenticate with a Microsoft Entra ID
/// access token. With a bare <c>UseNpgsql(connectionString)</c>, <c>dotnet ef</c> could never
/// reach the Azure database (no password exists there to put in the string).
/// </summary>
internal static class DesignTimeDataSource
{
    /// <summary>
    /// Last-resort connection string, used only when configuration provides none at all
    /// (e.g. <c>dotnet ef</c> invoked from a directory where Fakvio.API/appsettings.json
    /// is not reachable). Points at the local Docker PostgreSQL from docker-compose.yml.
    /// </summary>
    private const string LocalFallbackConnectionString =
        "Host=localhost;Database=fakvio;Username=fakvio;Password=YourStrong!Passw0rd";

    /// <summary>
    /// The factory is deliberately parked in a static field instead of being disposed at
    /// the end of <c>CreateDbContext</c>: the DbContext returned to EF Core outlives that
    /// method and keeps using the data source. Disposing it there would break every
    /// subsequent command. <c>dotnet ef</c> is a short-lived process, so leaving the data
    /// source alive until process exit leaks nothing.
    ///
    /// <see cref="Lazy{T}"/> keeps construction (which reads configuration and can throw a
    /// descriptive validation error) out of the type initializer, where any exception would
    /// be wrapped in a <see cref="TypeInitializationException"/> and hide the real message.
    /// </summary>
    private static readonly Lazy<NpgsqlDataSourceFactory> LazyFactory =
        new(() => NpgsqlDataSourceFactory.Create(BuildConfiguration()));

    /// <summary>
    /// The shared data source handed to EF Core at design time. Authenticates with either a
    /// password or an Entra ID token, depending on the resolved <see cref="DatabaseAuthMode"/>.
    /// Callers must NOT dispose it — see the note on <see cref="LazyFactory"/>.
    /// </summary>
    public static NpgsqlDataSource Root => LazyFactory.Value.Root;

    /// <summary>
    /// Builds the configuration the design-time factories run on: the API's appsettings files
    /// plus environment variables. Environment variables are added last so that a connection
    /// string can be overridden per invocation
    /// (<c>ConnectionStrings__DefaultConnection=... dotnet ef ...</c>) without editing
    /// appsettings.json.
    /// </summary>
    private static IConfiguration BuildConfiguration() =>
        ApplyLocalFallback(new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Fakvio.API"))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables());

    /// <summary>
    /// Appends the local Docker fallback to <paramref name="builder"/>, but only when the
    /// sources already registered on it provide no connection string at all.
    ///
    /// The fallback always sets <c>Database:AuthMode=Password</c> together with the
    /// connection string. That pairing is mandatory, not cosmetic: the fallback carries a
    /// password, and <see cref="DatabaseOptions.Validate"/> rejects a password in
    /// <see cref="DatabaseAuthMode.AzureEntraId"/> mode. Without pinning the mode, the
    /// fallback would be unusable on any machine whose environment says "Azure".
    ///
    /// Exposed (internal) as a seam so the fallback rules can be unit tested over an
    /// in-memory builder, without touching real appsettings files or the process environment.
    /// </summary>
    internal static IConfiguration ApplyLocalFallback(IConfigurationBuilder builder)
    {
        var configuration = builder.Build();

        // Both keys are checked because DatabaseOptions.Resolve accepts either, with
        // "Database:ConnectionString" taking precedence. Looking only at the classic key
        // would apply the fallback — and with it the forced Password mode — on top of a
        // perfectly valid "Database:ConnectionString" pointing at Azure.
        var hasConnectionString =
            !string.IsNullOrWhiteSpace(configuration["Database:ConnectionString"]) ||
            !string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection"));

        if (hasConnectionString)
        {
            return configuration;
        }

        // Re-building the same builder with one more source on top is intentional: the
        // fallback must lose to every real source, so it can only be evaluated once those
        // sources are known to be empty.
        return builder
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = LocalFallbackConnectionString,
                ["Database:AuthMode"] = nameof(DatabaseAuthMode.Password)
            })
            .Build();
    }
}
