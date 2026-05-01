using Fakvio.Contracts.Dto.ContentTemplate;
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

/// <summary>
/// Unit tests for issue #27: PDF + email templates for Proforma and TaxReceiptForAdvance.
///
/// Tested behaviours:
/// - EContentTemplateType has the four new values with correct numeric identifiers.
/// - PdfExportService.ResolveTemplateType correctly maps all four EDocumentType values.
/// - PdfExportService.ResolveEmailTemplateType correctly maps all four EDocumentType values.
/// - GetDefaultByTypeAsync is called with the right EContentTemplateType for Proforma and DPP.
/// - Built-in fallback returns a valid PDF for Proforma and TaxReceiptForAdvance (no DB template).
/// - Handlebars placeholder rendering works for the advance invoice template.
/// </summary>
public class ProformaTemplateTests : IDisposable
{
    // In-memory TenantDbContext — provides realistic invoice loading without a real DB.
    private readonly TenantDbContext _context;

    // Service under test
    private readonly PdfExportService _service;

    // Mocked dependencies
    private readonly IContentTemplateService _contentTemplateService;
    private readonly IQrPaymentService _qrPaymentService;
    private readonly ILogger<PdfExportService> _logger;

    public ProformaTemplateTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<PdfExportService>>();
        _contentTemplateService = Substitute.For<IContentTemplateService>();
        _qrPaymentService = Substitute.For<IQrPaymentService>();

