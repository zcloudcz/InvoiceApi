using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Import;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="InvoiceImportService"/> — the import orchestrator.
/// All dependencies are mocked to test the orchestration logic in isolation.
///
/// Tests cover:
/// - 3-tier waterfall extraction (QR → AI → Regex)
/// - Data merging from multiple sources
/// - Validation rules for issued and received invoices
/// - Client resolution and creation
/// - Confirm import flow
/// </summary>
public class InvoiceImportServiceTests
{
    private readonly IQrCodeExtractor _qrExtractor;
    private readonly IInvoiceAiExtractor _aiExtractor;
    private readonly IInvoiceTextExtractor _textExtractor;
    private readonly IPdfTextExtractorService _pdfReader;
    private readonly IClientService _clientService;
    private readonly IInvoiceService _invoiceService;
    private readonly IReceivedInvoiceService _receivedInvoiceService;
    private readonly ICurrencyService _currencyService;
    private readonly InvoiceImportService _service;

    // Test data constants
    private const string IssuerIco = "12345678";
    private const string ClientIco = "87654321";

    public InvoiceImportServiceTests()
    {
        _qrExtractor = Substitute.For<IQrCodeExtractor>();
        _aiExtractor = Substitute.For<IInvoiceAiExtractor>();
        _textExtractor = Substitute.For<IInvoiceTextExtractor>();
        _pdfReader = Substitute.For<IPdfTextExtractorService>();
        _clientService = Substitute.For<IClientService>();
        _invoiceService = Substitute.For<IInvoiceService>();
        _receivedInvoiceService = Substitute.For<IReceivedInvoiceService>();
        _currencyService = Substitute.For<ICurrencyService>();
        var logger = Substitute.For<ILogger<InvoiceImportService>>();

        // Create an in-memory TenantDbContext with a test schema name.
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var tenantContext = new TenantDbContext(tenantOptions);
        tenantContext.Schema = "tenant_1";

        _service = new InvoiceImportService(
            tenantContext, _qrExtractor, _aiExtractor, _textExtractor, _pdfReader,
            _clientService, _invoiceService, _receivedInvoiceService,
            _currencyService, logger);

        // Default mock setup: issuer exists with IČO 12345678
        _clientService.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 1, RegistrationNumber = IssuerIco, CompanyName = "Test s.r.o." });

        // Default: no QR found
        _qrExtractor.ExtractFromPdfAsync(Arg.Any<byte[]>())
            .Returns(QrExtractionResult.NotFound());

        // Default: AI returns null (simulates no provider or extraction failure).
        _aiExtractor.ExtractAsync(Arg.Any<long?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((InvoiceExtractedData?)null);

        // Default: regex returns empty
        _textExtractor.Extract(Arg.Any<string>())
            .Returns(new InvoiceExtractedData { Source = EExtractionSource.RegexFallback });

        // Default: PDF text extraction works
        _pdfReader.ExtractTextAsync(Arg.Any<byte[]>())
            .Returns("Faktura dummy text");

        // Default: no existing invoices
        _invoiceService.GetAllInvoicesAsync(
            Arg.Any<EDocumentType?>(), Arg.Any<EInvoiceStatus?>(),
            Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new List<InvoiceDto>());

        _receivedInvoiceService.GetAllAsync(
            Arg.Any<EReceivedInvoiceStatus?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedInvoiceDto>());

        // Default: CZK currency
        _currencyService.GetCurrencyByCodeAsync("CZK", Arg.Any<CancellationToken>())
            .Returns(new CurrencyDto { Id = 1, Code = "CZK" });
    }

    // ─── MergeExtractedData (static) tests ───────────────────────────────

    [Fact]
    public void MergeExtractedData_QrOnly_ReturnsQrData()
    {
        var qr = new InvoiceExtractedData
        {
            DocumentNumber = "FV001",
            TotalAmount = 1000m,
            Source = EExtractionSource.QrCode
        };

        var result = InvoiceImportService.MergeExtractedData(qr, null, null);

        result.DocumentNumber.ShouldBe("FV001");
        result.TotalAmount.ShouldBe(1000m);
        result.Source.ShouldBe(EExtractionSource.QrCode);
    }

    [Fact]
    public void MergeExtractedData_QrPlusAi_QrTakesPriority()
    {
        var qr = new InvoiceExtractedData
        {
            DocumentNumber = "FV-QR",
            TotalAmount = 1000m,
            Source = EExtractionSource.QrCode
        };
        var ai = new InvoiceExtractedData
        {
            DocumentNumber = "FV-AI", // Should NOT override QR
            IssuerName = "AI Issuer", // Should fill in
            Source = EExtractionSource.AiExtraction
        };

        var result = InvoiceImportService.MergeExtractedData(qr, ai, null);

        result.DocumentNumber.ShouldBe("FV-QR"); // QR wins
        result.IssuerName.ShouldBe("AI Issuer"); // AI fills gap
        result.Source.ShouldBe(EExtractionSource.Merged);
    }

    [Fact]
    public void MergeExtractedData_AllThree_QrThenAiThenRegex()
    {
        var qr = new InvoiceExtractedData
        {
            TotalAmount = 1000m,
            Source = EExtractionSource.QrCode
        };
        var ai = new InvoiceExtractedData
        {
            IssuerName = "AI Issuer",
            Source = EExtractionSource.AiExtraction
        };
        var regex = new InvoiceExtractedData
        {
            DocumentNumber = "FV-REGEX",
            VariableSymbol = "12345",
            Source = EExtractionSource.RegexFallback
        };

        var result = InvoiceImportService.MergeExtractedData(qr, ai, regex);

        result.TotalAmount.ShouldBe(1000m);       // from QR
        result.IssuerName.ShouldBe("AI Issuer");   // from AI
        result.DocumentNumber.ShouldBe("FV-REGEX"); // from Regex
        result.VariableSymbol.ShouldBe("12345");    // from Regex
        result.Source.ShouldBe(EExtractionSource.Merged);
    }

    [Fact]
    public void MergeExtractedData_AllNull_ReturnsEmptyData()
    {
        var result = InvoiceImportService.MergeExtractedData(null, null, null);
        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBeNull();
    }

    // ─── PreviewImportAsync — Issued Invoice ─────────────────────────────

    [Fact]
    public async Task PreviewImportAsync_IssuedInvoice_IssuerMatches_NoError()
    {
        // Arrange: regex extracts issuer IČO matching our company
        _textExtractor.Extract(Arg.Any<string>()).Returns(new InvoiceExtractedData
        {
            DocumentNumber = "FV2026001",
            TotalAmount = 5000m,
            IssuerRegistrationNumber = IssuerIco,
            RecipientRegistrationNumber = ClientIco,
            Source = EExtractionSource.RegexFallback
        });

        _clientService.GetClientByRegistrationNumberAsync(ClientIco, Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, RegistrationNumber = ClientIco, CompanyName = "Klient a.s." });

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "test.pdf", EImportTarget.IssuedInvoice);

        // Assert: no issuer mismatch error
        result.Validations.ShouldNotContain(v =>
            v.Field == "IssuerRegistrationNumber" && v.Severity == EImportValidationSeverity.Error);
        result.ResolvedClientId.ShouldBe(2);
        result.ResolvedClientName.ShouldBe("Klient a.s.");
    }

    [Fact]
    public async Task PreviewImportAsync_IssuedInvoice_IssuerMismatch_Error()
    {
        // Arrange: issuer IČO on PDF doesn't match our company
        _textExtractor.Extract(Arg.Any<string>()).Returns(new InvoiceExtractedData
        {
            IssuerRegistrationNumber = "99999999", // Different from our IssuerIco
            TotalAmount = 5000m,
            Source = EExtractionSource.RegexFallback
        });

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "test.pdf", EImportTarget.IssuedInvoice);

        // Assert
        result.Validations.ShouldContain(v =>
            v.Field == "IssuerRegistrationNumber" && v.Severity == EImportValidationSeverity.Error);
    }

    [Fact]
    public async Task PreviewImportAsync_IssuedInvoice_ClientNotFound_Warning()
    {
        // Arrange: recipient IČO not in DB
        _textExtractor.Extract(Arg.Any<string>()).Returns(new InvoiceExtractedData
        {
            RecipientRegistrationNumber = "55555555",
            TotalAmount = 1000m,
            Source = EExtractionSource.RegexFallback
        });

        _clientService.GetClientByRegistrationNumberAsync("55555555", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "test.pdf", EImportTarget.IssuedInvoice);

        // Assert
        result.ClientNeedsCreation.ShouldBeTrue();
        result.Validations.ShouldContain(v =>
            v.Field == "Client" && v.Severity == EImportValidationSeverity.Warning);
    }

    [Fact]
    public async Task PreviewImportAsync_IssuedInvoice_DuplicateDocNumber_Error()
    {
        // Arrange: document number already exists
        _textExtractor.Extract(Arg.Any<string>()).Returns(new InvoiceExtractedData
        {
            DocumentNumber = "FV2026001",
            TotalAmount = 1000m,
            Source = EExtractionSource.RegexFallback
        });

        _invoiceService.GetAllInvoicesAsync(
            Arg.Any<EDocumentType?>(), Arg.Any<EInvoiceStatus?>(),
            Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new List<InvoiceDto>
            {
                new() { Id = 99, DocumentNumber = "FV2026001" }
            });

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "test.pdf", EImportTarget.IssuedInvoice);

        // Assert
        result.Validations.ShouldContain(v =>
            v.Field == "DocumentNumber" && v.Severity == EImportValidationSeverity.Error);
    }

    // ─── PreviewImportAsync — Received Invoice ───────────────────────────

    [Fact]
    public async Task PreviewImportAsync_ReceivedInvoice_RecipientMatches_NoError()
    {
        // Arrange: recipient IČO matches our company (correct for received invoices)
        _textExtractor.Extract(Arg.Any<string>()).Returns(new InvoiceExtractedData
        {
            IssuerRegistrationNumber = ClientIco,     // Supplier
            RecipientRegistrationNumber = IssuerIco,  // Us (recipient)
            TotalAmount = 5000m,
            Source = EExtractionSource.RegexFallback
        });

        _clientService.GetClientByRegistrationNumberAsync(ClientIco, Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, RegistrationNumber = ClientIco, CompanyName = "Supplier s.r.o." });

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "test.pdf", EImportTarget.ReceivedInvoice);

        // Assert: no recipient mismatch error
        result.Validations.ShouldNotContain(v =>
            v.Field == "RecipientRegistrationNumber" && v.Severity == EImportValidationSeverity.Error);
        result.ResolvedClientId.ShouldBe(2); // Supplier resolved
    }

    [Fact]
    public async Task PreviewImportAsync_ReceivedInvoice_RecipientMismatch_Error()
    {
        // Arrange: recipient IČO doesn't match our company
        _textExtractor.Extract(Arg.Any<string>()).Returns(new InvoiceExtractedData
        {
            RecipientRegistrationNumber = "99999999",
            TotalAmount = 5000m,
            Source = EExtractionSource.RegexFallback
        });

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "test.pdf", EImportTarget.ReceivedInvoice);

        // Assert
        result.Validations.ShouldContain(v =>
            v.Field == "RecipientRegistrationNumber" && v.Severity == EImportValidationSeverity.Error);
    }

    [Fact]
    public async Task PreviewImportAsync_ReceivedInvoice_DuplicateFromSameSupplier_Error()
    {
        // Arrange: same document number from same supplier already exists
        _textExtractor.Extract(Arg.Any<string>()).Returns(new InvoiceExtractedData
        {
            DocumentNumber = "SUP-001",
            IssuerRegistrationNumber = ClientIco,
            TotalAmount = 1000m,
            Source = EExtractionSource.RegexFallback
        });

        _clientService.GetClientByRegistrationNumberAsync(ClientIco, Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, RegistrationNumber = ClientIco, CompanyName = "Supplier" });

        _receivedInvoiceService.GetAllAsync(
            Arg.Any<EReceivedInvoiceStatus?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedInvoiceDto>
            {
                new() { Id = 99, DocumentNumber = "SUP-001", SupplierId = 2 }
            });

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "test.pdf", EImportTarget.ReceivedInvoice);

        // Assert
        result.Validations.ShouldContain(v =>
            v.Field == "DocumentNumber" && v.Severity == EImportValidationSeverity.Error);
    }

    // ─── PreviewImportAsync — Extraction pipeline ────────────────────────

    [Fact]
    public async Task PreviewImportAsync_QrFound_UsesQrData()
    {
        // Arrange: QR code found with SIND data
        var sindData = new SindData
        {
            DocumentNumber = "FV-QR",
            Amount = 5000m,
            IssuerRegistrationNumber = IssuerIco
        };

        _qrExtractor.ExtractFromPdfAsync(Arg.Any<byte[]>()).Returns(new QrExtractionResult
        {
            Found = true,
            QrType = "SIND",
            Sind = sindData
        });

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "qr.pdf", EImportTarget.IssuedInvoice);

        // Assert
        result.HasQrCode.ShouldBeTrue();
        result.QrType.ShouldBe("SIND");
        result.DocumentNumber.ShouldBe("FV-QR");
        result.TotalAmount.ShouldBe(5000m);
    }

    [Fact]
    public async Task PreviewImportAsync_NoQr_AiAvailable_UsesAi()
    {
        // Arrange: no QR, but AI extracts data successfully
        _aiExtractor.ExtractAsync(Arg.Any<long?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceExtractedData
            {
                DocumentNumber = "FV-AI",
                TotalAmount = 3000m,
                IssuerName = "AI Company",
                Source = EExtractionSource.AiExtraction
            });

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "ai.pdf", EImportTarget.IssuedInvoice);

        // Assert
        result.DocumentNumber.ShouldBe("FV-AI");
        result.TotalAmount.ShouldBe(3000m);
        result.IssuerName.ShouldBe("AI Company");
    }

    [Fact]
    public async Task PreviewImportAsync_MissingAmount_Error()
    {
        // Arrange: extracted data has no amount
        _textExtractor.Extract(Arg.Any<string>()).Returns(new InvoiceExtractedData
        {
            DocumentNumber = "FV001",
            Source = EExtractionSource.RegexFallback
        });

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "noamount.pdf", EImportTarget.IssuedInvoice);

        // Assert
        result.Validations.ShouldContain(v =>
            v.Field == "TotalAmount" && v.Severity == EImportValidationSeverity.Error);
    }

    [Fact]
    public async Task PreviewImportAsync_PdfReadFails_ReturnsErrorPreview()
    {
        // Arrange: PDF reader throws
        _pdfReader.ExtractTextAsync(Arg.Any<byte[]>())
            .Returns<string>(x => throw new ArgumentException("Corrupt PDF"));

        // Act
        var result = await _service.PreviewImportAsync(
            new byte[] { 1 }, "corrupt.pdf", EImportTarget.IssuedInvoice);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.Validations.ShouldContain(v => v.Field == "PDF" && v.Severity == EImportValidationSeverity.Error);
    }

    // ─── ConfirmImportAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ConfirmImportAsync_IssuedInvoice_CreatesInvoice()
    {
        // Arrange
        _invoiceService.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 100, DocumentNumber = "FV2026001" });

        var request = new ConfirmInvoiceImportRequest
        {
            Target = EImportTarget.IssuedInvoice,
            Items = new List<ConfirmImportItemDto>
            {
                new()
                {
                    FileName = "test.pdf",
                    ClientId = 2,
                    TotalAmount = 5000m,
                    Currency = "CZK",
                    PdfBytes = new byte[] { 1 }
                }
            }
        };

        // Act
        var results = await _service.ConfirmImportAsync(request);

        // Assert
        results.Count.ShouldBe(1);
        results[0].Success.ShouldBeTrue();
        results[0].InvoiceId.ShouldBe(100);
        results[0].DocumentNumber.ShouldBe("FV2026001");
    }

    [Fact]
    public async Task ConfirmImportAsync_ReceivedInvoice_CreatesReceivedInvoice()
    {
        // Arrange
        _receivedInvoiceService.CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto { Id = 200, DocumentNumber = "SUP-001" });

        var request = new ConfirmInvoiceImportRequest
        {
            Target = EImportTarget.ReceivedInvoice,
            Items = new List<ConfirmImportItemDto>
            {
                new()
                {
                    FileName = "received.pdf",
                    ClientId = 3,
                    DocumentNumber = "SUP-001",
                    TotalAmount = 3000m,
                    Currency = "CZK",
                    PdfBytes = new byte[] { 1 }
                }
            }
        };

        // Act
        var results = await _service.ConfirmImportAsync(request);

        // Assert
        results.Count.ShouldBe(1);
        results[0].Success.ShouldBeTrue();
        results[0].InvoiceId.ShouldBe(200);
    }

    [Fact]
    public async Task ConfirmImportAsync_CreateClient_CreatesAndAssigns()
    {
        // Arrange: client doesn't exist, should be created
        _clientService.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 50, RegistrationNumber = "55555555", CompanyName = "New Client" });

        _invoiceService.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 101, DocumentNumber = "FV002" });

        var request = new ConfirmInvoiceImportRequest
        {
            Target = EImportTarget.IssuedInvoice,
            Items = new List<ConfirmImportItemDto>
            {
                new()
                {
                    FileName = "test.pdf",
                    CreateClient = true,
                    NewClientRegistrationNumber = "55555555",
                    TotalAmount = 1000m,
                    Currency = "CZK",
                    PdfBytes = new byte[] { 1 }
                }
            }
        };

        // Act
        var results = await _service.ConfirmImportAsync(request);

        // Assert
        results[0].Success.ShouldBeTrue();
        results[0].CreatedClientId.ShouldBe(50);
        await _clientService.Received(1).CreateClientAsync(
            Arg.Is<CreateClientDto>(d => d.RegistrationNumber == "55555555"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmImportAsync_NoClient_Fails()
    {
        // Arrange: no client ID, no creation requested
        var request = new ConfirmInvoiceImportRequest
        {
            Target = EImportTarget.IssuedInvoice,
            Items = new List<ConfirmImportItemDto>
            {
                new()
                {
                    FileName = "test.pdf",
                    ClientId = null,
                    CreateClient = false,
                    PdfBytes = new byte[] { 1 }
                }
            }
        };

        // Act
        var results = await _service.ConfirmImportAsync(request);

        // Assert
        results[0].Success.ShouldBeFalse();
        results[0].ErrorMessage.ShouldContain("No client");
    }

    [Fact]
    public async Task ConfirmImportAsync_ServiceThrows_ReturnsFailed()
    {
        // Arrange: invoice creation throws
        _invoiceService.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns<InvoiceDto>(x => throw new InvalidOperationException("Duplicate VS"));

        var request = new ConfirmInvoiceImportRequest
        {
            Target = EImportTarget.IssuedInvoice,
            Items = new List<ConfirmImportItemDto>
            {
                new()
                {
                    FileName = "test.pdf",
                    ClientId = 2,
                    Currency = "CZK",
                    PdfBytes = new byte[] { 1 }
                }
            }
        };

        // Act
        var results = await _service.ConfirmImportAsync(request);

        // Assert
        results[0].Success.ShouldBeFalse();
        results[0].ErrorMessage.ShouldContain("Duplicate VS");
    }
}
