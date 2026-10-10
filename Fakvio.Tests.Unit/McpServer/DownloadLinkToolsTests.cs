using System.Text.Json;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// MCP tools that hand out signed download links (attachments, received invoices, EPO filings).
/// The invoice/accounting export tools are covered in InvoiceToolsTests.
/// </summary>
public class DownloadLinkToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    public DownloadLinkToolsTests()
    {
        _api.CreateDownloadLinkAsync(Arg.Any<CreateDownloadLinkDto>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadLinkDto { Url = "https://api.example/api/download/tok", ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
    }

    private static void ShouldBeLinkWithoutBytes(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("success").GetBoolean().ShouldBeTrue();
        root.GetProperty("downloadUrl").GetString().ShouldBe("https://api.example/api/download/tok");
        root.TryGetProperty("base64Content", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ListAttachments_ReturnsMetadata()
    {
        _api.GetFileAttachmentsAsync("ReceivedInvoice", 9, Arg.Any<CancellationToken>())
            .Returns([new FileAttachmentDto { Id = 5, OriginalFileName = "scan.pdf", ContentType = "application/pdf", FileSizeBytes = 1234 }]);

        var json = await AttachmentTools.ListAttachments(_api, "ReceivedInvoice", 9);

        var first = JsonDocument.Parse(json).RootElement[0];
        first.GetProperty("id").GetInt64().ShouldBe(5);
        first.GetProperty("originalFileName").GetString().ShouldBe("scan.pdf");
    }

    [Fact]
    public async Task GetAttachmentDownloadLink_RequestsAttachmentFileKind()
    {
        var json = await AttachmentTools.GetAttachmentDownloadLink(_api, 5);

        ShouldBeLinkWithoutBytes(json);
        await _api.Received(1).CreateDownloadLinkAsync(
            Arg.Is<CreateDownloadLinkDto>(r => r.Kind == EDownloadKind.AttachmentFile && r.Id == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRecordAttachmentsZipLink_RequestsZipKindForRecord()
    {
        var json = await AttachmentTools.GetRecordAttachmentsZipLink(_api, "Invoice", 11);

        ShouldBeLinkWithoutBytes(json);
        JsonDocument.Parse(json).RootElement.GetProperty("mimeType").GetString().ShouldBe("application/zip");
        await _api.Received(1).CreateDownloadLinkAsync(
            Arg.Is<CreateDownloadLinkDto>(r => r.Kind == EDownloadKind.AttachmentsZip && r.EntityName == "Invoice" && r.Id == 11),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportReceivedInvoiceIsdoc_RequestsReceivedInvoiceIsdocKind()
    {
        var json = await ReceivedInvoiceTools.ExportReceivedInvoiceIsdoc(_api, 7);

        ShouldBeLinkWithoutBytes(json);
        await _api.Received(1).CreateDownloadLinkAsync(
            Arg.Is<CreateDownloadLinkDto>(r => r.Kind == EDownloadKind.ReceivedInvoiceIsdoc && r.Id == 7),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadReceivedInvoicesAttachments_PassesIds()
    {
        var json = await ReceivedInvoiceTools.DownloadReceivedInvoicesAttachments(_api, [3, 4]);

        ShouldBeLinkWithoutBytes(json);
        await _api.Received(1).CreateDownloadLinkAsync(
            Arg.Is<CreateDownloadLinkDto>(r => r.Kind == EDownloadKind.ReceivedInvoiceBulkAttachments && r.Ids!.SequenceEqual(new long[] { 3, 4 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportVatEpo_ReturnsLinkWithKnownFileName()
    {
        var json = await ReportingTools.ExportVatEpo(_api, "control", 2026, 3, "Monthly");

        ShouldBeLinkWithoutBytes(json);
        JsonDocument.Parse(json).RootElement.GetProperty("fileName").GetString().ShouldBe("DPHKH1_2026_M03.xml");
        await _api.Received(1).CreateDownloadLinkAsync(
            Arg.Is<CreateDownloadLinkDto>(r => r.Kind == EDownloadKind.VatEpoControlStatement && r.Year == 2026 && r.Period == 3 && r.PeriodType == EVatPeriodType.Monthly),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApiFailure_IsReturnedAsErrorJson_NotThrown()
    {
        _api.CreateDownloadLinkAsync(Arg.Any<CreateDownloadLinkDto>(), Arg.Any<CancellationToken>())
            .Returns<DownloadLinkDto>(_ => throw new HttpRequestException("boom"));

        var json = await AttachmentTools.GetAttachmentDownloadLink(_api, 5);

        JsonDocument.Parse(json).RootElement.TryGetProperty("error", out _).ShouldBeTrue();
    }
}
