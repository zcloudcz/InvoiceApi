using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for AttachFileTool and ListAttachmentsTool.
///
/// Uses NSubstitute mocks for IFileAttachmentService.
/// Does NOT touch the database — verifies parameter validation, Base64 decoding,
/// service delegation, output formatting, and error handling.
/// </summary>
public class FileAttachmentChatToolTests
{
    // ─── Shared helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal FileAttachmentDto for use in test scenarios.
    /// </summary>
    private static FileAttachmentDto BuildAttachmentDto(
        long id = 1,
        string entityName = "Invoice",
        long recordId = 42,
        string fileName = "contract.pdf",
        string contentType = "application/pdf",
        long sizeBytes = 102400,
        string? description = null)
    {
        return new FileAttachmentDto
        {
            Id = id,
            EntityName = entityName,
            RecordId = recordId,
            FileGuid = Guid.NewGuid(),
            OriginalFileName = fileName,
            ContentType = contentType,
            FileSizeBytes = sizeBytes,
            Description = description,
            CreatedAt = new DateTime(2024, 4, 1, 12, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = 1
        };
    }

    /// <summary>
    /// Encodes a small test payload to Base64.
    /// </summary>
    private static string Base64Of(string text)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    // ═══════════════════════════════════════════════════════════════════════
    //  AttachFileTool — parameter validation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AttachFileTool — returns failure when file_content_base64 is not valid Base64.
    /// </summary>
    [Fact]
    public async Task AttachFileTool_InvalidBase64_ReturnsFailure()
    {
        var service = Substitute.For<IFileAttachmentService>();
        var tool = new AttachFileTool(service, Substitute.For<ILogger<AttachFileTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Invoice",
            ["record_id"] = "42",
            ["file_name"] = "doc.pdf",
            ["file_content_base64"] = "not-valid-base64!!!"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Base64");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  AttachFileTool — successful upload
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// AttachFileTool — calls UploadAsync with correct decoded bytes and returns success message.
    /// </summary>
    [Fact]
    public async Task AttachFileTool_ValidParameters_CallsUploadAndReturnsSuccess()
    {
        var dto = BuildAttachmentDto(id: 99, entityName: "Invoice", recordId: 42, sizeBytes: 5120);
        var service = Substitute.For<IFileAttachmentService>();
        service
            .UploadAsync(Arg.Any<FileAttachmentUploadDto>(), Arg.Any<CancellationToken>())
            .Returns(dto);

        var tool = new AttachFileTool(service, Substitute.For<ILogger<AttachFileTool>>());

        var fileBytes = System.Text.Encoding.UTF8.GetBytes("fake pdf content");
        var b64 = Convert.ToBase64String(fileBytes);

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Invoice",
            ["record_id"] = "42",
            ["file_name"] = "contract.pdf",
            ["file_content_base64"] = b64,
            ["content_type"] = "application/pdf",
            ["description"] = "Signed contract"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("99");                  // attachment ID
        result.OutputText.ShouldContain("contract.pdf");        // file name
        result.OutputText.ShouldContain("Invoice");             // entity
        result.OutputText.ShouldContain("42");                  // record ID
        result.OutputText.ShouldContain("application/pdf");     // content type

        // Verify service received decoded bytes (not the raw base64 string).
        await service.Received(1).UploadAsync(
            Arg.Is<FileAttachmentUploadDto>(u =>
                u.EntityName == "Invoice" &&
                u.RecordId == 42 &&
                u.FileName == "contract.pdf" &&
                u.ContentType == "application/pdf" &&
                u.Description == "Signed contract" &&
                u.FileContent.Length == fileBytes.Length),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AttachFileTool — entity_name is normalised to canonical casing regardless of AI input.
    /// E.g., "invoice" → "Invoice", "receivedinvoice" → "ReceivedInvoice".
    /// </summary>
    [Theory]
    [InlineData("invoice", "Invoice")]
    [InlineData("INVOICE", "Invoice")]
    [InlineData("receivedinvoice", "ReceivedInvoice")]
    [InlineData("RECEIVEDINVOICE", "ReceivedInvoice")]
    [InlineData("client", "Client")]
    [InlineData("CLIENT", "Client")]
    public async Task AttachFileTool_EntityNameNormalisation_CanonicalCasing(
        string inputEntityName, string expectedEntityName)
    {
        var dto = BuildAttachmentDto(entityName: expectedEntityName);
        var service = Substitute.For<IFileAttachmentService>();
        service
            .UploadAsync(Arg.Any<FileAttachmentUploadDto>(), Arg.Any<CancellationToken>())
            .Returns(dto);

        var tool = new AttachFileTool(service, Substitute.For<ILogger<AttachFileTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = inputEntityName,
            ["record_id"] = "1",
            ["file_name"] = "file.pdf",
            ["file_content_base64"] = Base64Of("data")
        });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).UploadAsync(
            Arg.Is<FileAttachmentUploadDto>(u => u.EntityName == expectedEntityName),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AttachFileTool — defaults content_type to application/octet-stream when not provided.
    /// </summary>
    [Fact]
    public async Task AttachFileTool_NoContentType_DefaultsToOctetStream()
    {
        var dto = BuildAttachmentDto(contentType: "application/octet-stream");
        var service = Substitute.For<IFileAttachmentService>();
        service
            .UploadAsync(Arg.Any<FileAttachmentUploadDto>(), Arg.Any<CancellationToken>())
            .Returns(dto);

        var tool = new AttachFileTool(service, Substitute.For<ILogger<AttachFileTool>>());

        await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Invoice",
            ["record_id"] = "1",
            ["file_name"] = "file.bin",
            ["file_content_base64"] = Base64Of("data")
            // content_type intentionally omitted
        });

        await service.Received(1).UploadAsync(
            Arg.Is<FileAttachmentUploadDto>(u => u.ContentType == "application/octet-stream"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AttachFileTool — size display uses MB for files >= 1 MB.
    /// </summary>
    [Fact]
    public async Task AttachFileTool_LargeFile_SizeDisplayedInMb()
    {
        var dto = BuildAttachmentDto(sizeBytes: 2 * 1024 * 1024);  // 2 MB
        var service = Substitute.For<IFileAttachmentService>();
        service
            .UploadAsync(Arg.Any<FileAttachmentUploadDto>(), Arg.Any<CancellationToken>())
            .Returns(dto);

        var tool = new AttachFileTool(service, Substitute.For<ILogger<AttachFileTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Invoice",
            ["record_id"] = "1",
            ["file_name"] = "big.pdf",
            ["file_content_base64"] = Base64Of("data")
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("MB");
    }

    /// <summary>
    /// AttachFileTool — size display uses KB for files smaller than 1 MB.
    /// </summary>
    [Fact]
    public async Task AttachFileTool_SmallFile_SizeDisplayedInKb()
    {
        var dto = BuildAttachmentDto(sizeBytes: 512 * 1024);  // 512 KB
        var service = Substitute.For<IFileAttachmentService>();
        service
            .UploadAsync(Arg.Any<FileAttachmentUploadDto>(), Arg.Any<CancellationToken>())
            .Returns(dto);

        var tool = new AttachFileTool(service, Substitute.For<ILogger<AttachFileTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Invoice",
            ["record_id"] = "1",
            ["file_name"] = "small.pdf",
            ["file_content_base64"] = Base64Of("data")
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("KB");
    }

    /// <summary>
    /// AttachFileTool — returns failure when UploadAsync throws an exception.
    /// </summary>
    [Fact]
    public async Task AttachFileTool_ServiceThrows_ReturnsFailure()
    {
        var service = Substitute.For<IFileAttachmentService>();
        service
            .UploadAsync(Arg.Any<FileAttachmentUploadDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Blob storage unavailable"));

        var tool = new AttachFileTool(service, Substitute.For<ILogger<AttachFileTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Invoice",
            ["record_id"] = "42",
            ["file_name"] = "doc.pdf",
            ["file_content_base64"] = Base64Of("data")
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Blob storage unavailable");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ListAttachmentsTool — parameter validation
    // ═══════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════
    //  ListAttachmentsTool — successful queries
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ListAttachmentsTool — returns informational message when entity has no attachments.
    /// </summary>
    [Fact]
    public async Task ListAttachmentsTool_NoAttachments_ReturnsNoAttachmentsMessage()
    {
        var service = Substitute.For<IFileAttachmentService>();
        service
            .GetByEntityAsync("Invoice", 42, Arg.Any<CancellationToken>())
            .Returns(new List<FileAttachmentDto>());

        var tool = new ListAttachmentsTool(service, Substitute.For<ILogger<ListAttachmentsTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Invoice",
            ["record_id"] = "42"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No attachments");
        result.OutputText.ShouldContain("Invoice");
        result.OutputText.ShouldContain("42");
    }

    /// <summary>
    /// ListAttachmentsTool — returns formatted list with name, size, date for each attachment.
    /// </summary>
    [Fact]
    public async Task ListAttachmentsTool_TwoAttachments_ReturnsFormattedList()
    {
        var attachments = new List<FileAttachmentDto>
        {
            BuildAttachmentDto(id: 1, fileName: "contract.pdf", sizeBytes: 102400, description: "Signed contract"),
            BuildAttachmentDto(id: 2, fileName: "receipt.jpg", sizeBytes: 51200, contentType: "image/jpeg")
        };

        var service = Substitute.For<IFileAttachmentService>();
        service
            .GetByEntityAsync("Invoice", 42, Arg.Any<CancellationToken>())
            .Returns(attachments);

        var tool = new ListAttachmentsTool(service, Substitute.For<ILogger<ListAttachmentsTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Invoice",
            ["record_id"] = "42"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("2 file");
        result.OutputText.ShouldContain("contract.pdf");
        result.OutputText.ShouldContain("receipt.jpg");
        result.OutputText.ShouldContain("Signed contract");      // description rendered
        result.OutputText.ShouldContain("application/pdf");
        result.OutputText.ShouldContain("image/jpeg");
    }

    /// <summary>
    /// ListAttachmentsTool — size display uses MB for large files, KB for small files.
    /// </summary>
    [Fact]
    public async Task ListAttachmentsTool_SizeDisplay_MbForLargeKbForSmall()
    {
        var attachments = new List<FileAttachmentDto>
        {
            BuildAttachmentDto(id: 1, fileName: "big.pdf", sizeBytes: 3 * 1024 * 1024),   // 3 MB
            BuildAttachmentDto(id: 2, fileName: "small.txt", sizeBytes: 200 * 1024)         // 200 KB
        };

        var service = Substitute.For<IFileAttachmentService>();
        service
            .GetByEntityAsync("Client", 7, Arg.Any<CancellationToken>())
            .Returns(attachments);

        var tool = new ListAttachmentsTool(service, Substitute.For<ILogger<ListAttachmentsTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Client",
            ["record_id"] = "7"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("MB");
        result.OutputText.ShouldContain("KB");
    }

    /// <summary>
    /// ListAttachmentsTool — entity_name is normalised to canonical casing.
    /// </summary>
    [Theory]
    [InlineData("receivedinvoice", "ReceivedInvoice")]
    [InlineData("RECEIVEDINVOICE", "ReceivedInvoice")]
    [InlineData("client", "Client")]
    public async Task ListAttachmentsTool_EntityNameNormalisation_CanonicalCasing(
        string inputEntityName, string expectedEntityName)
    {
        var service = Substitute.For<IFileAttachmentService>();
        service
            .GetByEntityAsync(expectedEntityName, 1, Arg.Any<CancellationToken>())
            .Returns(new List<FileAttachmentDto>());

        var tool = new ListAttachmentsTool(service, Substitute.For<ILogger<ListAttachmentsTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = inputEntityName,
            ["record_id"] = "1"
        });

        result.IsSuccess.ShouldBeTrue();

        // Verify the service was called with the normalised entity name.
        await service.Received(1).GetByEntityAsync(
            expectedEntityName, 1, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ListAttachmentsTool — returns failure when GetByEntityAsync throws an exception.
    /// </summary>
    [Fact]
    public async Task ListAttachmentsTool_ServiceThrows_ReturnsFailure()
    {
        var service = Substitute.For<IFileAttachmentService>();
        service
            .GetByEntityAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB connection lost"));

        var tool = new ListAttachmentsTool(service, Substitute.For<ILogger<ListAttachmentsTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["entity_name"] = "Invoice",
            ["record_id"] = "42"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("DB connection lost");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ChatToolExecutor — intent detection (Path 8)
    // ═══════════════════════════════════════════════════════════════════════

}
