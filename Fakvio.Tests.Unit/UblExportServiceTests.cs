using System.Text;
using System.Xml.Linq;
using Fakvio.Application.Exceptions;
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
/// Unit tests for <c>UblExportService</c> (ADR 0002, F1.5) — the DB-backed loading, pre-flight
/// gate and BillingReference resolution wrapped around the pure <c>UblMapper</c>/<c>UblPreflight</c>
/// (covered separately in <c>UblMapperTests</c> and <c>UblPreflightTests</c>). Uses an InMemory
/// TenantDbContext, same pattern as <see cref="IsdocExportServiceTests"/>.
/// </summary>
public class UblExportServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ILogger<UblExportService> _logger;
    private readonly UblExportService _service;

    public UblExportServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<UblExportService>>();
        _service = new UblExportService(_context, _logger);

        SeedParties();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void SeedParties()
    {
        _context.Currency.Add(new Currency { Id = 1, Code = "EUR", Symbol = "EUR", Name = "Euro", IsActive = true });

        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Fakvio s.r.o.",
            RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
            IsVatPayer = true, IsIssuer = true, IsActive = true,
            Address = [new Address { Street = "Narodni 1", City = "Praha", PostalCode = "11000", Country = "CZ", IsPrimary = true }]
        });
        _context.Client.Add(new Client
        {
            Id = 2, CompanyName = "Odberatel s.r.o.",
            RegistrationNumber = "55667788", TaxNumber = "SK2020123456",
            IsVatPayer = true, IsIssuer = false, IsActive = true,
            Address = [new Address { Street = "Hlavna 2", City = "Bratislava", PostalCode = "81102", Country = "SK", IsPrimary = true }]
        });
        _context.SaveChanges();
    }

    // --------------------------------------------------------------------------
    // Happy path
    // --------------------------------------------------------------------------

    [Fact]
    public async Task ExportInvoiceAsync_ReadyInvoice_ReturnsUblBytes()
    {
        var invoice = NewInvoice(1, "INV2026001", EDocumentType.Invoice, EInvoiceStatus.Completed);
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        var bytes = await _service.ExportInvoiceAsync(1);

        var xml = Encoding.UTF8.GetString(bytes);
        xml.ShouldContain("<Invoice");
        xml.ShouldContain("INV2026001");
    }

    [Fact]
    public async Task ExportInvoiceAsync_UnknownId_ThrowsKeyNotFound()
    {
        await Should.ThrowAsync<KeyNotFoundException>(() => _service.ExportInvoiceAsync(999));
    }

    // --------------------------------------------------------------------------
    // Pre-flight
    // --------------------------------------------------------------------------

    [Fact]
    public async Task ExportInvoiceAsync_DraftInvoice_ThrowsTenantNotReady_WithEinvoiceDraftCode()
    {
        var invoice = NewInvoice(2, "INV2026002", EDocumentType.Invoice, EInvoiceStatus.Draft);
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        var ex = await Should.ThrowAsync<TenantNotReadyException>(() => _service.ExportInvoiceAsync(2));

        ex.Issues.ShouldContain(i => i.Code == "EINVOICE_DRAFT");
    }

    // --------------------------------------------------------------------------
    // BillingReference resolution
    // --------------------------------------------------------------------------

    [Fact]
    public async Task ExportInvoiceAsync_CreditNote_EmitsBillingReferenceToOriginalInvoice()
    {
        var original = NewInvoice(3, "INV2026003", EDocumentType.Invoice, EInvoiceStatus.Completed);
        _context.Invoice.Add(original);
        await _context.SaveChangesAsync();

        var creditNote = NewInvoice(4, "CN2026001", EDocumentType.CreditNote, EInvoiceStatus.Completed);
        creditNote.OriginalInvoiceId = 3;
        creditNote.TotalWithVat = -original.TotalWithVat;
        _context.Invoice.Add(creditNote);
        await _context.SaveChangesAsync();

        var bytes = await _service.ExportInvoiceAsync(4);

        var document = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        var cac = (XNamespace)"urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        var cbc = (XNamespace)"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        document.Root!.Element(cac + "BillingReference")!
            .Element(cac + "InvoiceDocumentReference")!.Element(cbc + "ID")!.Value.ShouldBe("INV2026003");
    }

    [Fact]
    public async Task ExportInvoiceAsync_FinalInvoiceClosingProforma_ReferencesTaxReceipts()
    {
        var proforma = NewInvoice(5, "PRO2026001", EDocumentType.Proforma, EInvoiceStatus.Completed);
        _context.Invoice.Add(proforma);
        await _context.SaveChangesAsync();

        var taxReceipt = NewInvoice(6, "TR2026001", EDocumentType.TaxReceiptForAdvance, EInvoiceStatus.Completed);
        taxReceipt.OriginalInvoiceId = 5;
        _context.Invoice.Add(taxReceipt);

        var finalInvoice = NewInvoice(7, "INV2026007", EDocumentType.Invoice, EInvoiceStatus.Completed);
        finalInvoice.OriginalInvoiceId = 5;
        _context.Invoice.Add(finalInvoice);
        await _context.SaveChangesAsync();

        var bytes = await _service.ExportInvoiceAsync(7);

        var document = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        var cac = (XNamespace)"urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        var cbc = (XNamespace)"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        document.Root!.Element(cac + "BillingReference")!
            .Element(cac + "InvoiceDocumentReference")!.Element(cbc + "ID")!.Value.ShouldBe("TR2026001");
    }

    [Fact]
    public async Task ExportInvoiceAsync_FinalInvoiceClosingProforma_ExcludesDraftTaxReceipts()
    {
        // Codex review round 2: a draft tax receipt is not "already issued" yet, so it must not
        // appear in the final invoice's BillingReference even though it references the same
        // pro-forma and is not soft-deleted.
        var proforma = NewInvoice(15, "PRO2026002", EDocumentType.Proforma, EInvoiceStatus.Completed);
        _context.Invoice.Add(proforma);
        await _context.SaveChangesAsync();

        var draftTaxReceipt = NewInvoice(16, "TR2026002-DRAFT", EDocumentType.TaxReceiptForAdvance, EInvoiceStatus.Draft);
        draftTaxReceipt.OriginalInvoiceId = 15;
        _context.Invoice.Add(draftTaxReceipt);

        var issuedTaxReceipt = NewInvoice(17, "TR2026002", EDocumentType.TaxReceiptForAdvance, EInvoiceStatus.Completed);
        issuedTaxReceipt.OriginalInvoiceId = 15;
        _context.Invoice.Add(issuedTaxReceipt);

        var finalInvoice = NewInvoice(18, "INV2026018", EDocumentType.Invoice, EInvoiceStatus.Completed);
        finalInvoice.OriginalInvoiceId = 15;
        _context.Invoice.Add(finalInvoice);
        await _context.SaveChangesAsync();

        var bytes = await _service.ExportInvoiceAsync(18);

        var document = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        var cac = (XNamespace)"urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        var cbc = (XNamespace)"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        var referencedIds = document.Root!.Elements(cac + "BillingReference")
            .Select(r => r.Element(cac + "InvoiceDocumentReference")!.Element(cbc + "ID")!.Value)
            .ToList();
        referencedIds.ShouldBe(["TR2026002"]);
    }

    // --------------------------------------------------------------------------
    // VAT total cross-check (warning only, never blocks the export)
    // --------------------------------------------------------------------------

    [Fact]
    public async Task ExportInvoiceAsync_VatMismatch_LogsWarning_ButStillReturnsBytes()
    {
        var invoice = NewInvoice(8, "INV2026008", EDocumentType.Invoice, EInvoiceStatus.Completed);
        // Stored TotalVat deliberately does not match 21% of 100 (= 21) — simulates a rounding
        // drift between what was saved and what the mapper recomputes from the lines.
        invoice.TotalVat = 999m;
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        var bytes = await _service.ExportInvoiceAsync(8);

        bytes.ShouldNotBeEmpty();
        LoggedWarnings().ShouldContain(m => m.Contains("recomputed VAT total"));
    }

    [Fact]
    public async Task ExportInvoiceAsync_MatchingVatTotal_LogsNoDiscrepancyWarning()
    {
        var invoice = NewInvoice(9, "INV2026009", EDocumentType.Invoice, EInvoiceStatus.Completed);
        invoice.TotalVat = 21m; // exactly 21% of the 100 base -- matches the mapper's own computation

        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        await _service.ExportInvoiceAsync(9);

        LoggedWarnings().ShouldNotContain(m => m.Contains("recomputed VAT total"));
    }

    private IEnumerable<string> LoggedWarnings() =>
        _logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Where(call => (LogLevel)call.GetArguments()[0]! == LogLevel.Warning)
            .Select(call => call.GetArguments()[2]!.ToString()!);

    // --------------------------------------------------------------------------
    // Test data builder
    // --------------------------------------------------------------------------

    private static Invoice NewInvoice(long id, string documentNumber, EDocumentType type, EInvoiceStatus status) => new()
    {
        Id = id,
        DocumentType = type,
        Status = status,
        DocumentNumber = documentNumber,
        IssueDate = new DateTime(2026, 3, 1),
        DueDate = type == EDocumentType.CreditNote ? null : new DateTime(2026, 3, 15),
        IssuerId = 1,
        ClientId = 2,
        CurrencyId = 1,
        PaymentMethod = EPaymentMethod.BankTransfer,
        IBAN = "SK8975000000000012345671",
        VariableSymbol = documentNumber,
        TotalBeforeVat = 100m,
        TotalVat = 21m,
        TotalWithVat = 121m,
        InvoiceItem =
        [
            new InvoiceItem
            {
                OrderIndex = 1, Description = "Service", Quantity = 1, Unit = "ks",
                UnitPrice = 100m, VatRatePercentage = 21m, VatRegime = EVatRegime.Standard,
                TotalBeforeVat = 100m, VatAmount = 21m, TotalWithVat = 121m
            }
        ]
    };
}
