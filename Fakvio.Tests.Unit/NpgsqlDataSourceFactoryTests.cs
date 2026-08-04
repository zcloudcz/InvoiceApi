using Fakvio.Infrastructure.Data;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="NpgsqlDataSourceFactory"/>.
///
/// No PostgreSQL server is required: <c>NpgsqlDataSourceBuilder.Build()</c> only parses
/// the connection string and constructs pooling infrastructure — it does not open a
/// network connection until a connection is actually requested from the pool.
/// </summary>
public class NpgsqlDataSourceFactoryTests
{
    private const string PasswordConnectionString =
        "Host=localhost;Database=fakvio;Username=fakvio;Password=YourStrong!Passw0rd";

    private const string EntraIdConnectionString =
        "Host=localhost;Database=fakvio;Username=fakvio_managed_identity";

    private static DatabaseOptions PasswordOptions() => new()
    {
        AuthMode = DatabaseAuthMode.Password,
        ConnectionString = PasswordConnectionString,
        SchemaDataSourceMaxPoolSize = 4
    };

    private static DatabaseOptions AzureEntraIdOptions() => new()
    {
        AuthMode = DatabaseAuthMode.AzureEntraId,
        ConnectionString = EntraIdConnectionString,
        SchemaDataSourceMaxPoolSize = 4
    };

    // ---------------------------------------------------------------------
    // Password mode never touches the Azure credential / token provider seam
    // ---------------------------------------------------------------------

    [Fact]
    public void Ctor_PasswordMode_NeverInvokesAccessTokenProvider()
    {
        var tokenProviderCalled = false;
        Func<CancellationToken, ValueTask<string>> tokenProvider = _ =>
        {
            tokenProviderCalled = true;
            throw new InvalidOperationException("Token provider must not be called in Password mode.");
        };

        using var factory = new NpgsqlDataSourceFactory(PasswordOptions(), tokenProvider);

        // Root is built eagerly in the constructor. If the token provider had been wired
        // up (i.e. AzureEntraId branch mistakenly taken), Npgsql would call it lazily on
        // first connection — but simply constructing the data source must never call it.
        tokenProviderCalled.ShouldBeFalse();
        factory.AuthMode.ShouldBe(DatabaseAuthMode.Password);
    }

    [Fact]
    public void GetForSchema_PasswordMode_NeverInvokesAccessTokenProvider()
    {
        var tokenProviderCalled = false;
        Func<CancellationToken, ValueTask<string>> tokenProvider = _ =>
        {
            tokenProviderCalled = true;
            throw new InvalidOperationException("Token provider must not be called in Password mode.");
        };

        using var factory = new NpgsqlDataSourceFactory(PasswordOptions(), tokenProvider);

        factory.GetForSchema("tenant_1");

        tokenProviderCalled.ShouldBeFalse();
    }

    // ---------------------------------------------------------------------
    // Root — connection string reflects auth mode
    // ---------------------------------------------------------------------

    [Fact]
    public void Root_PasswordMode_ActuallyOpensConnection_UsingThePasswordFromTheConnectionString()
    {
        // NpgsqlDataSource.ConnectionString always omits the password (Npgsql's own security
        // default, regardless of auth mode — verified: even with Password mode, "Password=..."
        // is stripped from the round-tripped ConnectionString). So we cannot assert password
        // presence by reading Root.ConnectionString back. Instead assert on the SOURCE
        // connection string actually handed to NpgsqlDataSourceBuilder: DatabaseOptions still
        // has it, proving the factory did not silently drop it before building.
        var options = PasswordOptions();
        using var factory = new NpgsqlDataSourceFactory(options);

        var builder = new NpgsqlConnectionStringBuilder(factory.Root.ConnectionString);
        builder.Password.ShouldBeNullOrEmpty("Npgsql never round-trips the password back out of NpgsqlDataSource.ConnectionString.");

        var sourceBuilder = new NpgsqlConnectionStringBuilder(options.ConnectionString!);
        sourceBuilder.Password.ShouldBe("YourStrong!Passw0rd", "the ORIGINAL options connection string must still carry the password.");
    }

    [Fact]
    public void Root_AzureEntraIdMode_HasNoPasswordInConnectionString()
    {
        // The token provider seam is provided so the DefaultAzureCredential probe chain
        // (IMDS, az login, ...) never runs during a unit test.
        Func<CancellationToken, ValueTask<string>> tokenProvider = _ => ValueTask.FromResult("fake-token");

        using var factory = new NpgsqlDataSourceFactory(AzureEntraIdOptions(), tokenProvider);

        var builder = new NpgsqlConnectionStringBuilder(factory.Root.ConnectionString);
        builder.Password.ShouldBeNullOrEmpty();
    }

    // ---------------------------------------------------------------------
    // GetForSchema — search_path composition
    // ---------------------------------------------------------------------

