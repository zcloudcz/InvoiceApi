using InvoiceApi.Contracts.Dto.ContentTemplate;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Unit tests for PdfExportService.
/// Tests HTML-to-PDF conversion, placeholder replacement, and template lookup.
/// Uses an in-memory database to simulate real invoice data.
///
/// PdfExportService depends on:
/// - TenantDbContext (in-memory database with seeded invoice data)
/// - IContentTemplateService (mocked — controls which template is returned)
/// - ILogger (mocked — just satisfies the dependency)
/// </summary>
public class PdfExportServiceTests : IDisposable
{
    // The in-memory database context with seeded invoice data
    private readonly TenantDbContext _context;

    // The service we are testing
    private readonly PdfExportService _service;

    // Mocked logger — we just need the dependency, no log verification needed
    private readonly ILogger<PdfExportService> _logger;

    // Mocked content template service — controls template resolution behaviour
    private readonly IContentTemplateService _contentTemplateService;

    // Mocked QR payment service — generates QR code images for PDF embedding
    private readonly IQrPaymentService _qrPaymentService;

    public PdfExportServiceTests()
    {
        // Create a fresh in-memory database for each test
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<PdfExportService>>();
        _contentTemplateService = Substitute.For<IContentTemplateService>();
        _qrPaymentService = Substitute.For<IQrPaymentService>();

        // QR service returns a small byte array simulating a PNG image.
        // The actual content doesn't matter for PDF generation tests — we just need
        // the service to return bytes so PdfExportService can base64-encode them.
        _qrPaymentService.GenerateQrCodeImageAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // Create the service under test with real DbContext + mocked dependencies
        _service = new PdfExportService(_context, _contentTemplateService, _qrPaymentService, _logger);

        // Seed the in-memory database with test invoice data
        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds the in-memory database with test data needed for PDF generation:
    /// currencies, clients (issuer + client), and an invoice with items.
    /// This data is required because PdfExportService loads the full invoice
    /// with all related entities before generating the PDF.
    /// </summary>
    private void SeedTestData()
    {
        // Add currency — invoices reference a currency for displaying totals
        var currency = new Currency
        {
            Id = 1,
            Code = "CZK",
            Symbol = "Kc",
            Name = "Czech Koruna",
            IsActive = true
        };
        _context.Currency.Add(currency);

        // Add issuer (your company) — the entity that sends the invoice
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
                new Address
                {
                    Id = 1,
                    Street = "Hlavni 1",
                    City = "Praha",
                    PostalCode = "11000",
                    Country = "Czech Republic",
                    AddressType = EAddressType.Billing,
                    IsPrimary = true
                }
            },
            Contact = new List<Contact>
            {
                new Contact
                {
                    Id = 1,
                    ContactType = EContactType.Email,
                    ContactValue = "issuer@test.com",
                    IsPrimary = true
                }
            }
        };
        _context.Client.Add(issuer);

