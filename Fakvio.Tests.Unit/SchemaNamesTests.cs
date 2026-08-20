using Fakvio.Infrastructure.Data;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="SchemaNames.Sanitize"/>.
///
/// The method itself is not new logic — its body is an unchanged extraction from
/// <c>TenantProvisioningService.SanitizeSchemaName</c> (PR #142) — but it had zero direct
/// test coverage before this PR (only exercised indirectly, e.g. via
/// <c>NpgsqlDataSourceFactoryTests.GetForSchema_SanitizesSqlInjectionAttempt</c>). Since it
/// is now a public, independently reusable API, it deserves tests of its own.
/// </summary>
public class SchemaNamesTests
{
    [Fact]
    public void Sanitize_AlreadyValidLowercaseName_ReturnsUnchanged()
    {
        SchemaNames.Sanitize("tenant_42").ShouldBe("tenant_42");
    }

    [Fact]
    public void Sanitize_UppercaseName_IsLowercased()
    {
        SchemaNames.Sanitize("Tenant_ABC").ShouldBe("tenant_abc");
    }

    [Theory]
    [InlineData("tenant-1", "tenant1")]
    [InlineData("tenant 1", "tenant1")]
    [InlineData("tenant.1", "tenant1")]
    [InlineData("tenant_1\"; DROP TABLE users; --", "tenant_1droptableusers")]
    public void Sanitize_StripsNonAlphanumericUnderscoreCharacters(string input, string expected)
    {
        SchemaNames.Sanitize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("tenant_", 42, "tenant_42")]   // canonical prefix — the appsettings default
    [InlineData("Tenant_", 42, "tenant_42")]   // non-canonical prefix — must still canonicalize
    [InlineData("TENANT-", 42, "tenant42")]    // separator stripped, letters lowercased
    public void Sanitize_TenantSchemaNameComposition_IsCanonical(
        string tenantPrefix, int companyId, string expected)
    {
        // Mirrors how Fakvio.MigrationTool composes a tenant schema name from
        // Migration:TenantSchemaPrefix + company id. The tool sanitizes ONCE and then uses that
        // single value for all three purposes: CREATE SCHEMA, the migration search_path, and the
        // value persisted into CompanySystemSettings.SchemaName.
        SchemaNames.Sanitize($"{tenantPrefix}{companyId}").ShouldBe(expected);
    }

    [Theory]
    [InlineData("tenant_", 42)]
    [InlineData("Tenant_", 42)]
    [InlineData("TENANT-", 42)]
    public void Sanitize_IsIdempotent_SoStoredSchemaNameMatchesCreatedSchema(
        string tenantPrefix, int companyId)
    {
        // This is the invariant that keeps a migrated tenant reachable at runtime.
        // The physical schema is created through Sanitize, while TenantDbContextFactory later
        // uses the value stored in CompanySystemSettings.SchemaName VERBATIM. Storing an
        // already-sanitized name is only safe because sanitizing it again is a no-op — with a
        // non-canonical prefix such as "Tenant_" a raw stored value ("Tenant_42") would point at
        // a schema that does not exist (the created one is "tenant_42").
        var stored = SchemaNames.Sanitize($"{tenantPrefix}{companyId}");
        var created = SchemaNames.Sanitize(stored);

        created.ShouldBe(stored);
    }

    [Fact]
    public void Sanitize_EmptyString_Throws()
    {
        Should.Throw<InvalidOperationException>(() => SchemaNames.Sanitize(string.Empty));
    }

    [Fact]
    public void Sanitize_OnlyInvalidCharacters_Throws()
    {
        // After stripping, nothing alphanumeric/underscore remains — must fail loudly
        // rather than silently produce an empty (and therefore ambiguous) schema name.
        var ex = Should.Throw<InvalidOperationException>(() => SchemaNames.Sanitize("\"; --"));

        ex.Message.ShouldContain("Invalid schema name");
    }
}
