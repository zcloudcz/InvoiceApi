using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Fakvio.Tests.MigrationTool;

/// <summary>
/// Covers <c>DataIntegrityVerifier</c> — the second half of issue #136 and, until now, the only
/// production class of the tool with no test at all.
///
/// Two things about it cannot be caught by the compiler, only by running it against a real server:
/// <list type="number">
/// <item>Its DbContexts are built by REFLECTION (<c>CreateContext&lt;T&gt;</c> constructs a
/// <c>DbContextOptionsBuilder&lt;T&gt;</c> and invokes the <c>UseNpgsql(NpgsqlDataSource, …)</c>
/// overload that lives on the NON-generic base class). If that overload ever stops binding, the
/// build stays green and the tool throws in the operator's face mid-verification.</item>
/// <item>Every tenant is read through <c>GetForSchema(schemaName, includePublicInSearchPath: false)</c>,
/// so the verifier's view of a tenant depends entirely on <c>search_path</c> — a property of the
/// connection, not of the C# code.</item>
/// </list>
/// </summary>
public class DataIntegrityVerifierTests : LiveMigrationToolTest
{
    /// <summary>
    /// The end-to-end happy path: migrate, then verify. Passing here means all eight checks ran,
    /// which in turn means the reflected context construction bound, the master data was read
    /// through the root data source and the tenant data through its per-schema one.
    /// </summary>
    [SkippableFact]
    public async Task Verification_AfterASuccessfulMigration_Passes()
    {
        SkipIfDatabaseUnavailable();
        await RunMigrationAndReadSettingsAsync();
        var (verifier, log) = CreateVerifier();

        var verified = await verifier.VerifyAsync();

        verified.ShouldBeTrue(
            $"The verifier failed right after a successful migration:\n{log}");
    }

    /// <summary>
    /// Proves the check above is not vacuous: the verifier has to be able to report a failure at
    /// all. Un-provisioning the freshly migrated tenant breaks the "every settings row is
    /// provisioned" check, which is the cheapest defect to stage that does not corrupt data.
    /// </summary>
    [SkippableFact]
    public async Task Verification_WhenAMigratedTenantIsNotMarkedProvisioned_Fails()
    {
        SkipIfDatabaseUnavailable();
        await RunMigrationAndReadSettingsAsync();
        await UnmarkProvisionedAsync();
        var (verifier, _) = CreateVerifier();

        var verified = await verifier.VerifyAsync();

        verified.ShouldBeFalse(
            "The verifier reported PASS for a tenant that is not provisioned — it is not " +
            "actually checking the master metadata it claims to check.");
    }

    private async Task UnmarkProvisionedAsync()
    {
        await using var master = CreateMasterContext();
        var settings = await master.CompanySystemSettings.SingleAsync();
        settings.IsProvisioned = false;
        await master.SaveChangesAsync();
    }
}
