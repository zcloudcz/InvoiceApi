# File Attachments — Architecture & Technical Plan

## 1. Overview

Add the ability to attach files (PDF, images, documents) to **any entity** in the system (Invoice, Client, ReceivedInvoice, etc.). The solution must be:

- **Entity-agnostic**: One table, one service, one component — works with any entity type
- **Storage-abstracted**: `IFileStorage` interface with swappable implementations
- **Multi-tenant**: Files are isolated per tenant (schema), blobs are isolated per tenant (container)
- **Configurable**: Azure Blob Storage connection string managed via SysAdmin System Settings (3-tier fallback pattern)
- **Reusable UI**: Single `<FileAttachmentManager>` Blazor component embeddable on any detail page

---

## 2. Domain Layer (`Fakvio.Domain`)

### 2.1 Entity: `FileAttachment`

```
Fakvio.Domain/Entities/FileAttachment.cs
```

Inherits `BaseEntity` (Id, CreatedAt, UpdatedAt, audit fields).

| Property | Type | Description |
|---|---|---|
| `EntityName` | `string` (required, max 100) | Discriminator — entity type name (e.g., `"Invoice"`, `"Client"`, `"ReceivedInvoice"`) |
| `RecordId` | `long` (required) | PK of the owning record in its table |
| `FileGuid` | `Guid` (required, default `NewGuid`) | Unique blob identifier — used as blob name in storage |
| `OriginalFileName` | `string` (required, max 500) | Original file name uploaded by user (e.g., `"contract.pdf"`) |
| `ContentType` | `string` (required, max 200) | MIME type (e.g., `"application/pdf"`, `"image/png"`) |
| `FileSizeBytes` | `long` | File size in bytes |
| `Description` | `string?` (max 500) | Optional user description / note |
| `BlobPath` | `string` (required, max 1000) | Full blob path in storage (e.g., `"Invoice/42/abc-123.pdf"`) |

**Composite Index**: `(EntityName, RecordId)` — fast lookup of all files for a given entity record.
**Unique Index**: `(FileGuid)` — ensures no duplicate blob references.

### 2.2 Enum (not needed)

No new enum required. `EntityName` is a free-form string matching entity class names. This is intentional — adding a new entity type doesn't require a code change or enum extension.

---

## 3. Application Layer (`Fakvio.Application`)

### 3.1 Interface: `IFileStorage`

```
Fakvio.Application/Service/IFileStorage.cs
```

Pure storage abstraction — knows nothing about `FileAttachment` entity or EF Core. Deals only in bytes, paths, and streams.

```csharp
public interface IFileStorage
{
    /// Upload file bytes to storage. Returns the blob path used.
    Task<string> UploadAsync(string containerName, string blobPath, byte[] content, string contentType, CancellationToken ct = default);

    /// Download file bytes from storage.
    Task<byte[]> DownloadAsync(string containerName, string blobPath, CancellationToken ct = default);

    /// Delete a file from storage.
    Task<bool> DeleteAsync(string containerName, string blobPath, CancellationToken ct = default);

    /// Check if a file exists in storage.
    Task<bool> ExistsAsync(string containerName, string blobPath, CancellationToken ct = default);
}
```

**Container naming convention**: `tenant-{companyId}` (e.g., `tenant-42`). Created lazily on first upload.

**Blob path convention**: `{EntityName}/{RecordId}/{FileGuid}{extension}` (e.g., `Invoice/42/a1b2c3d4.pdf`).

### 3.2 Interface: `IFileAttachmentService`

```
Fakvio.Application/Service/IFileAttachmentService.cs
```

Business logic layer — coordinates between EF Core (`FileAttachment` entity) and `IFileStorage`.

```csharp
public interface IFileAttachmentService
{
    /// Upload a file and create a FileAttachment record.
    Task<FileAttachmentDto> UploadAsync(FileAttachmentUploadDto upload, CancellationToken ct = default);

    /// Download file bytes by FileAttachment ID.
    Task<(byte[] Content, string FileName, string ContentType)> DownloadAsync(long attachmentId, CancellationToken ct = default);

    /// List all attachments for a given entity record.
    Task<List<FileAttachmentDto>> GetByEntityAsync(string entityName, long recordId, CancellationToken ct = default);

    /// Delete attachment (removes blob + DB record).
    Task<bool> DeleteAsync(long attachmentId, CancellationToken ct = default);
}
```

---

## 4. Contracts Layer (`Fakvio.Contracts`)

### 4.1 DTOs

