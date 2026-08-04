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
