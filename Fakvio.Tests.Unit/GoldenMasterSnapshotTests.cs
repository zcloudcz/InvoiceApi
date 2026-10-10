using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Fakvio.Infrastructure.Service.AccountingExport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Xunit;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Golden-master snapshot of everything that reads or writes "received invoices" (and of the issued side
/// that must stay untouched). It is NOT a normal test: it is the proof that the "one document table"
/// refactor changed no observable behaviour. Workflow:
///   1. run it with the OLD code against a copy of production  -> snapshot A,
///   2. migrate the same kind of copy, run it with the NEW code -> snapshot B,
///   3. diff A and B (they must be identical).
/// The test itself must therefore compile and behave the same on the old and the new code: it only talks to
/// public service interfaces and to raw SQL (never to the ReceivedInvoice entity).
///
/// Gating (so a plain <c>dotnet test</c> skips it): FAKVIO_GM=1, FAKVIO_GM_CONN = PostgreSQL connection string,
/// FAKVIO_GM_OUT = output directory. One sub-directory per tenant schema, one JSON file per snapshot.
/// Received document ids are removed from the snapshots (they change on purpose), issued ids are kept.
/// The snapshot never leaves the machine: the output directory is meant to be a throw-away local folder.
/// </summary>
public sealed class GoldenMasterSnapshotTests
{
    private const string Enable = "FAKVIO_GM";

    [AttributeUsage(AttributeTargets.Method)]
    private sealed class GoldenMasterFactAttribute : FactAttribute
    {
        public override string? Skip
        {
            get => Environment.GetEnvironmentVariable(Enable) == "1"
                ? base.Skip
                : "Golden-master snapshot disabled. Set FAKVIO_GM=1, FAKVIO_GM_CONN and FAKVIO_GM_OUT.";
            set => base.Skip = value;
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Keys whose values are ids of RECEIVED documents (they change by design) - removed in the received snapshots.
    private static readonly string[] ReceivedIdKeys = ["id", "receivedInvoiceId", "invoiceId", "matchedInvoiceId"];

    [GoldenMasterFact]
    public async Task WriteSnapshots()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var conn = Environment.GetEnvironmentVariable("FAKVIO_GM_CONN")
                   ?? throw new InvalidOperationException("FAKVIO_GM_CONN is not set.");
        var outDir = Environment.GetEnvironmentVariable("FAKVIO_GM_OUT")
                     ?? throw new InvalidOperationException("FAKVIO_GM_OUT is not set.");

        await using var root = new NpgsqlConnection(conn);
        await root.OpenAsync();

        // Real tenant schemas only (tenant_template is a stale leftover, see the task brief).
        var schemas = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT nspname FROM pg_namespace WHERE nspname ~ '^tenant_[0-9]+$' ORDER BY nspname", root))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) schemas.Add(r.GetString(0));

        foreach (var schema in schemas)
            await SnapshotSchemaAsync(conn, schema, Path.Combine(outDir, schema));
    }

    private static async Task SnapshotSchemaAsync(string conn, string schema, string dir)
    {
        Directory.CreateDirectory(dir);
        var companyId = long.Parse(schema["tenant_".Length..]);
        var scoped = new NpgsqlConnectionStringBuilder(conn) { SearchPath = schema }.ConnectionString;

        await using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(scoped, b =>
            {
                b.MigrationsAssembly("Fakvio.Infrastructure");
                b.MigrationsHistoryTable("__EFMigrationsHistory", schema);
            })
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options) { Schema = schema };
        await using var master = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(conn).ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
        await using var raw = new NpgsqlConnection(scoped);
        await raw.OpenAsync();

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(companyId);
        // Deterministic stand-in for the CNB rate lookup: the snapshot is about data flow, not about rates.
        var currency = Substitute.For<ICurrencyService>();
        currency.ConvertToCzkAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(ci.ArgAt<decimal>(0) * (ci.ArgAt<decimal?>(4) ?? 1m)));

        var received = new ReceivedInvoiceService(db, NullLogger<ReceivedInvoiceService>.Instance);
        var vat = new VatReportService(db, master, tenantResolver, new EpoSchemaProvider(), currency,
            NullLogger<VatReportService>.Instance);
        var dashboard = new DashboardService(db, NullLogger<DashboardService>.Instance);
        var exporters = new IAccountingExporter[]
            { new PohodaAccountingExporter(), new MoneyS3AccountingExporter(), new AbraFlexiAccountingExporter() };
        var accounting = new AccountingExportService(db, exporters, NullLogger<AccountingExportService>.Instance);
        var isdoc = new IsdocExportService(db, NullLogger<IsdocExportService>.Instance);
        var bankQuery = new BankTransactionQueryService(db, NullLogger<BankTransactionQueryService>.Instance);
        var invoiceService = new InvoiceService(db, master, tenantResolver, Substitute.For<INumberSequenceService>(),
            Substitute.For<ITenantReadinessService>(), NullLogger<InvoiceService>.Instance);
        var matching = new PaymentMatchingService(db, Substitute.For<INotificationService>(), invoiceService,
            NullLogger<PaymentMatchingService>.Instance);
        var reminders = new ReminderService(db, Substitute.For<IInterestCalculator>(), Substitute.For<IEmailService>(),
            Substitute.For<IPdfExportService>(), Substitute.For<IDocumentFileNameService>(),
            Substitute.For<IContentTemplateService>(), NullLogger<ReminderService>.Instance);

        // ── received invoices (list + overdue), received ids stripped, stable business order ──
        var all = await received.GetAllAsync();
        var overdue = await received.GetPagedAsync(new ReceivedInvoiceFilterDto { IsOverdue = true, PageSize = 100 });
        Write(dir, "received-list", all.OrderBy(x => x.DocumentNumber).ThenBy(x => x.IssueDate)
            .ThenBy(x => x.SupplierName).ThenBy(x => x.TotalWithVat).ToList(), strip: true);
        Write(dir, "received-overdue", overdue.Items.OrderBy(x => x.DocumentNumber).ThenBy(x => x.IssueDate)
            .ThenBy(x => x.SupplierName).ThenBy(x => x.TotalWithVat).ToList(), strip: true);
        foreach (var status in Enum.GetValues<EReceivedInvoiceStatus>())
        {
            var byStatus = await received.GetAllAsync(status);
            Write(dir, $"received-status-{status}", byStatus.Select(x => new { x.DocumentNumber, x.IssueDate, x.SupplierName, x.TotalWithVat })
                .OrderBy(x => x.DocumentNumber).ThenBy(x => x.IssueDate).ThenBy(x => x.SupplierName).ToList());
        }

        // ── per received document: payments, auto-match proposal, ISDOC (key = business key, never the id) ──
        var perDoc = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var d in all.OrderBy(x => x.DocumentNumber).ThenBy(x => x.IssueDate).ThenBy(x => x.SupplierName).ThenBy(x => x.TotalWithVat))
        {
            var key = $"{d.DocumentNumber}|{d.IssueDate:yyyy-MM-dd}|{d.SupplierName}|{d.TotalWithVat}";
            for (var n = 2; perDoc.ContainsKey(key); n++) key = $"{d.DocumentNumber}|{d.IssueDate:yyyy-MM-dd}|{d.SupplierName}|{d.TotalWithVat}#{n}";
            perDoc[key] = new
            {
                Payments = Strip(await matching.GetPaymentsForReceivedInvoiceAsync(d.Id), "id", "matchedInvoiceId"),
                AutoMatch = Strip(await Try(() => matching.FindAutoMatchForReceivedInvoiceAsync(d.Id)), ReceivedIdKeys),
                Isdoc = Normalize(await Try(async () => Encoding.UTF8.GetString(await isdoc.ExportReceivedInvoiceAsync(d.Id))))
            };
        }
        Write(dir, "received-per-document", perDoc);

        // ── raw facts per received document incl. deleted ones (attachments, notifications, ...) ──
        Write(dir, "received-raw-facts", await RawFactsAsync(raw));

        // ── VAT report + control statement + VAT return for every month/quarter that can have data ──
        var vatOut = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        for (var year = 2024; year <= 2026; year++)
        {
            for (var month = 1; month <= 12; month++)
            {
                var from = new DateTime(year, month, 1);
                var report = await Try(() => vat.GetReportAsync(from, from.AddMonths(1).AddDays(-1)));
                vatOut[$"report-{year}-{month:00}"] = report;
                vatOut[$"ks-{year}-{month:00}"] = Normalize(await Try(async () =>
                    Encoding.UTF8.GetString(await vat.ExportEpoControlStatementAsync(year, month, EVatPeriodType.Monthly))));
                vatOut[$"dph-{year}-{month:00}"] = Normalize(await Try(async () =>
                    Encoding.UTF8.GetString(await vat.ExportEpoVatReturnAsync(year, month, EVatPeriodType.Monthly))));
            }
            for (var q = 1; q <= 4; q++)
            {
                vatOut[$"ks-{year}-Q{q}"] = Normalize(await Try(async () =>
                    Encoding.UTF8.GetString(await vat.ExportEpoControlStatementAsync(year, q, EVatPeriodType.Quarterly))));
                vatOut[$"dph-{year}-Q{q}"] = Normalize(await Try(async () =>
                    Encoding.UTF8.GetString(await vat.ExportEpoVatReturnAsync(year, q, EVatPeriodType.Quarterly))));
            }
        }
        Write(dir, "vat", vatOut);

        Write(dir, "dashboard", await Try(() => dashboard.GetDashboardAsync(companyId)));

        // ── accounting export, every system, per year, issued only / received only / both ──
        var acc = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var system in Enum.GetValues<EAccountingSystem>())
            for (var year = 2024; year <= 2026; year++)
                foreach (var (issued, recv) in new[] { (true, false), (false, true), (true, true) })
                    acc[$"{system}-{year}-i{issued}-r{recv}"] = await Try(async () =>
                    {
                        var res = await accounting.ExportAsync(system, new DateTime(year, 1, 1), new DateTime(year, 12, 31), issued, recv);
                        return new { res.ExportedCount, res.FileName, res.SkippedDocuments, Content = Normalize(Encoding.UTF8.GetString(res.Content)) };
                    });
        Write(dir, "accounting", acc);

        // ── bank transactions with their matches (ids of transactions are stable; no invoice ids inside) ──
        var tx = new List<BankTransactionDto>();
        for (var page = 1; ; page++)
        {
            var res = await bankQuery.ListAsync(new BankTransactionFilterDto(), new Fakvio.Contracts.Common.Pagination.PaginationParams { Page = page, PageSize = 100 });
            tx.AddRange(res.Items);
            if (!res.HasNextPage) break;
        }
        Write(dir, "bank-transactions", tx.OrderBy(t => t.Id).ToList());
        Write(dir, "bank-transaction-proposals", await RawProposalsAsync(matching, tx));

        // ── issued side: must be totally unaffected (ids kept) ──
        Write(dir, "issued-invoices", await Try(() => invoiceService.GetAllInvoicesAsync()));

        // ── dunning: dashboard, list, then one real dunning cycle with mail/pdf stubbed (runs last: it writes) ──
        Write(dir, "reminders-dashboard", await Try(() => reminders.GetDashboardDataAsync()));
        Write(dir, "reminders-list", await Try(() => reminders.GetRemindersPagedAsync(new ReminderFilterDto { PageSize = 100 })));
        var created = await Try(() => reminders.ProcessOverdueInvoicesAsync());
        Write(dir, "reminders-process", new { created, After = await Try(() => reminders.GetRemindersPagedAsync(new ReminderFilterDto { PageSize = 100 })) });
    }

    // Proposals for each transaction (the matcher reads received invoices to propose them).
    private static async Task<object?> RawProposalsAsync(IPaymentMatchingService matching, List<BankTransactionDto> tx)
    {
        var list = new List<object?>();
        foreach (var t in tx.OrderBy(t => t.Id))
            list.Add(new { t.Id, Proposal = Strip(await Try(() => matching.FindAutoMatchForTransactionAsync(t.Id)), "invoiceId", "receivedInvoiceId") });
        return list;
    }

    /// <summary>
    /// Counts/filenames per received document, read with raw SQL so the same code works before the migration
    /// (table "ReceivedInvoice") and after it (rows of "Invoice" with DocumentType = 5).
    /// </summary>
    private static async Task<object?> RawFactsAsync(NpgsqlConnection c)
    {
        var legacy = await Scalar<bool>(c, "SELECT to_regclass('\"ReceivedInvoice\"') IS NOT NULL");
        var docs = legacy
            ? "SELECT r.\"Id\", r.\"DocumentNumber\", r.\"IssueDate\", s.\"CompanyName\", r.\"TotalWithVat\", r.\"Status\" FROM \"ReceivedInvoice\" r JOIN \"Client\" s ON s.\"Id\" = r.\"SupplierId\""
            : "SELECT r.\"Id\", r.\"DocumentNumber\", r.\"IssueDate\", s.\"CompanyName\", r.\"TotalWithVat\", r.\"Status\" FROM \"Invoice\" r JOIN \"Client\" s ON s.\"Id\" = r.\"ClientId\" WHERE r.\"DocumentType\" = 5";
        var itemSql = legacy
            ? "SELECT COUNT(*), COALESCE(SUM(\"TotalWithVat\"),0) FROM \"ReceivedInvoiceItem\" WHERE \"ReceivedInvoiceId\" = @id"
            : "SELECT COUNT(*), COALESCE(SUM(\"TotalWithVat\"),0) FROM \"InvoiceItem\" WHERE \"InvoiceId\" = @id";
        var inboundCol = legacy ? "ReceivedInvoiceId" : "InvoiceId";
        var pmCol = legacy ? "ReceivedInvoiceId" : "InvoiceId";

        var rows = new List<(long Id, string Key, int Status)>();
        await using (var cmd = new NpgsqlCommand(docs, c))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                rows.Add((r.GetInt64(0), $"{(r.IsDBNull(1) ? "" : r.GetString(1))}|{(r.IsDBNull(2) ? "" : r.GetDateTime(2).ToString("yyyy-MM-dd"))}|{r.GetString(3)}|{r.GetDecimal(4)}", r.GetInt32(5)));

        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (id, key0, status) in rows.OrderBy(x => x.Key).ThenBy(x => x.Id))
        {
            var key = key0;
            for (var n = 2; result.ContainsKey(key); n++) key = key0 + "#" + n;
            var (itemCount, itemSum) = await Two(c, itemSql, id);
            result[key] = new
            {
                Status = status,
                itemCount,
                itemSum,
                Attachments = await Strings(c, "SELECT \"OriginalFileName\" FROM \"FileAttachment\" WHERE \"EntityName\" = 'ReceivedInvoice' AND \"RecordId\" = @id ORDER BY 1", id),
                Notifications = await Scalar<long>(c, "SELECT COUNT(*) FROM \"Notification\" WHERE \"RelatedEntityType\" = 'ReceivedInvoice' AND \"RelatedEntityId\" = " + id),
                Alerts = await Scalar<long>(c, "SELECT COUNT(*) FROM \"Alert\" WHERE \"RelatedEntityType\" = 'ReceivedInvoice' AND \"RelatedEntityId\" = " + id),
                InboundEmails = await Scalar<long>(c, $"SELECT COUNT(*) FROM \"InboundInvoiceEmail\" WHERE \"{inboundCol}\" = " + id),
                MatchedAmounts = await Strings(c, $"SELECT \"MatchedAmount\"::text FROM \"PaymentMatch\" WHERE \"{pmCol}\" = @id ORDER BY 1", id)
            };
        }
        return result;
    }

    // ─── helpers ────────────────────────────────────────────────────────────
    private static async Task<T> Scalar<T>(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<(long, decimal)> Two(NpgsqlConnection c, string sql, long id)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt64(0), r.GetDecimal(1));
    }

    private static async Task<List<string>> Strings(NpgsqlConnection c, string sql, long id)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", id);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Runs a service call; an exception becomes part of the snapshot (it must be the same before and after).</summary>
    private static async Task<object?> Try<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (Exception ex) { return "EXCEPTION " + ex.GetType().Name + ": " + ex.Message; }
    }

    // Generated GUIDs / timestamps differ between two runs of the very same code; mask them.
    private static string? Normalize(object? o) => o is string s
        ? Regex.Replace(Regex.Replace(Regex.Replace(s, @"Fakvio\d{15,}", "Fakvio<ticks>"),"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<guid>"),
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?", "<ts>")
        : null;

    private static JsonNode? Strip(object? value, params string[] keys)
    {
        var node = JsonSerializer.SerializeToNode(value, Json);
        Remove(node, keys);
        return node;
    }

    private static void Remove(JsonNode? node, string[] keys)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(p => p.Key).ToList())
                    if (keys.Contains(k, StringComparer.OrdinalIgnoreCase)) o.Remove(k);
                foreach (var p in o) Remove(p.Value, keys);
                break;
            case JsonArray a:
                foreach (var i in a) Remove(i, keys);
                break;
        }
    }

    private static void Write(string dir, string name, object? value, bool strip = false)
    {
        var node = JsonSerializer.SerializeToNode(value, Json);
        if (strip) Remove(node, ReceivedIdKeys);
        // Strings inside nodes (exception messages) may contain generated values too.
        File.WriteAllText(Path.Combine(dir, name + ".json"), node?.ToJsonString(Json) ?? "null", new UTF8Encoding(false));
    }
}
