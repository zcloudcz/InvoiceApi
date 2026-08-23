using Fakvio.Infrastructure.Data;
using Shouldly;

namespace Fakvio.Tests.MigrationTool;

/// <summary>
/// Pins the one invariant the migration tool's composition root has to hold:
/// <b>the tenant schema name written into <c>CompanySystemSettings.SchemaName</c> must name
/// the schema the tool actually created.</b>
///
/// Why this needs a live database (issue #136, PR #166 review round 1): the physical schema
/// is created through <see cref="SchemaNames.Sanitize"/>, which lowercases. If the persisted
/// name were composed separately from the raw <c>Migration:TenantSchemaPrefix</c>, a
/// non-canonical prefix such as <c>"Tenant_"</c> would create <c>tenant_42</c> but store
/// <c>Tenant_42</c>. The runtime <c>TenantDbContextFactory</c> uses the stored name verbatim,
/// so the freshly migrated tenant would be unreachable — silently.
/// <c>DataIntegrityVerifier</c> cannot catch it either: it sanitizes the stored name before
/// looking the schema up, finds the data and reports PASS.
///
/// Why the existing unit tests do not cover it (review round 2, measured): reverting the fix
/// in <c>DataMigrationService.MigrateCompanyAsync</c> left <c>SchemaNamesTests</c> green,
/// because no test project referenced <c>Fakvio.MigrationTool</c> at all. Those tests document
/// <see cref="SchemaNames.Sanitize"/>; only a test that runs the real call site can pin the
/// composition. Hence this project and this class.
/// </summary>
public class TenantSchemaCanonicalizationTests : LiveMigrationToolTest
{
    /// <summary>A table every provisioned tenant schema must own — used as the "did migrations run here" probe.</summary>
    private const string TenantOwnedTable = "Invoice";

    /// <summary>
    /// The invariant in its purest form: whatever name ends up in CompanySystemSettings must
    /// name a schema that actually exists. That is precisely what runtime tenant resolution
    /// relies on, and precisely what a second, un-canonicalized composition breaks.
    /// </summary>
    [SkippableFact]
    public async Task Migration_WithNonCanonicalSchemaPrefix_PersistsTheSchemaNameItCreated()
    {
        SkipIfDatabaseUnavailable();

        var settings = await RunMigrationAndReadSettingsAsync();

        (await SchemaExistsAsync(settings.SchemaName)).ShouldBeTrue(
            $"CompanySystemSettings.SchemaName is '{settings.SchemaName}', but no such schema exists. " +
            "The migration created its schema through SchemaNames.Sanitize and persisted a " +
            "differently composed name, so this tenant is unreachable at runtime.");
    }

    /// <summary>
    /// The same defect stated as a property of the stored value alone: it must already be
    /// canonical, so sanitizing it again changes nothing. This is the form a future reader can
    /// check without a schema lookup, and it also fails on a merely partial normalization.
    /// </summary>
    [SkippableFact]
    public async Task Migration_WithNonCanonicalSchemaPrefix_PersistsACanonicalSchemaName()
    {
        SkipIfDatabaseUnavailable();

        var settings = await RunMigrationAndReadSettingsAsync();

        settings.SchemaName.ShouldBe(SchemaNames.Sanitize(settings.SchemaName));
        settings.SchemaName.ShouldBe(SchemaNames.Sanitize($"{NonCanonicalTenantPrefix}{settings.CompanyId}"));
    }

    /// <summary>
    /// Guards the other half of the change (issue #136: <c>includePublicInSearchPath: false</c>).
    /// The tenant DbContext is built without an explicit schema and resolves purely through
    /// search_path, so the tenant migrations must land inside the tenant schema. Were "public"
    /// — here the master schema — in that path, a table still missing from the half-built tenant
    /// schema would silently resolve to the master one and tenant data would be written to master.
    /// </summary>
    [SkippableFact]
    public async Task Migration_RunsTenantMigrationsInsideTheTenantSchemaOnly()
    {
        SkipIfDatabaseUnavailable();

        var settings = await RunMigrationAndReadSettingsAsync();

        (await TableExistsAsync(settings.SchemaName, TenantOwnedTable)).ShouldBeTrue(
            $"The tenant schema has no {TenantOwnedTable} table — the tenant migrations did not run inside it.");
        (await TableExistsAsync(MasterSchema, TenantOwnedTable)).ShouldBeFalse(
            $"An {TenantOwnedTable} table appeared in the master schema — tenant migrations leaked through search_path.");
    }
}
