using System.Text;
using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Integration-style tests for the UBL branch of <see cref="InvoiceEmailProcessor"/>
/// (F1.10 — import UBL/Peppol BIS invoices received by email, see
/// docs/adr/0002-sk-einvoicing-peppol.md). Uses a real InMemory TenantDbContext (the
/// processor queries Client/InvoiceMailbox/InboundInvoiceEmail directly) with the
/// downstream services mocked.
/// </summary>
public class InvoiceEmailProcessorUblTests
{
    private const string CompanyIco = "12345678"; // our company (the recipient)
    private const string SupplierIco = "87654321"; // the invoice issuer

    private readonly TenantDbContext _context;
    private readonly IUblImportParser _ublParser = Substitute.For<IUblImportParser>();
    private readonly IReceivedInvoiceService _receivedInvoiceService = Substitute.For<IReceivedInvoiceService>();
    private readonly InvoiceEmailProcessor _sut;
    private long _mailboxId;

    public InvoiceEmailProcessorUblTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options) { Schema = "tenant_1" };

        _receivedInvoiceService.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto { Id = 42, DocumentNumber = "UBL-100" });

        _sut = new InvoiceEmailProcessor(
            _context,
            Substitute.For<IIsdocImportParser>(),
            _ublParser,
            Substitute.For<IInvoiceEmailClassifier>(),
            Substitute.For<IInvoiceImportService>(),
            Substitute.For<IClientService>(),
            _receivedInvoiceService,
            Substitute.For<IInvoiceService>(),
            Substitute.For<INotificationService>(),
            Substitute.For<ILogger<InvoiceEmailProcessor>>());
    }

    private async Task SeedAsync()
    {
        var mailbox = new InvoiceMailbox
        {
            InboundAlias = "fak-test",
            IsActive = true,
            ActiveFrom = DateTime.UtcNow.AddDays(-30),
        };
        _context.InvoiceMailbox.Add(mailbox);

        // Our company (recipient) — direction is decided by comparing this to the
        // parsed issuer IČO, no AI classifier needed when both are present.
        _context.Client.Add(new Client { IsIssuer = true, RegistrationNumber = CompanyIco, CompanyName = "Our Company s.r.o." });

        // Supplier already known — avoids exercising client auto-creation in this test.
        _context.Client.Add(new Client { IsIssuer = false, RegistrationNumber = SupplierIco, CompanyName = "Supplier s.r.o." });

        await _context.SaveChangesAsync();
        _mailboxId = mailbox.Id;
    }

    private InvoiceEmailPayload BuildPayload() => new(
        InvoiceMailboxId: _mailboxId,
        MessageId: "msg-1@example.com",
        ImapUid: "1",
        ServerReceivedAt: DateTime.UtcNow,
        FromAddress: "supplier@example.com",
        FromDisplayName: "Supplier",
        ToAddress: "fak-test@fakvio.cz",
        Subject: "Invoice",
        EmailDate: DateTime.UtcNow,
        TextBody: "Please find attached invoice.",
        HtmlBody: null);

    [Fact]
    public async Task ProcessAsync_UblAttachment_CreatesReceivedInvoiceWithoutAi()
    {
        await SeedAsync();

        _ublParser.Parse(Arg.Any<byte[]>()).Returns(new InvoiceExtractedData
        {
            DocumentNumber = "UBL-100",
            IssuerRegistrationNumber = SupplierIco,
            IssuerName = "Supplier s.r.o.",
            TotalAmount = 1210m,
            TotalBeforeVat = 1000m,
            Currency = "EUR",
            IssueDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            VariableSymbol = "1000100",
            IBAN = "SK6807200002891987426353",
            Items =
            [
                new ExtractedInvoiceItem { Description = "Consulting", Quantity = 1m, UnitPrice = 1000m, VatRate = 21m, Unit = "DAY" }
            ],
            Source = EExtractionSource.Merged,
        });

        var attachment = new EmailAttachment("invoice.xml", "application/xml", Encoding.UTF8.GetBytes("<Invoice/>"));

        var status = await _sut.ProcessAsync(BuildPayload(), [attachment], companyId: 1);

        status.ShouldBe(EInvoiceEmailStatus.Imported);

        // The parser was called with the raw attachment bytes — no AI/PDF pipeline involved.
        _ublParser.Received(1).Parse(Arg.Any<byte[]>());

        await _receivedInvoiceService.Received(1).CreateAsync(
            Arg.Is<CreateReceivedInvoiceDto>(dto =>
                dto.DocumentNumber == "UBL-100"
                && dto.VariableSymbol == "1000100"
                && dto.IBAN == "SK6807200002891987426353"
                && dto.Items.Count == 1
                && dto.Items[0].Description == "Consulting"),
            Arg.Any<CancellationToken>());

        var email = await _context.InboundInvoiceEmail.SingleAsync();
        email.Status.ShouldBe(EInvoiceEmailStatus.Imported);
        email.ReceivedInvoiceId.ShouldBe(42L);
        email.Direction.ShouldBe(EInvoiceDirection.Received);
    }

    [Fact]
    public async Task ProcessAsync_UnparsableUblAttachment_FailsReadablyWithoutThrowing()
    {
        await SeedAsync();

        // Simulates malformed/unrecognized XML — the parser's own contract (never throws,
        // returns null). The processor must turn this into a readable Failed status, not
        // an unhandled exception.
        _ublParser.Parse(Arg.Any<byte[]>()).Returns((InvoiceExtractedData?)null);

        var attachment = new EmailAttachment("broken.xml", "application/xml", Encoding.UTF8.GetBytes("<not-ubl/>"));

        var status = await _sut.ProcessAsync(BuildPayload(), [attachment], companyId: 1);

        status.ShouldBe(EInvoiceEmailStatus.Failed);
        await _receivedInvoiceService.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }
}
