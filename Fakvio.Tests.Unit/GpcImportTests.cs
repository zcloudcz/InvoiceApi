using System.Text;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>GPC/ABO parser (pure) and import service (in-memory TenantDbContext + real matcher).</summary>
public class GpcImportTests
{
    // ─── Sample line builders (fixed-width, 1-based positions per GpcParser docs) ───

    private static string Put(string line, int pos1, string value) =>
        line[..(pos1 - 1)] + value + line[(pos1 - 1 + value.Length)..];

    /// <summary>074 header for account 123-4567890 with statement date 15.03.2026.</summary>
    private static string Header(int seq = 7, string account = "0001230004567890")
    {
        var l = Put("074".PadRight(128), 4, account);
        l = Put(l, 20, "ACME s.r.o.".PadRight(20));
        l = Put(l, 106, $"{seq:000}");
        return Put(l, 109, "150326");
    }

    private static string Item(string code, long halere, string vs = "", string counter = "0000000000987654",
        string bank = "0800", string ks = "0308", string name = "Jan Novak", string doc = "0000000000001",
        string account = "0001230004567890", string valueDate = "140326")
    {
        var l = "".PadRight(128);
        l = Put(l, 1, "075");
        l = Put(l, 4, account);
        l = Put(l, 20, counter);
        l = Put(l, 36, doc);
        l = Put(l, 49, halere.ToString("000000000000"));
        l = Put(l, 61, code);
        l = Put(l, 62, vs.PadLeft(10, '0'));
        l = Put(l, 72, bank);
        l = Put(l, 78, ks);
        l = Put(l, 82, "0000000000");
        l = Put(l, 92, valueDate);
        l = Put(l, 98, name.PadRight(20));
        return l;
    }

