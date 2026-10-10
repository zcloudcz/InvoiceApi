using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Issued and received documents share the <c>Invoice</c> table. These tests pin the by-id isolation rule:
/// an operation for ISSUED documents that is given the id of a RECEIVED document behaves as "not found",
/// and vice versa. Without it the id of a supplier invoice could be completed, mailed, exported as ours, ...
/// </summary>
public class DocumentSideIsolationTests : IDisposable
{
    private readonly TenantDbContext _db;
    private readonly InvoiceService _invoices;
    private readonly ReceivedInvoiceService _received;
    private readonly PaymentMatchingService _matching;
    private readonly long _issuedId;
    private readonly long _receivedId;
    private readonly long _txId;

    public DocumentSideIsolationTests()
    {
        _db = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        _db.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true });
        _db.Client.Add(new Client { Id = 1, CompanyName = "Us", RegistrationNumber = "11111111", IsIssuer = true, IsActive = true });
        _db.Client.Add(new Client { Id = 2, CompanyName = "Customer", RegistrationNumber = "22222222", IsActive = true });
        _db.Client.Add(new Client { Id = 3, CompanyName = "Supplier", RegistrationNumber = "33333333", IsActive = true });
        _db.SaveChanges();

        var issued = NewDoc(EDocumentType.Invoice, "2026001", clientId: 2);
        var received = NewDoc(EDocumentType.ReceivedInvoice, "SUP-77", clientId: 3);
        _db.Invoice.AddRange(issued, received);
        _db.BankTransaction.Add(new BankTransaction
        {
            BankAccountId = 1, Amount = 1210m, CurrencyCode = "CZK", Direction = EPaymentDirection.Outgoing,
            MatchStatus = EMatchStatus.Unmatched, TransactionDate = DateTime.UtcNow, DeduplicationHash = "h1"
        });
        _db.SaveChanges();
        _issuedId = issued.Id;
        _receivedId = received.Id;
        _txId = _db.BankTransaction.Single().Id;

        _invoices = new InvoiceService(_db, Substitute.For<INumberSequenceService>(),
            Substitute.For<ITenantReadinessService>(), NullLogger<InvoiceService>.Instance);
        _received = new ReceivedInvoiceService(_db, NullLogger<ReceivedInvoiceService>.Instance);
        _matching = new PaymentMatchingService(_db, Substitute.For<INotificationService>(), _invoices,
            NullLogger<PaymentMatchingService>.Instance);
    }

    public void Dispose()
    {
        _db.Database.EnsureDeleted();
        _db.Dispose();
    }

    private static Invoice NewDoc(EDocumentType type, string number, long clientId) => new()
    {
        DocumentType = type, Status = EInvoiceStatus.Completed, DocumentNumber = number, ClientId = clientId,
        IssuerId = 1, CurrencyId = 1, VariableSymbol = number, IssueDate = DateTime.UtcNow.AddDays(-30),
        DueDate = DateTime.UtcNow.AddDays(-10), TaxableSupplyDate = DateTime.UtcNow.AddDays(-30),
        TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m
    };

    // ─── Issued-side operations given a RECEIVED id ──────────────────────────

    [Fact]
    public async Task IssuedOperations_GivenReceivedId_ActAsNotFound()
    {
        (await _invoices.GetInvoiceByIdAsync(_receivedId)).ShouldBeNull();
        (await _invoices.GetInvoiceByDocumentNumberAsync("SUP-77")).ShouldBeNull();
        (await _invoices.UpdateInvoiceAsync(_receivedId, new UpdateInvoiceDto { Notes = "x" })).ShouldBeNull();
        (await _invoices.CompleteInvoiceAsync(_receivedId)).ShouldBeNull();
        (await _invoices.MarkAsPaidAsync(_receivedId)).ShouldBeNull();
        (await _invoices.RevertToDraftAsync(_receivedId)).ShouldBeNull();
        (await _invoices.DeleteInvoiceAsync(_receivedId)).ShouldBeFalse();
        (await _invoices.RestoreInvoiceAsync(_receivedId)).ShouldBeNull();
        (await _invoices.GetCreditNotesForInvoiceAsync(_receivedId)).ShouldBeEmpty();

        await Should.ThrowAsync<InvalidOperationException>(
            () => _invoices.CreateCreditNoteAsync(_receivedId, new CreateInvoiceDto()));

        // The supplier invoice is untouched by all of the above.
        var row = await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId);
        row.Status.ShouldBe(EInvoiceStatus.Completed);
        row.Notes.ShouldBeNull();
    }

    [Fact]
    public async Task BulkOperations_GivenReceivedId_FailTheItemAndLeaveItUntouched()
    {
        (await _invoices.BulkCompleteAsync([_receivedId])).FailedCount.ShouldBe(1);
        (await _invoices.BulkMarkAsPaidAsync([_receivedId])).FailedCount.ShouldBe(1);
        (await _invoices.BulkDeleteAsync([_receivedId])).FailedCount.ShouldBe(1);

        (await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId)).Status.ShouldBe(EInvoiceStatus.Completed);
    }

    [Fact]
    public async Task IssuedLists_DoNotContainReceivedDocuments()
    {
        var all = await _invoices.GetAllInvoicesAsync();

        all.Select(i => i.Id).ShouldBe([_issuedId]);
    }

    [Fact]
    public async Task IssuedExports_GivenReceivedId_ActAsNotFound()
    {
        var isdoc = new IsdocExportService(_db, NullLogger<IsdocExportService>.Instance);
        var ubl = new UblExportService(_db, NullLogger<UblExportService>.Instance);
        var qr = new QrPaymentService(_db, Substitute.For<IPayliboClient>(), NullLogger<QrPaymentService>.Instance);

        await Should.ThrowAsync<KeyNotFoundException>(() => isdoc.ExportInvoiceAsync(_receivedId));
        await Should.ThrowAsync<KeyNotFoundException>(() => ubl.ExportInvoiceAsync(_receivedId));
        await Should.ThrowAsync<KeyNotFoundException>(() => qr.GenerateSindStringAsync(_receivedId));
    }

    // ─── Received-side operations given an ISSUED id ─────────────────────────

    [Fact]
    public async Task ReceivedOperations_GivenIssuedId_ActAsNotFound()
    {
        (await _received.GetByIdAsync(_issuedId)).ShouldBeNull();
        (await _received.UpdateAsync(_issuedId, new Contracts.Dto.ReceivedInvoice.UpdateReceivedInvoiceDto { Notes = "x" })).ShouldBeNull();
        (await _received.ApproveAsync(_issuedId)).ShouldBeNull();
        (await _received.MarkAsPaidAsync(_issuedId)).ShouldBeNull();
        (await _received.RejectAsync(_issuedId)).ShouldBeNull();
        (await _received.DeleteAsync(_issuedId)).ShouldBeFalse();
        (await _received.GetDocumentNumbersAsync([_issuedId, _receivedId])).Keys.ShouldBe([_receivedId]);
        (await _received.GetAllAsync()).Select(r => r.Id).ShouldBe([_receivedId]);

        (await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _issuedId)).Status.ShouldBe(EInvoiceStatus.Completed);
    }

    [Fact]
    public async Task ReceivedIsdocExport_GivenIssuedId_ActsAsNotFound()
    {
        var isdoc = new IsdocExportService(_db, NullLogger<IsdocExportService>.Instance);

        await Should.ThrowAsync<KeyNotFoundException>(() => isdoc.ExportReceivedInvoiceAsync(_issuedId));
    }

    // ─── Payment matching across the sides ───────────────────────────────────

    [Fact]
    public async Task PaymentMatching_IssuedOperations_GivenReceivedId_ActAsNotFound()
    {
        (await _matching.GetPaymentsForInvoiceAsync(_receivedId)).ShouldBeEmpty();
        (await _matching.FindAutoMatchForInvoiceAsync(_receivedId)).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(
            () => _matching.ManualMatchAsync(_txId, _receivedId, 100m, null, null));
        await Should.ThrowAsync<InvalidOperationException>(
            () => _matching.ConfirmAutoMatchAsync(_txId, _receivedId, null, null));

        (await _db.PaymentMatch.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task PaymentMatching_ReceivedOperations_GivenIssuedId_ActAsNotFound()
    {
        (await _matching.GetPaymentsForReceivedInvoiceAsync(_issuedId)).ShouldBeEmpty();
        (await _matching.FindAutoMatchForReceivedInvoiceAsync(_issuedId)).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(
            () => _matching.ConfirmAutoMatchAsync(_txId, null, _issuedId, null));

        (await _db.PaymentMatch.CountAsync()).ShouldBe(0);
    }

    // ─── PaidAmount stays correct for received documents ─────────────────────

    [Fact]
    public async Task ConfirmAutoMatch_Received_UpdatesPaidAmount_AndUnmatchRevertsIt()
    {
        var result = await _matching.ConfirmAutoMatchAsync(_txId, null, _receivedId, null);

        var paid = await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId);
        paid.Status.ShouldBe(EInvoiceStatus.Paid);
        paid.PaidAmount.ShouldBe(1210m);
        (await _db.PaymentMatch.SingleAsync()).InvoiceId.ShouldBe(_receivedId);

        await _matching.UnmatchAsync(result.PaymentMatchId, null, null);

        var reverted = await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId);
        reverted.Status.ShouldBe(EInvoiceStatus.Completed);
        reverted.PaidAmount.ShouldBe(0m);
        reverted.PaidAt.ShouldBeNull();
    }
}
