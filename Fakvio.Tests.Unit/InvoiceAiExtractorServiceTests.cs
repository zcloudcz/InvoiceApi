using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Infrastructure.Service;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="InvoiceAiExtractorService"/>.
/// Tests cover JSON parsing, AI provider integration, error handling, and timeout behavior.
///
/// Most tests focus on the static ParseAiResponse method (pure function, no mocking needed).
/// Integration tests mock the IAiProvider to verify the full extraction flow.
/// </summary>
public class InvoiceAiExtractorServiceTests
{
    private readonly ICompanyAiSettingsResolver _companyAiResolver;
    private readonly IAiProvider _provider;
    private readonly ILogger<InvoiceAiExtractorService> _logger;
    private readonly InvoiceAiExtractorService _service;

    public InvoiceAiExtractorServiceTests()
    {
        _companyAiResolver = Substitute.For<ICompanyAiSettingsResolver>();
        _provider = Substitute.For<IAiProvider>();
        _logger = Substitute.For<ILogger<InvoiceAiExtractorService>>();

        // Default: resolver returns the mock provider for any companyId.
        _companyAiResolver.ResolveProviderAsync(Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_provider);

        _service = new InvoiceAiExtractorService(_companyAiResolver, _logger);
    }

    // ─── ExtractAsync integration tests ──────────────────────────────────

