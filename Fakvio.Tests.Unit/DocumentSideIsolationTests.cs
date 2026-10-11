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
/// Issued and received documents share the <c>Invoice</c> table. The contract (DEVGUIDE §5.6):
///   - the UNIFIED document operations (<see cref="InvoiceService"/>: get / update / every transition / delete /
///     restore / bulk) take the type from the stored row and work for BOTH sides;
///   - lists have an explicit scope and by default never mix the sides;
///   - the LEGACY received facade (<see cref="ReceivedInvoiceService"/>) still refuses an issued id as "not found";
///   - the issued-only reports / exports stay on their side.
/// Seeding goes through a SEPARATE DbContext from the services under test (EF fix-up would otherwise hide a missing
/// <c>.Include()</c> in a read the service does).
/// </summary>
public class DocumentSideIsolationTests : IDisposable
{
    private readonly TenantDbContext _seed;
    private readonly TenantDbContext _db;
    private readonly InvoiceService _invoices;
    private readonly ReceivedInvoiceService _received;
    private readonly PaymentMatchingService _matching;
    private readonly long _issuedId;
    private readonly long _receivedId;
    private readonly long _txId;

    public DocumentSideIsolationTests()
    {
        // Two contexts over ONE in-memory store: _seed writes the fixtures, _db is what the services use.
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _seed = new TenantDbContext(options);
        _db = new TenantDbContext(options);

        _seed.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true });
        _seed.Client.Add(new Client { Id = 1, CompanyName = "Us", RegistrationNumber = "11111111", IsIssuer = true, IsActive = true });
        _seed.Client.Add(new Client { Id = 2, CompanyName = "Customer", RegistrationNumber = "22222222", IsActive = true });
        _seed.Client.Add(new Client { Id = 3, CompanyName = "Supplier", RegistrationNumber = "33333333", IsActive = true });
        _seed.SaveChanges();

        var issued = NewDoc(EDocumentType.Invoice, "2026001", clientId: 2);
        var received = NewDoc(EDocumentType.ReceivedInvoice, "SUP-77", clientId: 3);
        received.ReceivedDate = DateTime.UtcNow.Date;
        _seed.Invoice.AddRange(issued, received);
        _seed.BankTransaction.Add(new BankTransaction
        {
            BankAccountId = 1, Amount = 1210m, CurrencyCode = "CZK", Direction = EPaymentDirection.Outgoing,
            MatchStatus = EMatchStatus.Unmatched, TransactionDate = DateTime.UtcNow, DeduplicationHash = "h1"
        });
        _seed.SaveChanges();
        _issuedId = issued.Id;
        _receivedId = received.Id;
        _txId = _seed.BankTransaction.Single().Id;

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
        _seed.Dispose();
    }

    private static Invoice NewDoc(EDocumentType type, string number, long clientId) => new()
    {
        DocumentType = type, Status = EInvoiceStatus.Completed, DocumentNumber = number, ClientId = clientId,
        IssuerId = 1, CurrencyId = 1, VariableSymbol = number, IssueDate = DateTime.UtcNow.AddDays(-30),
        DueDate = DateTime.UtcNow.AddDays(-10), TaxableSupplyDate = DateTime.UtcNow.AddDays(-30),
        TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m
    };

    // ─── Unified document operations given a RECEIVED id ─────────────────────

    [Fact]
    public async Task UnifiedReads_GivenReceivedId_ReturnTheReceivedDocument()
    {
        var dto = (await _invoices.GetInvoiceByIdAsync(_receivedId))!;

        dto.DocumentType.ShouldBe(EDocumentType.ReceivedInvoice);
        dto.DocumentNumber.ShouldBe("SUP-77");
        dto.ClientName.ShouldBe("Supplier"); // the supplier sits in the client fields
        dto.ReceivedDate.ShouldBe(DateTime.UtcNow.Date);
        (await _invoices.GetDocumentTypeAsync(_receivedId)).ShouldBe(EDocumentType.ReceivedInvoice);
        (await _invoices.GetDocumentTypeAsync(_issuedId)).ShouldBe(EDocumentType.Invoice);
        (await _invoices.GetDocumentTypeAsync(99999)).ShouldBeNull();
    }

    [Fact]
    public async Task DocumentNumberLookup_IsScoped_IssuedByDefault()
    {
        // A supplier number is not unique and may equal ours, so the search needs an explicit scope.
        (await _invoices.GetInvoiceByDocumentNumberAsync("SUP-77")).ShouldBeNull();
        (await _invoices.GetInvoiceByDocumentNumberAsync("2026001"))!.Id.ShouldBe(_issuedId);
        (await _invoices.GetInvoiceByDocumentNumberAsync("SUP-77", documentType: EDocumentType.ReceivedInvoice))!.Id.ShouldBe(_receivedId);
        (await _invoices.GetInvoiceByDocumentNumberAsync("2026001", documentType: EDocumentType.ReceivedInvoice)).ShouldBeNull();
    }

    [Fact]
    public async Task UnifiedTransitions_GivenReceivedId_RunTheSharedStateMachine_WithoutIssuedPolicy()
    {
        var sequences = Substitute.For<INumberSequenceService>();
        var readiness = Substitute.For<ITenantReadinessService>();
        var invoices = new InvoiceService(_db, sequences, readiness, NullLogger<InvoiceService>.Instance);

        // Completed -> Paid -> Completed -> Draft -> Completed again -> Draft -> Deleted -> Draft
        (await invoices.MarkAsPaidAsync(_receivedId))!.Status.ShouldBe(EInvoiceStatus.Paid);
        (await invoices.GetInvoiceByIdAsync(_receivedId))!.PaidAmount.ShouldBe(1210m);
        (await invoices.MarkAsUnpaidAsync(_receivedId)).Status.ShouldBe(EInvoiceStatus.Completed);
        (await invoices.RevertToDraftAsync(_receivedId))!.Status.ShouldBe(EInvoiceStatus.Draft);
        (await invoices.CompleteInvoiceAsync(_receivedId))!.Status.ShouldBe(EInvoiceStatus.Completed); // "approve"
        (await invoices.RevertToDraftAsync(_receivedId))!.Status.ShouldBe(EInvoiceStatus.Draft);
        (await invoices.DeleteInvoiceAsync(_receivedId)).ShouldBeTrue();
        (await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId)).Status.ShouldBe(EInvoiceStatus.Deleted);
        (await invoices.RestoreInvoiceAsync(_receivedId))!.Status.ShouldBe(EInvoiceStatus.Draft);

        // The issued policy never ran: the supplier's number stays untouched, nothing drawn from / released to a
        // number sequence, no readiness gate (a received invoice is not issued by us).
        (await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId)).DocumentNumber.ShouldBe("SUP-77");
        sequences.ReceivedCalls().ShouldBeEmpty();
        readiness.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task UnifiedUpdate_GivenReceivedId_AppliesTheReceivedRules()
    {
        var dto = await _invoices.UpdateInvoiceAsync(_receivedId, new UpdateInvoiceDto
        {
            DocumentNumber = "SUP-78", ReceivedDate = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), Notes = "n"
        });

        dto!.DocumentNumber.ShouldBe("SUP-78");
        dto.ReceivedDate.ShouldBe(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc));
        dto.Notes.ShouldBe("n");
    }

    [Fact]
    public async Task UnifiedBulk_AcceptsIdsOfAnyType_AndAppliesThePerTypePolicy()
    {
        var result = await _invoices.BulkMarkAsPaidAsync([_issuedId, _receivedId]);

        result.SuccessCount.ShouldBe(2);
        result.FailedCount.ShouldBe(0);
        (await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId)).Status.ShouldBe(EInvoiceStatus.Paid);
        (await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _issuedId)).Status.ShouldBe(EInvoiceStatus.Paid);

        // Bulk delete of mixed ids: the Paid ones are refused per item (a paid document is never deleted), none is "not found".
        var refused = await _invoices.BulkDeleteAsync([_issuedId, _receivedId]);
        refused.FailedCount.ShouldBe(2);
        refused.Errors.ShouldAllBe(e => !e.Error.Contains("not found"));
    }

    // ─── Lists: explicit scope, issued by default ────────────────────────────

    [Fact]
    public async Task Lists_DefaultToIssuedOnly()
    {
        (await _invoices.GetAllInvoicesAsync()).Select(i => i.Id).ShouldBe([_issuedId]);
        (await _invoices.GetInvoicesPagedAsync(new InvoiceFilterDto())).Items.Select(i => i.Id).ShouldBe([_issuedId]);
    }

    [Fact]
    public async Task Lists_WithReceivedScope_ReturnOnlyReceivedDocuments()
    {
        _seed.FileAttachment.Add(new FileAttachment
        {
            EntityName = "ReceivedInvoice", RecordId = _receivedId, OriginalFileName = "a.pdf", ContentType = "application/pdf",
            BlobPath = "x/a.pdf", FileSizeBytes = 1
        });
        await _seed.SaveChangesAsync();

        var byType = await _invoices.GetAllInvoicesAsync(documentType: EDocumentType.ReceivedInvoice);
        var byFilter = await _invoices.GetInvoicesPagedAsync(new InvoiceFilterDto { DocumentType = EDocumentType.ReceivedInvoice });
        var bySet = await _invoices.GetInvoicesPagedAsync(new InvoiceFilterDto { DocumentTypes = [EDocumentType.ReceivedInvoice] });
        var both = await _invoices.GetInvoicesPagedAsync(
            new InvoiceFilterDto { DocumentTypes = [EDocumentType.Invoice, EDocumentType.ReceivedInvoice] });

        byType.Select(i => i.Id).ShouldBe([_receivedId]);
        byFilter.Items.Select(i => i.Id).ShouldBe([_receivedId]);
        bySet.Items.Select(i => i.Id).ShouldBe([_receivedId]);
        bySet.Items.Single().AttachmentCount.ShouldBe(1);
        both.Items.Select(i => i.Id).OrderBy(x => x).ShouldBe(new[] { _issuedId, _receivedId }.OrderBy(x => x));
    }

    // ─── Issued-only operations given a RECEIVED id ──────────────────────────

    [Fact]
    public async Task IssuedOnlyOperations_GivenReceivedId_Refuse_AndLeaveTheDocumentUntouched()
    {
        (await _invoices.GetCreditNotesForInvoiceAsync(_receivedId)).ShouldBeEmpty();

        await Should.ThrowAsync<InvalidOperationException>(
            () => _invoices.CreateCreditNoteAsync(_receivedId, new CreateInvoiceDto()));
        await Should.ThrowAsync<KeyNotFoundException>(() => _invoices.IssueFinalInvoiceAsync(_receivedId, new IssueFinalInvoiceDto()));
        await Should.ThrowAsync<KeyNotFoundException>(() => _invoices.IssueTaxReceiptForPaidProformaAsync(_receivedId, null, null));
        (await _invoices.GetRemainingAdvanceAsync(_receivedId)).ShouldBe(0m);

        // Issued numbers come from a sequence: the supplier number cannot be edited through an issued update.
        await Should.ThrowAsync<InvalidOperationException>(
            () => _invoices.UpdateInvoiceAsync(_issuedId, new UpdateInvoiceDto { DocumentNumber = "X" }));
        await Should.ThrowAsync<InvalidOperationException>(
            () => _invoices.UpdateInvoiceAsync(_issuedId, new UpdateInvoiceDto { ReceivedDate = DateTime.UtcNow }));

        var row = await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId);
        row.Status.ShouldBe(EInvoiceStatus.Completed);
        row.Notes.ShouldBeNull();
        (await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _issuedId)).DocumentNumber.ShouldBe("2026001");
    }

    [Fact]
    public async Task IssuedExports_GivenReceivedId_ActAsNotFound()
    {
        // The services stay on the issued side; the controller turns a received id into a 400 before they run
        // (IssuedDocumentOnlyAttribute, covered in IssuedDocumentOnlyAttributeTests).
        var isdoc = new IsdocExportService(_db, NullLogger<IsdocExportService>.Instance);
        var ubl = new UblExportService(_db, NullLogger<UblExportService>.Instance);
        var qr = new QrPaymentService(_db, Substitute.For<IPayliboClient>(), NullLogger<QrPaymentService>.Instance);

        await Should.ThrowAsync<KeyNotFoundException>(() => isdoc.ExportInvoiceAsync(_receivedId));
        await Should.ThrowAsync<KeyNotFoundException>(() => ubl.ExportInvoiceAsync(_receivedId));
        await Should.ThrowAsync<KeyNotFoundException>(() => qr.GenerateSindStringAsync(_receivedId));
    }

    // ─── Legacy received facade given an ISSUED id ───────────────────────────

    [Fact]
    public async Task LegacyReceivedOperations_GivenIssuedId_ActAsNotFound()
    {
        (await _received.GetByIdAsync(_issuedId)).ShouldBeNull();
        (await _received.UpdateAsync(_issuedId, new Contracts.Dto.ReceivedInvoice.UpdateReceivedInvoiceDto { Notes = "x" })).ShouldBeNull();
        (await _received.ApproveAsync(_issuedId)).ShouldBeNull();
        (await _received.MarkAsPaidAsync(_issuedId)).ShouldBeNull();
        (await _received.RejectAsync(_issuedId)).ShouldBeNull();
        (await _received.DeleteAsync(_issuedId)).ShouldBeFalse();
        (await _received.MarkAsUnpaidAsync(_issuedId)).ShouldBeNull();
        (await _received.RevertToDraftAsync(_issuedId)).ShouldBeNull();
        (await _received.RestoreAsync(_issuedId)).ShouldBeNull();
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

    /// <summary>
    /// A row migrated from the old table: Paid with PaidAmount = total, although its matches cover less
    /// (the old matcher marked a received invoice Paid on ANY match). Such a row cannot be told apart from a
    /// document that was topped up by hand, so the same rule applies (owner decision, DEVGUIDE §5.6): a single
    /// match cannot be taken out - neither subtracting (1210 - 600 = 610 "paid" with no payment behind it) nor
    /// recomputing (100, the rest silently gone) is right. The payment is cancelled as a whole with mark-unpaid.
    /// </summary>
    [Fact]
    public async Task Unmatch_Received_PaidAboveItsMatches_IsRefused_MarkUnpaidCleansIt()
    {
        var received = await _seed.Invoice.SingleAsync(i => i.Id == _receivedId);
        received.Status = EInvoiceStatus.Paid;
        received.PaidAmount = 1210m;
        received.PaidAt = DateTime.UtcNow;
        var tx2 = new BankTransaction
        {
            BankAccountId = 1, Amount = 100m, CurrencyCode = "CZK", Direction = EPaymentDirection.Outgoing,
            MatchStatus = EMatchStatus.Matched, TransactionDate = DateTime.UtcNow, DeduplicationHash = "h2"
        };
        _seed.BankTransaction.Add(tx2);
        var big = new PaymentMatch { BankTransactionId = _txId, InvoiceId = _receivedId, MatchedAmount = 600m, MatchedAt = DateTime.UtcNow };
        var small = new PaymentMatch { BankTransaction = tx2, InvoiceId = _receivedId, MatchedAmount = 100m, MatchedAt = DateTime.UtcNow };
        _seed.PaymentMatch.AddRange(big, small);
        await _seed.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => _matching.UnmatchAsync(big.Id, null, null));

        var untouched = await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId);
        untouched.PaidAmount.ShouldBe(1210m);
        untouched.Status.ShouldBe(EInvoiceStatus.Paid);
        (await _db.PaymentMatch.AsNoTracking().CountAsync(m => m.InvoiceId == _receivedId)).ShouldBe(2);

        // Cancelling the payment on the document: nothing is paid, never "PartiallyPaid with zero matches".
        await _received.MarkAsUnpaidAsync(_receivedId);

        var unpaid = await _db.Invoice.AsNoTracking().SingleAsync(i => i.Id == _receivedId);
        unpaid.PaidAmount.ShouldBe(0m);
        unpaid.Status.ShouldBe(EInvoiceStatus.Completed);
        unpaid.PaidAt.ShouldBeNull();
        (await _db.PaymentMatch.AsNoTracking().AnyAsync(m => m.InvoiceId == _receivedId)).ShouldBeFalse();
        (await _received.GetByIdAsync(_receivedId))!.Status.ShouldBe(EReceivedInvoiceStatus.Approved);
    }
}
