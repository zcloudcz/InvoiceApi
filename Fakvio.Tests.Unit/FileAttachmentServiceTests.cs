using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for FileAttachmentService.
/// Verifies upload, download, list, and delete operations against
/// an in-memory TenantDbContext and a mocked IFileStorage.
///
/// The IFileStorage mock simulates Azure Blob Storage without
/// making actual HTTP calls — tests run fast and are deterministic.
/// </summary>
public class FileAttachmentServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly IFileStorage _fileStorageMock;
    private readonly ITenantResolver _tenantResolverMock;
    private readonly FileAttachmentService _service;

    private const long TestCompanyId = 42;

    public FileAttachmentServiceTests()
    {
        // In-memory tenant database — each test gets a unique DB name for isolation
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        // Mock IFileStorage — captures upload/download/delete calls.
        // New IFileStorage signature has no container parameter; the implementation
        // resolves the shared container name internally from system settings.
        _fileStorageMock = Substitute.For<IFileStorage>();
        _fileStorageMock.UploadAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<string>(0)); // Returns blobPath (first arg)

        _fileStorageMock.DownloadAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 }); // Returns dummy bytes

        _fileStorageMock.DeleteAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        // Mock tenant resolver — returns a fixed CompanyId for container name resolution
        _tenantResolverMock = Substitute.For<ITenantResolver>();
        _tenantResolverMock.GetCurrentCompanyId().Returns(TestCompanyId);

        var logger = Substitute.For<ILogger<FileAttachmentService>>();

        _service = new FileAttachmentService(_context, _fileStorageMock, _tenantResolverMock, logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task UploadAsync_ShouldCreateAttachmentAndCallStorage()
    {
        // Arrange
        var upload = new FileAttachmentUploadDto
        {
            EntityName = "Invoice",
            RecordId = 101,
            FileName = "contract.pdf",
            ContentType = "application/pdf",
            FileContent = new byte[] { 0x25, 0x50, 0x44, 0x46 }, // %PDF
            Description = "Signed contract"
        };

        // Act
        var result = await _service.UploadAsync(upload);

        // Assert — DTO returned correctly
        result.ShouldNotBeNull();
        result.EntityName.ShouldBe("Invoice");
        result.RecordId.ShouldBe(101);
        result.OriginalFileName.ShouldBe("contract.pdf");
        result.ContentType.ShouldBe("application/pdf");
        result.FileSizeBytes.ShouldBe(4);
        result.Description.ShouldBe("Signed contract");
        result.FileGuid.ShouldNotBe(Guid.Empty);

        // Assert — DB record created with new "{companyId}/{guid}.ext" path format
        var dbRecord = await _context.FileAttachment.FirstOrDefaultAsync();
        dbRecord.ShouldNotBeNull();
        dbRecord.BlobPath.ShouldStartWith($"{TestCompanyId}/");
        dbRecord.BlobPath.ShouldEndWith(".pdf");

        // Assert — IFileStorage.UploadAsync was called once (no container arg anymore)
        await _fileStorageMock.Received(1).UploadAsync(
            Arg.Is<string>(p => p.StartsWith($"{TestCompanyId}/") && p.EndsWith(".pdf")),
            upload.FileContent,
            "application/pdf",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectFileTooLarge()
    {
        // Arrange — 51 MB file (exceeds 50 MB limit)
        var upload = new FileAttachmentUploadDto
        {
            EntityName = "Invoice",
            RecordId = 1,
            FileName = "huge.pdf",
            ContentType = "application/pdf",
            FileContent = new byte[51 * 1024 * 1024]
        };

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.UploadAsync(upload));
        ex.Message.ShouldContain("exceeds");
    }

    [Fact]
    public async Task UploadAsync_ShouldRejectDisallowedExtension()
    {
        // Arrange — .exe is not in the allowed extensions whitelist
        var upload = new FileAttachmentUploadDto
        {
            EntityName = "Invoice",
            RecordId = 1,
            FileName = "malware.exe",
            ContentType = "application/octet-stream",
            FileContent = new byte[] { 0x4D, 0x5A } // MZ header
        };

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.UploadAsync(upload));
        ex.Message.ShouldContain(".exe");
    }

    [Fact]
    public async Task DownloadAsync_ShouldReturnFileContent()
    {
        // Arrange — seed a FileAttachment record with new path format
        var attachment = new FileAttachment
        {
            EntityName = "Client",
            RecordId = 55,
            FileGuid = Guid.NewGuid(),
            OriginalFileName = "doc.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 3,
            BlobPath = $"{TestCompanyId}/test-guid.pdf",
            CreatedAt = DateTime.UtcNow
        };
        _context.FileAttachment.Add(attachment);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DownloadAsync(attachment.Id);

        // Assert
        result.ShouldNotBeNull();
        result.Value.Content.ShouldBe(new byte[] { 1, 2, 3 });
        result.Value.FileName.ShouldBe("doc.pdf");
        result.Value.ContentType.ShouldBe("application/pdf");
    }

    [Fact]
    public async Task DownloadAsync_ShouldReturnNullForMissingAttachment()
    {
        // Act
        var result = await _service.DownloadAsync(9999);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetByEntityAsync_ShouldReturnFilteredResults()
    {
        // Arrange — seed multiple attachments for different entities
        _context.FileAttachment.AddRange(
            new FileAttachment { EntityName = "Invoice", RecordId = 1, FileGuid = Guid.NewGuid(), OriginalFileName = "a.pdf", ContentType = "application/pdf", BlobPath = $"{TestCompanyId}/a.pdf", CreatedAt = DateTime.UtcNow },
            new FileAttachment { EntityName = "Invoice", RecordId = 1, FileGuid = Guid.NewGuid(), OriginalFileName = "b.pdf", ContentType = "application/pdf", BlobPath = $"{TestCompanyId}/b.pdf", CreatedAt = DateTime.UtcNow.AddMinutes(1) },
            new FileAttachment { EntityName = "Invoice", RecordId = 2, FileGuid = Guid.NewGuid(), OriginalFileName = "c.pdf", ContentType = "application/pdf", BlobPath = $"{TestCompanyId}/c.pdf", CreatedAt = DateTime.UtcNow },
            new FileAttachment { EntityName = "Client", RecordId = 1, FileGuid = Guid.NewGuid(), OriginalFileName = "d.pdf", ContentType = "application/pdf", BlobPath = $"{TestCompanyId}/d.pdf", CreatedAt = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        // Act — get attachments for Invoice #1
        var results = await _service.GetByEntityAsync("Invoice", 1);

        // Assert — only 2 files for Invoice #1, newest first
        results.Count.ShouldBe(2);
        results[0].OriginalFileName.ShouldBe("b.pdf"); // Newer file first
        results[1].OriginalFileName.ShouldBe("a.pdf");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveBlobAndDbRecord()
    {
        // Arrange
        var blobPath = $"{TestCompanyId}/old.pdf";
        var attachment = new FileAttachment
        {
            EntityName = "Invoice",
            RecordId = 1,
            FileGuid = Guid.NewGuid(),
            OriginalFileName = "old.pdf",
            ContentType = "application/pdf",
            BlobPath = blobPath,
            CreatedAt = DateTime.UtcNow
        };
        _context.FileAttachment.Add(attachment);
        await _context.SaveChangesAsync();

        // Act
        var deleted = await _service.DeleteAsync(attachment.Id);

        // Assert
        deleted.ShouldBeTrue();

        // DB record should be gone
        var dbRecord = await _context.FileAttachment.FindAsync(attachment.Id);
        dbRecord.ShouldBeNull();

        // IFileStorage.DeleteAsync should have been called with the exact stored blob path
        await _fileStorageMock.Received(1).DeleteAsync(
            blobPath,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnFalseForMissingAttachment()
    {
        // Act
        var deleted = await _service.DeleteAsync(9999);

        // Assert
        deleted.ShouldBeFalse();
    }

    [Fact]
    public async Task UploadAsync_ShouldThrowWhenNoCompanyContext()
    {
        // Arrange — no company context (anonymous user)
        _tenantResolverMock.GetCurrentCompanyId().Returns((long?)null);

        var upload = new FileAttachmentUploadDto
        {
            EntityName = "Invoice",
            RecordId = 1,
            FileName = "test.pdf",
            ContentType = "application/pdf",
            FileContent = new byte[] { 1 }
        };

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.UploadAsync(upload));
    }
}
