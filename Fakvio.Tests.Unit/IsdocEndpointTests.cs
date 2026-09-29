using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using System.Text.Json;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the ISDOC export endpoint wiring (issue #14):
///   - InvoiceController.ExportIsdoc — HTTP action
///   - InvoiceFunctions.Invoice_ExportIsdoc — Azure Functions wrapper
///   - InvoiceTools.ExportInvoiceIsdoc — MCP tool
///
/// These tests verify the transport layer only — the ISDOC mapper/service logic
/// is covered separately in IsdocExportServiceTests.cs.
/// </summary>
public class IsdocEndpointTests
{
    // ── Test doubles ──────────────────────────────────────────────────────────

    private readonly IInvoiceService _invoiceService = Substitute.For<IInvoiceService>();
    private readonly IPdfExportService _pdfExportService = Substitute.For<IPdfExportService>();
    private readonly IIsdocExportService _isdocExportService = Substitute.For<IIsdocExportService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IQrPaymentService _qrPaymentService = Substitute.For<IQrPaymentService>();
    private readonly ICloudStorageOrchestrator _cloudStorage = Substitute.For<ICloudStorageOrchestrator>();
    private readonly ILogger<InvoiceController> _logger = Substitute.For<ILogger<InvoiceController>>();

    // System under test: the controller
    private readonly InvoiceController _controller;

    public IsdocEndpointTests()
    {
        _controller = new InvoiceController(
            _invoiceService,
            _pdfExportService,
            _isdocExportService,
            Substitute.For<IUblExportService>(),
            _emailService,
            _qrPaymentService,
            _cloudStorage,
            Substitute.For<IPaymentMatchingService>(),
            _logger);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal InvoiceDto stub for a given invoice ID and document number.
    /// </summary>
    private static InvoiceDto MakeInvoiceDto(long id, string docNumber, EDocumentType type = EDocumentType.Invoice)
        => new InvoiceDto
        {
            Id = id,
            DocumentNumber = docNumber,
            DocumentType = type,
            Status = EInvoiceStatus.Completed,
            ClientName = "Test Client",
            IssuerName = "Test Issuer",
            CurrencyCode = "CZK",
            CurrencySymbol = "Kc"
        };

    // ── Controller tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task ExportIsdoc_ExistingInvoice_ReturnsFileWithXmlContentType()
    {
        // Arrange — service returns fake ISDOC bytes and invoice metadata
        var invoiceId = 42L;
        var docNumber = "INV2026001";
        var fakeXmlBytes = "<ISDOC>test</ISDOC>"u8.ToArray();

        _isdocExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(fakeXmlBytes);
        _invoiceService.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, docNumber));

        // Act
        var result = await _controller.ExportIsdoc(invoiceId, CancellationToken.None);

        // Assert — must be a FileContentResult (not OkObjectResult, not redirect)
        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.ContentType.ShouldBe("application/xml");
        fileResult.FileContents.ShouldBe(fakeXmlBytes);

        // File name should include the document number and .isdoc extension
        fileResult.FileDownloadName.ShouldBe($"Invoice_{docNumber}.isdoc");
    }

    [Fact]
    public async Task ExportIsdoc_CreditNote_UsesCorrectFileNamePrefix()
    {
        // Arrange — credit note should use "CreditNote_" prefix in file name
        var invoiceId = 99L;
        var docNumber = "CN2026001";
        var fakeXmlBytes = "<ISDOC>credit</ISDOC>"u8.ToArray();

        _isdocExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(fakeXmlBytes);
        _invoiceService.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, docNumber, EDocumentType.CreditNote));

        // Act
        var result = await _controller.ExportIsdoc(invoiceId, CancellationToken.None);

        // Assert — credit note prefix must be used
        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.FileDownloadName.ShouldBe($"CreditNote_{docNumber}.isdoc");
    }

    [Fact]
    public async Task ExportIsdoc_InvoiceNotFound_Returns404()
    {
        // Arrange — service throws KeyNotFoundException for an unknown ID
        var missingId = 9999L;
        _isdocExportService.ExportInvoiceAsync(missingId, Arg.Any<CancellationToken>())
            .Throws(new KeyNotFoundException($"Invoice {missingId} not found."));

        // Act
        var result = await _controller.ExportIsdoc(missingId, CancellationToken.None);

        // Assert — controller must translate KeyNotFoundException to 404
        var notFound = result.ShouldBeOfType<NotFoundObjectResult>();
        notFound.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task ExportIsdoc_ContentTypeIsApplicationXml()
    {
        // Arrange — basic invoice setup
        var invoiceId = 1L;
        _isdocExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0x3C, 0x49, 0x53 }); // "<IS" bytes
        _invoiceService.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, "INV001"));

        // Act
        var result = await _controller.ExportIsdoc(invoiceId, CancellationToken.None);

        // Assert — ISDOC files must be served as application/xml (not application/pdf)
        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.ContentType.ShouldBe("application/xml");
    }

    // ── MCP tool tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportInvoiceIsdoc_ReturnsBase64EncodedXml()
    {
        // Arrange — mock API client returning known ISDOC bytes
        var apiClient = Substitute.For<IFakvioApiClient>();
        var invoiceId = 5L;
        var xmlString = "<?xml version=\"1.0\"?><ISDOC>invoice data</ISDOC>";
        var xmlBytes = System.Text.Encoding.UTF8.GetBytes(xmlString);

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, "INV2026005"));
        apiClient.ExportInvoiceIsdocAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(xmlBytes);

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        // Assert — the MCP tool should return a JSON object with base64-encoded content
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("mimeType").GetString().ShouldBe("application/xml");
        doc.RootElement.GetProperty("fileName").GetString().ShouldBe("Invoice_INV2026005.isdoc");

        // Base64 decode must round-trip to the original bytes
        var base64 = doc.RootElement.GetProperty("base64Content").GetString()!;
        Convert.FromBase64String(base64).ShouldBe(xmlBytes);
    }

    [Fact]
    public async Task ExportInvoiceIsdoc_InvoiceNotFound_ReturnsErrorJson()
    {
        // Arrange — API client returns null for the invoice lookup (not found)
        var apiClient = Substitute.For<IFakvioApiClient>();
        var missingId = 777L;

        apiClient.GetInvoiceByIdAsync(missingId, Arg.Any<CancellationToken>())
            .Returns((InvoiceDto?)null);

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, missingId);

        // Assert — tool must report the error instead of throwing
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain(missingId.ToString());
    }

    [Fact]
    public async Task ExportInvoiceIsdoc_ApiThrows_ReturnsErrorJson()
    {
        // Arrange — simulates a network or service failure
        var apiClient = Substitute.For<IFakvioApiClient>();
        var invoiceId = 3L;

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, "INV003"));
        apiClient.ExportInvoiceIsdocAsync(invoiceId, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        // Assert — exceptions must be caught and returned as error JSON (never propagate to MCP host)
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExportInvoiceIsdoc_CreditNote_UsesCorrectFileName()
    {
        // Arrange — credit note should use "CreditNote_" prefix in the returned file name
        var apiClient = Substitute.For<IFakvioApiClient>();
        var invoiceId = 20L;
        var xmlBytes = "<?xml?>"u8.ToArray();

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, "CN2026001", EDocumentType.CreditNote));
        apiClient.ExportInvoiceIsdocAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(xmlBytes);

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("fileName").GetString().ShouldBe("CreditNote_CN2026001.isdoc");
    }
}