    [Fact]
    public async Task ExtractAsync_ResolverThrows_ReturnsNull()
    {
        // Arrange: no AI provider configured — resolver throws.
        _companyAiResolver.ResolveProviderAsync(Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<IAiProvider>(_ => throw new InvalidOperationException("No AI providers configured"));

        // Act
        var result = await _service.ExtractAsync(1L, "some invoice text");

        // Assert: should NOT propagate the exception — returns null for regex fallback.
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractAsync_EmptyText_ReturnsNull()
    {
        var result = await _service.ExtractAsync(1L, "");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractAsync_ValidAiResponse_ReturnsExtractedData()
    {
        // Arrange: AI returns valid JSON
        var aiJson = """
            {
              "documentNumber": "FV2026001",
              "issueDate": "2026-03-01",
              "dueDate": "2026-03-15",
              "totalAmount": 12100.00,
              "totalVat": 2100.00,
              "totalBeforeVat": 10000.00,
              "currency": "CZK",
              "issuerName": "Test s.r.o.",
              "issuerRegistrationNumber": "12345678",
              "issuerTaxNumber": "CZ12345678"
            }
            """;
        _provider.GetCompletionAsync(
            Arg.Any<List<ChatMessageDto>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(aiJson);

        // Act
        var result = await _service.ExtractAsync(1L, "Faktura č. FV2026001...");

        // Assert
        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("FV2026001");
        result.TotalAmount.ShouldBe(12100.00m);
        result.IssuerName.ShouldBe("Test s.r.o.");
        result.Source.ShouldBe(EExtractionSource.AiExtraction);
    }

    [Fact]
    public async Task ExtractAsync_ProviderThrows_ReturnsNull()
    {
        // Arrange: AI provider throws an exception
        _provider.GetCompletionAsync(
            Arg.Any<List<ChatMessageDto>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("API key invalid"));

        // Act
        var result = await _service.ExtractAsync(1L, "some text");

        // Assert: should NOT propagate the exception
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ExtractAsync_CancellationRequested_ReturnsNull()
    {
        // Arrange: pre-cancelled token
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _provider.GetCompletionAsync(
            Arg.Any<List<ChatMessageDto>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        // Act
        var result = await _service.ExtractAsync(1L, "some text", cts.Token);

        // Assert
        result.ShouldBeNull();
    }

    // ─── ParseAiResponse (static) tests ──────────────────────────────────

    [Fact]
    public void ParseAiResponse_ValidJson_ParsesAllFields()
    {
        var json = """
            {
              "documentNumber": "FV2026042",
              "issueDate": "2026-03-10",
              "dueDate": "2026-03-24",
              "taxableSupplyDate": "2026-03-10",
              "totalAmount": 12100.00,
              "totalVat": 2100.00,
              "totalBeforeVat": 10000.00,
              "currency": "czk",
              "variableSymbol": "2026042",
              "iban": "CZ5855000000001265098001",
              "bankAccountNumber": "1265098001/5500",
              "swift": "RZBCCZPP",
              "paymentMethod": "BankTransfer",
              "issuerName": "Fakvio s.r.o.",
              "issuerRegistrationNumber": "12345678",
              "issuerTaxNumber": "CZ12345678",
              "recipientName": "Klient a.s.",
              "recipientRegistrationNumber": "87654321",
              "recipientTaxNumber": "CZ87654321",
              "items": [
                {
                  "description": "Web development",
                  "quantity": 40,
                  "unitPrice": 250.00,
                  "vatRate": 21,
                  "unit": "hod"
                }
              ]
            }
            """;

        var result = InvoiceAiExtractorService.ParseAiResponse(json);

        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("FV2026042");
        result.IssueDate.ShouldBe(new DateTime(2026, 3, 10));
        result.DueDate.ShouldBe(new DateTime(2026, 3, 24));
        result.TaxableSupplyDate.ShouldBe(new DateTime(2026, 3, 10));
        result.TotalAmount.ShouldBe(12100.00m);
        result.TotalVat.ShouldBe(2100.00m);
        result.TotalBeforeVat.ShouldBe(10000.00m);
        result.Currency.ShouldBe("CZK"); // Uppercased
        result.VariableSymbol.ShouldBe("2026042");
        result.IBAN.ShouldBe("CZ5855000000001265098001");
        result.BankAccountNumber.ShouldBe("1265098001/5500");
        result.SWIFT.ShouldBe("RZBCCZPP");
        result.PaymentMethod.ShouldBe("BankTransfer");
        result.IssuerName.ShouldBe("Fakvio s.r.o.");
        result.IssuerRegistrationNumber.ShouldBe("12345678");
        result.IssuerTaxNumber.ShouldBe("CZ12345678");
        result.RecipientName.ShouldBe("Klient a.s.");
        result.RecipientRegistrationNumber.ShouldBe("87654321");
        result.RecipientTaxNumber.ShouldBe("CZ87654321");

        // Items
        result.Items.ShouldNotBeNull();
        result.Items.Count.ShouldBe(1);
        result.Items[0].Description.ShouldBe("Web development");
        result.Items[0].Quantity.ShouldBe(40m);
        result.Items[0].UnitPrice.ShouldBe(250.00m);
        result.Items[0].VatRate.ShouldBe(21m);
        result.Items[0].Unit.ShouldBe("hod");
    }

    [Fact]
    public void ParseAiResponse_PartialData_NullableFieldsRemainNull()
    {
        // AI only found some fields
        var json = """
            {
              "documentNumber": "FV001",
              "totalAmount": 5000.00,
              "issuerName": "Test",
              "dueDate": null,
              "items": null
            }
            """;

        var result = InvoiceAiExtractorService.ParseAiResponse(json);

        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("FV001");
        result.TotalAmount.ShouldBe(5000.00m);
        result.IssuerName.ShouldBe("Test");
        result.DueDate.ShouldBeNull();
        result.IssueDate.ShouldBeNull();
        result.IBAN.ShouldBeNull();
        result.Items.ShouldBeNull();
    }

    [Fact]
    public void ParseAiResponse_InvalidJson_ReturnsNull()
    {
        var result = InvoiceAiExtractorService.ParseAiResponse("this is not json at all");
        result.ShouldBeNull();
    }

    [Fact]
    public void ParseAiResponse_WrappedInMarkdown_StillParses()
    {
        // Some AI models wrap JSON in markdown code blocks despite instructions
        var json = """
            ```json
            {
              "documentNumber": "FV001",
              "totalAmount": 1234.56
            }
            ```
            """;

        var result = InvoiceAiExtractorService.ParseAiResponse(json);

        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("FV001");
        result.TotalAmount.ShouldBe(1234.56m);
    }

    [Fact]
    public void ParseAiResponse_CzechDateFormat_ParsesCorrectly()
    {
        var json = """
            {
              "issueDate": "10.03.2026",
              "dueDate": "24.03.2026"
            }
            """;

        var result = InvoiceAiExtractorService.ParseAiResponse(json);

        result.ShouldNotBeNull();
        result.IssueDate.ShouldBe(new DateTime(2026, 3, 10));
        result.DueDate.ShouldBe(new DateTime(2026, 3, 24));
    }

    [Fact]
    public void ParseAiResponse_EmptyString_ReturnsNull()
    {
        InvoiceAiExtractorService.ParseAiResponse("").ShouldBeNull();
    }
}
