using System.IO.Compression;
using System.Net;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for attachment download endpoints:
///   - GET /api/file-attachment/{entityName}/{recordId}/download-all (per-record ZIP)
///   - GET /api/received-invoice/bulk/attachments?ids=... (bulk ZIP, folder per invoice)
///
/// Blob storage is replaced by an in-memory dictionary (InMemoryFileStorage) —
/// the tests exercise the full HTTP + service + EF pipeline, only the Azure
/// Blob dependency is faked.
/// </summary>
public class ReceivedInvoiceAttachmentEndpointTests : IClassFixture<ReceivedInvoiceAttachmentEndpointTests.AttachmentFactory>
{
    /// <summary>
    /// In-memory IFileStorage — blob path → bytes. Deterministic, no network.
    /// </summary>
    public class InMemoryFileStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _blobs = new();

        public Task<string> UploadAsync(string blobPath, byte[] content, string contentType, CancellationToken ct = default)
        {
            _blobs[blobPath] = content;
            return Task.FromResult(blobPath);
        }

        public Task<byte[]> DownloadAsync(string blobPath, CancellationToken ct = default)
            => _blobs.TryGetValue(blobPath, out var bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException($"Blob not found: {blobPath}");

        public Task<bool> DeleteAsync(string blobPath, CancellationToken ct = default)
            => Task.FromResult(_blobs.Remove(blobPath));

        public Task<bool> ExistsAsync(string blobPath, CancellationToken ct = default)
            => Task.FromResult(_blobs.ContainsKey(blobPath));

        public void Seed(string blobPath, byte[] content) => _blobs[blobPath] = content;
    }

