using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// The UnifyDocumentsInInvoiceTable tenant migration on a REAL PostgreSQL, in a disposable schema, starting from
/// the shape of the previous migration (received invoices in their own tables). The migration is hand-written SQL,
/// so only a real database can say whether the data really arrives where it should:
///  * every status maps as agreed (Received->Draft, Approved->Completed / PartiallyPaid, Paid->Paid,
///    Rejected->Deleted - the number 4 is never copied);
///  * PaidAmount, ids, payment matches, attachments, notifications and inbound e-mails are remapped;
///  * supplier numbers may repeat (also equal to one of OUR numbers) while issued numbers stay unique;
///  * an empty tenant and a brand-new tenant migrate; a failing self-check rolls the WHOLE migration back;
///  * messy but legitimate data does NOT stop the migration (a failed migration = HTTP 503 for the tenant):
///    an inbound e-mail linked to both sides, pointers to a received invoice that never existed;
///  * a new id never equals an OLD received id (a stale id must not resolve to a different document).
/// Skipped when FAKVIO_TEST_POSTGRES is not set (same convention as the other migration tests).
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class UnifyDocumentsMigrationTests
{
    private const string Previous = "20261005184918_LocalizeDefaultEmailTemplates";
    private const string UnifyMigration = "20261010200745_UnifyDocumentsInInvoiceTable";

    [SkippableFact]
    public async Task Up_MovesReceivedInvoicesIntoInvoiceTable_WithMappedStatusesAmountsAndPointers()
    {
        await RunAsync(async env =>
        {
            // Issued ids 1 and 2 collide with the OLD received ids 1..6 - exactly the situation of production.
            await env.ExecAsync("""
                INSERT INTO "Currency" ("Id","Code","Name","Symbol","DecimalPlaces","IsActive","SortOrder","CreatedAt") VALUES (1,'CZK','Koruna','Kc',2,TRUE,1,now()) ON CONFLICT DO NOTHING;
                INSERT INTO "Client" ("Id","RegistrationNumber","CompanyName","IsVatPayer","IsIssuer","IsActive","IsMainActivity","CreatedAt") VALUES
                    (10,'11111111','Us',TRUE,TRUE,TRUE,TRUE,now()),
                    (11,'22222222','Customer',FALSE,FALSE,TRUE,FALSE,now()),
                    (12,'33333333','Supplier A',FALSE,FALSE,TRUE,FALSE,now()),
                    (13,'44444444','Supplier B',FALSE,FALSE,TRUE,FALSE,now());
                INSERT INTO "Invoice" ("Id","DocumentType","Status","DocumentNumber","IssuerId","ClientId","CurrencyId","TotalBeforeVat","TotalVat","TotalWithVat","IsExported","IsSentByEmail","InvoiceType","CreatedAt")
                    VALUES (1,1,2,'2026001',10,11,1,100,21,121,FALSE,FALSE,'Invoice',now()),
                           (2,1,3,'2026002',10,11,1,200,42,242,FALSE,FALSE,'Invoice',now());
                -- Received: 1 Received, 2 Approved + partial payment, 3 Approved, 4 Paid, 5 Rejected, 6 Deleted.
                -- Documents 1+2 share a supplier number across suppliers, document 3 repeats one of OUR numbers.
                INSERT INTO "ReceivedInvoice" ("Id","Status","DocumentNumber","SupplierId","CurrencyId","TotalBeforeVat","TotalVat","TotalWithVat","VariableSymbol","ReceivedDate","IssueDate","CreatedAt","CreatedByUserId") VALUES
                    (1,1,'FV-1',12,1,1000,210,1210,'1001',now(),now(),now(),7),
                    (2,2,'FV-1',13,1,1000,210,1210,'1002',now(),now(),now(),7),
                    (3,2,'2026001',12,1,500,105,605,'1003',now(),now(),now(),7),
                    (4,3,'FV-4',12,1,300,63,363,'1004',now(),now(),now(),7),
                    (5,4,'FV-5',12,1,50,10.5,60.5,'1005',now(),now(),now(),7),
                    (6,5,'FV-6',12,1,70,14.7,84.7,'1006',now(),now(),now(),7);
                INSERT INTO "ReceivedInvoiceItem" ("Id","ReceivedInvoiceId","OrderIndex","Description","Quantity","Unit","UnitPrice","VatRatePercentage","TotalBeforeVat","VatAmount","TotalWithVat","CreatedAt") VALUES
                    (1,1,1,'A',1,'ks',600,21,600,126,726,now()), (2,1,2,'B',1,'ks',400,21,400,84,484,now()),
                    (3,2,1,'C',1,'ks',1000,21,1000,210,1210,now()), (4,3,1,'D',1,'ks',500,21,500,105,605,now()),
                    (5,4,1,'E',1,'ks',300,21,300,63,363,now()), (6,5,1,'F',1,'ks',50,21,50,10.5,60.5,now()),
                    (7,6,1,'G',1,'ks',70,21,70,14.7,84.7,now());
                INSERT INTO "BankAccount" ("Id","ClientId","AccountNumber","IsDefault","CreatedAt") VALUES (1,10,'123/0100',TRUE,now());
                INSERT INTO "BankTransaction" ("Id","BankAccountId","DeduplicationHash","TransactionDate","Amount","CurrencyCode","Direction","ImportSource","MatchStatus","CreatedAt") VALUES
                    (1,1,'h1',now(),400,'CZK',2,0,1,now()), (2,1,'h2',now(),363,'CZK',2,0,1,now()), (3,1,'h3',now(),121,'CZK',1,0,1,now());
                INSERT INTO "PaymentMatch" ("Id","BankTransactionId","InvoiceId","ReceivedInvoiceId","MatchedAmount","MatchedBy","MatchedAt","CreatedAt") VALUES
                    (1,1,NULL,2,400,1,now(),now()),   -- partial payment of received 2
                    (2,2,NULL,4,363,1,now(),now()),   -- full payment of received 4
                    (3,3,1,NULL,121,1,now(),now());   -- an ISSUED invoice payment must not move
                INSERT INTO "FileAttachment" ("Id","EntityName","RecordId","FileGuid","OriginalFileName","ContentType","FileSizeBytes","BlobPath","CreatedAt") VALUES
                    (1,'ReceivedInvoice',2,gen_random_uuid(),'a.pdf','application/pdf',1,'p/1',now()),
                    (2,'Invoice',2,gen_random_uuid(),'issued.pdf','application/pdf',1,'p/2',now()),
                    -- Orphans (ids 3 of each table below): the upload endpoint never validated the target, so a pointer
                    -- to a received invoice that does not exist is legitimate old data and must not stop the migration.
                    (3,'ReceivedInvoice',999,gen_random_uuid(),'orphan.pdf','application/pdf',1,'p/3',now());
                INSERT INTO "Notification" ("Id","Type","Title","Message","RelatedEntityId","RelatedEntityType","CreatedAt") VALUES
                    (1,1,'t','m',2,'ReceivedInvoice',now()), (2,1,'t','m',2,'Invoice',now()), (3,1,'t','m',999,'ReceivedInvoice',now());
                INSERT INTO "Alert" ("Id","Type","RelatedEntityId","RelatedEntityType","Message","CreatedAt") VALUES
                    (1,1,3,'ReceivedInvoice','m',now()), (3,1,999,'ReceivedInvoice','m',now());
                INSERT INTO "InvoiceMailbox" ("Id","InboundAlias","IsActive","ActiveFrom","EmailsReceivedCount","CreatedAt") VALUES (1,'fak-x',TRUE,now(),0,now());
                INSERT INTO "InboundInvoiceEmail" ("Id","InvoiceMailboxId","MessageId","ServerReceivedAt","FromAddress","ToAddress","BodyTruncated","DeduplicationHash","Status","ProcessAttempts","AttachmentCount","HasPdf","HasIsdoc","CreatedAt","ReceivedInvoiceId","InvoiceId") VALUES
                    (1,1,'m1',now(),'a@b.cz','fak-x@z.cz',FALSE,'d1',1,1,1,TRUE,FALSE,now(),4,NULL),
                    (2,1,'m2',now(),'a@b.cz','fak-x@z.cz',FALSE,'d2',1,1,1,TRUE,FALSE,now(),NULL,2),
                    -- One e-mail with an issued AND a received attachment: the old processor filled both links.
                    (3,1,'m3',now(),'a@b.cz','fak-x@z.cz',FALSE,'d3',1,1,2,TRUE,FALSE,now(),1,1);
                """);

            await env.MigrateToLatestAsync();

            // ── the old tables are parked, with their data, and the id map is persistent ──
            (await env.CountAsync("SELECT COUNT(*) FROM \"ReceivedInvoice_legacy\"")).ShouldBe(6);
            (await env.CountAsync("SELECT COUNT(*) FROM \"ReceivedInvoiceItem_legacy\"")).ShouldBe(7);
            (await env.CountAsync("SELECT COUNT(*) FROM \"ReceivedInvoiceIdMap_legacy\"")).ShouldBe(6);
            (await env.FlagAsync("SELECT to_regclass('\"ReceivedInvoice\"') IS NULL")).ShouldBe(true);
            // ... and their foreign keys are gone, so they cannot block deletes elsewhere.
            (await env.CountAsync("SELECT COUNT(*) FROM pg_constraint WHERE contype = 'f' AND conrelid IN ('\"ReceivedInvoice_legacy\"'::regclass, '\"ReceivedInvoiceItem_legacy\"'::regclass)")).ShouldBe(0);

            // ── ids: deterministic block, in order of the old ids, never colliding with issued ids 1 and 2
            //    NOR with the old received ids 1..6 (a stale old id must not resolve to another document) ──
            var map = await env.RowsAsync("SELECT \"OldId\", \"NewId\" FROM \"ReceivedInvoiceIdMap_legacy\" ORDER BY \"OldId\"");
            map.Select(r => (long)r[0]!).ShouldBe([1L, 2, 3, 4, 5, 6]);
            var newIds = map.Select(r => (long)r[1]!).ToArray();
            newIds.Distinct().Count().ShouldBe(6);
            newIds.ShouldAllBe(id => id > 6);
            newIds.Zip(newIds.Skip(1)).ShouldAllBe(p => p.Second == p.First + 1);
            // Old received ids now answer "not found" on the received side: they are an issued document or nothing.
            (await env.CountAsync("SELECT COUNT(*) FROM \"Invoice\" WHERE \"Id\" BETWEEN 1 AND 6 AND \"DocumentType\" = 5")).ShouldBe(0);

            // ── statuses / PaidAmount (4 = Rejected is NEVER copied: it would mean Creditnoted) ──
            var docs = (await env.RowsAsync(
                "SELECT m.\"OldId\", i.\"Status\", i.\"PaidAmount\", i.\"DocumentType\", i.\"ClientId\", i.\"IssuerId\", i.\"InvoiceType\", i.\"CreatedByUserId\" " +
                "FROM \"Invoice\" i JOIN \"ReceivedInvoiceIdMap_legacy\" m ON m.\"NewId\" = i.\"Id\" ORDER BY m.\"OldId\""))
                .ToDictionary(r => (long)r[0]!);
            docs[1][1].ShouldBe(1);                 // Received  -> Draft
            docs[2][1].ShouldBe(6);                 // Approved with a payment -> PartiallyPaid
            docs[2][2].ShouldBe(400m);
            docs[3][1].ShouldBe(2);                 // Approved  -> Completed
            docs[3][2].ShouldBe(0m);
            docs[4][1].ShouldBe(3);                 // Paid      -> Paid, fully paid
            docs[4][2].ShouldBe(363m);
            docs[5][1].ShouldBe(5);                 // Rejected  -> Deleted (not 4!)
            docs[6][1].ShouldBe(5);                 // Deleted   -> Deleted
            docs.Values.ShouldAllBe(r => (int)r[3]! == 5 && (int)r[1]! != 4);
            docs.Values.ShouldAllBe(r => (long)r[5]! == 10 && (string)r[6]! == "Invoice" && (long)r[7]! == 7);
            docs[1][4].ShouldBe(12L);               // supplier -> ClientId
            docs[2][4].ShouldBe(13L);

            // ── items arrive as InvoiceItem rows (no text rows) ──
            (await env.CountAsync("SELECT COUNT(*) FROM \"InvoiceItem\" it JOIN \"Invoice\" i ON i.\"Id\" = it.\"InvoiceId\" WHERE i.\"DocumentType\" = 5")).ShouldBe(7);
            (await env.CountAsync("SELECT COUNT(*) FROM \"InvoiceItem\" it JOIN \"Invoice\" i ON i.\"Id\" = it.\"InvoiceId\" WHERE i.\"DocumentType\" = 5 AND it.\"IsTextRow\"")).ShouldBe(0);

            // ── pointers: payment matches, attachments, notifications, alerts, inbound e-mails ──
            var pm = (await env.RowsAsync("SELECT \"Id\", \"InvoiceId\" FROM \"PaymentMatch\" ORDER BY \"Id\"")).ToDictionary(r => (long)r[0]!, r => (long)r[1]!);
            pm[1].ShouldBe(newIds[1]);
            pm[2].ShouldBe(newIds[3]);
            pm[3].ShouldBe(1L);                     // the issued payment is untouched
            (await env.ScalarAsync("SELECT \"RecordId\" FROM \"FileAttachment\" WHERE \"Id\" = 1")).ShouldBe(newIds[1]);
            (await env.ScalarAsync("SELECT \"EntityName\" FROM \"FileAttachment\" WHERE \"Id\" = 1")).ShouldBe("ReceivedInvoice"); // string kept
            (await env.ScalarAsync("SELECT \"RecordId\" FROM \"FileAttachment\" WHERE \"Id\" = 2")).ShouldBe(2L);                 // other entity untouched
            (await env.ScalarAsync("SELECT \"RelatedEntityId\" FROM \"Notification\" WHERE \"Id\" = 1")).ShouldBe(newIds[1]);
            (await env.ScalarAsync("SELECT \"RelatedEntityId\" FROM \"Notification\" WHERE \"Id\" = 2")).ShouldBe(2L);
            (await env.ScalarAsync("SELECT \"RelatedEntityId\" FROM \"Alert\" WHERE \"Id\" = 1")).ShouldBe(newIds[2]);
            (await env.ScalarAsync("SELECT \"InvoiceId\" FROM \"InboundInvoiceEmail\" WHERE \"Id\" = 1")).ShouldBe(newIds[3]);
            (await env.ScalarAsync("SELECT \"InvoiceId\" FROM \"InboundInvoiceEmail\" WHERE \"Id\" = 2")).ShouldBe(2L);

            // ── e-mail linked to both sides: the issued link is kept, the received one is parked for audit ──
            (await env.ScalarAsync("SELECT \"InvoiceId\" FROM \"InboundInvoiceEmail\" WHERE \"Id\" = 3")).ShouldBe(1L);
            var parked = await env.RowsAsync("SELECT \"InboundInvoiceEmailId\", \"OldReceivedInvoiceId\", \"NewInvoiceId\" FROM \"InboundInvoiceEmailReceivedLink_legacy\"");
            parked.Count.ShouldBe(1);
            parked[0].Select(v => (long)v!).ShouldBe([3L, 1L, newIds[0]]);

            // ── orphan pointers are left exactly as they were ──
            (await env.ScalarAsync("SELECT \"RecordId\" FROM \"FileAttachment\" WHERE \"Id\" = 3")).ShouldBe(999L);
            (await env.ScalarAsync("SELECT \"RelatedEntityId\" FROM \"Notification\" WHERE \"Id\" = 3")).ShouldBe(999L);
            (await env.ScalarAsync("SELECT \"RelatedEntityId\" FROM \"Alert\" WHERE \"Id\" = 3")).ShouldBe(999L);

            // ── the old pointer columns and the check constraint are gone; InvoiceId is required ──
            (await env.CountAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND column_name = 'ReceivedInvoiceId' AND table_name IN ('PaymentMatch','InboundInvoiceEmail')")).ShouldBe(0);
            (await env.CountAsync("SELECT COUNT(*) FROM pg_constraint WHERE conname = 'CK_PaymentMatch_Target'")).ShouldBe(0);
            (await env.FlagAsync("SELECT is_nullable = 'NO' FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'PaymentMatch' AND column_name = 'InvoiceId'")).ShouldBe(true);

            // ── duplicate numbers: supplier numbers repeat, ours stay unique ──
            (await env.CountAsync("SELECT COUNT(*) FROM \"Invoice\" WHERE \"DocumentType\" = 5 AND \"DocumentNumber\" IN ('FV-1','2026001')")).ShouldBe(3);
            var duplicateIssued = await Should.ThrowAsync<PostgresException>(() => env.ExecAsync(
                "INSERT INTO \"Invoice\" (\"DocumentType\",\"Status\",\"DocumentNumber\",\"IssuerId\",\"CurrencyId\",\"TotalBeforeVat\",\"TotalVat\",\"TotalWithVat\",\"IsExported\",\"IsSentByEmail\",\"InvoiceType\",\"CreatedAt\") " +
                "VALUES (1,2,'2026001',10,1,1,0,1,FALSE,FALSE,'Invoice',now())"));
            duplicateIssued.SqlState.ShouldBe("23505");

            // ── the identity sequence ends above the block: the next document cannot reuse a moved id ──
            var nextId = (long)(await env.ScalarAsync(
                "INSERT INTO \"Invoice\" (\"DocumentType\",\"Status\",\"DocumentNumber\",\"IssuerId\",\"CurrencyId\",\"TotalBeforeVat\",\"TotalVat\",\"TotalWithVat\",\"IsExported\",\"IsSentByEmail\",\"InvoiceType\",\"CreatedAt\") " +
                "VALUES (1,2,'2026003',10,1,1,0,1,FALSE,FALSE,'Invoice',now()) RETURNING \"Id\""))!;
            nextId.ShouldBeGreaterThan(newIds.Max());
        });
    }

    [SkippableFact]
    public async Task Up_WithOnlyOrphanPointers_AndNoReceivedInvoice_Succeeds()
    {
        await RunAsync(async env =>
        {
            // No received invoice at all, yet an attachment claims to belong to one: the data move is skipped,
            // and the final check must not treat the old orphan as something the migration broke.
            await env.ExecAsync("""
                INSERT INTO "FileAttachment" ("Id","EntityName","RecordId","FileGuid","OriginalFileName","ContentType","FileSizeBytes","BlobPath","CreatedAt")
                    VALUES (1,'ReceivedInvoice',999,gen_random_uuid(),'a.pdf','application/pdf',1,'p/1',now());
                """);

            await env.MigrateToLatestAsync();

            (await env.ScalarAsync("SELECT \"RecordId\" FROM \"FileAttachment\" WHERE \"Id\" = 1")).ShouldBe(999L);
            (await env.CountAsync("SELECT COUNT(*) FROM \"Invoice\" WHERE \"DocumentType\" = 5")).ShouldBe(0);
        });
    }

    [SkippableFact]
    public async Task Up_OnEmptyTenant_Succeeds_AndLeavesNoReceivedDocuments()
    {
        await RunAsync(async env =>
        {
            await env.MigrateToLatestAsync();

            (await env.CountAsync("SELECT COUNT(*) FROM \"Invoice\" WHERE \"DocumentType\" = 5")).ShouldBe(0);
            (await env.CountAsync("SELECT COUNT(*) FROM \"ReceivedInvoiceIdMap_legacy\"")).ShouldBe(0);
            (await env.CountAsync("SELECT COUNT(*) FROM \"ReceivedInvoice_legacy\"")).ShouldBe(0);
        });
    }

    [SkippableFact]
    public async Task Up_OnBrandNewTenant_AllMigrationsFromScratch_Succeed()
    {
        await RunAsync(async env =>
        {
            // A new tenant never sees the previous shape: everything is applied in one go.
            await env.MigrateToLatestAsync();

            (await env.CountAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Invoice' AND column_name = 'ReceivedDate'")).ShouldBe(1);
            (await env.FlagAsync("SELECT to_regclass('\"ReceivedInvoice_legacy\"') IS NOT NULL")).ShouldBe(true);
        }, migrateToPrevious: false);
    }

    /// <summary>
    /// The self-check is exercised with a simulated bug in the data move: a trigger in the disposable schema
    /// changes every received row while it is being inserted. (An orphan pointer used to be the trigger here,
    /// but that is legitimate old data and no longer stops the migration.)
    /// The first case changes a total, which the count/sum comparison sees. The second one swaps nothing but a
    /// document number - sums stay equal, only the row-by-row comparison through the id map can catch it.
    /// </summary>
    [SkippableTheory]
    [InlineData("NEW.\"TotalWithVat\" := NEW.\"TotalWithVat\" + 1", "after")]
    [InlineData("NEW.\"DocumentNumber\" := NEW.\"DocumentNumber\" || '-x'", "differ from their source row")]
    public async Task Up_WhenSelfCheckFails_RollsTheWholeMigrationBack(string tamper, string expectedMessage)
    {
        await RunAsync(async env =>
        {
            await env.ExecAsync($$"""
                CREATE FUNCTION tamper_received() RETURNS trigger LANGUAGE plpgsql AS $t$
                BEGIN
                    IF NEW."DocumentType" = 5 THEN {{tamper}}; END IF;
                    RETURN NEW;
                END $t$;
                CREATE TRIGGER tamper_received BEFORE INSERT ON "Invoice" FOR EACH ROW EXECUTE FUNCTION tamper_received();
                """);
            await env.ExecAsync("""
                INSERT INTO "Currency" ("Id","Code","Name","Symbol","DecimalPlaces","IsActive","SortOrder","CreatedAt") VALUES (1,'CZK','Koruna','Kc',2,TRUE,1,now()) ON CONFLICT DO NOTHING;
                INSERT INTO "Client" ("Id","RegistrationNumber","CompanyName","IsVatPayer","IsIssuer","IsActive","IsMainActivity","CreatedAt") VALUES
                    (10,'11111111','Us',TRUE,TRUE,TRUE,TRUE,now()), (12,'33333333','Supplier',FALSE,FALSE,TRUE,FALSE,now());
                INSERT INTO "Invoice" ("Id","DocumentType","Status","DocumentNumber","IssuerId","CurrencyId","TotalBeforeVat","TotalVat","TotalWithVat","IsExported","IsSentByEmail","InvoiceType","CreatedAt")
                    VALUES (1,1,2,'2026001',10,1,100,21,121,FALSE,FALSE,'Invoice',now());
                INSERT INTO "ReceivedInvoice" ("Id","Status","DocumentNumber","SupplierId","CurrencyId","TotalBeforeVat","TotalVat","TotalWithVat","CreatedAt")
                    VALUES (1,2,'FV-1',12,1,100,21,121,now());
                INSERT INTO "BankAccount" ("Id","ClientId","AccountNumber","IsDefault","CreatedAt") VALUES (1,10,'123/0100',TRUE,now());
                INSERT INTO "BankTransaction" ("Id","BankAccountId","DeduplicationHash","TransactionDate","Amount","CurrencyCode","Direction","ImportSource","MatchStatus","CreatedAt")
                    VALUES (1,1,'h1',now(),121,'CZK',2,0,1,now());
                INSERT INTO "FileAttachment" ("Id","EntityName","RecordId","FileGuid","OriginalFileName","ContentType","FileSizeBytes","BlobPath","CreatedAt")
                    VALUES (1,'ReceivedInvoice',1,gen_random_uuid(),'a.pdf','application/pdf',1,'p/1',now());
                """);

            var ex = await Should.ThrowAsync<Exception>(() => env.MigrateToLatestAsync());
            ex.ToString().ShouldContain("UnifyDocuments");
            ex.ToString().ShouldContain(expectedMessage);

            // Nothing of the migration survived: same tables, same columns, no history row, data untouched.
            (await env.FlagAsync("SELECT to_regclass('\"ReceivedInvoice\"') IS NOT NULL")).ShouldBe(true);
            (await env.FlagAsync("SELECT to_regclass('\"ReceivedInvoiceIdMap_legacy\"') IS NULL")).ShouldBe(true);
            (await env.FlagAsync("SELECT to_regclass('\"InboundInvoiceEmailReceivedLink_legacy\"') IS NULL")).ShouldBe(true);
            (await env.CountAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'Invoice' AND column_name = 'ReceivedDate'")).ShouldBe(0);
            (await env.CountAsync($"SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{UnifyMigration}'")).ShouldBe(0);
            (await env.CountAsync("SELECT COUNT(*) FROM \"Invoice\"")).ShouldBe(1);
            (await env.CountAsync("SELECT COUNT(*) FROM \"ReceivedInvoice\"")).ShouldBe(1);
            (await env.ScalarAsync("SELECT \"RecordId\" FROM \"FileAttachment\" WHERE \"Id\" = 1")).ShouldBe(1L);
        });
    }

    // ─── Harness ─────────────────────────────────────────────────────────────

    private sealed class Env(NpgsqlConnection root, string schema, string scopedConnectionString, TenantDbContext db)
    {
        public Task MigrateToLatestAsync() => db.Database.MigrateAsync();

        public async Task ExecAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(scopedConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>First column of the first row, normalised: bigint/int -> long, bool as is.</summary>
        public async Task<object?> ScalarAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(scopedConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            var value = await cmd.ExecuteScalarAsync();
            return value is int i ? (long)i : value;
        }

        public async Task<long> CountAsync(string sql) => (long)(await ScalarAsync(sql))!;

        public async Task<bool> FlagAsync(string sql) => (bool)(await ScalarAsync(sql))!;

        public async Task<List<object?[]>> RowsAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(scopedConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<object?[]>();
            while (await reader.ReadAsync())
            {
                var row = new object?[reader.FieldCount];
                for (var c = 0; c < row.Length; c++)
                    row[c] = reader.IsDBNull(c) ? null : reader.GetValue(c);
                rows.Add(row);
            }
            return rows;
        }
    }

    private static async Task RunAsync(Func<Env, Task> test, bool migrateToPrevious = true)
    {
        var connectionString = Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(connectionString), "Set FAKVIO_TEST_POSTGRES to a disposable PostgreSQL database.");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        var schema = "test_unifydocs_" + Guid.NewGuid().ToString("N");
        await using var root = new NpgsqlConnection(connectionString);
        await root.OpenAsync();
        await Exec(root, $"CREATE SCHEMA \"{schema}\"");
        try
        {
            // The same search_path trick the application uses: unqualified table names resolve to the tenant schema.
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString;
            await using var db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
                .UseNpgsql(scoped, b =>
                {
                    b.MigrationsAssembly("Fakvio.Infrastructure");
                    b.MigrationsHistoryTable("__EFMigrationsHistory", schema);
                })
                .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options)
            { Schema = schema };

            if (migrateToPrevious)
                await db.Database.GetService<IMigrator>().MigrateAsync(Previous);

            await test(new Env(root, schema, scoped, db));
        }
        finally
        {
            // The schema name is generated locally above, never supplied by a caller.
            await Exec(root, $"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    private static async Task Exec(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
