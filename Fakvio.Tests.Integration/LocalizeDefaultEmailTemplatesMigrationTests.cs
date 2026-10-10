using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// The LocalizeDefaultEmailTemplates migrations (Master + Tenant) on a REAL PostgreSQL, in a disposable schema.
/// The migrations are hand-written SQL, so InMemory cannot say anything about them:
///  * rows that are still the original (English) seed become Czech, rows a user edited are NOT touched;
///  * English defaults are inserted without fixed Ids (even when the identity sequence is behind the seeded Ids)
///    and never duplicated;
///  * Down() reverts only what Up() wrote.
/// Skipped when FAKVIO_TEST_POSTGRES is not set (same convention as EmailAttachmentDefaultsMigrationTests).
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class LocalizeDefaultEmailTemplatesMigrationTests
{
    private const string MasterPrevious = "20261005174836_AddEmailAttachmentDefaults";
    private const string TenantPrevious = "20261005174849_AddEmailAttachmentDefaults";

    /// <summary>The four default e-mails that used to be English although labelled Czech.</summary>
    private static readonly EContentTemplateType[] WronglyEnglish =
    [
        EContentTemplateType.InvoiceEmail, EContentTemplateType.CreditNoteEmail,
        EContentTemplateType.InvitationEmail, EContentTemplateType.ReminderEmail
    ];

    private static readonly EContentTemplateType[] AllLocalized =
    [
        .. WronglyEnglish, EContentTemplateType.AdvanceInvoiceEmail, EContentTemplateType.TaxReceiptForAdvanceEmail
    ];

    private sealed record Row(long Id, int Type, string Language, bool IsDefault, bool IsActive, string Name, string Subject, string Body);

    /// <summary>Czech text the migration writes, by template type.</summary>
    private static (string Subject, string Body) Czech(EContentTemplateType t) => t switch
    {
        EContentTemplateType.InvoiceEmail => (DefaultEmailTemplates.InvoiceCsSubject, DefaultEmailTemplates.InvoiceCsBody),
        EContentTemplateType.CreditNoteEmail => (DefaultEmailTemplates.CreditNoteCsSubject, DefaultEmailTemplates.CreditNoteCsBody),
        EContentTemplateType.InvitationEmail => (DefaultEmailTemplates.InvitationCsSubject, DefaultEmailTemplates.InvitationCsBody),
        EContentTemplateType.ReminderEmail => (DefaultEmailTemplates.ReminderCsSubject, DefaultEmailTemplates.ReminderCsBody),
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    /// <summary>English text the migration inserts, by template type.</summary>
    private static (string Subject, string Body) English(EContentTemplateType t) => t switch
    {
        EContentTemplateType.InvoiceEmail => (DefaultEmailTemplates.InvoiceEnSubject, DefaultEmailTemplates.InvoiceEnBody),
        EContentTemplateType.CreditNoteEmail => (DefaultEmailTemplates.CreditNoteEnSubject, DefaultEmailTemplates.CreditNoteEnBody),
        EContentTemplateType.InvitationEmail => (DefaultEmailTemplates.InvitationEnSubject, DefaultEmailTemplates.InvitationEnBody),
        EContentTemplateType.ReminderEmail => (DefaultEmailTemplates.ReminderEnSubject, DefaultEmailTemplates.ReminderEnBody),
        EContentTemplateType.AdvanceInvoiceEmail => (DefaultEmailTemplates.AdvanceInvoiceEnSubject, DefaultEmailTemplates.AdvanceInvoiceEnBody),
        EContentTemplateType.TaxReceiptForAdvanceEmail => (DefaultEmailTemplates.TaxReceiptEnSubject, DefaultEmailTemplates.TaxReceiptEnBody),
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Up_TranslatesUntouchedSeed_AndAddsEnglishDefaults_EvenWhenSequenceIsBehind(bool isTenant)
    {
        await RunAsync(isTenant, async env =>
        {
            // Old database: English text under "cs", no English rows. Simulate a sequence that is behind the seeded Ids.
            var before = await env.ReadAsync();
            before.Single(r => r.Type == 10 && r.Language == "cs").Subject.ShouldBe(DefaultEmailTemplates.InvoiceEnSubject);
            before.Count(r => r.Language == "en").ShouldBe(0);
            await env.ExecAsync("SELECT setval(pg_get_serial_sequence('\"" + env.Schema + "\".\"ContentTemplate\"', 'Id'), 1, false)");

            await env.MigrateToLatestAsync();

            var after = await env.ReadAsync();

            // Czech text where the seed was untouched ...
            foreach (var type in WronglyEnglish)
            {
                var cs = after.Single(r => r.Type == (int)type && r.Language == "cs");
                (cs.Subject, cs.Body).ShouldBe(Czech(type));
            }

            // ... the already-Czech advance/tax-receipt e-mails and unrelated rows are not touched ...
            after.Single(r => r.Type == 12 && r.Language == "cs").Subject.ShouldStartWith("Zálohová faktura");
            after.Single(r => r.Type == 13 && r.Language == "cs").Subject.ShouldStartWith("Daňový doklad");
            after.Count(r => r.Language == "cs").ShouldBe(before.Count);

            // ... and exactly one English default per e-mail type, with a fresh Id (PK would have thrown on a clash).
            foreach (var type in AllLocalized)
            {
                var en = after.Single(r => r.Type == (int)type && r.Language == "en");
                en.IsDefault.ShouldBeTrue();
                en.IsActive.ShouldBeTrue();
                (en.Subject, en.Body).ShouldBe(English(type));
            }
            after.Count(r => r.Language == "en").ShouldBe(AllLocalized.Length);
        });
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Up_LeavesUserEditedTemplatesUntouched(bool isTenant)
    {
        await RunAsync(isTenant, async env =>
        {
            // The user edited the credit-note subject and only the BODY of the invitation. Either edit means "theirs".
            await env.ExecAsync(
                $"UPDATE {env.Table} SET \"Subject\" = 'Moje vlastní předmět' WHERE \"TemplateType\" = 11 AND \"Language\" = 'cs'");
            await env.ExecAsync(
                $"UPDATE {env.Table} SET \"HtmlBody\" = \"HtmlBody\" || '<p>Naše vlastní dodatky</p>' WHERE \"TemplateType\" = 20 AND \"Language\" = 'cs'");

            await env.MigrateToLatestAsync();
            var after = await env.ReadAsync();

            var creditNote = after.Single(r => r.Type == 11 && r.Language == "cs");
            creditNote.Subject.ShouldBe("Moje vlastní předmět");
            creditNote.Body.ShouldBe(DefaultEmailTemplates.CreditNoteEnBody, "an edited row must not be rewritten at all");

            var invitation = after.Single(r => r.Type == 20 && r.Language == "cs");
            invitation.Subject.ShouldBe(DefaultEmailTemplates.InvitationEnSubject);
            invitation.Body.ShouldEndWith("<p>Naše vlastní dodatky</p>");

            // The rows nobody touched are still translated, and the English defaults exist for every type.
            after.Single(r => r.Type == 10 && r.Language == "cs").Subject.ShouldBe(DefaultEmailTemplates.InvoiceCsSubject);
            after.Single(r => r.Type == 21 && r.Language == "cs").Subject.ShouldBe(DefaultEmailTemplates.ReminderCsSubject);
            after.Count(r => r.Language == "en").ShouldBe(AllLocalized.Length);
        });
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Up_DoesNotDuplicateExistingEnglishTemplates_AndIsRerunnable(bool isTenant)
    {
        await RunAsync(isTenant, async env =>
        {
            // A user already created their own English invoice e-mail (not a default).
            await env.ExecAsync(
                $"INSERT INTO {env.Table} (\"Name\", \"Subject\", \"HtmlBody\", \"TemplateType\", \"IsDefault\", \"IsActive\", \"Language\", \"CreatedAt\") " +
                "VALUES ('My EN invoice', 'Hello', '<p>Hi</p>', 10, FALSE, TRUE, 'en', now())");

            await env.MigrateToLatestAsync();
            var after = await env.ReadAsync();

            // The type that already had an English template got no second one; it is exactly the user's row.
            var invoiceEn = after.Single(r => r.Type == 10 && r.Language == "en");
            invoiceEn.Name.ShouldBe("My EN invoice");
            invoiceEn.IsDefault.ShouldBeFalse();
            after.Count(r => r.Language == "en").ShouldBe(AllLocalized.Length);

            // Running the same statements again (e.g. a repeated deployment of the script) changes nothing.
            await using var scoped = new NpgsqlConnection(env.ScopedConnectionString);
            await scoped.OpenAsync();
            foreach (var sql in LocalizeDefaultEmailTemplatesSql.Up())
            {
                await using var cmd = new NpgsqlCommand(sql, scoped);
                await cmd.ExecuteNonQueryAsync();
            }

            var again = await env.ReadAsync();
            again.Select(r => (r.Id, r.Type, r.Language, r.Subject, r.Body))
                .ShouldBe(after.Select(r => (r.Id, r.Type, r.Language, r.Subject, r.Body)));
        });
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Down_RevertsOnlyRowsStillEqualToWhatUpWrote(bool isTenant)
    {
        await RunAsync(isTenant, async env =>
        {
            // Tenant: stop at THIS migration - the later UnifyDocumentsInInvoiceTable migration cannot be reverted by design.
            if (isTenant)
                await env.MigrateAsync("20261005184918_LocalizeDefaultEmailTemplates");
            else
                await env.MigrateToLatestAsync();

            // After the upgrade the user edits the Czech reminder and the English credit-note e-mail.
            await env.ExecAsync($"UPDATE {env.Table} SET \"Subject\" = 'Vlastní upomínka' WHERE \"TemplateType\" = 21 AND \"Language\" = 'cs'");
            await env.ExecAsync($"UPDATE {env.Table} SET \"Subject\" = 'My credit note' WHERE \"TemplateType\" = 11 AND \"Language\" = 'en'");

            await env.MigrateAsync(isTenant ? TenantPrevious : MasterPrevious);
            var after = await env.ReadAsync();

            // Untouched Czech rows are English again (the original seed), the edited one stays.
            after.Single(r => r.Type == 10 && r.Language == "cs").Subject.ShouldBe(DefaultEmailTemplates.InvoiceEnSubject);
            after.Single(r => r.Type == 10 && r.Language == "cs").Body.ShouldBe(DefaultEmailTemplates.InvoiceEnBody);
            after.Single(r => r.Type == 21 && r.Language == "cs").Subject.ShouldBe("Vlastní upomínka");

            // Untouched English rows are gone, the edited one stays.
            after.Where(r => r.Language == "en").Select(r => r.Type).ShouldBe([11]);
        });
    }

    // ─── Harness ─────────────────────────────────────────────────────────────

    private sealed class Env(NpgsqlConnection root, string schema, string scopedConnectionString, DbContext db)
    {
        public string Schema => schema;
        public string Table => $"\"{schema}\".\"ContentTemplate\"";
        public string ScopedConnectionString => scopedConnectionString;

        public Task MigrateToLatestAsync() => db.Database.MigrateAsync();

        public Task MigrateAsync(string target) => db.Database.GetService<IMigrator>().MigrateAsync(target);

        public async Task ExecAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, root);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<List<Row>> ReadAsync()
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT \"Id\", \"TemplateType\", \"Language\", \"IsDefault\", \"IsActive\", \"Name\", COALESCE(\"Subject\", ''), \"HtmlBody\" " +
                $"FROM {Table} ORDER BY \"Id\"", root);
            var rows = new List<Row>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(new Row(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3),
                    reader.GetBoolean(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
            return rows;
        }
    }

    /// <summary>Creates a disposable schema, migrates it to the version BEFORE this change and hands it to the test.</summary>
    private static async Task RunAsync(bool isTenant, Func<Env, Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("FAKVIO_TEST_POSTGRES");
        Skip.If(string.IsNullOrEmpty(connectionString), "Set FAKVIO_TEST_POSTGRES to a disposable PostgreSQL database.");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        var schema = "test_emailloc_" + Guid.NewGuid().ToString("N");
        await using var root = new NpgsqlConnection(connectionString);
        await root.OpenAsync();
        await Exec(root, $"CREATE SCHEMA \"{schema}\"");
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString;
            DbContext db = isTenant ? CreateTenant(scoped, schema) : CreateMaster(scoped);
            await using (db)
            {
                await db.Database.GetService<IMigrator>().MigrateAsync(isTenant ? TenantPrevious : MasterPrevious);
                await test(new Env(root, schema, scoped, db));
            }
        }
        finally
        {
            // The schema name is generated locally above, never supplied by a caller.
            await Exec(root, $"DROP SCHEMA \"{schema}\" CASCADE");
        }
    }

    private static MasterDbContext CreateMaster(string connectionString) =>
        new(new DbContextOptionsBuilder<MasterDbContext>().UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);

    private static TenantDbContext CreateTenant(string connectionString, string schema) =>
        new(new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connectionString, b =>
            {
                b.MigrationsAssembly("Fakvio.Infrastructure");
                b.MigrationsHistoryTable("__EFMigrationsHistory", schema);
            })
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options)
        { Schema = schema };

    private static async Task Exec(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
