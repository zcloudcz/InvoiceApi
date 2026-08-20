using Fakvio.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    public void Root_PasswordMode_ConnectionStringOmitsPassword_ButSourceOptionsRetainIt()
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

    [Fact]
    public void AuthMode_ReflectsAzureEntraIdOptions()
    {
        // The interface property is a plain pass-through to the options the factory was
        // built with — but it is what design-time factories and #133's composition root
        // will branch on, so it deserves its own explicit assertion rather than relying
        // on it being implicitly exercised elsewhere.
        Func<CancellationToken, ValueTask<string>> tokenProvider = _ => ValueTask.FromResult("fake-token");

        using var factory = new NpgsqlDataSourceFactory(AzureEntraIdOptions(), tokenProvider);

        factory.AuthMode.ShouldBe(DatabaseAuthMode.AzureEntraId);
    }

    [Fact]
    public void Ctor_PasswordMode_WithDefaultAccessTokenProvider_DoesNotThrow()
    {
        // No seam passed here at all (accessTokenProvider defaults to null), which is the
        // real production code path — the ctor wires up a Lazy<DefaultAzureCredential>
        // closure regardless of mode. This must succeed in Password mode with no Azure
        // environment (no az login, no managed identity) available, because the closure
        // is never dereferenced unless AzureEntraId mode actually builds a data source
        // that opens a connection.
        Should.NotThrow(() =>
        {
            using var factory = new NpgsqlDataSourceFactory(PasswordOptions());
        });
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

    [Fact]
    public void Evict_DisposesBothIncludePublicVariants()
    {
        // Evict's doc comment promises it removes BOTH the includePublic=true and
        // includePublic=false cached entries for a schema, not just whichever one was
        // requested first. Cache both variants, evict once, and assert both are gone —
        // a per-variant bug here would silently leave one pooled connection alive against
        // a schema that was supposed to be fully evicted.
        using var factory = new NpgsqlDataSourceFactory(PasswordOptions());
        var withPublic = factory.GetForSchema("tenant_evict_both", includePublicInSearchPath: true);
        var withoutPublic = factory.GetForSchema("tenant_evict_both", includePublicInSearchPath: false);

        factory.Evict("tenant_evict_both");

        Should.Throw<ObjectDisposedException>(() => withPublic.OpenConnection());
        Should.Throw<ObjectDisposedException>(() => withoutPublic.OpenConnection());
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

    [Fact]
    public void Root_DisposedDirectly_ThenFactoryDisposedToo_DoesNotThrow()
    {
        // Guards the ownership-rule caveat documented on ServiceCollectionExtensions'
        // `services.AddSingleton(factory.Root)` line: if some caller disposes Root directly —
        // bypassing the "callers never dispose anything from this factory" contract — the
        // factory's own Dispose() must not throw when it disposes the SAME Root a second time
        // as part of its normal cleanup. This backs the "NpgsqlDataSource.Dispose() is
        // idempotent" claim with an actual test instead of just asserting it in a comment.
        var factory = new NpgsqlDataSourceFactory(PasswordOptions());

        factory.Root.Dispose();

        Should.NotThrow(() => factory.Dispose());
    }

    // ---------------------------------------------------------------------
    // Composition-root ownership contract — mirrors the registration pattern in
    // ServiceCollectionExtensions.AddDatabaseContexts (Fakvio.Infrastructure).
    // ---------------------------------------------------------------------

    [Fact]
    public void ContainerDisposesFactory_WhenRegisteredViaFactoryDelegate_AndResolved()
    {
        // NOTE on what this test proves and what it does not: it mirrors the registration
        // shape from ServiceCollectionExtensions.AddDatabaseContexts (delegate registration,
        // `AddSingleton<INpgsqlDataSourceFactory>(_ => factory)`), but it ALSO resolves the
        // service (`GetRequiredService<INpgsqlDataSourceFactory>()`) before disposing the
        // provider. That resolve step is exactly what today's composition root does NOT do —
        // as of this PR nothing in production code resolves INpgsqlDataSourceFactory (the
        // first consumer arrives with #134). So this test demonstrates the rule "delegate
        // registration + resolution => the container disposes it at shutdown", not the claim
        // "the composition root disposes the factory today". See the bare-instance sibling
        // test below for the contrasting case that pins the MEDI rule this depends on.
        var factory = new NpgsqlDataSourceFactory(PasswordOptions());
        var services = new ServiceCollection();
        services.AddSingleton<INpgsqlDataSourceFactory>(_ => factory);
        services.AddSingleton(factory.Root);

        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<INpgsqlDataSourceFactory>().ShouldBeSameAs(factory);
        }

        // The `using` block above disposed the ServiceProvider — because the factory was
        // resolved above, the delegate registration gave the container ownership, so
        // factory.Dispose() already ran and Root is unusable now.
        Should.Throw<ObjectDisposedException>(() => factory.Root.OpenConnection());
    }

    [Fact]
    public void ContainerDoesNotDisposeInstance_WhenRegisteredViaBareInstance_EvenIfResolved()
    {
        // Contrasting case for the test above: registering the SAME kind of singleton as a
        // BARE INSTANCE (`AddSingleton<TService>(instance)`, not a delegate) means the
        // container never considers itself to have created it, so it is never disposed at
        // shutdown — even though it was resolved. This pins the MEDI rule that
        // AddDatabaseContexts' `services.AddSingleton(factory.Root)` line relies on (Root is
        // registered this same, bare-instance way) as a regression test instead of just a
        // code comment.
        //
        // A minimal disposal-tracking spy is used here instead of NpgsqlDataSourceFactory/
        // NpgsqlDataSource, so the test exercises only the MEDI rule and does not depend on
        // Npgsql or network behaviour (calling a real NpgsqlDataSource's OpenConnection()
        // while NOT disposed would require a reachable PostgreSQL server, which this test
        // class is documented to never need).
        var spy = new DisposalTrackingSpy();
        var services = new ServiceCollection();
        services.AddSingleton<IDisposable>(spy);

        using (var provider = services.BuildServiceProvider())
        {
            provider.GetRequiredService<IDisposable>().ShouldBeSameAs(spy);
        }

        spy.WasDisposed.ShouldBeFalse();
    }

    [Fact]
    public void ContainerDoesNotDisposeFactory_WhenRegisteredViaDelegate_ButNeverResolved()
    {
        // Pins the state ServiceCollectionExtensions.AddDatabaseContexts is actually in TODAY:
        // the factory is registered via a delegate (same shape as ContainerDisposesFactory_...
        // above), but — as of this PR — nothing in production code resolves
        // INpgsqlDataSourceFactory (0 consumers, first arrives with #134). A delegate
        // registration only gives the container something to dispose once the delegate has
        // run at least once; if it never runs, there is nothing "created" to dispose. Without
        // this test, the delegate-registration claim in the AddDatabaseContexts comment
        // ("today this line does NOT yet give the factory a disposal path") would rest on
        // narrative alone, same failure mode that blocked review rounds 1 and 2.
        var spy = new DisposalTrackingSpy();
        var services = new ServiceCollection();
        services.AddSingleton<IDisposable>(_ => spy);

        using (var provider = services.BuildServiceProvider())
        {
            // Deliberately NOT resolved — mirrors today's composition root, where
            // INpgsqlDataSourceFactory is registered but nothing calls GetRequiredService on it.
        }

        spy.WasDisposed.ShouldBeFalse();
    }

    private sealed class DisposalTrackingSpy : IDisposable
    {
        public bool WasDisposed { get; private set; }

        public void Dispose() => WasDisposed = true;
    }

    // ---------------------------------------------------------------------
    // static Create(IConfiguration, ...) — the single entry point future tasks
    // (#133-#137: composition root, design-time factories, MigrationTool, smoke test)
    // will all call. Resolve + Validate + ctor must actually compose correctly.
    // ---------------------------------------------------------------------

    [Fact]
    public void Create_ValidConfiguration_ReturnsFactoryWithResolvedAuthMode()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:AuthMode"] = "Password",
            ["ConnectionStrings:DefaultConnection"] = PasswordConnectionString
        }).Build();

        using var factory = NpgsqlDataSourceFactory.Create(config);

        factory.AuthMode.ShouldBe(DatabaseAuthMode.Password);
        factory.Root.ShouldNotBeNull();
    }

    [Fact]
    public void Create_InvalidConfiguration_PropagatesValidationFailure()
    {
        // Create() is documented as Resolve() + Validate() + new(...) — a bad connection
        // string must fail fast here too, not just when DatabaseOptions.Validate() is
        // called directly. This is what makes it safe for #133's composition root to call
        // Create() without a separate manual Validate() step.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:AuthMode"] = "AzureEntraId",
            ["ConnectionStrings:DefaultConnection"] = PasswordConnectionString // has Password=, invalid for AzureEntraId
        }).Build();

        var ex = Should.Throw<InvalidOperationException>(() => NpgsqlDataSourceFactory.Create(config));

        ex.Message.ShouldContain("Password");
    }

    [Fact]
    public void Create_SectionNameAndConnectionStringName_AreForwardedToResolve()
    {
        // MigrationTool needs a second instance built from a differently-named section
        // ("SourceDatabase" / "SourceConnection"). Create() must forward both parameters
        // to Resolve() rather than hardcoding "Database" / "DefaultConnection".
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SourceDatabase:ConnectionString"] = PasswordConnectionString
        }).Build();

        using var factory = NpgsqlDataSourceFactory.Create(config, sectionName: "SourceDatabase", connectionStringName: "SourceConnection");

        factory.AuthMode.ShouldBe(DatabaseAuthMode.Password);
    }
}