        // Add client (customer) — the entity that receives the invoice
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
                new Address
                {
                    Id = 2,
                    Street = "Vedlejsi 2",
                    City = "Brno",
                    PostalCode = "60200",
                    Country = "Czech Republic",
                    AddressType = EAddressType.Billing,
                    IsPrimary = true
                }
            },
            Contact = new List<Contact>()
        };
        _context.Client.Add(client);

        // Add an invoice with one line item — this is the document we will generate a PDF for
        var invoice = new Invoice
        {
            Id = 1,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2025001",
            IssueDate = new DateTime(2025, 1, 15),
            DueDate = new DateTime(2025, 1, 29),
            TaxableSupplyDate = new DateTime(2025, 1, 15),
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
                    Id = 1,
                    OrderIndex = 1,
                    Description = "Web development",
                    Quantity = 10,
                    Unit = "hours",
                    UnitPrice = 1000,
                    VatRatePercentage = 21,
                    TotalBeforeVat = 10000,
                    VatAmount = 2100,
                    TotalWithVat = 12100
                }
            }
        };
        _context.Invoice.Add(invoice);
        _context.SaveChanges();
    }

    /// <summary>
    /// Tests that GenerateInvoicePdfAsync returns a valid PDF byte array
    /// for an existing invoice. The mock returns null from GetDefaultByTypeAsync
    /// so the service falls back to its built-in default HTML template.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_ValidInvoice_ReturnsPdfBytes()
    {
        // Arrange — mock returns null so the built-in fallback template is used
        _contentTemplateService
            .GetDefaultByTypeAsync(EContentTemplateType.InvoicePdf, Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        // Act — generate PDF for the test invoice
        var result = await _service.GenerateInvoicePdfAsync(1);

        // Assert — should return non-empty byte array starting with PDF header (%PDF)
        result.ShouldNotBeNull();
        result.ShouldNotBeEmpty();
        result.Length.ShouldBeGreaterThan(0);
        // PDF files always start with %PDF header — the '%' character is 0x25 in ASCII
        result[0].ShouldBe((byte)0x25);
    }

    /// <summary>
    /// Tests that GenerateInvoicePdfAsync throws KeyNotFoundException
    /// when the invoice ID doesn't exist in the database.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_InvalidId_ThrowsKeyNotFoundException()
    {
        // Act & Assert — requesting PDF for non-existent invoice should throw
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.GenerateInvoicePdfAsync(999));
    }

    /// <summary>
    /// Tests that GeneratePdfFromHtmlAsync converts basic HTML to a valid PDF.
    /// This method does not involve template lookup — it just converts raw HTML to PDF bytes.
    /// </summary>
    [Fact]
    public async Task GeneratePdfFromHtmlAsync_SimpleHtml_ReturnsPdfBytes()
    {
        // Arrange — simple HTML document
        var html = "<html><body><h1>Test Invoice</h1><p>Total: 12,100.00 CZK</p></body></html>";

        // Act
        var result = await _service.GeneratePdfFromHtmlAsync(html);

        // Assert — should produce a non-trivial PDF byte array
        result.ShouldNotBeNull();
        result.ShouldNotBeEmpty();
        result.Length.ShouldBeGreaterThan(100);
    }

    /// <summary>
    /// Tests that the built-in fallback template is used when no content template
    /// is configured in the database for the invoice's document type.
    /// The service calls IContentTemplateService.GetDefaultByTypeAsync and, when it
    /// returns null, falls back to the hardcoded default HTML template.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_NoCustomTemplate_UsesDefaultTemplate()
    {
        // Arrange — mock GetDefaultByTypeAsync to return null (no template configured)
        _contentTemplateService
            .GetDefaultByTypeAsync(EContentTemplateType.InvoicePdf, Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        // Act — should still generate PDF using the built-in fallback template
        var result = await _service.GenerateInvoicePdfAsync(1);

        // Assert — PDF should be generated successfully even without a database template
        result.ShouldNotBeNull();
        result.ShouldNotBeEmpty();

        // Verify that the service actually tried to look up a content template
        await _contentTemplateService.Received(1)
            .GetDefaultByTypeAsync(EContentTemplateType.InvoicePdf, Arg.Any<CancellationToken>());
    }

    // ─── Phase D: Template Selection Tests ───────────────────────────────────

    /// <summary>
    /// Tests that when a specific contentTemplateId is provided, the service
    /// uses that template instead of the default. The mock returns a custom template
    /// with a simple HTML body containing a placeholder.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_WithSpecificTemplateId_UsesSelectedTemplate()
    {
        // Arrange — mock GetByIdAsync to return a specific template when requested
        var customTemplate = new ContentTemplateDto
        {
            Id = 42,
            Name = "Custom Layout",
            HtmlBody = "<html><body><h1>Custom: {{DocumentNumber}}</h1><p>Total: {{TotalWithVat}} {{CurrencySymbol}}</p>{{InvoiceItems}}{{VatBreakdown}}{{QrCodeImage}}</body></html>",
            TemplateType = EContentTemplateType.InvoicePdf,
            IsActive = true,
            IsDefault = false
        };
        _contentTemplateService
            .GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(customTemplate);

        // Act — generate PDF with specific template ID
        var result = await _service.GenerateInvoicePdfAsync(1, 42);

        // Assert — should return valid PDF bytes
        result.ShouldNotBeNull();
        result.ShouldNotBeEmpty();
        result[0].ShouldBe((byte)0x25); // PDF header

        // Verify it called GetByIdAsync with the specific template ID (NOT GetDefaultByTypeAsync)
        await _contentTemplateService.Received(1).GetByIdAsync(42, Arg.Any<CancellationToken>());
        await _contentTemplateService.DidNotReceive()
            .GetDefaultByTypeAsync(Arg.Any<EContentTemplateType>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Tests that when contentTemplateId is null, the service falls back to the default
    /// template resolution (GetDefaultByTypeAsync) — same as the no-parameter overload.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_NullTemplateId_FallsBackToDefault()
    {
        // Arrange — mock GetDefaultByTypeAsync to return null (built-in fallback used)
        _contentTemplateService
            .GetDefaultByTypeAsync(EContentTemplateType.InvoicePdf, Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        // Act — null templateId = use default resolution
        var result = await _service.GenerateInvoicePdfAsync(1, null);

        // Assert — should produce valid PDF using built-in fallback
        result.ShouldNotBeNull();
        result.ShouldNotBeEmpty();

        // Verify it used GetDefaultByTypeAsync (NOT GetByIdAsync)
        await _contentTemplateService.Received(1)
            .GetDefaultByTypeAsync(EContentTemplateType.InvoicePdf, Arg.Any<CancellationToken>());
        await _contentTemplateService.DidNotReceive()
            .GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Tests that when an invalid (non-existent) contentTemplateId is provided,
    /// the service throws KeyNotFoundException to signal the template doesn't exist.
    /// This prevents silent fallback to default when the user explicitly selected a template.
    /// </summary>
    [Fact]
    public async Task GenerateInvoicePdfAsync_InvalidTemplateId_ThrowsKeyNotFoundException()
    {
        // Arrange — mock GetByIdAsync returns null for the requested template ID
        _contentTemplateService
            .GetByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        // Act & Assert — should throw because the user explicitly requested a non-existent template
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.GenerateInvoicePdfAsync(1, 999));
    }
}
