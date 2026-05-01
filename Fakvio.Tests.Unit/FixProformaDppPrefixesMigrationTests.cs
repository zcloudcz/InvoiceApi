using Fakvio.Infrastructure.Migrations.Tenant;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Shouldly;
using System.Reflection;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the FixProformaDppPrefixes migration (issue #26).
///
/// The migration uses raw SQL UPDATE statements with WHERE guards to make them idempotent.
/// We verify the SQL text directly because:
///   1. We cannot run the migration against InMemoryDatabase (it doesn't support raw SQL).
///   2. The WHERE guards are the critical correctness invariant — removing them would cause
///      the migration to overwrite any custom prefix a user has set.
///
/// These tests act as a "migration contract" — if someone accidentally edits the SQL
/// and removes the idempotency guards, these tests will catch it.
///
/// Technique: MigrationBuilder.Sql() adds a SqlOperation to the builder's Operations list.
/// We call Up()/Down() on a real MigrationBuilder and inspect Operations afterward.
/// </summary>
public class FixProformaDppPrefixesMigrationTests
{
    /// <summary>
    /// Calls Up() or Down() on the migration with a fresh MigrationBuilder,
    /// then extracts all SQL strings from the recorded SqlOperation entries.
    /// MigrationBuilder.Sql() is not virtual, so we read Operations instead of subclassing.
    /// </summary>
    private static List<string> GetMigrationSql(Action<MigrationBuilder> migrationAction)
    {
        // Use a neutral provider — the SQL we're testing is provider-agnostic raw SQL.
        var builder = new MigrationBuilder(activeProvider: "Microsoft.EntityFrameworkCore.SqlServer");
        migrationAction(builder);

        // MigrationBuilder.Sql() records a SqlOperation in the Operations list.
        return builder.Operations
            .OfType<SqlOperation>()
            .Select(op => op.Sql)
            .ToList();
    }

    // Reflection helpers — Up() and Down() are protected on Migration

    private static void CallUp(Migration migration, MigrationBuilder builder)
    {
        var method = typeof(FixProformaDppPrefixes).GetMethod(
            "Up", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(migration, [builder]);
    }

    private static void CallDown(Migration migration, MigrationBuilder builder)
    {
        var method = typeof(FixProformaDppPrefixes).GetMethod(
            "Down", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(migration, [builder]);
    }

    // ─── Up() SQL tests ────────────────────────────────────────────────────────

    [Fact]
    public void Up_ContainsExactlyTwoStatements()
    {
        // Up() should update exactly two rows (Proforma Id=3, TaxReceiptForAdvance Id=4).
        var statements = GetMigrationSql(b => CallUp(new FixProformaDppPrefixes(), b));

        statements.Count.ShouldBe(2,
            "Up() must issue exactly 2 SQL statements — one per document type prefix correction");
    }

    [Fact]
    public void Up_ProformaStatement_SetsPfDashAndGuardsOnOldPF()
    {
        // The WHERE clause "Prefix = 'PF'" ensures the update is idempotent —
        // running it again when prefix is already 'PF-' is a no-op.
        var statements = GetMigrationSql(b => CallUp(new FixProformaDppPrefixes(), b));

        // Proforma is Id=3
        var proformaStatement = statements.FirstOrDefault(s => s.Contains("= 3"));
        proformaStatement.ShouldNotBeNull("Up() must contain an UPDATE for Id=3 (Proforma)");
        proformaStatement.ShouldContain("PF-");   // new prefix (with hyphen)
        proformaStatement.ShouldContain("'PF'");  // old prefix in WHERE guard (idempotency)
    }

    [Fact]
    public void Up_TaxReceiptStatement_SetsDppDashAndGuardsOnOldZF()
    {
        // TaxReceiptForAdvance: "ZF" → "DPP-"
        var statements = GetMigrationSql(b => CallUp(new FixProformaDppPrefixes(), b));

        // TaxReceiptForAdvance is Id=4
        var taxReceiptStatement = statements.FirstOrDefault(s => s.Contains("= 4"));
        taxReceiptStatement.ShouldNotBeNull("Up() must contain an UPDATE for Id=4 (TaxReceiptForAdvance)");
        taxReceiptStatement.ShouldContain("DPP-");  // new prefix
        taxReceiptStatement.ShouldContain("'ZF'");  // old prefix in WHERE guard (idempotency)
    }

    // ─── Down() SQL tests ──────────────────────────────────────────────────────

    [Fact]
    public void Down_ContainsExactlyTwoStatements()
    {
        var statements = GetMigrationSql(b => CallDown(new FixProformaDppPrefixes(), b));

        statements.Count.ShouldBe(2,
            "Down() must issue exactly 2 SQL statements — one per prefix to restore");
    }

    [Fact]
    public void Down_ProformaStatement_RestoresPFAndGuardsOnPFDash()
    {
        // Down() reverses Up() — must restore 'PF' and guard on current value 'PF-'.
        var statements = GetMigrationSql(b => CallDown(new FixProformaDppPrefixes(), b));

        var proformaStatement = statements.FirstOrDefault(s => s.Contains("= 3"));
        proformaStatement.ShouldNotBeNull("Down() must contain an UPDATE for Id=3 (Proforma)");
        proformaStatement.ShouldContain("'PF'");   // restore to old value
        proformaStatement.ShouldContain("'PF-'");  // guard on current value (idempotency)
    }

    [Fact]
    public void Down_TaxReceiptStatement_RestoresZFAndGuardsOnDppDash()
    {
        var statements = GetMigrationSql(b => CallDown(new FixProformaDppPrefixes(), b));

        var taxReceiptStatement = statements.FirstOrDefault(s => s.Contains("= 4"));
        taxReceiptStatement.ShouldNotBeNull("Down() must contain an UPDATE for Id=4 (TaxReceiptForAdvance)");
        taxReceiptStatement.ShouldContain("'ZF'");    // restore to old value
        taxReceiptStatement.ShouldContain("'DPP-'");  // guard on current value (idempotency)
    }
}