    private static string Join(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    // ─── Parser ───

    [Fact]
    public void Parse_HeaderAndCredit_MapsAllFields()
    {
        var r = GpcParser.Parse(Join(Header(), Item("2", 123456, vs: "2026001")));

        r.Errors.ShouldBeEmpty();
        var st = r.Statements.ShouldHaveSingleItem();
        st.SequenceNumber.ShouldBe(7);
        st.StatementDate.ShouldBe(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));
        var it = st.Items.ShouldHaveSingleItem();
        it.Amount.ShouldBe(1234.56m);
        it.PostingCode.ShouldBe(2);
        it.VariableSymbol.ShouldBe("2026001");
        it.ConstantSymbol.ShouldBe("308");
        it.SpecificSymbol.ShouldBeNull();
        it.CounterAccount.ShouldBe("987654/0800");
        it.Name.ShouldBe("Jan Novak");
        it.ValueDate.ShouldBe(new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Parse_MultipleStatements_ItemsBelongToTheirHeader()
    {
        var other = "0000000009999999";
        var r = GpcParser.Parse(Join(
            Header(1), Item("2", 100),
            Header(2, other), Item("1", 200, account: other), Item("2", 300, account: other)));

        r.Statements.Count.ShouldBe(2);
        r.Statements[0].Items.Count.ShouldBe(1);
        r.Statements[1].Items.Count.ShouldBe(2);
    }

    [Fact]
    public void Parse_MalformedLines_AreSkippedAndReported()
    {
        var bad = Put(Item("2", 100), 49, "12X456789012");
        var r = GpcParser.Parse(Join(
            Item("2", 5),                 // item before any header
            Header(),
            bad,                          // non-numeric amount
            Item("9", 100),               // invalid posting code
            "076" + "".PadRight(60),      // additional info - ignored silently
            Item("2", 700)));             // valid

        r.Statements.ShouldHaveSingleItem().Items.ShouldHaveSingleItem().Amount.ShouldBe(7m);
        r.Errors.Count.ShouldBe(3);
    }

    [Fact]
    public void Parse_Windows1250_AndUtf8_BothDecoded()
    {
        var text = Join(Header(), Item("2", 100, name: "Josef Novak"));
        GpcParser.Parse(Encoding.UTF8.GetBytes(text)).Statements.ShouldHaveSingleItem();

        // A 1250-only byte (0xE8 = c with caron) in the name field must not break UTF-8 detection.
        var bytes = Encoding.Latin1.GetBytes(Join(Header(), Item("2", 100, name: "Josef Novak")));
        var idx = Array.IndexOf(bytes, (byte)'J', 100);
        bytes[idx] = 0xE8;
        var parsed = GpcParser.Parse(bytes);
        parsed.Statements.ShouldHaveSingleItem().Items[0].Name.ShouldStartWith("č");
    }

    // ─── Import service ───

    private static (TenantDbContext Db, BankStatementImportService Sut) Create()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new TenantDbContext(options);
        var czk = new Currency { Code = "CZK", Name = "Koruna", Symbol = "Kc", IsActive = true, SortOrder = 1 };
        var issuer = new Client { RegistrationNumber = "11111111", CompanyName = "Issuer", IsIssuer = true, IsActive = true };
        var client = new Client { RegistrationNumber = "22222222", CompanyName = "Client", IsActive = true };
        db.Currency.Add(czk);
        db.Client.AddRange(issuer, client);
        db.SaveChanges();
        db.BankAccount.Add(new BankAccount { ClientId = issuer.Id, AccountNumber = "123-4567890/0800", CurrencyCode = "CZK" });
        db.Invoice.Add(new Invoice
        {
            DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Completed, DocumentNumber = "INV-2026001",
            IssueDate = DateTime.UtcNow.AddDays(-10), DueDate = DateTime.UtcNow.AddDays(4),
            IssuerId = issuer.Id, ClientId = client.Id, VariableSymbol = "2026001",
            TotalBeforeVat = 1000m, TotalWithVat = 1000m, CurrencyId = czk.Id, InvoiceItem = new List<InvoiceItem>(),
        });
        db.SaveChanges();

        var matcher = new PaymentMatchingService(db, Substitute.For<INotificationService>(), Substitute.For<IInvoiceService>(),
            Substitute.For<ILogger<PaymentMatchingService>>());
        return (db, new BankStatementImportService(db, matcher, Substitute.For<ILogger<BankStatementImportService>>()));
    }

    private static byte[] Bytes(params string[] lines) => Encoding.UTF8.GetBytes(Join(lines));

    [Fact]
    public async Task Import_CreatesTransactions_MatchesIncoming_SkipsStornoAndDebitMatching()
    {
        var (db, sut) = Create();

        var r = await sut.ImportAsync(Bytes(
            Header(),
            Item("2", 100000, vs: "2026001", doc: "1"),   // pays the invoice
            Item("2", 5000, vs: "999", doc: "2"),          // no invoice -> unmatched
            Item("1", 2000, doc: "3"),                     // debit - imported, not matched against issued invoices
            Item("4", 2000, doc: "4")), null);             // storno - skipped with a warning

        r.Statements.ShouldBe(1);
        r.Imported.ShouldBe(3);
        r.Duplicates.ShouldBe(0);
        r.Matched.ShouldBe(1);
        r.Unmatched.ShouldBe(1);
        r.Errors.ShouldHaveSingleItem().ShouldContain("storno");
        (await db.Invoice.SingleAsync()).Status.ShouldBe(EInvoiceStatus.Paid);
        (await db.BankTransaction.CountAsync(t => t.ImportSource == EImportSource.GpcImport)).ShouldBe(3);
    }

    [Fact]
    public async Task Import_SameFileTwice_CreatesNothingTheSecondTime()
    {
        var (db, sut) = Create();
        // Two identical lines in one statement are two payments, not duplicates of each other.
        var file = Bytes(Header(), Item("2", 5000, vs: "999"), Item("2", 5000, vs: "999"));

        (await sut.ImportAsync(file, null)).Imported.ShouldBe(2);
        var second = await sut.ImportAsync(file, null);

        second.Imported.ShouldBe(0);
        second.Duplicates.ShouldBe(2);
        (await db.BankTransaction.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Import_UnknownAccount_ReportsErrorAndImportsNothing()
    {
        var (db, sut) = Create();
        var other = "0000000009999999";

        var r = await sut.ImportAsync(Bytes(Header(1, other), Item("2", 100, account: other)), null);

        r.Imported.ShouldBe(0);
        r.Errors.ShouldHaveSingleItem().ShouldContain("no matching bank account");
        (await db.BankTransaction.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Import_PaymentAlreadyIngestedFromImap_IsDuplicate_OneToOne()
    {
        var (db, sut) = Create();
        var accountId = (await db.BankAccount.SingleAsync()).Id;
        db.BankTransaction.Add(new BankTransaction
        {
            BankAccountId = accountId, DeduplicationHash = "imap-hash", ImportSource = EImportSource.InboundEmail,
            TransactionDate = new DateTime(2026, 3, 13, 22, 0, 0, DateTimeKind.Utc), // +-1 day tolerance
            Amount = 50m, Direction = EPaymentDirection.Incoming, VariableSymbol = "555", CurrencyCode = "CZK",
        });
        db.SaveChanges();

        // One GPC payment == the IMAP row.
        var one = await sut.ImportAsync(Bytes(Header(), Item("2", 5000, vs: "555")), null);
        one.Imported.ShouldBe(0);
        one.Duplicates.ShouldBe(1);

        // Two identical GPC payments, one IMAP row: only one of them is the twin.
        var two = await sut.ImportAsync(Bytes(Header(), Item("2", 5000, vs: "555"), Item("2", 5000, vs: "555")), null);
        two.Imported.ShouldBe(1);
        two.Duplicates.ShouldBe(1);
    }

    [Fact]
    public async Task Import_OutgoingDebit_MatchesReceivedInvoice()
    {
        var (db, sut) = Create();
        db.ReceivedInvoice.Add(new ReceivedInvoice
        {
            DocumentNumber = "RI-1", Status = EReceivedInvoiceStatus.Approved, SupplierId = 1,
            CurrencyId = db.Currency.Single().Id, TotalWithVat = 300m, TotalBeforeVat = 300m, VariableSymbol = "777",
        });
        db.SaveChanges();

        var r = await sut.ImportAsync(Bytes(Header(), Item("1", 30000, vs: "777")), null);

        r.Imported.ShouldBe(1);
        r.Matched.ShouldBe(1);
        (await db.ReceivedInvoice.SingleAsync()).Status.ShouldBe(EReceivedInvoiceStatus.Paid);
    }

    [Fact]
    public void AccountMatches_Iban()
    {
        var acc = new BankAccount { AccountNumber = "", IBAN = "CZ65 0800 0001 2300 0456 7890" };
        BankStatementImportService.AccountMatches(acc, "0001230004567890").ShouldBeTrue();
        BankStatementImportService.AccountMatches(acc, "0000000000000001").ShouldBeFalse();
    }

    [Fact]
    public void Parse_BlankValueDate_FallsBackToPostingDate()
    {
        var line = Put(Item("2", 100, valueDate: "      "), 123, "100326");
        GpcParser.Parse(Join(Header(), line)).Statements[0].Items[0].ValueDate
            .ShouldBe(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("123-4567890/0800", "0001230004567890", true)]
    [InlineData("4567890/0800", "0000000004567890", true)]
    [InlineData("4567890/0800", "0001230004567890", false)]
    [InlineData("1234567890/0100", "0000001234567890", true)]
    public void AccountMatches_HandlesPrefixAndPadding(string ours, string gpc, bool expected) =>
        BankStatementImportService.AccountMatches(ours, gpc).ShouldBe(expected);
}