```
Fakvio.Contracts/Dto/FileAttachment/FileAttachmentDto.cs
Fakvio.Contracts/Dto/FileAttachment/FileAttachmentUploadDto.cs
```

**FileAttachmentDto** (read — returned from API):

| Property | Type |
|---|---|
| `Id` | `long` |
| `EntityName` | `string` |
| `RecordId` | `long` |
| `FileGuid` | `Guid` |
| `OriginalFileName` | `string` |
| `ContentType` | `string` |
| `FileSizeBytes` | `long` |
| `Description` | `string?` |
| `CreatedAt` | `DateTime` |
| `CreatedByUserId` | `long?` |

**FileAttachmentUploadDto** (write — sent from UI):

| Property | Type |
|---|---|
| `EntityName` | `string` (required) |
| `RecordId` | `long` (required) |
| `FileName` | `string` (required) |
| `ContentType` | `string` (required) |
| `FileContent` | `byte[]` (required) |
| `Description` | `string?` |

---

## 5. Infrastructure Layer (`Fakvio.Infrastructure`)

### 5.1 `AzureBlobFileStorage` — implements `IFileStorage`

```
Fakvio.Infrastructure/Service/FileStorage/AzureBlobFileStorage.cs
```

- Uses `Azure.Storage.Blobs` NuGet package
- Resolves connection string from **3-tier fallback**: `CompanySystemSettings.AzureBlobConnectionString` → `SystemConfiguration.AzureBlobConnectionString` → `appsettings.json:AzureBlobStorage:ConnectionString`
- Container created lazily with `CreateIfNotExistsAsync()` (one-time per tenant)
- Uses `BlobServiceClient` → `BlobContainerClient` → `BlobClient` pattern
- Content-Disposition header set on upload for browser-friendly downloads

### 5.2 `FileAttachmentService` — implements `IFileAttachmentService`

```
Fakvio.Infrastructure/Service/FileAttachmentService.cs
```

- Injects `TenantDbContext`, `IFileStorage`, `ITenantResolver`, `ILogger`
- `UploadAsync`: generates `FileGuid`, builds blob path, calls `IFileStorage.UploadAsync`, saves `FileAttachment` entity
- `DownloadAsync`: loads `FileAttachment` by ID, calls `IFileStorage.DownloadAsync`
- `GetByEntityAsync`: queries `FileAttachment` where `EntityName == x && RecordId == y`, returns DTOs
- `DeleteAsync`: loads entity, calls `IFileStorage.DeleteAsync`, removes DB record

### 5.3 EF Core Configuration

**TenantDbContext changes**:
- Add `DbSet<FileAttachment> FileAttachment { get; set; }`
- Fluent config in `OnModelCreating`:
  - `EntityName`: required, max 100
  - `RecordId`: required
  - `FileGuid`: required, has default `Guid.NewGuid()`
  - `OriginalFileName`: required, max 500
  - `ContentType`: required, max 200
  - `BlobPath`: required, max 1000
  - Index on `(EntityName, RecordId)`
  - Unique index on `(FileGuid)`

**Migration**: `AddFileAttachment` migration for tenant schema.

### 5.4 Configuration Fields

**CompanySystemSettings** (add new properties — per-company override):

```csharp
// ─── Azure Blob Storage Settings ────────────────────────────────────────
public string? AzureBlobConnectionString { get; set; }  // Encrypted at rest
public string? AzureBlobContainerPrefix { get; set; }    // Override container prefix (default: "tenant")
```

**SystemConfiguration** (add new properties — system-wide fallback):

```csharp
// ─── Azure Blob Storage Settings ────────────────────────────────────────
public string? AzureBlobConnectionString { get; set; }  // Encrypted at rest
public string? AzureBlobContainerPrefix { get; set; }    // Override container prefix (default: "tenant")
```

This follows the exact same 3-tier fallback pattern as SMTP and AI settings.

### 5.5 DI Registration

In `ServiceCollectionExtensions.AddFakvioCore()`:

```csharp
// ── File Storage ───────────────────────────────────────────────────────
// Azure Blob Storage — primary file storage for tenant file attachments.
// Resolves connection string from CompanySystemSettings → SystemConfiguration → appsettings.json.
services.AddScopedWithLogging<IFileStorage, AzureBlobFileStorage>();
services.AddScopedWithLogging<IFileAttachmentService, FileAttachmentService>();
```

---

## 6. API Layer (`Fakvio.API`)

### 6.1 `FileAttachmentController`

```
Fakvio.API/Controller/FileAttachmentController.cs
```

```
[ApiController]
[Route("api/file-attachment")]
[Authorize]
```

