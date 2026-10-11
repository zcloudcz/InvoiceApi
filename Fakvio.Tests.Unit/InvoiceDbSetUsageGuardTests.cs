using System.Text.RegularExpressions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Guard for the "one table for all documents" model: the <c>Invoice</c> DbSet holds issued AND received
/// (supplier) documents, so every query on it must pick a side with <c>.IssuedOnly()</c> or <c>.ReceivedOnly()</c>.
/// A forgotten helper is severe (payment reminders e-mailed to suppliers, supplier invoices counted as revenue
/// or output VAT, number-sequence checks tripping over supplier numbers).
///
/// A plain source scan on purpose: simple, fast, and it fails with the exact file and line to fix.
/// It recognises the DbSet by the conventional receiver names (<c>_context</c>, <c>_db</c>, <c>_tenantContext</c>,
/// <c>tenantCtx</c>, ...) and by <c>Set&lt;Invoice&gt;()</c>; navigation properties (<c>match.Invoice</c>) are
/// out of its reach and have to be reviewed by hand (see DEVGUIDE "One table for all documents").
/// </summary>
public class InvoiceDbSetUsageGuardTests
{
    // Production projects that talk to TenantDbContext. (Fakvio.MigrationTool is a one-off SQLite importer
    // whose integrity counts compare against the old database, it is intentionally not scanned.)
    private static readonly string[] ScannedProjects =
        ["Fakvio.Infrastructure", "Fakvio.API", "Fakvio.Application", "Fakvio.McpServer"];

    // `<receiver>.Invoice` where the receiver looks like a DbContext, or Set<Invoice>(), NOT followed by a helper.
    private static readonly Regex Unfiltered = new(
        @"(?:\b_?(?:context|db|dbContext|tenantContext|tenantCtx|tenantDb|ctx)\.Invoice\b|Set<(?:Domain\.Entities\.)?Invoice>\(\))" +
        @"(?!\s*\.\s*(?:IssuedOnly|ReceivedOnly)\s*\()",
        RegexOptions.Compiled);

    /// <summary>
    /// The explicit allowlist: (file name, text that must appear on the line). Only inserts of NEW rows, which
    /// are not queries - nothing is read, so there is no side to pick.
    /// </summary>
    private static readonly (string File, string LineContains)[] Allowlist =
    [
        ("InvoiceService.cs", "_context.Invoice.Add(invoice);"),
        ("ReceivedInvoiceService.cs", "_context.Invoice.Add(entity);"),
    ];

    [Fact]
    public void EveryInvoiceDbSetQuery_PicksAnIssuedOrReceivedSide()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var project in ScannedProjects)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/") || relative.Contains("/Migrations/")
                    || relative.Contains("/Generated/"))
                    continue;

                var text = File.ReadAllText(file);
                foreach (Match m in Unfiltered.Matches(text))
                {
                    var line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    var lineText = text.Split('\n')[line - 1].Trim();
                    if (Allowlist.Any(a => relative.EndsWith(a.File, StringComparison.Ordinal) && lineText.Contains(a.LineContains)))
                        continue;
                    offenders.Add($"{relative}:{line}: {lineText}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "These queries read the Invoice table without .IssuedOnly() / .ReceivedOnly() - they would mix issued " +
            "documents with received supplier invoices:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("var q = _context.Invoice.AsNoTracking();", true)]
    [InlineData("var q = _db.Invoice\n    .Include(i => i.Client);", true)]
    [InlineData("var q = _tenantContext.Set<Invoice>().Where(x => true);", true)]
    [InlineData("var q = _context.Invoice.IssuedOnly().AsNoTracking();", false)]
    [InlineData("var q = _context.Invoice\n    .ReceivedOnly()\n    .AsNoTracking();", false)]
    [InlineData("var items = _context.InvoiceItem.Where(x => true);", false)]
    [InlineData("var n = match.Invoice.DocumentNumber;", false)]
    public void GuardRegex_RecognisesUnfilteredQueries(string code, bool shouldBeFlagged)
    {
        Unfiltered.IsMatch(code).ShouldBe(shouldBeFlagged);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Fakvio.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Fakvio.sln not found above the test output directory.");
    }
}
