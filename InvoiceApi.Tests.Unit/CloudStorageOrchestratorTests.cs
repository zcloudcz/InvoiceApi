using InvoiceApi.Contracts.Dto.CloudStorage;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service.CloudStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Unit tests for CloudStorageOrchestrator.
/// Verifies that the orchestrator correctly routes uploads to enabled providers,
/// handles failures gracefully, and aggregates provider statuses.
///
/// Dependencies:
/// - IExternalCloudStorage (mocked) — simulates Google Drive and OneDrive providers
/// - MasterDbContext (in-memory) — stores CompanySystemSettings
/// - ITenantResolver (mocked) — returns a fixed CompanyId
/// </summary>
public class CloudStorageOrchestratorTests : IDisposable
{
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly IExternalCloudStorage _googleDriveMock;
    private readonly IExternalCloudStorage _oneDriveMock;
    private readonly CloudStorageOrchestrator _orchestrator;
    private readonly ILogger<CloudStorageOrchestrator> _logger;

    private const long TestCompanyId = 42;

    public CloudStorageOrchestratorTests()
    {
        // In-memory master database for CompanySystemSettings
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _masterContext = new MasterDbContext(options);
        _logger = Substitute.For<ILogger<CloudStorageOrchestrator>>();
        _tenantResolver = Substitute.For<ITenantResolver>();
        _tenantResolver.GetCurrentCompanyId().Returns(TestCompanyId);

        // Mock provider implementations
        _googleDriveMock = Substitute.For<IExternalCloudStorage>();
        _googleDriveMock.Provider.Returns(ECloudStorageProvider.GoogleDrive);

        _oneDriveMock = Substitute.For<IExternalCloudStorage>();
        _oneDriveMock.Provider.Returns(ECloudStorageProvider.OneDrive);

        var providers = new List<IExternalCloudStorage> { _googleDriveMock, _oneDriveMock };

        _orchestrator = new CloudStorageOrchestrator(providers, _masterContext, _tenantResolver, _logger);
    }

    public void Dispose()
    {
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }

    /// <summary>
    /// Seeds CompanySystemSettings with the specified cloud storage settings.
    /// </summary>
    private void SeedSettings(
        bool? googleEnabled = null, string? googleRefreshToken = null,
        bool? onedriveEnabled = null, string? onedriveRefreshToken = null)
    {
        // Seed a Client (company) first — required by CompanySystemSettings FK
        _masterContext.Client.Add(new Client
        {
            Id = TestCompanyId,
            CompanyName = "Test Company",
            RegistrationNumber = "TEST-001",
            IsIssuer = true,
            IsActive = true
        });
        _masterContext.SaveChanges();

        _masterContext.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = TestCompanyId,
            SchemaName = "test_tenant_db",
            IsProvisioned = true,
            IsActive = true,
            GoogleDriveEnabled = googleEnabled,
            GoogleDriveRefreshToken = googleRefreshToken,
            OneDriveEnabled = onedriveEnabled,
            OneDriveRefreshToken = onedriveRefreshToken
        });
        _masterContext.SaveChanges();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UploadInvoicePdfAsync Tests
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When both Google Drive and OneDrive are enabled,
    /// the orchestrator should upload to BOTH providers.
    /// </summary>
    [Fact]
    public async Task UploadInvoicePdfAsync_BothEnabled_UploadsToBoth()
    {
        // Arrange — enable both providers
        SeedSettings(googleEnabled: true, googleRefreshToken: "g-token",
                     onedriveEnabled: true, onedriveRefreshToken: "o-token");

        _googleDriveMock.UploadFileAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CloudStorageFileResult { FileName = "test.pdf", FileId = "g-123" });
        _oneDriveMock.UploadFileAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CloudStorageFileResult { FileName = "test.pdf", FileId = "o-456" });

        // Act
        var results = await _orchestrator.UploadInvoicePdfAsync(1, new byte[] { 1, 2, 3 }, "test.pdf");

        // Assert — both uploads should succeed
        results.Count.ShouldBe(2);
        results.ShouldContain(r => r.FileId == "g-123");
        results.ShouldContain(r => r.FileId == "o-456");
    }

    /// <summary>
    /// When no providers are enabled, the orchestrator should return an empty list
    /// without attempting any uploads.
    /// </summary>
    [Fact]
    public async Task UploadInvoicePdfAsync_NoneEnabled_ReturnsEmpty()
    {
        // Arrange — no cloud storage enabled
        SeedSettings(googleEnabled: false, onedriveEnabled: false);

        // Act
        var results = await _orchestrator.UploadInvoicePdfAsync(1, new byte[] { 1, 2, 3 }, "test.pdf");

        // Assert — no uploads attempted
        results.ShouldBeEmpty();
        await _googleDriveMock.DidNotReceive()
            .UploadFileAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When one provider fails, the orchestrator should still upload to the other
    /// and return the successful result (failures are logged as warnings, not thrown).
    /// </summary>
    [Fact]
    public async Task UploadInvoicePdfAsync_OneProviderFails_OtherStillUploads()
    {
        // Arrange — both enabled, but Google Drive throws
        SeedSettings(googleEnabled: true, googleRefreshToken: "g-token",
                     onedriveEnabled: true, onedriveRefreshToken: "o-token");

        _googleDriveMock.UploadFileAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Google API error"));
        _oneDriveMock.UploadFileAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CloudStorageFileResult { FileName = "test.pdf", FileId = "o-789" });

        // Act — should NOT throw even though Google Drive failed
        var results = await _orchestrator.UploadInvoicePdfAsync(1, new byte[] { 1, 2, 3 }, "test.pdf");

        // Assert — only OneDrive result present
        results.Count.ShouldBe(1);
        results.First().FileId.ShouldBe("o-789");
    }

    // ═════════════════════════════════════════════════════════════════════════
    // GetProvidersStatusAsync Tests
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GetProvidersStatusAsync should return status for both providers,
    /// reflecting the current connection state from CompanySystemSettings.
    /// </summary>
    [Fact]
    public async Task GetProvidersStatusAsync_ReturnsStatusForBothProviders()
    {
        // Arrange — Google Drive connected, OneDrive not connected
        SeedSettings(googleEnabled: true, googleRefreshToken: "g-refresh-token",
                     onedriveEnabled: false);

        // Act
        var statuses = await _orchestrator.GetProvidersStatusAsync();

        // Assert — should have 2 entries (one per provider)
        statuses.Count.ShouldBe(2);

        var googleStatus = statuses.First(s => s.Provider == ECloudStorageProvider.GoogleDrive);
        googleStatus.IsConnected.ShouldBeTrue();

        var onedriveStatus = statuses.First(s => s.Provider == ECloudStorageProvider.OneDrive);
        onedriveStatus.IsConnected.ShouldBeFalse();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SetFolderAsync Tests
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SetFolderAsync should update the correct provider's folder in CompanySystemSettings.
    /// </summary>
    [Fact]
    public async Task SetFolderAsync_GoogleDrive_UpdatesSettings()
    {
        // Arrange
        SeedSettings(googleEnabled: true, googleRefreshToken: "token");

        var request = new CloudStorageSetFolderRequest
        {
            FolderId = "folder-abc-123",
            FolderName = "Invoices 2026"
        };

        // Act
        var result = await _orchestrator.SetFolderAsync(ECloudStorageProvider.GoogleDrive, request);

        // Assert
        result.ShouldBeTrue();

        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == TestCompanyId);
        settings.ShouldNotBeNull();
        settings.GoogleDriveFolderId.ShouldBe("folder-abc-123");
        settings.GoogleDriveFolderName.ShouldBe("Invoices 2026");
    }

    /// <summary>
    /// SetFolderAsync for OneDrive should update OneDrive-specific fields.
    /// </summary>
    [Fact]
    public async Task SetFolderAsync_OneDrive_UpdatesSettings()
    {
        // Arrange
        SeedSettings(onedriveEnabled: true, onedriveRefreshToken: "token");

        var request = new CloudStorageSetFolderRequest
        {
            FolderId = "onedrive-item-xyz",
            FolderName = "Company PDFs"
        };

        // Act
        var result = await _orchestrator.SetFolderAsync(ECloudStorageProvider.OneDrive, request);

        // Assert
        result.ShouldBeTrue();

        var settings = await _masterContext.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == TestCompanyId);
        settings.ShouldNotBeNull();
        settings.OneDriveFolderId.ShouldBe("onedrive-item-xyz");
        settings.OneDriveFolderName.ShouldBe("Company PDFs");
    }
}