| Endpoint | Method | Description |
|---|---|---|
| `POST /api/file-attachment/upload` | POST | Upload file (multipart/form-data) |
| `GET /api/file-attachment/{id}/download` | GET | Download file by attachment ID |
| `GET /api/file-attachment/{entityName}/{recordId}` | GET | List attachments for entity record |
| `DELETE /api/file-attachment/{id}` | DELETE | Delete attachment |

**Upload endpoint** accepts `IFormFile` + metadata (entityName, recordId, description) to support standard multipart file upload. Converts to `FileAttachmentUploadDto` internally.

**Download endpoint** returns `FileContentResult` with correct Content-Type and Content-Disposition headers.

**File size limit**: Configurable via `SystemConfiguration.MaxFileSizeBytes` (default 50MB). Enforced in controller before calling service.

---

## 7. UI Layer (`Fakvio.UI.Shared`)

### 7.1 `FileAttachmentApiService`

```
Fakvio.UI.Shared/Services/FileAttachmentApiService.cs
```

Extends `ApiClientBase`. Methods:

- `UploadAsync(string entityName, long recordId, IBrowserFile file, string? description)` — uses `MultipartFormDataContent` for file upload
- `DownloadAsync(long attachmentId)` — returns `byte[]` (uses `GetBytesAsync`)
- `GetByEntityAsync(string entityName, long recordId)` — returns `List<FileAttachmentDto>`
- `DeleteAsync(long attachmentId)` — returns `bool`

### 7.2 `FileAttachmentManager.razor` — Reusable Component

```
Fakvio.UI.Shared/Components/Shared/FileAttachmentManager.razor
```

**Parameters**:

```csharp
[Parameter] public string EntityName { get; set; } = string.Empty;   // e.g., "Invoice"
[Parameter] public long RecordId { get; set; }                       // e.g., 42
[Parameter] public bool ReadOnly { get; set; } = false;              // Hide upload/delete in read mode
```

**UI Layout** (MudBlazor components):

```
┌─────────────────────────────────────────────────────────────────┐
│  📎 Attachments (3)                                  [Upload]   │
├─────────────────────────────────────────────────────────────────┤
│  📄 contract.pdf          1.2 MB   2026-04-09   [⬇] [🗑]       │
│  🖼️ scan-front.jpg        340 KB   2026-04-08   [⬇] [🗑]       │
│  📄 invoice-original.pdf  890 KB   2026-04-07   [⬇] [🗑]       │
└─────────────────────────────────────────────────────────────────┘
```

- **Upload**: `MudFileUpload` with drag-and-drop support, multiple files, max size validation
- **List**: `MudTable` or `MudList` showing file name, size (formatted), date, download/delete actions
- **Download**: Triggers browser file download via JS interop (`URL.createObjectURL` + click)
- **Delete**: Confirmation dialog before deletion
- **Loading**: `MudProgressLinear` during upload/download
- **Localization**: All strings via `IStringLocalizer<SharedResource>` (CZ/EN)

### 7.3 Usage on Entity Detail Pages

Example — `InvoiceDetail.razor`:

```razor
@* File attachments section — works for any entity *@
<FileAttachmentManager EntityName="Invoice"
                       RecordId="_invoice.Id"
                       ReadOnly="@(!_isEditing)" />
```

Example — `ClientDetail.razor`:

```razor
<FileAttachmentManager EntityName="Client"
                       RecordId="_client.Id"
                       ReadOnly="@(!_isEditing)" />
```

No additional code needed on the page — the component is fully self-contained.

---

## 8. System Settings UI

### 8.1 SysAdmin — System Settings Page

Add "Azure Blob Storage" section to the existing `SystemSettings.razor`:

- **Connection String** — `MudTextField` (password mode), encrypted with `ICredentialProtector`
- **Container Prefix** — `MudTextField` (default "tenant")
- **Test Connection** button — calls `IFileStorage.ExistsAsync` with a test blob

### 8.2 Company Settings

Add "Azure Blob Storage" section to company settings (optional override):

- Same fields as system settings
- Empty = use system-wide settings (3-tier fallback)

---

## 9. Security Considerations

- **Tenant isolation**: Blob container per tenant (`tenant-42`). Service validates `ITenantResolver.GetCurrentCompanyId()` before any storage operation.
- **Authorization**: `[Authorize]` on all controller endpoints. Only authenticated users can upload/download.
- **File type validation**: Whitelist of allowed MIME types and extensions (configurable). Reject executables (.exe, .bat, .sh, etc.).
- **File size limit**: Configurable per system (default 50MB). Enforced at controller level before buffering.
- **Connection string encryption**: Stored encrypted via `ICredentialProtector` (same as SMTP passwords, AI API keys).
- **No direct blob URLs**: Files are always served through the API (controller proxies the download). No SAS tokens or public container access.

