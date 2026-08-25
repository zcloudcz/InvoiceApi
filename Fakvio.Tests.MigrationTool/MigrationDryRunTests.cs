using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Fakvio.Tests.MigrationTool;

/// <summary>
/// The acceptance criteria of issue #136 asked for a <c>--dry-run</c> baseline: identical output
/// before and after rewiring the tool onto <c>INpgsqlDataSourceFactory</c>. A byte comparison of
/// console output is not something a test suite can keep honest (the PR deliberately adds an
/// "Auth mode:" line), so the property that baseline was really protecting is pinned instead:
/// <b>a dry run must not write anything to the target database.</b>
///
/// This matters more after the rewiring than before it. A dry run is the operator's way of
/// pointing the tool at production and looking at what it would do; the tool now reaches that
/// database through data sources it builds itself, so "does not write" is worth re-proving
/// against a real server rather than reasoning about from the diff.
/// </summary>
public class MigrationDryRunTests : LiveMigrationToolTest
{
    [SkippableFact]
    public async Task DryRun_LeavesTheTargetDatabaseUntouched()
    {
        SkipIfDatabaseUnavailable();
        // A dry run skips the master migrations itself, so the schema has to be there already —
        // which is also the only situation a dry run is ever used in.
        await ApplyMasterMigrationsAsync();

        var succeeded = await CreateMigrationService(dryRun: true).MigrateAsync();

        succeeded.ShouldBeTrue("The dry run reported errors — see the tool's own logging.");
        await AssertNothingWasWrittenAsync();
    }

    private async Task AssertNothingWasWrittenAsync()
    {
        await using var master = CreateMasterContext();

        (await master.Client.CountAsync()).ShouldBe(0,
            "A dry run copied the issuer into the master database.");
        (await master.CompanySystemSettings.CountAsync()).ShouldBe(0,
            "A dry run created a CompanySystemSettings row.");

        (await ListSchemasOfThisRunAsync()).ShouldBe(
            [SourceSchema, MasterSchema], ignoreOrder: true,
            "A dry run created a tenant schema — the destructive half of the tool ran.");
    }
}
