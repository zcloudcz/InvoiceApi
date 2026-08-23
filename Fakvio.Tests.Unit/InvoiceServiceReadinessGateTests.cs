using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the readiness gate in <see cref="InvoiceService.CompleteInvoiceAsync"/> (issue #206).
///
/// A document may only be issued when the tenant has its mandatory settings filled in
/// (issuer address, IČO/DIČ, bank account, number sequence). The rules themselves live in
/// <see cref="ITenantReadinessService"/> and are tested in <c>TenantReadinessServiceTests</c>;
/// here we only verify the gate itself:
///   - it asks about THIS invoice's issuer and document type,
///   - a blocking answer stops the completion before anything is written,
///   - a clean answer leaves the previous behaviour untouched,
///   - the bulk path reports the refusal per invoice instead of aborting the batch.
///
/// The readiness service is substituted (not exercised for real) so a failure here can only
/// mean the gate is wrong — never that a readiness rule changed.
/// </summary>
public class InvoiceServiceReadinessGateTests : IDisposable
{
    private const long CustomerId = 1;
    private const long IssuerId = 2;
    private const long OtherIssuerId = 3;
    private const long CurrencyId = 1;

    private readonly TenantDbContext _context;
    private readonly ITenantReadinessService _readiness = Substitute.For<ITenantReadinessService>();
    private readonly InvoiceService _service;

    public InvoiceServiceReadinessGateTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);

        var numberSequence = Substitute.For<INumberSequenceService>();
        numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("TEST001");

        _service = new InvoiceService(
            _context, numberSequence, _readiness, Substitute.For<ILogger<InvoiceService>>());

        SeedReferenceData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // =========================================================================
    // The gate blocks
    // =========================================================================

    /// <summary>
    /// A blocking readiness issue must abort the completion — and abort it before any state
    /// is written. An invoice left half-issued (status changed, number drawn) would burn a
    /// number from the sequence for a document the user cannot send.
    /// </summary>
    [Fact]
    public async Task CompleteInvoiceAsync_TenantNotReady_ThrowsAndLeavesTheInvoiceUntouched()
    {
        var invoiceId = AddInvoice();
        RefuseReadiness(ReadinessCodes.IssuerBankAccountMissing, nameof(Client.BankAccount));

        var ex = await Should.ThrowAsync<TenantNotReadyException>(
            () => _service.CompleteInvoiceAsync(invoiceId));

        ex.Code.ShouldBe(TenantNotReadyException.ErrorCode);
        ex.MissingFields.ShouldBe([nameof(Client.BankAccount)]);

        // Nothing was persisted — the invoice is still an unnumbered draft.
        var stored = await _context.Invoice.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        stored.Status.ShouldBe(EInvoiceStatus.Draft);
        stored.DocumentNumber.ShouldBe("DRAFT");
    }

    /// <summary>
    /// The approved rule is "check the issuer of THIS invoice", not "check every issuer of
    /// the tenant" — and likewise only the document type being issued. A tenant that never
    /// configured credit notes must still be able to issue invoices.
    /// </summary>
    [Fact]
    public async Task CompleteInvoiceAsync_ChecksOnlyTheInvoicesOwnIssuerAndDocumentType()
    {
        var invoiceId = AddInvoice(issuerId: OtherIssuerId, documentType: EDocumentType.CreditNote);

        await _service.CompleteInvoiceAsync(invoiceId);

        await _readiness.Received(1).EnsureReadyAsync(
            OtherIssuerId, EDocumentType.CreditNote, Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // The gate lets everything else through unchanged
    // =========================================================================

    [Fact]
    public async Task CompleteInvoiceAsync_ReadyTenant_CompletesAsBefore()
    {
        var invoiceId = AddInvoice();

        var result = await _service.CompleteInvoiceAsync(invoiceId);

        result.ShouldNotBeNull();
        result.Status.ShouldBe(EInvoiceStatus.Completed);
        result.DocumentNumber.ShouldBe("TEST001");
    }

    /// <summary>
    /// Guard order: a non-existent invoice has no issuer to check, so the gate must not run
    /// (and the caller must still get the plain "not found" null it always got).
    /// </summary>
    [Fact]
    public async Task CompleteInvoiceAsync_UnknownInvoice_ReturnsNullWithoutCheckingReadiness()
    {
        var result = await _service.CompleteInvoiceAsync(invoiceId: 999_999);

        result.ShouldBeNull();
        await _readiness.DidNotReceive().EnsureReadyAsync(
            Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An already-completed invoice keeps failing with its original message — the readiness
    /// gate must not mask the more specific status error.
    /// </summary>
    [Fact]
    public async Task CompleteInvoiceAsync_AlreadyCompleted_StillReportsTheStatusError()
    {
        var invoiceId = AddInvoice(status: EInvoiceStatus.Completed);
        RefuseReadiness(ReadinessCodes.IssuerBankAccountMissing, nameof(Client.BankAccount));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CompleteInvoiceAsync(invoiceId));

        ex.Message.ShouldContain("already");
    }

    // =========================================================================
    // Bulk path
    // =========================================================================

    /// <summary>
    /// Bulk completion processes invoices one by one. A refused invoice must be reported as a
    /// failure with the reason attached, while the rest of the batch still goes through.
    /// </summary>
    [Fact]
    public async Task BulkCompleteAsync_TenantNotReady_ReportsTheRefusalPerInvoice()
    {
        var blockedId = AddInvoice(issuerId: OtherIssuerId);
        var allowedId = AddInvoice(issuerId: IssuerId);

        // Only the invoice of the unready issuer is refused.
        _readiness
            .EnsureReadyAsync(OtherIssuerId, Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(NotReady(ReadinessCodes.IssuerAddressIncomplete, nameof(Address.Street)));

        var result = await _service.BulkCompleteAsync([blockedId, allowedId]);

        result.SuccessCount.ShouldBe(1);
        result.FailedCount.ShouldBe(1);
        result.Errors.ShouldHaveSingleItem().InvoiceId.ShouldBe(blockedId);
        result.Errors[0].Error.ShouldContain(ReadinessCodes.IssuerAddressIncomplete);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>Makes the readiness service refuse every check with one blocking issue.</summary>
    private void RefuseReadiness(string code, string missingField)
        => _readiness
            .EnsureReadyAsync(Arg.Any<long?>(), Arg.Any<EDocumentType?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(NotReady(code, missingField));

    private static TenantNotReadyException NotReady(string code, string missingField)
        => new([new ReadinessIssueDto
        {
            Code = code,
            Severity = EReadinessSeverity.Blocking,
            MissingFields = [missingField]
        }]);

    /// <summary>Seeds one draft invoice and returns its ID.</summary>
    private long AddInvoice(
        long issuerId = IssuerId,
        EDocumentType documentType = EDocumentType.Invoice,
        EInvoiceStatus status = EInvoiceStatus.Draft)
    {
        var invoice = new Invoice
        {
            DocumentType = documentType,
            Status = status,
            DocumentNumber = "DRAFT",
            ClientId = CustomerId,
            IssuerId = issuerId,
            CurrencyId = CurrencyId,
            IssueDate = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(14),
            PaymentMethod = EPaymentMethod.BankTransfer
        };

        _context.Invoice.Add(invoice);
        _context.SaveChanges();

        return invoice.Id;
    }

    /// <summary>
    /// Two issuers so the tests can prove the gate asks about the right one, plus the
    /// customer and the currency every invoice needs as a foreign key.
    /// </summary>
    private void SeedReferenceData()
    {
        _context.Client.Add(new Client
        {
            Id = CustomerId, CompanyName = "Test Customer",
            RegistrationNumber = "CUST-001", IsIssuer = false, IsActive = true
        });
        _context.Client.Add(new Client
        {
            Id = IssuerId, CompanyName = "Test Issuer Ltd.",
            RegistrationNumber = "ISS-001", IsIssuer = true, IsActive = true, IsVatPayer = false
        });
        _context.Client.Add(new Client
        {
            Id = OtherIssuerId, CompanyName = "Second Issuer Ltd.",
            RegistrationNumber = "ISS-002", IsIssuer = true, IsActive = true, IsVatPayer = false
        });
        _context.Currency.Add(new Currency
        {
            Id = CurrencyId, Code = "CZK", Name = "Czech Koruna", Symbol = "Kc",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.SaveChanges();
    }
}