---

## 10. Testing

### 10.1 Unit Tests (`Fakvio.Tests.Unit`)

```
Tests/FileAttachment/FileAttachmentServiceTests.cs  — 8+ tests
Tests/FileAttachment/AzureBlobFileStorageTests.cs   — 5+ tests (mocked BlobServiceClient)
```

Test scenarios:
- Upload creates entity + calls storage
- Download returns correct bytes
- Delete removes entity + blob
- GetByEntity returns correct filtered list
- Upload with invalid entity name → validation error
- Upload exceeding max size → rejected
- Download non-existent attachment → returns null/error
- Tenant isolation (different companyId → different container)

### 10.2 Integration Tests

- Upload → Download round-trip with InMemoryDatabase + mock `IFileStorage`
- API endpoint integration via WebApplicationFactory

---

## 11. NuGet Packages

| Package | Project | Purpose |
|---|---|---|
| `Azure.Storage.Blobs` | Fakvio.Infrastructure | Azure Blob Storage SDK |

No additional packages needed — all other dependencies already exist.

---

## 12. Migration Checklist

EF Core migration for **TenantDbContext** (applied per tenant schema):

```
dotnet ef migrations add AddFileAttachment -c TenantDbContext -p Fakvio.Infrastructure -s Fakvio.API
```

Master DB migration for `CompanySystemSettings` + `SystemConfiguration` new columns:

```
dotnet ef migrations add AddBlobStorageSettings -c MasterDbContext -p Fakvio.Infrastructure -s Fakvio.API
```

---

## 13. Implementation Order

| Step | Layer | Files | Depends On |
|---|---|---|---|
| **1** | Domain | `FileAttachment.cs` entity | — |
| **2** | Application | `IFileStorage.cs`, `IFileAttachmentService.cs` | Step 1 |
| **3** | Contracts | `FileAttachmentDto.cs`, `FileAttachmentUploadDto.cs` | — |
| **4** | Domain | Add `AzureBlob*` properties to `CompanySystemSettings` + `SystemConfiguration` | — |
| **5** | Infrastructure | `TenantDbContext` — add DbSet + Fluent config | Step 1 |
| **6** | Infrastructure | EF Core migrations (tenant + master) | Steps 4, 5 |
| **7** | Infrastructure | `AzureBlobFileStorage.cs` | Step 2 |
| **8** | Infrastructure | `FileAttachmentService.cs` | Steps 2, 5, 7 |
| **9** | Infrastructure | DI registration in `ServiceCollectionExtensions` | Steps 7, 8 |
| **10** | API | `FileAttachmentController.cs` | Steps 2, 3 |
| **11** | UI | `FileAttachmentApiService.cs` | Step 3 |
| **12** | UI | `FileAttachmentManager.razor` component | Step 11 |
| **13** | UI | Add component to `InvoiceDetail`, `ClientDetail`, etc. | Step 12 |
| **14** | UI | System Settings + Company Settings UI sections | Steps 4, 11 |
| **15** | Tests | Unit tests + Integration tests | Steps 7, 8, 10 |
| **16** | Localization | Resource keys for CZ/EN | Step 12 |

---

## 14. Blob Path Examples

```
Container: tenant-42
├── Invoice/
│   ├── 101/
│   │   ├── a1b2c3d4-e5f6-7890-abcd-ef1234567890.pdf    ← contract.pdf
│   │   └── b2c3d4e5-f6a7-8901-bcde-f12345678901.jpg    ← scan-front.jpg
│   └── 102/
│       └── c3d4e5f6-a7b8-9012-cdef-123456789012.pdf    ← invoice-original.pdf
├── Client/
│   └── 55/
│       └── d4e5f6a7-b8c9-0123-defa-234567890123.pdf    ← registration-doc.pdf
└── ReceivedInvoice/
    └── 200/
        └── e5f6a7b8-c9d0-1234-efab-345678901234.pdf    ← scanned-invoice.pdf
```

---

## 15. Future Extensibility

The `IFileStorage` interface is deliberately simple and provider-agnostic. Future implementations can be added without changing the service or domain layer:

- **`LocalFileStorage`** — file system storage for development/testing
- **`S3FileStorage`** — Amazon S3
- **`MinioFileStorage`** — self-hosted S3-compatible storage

The `EntityName` + `RecordId` design means new entity types automatically support attachments — just drop `<FileAttachmentManager>` on the detail page.