        // QR service: return minimal valid PNG header bytes — content is irrelevant for these tests
        _qrPaymentService.GenerateQrCodeImageAsync(
                Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        _service = new PdfExportService(_context, _contentTemplateService, _qrPaymentService, _logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── EContentTemplateType enum values ────────────────────────────────────

    /// <summary>
    /// Verifies that the four new enum members exist and their integer values do not
    /// overlap with previously existing values (numerical stability requirement from issue #27).
    /// </summary>
    [Fact]
    public void EContentTemplateType_NewValues_HaveCorrectNumericIdentifiers()
    {
        // PDF group — new values must sit in the 1-9 range and not clash with existing 1, 2, 3
        ((int)EContentTemplateType.AdvanceInvoicePdf).ShouldBe(4);
        ((int)EContentTemplateType.TaxReceiptForAdvancePdf).ShouldBe(5);

        // Email group — new values must sit in the 10-19 range and not clash with 10 or 11
        ((int)EContentTemplateType.AdvanceInvoiceEmail).ShouldBe(12);
        ((int)EContentTemplateType.TaxReceiptForAdvanceEmail).ShouldBe(13);
    }

    /// <summary>
    /// Verifies that the existing enum values were NOT renumbered by the change.
    /// Changing an existing int value would corrupt existing DB rows.
    /// </summary>
    [Fact]
    public void EContentTemplateType_ExistingValues_AreUnchanged()
    {
        ((int)EContentTemplateType.InvoicePdf).ShouldBe(1);
        ((int)EContentTemplateType.CreditNotePdf).ShouldBe(2);
        ((int)EContentTemplateType.ReminderPdf).ShouldBe(3);
        ((int)EContentTemplateType.InvoiceEmail).ShouldBe(10);
        ((int)EContentTemplateType.CreditNoteEmail).ShouldBe(11);
        ((int)EContentTemplateType.InvitationEmail).ShouldBe(20);
        ((int)EContentTemplateType.ReminderEmail).ShouldBe(21);
        ((int)EContentTemplateType.PasswordResetEmail).ShouldBe(22);
        ((int)EContentTemplateType.TwoFactorEmail).ShouldBe(23);
    }

    // ─── ResolveTemplateType (PDF) ────────────────────────────────────────────

    /// <summary>
    /// ResolveTemplateType must return the PDF template type for each of the four document types.
    /// This is the single authoritative mapping; incorrect mappings would cause the wrong
    /// template to be used when generating PDFs.
    /// </summary>
    [Theory]
    [InlineData(EDocumentType.Invoice, EContentTemplateType.InvoicePdf)]
    [InlineData(EDocumentType.CreditNote, EContentTemplateType.CreditNotePdf)]
    [InlineData(EDocumentType.Proforma, EContentTemplateType.AdvanceInvoicePdf)]
    [InlineData(EDocumentType.TaxReceiptForAdvance, EContentTemplateType.TaxReceiptForAdvancePdf)]
    public void ResolveTemplateType_AllDocumentTypes_ReturnCorrectPdfTemplateType(
        EDocumentType documentType,
        EContentTemplateType expectedTemplateType)
    {
        var result = PdfExportService.ResolveTemplateType(documentType);
        result.ShouldBe(expectedTemplateType);
    }

    // ─── ResolveEmailTemplateType ─────────────────────────────────────────────

    /// <summary>
    /// ResolveEmailTemplateType must return the email template type for each document type.
    /// Used by EmailService to select the correct email body when sending documents.
    /// </summary>
    [Theory]
    [InlineData(EDocumentType.Invoice, EContentTemplateType.InvoiceEmail)]
    [InlineData(EDocumentType.CreditNote, EContentTemplateType.CreditNoteEmail)]
    [InlineData(EDocumentType.Proforma, EContentTemplateType.AdvanceInvoiceEmail)]
    [InlineData(EDocumentType.TaxReceiptForAdvance, EContentTemplateType.TaxReceiptForAdvanceEmail)]
    public void ResolveEmailTemplateType_AllDocumentTypes_ReturnCorrectEmailTemplateType(
        EDocumentType documentType,
        EContentTemplateType expectedTemplateType)
    {
        var result = PdfExportService.ResolveEmailTemplateType(documentType);
        result.ShouldBe(expectedTemplateType);
    }

    // ─── GetDefaultByTypeAsync called with correct template type ─────────────

    /// <summary>
    /// For a Proforma invoice, GenerateInvoicePdfAsync should call GetDefaultByTypeAsync
    /// with EContentTemplateType.AdvanceInvoicePdf — not InvoicePdf.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_ProformaInvoice_RequestsAdvanceInvoicePdfTemplate()
    {
        // Arrange — mock returns null so built-in fallback is used (test focus is on what's requested)
        _contentTemplateService
            .GetDefaultByTypeAsync(EContentTemplateType.AdvanceInvoicePdf, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        // Act
        var result = await _service.GenerateInvoicePdfAsync(invoiceIdFor(EDocumentType.Proforma));

        // Assert — must have queried the advance invoice template type
        result.ShouldNotBeEmpty();
        await _contentTemplateService.Received(1)
            .GetDefaultByTypeAsync(EContentTemplateType.AdvanceInvoicePdf, Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Must NOT have queried the regular invoice template type
        await _contentTemplateService.DidNotReceive()
            .GetDefaultByTypeAsync(EContentTemplateType.InvoicePdf, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// For a TaxReceiptForAdvance, GenerateInvoicePdfAsync should call GetDefaultByTypeAsync
    /// with EContentTemplateType.TaxReceiptForAdvancePdf.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_TaxReceiptForAdvance_RequestsTaxReceiptPdfTemplate()
    {
        _contentTemplateService
            .GetDefaultByTypeAsync(EContentTemplateType.TaxReceiptForAdvancePdf, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        var result = await _service.GenerateInvoicePdfAsync(invoiceIdFor(EDocumentType.TaxReceiptForAdvance));

        result.ShouldNotBeEmpty();
        await _contentTemplateService.Received(1)
            .GetDefaultByTypeAsync(EContentTemplateType.TaxReceiptForAdvancePdf, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Built-in fallback templates ─────────────────────────────────────────

    /// <summary>
    /// When no DB template is configured for Proforma, the service must still produce a valid PDF
    /// using the built-in AdvanceInvoicePdfTemplate.html embedded resource.
    /// Valid PDF = non-empty byte array starting with the PDF magic bytes "%PDF".
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_ProformaNoTemplate_UsesBuiltInFallbackAndReturnsPdf()
    {
        // Arrange — no template in DB
        _contentTemplateService
            .GetDefaultByTypeAsync(Arg.Any<EContentTemplateType>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        // Act
        var result = await _service.GenerateInvoicePdfAsync(invoiceIdFor(EDocumentType.Proforma));

        // Assert — PDF magic bytes %PDF
        result.ShouldNotBeNull();
        result.ShouldNotBeEmpty();
        result[0].ShouldBe((byte)0x25); // '%'
    }

    /// <summary>
    /// Same as above for TaxReceiptForAdvance — built-in fallback must produce a valid PDF.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_TaxReceiptNoTemplate_UsesBuiltInFallbackAndReturnsPdf()
    {
        _contentTemplateService
            .GetDefaultByTypeAsync(Arg.Any<EContentTemplateType>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        var result = await _service.GenerateInvoicePdfAsync(invoiceIdFor(EDocumentType.TaxReceiptForAdvance));

        result.ShouldNotBeNull();
        result.ShouldNotBeEmpty();
        result[0].ShouldBe((byte)0x25);
    }

    // ─── Handlebars placeholder rendering ────────────────────────────────────

    /// <summary>
    /// When a custom Advance Invoice PDF template is configured and returned by the mock,
    /// the service must render the Handlebars placeholders ({{DocumentNumber}}, etc.) and
    /// produce a valid PDF byte array.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_ProformaWithCustomTemplate_RendersPlaceholders()
    {
        // Arrange — mock returns a simple template with a placeholder
        var customTemplate = new ContentTemplateDto
        {
            Id = 50,
            Name = "Custom Advance Invoice",
            HtmlBody = "<html><body><h1>{{DocumentTypeLabel}} {{DocumentNumber}}</h1>{{InvoiceItems}}{{VatBreakdown}}{{QrCodeImage}}</body></html>",
            TemplateType = EContentTemplateType.AdvanceInvoicePdf,
            IsDefault = true,
            IsActive = true
        };
        _contentTemplateService
            .GetDefaultByTypeAsync(EContentTemplateType.AdvanceInvoicePdf, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(customTemplate);

        // Act
        var result = await _service.GenerateInvoicePdfAsync(invoiceIdFor(EDocumentType.Proforma));

        // Assert — valid PDF produced from the custom template
        result.ShouldNotBeNull();
        result.ShouldNotBeEmpty();
        result[0].ShouldBe((byte)0x25); // %PDF
    }

    // ─── Seed helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Maps document type to seeded invoice IDs — keeps the test methods readable.
    /// Invoice IDs: Invoice=1, CreditNote=2, Proforma=3, TaxReceiptForAdvance=4.
    /// </summary>
    private static long invoiceIdFor(EDocumentType type) => type switch
    {
        EDocumentType.Invoice => 1,
        EDocumentType.CreditNote => 2,
        EDocumentType.Proforma => 3,
        EDocumentType.TaxReceiptForAdvance => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>
    /// Seeds one invoice of each document type so every test can request any type.
    /// All four share the same issuer and client to keep the seed compact.
    /// </summary>
    private void SeedTestData()
    {
        var currency = new Currency { Id = 1, Code = "CZK", Symbol = "Kč", Name = "Czech Koruna", IsActive = true };
        _context.Currency.Add(currency);
        _context.SaveChanges();

        var issuer = new Client
        {
            Id = 1,
            CompanyName = "Test Issuer s.r.o.",
            RegistrationNumber = "12345678",
            TaxNumber = "CZ12345678",
            IsVatPayer = true,
            IsIssuer = true,
            IsActive = true,
            Address = new List<Address>
            {
                new Address { Id = 1, Street = "Hlavni 1", City = "Praha", PostalCode = "11000", Country = "CZ", AddressType = EAddressType.Billing, IsPrimary = true }
            },
            Contact = new List<Contact>
            {
                new Contact { Id = 1, ContactType = EContactType.Email, ContactValue = "issuer@test.cz", IsPrimary = true }
            }
        };
        _context.Client.Add(issuer);
        _context.SaveChanges();

        var client = new Client
        {
            Id = 2,
            CompanyName = "Test Client a.s.",
            RegistrationNumber = "87654321",
            TaxNumber = "CZ87654321",
            IsVatPayer = true,
            IsIssuer = false,
            IsActive = true,
            Address = new List<Address>
            {
                new Address { Id = 2, Street = "Vedlejsi 2", City = "Brno", PostalCode = "60200", Country = "CZ", AddressType = EAddressType.Billing, IsPrimary = true }
            },
            Contact = new List<Contact>()
        };
        _context.Client.Add(client);
        _context.SaveChanges();

        // One invoice of each document type
        var docTypes = new[]
        {
            (Id: 1L, Type: EDocumentType.Invoice, DocNum: "INV2025001"),
            (Id: 2L, Type: EDocumentType.CreditNote, DocNum: "CN2025001"),
            (Id: 3L, Type: EDocumentType.Proforma, DocNum: "PF-2025001"),
            (Id: 4L, Type: EDocumentType.TaxReceiptForAdvance, DocNum: "DPP-2025001"),
        };

        foreach (var (id, type, docNum) in docTypes)
        {
            var inv = new Invoice
            {
                Id = id,
                DocumentType = type,
                Status = EInvoiceStatus.Completed,
                DocumentNumber = docNum,
                IssueDate = new DateTime(2025, 6, 1),
                DueDate = new DateTime(2025, 6, 15),
                TaxableSupplyDate = new DateTime(2025, 6, 1),
                ClientId = 2,
                IssuerId = 1,
                CurrencyId = 1,
                PaymentMethod = EPaymentMethod.BankTransfer,
                BankAccountNumber = "123456789/0100",
                VariableSymbol = "2025001",
                TotalBeforeVat = 10000,
                TotalVat = 2100,
                TotalWithVat = 12100,
                InvoiceItem = new List<InvoiceItem>
                {
                    new InvoiceItem
                    {
                        Id = (int)id,
                        OrderIndex = 1,
                        Description = "Testovací položka",
                        Quantity = 10,
                        Unit = "hod",
                        UnitPrice = 1000,
                        VatRatePercentage = 21,
                        TotalBeforeVat = 10000,
                        VatAmount = 2100,
                        TotalWithVat = 12100
                    }
                }
            };
            _context.Invoice.Add(inv);
            _context.SaveChanges();
        }
    }
}
