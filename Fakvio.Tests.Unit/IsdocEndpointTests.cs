using Fakvio.Contracts.Common;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.Readiness;
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
    private readonly IDocumentFileNameService _fileNameService = Substitute.For<IDocumentFileNameService>();
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
            _fileNameService,
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
        // The controller must hand out exactly the name the file name service resolves
        _fileNameService.GetFileNameAsync(invoiceId, DocumentFileName.IsdocExtension, Arg.Any<CancellationToken>())
            .Returns("Custom_INV2026001.isdoc");

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

        fileResult.FileDownloadName.ShouldBe("Custom_INV2026001.isdoc");
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
    public async Task ExportInvoiceIsdoc_ReturnsDownloadLink_NotFileContent()
    {
        // Arrange - the tool must ask the API for a signed link instead of downloading the bytes
        var apiClient = Substitute.For<IFakvioApiClient>();
        var invoiceId = 5L;
        var expires = DateTime.UtcNow.AddMinutes(10);

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, "INV2026005"));
        apiClient.CreateDownloadLinkAsync(Arg.Any<CreateDownloadLinkDto>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadLinkDto { Url = "https://api.example/api/download/tok", ExpiresAt = expires });

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("mimeType").GetString().ShouldBe("application/xml");
        doc.RootElement.GetProperty("downloadUrl").GetString().ShouldBe("https://api.example/api/download/tok");
        doc.RootElement.TryGetProperty("expiresAt", out _).ShouldBeTrue();
        doc.RootElement.TryGetProperty("base64Content", out _).ShouldBeFalse();
        await apiClient.Received(1).CreateDownloadLinkAsync(
            Arg.Is<CreateDownloadLinkDto>(r => r.Kind == EDownloadKind.InvoiceIsdoc && r.Id == invoiceId),
            Arg.Any<CancellationToken>());
        await apiClient.DidNotReceive().ExportInvoiceIsdocAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
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
        apiClient.CreateDownloadLinkAsync(Arg.Any<CreateDownloadLinkDto>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        // Act
        var json = await InvoiceTools.ExportInvoiceIsdoc(apiClient, invoiceId);

        // Assert — exceptions must be caught and returned as error JSON (never propagate to MCP host)
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
    }


    // ── MCP tool tests — ExportInvoiceUbl (ADR 0002, F1.7) ─────────────────────

    [Fact]
    public async Task ExportInvoiceUbl_ReturnsDownloadLink_NotFileContent()
    {
        var apiClient = Substitute.For<IFakvioApiClient>();
        var invoiceId = 5L;

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, "INV2026005"));
        apiClient.CreateDownloadLinkAsync(Arg.Any<CreateDownloadLinkDto>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadLinkDto { Url = "https://api.example/api/download/tok", ExpiresAt = DateTime.UtcNow.AddMinutes(10) });

        var json = await InvoiceTools.ExportInvoiceUbl(apiClient, invoiceId);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("mimeType").GetString().ShouldBe("application/xml");
        doc.RootElement.GetProperty("downloadUrl").GetString().ShouldBe("https://api.example/api/download/tok");
        doc.RootElement.TryGetProperty("base64Content", out _).ShouldBeFalse();
        await apiClient.Received(1).CreateDownloadLinkAsync(
            Arg.Is<CreateDownloadLinkDto>(r => r.Kind == EDownloadKind.InvoiceUbl && r.Id == invoiceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportInvoiceUbl_InvoiceNotFound_ReturnsErrorJson()
    {
        var apiClient = Substitute.For<IFakvioApiClient>();
        var missingId = 777L;

        apiClient.GetInvoiceByIdAsync(missingId, Arg.Any<CancellationToken>())
            .Returns((InvoiceDto?)null);

        var json = await InvoiceTools.ExportInvoiceUbl(apiClient, missingId);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain(missingId.ToString());
    }

    [Fact]
    public async Task ExportInvoiceUbl_TenantNotReady_ReturnsStructuredIssues_NotStackTrace()
    {
        // Simulates the API's 400 TENANT_NOT_READY response — FakvioApiClient.EnsureSuccessAsync
        // parses that shape into TenantNotReadyApiException (mirrors CompleteInvoice, #342) so
        // the tool can hand back the readable EINVOICE_* codes instead of a stack trace.
        // The readiness check now runs when the link is minted (POST /api/download-links), so
        // that is the call that is refused.
        var apiClient = Substitute.For<IFakvioApiClient>();
        var invoiceId = 8L;

        apiClient.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, "INV2026008"));
        apiClient.CreateDownloadLinkAsync(Arg.Any<CreateDownloadLinkDto>(), Arg.Any<CancellationToken>())
            .Throws(new TenantNotReadyApiException(
                "Tenant is not ready. Unresolved blocking issue(s): EINVOICE_DRAFT.",
                missingFields: [],
                issues: [new ReadinessIssueDto { Code = "EINVOICE_DRAFT", Severity = EReadinessSeverity.Blocking }]));

        var json = await InvoiceTools.ExportInvoiceUbl(apiClient, invoiceId);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("TENANT_NOT_READY");
        doc.RootElement.GetProperty("issues")[0].GetProperty("code").GetString().ShouldBe("EINVOICE_DRAFT");
        doc.RootElement.TryGetProperty("stackTrace", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("downloadUrl", out _).ShouldBeFalse();
    }

}