    /// <summary>
    /// FakvioFactory variant with IFileStorage replaced by the in-memory fake.
    /// Singleton so tests can seed blobs through the same instance the API uses.
    /// </summary>
    public class AttachmentFactory : FakvioFactory
    {
        public InMemoryFileStorage FileStorage { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IFileStorage>();
                services.AddSingleton<IFileStorage>(FileStorage);
            });
        }
    }

    private readonly AttachmentFactory _factory;

    private const long CompanyId = 8800L;
    private const long SupplierId = 8801L;
    private const long CurrencyId = 1L; // CZK seeded by InitializeDatabase()

    public ReceivedInvoiceAttachmentEndpointTests(AttachmentFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        SeedTenant();
    }

    // ─── Seed helpers ────────────────────────────────────────────────────────

    private void SeedTenant()
    {
        using var scope = _factory.Services.CreateScope();
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        if (!masterDb.Client.Any(c => c.Id == CompanyId))
        {
            masterDb.Client.Add(new Client
            {
                Id = CompanyId, CompanyName = "Attachment Test Co",
                RegistrationNumber = "AT000001", IsIssuer = true, IsActive = true
            });
        }
        if (!masterDb.CompanySystemSettings.Any(s => s.CompanyId == CompanyId))
        {
            masterDb.CompanySystemSettings.Add(new CompanySystemSettings
            {
                CompanyId = CompanyId, SchemaName = $"tenant_att_{CompanyId}",
                IsProvisioned = true, IsActive = true
            });
        }
        masterDb.SaveChanges();

        if (!tenantDb.Client.Any(c => c.Id == SupplierId))
        {
            tenantDb.Client.Add(new Client
            {
                Id = SupplierId, CompanyName = "Attachment Supplier",
                RegistrationNumber = "AT000002", IsIssuer = false, IsActive = true
            });
            tenantDb.SaveChanges();
        }
    }

    /// <summary>Seeds a received invoice and returns its id.</summary>
    private long SeedReceivedInvoice(string documentNumber)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();

        var invoice = new ReceivedInvoice
        {
            DocumentNumber = documentNumber,
            Status = EReceivedInvoiceStatus.Received,
            SupplierId = SupplierId,
            CurrencyId = CurrencyId,
            IssueDate = DateTime.UtcNow.AddDays(-5),
            ReceivedDate = DateTime.UtcNow,
            TotalBeforeVat = 100m, TotalVat = 21m, TotalWithVat = 121m
        };
        tenantDb.ReceivedInvoice.Add(invoice);
        tenantDb.SaveChanges();
        return invoice.Id;
    }

    /// <summary>
    /// Seeds one FileAttachment metadata row + the matching in-memory blob.
    /// </summary>
    private void SeedAttachment(long recordId, string fileName, byte[] content)
    {
        var blobPath = $"{CompanyId}/{Guid.NewGuid()}{Path.GetExtension(fileName)}";
        _factory.FileStorage.Seed(blobPath, content);

        using var scope = _factory.Services.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        tenantDb.FileAttachment.Add(new FileAttachment
        {
            EntityName = nameof(ReceivedInvoice),
            RecordId = recordId,
            OriginalFileName = fileName,
            ContentType = "application/pdf",
            FileSizeBytes = content.Length,
            BlobPath = blobPath
        });
        tenantDb.SaveChanges();
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        AuthHelper.SetImpersonation(client, CompanyId);
        return client;
    }

    private static List<string> ZipEntryNames(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    // ─── download-all (per record) ───────────────────────────────────────────

    [Fact]
    public async Task DownloadAll_TwoAttachments_ReturnsZipWithBothEntries()
    {
        var invoiceId = SeedReceivedInvoice("ATT-2026-001");
        SeedAttachment(invoiceId, "scan.pdf", new byte[] { 1, 2, 3 });
        SeedAttachment(invoiceId, "photo.pdf", new byte[] { 4, 5 });

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/file-attachment/ReceivedInvoice/{invoiceId}/download-all");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");

        var entries = ZipEntryNames(await response.Content.ReadAsByteArrayAsync());
        entries.Count.ShouldBe(2);
        entries.ShouldContain("scan.pdf");
        entries.ShouldContain("photo.pdf");
    }

    [Fact]
    public async Task DownloadAll_DuplicateFileNames_AreDeduplicatedInZip()
    {
        var invoiceId = SeedReceivedInvoice("ATT-2026-002");
        SeedAttachment(invoiceId, "scan.pdf", new byte[] { 1 });
        SeedAttachment(invoiceId, "scan.pdf", new byte[] { 2 });

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/file-attachment/ReceivedInvoice/{invoiceId}/download-all");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entries = ZipEntryNames(await response.Content.ReadAsByteArrayAsync());
        entries.Count.ShouldBe(2);
        entries.ShouldContain("scan.pdf");
        entries.ShouldContain("scan (2).pdf");
    }

    [Fact]
    public async Task DownloadAll_RecordWithoutAttachments_Returns404()
    {
        var invoiceId = SeedReceivedInvoice("ATT-2026-003");

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/file-attachment/ReceivedInvoice/{invoiceId}/download-all");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ─── bulk attachments ────────────────────────────────────────────────────

    [Fact]
    public async Task Bulk_FolderPerInvoice_SkipsInvoicesWithoutAttachments_AndUnknownIds()
    {
        var withAttachments = SeedReceivedInvoice("BULK-2026-001");
        SeedAttachment(withAttachments, "a.pdf", new byte[] { 1 });
        SeedAttachment(withAttachments, "b.pdf", new byte[] { 2 });
        var withoutAttachments = SeedReceivedInvoice("BULK-2026-002");
        const long unknownId = 999_999;

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync(
            $"/api/received-invoice/bulk/attachments?ids={withAttachments},{withoutAttachments},{unknownId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entries = ZipEntryNames(await response.Content.ReadAsByteArrayAsync());

        // Folder named by document number; invoice without attachments + unknown id skipped
        entries.Count.ShouldBe(2);
        entries.ShouldContain("BULK-2026-001/a.pdf");
        entries.ShouldContain("BULK-2026-001/b.pdf");
        entries.ShouldAllBe(e => e.StartsWith("BULK-2026-001/"));
    }

    [Fact]
    public async Task Bulk_NoAttachmentsAnywhere_Returns404()
    {
        var invoiceId = SeedReceivedInvoice("BULK-2026-003");

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/received-invoice/bulk/attachments?ids={invoiceId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Bulk_TooManyIds_Returns400()
    {
        var ids = string.Join(',', Enumerable.Range(1, 101));

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/api/received-invoice/bulk/attachments?ids={ids}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bulk_InvalidIds_Returns400()
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/received-invoice/bulk/attachments?ids=abc,,");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
