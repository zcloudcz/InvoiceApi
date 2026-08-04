using Fakvio.Infrastructure.Data;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="DatabaseOptions"/> — precedence resolution (new config key
/// vs. legacy bool) and fail-fast validation of connection string + auth mode combinations.
///
/// Uses <see cref="ConfigurationBuilder.AddInMemoryCollection"/> to build isolated
/// IConfiguration instances per scenario — no real appsettings files are touched.
/// </summary>
public class DatabaseOptionsTests
{
    private const string PasswordConnectionString =
        "Host=localhost;Database=fakvio;Username=fakvio;Password=YourStrong!Passw0rd";

    private const string EntraIdConnectionString =
        "Host=localhost;Database=fakvio;Username=fakvio_managed_identity";

    // Exact production connection string from Fakvio.API\appsettings.json:8. This is a
    // regression guard for the running Azure deploy: validating it in AzureEntraId mode
    // must NEVER throw, or the next deploy fails at startup.
    private const string ProductionEntraIdConnectionString =
        "Host=zcloudpostgresql.postgres.database.azure.com;Database=postgres;Port=5432;" +
        "Username=zahalos_seznam.cz#EXT#@zahalosseznam.onmicrosoft.com;Ssl Mode=Require;";

    private static IConfiguration BuildConfig(Dictionary<string, string?> data) =>
        new ConfigurationBuilder().AddInMemoryCollection(data).Build();

    // ---------------------------------------------------------------------
    // Resolve() — auth mode precedence
    // ---------------------------------------------------------------------

    [Fact]
    public void Resolve_ExplicitAuthModePassword_IsUsed()
    {
        var config = BuildConfig(new()
        {
            ["Database:AuthMode"] = "Password",
            ["ConnectionStrings:DefaultConnection"] = PasswordConnectionString
        });

        var options = DatabaseOptions.Resolve(config);

        options.AuthMode.ShouldBe(DatabaseAuthMode.Password);
        options.AuthModeSource.ShouldBe("Database:AuthMode");
    }

    [Fact]
    public void Resolve_ExplicitAuthModeAzureEntraId_IsUsed()
    {
        var config = BuildConfig(new()
        {
            ["Database:AuthMode"] = "AzureEntraId",
            ["ConnectionStrings:DefaultConnection"] = EntraIdConnectionString
        });

        var options = DatabaseOptions.Resolve(config);

        options.AuthMode.ShouldBe(DatabaseAuthMode.AzureEntraId);
        options.AuthModeSource.ShouldBe("Database:AuthMode");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("PASSWORD")]
    [InlineData("PaSsWoRd")]
    public void Resolve_AuthMode_IsCaseInsensitive(string rawValue)
    {
        var config = BuildConfig(new()
        {
            ["Database:AuthMode"] = rawValue,
            ["ConnectionStrings:DefaultConnection"] = PasswordConnectionString
        });

        var options = DatabaseOptions.Resolve(config);

        options.AuthMode.ShouldBe(DatabaseAuthMode.Password);
    }

    [Fact]
    public void Resolve_LegacyKeyTrue_FallsBackToAzureEntraId()
    {
        var config = BuildConfig(new()
        {
            ["UseAzureAdAuthentication"] = "true",
            ["ConnectionStrings:DefaultConnection"] = EntraIdConnectionString
        });

        var options = DatabaseOptions.Resolve(config);

        options.AuthMode.ShouldBe(DatabaseAuthMode.AzureEntraId);
        options.AuthModeSource.ShouldBe("UseAzureAdAuthentication (legacy)");
    }

    [Fact]
    public void Resolve_LegacyKeyFalse_FallsBackToPassword()
    {
        var config = BuildConfig(new()
        {
            ["UseAzureAdAuthentication"] = "false",
            ["ConnectionStrings:DefaultConnection"] = PasswordConnectionString
        });

        var options = DatabaseOptions.Resolve(config);

        options.AuthMode.ShouldBe(DatabaseAuthMode.Password);
        options.AuthModeSource.ShouldBe("UseAzureAdAuthentication (legacy)");
    }

    [Fact]
    public void Resolve_NeitherKeyPresent_DefaultsToPassword()
    {
        var config = BuildConfig(new()
        {
            ["ConnectionStrings:DefaultConnection"] = PasswordConnectionString
        });

        var options = DatabaseOptions.Resolve(config);

        options.AuthMode.ShouldBe(DatabaseAuthMode.Password);
        options.AuthModeSource.ShouldBe("default");
    }

    [Fact]
    public void Resolve_AgreeingPair_DoesNotThrow_AndSourceIsNewKey()
    {
        var config = BuildConfig(new()
        {
            ["Database:AuthMode"] = "AzureEntraId",
            ["UseAzureAdAuthentication"] = "true",
            ["ConnectionStrings:DefaultConnection"] = EntraIdConnectionString
        });

        var options = DatabaseOptions.Resolve(config);

        options.AuthMode.ShouldBe(DatabaseAuthMode.AzureEntraId);
        options.AuthModeSource.ShouldBe("Database:AuthMode");
    }

    [Fact]
    public void Resolve_ConflictingPair_Throws_NamingBothKeys()
    {
        var config = BuildConfig(new()
        {
            ["Database:AuthMode"] = "Password",
            ["UseAzureAdAuthentication"] = "true",
            ["ConnectionStrings:DefaultConnection"] = PasswordConnectionString
        });

        var ex = Should.Throw<InvalidOperationException>(() => DatabaseOptions.Resolve(config));

        ex.Message.ShouldContain("Database:AuthMode");
        ex.Message.ShouldContain("UseAzureAdAuthentication");
    }

    [Fact]
    public void Resolve_UnknownAuthModeValue_Throws_NamingAllowedValues()
    {
        var config = BuildConfig(new()
        {
            ["Database:AuthMode"] = "Kerberos",
            ["ConnectionStrings:DefaultConnection"] = PasswordConnectionString
        });

        var ex = Should.Throw<InvalidOperationException>(() => DatabaseOptions.Resolve(config));

        ex.Message.ShouldContain("Password");
        ex.Message.ShouldContain("AzureEntraId");
    }

    [Fact]
    public void Resolve_LegacyKey_DoesNotApply_ToNonPrimarySection()
    {
        // "SourceDatabase" is the MigrationTool's second section — the global legacy key
        // must never leak into it, even when it disagrees with the section's own default.
        var config = BuildConfig(new()
        {
            ["UseAzureAdAuthentication"] = "true",
            ["SourceDatabase:ConnectionString"] = PasswordConnectionString
        });

        var options = DatabaseOptions.Resolve(config, sectionName: "SourceDatabase", connectionStringName: "SourceConnection");

        options.AuthMode.ShouldBe(DatabaseAuthMode.Password);
        options.AuthModeSource.ShouldBe("default");
    }

    // ---------------------------------------------------------------------
    // Resolve() — connection string precedence
    // ---------------------------------------------------------------------

    [Fact]
    public void Resolve_DatabaseConnectionString_OverridesDefaultConnection()
    {
        var config = BuildConfig(new()
        {
            ["Database:ConnectionString"] = PasswordConnectionString,
            ["ConnectionStrings:DefaultConnection"] = "Host=should-not-be-used"
        });

        var options = DatabaseOptions.Resolve(config);

        options.ConnectionString.ShouldBe(PasswordConnectionString);
    }

    [Fact]
    public void Resolve_NoConnectionStringAnywhere_Throws()
    {
        var config = BuildConfig(new());

        var ex = Should.Throw<InvalidOperationException>(() => DatabaseOptions.Resolve(config));

        ex.Message.ShouldContain("ConnectionStrings__DefaultConnection");
    }

    // ---------------------------------------------------------------------
    // Validate() — the 6 required cases
    // ---------------------------------------------------------------------

    [Fact]
    public void Validate_MissingConnectionString_Throws_NamingKey()
    {
        var options = new DatabaseOptions { ConnectionString = null };

        var ex = Should.Throw<InvalidOperationException>(() => options.Validate());

        ex.Message.ShouldContain("ConnectionStrings__DefaultConnection");
    }

    [Fact]
    public void Validate_UnparsableConnectionString_WrapsException_NamingKey()
    {
        var options = new DatabaseOptions { ConnectionString = "Host=localhost;Port=notanumber" };

        var ex = Should.Throw<InvalidOperationException>(() => options.Validate());

        ex.InnerException.ShouldNotBeNull();
        ex.Message.ShouldContain("connection string");
    }

    [Fact]
    public void Validate_AzureEntraId_WithPasswordInConnectionString_Throws()
    {
        var options = new DatabaseOptions
        {
            AuthMode = DatabaseAuthMode.AzureEntraId,
            ConnectionString = PasswordConnectionString // has Password=
        };

        var ex = Should.Throw<InvalidOperationException>(() => options.Validate());

        ex.Message.ShouldContain("Password");
    }

    [Fact]
    public void Validate_AzureEntraId_WithEmptyUsername_Throws()
    {
        var options = new DatabaseOptions
        {
            AuthMode = DatabaseAuthMode.AzureEntraId,
            ConnectionString = "Host=localhost;Database=fakvio"
        };

        var ex = Should.Throw<InvalidOperationException>(() => options.Validate());

        ex.Message.ShouldContain("Username");
    }

    [Fact]
    public void Validate_AzureEntraId_ValidConnectionString_DoesNotThrow()
    {
        var options = new DatabaseOptions
        {
            AuthMode = DatabaseAuthMode.AzureEntraId,
            ConnectionString = EntraIdConnectionString
        };

        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_AzureEntraId_WithExactProductionConnectionString_DoesNotThrow()
    {
        // Regression guard for the running Azure deploy: this exact connection string
        // (including the Entra "#EXT#@" principal form) must never fail validation.
        var options = new DatabaseOptions
        {
            AuthMode = DatabaseAuthMode.AzureEntraId,
            ConnectionString = ProductionEntraIdConnectionString
        };

        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Password_WithEmptyPassword_NoPassfile_NotAllowed_Throws()
    {
        var options = new DatabaseOptions
        {
            AuthMode = DatabaseAuthMode.Password,
            ConnectionString = "Host=localhost;Database=fakvio;Username=fakvio",
            AllowPasswordlessConnectionString = false
        };

        var ex = Should.Throw<InvalidOperationException>(() => options.Validate());

        ex.Message.ShouldContain("Password");
    }

    [Fact]
    public void Validate_Password_WithPassfile_DoesNotThrow()
    {
        var options = new DatabaseOptions
        {
            AuthMode = DatabaseAuthMode.Password,
            ConnectionString = "Host=localhost;Database=fakvio;Username=fakvio;Passfile=/home/fakvio/.pgpass"
        };

        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Password_WithAllowPasswordlessConnectionString_DoesNotThrow()
    {
        var options = new DatabaseOptions
        {
            AuthMode = DatabaseAuthMode.Password,
            ConnectionString = "Host=localhost;Database=fakvio;Username=fakvio",
            AllowPasswordlessConnectionString = true
        };

        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Password_UnixSocketHost_DoesNotThrow_EvenWithoutPassword()
    {
        var options = new DatabaseOptions
        {
            AuthMode = DatabaseAuthMode.Password,
            ConnectionString = "Host=/var/run/postgresql;Database=fakvio;Username=fakvio"
        };

        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Password_WithPassword_DoesNotThrow()
    {
        var options = new DatabaseOptions
        {
            AuthMode = DatabaseAuthMode.Password,
            ConnectionString = PasswordConnectionString
        };

        Should.NotThrow(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveNumericValue_Throws_NamingKeyAndValue(int badValue)
    {
        var options = new DatabaseOptions
        {
            ConnectionString = PasswordConnectionString,
            MaxRetryCount = badValue
        };

        var ex = Should.Throw<InvalidOperationException>(() => options.Validate());

        ex.Message.ShouldContain(nameof(DatabaseOptions.MaxRetryCount));
        ex.Message.ShouldContain(badValue.ToString());
    }
}
