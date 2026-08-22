using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Application.Service;
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
/// Unit tests for the two-phase document number generation logic inside
/// InvoiceService.GenerateDocumentNumberAsync.
///
/// Phase 1 — Sequence resolution:
///   overrideSequenceId (template) > client.BillingSettings.Custom*SequenceId > default.
///
/// Phase 2 — Prefix/suffix resolution:
///   Always taken from client.BillingSettings (when present), regardless of which
///   sequence was selected in Phase 1. This was the original bug: prefix/suffix
///   were only applied in the "else" branch, so any template override silently
///   discarded the client label.
/// </summary>
public class GenerateDocumentNumberTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly INumberSequenceService _numberSequence;

    // IDs used across tests — kept as constants so seeds and assertions stay in sync.
    private const long CustomerId = 10;
    private const long IssuerId = 20;
    private const long CurrencyId = 1;
    private const long TemplateSequenceId = 99;
    private const long ClientCustomSequenceId = 88;

    public GenerateDocumentNumberTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _numberSequence = Substitute.For<INumberSequenceService>();

        // Default: GenerateNextNumberForDocumentTypeAsync returns "INV2026001"
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("INV2026001");

        // Default: GenerateNextNumberAsync (named sequence) returns "SEQ2026001"
        _numberSequence
            .GenerateNextNumberAsync(
                Arg.Any<long>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns("SEQ2026001");

        _service = new InvoiceService(_context, _numberSequence, Substitute.For<ILogger<InvoiceService>>());

        SeedSharedEntities();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ── Seed helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the minimum set of reference entities needed for invoice creation:
    /// one customer, one issuer, one currency.
    /// BillingSettings are added per-test via AddBillingSettings().
    /// </summary>
    private void SeedSharedEntities()
    {
        _context.Client.Add(new Client
        {
            Id = CustomerId,
            CompanyName = "Test Customer",
            RegistrationNumber = "CUST001",
            IsIssuer = false,
            IsActive = true
        });
        _context.SaveChanges();

        _context.Client.Add(new Client
        {
            Id = IssuerId,
            CompanyName = "Test Issuer",
            RegistrationNumber = "ISS001",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = false
        });
        _context.SaveChanges();

        _context.Currency.Add(new Currency
        {
            Id = CurrencyId,
            Code = "CZK",
            Name = "Czech Koruna",
            Symbol = "Kc",
            DecimalPlaces = 2,
            SortOrder = 1,
            IsActive = true
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Adds BillingSettings to the seeded customer and saves immediately.
    /// </summary>
    private void AddBillingSettings(
        string? invoicePrefix = null,
        string? invoiceSuffix = null,
        string? creditNotePrefix = null,
        string? creditNoteSuffix = null,
        long? customInvoiceSequenceId = null)
    {
        _context.Set<BillingSettings>().Add(new BillingSettings
        {
            ClientId = CustomerId,
            InvoiceNumberPrefix = invoicePrefix,
            InvoiceNumberSuffix = invoiceSuffix,
            CreditNoteNumberPrefix = creditNotePrefix,
            CreditNoteNumberSuffix = creditNoteSuffix,
            CustomInvoiceNumberSequenceId = customInvoiceSequenceId
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Builds a minimal CreateInvoiceDto for the seeded customer/issuer/currency.
    /// </summary>
    private CreateInvoiceDto MakeInvoiceDto(
        EDocumentType documentType = EDocumentType.Invoice,
        long? numberSequenceId = null)
    {
        return new CreateInvoiceDto
        {
            DocumentType = documentType,
            ClientId = CustomerId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            NumberSequenceId = numberSequenceId,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Service",
                    Quantity = 1,
                    Unit = "pcs",
                    UnitPrice = 1000
                }
            }
        };
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// REGRESSION: When a template specifies NumberSequenceId (overrideSequenceId),
    /// the client's InvoiceNumberPrefix must still be applied to the generated number.
    /// Before the fix this test would fail because prefix was only set in the "else" branch.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_FromTemplate_ShouldApplyClientPrefix()
    {
        // Arrange — client has "EU-" prefix; template forces a specific sequence
        AddBillingSettings(invoicePrefix: "EU-");

        _numberSequence
            .GenerateNextNumberAsync(TemplateSequenceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("INV2026042");

        var dto = MakeInvoiceDto(numberSequenceId: TemplateSequenceId);

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — bare number "INV2026042" must be wrapped with the client prefix
        result.DocumentNumber.ShouldBe("EU-INV2026042");
        result.DocumentNumber.ShouldStartWith("EU-");
    }

    /// <summary>
    /// Analogous to the prefix test — suffix must be applied even when a template
    /// sequence override is in effect.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_FromTemplate_ShouldApplyClientSuffix()
    {
        // Arrange — client has "-CZ" suffix; template forces a specific sequence
        AddBillingSettings(invoiceSuffix: "-CZ");

        _numberSequence
            .GenerateNextNumberAsync(TemplateSequenceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("INV2026001");

        var dto = MakeInvoiceDto(numberSequenceId: TemplateSequenceId);

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert
        result.DocumentNumber.ShouldBe("INV2026001-CZ");
        result.DocumentNumber.ShouldEndWith("-CZ");
    }

    /// <summary>
    /// REGRESSION: When no template override is used (standard client invoice creation),
    /// the client prefix must still be applied.  This was already working before the fix;
    /// this test guards against future regressions.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_NoTemplate_ClientPrefixStillApplied()
    {
        // Arrange — client has "EU-" prefix; no template sequence override
        AddBillingSettings(invoicePrefix: "EU-");

        // GenerateNextNumberForDocumentTypeAsync receives the prefix as a parameter;
        // here we simulate the sequence service embedding it in the result.
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                EDocumentType.Invoice, Arg.Any<DateTime>(),
                "EU-", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("EU-INV2026001");

        var dto = MakeInvoiceDto(); // no NumberSequenceId

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — prefix is forwarded to the default-sequence call
        result.DocumentNumber.ShouldBe("EU-INV2026001");
    }

    /// <summary>
    /// CreditNote variant: when a template sequence override is in effect,
    /// the client's CreditNoteNumberPrefix must still be applied.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_CreditNoteFromTemplate_ShouldApplyClientCreditNotePrefix()
    {
        // Arrange — seed an already-completed invoice directly in the DB so we have a valid
        // OriginalInvoiceId without going through CreateInvoiceAsync / CompleteInvoiceAsync
        // (avoids document-number mock conflicts that would create VS collision).
        var originalInvoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            ClientId = CustomerId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            DocumentNumber = "BASE-INV-001",
            VariableSymbol = "1111111111",
            IssueDate = DateTime.UtcNow.Date,
            TaxableSupplyDate = DateTime.UtcNow.Date,
            DueDate = DateTime.UtcNow.Date.AddDays(14),
            InvoiceItem = new List<InvoiceItem>()
        };
        _context.Invoice.Add(originalInvoice);
        await _context.SaveChangesAsync();

        // Client has "RET-" credit-note prefix; template forces a specific sequence
        AddBillingSettings(creditNotePrefix: "RET-");

        // Credit-note sequence produces a number whose digits don't collide with any seeded VS
        _numberSequence
            .GenerateNextNumberAsync(TemplateSequenceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("CN20260099");

        var creditNoteDto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.CreditNote,
            ClientId = CustomerId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            OriginalInvoiceId = originalInvoice.Id,
            NumberSequenceId = TemplateSequenceId,
            // Digits from "RET-CN20260099" would give "20260099" — unique in this DB.
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Refund",
                    Quantity = 1,
                    Unit = "pcs",
                    UnitPrice = 1000
                }
            }
        };

        // Act
        var result = await _service.CreateInvoiceAsync(creditNoteDto);

        // Assert — bare number "CN20260099" wrapped with credit-note prefix
        result.DocumentNumber.ShouldBe("RET-CN20260099");
        result.DocumentNumber.ShouldStartWith("RET-");
    }

    /// <summary>
    /// When the client has no BillingSettings at all, no prefix/suffix is applied
    /// and the sequence from the template override is used as-is.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_NoBillingSettings_ShouldFallBackToTemplateSequence()
    {
        // Arrange — no BillingSettings seeded for this customer
        _numberSequence
            .GenerateNextNumberAsync(TemplateSequenceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("INV2026999");

        var dto = MakeInvoiceDto(numberSequenceId: TemplateSequenceId);

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — bare number used, no wrapping
        result.DocumentNumber.ShouldBe("INV2026999");
    }

    /// <summary>
    /// Edge case: both prefix AND suffix are configured when a template sequence override
    /// is in effect. Both must be applied exactly once (no double-application).
    ///
    /// The implementation has a code comment warning that GenerateNextNumberForDocumentTypeAsync
    /// (default-sequence path) already embeds prefix/suffix and returns early to avoid this
    /// double-application. The named-sequence path applies them manually after the call.
    /// This test verifies the named-sequence path wraps correctly.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_FromTemplate_ShouldApplyBothPrefixAndSuffix()
    {
        // Arrange — client has prefix and suffix; template forces a specific sequence
        AddBillingSettings(invoicePrefix: "EU-", invoiceSuffix: "-CZ");

        _numberSequence
            .GenerateNextNumberAsync(TemplateSequenceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("INV2026042");

        var dto = MakeInvoiceDto(numberSequenceId: TemplateSequenceId);

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — bare number wrapped with both prefix and suffix, applied once
        result.DocumentNumber.ShouldBe("EU-INV2026042-CZ");
        result.DocumentNumber.ShouldStartWith("EU-");
        result.DocumentNumber.ShouldEndWith("-CZ");
        // Guard against double-application: "EU-EU-..." or "...-CZ-CZ" would fail
        result.DocumentNumber.ShouldNotContain("EU-EU-");
        result.DocumentNumber.ShouldNotContain("-CZ-CZ");
    }

    /// <summary>
    /// Edge case: client has a custom sequence in BillingSettings (Phase 1 uses client
    /// custom sequence, not template override) AND an invoice prefix (Phase 2).
    /// Both phases must operate independently — prefix from Phase 2 must still be applied
    /// even when Phase 1 resolves to the client's own custom sequence (not template override).
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_ClientCustomSequence_ShouldApplyClientPrefix()
    {
        // Arrange — client has a custom sequence AND a "VIP-" prefix; no template override
        AddBillingSettings(invoicePrefix: "VIP-", customInvoiceSequenceId: ClientCustomSequenceId);

        _numberSequence
            .GenerateNextNumberAsync(ClientCustomSequenceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("VIP2026001");

        var dto = MakeInvoiceDto(); // no template NumberSequenceId → client custom sequence used

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — client sequence was used (VIP2026001) AND prefix applied
        result.DocumentNumber.ShouldBe("VIP-VIP2026001");
        result.DocumentNumber.ShouldStartWith("VIP-");
    }

    /// <summary>
    /// Boundary: prefix/suffix are empty strings (not null). Empty strings must NOT
    /// be applied (the implementation guards with IsNullOrEmpty). This test ensures
    /// the guard works so the document number stays clean.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_EmptyPrefixSuffix_ShouldNotWrapNumber()
    {
        // Arrange — BillingSettings present but prefix/suffix are empty strings
        AddBillingSettings(invoicePrefix: "", invoiceSuffix: "");

        _numberSequence
            .GenerateNextNumberAsync(TemplateSequenceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("INV2026001");

        var dto = MakeInvoiceDto(numberSequenceId: TemplateSequenceId);

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — number stays as-is (IsNullOrEmpty guard prevents wrapping with "")
        result.DocumentNumber.ShouldBe("INV2026001");
    }

    // ── Issue #155: no silent fallback when the sequence cannot produce a number ──
    //
    // Historically GenerateDocumentNumberAsync caught InvalidOperationException from
    // INumberSequenceService and returned a hardcoded "INV{year}{count+1}" number
    // (e.g. "INV2026001"), logging only a warning. The user then received a document
    // whose number matched neither the configured sequence, prefix nor format — and
    // was never told. For accounting documents that is a serious defect: the series
    // must stay continuous and predictable.
    //
    // Expected behaviour now: the failure surfaces as an InvalidOperationException
    // whose message tells the user WHAT is wrong and WHERE to fix it, with the
    // original sequence error preserved as InnerException for the log.

    /// <summary>
    /// Missing series: no default sequence exists for the document type, so the sequence
    /// service throws. Creation must fail loudly instead of inventing "INV2026001".
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_NoDefaultSequence_ShouldThrowInsteadOfFallback()
    {
        // Arrange — default-sequence path throws (this is what NumberSequenceService
        // does when no active default sequence exists for the document type)
        var sequenceError = new InvalidOperationException(
            "No default number sequence configured for Invoice");

        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(sequenceError);

        var dto = MakeInvoiceDto();

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CreateInvoiceAsync(dto));

        // Assert — actionable message + original cause preserved
        ex.Message.ShouldContain("number sequence",
            customMessage: "The error must name the number sequence as the cause");
        ex.Message.ShouldContain("/number-sequences",
            customMessage: "The error must tell the user where to configure the sequence");
        ex.InnerException.ShouldBe(sequenceError);

        // Assert — no invoice may receive a fallback number that ignores the configured
        // series. Since issue #181 the failed attempt is not persisted at all, which is the
        // strongest form of that guarantee: there is no row left to carry a wrong number.
        var invoices = await _context.Invoice.ToListAsync();
        invoices.ShouldBeEmpty();
    }

    /// <summary>
    /// Inactive series: the sequence exists but is deactivated, so the sequence service
    /// throws. Same rule — fail loudly, never substitute a made-up number.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_InactiveSequence_ShouldThrowInsteadOfFallback()
    {
        // Arrange — template forces a sequence that is not active
        var sequenceError = new InvalidOperationException(
            "Number sequence Export invoices is not active");

        _numberSequence
            .GenerateNextNumberAsync(TemplateSequenceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(sequenceError);

        var dto = MakeInvoiceDto(numberSequenceId: TemplateSequenceId);

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CreateInvoiceAsync(dto));

        // Assert
        ex.InnerException.ShouldBe(sequenceError);
        ex.Message.ShouldNotContain("INV2026",
            customMessage: "The error must not hand out a substitute document number");

        // Issue #181 — the failed attempt leaves no row behind.
        var invoices = await _context.Invoice.ToListAsync();
        invoices.ShouldBeEmpty();
    }

    /// <summary>
    /// Number collision: concurrent requests exhausted the optimistic-concurrency retries
    /// inside NumberSequenceService. The old fallback turned that transient conflict into
    /// a permanently wrong number; now the caller sees the failure and can retry.
    ///
    /// The mocked exception mirrors EXACTLY what the real service throws on this path —
    /// an InvalidOperationException wrapping the last DbUpdateConcurrencyException. That
    /// shape is pinned on the producing side by
    /// NumberSequenceServiceTests.GenerateNextNumberAsync_WhenEveryAttemptConflicts_…,
    /// so the two tests together cover the whole chain instead of agreeing on a fiction.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_ConcurrencyCollision_ShouldThrowInsteadOfFallback()
    {
        // Arrange — client custom sequence path exhausts its retries
        AddBillingSettings(customInvoiceSequenceId: ClientCustomSequenceId);

        var sequenceError = new InvalidOperationException(
            $"Failed to generate document number for sequence {ClientCustomSequenceId} " +
            "after 3 retries due to concurrency conflicts.",
            new DbUpdateConcurrencyException("Simulated optimistic concurrency conflict."));

        _numberSequence
            .GenerateNextNumberAsync(ClientCustomSequenceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(sequenceError);

        var dto = MakeInvoiceDto();

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CreateInvoiceAsync(dto));

        // Assert — original cause preserved for the log
        ex.InnerException.ShouldBe(sequenceError);

        // Assert — a transient collision must advise a retry, NOT send the user to
        // /number-sequences: nothing is misconfigured there, so that advice would mislead.
        ex.Message.ShouldContain("repeat the action",
            customMessage: "A transient collision must tell the user to simply try again");
        ex.Message.ShouldNotContain("/number-sequences",
            customMessage: "A transient collision is not a configuration problem");

        // Issue #181 — "repeat the action" is only honest advice when the failed attempt
        // left nothing behind that the repeated attempt could collide with.
        var invoices = await _context.Invoice.ToListAsync();
        invoices.ShouldBeEmpty();
    }

    /// <summary>
    /// Boundary between the two catch blocks: the transient branch is selected by the TYPE of
    /// the inner exception (DbUpdateConcurrencyException), not by the mere presence of one.
    ///
    /// A configuration failure often carries an inner exception too — the sequence service
    /// wraps whatever EF Core threw while loading the series. Such a failure must still be
    /// reported as a configuration problem and send the user to /number-sequences; telling him
    /// to "repeat the action" would send him in circles, because repeating cannot fix a missing
    /// series.
    /// </summary>
    [Fact]
    public async Task GenerateDocumentNumber_ConfigurationErrorWithUnrelatedInnerException_KeepsConfigurationAdvice()
    {
        // Arrange — configuration failure carrying an inner exception that is NOT a concurrency conflict
        var sequenceError = new InvalidOperationException(
            "No default number sequence configured for Invoice",
            new TimeoutException("Reading the sequence timed out"));

        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(sequenceError);

        var dto = MakeInvoiceDto();

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CreateInvoiceAsync(dto));

        // Assert — configuration advice, not the transient "repeat the action" one
        ex.Message.ShouldContain("/number-sequences",
            customMessage: "Only a DbUpdateConcurrencyException inner may switch to the transient branch");
        ex.Message.ShouldNotContain("repeat the action");
        ex.InnerException.ShouldBe(sequenceError);
    }
}
