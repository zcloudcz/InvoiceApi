using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ExportInvoiceTool.
/// Tests invoice lookup (by document number and by client name),
/// download action generation, and error handling.
///
/// Uses NSubstitute to mock IInvoiceService and IClientService.
/// </summary>
public class ExportInvoiceToolTests
{
    private readonly ExportInvoiceTool _tool;
    private readonly IInvoiceService _invoiceService;
    private readonly IClientService _clientService;

    public ExportInvoiceToolTests()
    {
        _invoiceService = Substitute.For<IInvoiceService>();
        _clientService = Substitute.For<IClientService>();
        var logger = Substitute.For<ILogger<ExportInvoiceTool>>();
        _tool = new ExportInvoiceTool(_invoiceService, _clientService, logger);
    }

    /// <summary>
    /// Throwaway in-memory TenantDbContext — the executor only needs one to discard leftover
    /// change-tracker entries after a failed write (issue #305); no test here touches it.
    /// </summary>
    private static TenantDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    // ─── Basic Properties ─────────────────────────────────────────────

    [Fact]
    public void ToolName_ShouldBeExportInvoice()
    {
        _tool.ToolName.ShouldBe("export_invoice");
    }

    // ─── Export by Document Number ────────────────────────────────────

    [Fact]
    public async Task ExportByDocumentNumber_Found_ReturnsDownloadAction()
    {
        // Arrange — invoice exists with the given document number
        var invoice = new InvoiceDto
        {
            Id = 42,
            DocumentNumber = "FV-2024-0001",
            DocumentType = EDocumentType.Invoice,
            ClientName = "Test s.r.o."
        };
        _invoiceService.GetInvoiceByDocumentNumberAsync("FV-2024-0001", Arg.Any<CancellationToken>())
            .Returns(invoice);

        var parameters = new Dictionary<string, string>
        {
            ["document_number"] = "FV-2024-0001"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — should return a download action pointing to the PDF endpoint
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Type.ShouldBe("download");
        result.UiAction.Url.ShouldBe("/api/invoice/42/pdf");
        result.UiAction.Parameters.ShouldNotBeNull();
        result.UiAction.Parameters!["fileName"].ShouldBe("Invoice_FV-2024-0001.pdf");
        result.UiAction.Parameters["mimeType"].ShouldBe("application/pdf");
        result.OutputText.ShouldContain("FV-2024-0001");
    }

    [Fact]
    public async Task ExportByDocumentNumber_CreditNote_ReturnsCorrectPrefix()
    {
        // Arrange — credit note (dobropis) should use "CreditNote" prefix in file name
        var invoice = new InvoiceDto
        {
            Id = 10,
            DocumentNumber = "DP-2024-0001",
            DocumentType = EDocumentType.CreditNote,
            ClientName = "ABC"
        };
        _invoiceService.GetInvoiceByDocumentNumberAsync("DP-2024-0001", Arg.Any<CancellationToken>())
            .Returns(invoice);

        var parameters = new Dictionary<string, string>
        {
            ["document_number"] = "DP-2024-0001"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — file name should use CreditNote prefix
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Parameters!["fileName"].ShouldBe("CreditNote_DP-2024-0001.pdf");
        result.OutputText.ShouldContain("credit note");
    }

    [Fact]
    public async Task ExportByDocumentNumber_NotFound_ReturnsFailure()
    {
        // Arrange — no invoice with this document number
        _invoiceService.GetInvoiceByDocumentNumberAsync("INVALID", Arg.Any<CancellationToken>())
            .Returns((InvoiceDto?)null);

        var parameters = new Dictionary<string, string>
        {
            ["document_number"] = "INVALID"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.UiAction.ShouldBeNull();
        result.OutputText.ShouldContain("INVALID");
    }

    // ─── Export by Client Name ────────────────────────────────────────

    [Fact]
    public async Task ExportByClientName_Found_ReturnsMostRecentInvoice()
    {
        // Arrange — client found, two invoices exist, should pick the most recent one
        var client = new ClientDto { Id = 5, CompanyName = "Alza.cz" };
        _clientService.GetAllClientsAsync(false, Arg.Any<CancellationToken>())
            .Returns(new List<ClientDto> { client });

        var invoices = new List<InvoiceDto>
        {
            new()
            {
                Id = 1, ClientId = 5, ClientName = "Alza.cz",
                DocumentNumber = "FV-2024-0001", DocumentType = EDocumentType.Invoice,
                CreatedAt = new DateTime(2024, 1, 1)
            },
            new()
            {
                Id = 2, ClientId = 5, ClientName = "Alza.cz",
                DocumentNumber = "FV-2024-0002", DocumentType = EDocumentType.Invoice,
                CreatedAt = new DateTime(2024, 6, 1) // More recent
            }
        };
        _invoiceService.GetAllInvoicesAsync(null, null, null, null, Arg.Any<CancellationToken>())
            .Returns(invoices);

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — should pick the most recent invoice (Id=2)
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Type.ShouldBe("download");
        result.UiAction.Url.ShouldBe("/api/invoice/2/pdf");
        result.UiAction.Parameters!["fileName"].ShouldBe("Invoice_FV-2024-0002.pdf");
    }

    [Fact]
    public async Task ExportByClientName_NoClient_ReturnsFailure()
    {
        // Arrange — no client matches
        _clientService.GetAllClientsAsync(false, Arg.Any<CancellationToken>())
            .Returns(new List<ClientDto>());

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "NonExistent"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("NonExistent");
    }

    [Fact]
    public async Task ExportByClientName_TooManyMatches_ReturnsFailure()
    {
        // Arrange — more than 5 clients match → too ambiguous
        var clients = Enumerable.Range(1, 6)
            .Select(i => new ClientDto { Id = i, CompanyName = $"ABC Company {i}" })
            .ToList();
        _clientService.GetAllClientsAsync(false, Arg.Any<CancellationToken>())
            .Returns(clients);

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "ABC"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("6");
        result.OutputText.ShouldContain("specific");
    }

    [Fact]
    public async Task ExportByClientName_ClientFoundButNoInvoices_ReturnsFailure()
    {
        // Arrange — client exists but has no invoices
        var client = new ClientDto { Id = 5, CompanyName = "Empty Corp" };
        _clientService.GetAllClientsAsync(false, Arg.Any<CancellationToken>())
            .Returns(new List<ClientDto> { client });

        _invoiceService.GetAllInvoicesAsync(null, null, null, null, Arg.Any<CancellationToken>())
            .Returns(new List<InvoiceDto>());

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Empty"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Empty Corp");
    }

    // ─── Validation ───────────────────────────────────────────────────

    [Fact]
    public async Task NoParameters_ExportsMostRecentInvoice()
    {
        // Arrange — no parameters, should export most recent invoice
        var invoices = new List<InvoiceDto>
        {
            new()
            {
                Id = 1, ClientId = 1, ClientName = "Old Client",
                DocumentNumber = "FV-2024-0001", DocumentType = EDocumentType.Invoice,
                CreatedAt = new DateTime(2024, 1, 1)
            },
            new()
            {
                Id = 3, ClientId = 2, ClientName = "New Client",
                DocumentNumber = "FV-2024-0003", DocumentType = EDocumentType.Invoice,
                CreatedAt = new DateTime(2024, 12, 1) // Most recent
            }
        };
        _invoiceService.GetAllInvoicesAsync(null, null, null, null, Arg.Any<CancellationToken>())
            .Returns(invoices);

        var parameters = new Dictionary<string, string>();

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — should pick the most recent (Id=3)
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Type.ShouldBe("download");
        result.UiAction.Url.ShouldBe("/api/invoice/3/pdf");
    }

    [Fact]
    public async Task NoParameters_NoInvoices_ReturnsFailure()
    {
        // Arrange — no invoices exist at all
        _invoiceService.GetAllInvoicesAsync(null, null, null, null, Arg.Any<CancellationToken>())
            .Returns(new List<InvoiceDto>());

        var parameters = new Dictionary<string, string>();

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("No invoices");
    }

    [Fact]
    public async Task DocumentNumberTakesPriority_OverClientName()
    {
        // Arrange — both parameters provided, document_number should be used first
        var invoice = new InvoiceDto
        {
            Id = 99,
            DocumentNumber = "FV-2024-0099",
            DocumentType = EDocumentType.Invoice,
            ClientName = "Some Client"
        };
        _invoiceService.GetInvoiceByDocumentNumberAsync("FV-2024-0099", Arg.Any<CancellationToken>())
            .Returns(invoice);

        var parameters = new Dictionary<string, string>
        {
            ["document_number"] = "FV-2024-0099",
            ["client_name"] = "Alza"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — should use document_number path, not client_name
        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Url.ShouldBe("/api/invoice/99/pdf");

        // Client search should NOT have been called
        await _clientService.DidNotReceive()
            .GetAllClientsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ─── Export format (issue #217 — MCP parity with ExportInvoiceIsdoc) ──

    /// <summary>
    /// The format decides three things at once — endpoint, file extension and MIME type — so
    /// they are asserted together: an .isdoc downloaded from the PDF endpoint is a corrupt file
    /// with a plausible name.
    /// </summary>
    [Theory]
    [InlineData("isdoc", "/api/invoice/42/isdoc", "Invoice_FV-2024-0001.isdoc", "application/xml")]
    [InlineData("pdf", "/api/invoice/42/pdf", "Invoice_FV-2024-0001.pdf", "application/pdf")]
    public async Task ExportFormat_ChoosesTheEndpointFileNameAndMimeType(
        string format, string expectedUrl, string expectedFileName, string expectedMimeType)
    {
        _invoiceService.GetInvoiceByDocumentNumberAsync("FV-2024-0001", Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto
            {
                Id = 42,
                DocumentNumber = "FV-2024-0001",
                DocumentType = EDocumentType.Invoice,
                ClientName = "Test s.r.o."
            });

        var result = await _tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["document_number"] = "FV-2024-0001",
            ["format"] = format
        });

        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Url.ShouldBe(expectedUrl);
        result.UiAction.Parameters!["fileName"].ShouldBe(expectedFileName);
        result.UiAction.Parameters["mimeType"].ShouldBe(expectedMimeType);
    }

    [Fact]
    public async Task ExportFormat_DefaultsToPdf_WhenTheModelSendsNone()
    {
        _invoiceService.GetInvoiceByDocumentNumberAsync("FV-2024-0001", Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto
            {
                Id = 42,
                DocumentNumber = "FV-2024-0001",
                DocumentType = EDocumentType.Invoice,
                ClientName = "Test s.r.o."
            });

        var result = await _tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["document_number"] = "FV-2024-0001"
        });

        result.UiAction!.Url.ShouldBe("/api/invoice/42/pdf");
        result.OutputText.ShouldContain("as PDF");
    }

    /// <summary>
    /// Arranges the one invoice the format tests export. Kept here so a format test says
    /// nothing but what it is about.
    /// </summary>
    private void GivenExportableInvoice()
        => _invoiceService.GetInvoiceByDocumentNumberAsync("FV-2024-0001", Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto
            {
                Id = 42,
                DocumentNumber = "FV-2024-0001",
                DocumentType = EDocumentType.Invoice,
                ClientName = "Test s.r.o."
            });

    /// <summary>
    /// Models do not spell parameters carefully, and the executor accepts an allowed value in
    /// any casing and dispatches it raw. Without normalisation "ISDOC" would build the URL
    /// /api/invoice/42/ISDOC and a file named .ISDOC — a 404 with a plausible name.
    /// </summary>
    [Theory]
    [InlineData("ISDOC")]
    [InlineData("  isdoc  ")]
    public async Task ExportFormat_IsNormalised_BeforeItBecomesAnUrl(string format)
    {
        GivenExportableInvoice();

        var result = await _tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["document_number"] = "FV-2024-0001",
            ["format"] = format
        });

        result.IsSuccess.ShouldBeTrue();
        result.UiAction!.Url.ShouldBe("/api/invoice/42/isdoc");
        result.UiAction.Parameters!["fileName"].ShouldBe("Invoice_FV-2024-0001.isdoc");
        result.UiAction.Parameters["mimeType"].ShouldBe("application/xml");
    }

    /// <summary>
    /// The tool treats anything that is not isdoc as pdf, which is only safe because the
    /// executor rejects a format outside AllowedValues first. Without that wiring a model
    /// asking for xlsx would silently receive a PDF and report success.
    /// </summary>
    [Fact]
    public async Task ExportFormat_UnknownValue_IsRejected_InsteadOfSilentlyBecomingPdf()
    {
        GivenExportableInvoice();
        var executor = new ChatToolExecutor([_tool], CreateDbContext(), Substitute.For<ILogger<ChatToolExecutor>>());

        var result = await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "export_invoice",
            Parameters = new Dictionary<string, string>
            {
                ["document_number"] = "FV-2024-0001",
                ["format"] = "xlsx"
            }
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("format");
        result.UiAction.ShouldBeNull();
        await _invoiceService.DidNotReceive()
            .GetInvoiceByDocumentNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

}