    [Fact]
    public void GetForSchema_IncludePublicTrue_ComposesSearchPathWithPublic()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        var source = factory.GetForSchema("tenant_42", includePublicInSearchPath: true);

        var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        builder.SearchPath.ShouldBe("\"tenant_42\", public");
    }

    [Fact]
    public void GetForSchema_IncludePublicFalse_ComposesSearchPathWithoutPublic()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        var source = factory.GetForSchema("tenant_42", includePublicInSearchPath: false);

        var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        builder.SearchPath.ShouldBe("\"tenant_42\"");
    }

    [Fact]
    public void GetForSchema_SanitizesSqlInjectionAttempt()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        var source = factory.GetForSchema("tenant_1\"; DROP TABLE users; --");

        var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        builder.SearchPath.ShouldBe("\"tenant_1droptableusers\", public");
    }

    [Fact]
    public void GetForSchema_AppliesConfiguredMaxPoolSize()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        var source = factory.GetForSchema("tenant_1");

        var builder = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        builder.MaxPoolSize.ShouldBe(4);
    }

    // ---------------------------------------------------------------------
    // Leak fix: same schema/flag -> same instance; different schema/flag -> different instance
    // ---------------------------------------------------------------------

    [Fact]
    public void GetForSchema_SameSchemaAndFlag_ReturnsSameInstance()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        var first = factory.GetForSchema("tenant_1");
        var second = factory.GetForSchema("tenant_1");

        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public void GetForSchema_DifferentSchema_ReturnsDifferentInstance()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        var tenant1 = factory.GetForSchema("tenant_1");
        var tenant2 = factory.GetForSchema("tenant_2");

        ReferenceEquals(tenant1, tenant2).ShouldBeFalse();
    }

    [Fact]
    public void GetForSchema_SameSchema_DifferentIncludePublicFlag_ReturnsDifferentInstance()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        var withPublic = factory.GetForSchema("tenant_1", includePublicInSearchPath: true);
        var withoutPublic = factory.GetForSchema("tenant_1", includePublicInSearchPath: false);

        ReferenceEquals(withPublic, withoutPublic).ShouldBeFalse();
    }

    /// <summary>
    /// Thread-safety proof: 200 concurrent GetForSchema calls for the SAME schema must all
    /// observe the SAME NpgsqlDataSource instance. A bare
    /// ConcurrentDictionary.GetOrAdd(key, valueFactory) does not guarantee this under
    /// contention — the value factory can run more than once, producing several distinct
    /// (leaked) instances. Lazy&lt;T&gt; with ExecutionAndPublication is what prevents that.
    /// </summary>
    [Fact]
    public void GetForSchema_ConcurrentCallsForSameSchema_AllReturnSameInstance()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());
        var results = new NpgsqlDataSource[200];

        Parallel.For(0, 200, i =>
        {
            results[i] = factory.GetForSchema("tenant_concurrent");
        });

        results.Distinct().Count().ShouldBe(1);
    }

    // ---------------------------------------------------------------------
    // Evict
    // ---------------------------------------------------------------------

    [Fact]
    public void Evict_DisposesTheCachedDataSource()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());
        var source = factory.GetForSchema("tenant_evict");

        factory.Evict("tenant_evict");

        Should.Throw<ObjectDisposedException>(() => source.OpenConnection());
    }

    [Fact]
    public void Evict_ThenGetForSchemaAgain_BuildsAFreshInstance()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());
        var first = factory.GetForSchema("tenant_evict_2");

        factory.Evict("tenant_evict_2");
        var second = factory.GetForSchema("tenant_evict_2");

        ReferenceEquals(first, second).ShouldBeFalse();
    }

    [Fact]
    public void Evict_UnknownSchema_DoesNotThrow()
    {
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        Should.NotThrow(() => factory.Evict("never_requested"));
    }

    // ---------------------------------------------------------------------
    // Disposal
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        await Should.NotThrowAsync(async () =>
        {
            await factory.DisposeAsync();
            await factory.DisposeAsync();
        });
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        Should.NotThrow(() =>
        {
            factory.Dispose();
            factory.Dispose();
        });
    }

    [Fact]
    public async Task DisposeAsync_AlsoDisposesRoot()
    {
        var factory = new NpgsqlDataSourceFactory(PasswordOptions());
        var root = factory.Root;

        await factory.DisposeAsync();

        Should.Throw<ObjectDisposedException>(() => root.OpenConnection());
    }

    [Fact]
    public void GetForSchema_AfterDispose_Throws()
    {
        var factory = new NpgsqlDataSourceFactory(PasswordOptions());
        factory.Dispose();

        Should.Throw<ObjectDisposedException>(() => factory.GetForSchema("tenant_1"));
    }
}
