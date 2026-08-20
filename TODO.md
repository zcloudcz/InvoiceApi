# Fakvio TODO

## Completed (2026-04-09)

### File Attachments — Entity-Agnostic File Upload System ✅
**Feature:** Attach files (PDF, images, documents) to any entity in the system.

**Implemented:**
- [x] `FileAttachment` entity (Domain) — polymorphic FK via EntityName + RecordId + FileGuid
- [x] `IFileStorage` interface (Application) — pure storage abstraction (upload/download/delete/exists)
- [x] `IFileAttachmentService` interface (Application) — business logic layer
- [x] `FileAttachmentDto` + `FileAttachmentUploadDto` (Contracts)
- [x] `AzureBlobFileStorage` (Infrastructure) — Azure Blob Storage with 3-tier connection string fallback
- [x] `FileAttachmentService` (Infrastructure) — coordinates EF + blob storage, file type whitelist, 50MB limit
- [x] `FileAttachmentController` (API) — multipart upload, download, list, delete endpoints
- [x] `FileAttachmentApiService` (UI.Shared) — Blazor API service with multipart upload
- [x] `FileAttachmentManager.razor` (UI.Shared) — reusable MudBlazor component (drag-and-drop, download, delete)
- [x] Integrated into InvoiceDetail, ClientDetail, ReceivedInvoiceDetail pages
- [x] Azure Blob settings added to CompanySystemSettings + SystemConfiguration (encrypted)
- [x] System Settings UI section for Azure Blob Storage configuration
- [x] CZ/EN localization (25 resource keys)
- [x] 9 unit tests (FileAttachmentServiceTests)
- [x] EF migrations: `AddFileAttachment` (tenant) + `AddBlobStorageSettings` (master)
- [x] DI registration in ServiceCollectionExtensions

---

## Completed (2026-04-16)

### Payment Reminders (Dunning / Upomínky) ✅
**Feature:** Automatic payment reminder system for overdue invoices with multi-level escalation.

**Implemented:**
- [x] Domain entities: `ReminderSettings`, `ReminderLevel`, `Reminder` + `EReminderStatus` enum
- [x] `ReminderPdf = 3` added to `EContentTemplateType`
- [x] DTOs: `ReminderDto`, `ReminderFilterDto`, `ReminderSettingsDto`, `UpdateReminderSettingsDto`, `ReminderLevelDto`, `UpdateReminderLevelDto`, `ReminderDashboardDto`
- [x] `IReminderService` + `IInterestCalculator` interfaces (Application)
- [x] `InterestCalculator` — statutory late payment interest per § 1970 OZ (CNB repo rate + 8 p.p.)
- [x] `ReminderService` — settings CRUD, dunning job, send/cancel, dashboard data
- [x] `ReminderProfile` ZMapper mapping
- [x] TenantDbContext: 3 DbSets + Fluent API config + `AddReminders` migration
- [x] DI registration (Scoped + Singleton)
- [x] `ReminderController` — 8 API endpoints (settings + reminders + dashboard)
- [x] `ReminderFunctions` — Azure Functions TimerTrigger (`ProcessReminders`, daily 6 AM UTC)
- [x] `ReminderApiService` (UI.Shared) — Blazor API client
- [x] `/reminders` page — MudDataGrid with bulk actions, column filters, multi-select
- [x] `/reminders/settings` page — company defaults + client overrides management
- [x] Dashboard widget — KPI chips + recent reminders table on Home.razor
- [x] NavMenu links (Reminders + Credit Notes)
- [x] CZ/EN localization (~65 resource keys)
- [x] InterestCalculatorTests (10 tests) + ReminderServiceTests (17 tests)

---

## Pending

### SysAdmin master code table management (Architecture Decision Needed)
After removing the dual-context pattern (IsMasterContext) from services, SysAdmin currently has no way to manage master-level code tables (VatRate, ContentTemplate, NumberSequenceFormat). These master records serve as **templates for new tenant provisioning** (CopyCodeTablesAsync copies them).

**Options:**
1. **Dedicated SysAdmin endpoints** — separate controller/service for master code table CRUD (e.g., `SysAdminCodeTableController` using `MasterDbContext` directly)
2. **Explicit `useMaster` flag** — add parameter to existing service methods, gated by SysAdmin role check

**Scope:** VatRate, ContentTemplate, NumberSequenceFormat only. Currency already reads from master (global/shared).

---

### Dual-context pattern removed — data isolation fix (2026-03-31) ✅
**Bug:** `NumberSequenceService.CreateSequenceAsync` threw `InvalidOperationException: Number sequence format with ID 1 not found` in production (Azure Functions). Root cause: dual-context pattern (`IsMasterContext`) caused `GetAllFormatsAsync` to read formats from **master DB** while `CreateSequenceAsync` validated against **tenant DB** — ID mismatch.

**Fix applied:**
- [x] `NumberSequenceService` — always tenant DB (formats + sequences are per-tenant)
- [x] `VatRateService` — always tenant DB (VAT rates are per-tenant)
- [x] `ContentTemplateService` — always tenant DB (templates are per-tenant)
- [x] `CurrencyService` — always master DB (currencies are global/shared, tenant table exists only for FK integrity)
- [x] `CurrencyServiceTests` — seed data moved to master context
- [x] `EnsureDefaultFormatsExistAsync` — added PG sequence reset to prevent ID gaps after re-seeding

**Verified:** Full document number generation chain is consistent (Invoice → NumberSequence → NumberSequenceFormat — all tenant DB). `BillingSettings.CustomInvoiceNumberSequenceId` / `CustomCreditNoteNumberSequenceId` correctly reference tenant `NumberSequence.Id`.

---

### Eliminate IHttpContextAccessor dependency from services (Tech Debt)
Azure Functions Isolated Worker has two DI scopes per request (`httpContext.RequestServices` vs `context.InstanceServices`). `IHttpContextAccessor` returns null in the worker scope where injected services live. Current workaround: `ChatService` falls back to parsing CompanyId from `TenantDbContext.Schema` (`"tenant_42"` → `42`).

**Clean solution:** Pass `companyId` explicitly through the entire call chain instead of relying on `IHttpContextAccessor`/`ITenantResolver`:
- [ ] Add `companyId` parameter to `IChatService` methods (`SendMessageAsync`, `StreamMessageAsync`, `GetAvailableProvidersAsync`)
- [ ] `ChatController` reads CompanyId from JWT claims and passes it down
- [ ] Remove `ITenantResolver` dependency from `ChatService`
- [ ] Remove `ParseCompanyIdFromSchema` workaround
- [ ] Audit other services that use `ITenantResolver` — same problem may exist (EmailService, etc.)
- [ ] Consider removing `ITenantResolver` entirely if all callers can pass CompanyId explicitly

**Why:** `IHttpContextAccessor` is unreliable in Azure Functions dual-scope architecture. Explicit parameter passing is safer, testable, and doesn't depend on ambient state.

---

### Multi-page document support (PDF templates)
Current state: `PdfExportService` renders the entire HTML template as a single block via iText7 `HtmlConverter.ConvertToPdf` — no pagination control, no repeating headers/footers.

**Goal:** Professional multi-page invoices with repeating header/footer and proper page numbering.

**Template structure — 3 sections:**
- [ ] `{{#header}}` section — repeats on every page (company logo, document number, issuer/client info)
- [ ] `{{#body}}` section — main content (invoice items table), auto-flows across pages
- [ ] `{{#footer}}` section — repeats at bottom of every page (page number, QR code, signature, bank details)

**Backend (PdfExportService + iText7):**
- [ ] Parse template HTML into 3 sections (header/body/footer) before rendering
- [ ] Implement iText7 `IEventHandler` for repeating header/footer on each page
- [ ] CSS `@page` rules for margins (reserve space for header/footer)
- [ ] `{{PageNumber}}` / `{{TotalPages}}` placeholders (two-pass rendering or iText7 page events)
- [ ] Invoice items table: auto-overflow with repeated `<thead>` on each page

**Template editor UI:**
- [ ] Split editor into 3 tabs/sections (Header / Body / Footer)
- [ ] Live preview of page layout
- [ ] Migration: existing single-body templates treated as "body only" (backwards compatible)

**Database:**
- [ ] Add `HeaderHtml` and `FooterHtml` columns to `ContentTemplate` entity (nullable — backwards compatible)
- [ ] Migration for existing templates

---

### AI Chat: export_invoice tool + download action type (2026-03-27)
- [x] New `ChatUiAction.Download(url, fileName, mimeType)` factory — extends UI action system beyond "navigate"
- [x] New `ExportInvoiceTool` (6th chat tool) — finds invoice by document number or client name, returns download action
- [x] Export regex detection pattern in `ChatToolExecutor` (Czech + English: stáhni/exportuj/download fakturu)
- [x] `ChatPanel.razor` handles `download` action type — fetches PDF bytes via API, triggers browser download via JS interop
- [x] `ChatApiService.DownloadFileAsync(url)` — authenticated binary file download
- [x] `ApiClientBase.GetStringAsync(endpoint)` — raw string HTTP response
- [x] Updated `ChatContextBuilder` and `BuildToolInstructions` with export_invoice tool description + examples
- [x] DI registration for `ExportInvoiceTool`
- [x] 10 unit tests (ExportInvoiceToolTests) — all passing
- [x] MCP Server: `ExportInvoicePdf` tool (returns base64 PDF) + `IFakvioApiClient.ExportInvoicePdfAsync`
- [x] 3 MCP unit tests (InvoiceToolsTests) — ExportInvoicePdf success, credit note prefix, not found

### Template download/upload + preview (2026-03-26)
- [x] Download HTML body as `.html` file (detail page + list page)
- [x] Upload `.html`/`.htm` file to replace template body (detail page in edit mode + list page inline)
- [x] Preview button: PDF templates → download rendered PDF with first valid invoice; email templates → rendered HTML in MudDialog popup
- [x] New API endpoint `GET /api/contenttemplate/{id}/preview` with sample placeholder data from issuer company
- [x] `GetStringAsync` added to `ApiClientBase` for raw string API responses
- [x] Resource keys: `ContentTemplate_DownloadHtml`, `ContentTemplate_UploadHtml`, `ContentTemplate_Preview`, `ContentTemplate_PreviewTitle`

---

## Completed

### Odstranění mrtvé cesty ověření e-mailu (issue #162) (2026-08-20)
`EmailVerificationToken` nikdy nikdo nenastavil — celá větev byla nedosažitelná. Adresu ověřuje
odkaz `/set-password?token={InvitationToken}` z registračního e-mailu.

- [x] Smazán endpoint `POST /api/auth/verify-email` (`AuthController`, `AuthFunctions`), `IAuthService.VerifyEmailAsync` + implementace, DTO `VerifyEmailRequest`/`VerifyEmailResponse`
- [x] Smazána stránka `/verify-email` (`VerifyEmail.razor`), `AuthApiService.VerifyEmailAsync` a 6 lokalizačních klíčů `VerifyEmail_*` (CZ + EN)
- [x] Smazána pole `User.EmailVerificationToken(ExpiresAt)` + filtrovaný index; migrace `RemoveEmailVerificationToken` (index `IsEmailVerified` zůstává)
- [x] `AuthService` už nebere `ITenantProvisioningService` — závislost sloužila jen mrtvé metodě
- [x] Testy: 5 mrtvých testů pryč, nový `SetPasswordAsync_ValidToken_VerifiesEmailAndProvisionsTenant` pokrývá skutečný tok
- [x] DEVGUIDE §2.8 (skutečný registrační tok) + poznámka o automatickém provisioningu v ADMINGUIDE §3

### Card payment recognition — merchant name pattern + transaction code (2026-07-19)
Platby kartou nemají protiúčet ani symboly — rozšíření rozpoznávání:

- [x] `RecognizedCounterparty.CounterpartyNamePattern` (case-insensitive substring proti CounterpartyName i Message), účet nyní volitelný — aspoň jedno z (účet, vzor) povinné; migrace `Add_CardPaymentRecognition_v54`
- [x] `TryRecognizeAsync` bez account gate — per-entry constraints (účet vyžaduje účet na transakci); specificita účet +2 > vzor +1; rescan bere i transakce bez účtu
- [x] AI prompt: karty JSOU platby, merchant → counterparty_name, maskovaná karta NENÍ účet, `transaction_code` extrakce
- [x] `BankTransaction.TransactionCode` + zahrnut do dedup hashe (fix: dvě stejné denní platby kartou se slévaly do jedné)
- [x] UI: pole vzoru v dialogu + sloupec v editoru, validace „aspoň jedno", CreateFromPayment prefilluje vzor u plateb bez účtu, kód transakce na detailu platby
- [x] 12 nových testů (name-pattern matching, hash uniqueness, parser card JSON, validace), unit suite 1787 zelených
- [x] USERGUIDE §15, DEVGUIDE §4.5.2

### Recognized counterparties — payment recognition without invoice (2026-07-19)
Evidence známých bankovních účtů (pojišťovny, FÚ…) + rozpoznávání pravidelných plateb:

- [x] Entity `RecognizedCounterparty` (Label, účet, volitelné VS/SS/KS = wildcard, `EPaymentCategory?`, IsActive) + migrace `Add_RecognizedCounterparty_v53`
- [x] `EMatchStatus.Recognized = 6` + nullable FK `BankTransaction.RecognizedCounterpartyId` (SetNull) — kategorizace mimo PaymentMatch (nezasahuje do PaidAmount invariantů)
- [x] `TryRecognizeAsync` — Rule 3 fallback v obou směrech matcheru; nejspecifičtější záznam vyhrává, remíza různých labelů → NeedsReview; faktura vždy vítězí (manual/confirm match nuluje FK)
- [x] `RescanUnmatchedAsync` — automaticky po create/update aktivního záznamu (počet → snackbar), + `POST /rescan`; delete/deaktivace resetuje transakce na Unmatched
- [x] API: `RecognizedCounterpartyController` (CRUD + rescan), assign/unassign-recognized na PaymentMatchingController + Functions wrappery (8 nových funkcí)
- [x] UI: `RecognizedCounterpartyEditor` sekce na MyCompany, `AssignRecognizedDialog` picker, chip + akce na Payments/PaymentDetail, filtr stavu Rozpoznané
- [x] Lokalizace: `EMatchStatus_Recognized`, 6× `EPaymentCategory_*`, 23× `RecognizedCounterparty_*` (CZ/EN)
- [x] 33 nových testů (matching + CRUD service + Functions parity smoke), unit suite 1775 zelených
- [x] USERGUIDE §15, DEVGUIDE §4.5.2
- [x] Follow-up: tlačítko „Vytvořit protistranu z platby" v assign dialogu — formulář předvyplněný z transakce (účet, VS/SS/KS, název), po uložení rovnou přiřadí

### ISDOC export for received invoices + bulk ISDOC actions (2026-07-17)
ISDOC export (dosud jen na detailu vydané faktury) rozšířen na přijaté faktury a hromadné akce:

- [x] `IIsdocExportService.ExportReceivedInvoiceAsync` + implementace (supplier = dodavatel, customer = tenant issuer `IsIssuer = true`)
- [x] `IsdocMapper.Map(ReceivedInvoice, Client?)` — sdílené buildery (lines, TaxTotal, LegalMonetaryTotal, PaymentMeans) s vydanými fakturami; UUID prefix `fakvio-received-invoice-{id}` (žádné kolize s vydanými)
- [x] Null party → XSD-validní prázdný skeleton (misconfigured tenant bez issuer záznamu)
- [x] Endpointy: `GET /api/received-invoice/{id}/isdoc`, `GET /api/received-invoice/bulk/isdoc?ids=`, `GET /api/invoice/bulk/isdoc?ids=` (bulk = ZIP, selhané kusy přeskočeny, `ZipArchiveHelper` dedup názvů)
- [x] Azure Functions wrappery: `ReceivedInvoice_ExportIsdoc`, `ReceivedInvoice_BulkExportIsdoc`, `Invoice_BulkExportIsdoc`
- [x] UI: tlačítko „Stáhnout ISDOC" na ReceivedInvoiceDetail; bulk akce na Invoices (Drafty přeskočeny) i ReceivedInvoices
- [x] API services: `InvoiceApiService.BulkExportIsdocAsync`, `ReceivedInvoiceApiService.ExportIsdocAsync/BulkExportIsdocAsync`
- [x] Lokalizace `Bulk_Isdoc` (CZ/EN)
- [x] 25 nových testů (IsdocReceivedInvoiceExportTests + IsdocReceivedInvoiceEndpointTests), všech 133 ISDOC testů zelených
- [x] USERGUIDE + DEVGUIDE aktualizovány

### Localized file names + MudBlazor.HtmlEditor migration (2026-03-26)
Two improvements implemented in a single pass:

**1. Localized download file names:**
- [x] Added resource keys `FileName_Invoice`, `FileName_CreditNote`, `FileName_Invoices`, `FileName_CreditNotes` (CZ: Faktura/Dobropis/Faktury/Dobropisy, EN: Invoice/CreditNote/Invoices/CreditNotes)
- [x] `Invoices.razor` — 3 places: single PDF download, template PDF download, bulk ZIP download (uses `_isCreditNoteView` for ZIP name)
- [x] `InvoiceDetail.razor` — 2 places: default PDF download, template PDF download (uses `_invoice.DocumentType`)
- [x] `InvoiceController.cs` — 3 places: single PDF, bulk PDF entries, ZIP archive name (uses `invoice.DocumentType`)
- [x] `EmailService.cs` — email attachment file name (uses `invoice.DocumentType`)

**2. WYSIWYG editor migration (Blazored.TextEditor → Tizzani.MudBlazor.HtmlEditor):**
- [x] Replaced `Blazored.TextEditor` v1.1.3 (abandoned, Quill 1.x) with `Tizzani.MudBlazor.HtmlEditor` v2.3.0 (active, Quill 2.x, MudBlazor-native)
- [x] Rewrote `ContentTemplateDetail.razor` — replaced `BlazoredTextEditor` with `MudHtmlEditor` + `@bind-Value`
- [x] Removed 30-attempt polling hack in `OnAfterRenderAsync` (no longer needed — MudHtmlEditor handles lifecycle)
- [x] Removed `_quillEditor`, `_quillNeedsLoad`, `_quillReady` fields
- [x] Updated `index.html` (BlazorUI + MauiApp): removed Quill 1.x CDN + Blazored.TextEditor scripts, added Quill 2.x CDN + MudHtmlEditor CSS/JS
- [x] Updated `Fakvio.UI.Shared.csproj`: swapped NuGet packages

---


### Credential encryption at rest (2026-03-24)
All sensitive credentials stored in the database are now encrypted using ASP.NET Core Data Protection API (AES-256-CBC + HMACSHA256). Migration-safe: legacy plaintext values are returned unchanged by Decrypt and automatically encrypted on next save.

- [x] **ICredentialProtector** interface (Application layer) — Encrypt/Decrypt with null-safety and migration safety
- [x] **CredentialProtector** implementation (Infrastructure) — uses IDataProtectionProvider with "Fakvio.Credentials.v1" purpose
- [x] **CompanyController.CreateSettings** — encrypts SmtpPassword, AiClaudeApiKey, AiOpenAiApiKey, AiGeminiApiKey
- [x] **CompanyController.UpdateSettings** — encrypts SmtpPassword, AI API keys on update
- [x] **SystemConfigurationService.UpdateAsync** — encrypts SmtpPassword on save
- [x] **SystemConfigurationService.MapToDto** — decrypts SmtpPassword for downstream consumers
- [x] **EmailService.ResolveSmtpSettingsAsync** — decrypts SmtpPassword (tier 1: CompanySystemSettings)
- [x] **CompanyAiSettingsResolver** — decrypts AI API keys after loading from DB
- [x] **GoogleDriveStorageService** — encrypts/decrypts OAuth tokens (access + refresh)
- [x] **OneDriveStorageService** — encrypts/decrypts OAuth tokens (access + refresh)
- [x] **DI Registration** — ICredentialProtector as Singleton in ServiceCollectionExtensions
- [x] **Tests** — 15 CredentialProtectorTests (roundtrip, null-safety, legacy plaintext, different IVs)
- [x] **All affected tests updated** — pass-through mock protector in EmailServiceTests, SystemConfigurationServiceTests, CompanyAiSettingsResolverTests

### Fix service logging + SMTP authentication (2026-03-23)
LoggingProxy used `LogDebug` for async ENTER/EXIT — silently dropped by DatabaseLoggerProvider (minimum: Information). SMTP used bool `UseSsl` → mapped to `SslOnConnect` even on port 587, causing `AuthenticationException`.

- [x] **LoggingProxy.cs**: Changed `LogDebug` → `LogInformation` for async ENTER/EXIT/method logging (lines 111, 154, 177) — now all service method calls are persisted to AppLog DB table
- [x] **EmailService.cs**: Replaced `bool useSsl` ConnectAsync overload with `SecureSocketOptions` — port 465 → SslOnConnect, port 587 → StartTls, UseSsl=false → None
- [x] **EmailService.cs**: Added `ResolveSocketOptions` helper method with port-based SSL strategy
- [x] **EmailService.cs**: Added try-catch around `AuthenticateAsync` with detailed error logging (username, host, port, socket options)
- [x] **Tests**: All 9 EmailService tests pass

### Company-Level AI Settings (2026-03-19)
Per-company AI provider configuration — same 2-tier pattern as SMTP (Company → System fallback).

- [x] **CompanySystemSettings entity**: Added 9 AI fields (AiDefaultProvider, AiClaudeApiKey/Model, AiOpenAiApiKey/Model, AiGeminiApiKey/Model, AiOllamaBaseUrl/Model)
- [x] **DTOs**: Updated CompanySystemSettingsDto (read), CreateCompanySystemSettingsDto, UpdateCompanySystemSettingsDto — API keys never exposed, only HasXxxApiKey flags
- [x] **CompanyController**: Updated CreateSettings, UpdateSettings, MapSettingsToDto for AI fields
- [x] **ICompanyAiSettingsResolver**: New interface in Application — 2-tier provider resolution (company → system)
- [x] **CompanyAiSettingsResolver**: Implementation with ad-hoc provider creation (AdHocClaudeProvider, AdHocOpenAiProvider)
- [x] **ChatService**: Uses ICompanyAiSettingsResolver instead of direct IAiProviderFactory; GetAvailableProviders → async GetAvailableProvidersAsync
- [x] **ChatController**: GetProviders endpoint updated to async
- [x] **DI Registration**: ICompanyAiSettingsResolver registered as scoped in ServiceCollectionExtensions
- [x] **EF Migration**: AddCompanyAiSettings migration for MasterDbContext
- [x] **Ad-hoc providers**: AdHocClaudeProvider, AdHocOpenAiProvider, AdHocGeminiProvider (REST API + SSE streaming), AdHocOllamaProvider (NDJSON streaming + native tool calling)
- [x] **IHttpClientFactory**: Injected into CompanyAiSettingsResolver for Gemini/Ollama ad-hoc HttpClient creation
- [x] **Tests**: 9 CompanyAiSettingsResolverTests (incl. Gemini + Ollama) + 24 ChatServiceTests = 33 total

### Schema Permissions for Azure PostgreSQL (2026-03-15)
GRANT ALL + ALTER DEFAULT PRIVILEGES on tenant schemas — ensures the Azure (Entra ID) user has full access to all existing and future objects.

- [x] **TenantProvisioningService.CreateSchemaAsync**: After CREATE SCHEMA, grants ALL on schema + existing tables/sequences + sets ALTER DEFAULT PRIVILEGES for future tables/sequences/functions
- [x] **TenantProvisioningService.GrantSchemaPermissionsAsync**: New private helper — resolves CURRENT_USER, applies 6 GRANT/ALTER DEFAULT PRIVILEGES statements
- [x] **TenantProvisioningService.EnsureSchemaPermissionsAsync**: New private helper — opens own connection, calls GrantSchemaPermissionsAsync (used after MigrateAsync)
- [x] **TenantProvisioningService.MigrateTenantAsync**: Re-applies permissions after EF Core migration (new tables from migration get proper grants)
- [x] **TenantOperationController.FixSchemaPermissions**: New POST /api/tenant-operation/fix-permissions endpoint — retroactively fixes permissions for ALL provisioned schemas (one-time SysAdmin action)
- [x] **MigrationTool.DataMigrationService**: Added GrantSchemaPermissionsAsync after schema creation (same pattern as TenantProvisioningService)

### Playwright E2E Tests (2026-03-10)
Comprehensive browser-based UI tests using Microsoft.Playwright.NUnit (Chromium headless).

- [x] **Fakvio.Tests.Playwright** project: NUnit + Playwright 1.52.0, added to solution
- [x] **Infrastructure**: TestConfiguration (env vars), PlaywrightSetup (health checks), FakvioPageTest (base class), AuthHelper (API login + PascalCase localStorage injection + SysAdmin impersonation)
- [x] **Auth tests** (5): Login form rendering, valid/invalid credentials, OAuth buttons, registration link
- [x] **Navigation tests** (3): Dashboard link, multiple nav links, settings group
- [x] **Authorization tests** (6): 5 protected routes redirect to login, authenticated user can access dashboard
- [x] **Dashboard tests** (3): Page content, nav menu visibility, page title
- [x] **Invoice list tests** (8): Table headers, search, filters, new button, pagination, import dropdown, credit notes
- [x] **Invoice create tests** (4): Form selects, buttons, date pickers, credit note mode
- [x] **Received invoice tests** (6): Table, new/import buttons, filters, date pickers, pagination
- [x] **Import wizard tests** (5): Upload step, radio buttons, file upload button, query params, analyze hidden
- [x] **Client list tests** (3): Table, search, new button
- [x] **Settings tests** (5): VAT rates table + actions, number sequences, My Company heading + inputs
- [x] **Key fix**: Blazored.LocalStorage stores PascalCase JSON (C# default), not camelCase (API default). AuthHelper converts API response to PascalCase before injection
- [x] **Key fix**: SysAdmin needs `ImpersonatedCompanyId` in localStorage to see invoicing pages
- [x] **Key fix**: `.mud-paper` selector matches hidden MudPopover elements — use `h4` instead
- [x] **48 Playwright tests passing** + 756 unit tests passing

### PDF Invoice Import (2026-03-10)
3-tier extraction pipeline: QR Code → AI → Regex fallback. 2-step wizard: Preview → Confirm.

- [x] **SindParser**: Reverse of SindBuilder — parses SIND QR Faktura strings with CRC32 validation
- [x] **SpdParser**: Reverse of SpdIntegrator — parses SPD QR Platba strings, decodes embedded X-INV SIND
- [x] **InvoiceExtractedData**: Unified data model for all extraction sources (QR/AI/Regex)
- [x] **InvoiceAiExtractorService** (IInvoiceAiExtractor): AI-based extraction via IAiProviderFactory, structured JSON prompt, graceful fallback on error/timeout
- [x] **InvoiceTextExtractorService** (IInvoiceTextExtractor): Regex fallback for Czech invoices — document number, dates, amounts, IČO/DIČ, bank accounts, currency
- [x] **QrCodeExtractorService** (IQrCodeExtractor): ZXing.Net.Bindings.SkiaSharp, iText7 ImageRenderListener, extracts QR from PDF embedded images
- [x] **InvoiceImportService** (IInvoiceImportService): Waterfall pipeline (QR → AI → Regex → Merge), client lookup/creation by IČO, duplicate detection, validation
- [x] **Import DTOs**: EImportTarget, InvoiceImportPreviewDto, ConfirmInvoiceImportRequest, ConfirmImportItemDto, ImportResultDto, ImportValidationMessage
- [x] **ImportController**: POST /api/import/preview (multipart/form-data, 50MB), POST /api/import/confirm (JSON)
- [x] **ImportApiService**: Blazor HTTP service with multipart upload for preview, JSON POST for confirm
- [x] **InvoiceImport.razor**: 3-step wizard (Upload → Preview → Results), MudFileUpload, MudTable, validation panels
- [x] **UI integration**: Import buttons on Invoices + ReceivedInvoices pages, NavMenu entry
- [x] **Localization**: 25 Import_* resource keys in CZ/EN
- [x] **DI registration**: All 4 services registered (IQrCodeExtractor, IInvoiceAiExtractor, IInvoiceTextExtractor, IInvoiceImportService)
- [x] **InternalsVisibleTo**: Infrastructure → Tests.Unit for internal method testing
- [x] **105 new tests**: SindParser (17), SpdParser (15), InvoiceAiExtractorService (13), InvoiceTextExtractorService (28), QrCodeExtractorService (12), InvoiceImportService (20)
- [x] **756 total tests passing** (0 failures)

### AI Chat Hardening, VS Duplicate Check, Invoice Restore, Date Locale Fix (2026-03-07)
- [x] **AI tool calling**: Removed regex-based intent detection — LLM now decides when to use tools via native API (Path A) or text-based instructions (Path B)
- [x] **Anti-hallucination**: System prompt explicitly declares capabilities and forbids claiming actions without tool confirmation
- [x] **OllamaProvider dynamic tool support**: `SupportsNativeTools` starts true, auto-disables on "does not support tools" error (gemma3:12b, phi4 don't support tools)
- [x] **SSE heartbeat**: `yield return ""` before blocking non-streaming calls to prevent HTTP timeout
- [x] **HttpClient timeout**: Increased from 100s to 5min in BlazorUI Program.cs
- [x] **Variable Symbol duplicate check**: InvoiceService.CreateInvoiceAsync + CompleteInvoiceAsync — checks for existing non-deleted invoice with same VS before saving, throws InvalidOperationException
- [x] **ChatContextBuilder VS rules**: System prompt instructs AI to check for duplicate VS and warn user
- [x] **Invoice restore**: RestoreInvoiceAsync (interface, service, controller POST {id}/restore, API service, UI button), only Deleted → Draft
- [x] **Deleted invoice viewable**: GetInvoiceByIdAsync no longer filters out Deleted status
- [x] **Date picker Czech locale**: BlazorUI Program.cs uses `cs-CZ` culture (dd.MM.yyyy) instead of InvariantCulture, with `.` decimal separator override
- [x] **Localization**: Invoice_Restore/Invoice_Restored (CZ+EN)
- [x] **Unit tests**: 2 VS duplicate tests + 6 restore tests = 8 new tests
- [x] Build: 0 errors, **651 tests** (all pass)

### Tax Estimation Extensions — CRUD, Income, MCP, Dashboard, PDF, Insurance (2026-03-07)
- [x] **CreateTaxYearConfigDto**: DTO for CRUD operations on TaxYearConfig
- [x] **AnnualIncomeDto**: DTO for auto-calculated annual gross income from invoices (year, grossIncome, grossIncomeWithVat, invoiceCount, currencyCode)
- [x] **InsuranceAdvanceDto**: DTO for insurance advance notification (monthlySocial, monthlyHealth, nextPaymentDate, daysUntilPayment)
- [x] **ITaxEstimationService extended**: +CreateConfigAsync, +UpdateConfigAsync, +DeleteConfigAsync, +GetAnnualIncomeAsync, +GetInsuranceAdvanceAsync
- [x] **TaxEstimationService extended**: Full CRUD (duplicate check, update, delete), annual income from invoices (Completed/Paid, excludes Drafts/CreditNotes), insurance advance calculation (issuer TaxRegime → estimation → monthly amounts + next 20th date)
- [x] **TaxController extended**: 6 new endpoints (POST config, PUT config/{id}, DELETE config/{id}, GET income/{year}, GET insurance-advance, GET compare/pdf)
- [x] **PDF export**: GET /api/tax/compare/pdf — generates HTML comparison table → iText7 PDF, with filename tax-comparison-{country}-{year}.pdf
- [x] **TaxApiService extended**: +CreateConfigAsync, +UpdateConfigAsync, +DeleteConfigAsync, +GetAnnualIncomeAsync, +GetInsuranceAdvanceAsync
- [x] **TaxYearConfigs.razor**: New admin page (SysAdmin/Admin only) — CRUD table + dialog form with grouped sections (Common, IncomeTax, Social, Health, FlatRate, LumpSum)
- [x] **TaxEstimation.razor updated**: "Load from invoices" button (auto-fills gross income from issued invoices), "Export to PDF" button (opens comparison PDF in new tab)
- [x] **Dashboard (Home.razor) updated**: Insurance advance cards (monthly total, next payment date with due-in-days/overdue chip, social+health breakdown) — clickable → /tax-estimation
- [x] **NavMenu.razor**: Added "Tax Rates / Daňové sazby" link under SysAdmin settings
- [x] **MCP Server**: 5 new tax tools (EstimateTax, CompareTaxRegimes, GetAnnualIncome, GetInsuranceAdvance, GetTaxConfig), IFakvioApiClient + FakvioApiClient extended
- [x] **Localization**: ~25 CZ + ~25 EN new keys (TaxConfig_*, Tax_AutoIncome/ExportPdf, Dashboard_InsuranceAdvance/Social/Health/Total/NextDate/DueIn/Overdue, Nav_TaxConfig)
- [x] **MCP TaxTools tests**: 11 new tests — EstimateTax (3), CompareTaxRegimes (2), GetAnnualIncome (2), GetInsuranceAdvance (2), GetTaxConfig (2)
- [x] **Unit tests**: 13 new TaxEstimationService tests — CRUD (6), annual income (4), insurance advance (3)
- [x] **EF Migrations**: Master (AddTaxYearConfigAndClientTaxFields) + Tenant (AddClientTaxFields) — TaxYearConfig table + Client tax fields + seed data (CZ/SK 2025/2026)
- [x] Build: 0 errors, **643 tests** (all pass)

### Ollama Native Tool Calling Optimization (2026-03-07)
- [x] **IAiProvider extended**: Added `SupportsNativeTools` (default false), `GetCompletionWithToolsAsync` (default null), `NativeToolDefinition`, `NativeToolParameter`, `NativeToolCallResult`, `NativeToolCall` models
- [x] **OllamaProvider rewritten**: Uses Ollama's native `tools` parameter in `/api/chat` — model produces structured `tool_calls` instead of free-text JSON. Parses `tool_calls[].function.name` + `arguments` from NDJSON response
- [x] **IChatToolExecutor extended**: Added `GetToolDefinitions()` — converts IChatTool registry into NativeToolDefinition list with JSON Schema parameters (per-tool hardcoded mapping for ares_lookup, create_client, navigate, create_invoice)
- [x] **ChatService dual-path architecture**: Path A (native tools) — always sends tool definitions, model decides; Path B (text-based) — regex intent detection + two-pass AI flow. Both sync and streaming paths updated
- [x] **Regex patterns broadened**: ToolKeywordPattern (added Czech declensions: klienta/firmu/přidat/pridej), NavigationKeywordPattern (added: chci, potřebuju, přehled faktur, nového klienta, ukázat, navigovat)
- [x] Build: 0 errors, **643 tests** (all pass)

### Tax Regime & Estimation System (2026-03-06)
- [x] **3 new enums**: ETaxRegime (7 values: FlatRateTax, LumpSumExpenses 80/60/40/30, TaxRecords, FullAccounting), EActivityType (4 values: CraftTrade, NonCraftTrade, RegulatedProfession, Rental), EFlatRateBand (Band1/2/3)
- [x] **TaxYearConfig entity**: Annual tax rates per country (AverageMonthlyWage, IncomeTaxRate, ProgressiveTaxRate, SocialInsuranceRate, HealthInsuranceRate, FlatRateBand monthly amounts, LumpSum caps, min/max thresholds)
- [x] **Client entity extended**: +TaxRegime, +ActivityType, +IsMainActivity, +FlatRateBand fields
- [x] **TaxYearConfig seed data**: CZ 2025, CZ 2026, SK 2025, SK 2026 with real-world rates
- [x] **Contracts DTOs**: TaxEstimationRequest, TaxEstimationResult (full breakdown + calculation steps), TaxYearConfigDto
- [x] **Client DTOs updated**: ClientDto, CreateClientDto, UpdateClientDto — all have TaxRegime, ActivityType, IsMainActivity, FlatRateBand (string for API, enum in entity)
- [x] **ITaxEstimationService + TaxEstimationService**: Full CZ/SK tax calculation (flat-rate, lump-sum, tax records, full accounting), progressive tax, social/health insurance with min/max enforcement, regime comparison, step-by-step breakdown
- [x] **TaxController**: 4 endpoints (POST /estimate, GET /compare, GET /config/{country}/{year}, GET /configs)
- [x] **DI registration**: TaxEstimationService in ServiceCollectionExtensions
- [x] **MasterDbContext**: ConfigureTaxYearConfig (precision, unique index on Year+Country)
- [x] **TenantDbContext**: Client config extended with enum→string conversion for TaxRegime, ActivityType, FlatRateBand
- [x] **ClientService**: Create and Update methods map string→enum for tax fields; MapToDto handles enum→string manually (ZMapper can't auto-map enum?→string?)
- [x] **ClientProfile**: ZMapper ForMember Ignore on TaxRegime, ActivityType, FlatRateBand (manual mapping in service)
- [x] **TaxApiService**: Blazor UI service (estimate, compare, getConfig, getAllConfigs) inheriting ApiClientBase
- [x] **UI service registration**: TaxApiService in UI.Shared ServiceCollectionExtensions
- [x] **Localization**: ~50 CZ + ~50 EN keys (ETaxRegime_*, EActivityType_*, EFlatRateBand_*, Tax_*, Client_TaxSection/TaxRegime/ActivityType/IsMainActivity/FlatRateBand)
- [x] **Unit tests**: 35 new tests — flat-rate (4), lump-sum (4), income tax (2), social insurance (2), health insurance (1), tax records/accounting (3), totals (3), SK (3), error handling (2), compare regimes (4), config (3), edge cases (2) — all pass
- [x] **TaxEstimation.razor**: Full Blazor page — single regime estimation with summary cards + step-by-step breakdown, side-by-side regime comparison table with "Best option" chip, country-aware regime selection (CZ/SK), pre-fills from company tax settings
- [x] **MyCompany.razor**: Tax Settings section (4-field card: TaxRegime, ActivityType, IsMainActivity, FlatRateBand) with read/edit modes, conditional FlatRateBand visibility
- [x] **ClientDetail.razor**: Tax Settings section (same layout), wired into EnableEditing + SaveClient, GetLocalizedEnum helper
- [x] **NavMenu.razor**: Added "Odhad daní" / "Tax Estimation" link with AccountBalance icon
- [x] Build: 0 errors, **619 tests** (35 new + 584 existing)

### Received Invoices + VAT Report (2026-03-06)
- [x] **ReceivedInvoice domain**: New entity `ReceivedInvoice` + `ReceivedInvoiceItem` with full DUZP support, supplier FK, status lifecycle (Received→Approved→Paid, Rejected, Deleted)
- [x] **EReceivedInvoiceStatus enum**: Received=1, Approved=2, Paid=3, Rejected=4, Deleted=5
- [x] **Contracts DTOs**: ReceivedInvoiceDto, CreateReceivedInvoiceDto, UpdateReceivedInvoiceDto, ReceivedInvoiceFilterDto, ReceivedInvoiceItemDto, CreateReceivedInvoiceItemDto
- [x] **VatReport DTOs**: VatReportDto (output/input VAT, tax liability, revenue/expenses/profit), VatReportLineDto (per VAT rate breakdown)
- [x] **IReceivedInvoiceService + ReceivedInvoiceService**: Full CRUD + status transitions, totals calculation, paged filtering/sorting
- [x] **IVatReportService + VatReportService**: Aggregates output VAT (issued invoices) vs input VAT (received invoices) by DUZP period, excludes Draft/Deleted/Received/Rejected
- [x] **ZMapper profile**: ReceivedInvoiceProfile with SupplierName/CurrencyCode/CurrencySymbol ignored (manual mapping)
- [x] **TenantDbContext**: DbSet<ReceivedInvoice>, DbSet<ReceivedInvoiceItem>, ConfigureReceivedInvoice/ConfigureReceivedInvoiceItem with indexes and FKs
- [x] **DI registration**: ReceivedInvoiceService + VatReportService in ServiceCollectionExtensions
- [x] **ReceivedInvoiceController**: 9 endpoints (GET, GET/paged, GET/{id}, POST, PUT/{id}, POST/{id}/approve, POST/{id}/mark-paid, POST/{id}/reject, DELETE/{id})
- [x] **VatReportController**: GET endpoint with from/to query params
- [x] **MCP Server**: 6 ReceivedInvoice tools + 2 new ReportingTools (GetVatReport, GetOverdueReceivedInvoices), FakvioApiClient extended
- [x] **Blazor UI**: ReceivedInvoices list page (server-side paging, filters, status actions), ReceivedInvoiceDetail (create/edit/view with inline items), VatReport (period selection, summary cards, VAT breakdowns)
- [x] **Nav menu**: Added "Přijaté faktury" and "Přehled DPH" links
- [x] **Localization**: ~45 CZ + ~45 EN keys for ReceivedInvoice_* and VatReport_*
- [x] **UI services**: ReceivedInvoiceApiService + VatReportApiService (ApiClientBase inheritance)
- [x] **Unit tests**: 36 new tests (26 ReceivedInvoiceService + 10 VatReportService) — all pass
- [x] Build: 0 errors, **584 tests pass** (36 new + 548 existing, 0 failures)

### Image Upload Support for AI Chat + Gemma3:12b Default (2026-03-03)
- [x] **Default model**: Changed Ollama default from llama3.1:8b/llama3.2 to gemma3:12b (multimodal, already pulled in Docker)
- [x] **SendMessageRequest DTO**: Added `AttachedImageBase64` (raw base64, max ~10 MB) for image uploads
- [x] **ChatMessageDto DTO**: Added `[JsonIgnore] Images` list — transient property to pass base64 data from ChatService to IAiProvider without DB/API serialization
- [x] **OllamaProvider**: `BuildRequestBody` now emits `images[]` array when `msg.Images` has entries, enabling multimodal Gemma3 processing
- [x] **ChatService**: `BuildMessageWithAttachment` — image takes precedence over PDF; stores `[Image: filename]` placeholder in DB (not base64). New `AttachImageToLastMessage` helper sets Images on last user message for provider
- [x] **ChatInput.razor**: Accept filter expanded to `.pdf,.jpg,.jpeg,.png,.gif,.webp`. New `AttachedImagePreview` parameter shows 80x80 thumbnail with Image icon chip
- [x] **ChatPanel.razor**: New `_attachedImageBase64`/`_attachedImagePreview` state. Refactored `HandleFileAttached` to route by extension (PDF → server extraction, Image → client base64). `HandleImageAttached` reads bytes in browser via `Convert.ToBase64String`
- [x] **Localization**: Updated `Chat_AttachFile` ("Připojit soubor" / "Attach file"), added `Chat_UnsupportedFileType`
- [x] **3 new tests**: Image stores placeholder in DB (not base64), image base64 passed via ChatMessageDto.Images, image takes precedence over PDF
- [x] Build: 0 errors, **548 tests pass** (3 new image + 545 existing, 0 failures)

### Phase D: MCP Server — AI-Assisted Invoicing (2026-03-03)
- [x] **Fakvio.McpServer**: New console app project (net10.0, PackAsTool, ToolCommandName=fakvio-mcp)
- [x] **NuGet**: ModelContextProtocol 1.0.0 + Microsoft.Extensions.Hosting + Microsoft.Extensions.Http
- [x] **ProjectRef**: Fakvio.Contracts only (zero DB/Infrastructure deps — thin wrapper over REST API)
- [x] **McpServerSettings**: POCO for FAKVIO_API_URL + FAKVIO_API_TOKEN env vars
- [x] **IFakvioApiClient + FakvioApiClient**: Typed HttpClient wrapper with ~20 methods mapping to API endpoints, query string builder, error extraction
- [x] **Program.cs**: Host builder, stderr-only logging (stdout reserved for MCP protocol), env var config, HttpClient with Bearer auth, MCP stdio transport, WithToolsFromAssembly
- [x] **InvoiceTools** (8 tools): ListInvoices, GetInvoice, FindInvoiceByNumber, CreateInvoice, CompleteInvoice, MarkInvoicePaid, SendInvoiceEmail, DeleteInvoice
- [x] **ClientTools** (6 tools): ListClients, GetClient, CreateClient, UpdateClient, LookupAres, GetIssuer
- [x] **TemplateTools** (3 tools): ListTemplates, GetTemplate, CreateInvoiceFromTemplate
- [x] **ReportingTools** (4 tools): GetDashboard, GetOverdueInvoices, GetClientInvoices, GetInvoicesByDateRange
- [x] **21 MCP tools total** — all with [Description] for AI model guidance, enum string parsing, JSON string params for complex DTOs
- [x] **JsonStringEnumConverter**: All tool JSON options include enum-as-string for natural AI interaction
- [x] **Solution**: Added Fakvio.McpServer to Fakvio.sln, added ProjectReference in Tests.Unit
- [x] **FakvioApiClientTests** (18 tests): MockHttpMessageHandler, query string verification, error handling (400/404/500), URL encoding
- [x] **InvoiceToolsTests** (24 tests): All 4 tool classes tested — JSON serialization, null handling, enum parsing, date parsing, error wrapping
- [x] Build: 0 errors, **545 tests pass** (42 new MCP + 503 existing, 0 failures)

### PDF Text Extraction for AI Chat (2026-03-03)
- [x] **IPdfTextExtractorService**: New interface in Application/Service — `ExtractTextAsync(byte[], CancellationToken)`
- [x] **PdfTextExtractorService**: Infrastructure implementation using iText7 `PdfTextExtractor` + `SimpleTextExtractionStrategy`, validates size (10 MB max), handles corrupt/password-protected PDFs
- [x] **SendMessageRequest DTO**: Added optional `AttachedFileContent` and `AttachedFileName` properties
- [x] **ChatController**: New `POST extract-pdf` endpoint accepting `IFormFile`, validates .pdf extension + 10 MB size limit
- [x] **ChatService**: `BuildMessageWithAttachment()` helper prepends PDF content as context block (`[Attached PDF: filename]` + content + user question)
- [x] **ChatApiService**: New `ExtractPdfTextAsync(byte[], string)` method sending multipart/form-data to API
- [x] **ChatInput.razor**: Added `MudFileUpload` (paper clip icon) for PDF files, file chip indicator with remove button, extraction progress indicator
- [x] **ChatPanel.razor**: Handles file attachment lifecycle (select → extract → attach → send → clear), includes content in `SendMessageRequest`
- [x] **DI**: Registered `IPdfTextExtractorService` → `PdfTextExtractorService` (scoped) in Infrastructure ServiceCollectionExtensions
- [x] **Localization**: 6 new keys (Chat_AttachFile, Chat_FileAttached, Chat_RemoveAttachment, Chat_ExtractingText, Chat_PdfOnly, Chat_FileTooLarge) in both CZ/EN
- [x] **Unit tests**: 8 PdfTextExtractorServiceTests (valid PDF, multi-page, empty, null/empty/corrupt bytes, oversize, cancellation) + 3 ChatServiceTests (attachment prepend, no-attachment passthrough, empty attachment ignored)
- [x] Build: 0 errors (excl. MAUI workload), **503 tests pass** (0 failures)

### Per-Client Language for Documents + Default cs-CZ UI (2026-03-02)
- [x] **Domain**: Added `string Language` property to `Client.cs` and `ContentTemplate.cs` (ISO 639-1, default "cs")
- [x] **DTOs**: Added Language to all 6 DTOs (ClientDto, CreateClientDto, UpdateClientDto, ContentTemplateDto, CreateContentTemplateDto, UpdateContentTemplateDto)
- [x] **DB config**: TenantDbContext + MasterDbContext — Language column (varchar(5), NOT NULL, default 'cs'), updated composite index to (TemplateType, Language, IsDefault)
- [x] **Migrations**: Generated `AddLanguageToClientAndContentTemplate` for both Tenant and Master contexts
- [x] **Seed data**: Added `Language = "cs"` to all seeded ContentTemplate entries (6 tenant + 7 master)
- [x] **IContentTemplateService**: Added language-aware overload `GetDefaultByTypeAsync(type, language, ct)` with fallback chain (exact match → any-language → null)
- [x] **ContentTemplateService**: Implemented language-aware resolution, scoped `UnsetDefaultForTypeAsync` by (type, language), mapped Language in Create/Update
- [x] **PdfExportService**: Reads `invoice.Client?.Language ?? "cs"`, passes to language-aware template resolution, added `GetDocumentTypeLabel` and updated `GetPaymentMethodLabel` with CZ/EN labels
- [x] **EmailService**: Added `.Include(i => i.Client)` to invoice query, reads client language for template resolution, system emails (invitation, 2FA) use "cs" explicitly
- [x] **UI: MainLayout**: Removed `<LanguageSwitcher />` from AppBar
- [x] **UI: Program.cs**: Replaced localStorage culture detection with fixed `CultureInfo("cs-CZ")`
- [x] **UI: ClientDetail + MyCompany**: Added Language `<MudSelect>` (cs/en) in edit mode, language display in read-only mode, mapped in EnableEditing/SaveClient
- [x] **UI: ContentTemplates**: Added Language column to list table
- [x] **UI: ContentTemplateDetail**: Added Language `<MudSelect>` in form mode, language display in read-only mode, mapped in Create/Update DTOs
- [x] **Resources**: Added `Client_Language` ("Jazyk dokumentů" / "Document language") and `ContentTemplate_Language` ("Jazyk" / "Language")
- [x] **Unit tests**: 8 new tests — ContentTemplateService (4: language match, fallback, null, scoped default), PdfExportService (2: cs/en client language), EmailService (2: invoice + invitation language)
- [x] **Updated existing tests**: Fixed PdfExportServiceTests and EmailServiceTests mock signatures for new language-aware overload
- [x] Build: 0 errors, **492 tests pass** (0 failures)

### Rebrand InvoiceApi → Fakvio + Registration Form Changes (2026-03-01)
- [x] Global find-and-replace across 382 files: InvoiceApi → Fakvio, invoiceapi → fakvio, Invoice API → Fakvio, InvoiceApiClient → FakvioClient
- [x] Renamed 13 project directories (InvoiceApi.* → Fakvio.*), 13 .csproj files, InvoiceApi.sln → Fakvio.sln
- [x] Verified docker-compose.yml, appsettings*.json, index.html, manifest.webmanifest, .resx files, .sln references
- [x] Build: 0 errors (excl. MAUI workload), 340 unit tests pass, 5 integration tests pass
- [x] Registration form: removed Password field from RegisterRequest DTO (both Contracts and UI.Shared)
- [x] Registration form: RegistrationNumber (IČO) now [Required] instead of optional
- [x] AuthService.RegisterAsync: PasswordHash = null, IsInvitationPending = true, InvitationToken set
- [x] AuthService.RegisterAsync: email link changed from /verify-email to /set-password?token=
- [x] UserService.SetPasswordAsync: sets IsEmailVerified = true, triggers tenant provisioning
- [x] Register.razor: removed password fields, removed OAuth buttons, IČO required
- [x] Resources: updated Register_RegistrationNumber (removed "optional"), added Register_SuccessSetPassword key (CZ + EN)
- [x] Unit tests: updated 5 existing register tests (no Password, added RegistrationNumber), added Register_EmailContainsSetPasswordLink test
- [x] UserInvitationTests: updated UserService constructor to include ITenantProvisioningService + ILogger

### Azure SQL → Azure PostgreSQL Multi-Schema Migration (2026-02-28)
- [x] Analysis of current multi-tenant architecture (MasterDbContext, TenantDbContext, TenantDbContextFactory, TenantProvisioningService, AzureSqlService)
- [x] Identified all SQL Server-specific code: 12 UseSqlServer, 9 SqlConnectionStringBuilder, 7 SqlConnection, 4 HasFilter, 1 IsRowVersion
- [x] Created comprehensive migration plan (`MIGRATION-PLAN-POSTGRESQL.md`)
- [x] **Step 1**: NuGet packages — SqlServer → Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0 (3 .csproj files)
- [x] **Step 2**: Domain entities — NumberSequence.RowVersion (byte[] → uint xmin), CompanySystemSettings (DatabaseName → SchemaName, removed ConnectionString)
- [x] **Step 3**: DbContext configs — HasFilter syntax (PostgreSQL double-quotes), IsConcurrencyToken+ValueGeneratedOnAddOrUpdate (xmin), TenantDbContext.Schema + HasDefaultSchema
- [x] **Step 4**: Schema-based tenant resolution — TenantModelCacheKeyFactory, TenantDbContextFactory rewrite, ServiceCollectionExtensions (single DefaultConnection)
- [x] **Step 5**: Tenant provisioning — CREATE SCHEMA, NpgsqlConnection, information_schema.schemata, ALTER SEQUENCE
- [x] **Step 6**: Remove Azure SQL service — deleted IAzureSqlService + AzureSqlService, rewrote AzureOperationController → TenantOperationController
- [x] **Step 7**: Raw ADO.NET — SqlConnection → NpgsqlConnection (LogFlush, LogCleanup, TimerFunctions, Diagnostics)
- [x] **Step 8**: Connection strings — MasterConnection+TenantTemplateConnection → single DefaultConnection (PostgreSQL format)
- [x] **Step 9**: Design-time factories — UseSqlServer → UseNpgsql, TenantDesignTimeFactory sets Schema="tenant_template"
- [x] **Step 10**: Deleted old SQL Server migrations, generated InitPostgres for both Master + Tenant
- [x] **Step 11**: MigrationTool — SqlConnection → NpgsqlConnection, CREATE DATABASE → CREATE SCHEMA
- [x] **Step 12**: Tests — deleted AzureSqlServiceTests (30 tests), updated 8 test files (DatabaseName→SchemaName, connection strings, RowVersion)
- [x] **Step 13**: DTOs — AzureOperation → TenantOperation, removed HasCustomConnectionString
- [x] Blazor UI — CompanySettings.razor + CompanyDetail.razor updated (SchemaName, removed ConnectionString column/dialog)
- [x] Build verification: 14/15 projects compile (MauiApp needs SDK install)
- [x] Test verification: **327 unit tests pass** (0 failures)


### PWA Implementation (2026-02-14)
- [x] Created `wwwroot/manifest.webmanifest` — app metadata (name, icons, theme color `#594ae2`, display: standalone)
- [x] Created `wwwroot/service-worker.js` — dev pass-through (no caching during development)
- [x] Created `wwwroot/service-worker.published.js` — production offline-first strategy with versioned cache, API calls excluded, SHA-256 integrity checks from asset manifest
- [x] Generated `wwwroot/icon-192.png` + `wwwroot/icon-512.png` — purple "INV" placeholder icons (replace with real logo)
- [x] Updated `wwwroot/index.html` — manifest link, iOS meta tags (`apple-mobile-web-app-capable`, `apple-touch-icon`), theme-color, service worker registration
- [x] Updated `Fakvio.BlazorUI.csproj` — added `<ServiceWorkerAssetsManifest>service-worker-assets.js</ServiceWorkerAssetsManifest>` (generates asset manifest with hashes at publish time)
- [x] Build: 0 errors, Tests: 323 pass (318 unit + 5 integration)

### Azure Functions Deployment Guide (2026-02-13)
- [x] Created `DEPLOYMENT-AZURE-FUNCTIONS.md` — comprehensive deployment guide covering:
  - Architecture overview (HTTP catch-all + 2 timer triggers)
  - Azure resource creation (Resource Group, Function App, SQL Server, Storage, App Insights)
  - Application Settings reference (all required + optional configuration keys)
  - Database setup (EF Core migrations — manual, not auto-migrate)
  - Deploy via Azure CLI (`func azure functionapp publish`)
  - Deploy via GitHub Actions CI/CD (build → test → deploy → migrate)
  - Deploy via Visual Studio / Rider
  - CORS configuration (Azure Portal, not in code)
  - Blazor WASM connection (appsettings.json BaseUrl)
  - Monitoring (Application Insights, KQL queries, live log streaming)
  - Scaling (Consumption vs Premium plan, timer trigger considerations)
  - Security checklist (Key Vault, Managed Identity, TLS, firewall)
  - Troubleshooting (common issues table)
  - Cost estimation (Consumption ~$11/month, Premium ~$215/month)
  - Quick reference deployment cheatsheet

### APPKA.md Re-Analysis v2 (2026-02-13)
- [x] Complete rewrite of mobile app strategy after Blazor WASM migration completion
- [x] Evaluated 3 options: **PWA** (1-2 days), **MAUI Blazor Hybrid** (3-6 weeks), **Native rewrite** (3-6 months)
- [x] Updated stats: 31 pages, 12 shared components, 15 API services, 323 tests (318 unit + 5 integration)
- [x] **Recommendation: PWA first** — trivial to add on existing WASM, covers 95% of mobile B2B needs
- [x] Detailed comparison table (effort, code sharing, offline, native API, App Store, maintenance)
- [x] Phased strategy: Phase 1 PWA (immediate) → Phase 2 mobile optimization (optional) → Phase 3 MAUI (only if business requires)
- [x] Technical notes: service worker caching, WASM bundle optimization, MudBlazor responsive, Quill mobile compatibility

### Blazor Server → Standalone WebAssembly Migration (2026-02-13)
- [x] **Phase 1: Fakvio.Contracts Library** — extracted 53 DTOs + Pagination + DueDateCalculator from Application into zero-NuGet-dependency Contracts library
  - Created `Fakvio.Contracts.csproj` (net10.0, references Domain for enums)
  - Updated `Application.csproj` → references Contracts
  - Updated `BlazorUI.csproj` → references Contracts + Domain (no Application)
  - Updated ~107 files across solution (using statement migration)
  - Added Contracts project to solution
- [x] **Phase 2: WASM Project Conversion** — converted BlazorUI from Blazor Server to Standalone WebAssembly
  - Changed SDK: `Microsoft.NET.Sdk.Web` → `Microsoft.NET.Sdk.BlazorWebAssembly`
  - Added NuGet packages: `Microsoft.AspNetCore.Components.WebAssembly`, `Microsoft.AspNetCore.Components.Authorization`, `Microsoft.Extensions.Http`, `Blazored.LocalStorage`
  - Rewrote `Program.cs` — `WebAssemblyHostBuilder` replacing `WebApplication.CreateBuilder`
  - Created `wwwroot/index.html` — static HTML host page (replaces server-rendered App.razor shell)
  - Created `wwwroot/appsettings.json` — configurable API base URL
  - Rewrote `App.razor` — pure Blazor router (merged with Routes.razor)
  - Removed `Routes.razor` (content merged into App.razor)
  - Removed `@rendermode InteractiveServer` from all 28 page files
  - Removed `@using static Microsoft.AspNetCore.Components.Web.RenderMode` from _Imports.razor
  - Updated `Error.razor` — removed server-only HttpContext dependency
- [x] **Phase 3: Authentication Rewrite** — replaced ProtectedSessionStorage with Blazored.LocalStorage
  - `CustomAuthenticationStateProvider` now uses `ILocalStorageService` for JWT persistence
  - All session operations (Get/Set/Delete) translated to localStorage equivalents
  - Impersonation data stored as plain strings in localStorage
- [x] **Phase 4: Culture Switching Rewrite** — replaced server-side cookie with localStorage
  - `LanguageSwitcher.razor` now saves culture to localStorage via JS interop
  - `Program.cs` reads stored culture on startup before rendering
  - Page reload applies new culture via `NavigateTo(uri, forceLoad: true)`
- [x] **Phase 5: CORS Configuration** — added WASM dev origins (localhost:7212, localhost:5145)
- [x] **Phase 6: Verification** — full solution builds (0 errors), all 319 tests pass (318 unit + 1 integration)
- [x] **Runtime Fixes** — resolved WASM startup crash and localization issues:
  - Added `<BlazorWebAssemblyLoadAllGlobalizationData>true</BlazorWebAssemblyLoadAllGlobalizationData>` — WASM needs full ICU data for dynamic culture switching
  - Fixed `AddLocalization()` — removed `ResourcesPath = "Resources"` (embedded resource name derives from C# type namespace, not file path)
  - Removed `AddCascadingAuthenticationState()` from Program.cs — conflicts with component-based `<CascadingAuthenticationState>` in App.razor
  - Added `window.blazorCulture` JS helper in index.html for safe localStorage culture access
  - Removed redundant `<CascadingAuthenticationState>` from MainLayout.razor
  - Changed `RedirectToLogin` to use SPA navigation (no `forceLoad: true` — avoids full WASM reload)

### Azure Functions Migration — Fakvio.Functions Project (2026-02-12)
- [x] Created `Fakvio.Functions` project (Azure Functions v4 Isolated Worker Model with ASP.NET Core Integration)
- [x] `Fakvio.Functions.csproj` — NuGet refs (Worker, Sdk, Http.AspNetCore, Timer) + project refs (API, Infrastructure, Application)
- [x] `Program.cs` — Duplicated DI registrations from API/Program.cs adapted for Functions hosting (no Swagger, no CORS, no auto-migration, no hosted services)
- [x] `HttpTriggerFunction.cs` — Catch-all HTTP trigger (`Route = "{*route}"`) routing all requests to ASP.NET Core pipeline (controllers discovered via `AddApplicationPart`)
- [x] `TimerFunctions.cs` — Timer triggers replacing `LogFlushService` (every 5s) and `LogCleanupService` (every 1h); uses reflection to access `internal` `DatabaseLoggerProvider.LogQueue`
- [x] `host.json` — `routePrefix: ""` to preserve `/api/*` routes; log level filtering for EF Core and ASP.NET Core
- [x] `local.settings.json` — Development settings (connection strings, JWT config, Azurite storage)
- [x] Added project to solution (`dotnet sln add`)
- [x] Full solution builds with 0 errors (only benign MSB3277 version alignment warnings)
- [x] All 312 existing tests pass (311 unit + 1 integration) — zero regressions
- [x] **Zero changes** to existing projects (API, Infrastructure, Application, Domain, BlazorUI, Tests)

### Due Date Calculation Fix — EDueDateCalculationType (2026-02-10)
- [x] **Bug**: `InvoiceService.CalculateDueDate()` and Blazor UI `RecalculateDueDate()` ignored `EDueDateCalculationType` — only ever used `DaysFromIssue` (simple AddDays)
- [x] Created `DueDateCalculator` shared helper in `Application/Common/DueDateCalculator.cs` — implements all 4 algorithms (DaysFromIssue, DaysFromEndOfMonth, EndOfNextMonth, EndOfCurrentMonth)
- [x] Updated `InvoiceService.CalculateDueDate()` to use `DueDateCalculator.Calculate()` with client's `BillingSettings.DueDateCalculationType`
- [x] Updated `InvoiceService.CreateAsync()` — auto-syncs `TaxableSupplyDate` (DUZP) with `IssueDate` (datum vystavení) when user didn't explicitly provide it
- [x] Updated Blazor `InvoiceDetail.razor`:
  - `RecalculateDueDate()` now accepts `EDueDateCalculationType` parameter and uses `DueDateCalculator`
  - `RecalculateDueDate()` also syncs `_createTaxableSupplyDate` to the new due date
  - `OnClientChanged()` passes `DueDateCalculationType` from client's BillingSettings
  - `OnIssueDateChanged()` passes `DueDateCalculationType` from client's BillingSettings
  - `InitCreateDtoFromTemplate()` passes `DueDateCalculationType` from client's BillingSettings
- [x] Added 18 unit tests in `DueDateCalculatorTests.cs` covering all 4 calculation types + edge cases (leap year, year boundary, 30-day months, zero days)

### ZMapper BaseEntity Property Fix (2026-02-07)
- [x] Fixed VatRateService.cs - Added MapToDto helper for Id, CreatedAt, UpdatedAt
- [x] Fixed CurrencyService.cs - Added MapToDto helper for Id, CreatedAt, UpdatedAt
- [x] Fixed UserService.cs - Added MapToDto helper for Id, CreatedAt, UpdatedAt, CompanyName, FullName
- [x] Fixed NumberSequenceService.cs - Added MapFormatToDto and MapSequenceToDto helpers for Id and nested format Id
- [x] Fixed ClientService.cs - Added MapToDto helper for Id, CreatedAt, UpdatedAt, and nested Address/Contact/BillingSettings Ids
- [x] Fixed InvoiceService.cs - Added MapToDto helper for Id, CreatedAt, UpdatedAt, ClientName, IssuerName, CurrencyCode, CurrencySymbol, OriginalInvoiceNumber, and nested InvoiceItem Ids
- [x] Fixed InvoiceTemplateService.cs - Added MapToDto helper for Id, CreatedAt, UpdatedAt, IssuerName, CurrencyCode, CurrencySymbol, and nested InvoiceItem Ids
- [x] Fixed DashboardService.cs - Added MapInvoiceToDto helper (same as InvoiceService) for Id, CreatedAt, UpdatedAt, navigation props, and nested InvoiceItem Ids
- [x] All 60 unit tests passing
- [x] Build succeeds with 0 errors

### UI Localization CZ/EN (2026-02-08)
- [x] Configured localization middleware in BlazorUI Program.cs (cs-CZ default, en-US supported)
- [x] Created SharedResource.cs marker class + .cs.resx (Czech) + .en.resx (English) resource files (~390 keys)
- [x] Added LanguageSwitcher component in MainLayout app bar (CZ/EN toggle)
- [x] Added /Culture/SetCulture minimal API endpoint with cookie-based culture provider
- [x] Localized all 13 Razor pages: NavMenu, MainLayout, Login, SetPassword, Home, VatRates, Clients, ClientDetail, Companies, Users, Currencies, InvoiceTemplates, Invoices, InvoiceItemEditor
- [x] Fixed duplicate InvoiceItem_ProductCode resource key
- [x] All 60 unit tests passing, build 0 errors

### Localization & Currency Fix (2026-02-08)
- [x] Fixed translations not showing (only key codes visible): removed `DefaultThreadCurrentUICulture = InvariantCulture` override
- [x] Renamed `SharedResource.cs.resx` → `SharedResource.resx` (default fallback for resource lookup)
- [x] Fixed duplicate CurrencyController (Controller/ + Controllers/) causing 500 error on templates page
- [x] Localized InvoiceTemplates.razor and Invoices.razor (were missed by previous agent)
- [x] All 61 tests passing, build 0 errors

### 8-Step Improvement Plan (2026-02-08/09)
- [x] **1. ZMapper v1.1.0 upgrade** — bumped NuGet, removed IgnoreNonExisting(), added ForMember Ignore for nav props, simplified all 8 service MapToDto helpers
- [x] **2. Client RegistrationNumber optional** — made nullable in entity/DTO/service/UI for physical persons without IČO
- [x] **3. Billing Settings UI** — added collapsible billing settings panel (DueDays, prefixes/suffixes, payment method, bank account, notes) to Client create/edit dialog
- [x] **4. Email templates** — new EmailTemplate entity + EEmailType enum + DTOs + IEmailTemplateService + EmailTemplateService (CRUD + Handlebars rendering) + API controller + Blazor page with Quill editor + 4 seed templates; refactored EmailService to use templates with fallback
- [x] **5. Invoice document number at creation** — moved GenerateDocumentNumberAsync from CompleteInvoiceAsync to CreateInvoiceAsync; VariableSymbol auto-filled
- [x] **6. Remove Issuer from detail** — hidden when single issuer, auto-selected in create dialog
- [x] **7. Fix "Create Template from Invoice"** — implemented CreateFromInvoiceAsync in InvoiceTemplateApiService, wired button to POST /api/invoicetemplate/from-invoice/{id}
- [x] **8. Inline editable grid** — rewrote InvoiceItemEditor: replaced dialog-based editing with inline MudTable rows (MudTextField/MudNumericField/MudSelect per cell)
- [x] 15 new EmailTemplateService unit tests (75 total unit + 1 integration)
- [x] Build: 0 errors, Tests: 76 pass

### 5-Fix Improvement Plan (2026-02-09)
- [x] **1. EPaymentMethod enum** — new `Domain/Enums/EPaymentMethod.cs` (BankTransfer, Cash, CreditCard, PayPal, Other); changed `string?` → `EPaymentMethod?` in BillingSettings, Invoice, and all DTOs (10 files); updated DbContext from `HasMaxLength(200)` → `HasConversion<int?>()`; EF migration `PaymentMethodStringToEnum`; updated PdfExportService placeholder, PdfExportServiceTests
- [x] **2. Reusable components** — new `EnumSelect.razor` (generic localized enum dropdown with nullable support, resource key convention `{EnumType}_{Value}`); new `BankAccountInput.razor` (MudTextField wrapper for future IBAN validation); added `_Imports.razor` reference; replaced manual MudSelect in Clients.razor (DueDateCalcType + PaymentMethod) and Invoices.razor (PaymentMethod + BankAccount); added resource keys for `EDueDateCalculationType_*` and `EPaymentMethod_*`
- [x] **3. Localize Invoices.razor** — replaced ~15 hardcoded strings with resource keys: TaxableSupplyDate, VariableSymbol, Notes, Close, Create buttons; all snackbar messages (Invoice_Created, Invoice_Deleted, Invoice_Issued, Invoice_MarkedAsPaid, Invoice_PdfDownloaded, Invoice_PdfFailed, Invoice_EmailSentTo, Invoice_EmailFailed, Invoice_AddAtLeastOneItem); localized GetStatusText helper with resource keys (Invoice_StatusDraft/Issued/Paid/Credited/Deleted)
- [x] **4. InvoiceItemEditor in Invoices.razor** — replaced card-based item rendering (MudPaper per item) with shared `<InvoiceItemEditor>` component; removed AddInvoiceItem/RemoveInvoiceItem methods; removed _vatRates field + LoadVatRates + VatRateApiService injection (InvoiceItemEditor loads its own VAT rates)
- [x] **5. InvoiceTemplateApiService → ApiClientBase** — refactored to inherit ApiClientBase (like VatRateApiService); removed 50 lines of duplicate auth code; replaced anonymous `new { TemplateName }` with typed `CreateTemplateFromInvoiceDto`; all methods now use base class GetAsync/PostAsync/PutAsync which throw HttpRequestException with error details instead of silently returning null
- [x] Added missing resource keys: `Billing_Title`, `Billing_PaymentMethod`, `EPaymentMethod_*` (5 values), `EDueDateCalculationType_*` (4 values)
- [x] Build: 0 errors (0 warnings), Tests: 76 pass (75 unit + 1 integration)

### 6-Fix Improvement Plan (2026-02-09)
- [x] **1. ZMapper v1.2.0 upgrade** — bumped NuGet from v1.1.0 to v1.2.0; removed `using ZMapper.Abstractions.Configuration;` from all 8 mapper profiles (only `using ZMapper;` needed now); added `using ZMapper;` to API Program.cs for `AddZMapper()` resolution
- [x] **2. Invoice Detail dialog size** — changed from `MaxWidth.Large` to `MaxWidth.ExtraLarge` for better usability; `FullWidth=true` ensures mobile responsiveness
- [x] **3. AddItem button above grid** — moved from below the MudTable to a header row above; placed in flex container alongside the "Invoice Items" heading for easy access
- [x] **4. Fix FakvioService endpoint mismatches** — `IssueAsync` now calls `/complete` (was `/issue`); `MarkAsPaidAsync` now calls `/mark-paid` (was `/mark-as-paid`); removed dead `CancelAsync` method (no matching API endpoint)
- [x] **5. NumberSequences page** — created `NumberSequenceApiService.cs` (inherits ApiClientBase, all 8 endpoints); created `NumberSequences.razor` page (formats table + sequences table with preview, create dialogs, set-default, deactivate); registered service in BlazorUI Program.cs; added ~25 resource keys (`NumSeq_*`) to both .resx files
- [x] **6. InvoiceTemplates detail fixes** — moved `[Inject] IDialogService` from after @code to proper @inject directive; removed duplicate local `ClientDto`/`CurrencyDto` classes (now uses imported Application DTOs); added `@using Fakvio.Application.Dto.Client`; localized ~15 hardcoded Czech strings to resource keys (`L["Msg_Error"]`, `L["Msg_SaveSuccess"]`, `L["Msg_DeleteSuccess"]`, `L["Invoice_Created"]`, `L["Invoice_AddAtLeastOneItem"]`, `L["Msg_ConfirmDeleteTitle"]`, `L["Template_ConfirmDelete"]`)
- [x] Build: 0 errors (0 warnings), Tests: 76 pass (75 unit + 1 integration)

### 4-Fix UX Improvement Plan (2026-02-09)
- [x] **1. InvoiceTemplates page NOT WORKING** — ROOT CAUSE: missing `@rendermode InteractiveServer` directive (page had zero interactivity — no button clicks, no dialogs); also fixed `@bind-IsVisible` → `@bind-Visible` (MudBlazor v8 renamed the parameter)
- [x] **2. Clients edit/save** — added proper View/Edit/Save workflow: readonly mode shows `ReadOnly` fields + Edit button; Edit mode enables all fields + Save/Cancel buttons; new `UpdateClient()` method calls `ClientService.UpdateAsync()` with full DTO including addresses, contacts, and billing settings; extended `UpdateClientDto` with Address/Contact/BillingSettings collections; extended `ClientService.UpdateClientAsync()` to handle nested entity updates (replace addresses, contacts; merge billing settings)
- [x] **3. Invoice Detail full page** — created new `InvoiceDetail.razor` at `/invoices/{id:long}` replacing the cramped dialog; three-column card layout (info, payment, totals) + items table + notes; supports editing Draft invoices (Save/Cancel/Issue/Delete buttons); PDF download and email sending integrated; removed `ViewInvoice` dialog from Invoices.razor; view button now navigates to detail page
- [x] **4. Row click to open detail** — added `OnRowClick` handler to Invoices grid (navigates to `/invoices/{id}`) and Clients grid (opens detail dialog); tables show pointer cursor for discoverability
- [x] Added `Disabled` parameter to EnumSelect and BankAccountInput shared components
- [x] Added localization keys: Invoice_PaymentInfo, Invoice_Summary, Invoice_PaidOn, Invoice_Draft (CZ + EN)
- [x] Removed duplicate Invoice_StatusCredited/Invoice_StatusDeleted resource entries
- [x] Build: 0 errors, Tests: 76 pass (75 unit + 1 integration)

### Grid Edit Consistency + Content Template System (2026-02-09)
- [x] **Part 1A: VatRates Edit UI** — added edit icon button in row actions, separate edit dialog with UpdateVatRateDto, OpenEditDialog + UpdateVatRate methods; all resource keys already existed
- [x] **Part 1B: NumberSequences Update Backend + Edit UI** — created UpdateNumberSequenceFormatDto (Name, FormatPattern) + UpdateNumberSequenceDto (Name, Prefix, Suffix); implemented UpdateFormatAsync + UpdateSequenceAsync in service; added PUT endpoints to controller; added edit dialogs to NumberSequences.razor; added NumSeq_EditFormat/NumSeq_EditSequence resource keys
- [x] **Part 1C: NumberSequence Update Tests** — 18 new tests: UpdateFormat (5), UpdateSequence (6), Create/Get/Preview/Deactivate/Validate (7)
- [x] **Part 2: Content Template System** — unified HTML template management replacing separate EmailTemplate + InvoiceTemplate.HtmlTemplate:
  - New `ContentTemplate` entity + `EContentTemplateType` enum (InvoicePdf=1, CreditNotePdf=2, InvoiceEmail=10, CreditNoteEmail=11, InvitationEmail=20, ReminderEmail=21, PasswordResetEmail=22)
  - New `ContentTemplateService` (CRUD + Handlebars rendering), `ContentTemplateController`, `ContentTemplateApiService`
  - New `ContentTemplates.razor` page with type-group filtering (PDF/Email/System), Quill editor, placeholder docs
  - Updated `PdfExportService` to resolve templates from ContentTemplate (with fallback)
  - Updated `EmailService` to use IContentTemplateService instead of IEmailTemplateService
  - Removed HtmlTemplate from InvoiceTemplate entity/DTOs/service/UI
  - Deleted old files: EmailTemplate entity, EEmailType enum, IEmailTemplateService, EmailTemplateService, EmailTemplateController, EmailTemplateApiService, EmailTemplates.razor, EmailTemplateProfile, EmailTemplateDto
  - Removed old EmailTemplate DbSet, config, and seeds from ApplicationDbContext
  - Renamed nav link: Email Templates → Content Templates
  - Added 22 localization keys (ContentTemplate_*, ContentType_*, Label_All, Nav_ContentTemplates) in CZ + EN
  - Rewrote EmailTemplateServiceTests → ContentTemplateServiceTests (16 tests)
  - Updated PdfExportServiceTests (mock IContentTemplateService)
  - Updated EmailServiceTests (mock IContentTemplateService)
- [x] Build: 0 errors, Tests: 95 pass (94 unit + 1 integration)

### Reusable AppDataGrid + Service Consistency (2026-02-10)
- [x] **Phase 1A: ApiClientBase extensions** — added 4 new protected methods: `GetBytesAsync` (PDF downloads), `PostWithoutBodyAsync<TResponse>` (POST without body), `PostBoolAsync<TRequest>` (POST returning bool), `PutBoolAsync<TRequest>` (PUT returning bool)
- [x] **Phase 1B: Refactored 5 services to inherit ApiClientBase** — FakvioService, ClientApiService, CompanyApiService, UserApiService, DashboardApiService; removed ~25 lines of duplicate auth/HttpClient logic from each
- [x] **Phase 1C: Created CurrencyApiService** — new service inheriting ApiClientBase with CRUD + paged methods; registered in DI
- [x] **Phase 1D: Fixed InvoiceItemEditor.razor** — replaced raw `HttpClient` injection with `VatRateApiService`
- [x] **Phase 1E: Fixed Currencies.razor** — added `@rendermode InteractiveServer`, replaced raw `HttpClient` with `CurrencyApiService`, fixed `@bind-IsVisible` → `@bind-Visible`
- [x] **Phase 1F: Fixed InvoiceTemplates.razor** — replaced raw `HttpClient` + manual auth headers with typed API services (InvoiceTemplateApiService, ClientApiService, CompanyApiService, CurrencyApiService); added missing methods to InvoiceTemplateApiService (GetPagedAsync, DeleteAsync, CreateInvoiceFromTemplateAsync)
- [x] **Phase 2: Created AppDataGrid<TItem> shared component** — reusable generic data grid with: both client-side (Items) and server-side (ServerData) data modes; auto-generated Actions column with Edit/Delete icons + stopPropagation; consistent Dense+Hover+Striped+pointer styling; RenderFragment parameters for Columns, RowContent, RowActions, ToolBarContent; public ReloadServerDataAsync() method
- [x] **Phase 3: Refactored all 9 grid pages to use AppDataGrid** — VatRates, Currencies, Users, InvoiceTemplates, Clients, Invoices, Companies, ContentTemplates, NumberSequences (2 grids: Formats + Sequences)
- [x] **Phase 4: Localization** — added `Msg_NoRecords` key to both CZ and EN resource files
- [x] Build: 0 errors, Tests: 95 pass (94 unit + 1 integration)

### 4-Fix Bugfix + UX Improvements (2026-02-10)
- [x] **1. IssuerId=0 bug fix** — ROOT CAUSE: `ClientApiService.GetAllAsync()` returns only non-issuers (`!IsIssuer`), so searching `_allClients` for issuer always returned null; FIX: added `GetIssuerAsync()` to ClientApiService (calls `GET /api/client/issuer` which auto-resolves from JWT CompanyId); Invoices.razor loads `_issuer` at init via `ClientApiService.GetIssuerAsync()`, removed issuer dropdown entirely; InvoiceTemplates.razor also uses `GetIssuerAsync()` (replaced CompanyApiService which is SysAdmin-only), removed issuer dropdown from template dialog; `OpenCreateDialog()` guards against null issuer with `Invoice_NoIssuerFound` snackbar error; added CZ+EN resource keys
- [x] **2. Two-tier dialog sizing** — added `.dialog-large` CSS class (85vw width, 85vh max-height) in app.css; applied `MaxWidth.False` + `BackgroundClass = "dialog-large"` to complex form dialogs (Companies, Clients, InvoiceTemplates, ContentTemplates, Invoices create); small dialogs (VatRates, Currencies, NumberSequences, Users) unchanged at `MaxWidth.Medium`
- [x] **3. InvoiceTemplate fields alignment** — removed `Category` from DTOs (CreateInvoiceTemplateDto, UpdateInvoiceTemplateDto, InvoiceTemplateDto) and UI; added `ClientId` + `ClientName` to DTOs + mapping + service queries (`.Include(t => t.Client)`); template dialog now organized in 6 sections: Template Info, Document Settings (DocumentType, Issuer, Client, Currency), Payment Settings (PaymentMethod, DueDays, symbols), Bank Account (BankAccountNumber, IBAN, SWIFT), Notes, Items; removed Category column from grid, added Client column; `CreateTemplateFromInvoiceAsync` now copies `ClientId` from invoice; `OpenCreateInvoiceDialog` pre-fills ClientId from template
- [x] **4. BlazorHtmlEditor assessment** — BlazorHtmlEditor NuGet (v1.0.1, ~409 downloads) is a Monaco-based Razor code editor, NOT a WYSIWYG editor — not suitable for content template editing; upgraded Blazored.TextEditor from v1.1.0 → v1.1.3 (latest, .NET 9 targeting)
- [x] Added 6 resource keys: `Invoice_NoIssuerFound`, `Template_InfoSection`, `Template_DocumentSection`, `Template_ConstantSymbol`, `Template_SpecificSymbol` (CZ + EN)
- [x] Build: 0 errors, Tests: 96 pass (95 unit + 1 integration)

### Full-Page Detail Views for All Entity Types (2026-02-10)
- [x] **Phase 1: Invoice** — extended `InvoiceDetail.razor` with `@page "/invoices/create"` route; added create mode with 3-column cards (Document Settings, Dates, Payment Settings) + items editor + notes; `SaveNewInvoice()` navigates to `/invoices/{result.Id}` on success; removed create dialog from `Invoices.razor` (removed `_showDialog`, `_currentInvoice`, date fields, `SaveInvoice()`, `CloseDialog()`, `_issuer`, `LoadIssuer()`); create button now navigates to `/invoices/create`
- [x] **Phase 2: Client** — expanded `ClientDetail.razor` with `@page "/clients/create"` route + full create/edit/view modes; `_isFormMode => _isCreateMode || _isEditing` pattern; 3-column cards (Company Info with ARES button, Address, Contacts) + billing settings expansion panel; invoice history section only for existing clients; removed entire dialog (~250 lines) from `Clients.razor`; create button and row click navigate to detail page
- [x] **Phase 3: InvoiceTemplate** — created new `InvoiceTemplateDetail.razor` with dual routes + create/view/edit modes; 6 sections (Template Info, Document Settings, Payment Settings, Bank Account, Notes, Items) with local `TemplateEditModel` class; removed template create/edit dialog from `InvoiceTemplates.razor` (kept "Create Invoice from Template" small popup); OnRowClick and OnEdit navigate to `/invoice-templates/{id}`
- [x] **Phase 4: ContentTemplate** — created new `ContentTemplateDetail.razor` with dual routes; Quill WYSIWYG editor with `_quillNeedsLoad` flag and `OnAfterRenderAsync` lifecycle for loading HTML; type helpers (`IsEmailType`, `IsPdfType`, `GetTemplateTypeColor`, `GetTemplateTypeLabel`); placeholder reference panel; removed create/edit dialog from `ContentTemplates.razor`; OnRowClick and OnEdit navigate to detail page
- [x] **Phase 5: Company** — created new `CompanyDetail.razor` with dual routes + `[Authorize(Roles = "SysAdmin")]`; 3-column cards (Company Info with ARES, Address, Contacts & Bank) + create-only user invitation section; removed entire dialog (~300 lines) from `Companies.razor`; create button and row click navigate to detail page
- [x] Consistent pattern across all 5 entities: `@page "/entity/create"` (Id=0 → create) + `@page "/entity/{Id:long}"` (Id>0 → view/edit)
- [x] Small dialogs unchanged: VatRates, Currencies, NumberSequences, Users, "Create Invoice from Template"
- [x] Build: 0 errors, Tests: 96 pass (95 unit + 1 integration)

### 4 Invoice & Data Management Fixes (2026-02-10)
- [x] **Fix 1: Document Number on Invoice pages** — added `CustomDocumentNumber` field in create mode (with auto-generation hint), `DocumentNumber` readonly in edit mode, `DocumentNumber` as first item in view mode info card
- [x] **Fix 2: Dropdown "New Invoice" with Template option** — replaced single "New" button with `MudMenu` dropdown (New Blank Invoice → navigates to `/invoices/create`, New from Template → opens dialog with template selector, client selector, dates, auto-complete toggle); added `GetAllActiveAsync()` to InvoiceTemplateApiService; templates loaded at init alongside clients
- [x] **Fix 3: Email Sent indicator** — added `Invoice_EmailColumn` header + email icon column to Invoices grid (green `MarkEmailRead` icon with tooltip showing date if sent, dash if not); added `MudChip` with sent date in InvoiceDetail header next to status chip
- [x] **Fix 4: Soft Delete audit** — converted Currency `DeleteCurrencyAsync` from hard delete (`Remove()`) to soft delete (`IsActive = false`), removed FK reference guards (safe with soft delete); modified `InvoiceService.GetInvoicesPagedAsync` to only hide deleted invoices when no status filter is set (previously always excluded); added "Deleted" filter option to Invoices status dropdown; Currencies page already had active/inactive filter
- [x] Added 11 resource keys (CZ + EN): Invoice_DocumentNumber, Invoice_AutoGeneratedHint, Invoice_NewBlank, Invoice_NewFromTemplate, Invoice_SelectTemplate, Invoice_AutoComplete, Invoice_EmailColumn, Invoice_EmailSentAt, Invoice_EmailNotSent, Invoice_FilterDeleted
- [x] New tests: `CurrencyServiceTests.cs` (7 tests: soft delete, soft delete with FK references, not found, active filtering, paged filtering); `InvoiceServiceFilterTests.cs` (4 tests: exclude deleted by default, show deleted when filtered, show only drafts, total count excludes deleted)
- [x] Build: 0 errors, Tests: 106 pass (105 unit + 1 integration)

### 8-Point UX & Data Quality Fixes (2026-02-10)
- [x] **Fix 1: VatRates Dialog ESC key** — added `DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true }` to both create and edit `MudDialog` elements; ESC key and X button now work for dismissing both dialogs
- [x] **Fix 2: VariableSymbol digits-only validation** — changed `[StringLength(50)]` → `[StringLength(10)] + [RegularExpression(@"^\d{0,10}$")]` on all 5 DTO VariableSymbol properties (CreateInvoiceDto, UpdateInvoiceDto, CreateInvoiceTemplateDto, UpdateInvoiceTemplateDto, CreateInvoiceFromTemplateDto); updated InvoiceService auto-fill to strip non-digits and truncate to 10 (`Where(char.IsDigit).Take(10)`); added `ValidateVariableSymbol()` helper + MaxLength + HelperText to InvoiceDetail.razor and InvoiceTemplateDetail.razor
- [x] **Fix 3: Split button for "New Invoice"** — replaced `MudMenu` with `MudButtonGroup` split button: primary click → `/invoices/create`, dropdown arrow → "New from Template" dialog
- [x] **Fix 4: Simplified "New from Template" dialog** — removed date pickers, notes, auto-complete toggle from dialog; kept only Template selector + Client selector; navigate to `/invoices/{id}?edit=true` after creation so user can review and adjust; added `[SupplyParameterFromQuery] bool Edit` to InvoiceDetail.razor that auto-enters edit mode on load
- [x] **Fix 5: NumberSequence on InvoiceTemplate** — added `NumberSequenceId` + `NumberSequence` nav property to InvoiceTemplate entity; configured FK in DbContext (Restrict, optional); added `NumberSequenceId` to Create/Update/Response DTOs; `NumberSequenceName` in InvoiceTemplateDto (set manually, ignored in ZMapper profile); `.Include(t => t.NumberSequence)` in all template queries; NumberSequence dropdown in InvoiceTemplateDetail.razor Document Settings card; EF migration `AddNumberSequenceToInvoiceTemplate`
- [x] **Fix 6: NumberSequenceId passthrough + document number** — added `NumberSequenceId` to CreateInvoiceDto; `GenerateDocumentNumberAsync` accepts optional `overrideSequenceId` parameter (priority: override > issuer settings > default); `InvoiceTemplateService.CreateInvoiceFromTemplateAsync` passes `template.NumberSequenceId` to invoice creation
- [x] **Fix 7: Test coverage** — `InvoiceTemplateServiceTests.cs` (12 tests: create valid/calculated/invalid issuer/with sequence ID, create-from-template valid/invalid/inactive/usage count/copy fields/VS override, create-template-from-invoice, soft delete); `InvoiceServiceCreateTests.cs` (12 tests: create valid/totals/auto doc number/custom doc number, VS auto-fill digits/truncate/explicit, invalid client/issuer/credit note, delete draft/delete completed)
- [x] **Fix 8: BaseEntity architecture comment** — added XML doc comment explaining why soft-delete is NOT on BaseEntity (different entities use different strategies: Status enum vs IsActive boolean)
- [x] Added 6 resource keys (CZ + EN): Invoice_VSMaxLength, Invoice_VSDigitsOnly, Template_NumberSequence
- [x] Build: 0 errors, Tests: 130 pass (129 unit + 1 integration)

### 5 UX Enhancements — Invoice Creation, Unsaved Changes, Credit Notes (2026-02-10)
- [x] **Feature 1: Document number preview** — added `PreviewNextNumberForDocumentTypeAsync` to INumberSequenceService/NumberSequenceService (resolves default sequence, calls PreviewNextNumberAsync); new `GET /api/numbersequence/preview-by-type` endpoint; `PreviewByDocumentTypeAsync` in NumberSequenceApiService; InvoiceDetail.razor create mode shows `"Next: INV-2026-043 (leave empty for auto)"` helper text on CustomDocumentNumber field
- [x] **Feature 2: Client change auto-fills billing + due date** — InvoiceDetail.razor create mode: changed client selector from `@bind-Value` to reactive `ValueChanged` → `OnClientChanged()` auto-fills PaymentMethod, BankAccountNumber, and recalculates DueDate from client's BillingSettings.DueDays; issue date picker changed to reactive `DateChanged` → `OnIssueDateChanged()` recalculates DueDate; `RecalculateDueDate(dueDays)` helper used by both
- [x] **Feature 3: Unsaved changes warning** — created `UnsavedChangesLock.razor` shared component (uses Blazor `<NavigationLock>` for internal nav + `ConfirmExternalNavigation` for tab close/refresh); added `_isDirty` tracking to 5 pages: InvoiceDetail (create/edit), ClientDetail, InvoiceTemplateDetail, ContentTemplateDetail, CompanyDetail; dirty=true on edit enable/field change, dirty=false on save/cancel
- [x] **Feature 4: Credit Notes evidence** — added "Credit Notes" nav link (`/invoices?type=CreditNote`); Invoices.razor: `[SupplyParameterFromQuery]` TypeFilter, `_isCreditNoteView` flag, dynamic page title/subtitle/button labels, `_filterDocumentType` passed to API, DocumentType filter dropdown; InvoiceDetail.razor: `TypeParam` query param → pre-sets `DocumentType=CreditNote` in create mode, loads `_completedInvoices` for OriginalInvoiceId dropdown, shows Original Invoice link in view mode
- [x] **Feature 5: Template pre-fill client** — Invoices.razor "New from Template" dialog: changed template selector from `@bind-Value` to reactive `ValueChanged` → `OnTemplateSelected()` auto-fills ClientId from template's default ClientId
- [x] 4 new unit tests: `PreviewNextNumberForDocumentTypeAsync` (returns preview for Invoice/CreditNote, returns null when no default, does not increment counter)
- [x] 8 new resource keys (CZ + EN): Invoice_NextNumberHint, Msg_UnsavedChanges, Nav_CreditNotes, CreditNote_New, CreditNote_Detail, CreditNote_OriginalInvoice, CreditNote_Subtitle
- [x] Build: 0 errors, Tests: 135 pass (134 unit + 1 integration)

### 3 UX Fixes — Document Number, Edit Layout, Template Flow (2026-02-10)
- [x] **Fix 1: Pre-fill document number** — changed from showing preview as HelperText to actually pre-filling `CustomDocumentNumber` field with the generated number from default sequence; supports custom NumberSequenceId from template; user can still clear and type a custom number
- [x] **Fix 2: Edit form layout matches create form** — redesigned edit mode from cramped single-column layout to same 3-column card layout as create mode: Left=Document settings (readonly: DocumentNumber, Client, Issuer, DocumentType, Currency), Middle=Dates (readonly IssueDate + editable DueDate/TaxableSupplyDate), Right=Payment (editable: PaymentMethod, VariableSymbol, BankAccount); Items editor and Notes as full-width sections below
- [x] **Fix 3: "New from Template" no DB save** — changed from creating a DB record then navigating to detail, to navigating to `/invoices/create?templateId={id}&clientId={id}` with query params; InvoiceDetail.razor loads template via `GetByIdAsync()` and pre-fills entire create form (DocumentType, IssuerId, CurrencyId, PaymentMethod, BankAccount, VariableSymbol, Notes, NumberSequenceId, InvoiceItems, DueDateOffsetDays → calculated dates); invoice is only saved when user explicitly clicks Save
- [x] Build: 0 errors, Tests: 135 pass (134 unit + 1 integration)

### Multi-Tenant Roadmap — Phase 0: Security Pre-requisites (2026-02-10)
- [x] **0.1 JWT Secret out of config** — JWT secret already loaded from User Secrets / env vars (throws `InvalidOperationException` on startup if missing); added comment in `appsettings.json` warning not to store secret there
- [x] **0.2 CORS restriction** — replaced `AllowAnyOrigin()` with environment-specific policy; `CorsSettings:AllowedOrigins` array in appsettings; dev uses localhost ports; prod with no configured origins blocks all cross-origin; `AllowCredentials()` for cookie/SignalR support
- [x] **0.3 NumberSequence concurrency control** — added `byte[] RowVersion` to `NumberSequence` entity; configured `.IsRowVersion().IsConcurrencyToken()` in DbContext; rewrote `GenerateNextNumberAsync` with retry loop (`MaxConcurrencyRetries = 3`), `DbUpdateConcurrencyException` catch, entity detach; EF migration `AddNumberSequenceRowVersion`
- [x] **0.4 AsNoTracking for read-only queries** — added `.AsNoTracking()` to all GET/List methods across 10 services: InvoiceService (5 methods), ClientService (5), VatRateService (5), CurrencyService (4), NumberSequenceService (4), DashboardService (1), UserService (4), InvoiceTemplateService (3), ContentTemplateService (already had it)
- [x] **0.5 Audit trail (ICurrentUserService)** — created `ICurrentUserService` interface + `CurrentUserService` implementation (reads `ClaimTypes.NameIdentifier` from JWT); injected into `ApplicationDbContext` via second constructor; `UpdateTimestamps()` now auto-fills `CreatedByUserId` (on insert only) and `UpdatedByUserId` (on every save); added `AddHttpContextAccessor()` + `AddScoped<ICurrentUserService>()` to DI; added `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to Infrastructure.csproj
- [x] **Tests** — 6 new audit trail tests (CreatedByUserId on create, UpdatedByUserId on create/update, anonymous user leaves null, no ICurrentUserService still works, RowVersion property exists); fixed RowVersion in NumberSequence test seed data (2 files); removed duplicate `using` in Program.cs
- [x] Build: 0 errors, Tests: 161 pass (160 unit + 1 integration)

### Multi-Tenant Roadmap — Phase 1: SQLite to PostgreSQL Migration (2026-02-10)
- [x] **1.1 NuGet package swap** — removed `Microsoft.EntityFrameworkCore.Sqlite`, added `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0`; aligned EF Core Design/Tools to 10.0.0; aligned test InMemory to 10.0.0
- [x] **1.2 DbContext provider change** — `UseSqlite(...)` → `UseNpgsql(...)` in Program.cs; connection string throws `InvalidOperationException` if missing; updated `appsettings.json` (PostgreSQL prod template) and `appsettings.Development.json` (local dev connection)
- [x] **1.3 DesignTimeFactory update** — updated `ApplicationDbContextFactory.cs` to use `UseNpgsql(...)` for EF Core tooling
- [x] **1.4 RowVersion → xmin concurrency token** — changed `byte[] RowVersion` → `uint xmin` on NumberSequence entity; configured `.HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken()` in DbContext (PostgreSQL uses built-in xmin system column for optimistic concurrency)
- [x] **1.5 Archived SQLite migrations** — moved 14 old SQLite migrations to `Migrations_SQLite_Archive/` folder; excluded from compilation via `<Compile Remove="Migrations_SQLite_Archive/**" />` in csproj
- [x] **1.6 Fresh PostgreSQL initial migration** — created `InitialPostgreSQL` migration with PostgreSQL-native types: `bigint` IDs with `IdentityByDefaultColumn`, `timestamp with time zone` for DateTime, `boolean` for bools, `character varying(n)` for strings, `xid` for xmin concurrency token
- [x] **1.7 Updated comments** — removed SQLite-specific comments from Program.cs (invariant culture)
- [x] Build: 0 errors, Tests: 161 pass (160 unit + 1 integration)
- [x] Note: MSB3277 warning (EF Core Relational 10.0.0 vs 10.0.1 transitive dependency) is benign — will resolve when Npgsql publishes 10.0.1

### Multi-Tenant Roadmap — Phase 2: Multi-Tenant Foundation Infrastructure (2026-02-10)
- [x] **2.1 CompanySystemSettings entity** — new `Domain/Entities/CompanySystemSettings.cs` with CompanyId (FK→Client), DatabaseName, ConnectionString?, ProvisionedAt?, IsProvisioned, IsActive, MaxUsers?, AdminNotes?; added DbSet + configuration to ApplicationDbContext (unique index on CompanyId, Restrict delete)
- [x] **2.2 ITenantResolver + HttpContextTenantResolver** — `Application/Service/ITenantResolver.cs` interface with `GetCurrentCompanyId()` + `IsSysAdmin()`; `Infrastructure/Service/HttpContextTenantResolver.cs` reads "CompanyId" custom claim from JWT (same claim used by normal users and SysAdmin impersonation)
- [x] **2.3 MasterDbContext** — `Infrastructure/Data/MasterDbContext.cs` with 7 DbSets (User, Client, CompanySystemSettings, VatRate, Currency, NumberSequenceFormat, ContentTemplate); explicitly ignores tenant-only entities (Address, Contact, BillingSettings, Invoice, etc.); audit trail support; seed data for SysAdmin user + code tables; `MasterDesignTimeFactory.cs` for EF CLI
- [x] **2.4 TenantDbContext** — `Infrastructure/Data/TenantDbContext.cs` with 12 DbSets (Client, Address, Contact, BillingSettings, Invoice, InvoiceTemplate, InvoiceItem, NumberSequence, NumberSequenceFormat, Currency, VatRate, ContentTemplate, AresCache); explicitly ignores master-only entities (User, CompanySystemSettings); xmin concurrency token on NumberSequence; audit trail support; seed data for code tables + default sequences; `TenantDesignTimeFactory.cs` for EF CLI
- [x] **2.5 ITenantDbContextFactory** — `Application/Service/ITenantDbContextFactory.cs` interface with `CreateContextAsync()` (from JWT), `CreateContextForCompanyAsync(companyId)` (explicit), `GetConnectionStringAsync(companyId)`; `Infrastructure/Service/TenantDbContextFactory.cs` implementation resolves CompanySystemSettings from master DB, validates provisioned+active status, builds connection string from template or custom override via NpgsqlConnectionStringBuilder
- [x] **2.6 Dual migration setup** — Master migrations in `Migrations/Master/` (7 tables), Tenant migrations in `Migrations/Tenant/` (12 tables); both PostgreSQL-native types (bigint, timestamp with time zone, xid for xmin)
- [x] **2.7 Connection string configuration** — added `MasterConnection` and `TenantTemplateConnection` to `appsettings.json` (production) and `appsettings.Development.json` (local dev); `DefaultConnection` retained during transition period
- [x] **2.8 Unit tests** — 10 TenantResolverTests (CompanyId resolution, SysAdmin detection, anonymous, null context, invalid claim); 8 TenantDbContextFactoryTests (no CompanyId, no settings, not provisioned, inactive, null connection, build from template, custom connection, resolver integration); 6 CompanySystemSettingsTests (create, defaults, unique index, correct indexes, required fields, FK to Client)
- [x] Build: 0 errors, Tests: 185 pass (184 unit + 1 integration)
- [x] ApplicationDbContext still functional (coexists during Phase 2 transition)

### Multi-Tenant Roadmap — Phase 3: Service Layer Migration (2026-02-10)
- [x] **3.1 AuthService + UserService → MasterDbContext** — replaced all `ApplicationDbContext` references with `MasterDbContext` (field type, constructor parameter, using directive); these services operate on Users and Companies which live in the master database
- [x] **3.2 Tenant services → TenantDbContext** — replaced `ApplicationDbContext` → `TenantDbContext` in 10 services: NumberSequenceService, InvoiceTemplateService, DashboardService, CurrencyService, VatRateService, ClientService, InvoiceService, EmailService, PdfExportService, ContentTemplateService; also swapped AresCacheRepository
- [x] **3.3 Comment updates** — updated `ICurrentUserService` and `CurrentUserService` doc comments from "ApplicationDbContext" to "MasterDbContext/TenantDbContext" (no functional change, just comment accuracy)
- [x] **3.4 DI registration + Program.cs update** — registered `MasterDbContext` (MasterConnection), `TenantDbContext` (TenantTemplateConnection fallback), `ITenantResolver` → `HttpContextTenantResolver`, `ITenantDbContextFactory` → `TenantDbContextFactory`; updated startup migration block to use `MasterDbContext` instead of `ApplicationDbContext`; kept `ApplicationDbContext` temporarily for backward compatibility
- [x] **3.5 Unit tests updated** — updated 12 test files: AuthServiceTests + UserInvitationTests → `MasterDbContext`; NumberSequenceServiceTests, DashboardServiceTests, EmailTemplateServiceTests, VatRateServiceTests, CurrencyServiceTests, InvoiceServiceFilterTests, PdfExportServiceTests, InvoiceServiceCreateTests, InvoiceTemplateServiceTests, EmailServiceTests → `TenantDbContext`; AuditTrailTests left on ApplicationDbContext (still valid, tests the base audit mechanism)
- [x] Build: 0 errors, Tests: 185 pass (184 unit + 1 integration)
- [x] ApplicationDbContext still registered in DI (transition period) — will be removed in a future phase when all consumers are verified

### Multi-Tenant Roadmap — Phase 4: Tenant Lifecycle & Middleware (2026-02-10)
- [x] **4.1 TenantProvisioningService** — `ITenantProvisioningService` interface (5 methods: Provision, Activate, Deactivate, Migrate, MigrateAll); `TenantProvisioningService` implementation with 9-step provisioning flow: CREATE DATABASE → Apply migrations → Copy code tables (VatRate, Currency, NumberSequenceFormat, ContentTemplate) → Create issuer + addresses + contacts → Create default number sequences (Invoice + CreditNote) → Mark provisioned; SQL injection prevention via `SanitizeDatabaseName`; `MigrateAllTenantsAsync` with per-tenant error handling
- [x] **4.2 TenantContextMiddleware** — validates tenant context for tenant-scoped endpoints; skips master-only paths (/api/auth, /api/user, /api/company, /swagger, /health); SysAdmin without impersonation passes through; validates CompanyId claim exists → CompanySystemSettings exists → IsProvisioned → IsActive; returns 403 with descriptive JSON messages for each failure
- [x] **4.3 CompanyController provisioning endpoints** — 3 new DTOs (CompanySystemSettingsDto, CreateCompanySystemSettingsDto, UpdateCompanySystemSettingsDto); Settings CRUD: `GET /api/company/{id}/settings`, `GET /api/company/settings` (all), `POST /api/company/settings`, `PUT /api/company/{id}/settings`; Tenant lifecycle: `POST /api/company/{id}/provision`, `PUT /api/company/{id}/activate`, `PUT /api/company/{id}/deactivate`, `POST /api/company/{id}/migrate`; connection string NOT exposed in DTO (security — only `HasCustomConnectionString` flag)
- [x] **4.4 Middleware pipeline** — wired `UseTenantContext()` after `UseImpersonation()` in Program.cs; registered `ITenantProvisioningService` in DI
- [x] **4.5 Startup migration** — updated Program.cs startup block: `MasterDbContext.MigrateAsync()` → `ITenantProvisioningService.MigrateAllTenantsAsync()`; async startup with `await app.RunAsync()`
- [x] **4.6 Unit tests** — 15 TenantContextMiddlewareTests (path skipping x8 InlineData, non-API paths x3, unauthenticated passthrough, SysAdmin without CompanyId passthrough, missing CompanyId 403, invalid CompanyId 403, valid tenant passthrough, missing settings 403, unprovisioned 403, inactive 403, SysAdmin impersonation valid, SysAdmin impersonation inactive 403); 13 TenantProvisioningServiceTests (activate valid/not found/not provisioned, deactivate valid/not found/already inactive, provision no settings/already provisioned, migrate all no tenants/inactive only/unprovisioned only, migrate single not found/not provisioned)
- [x] Build: 0 errors, Tests: 219 pass (218 unit + 1 integration)

### Multi-Tenant Roadmap — Phase 5: Data Migration Tool (2026-02-10)
- [x] **5.1 MigrationTool console project** — new `Fakvio.MigrationTool` .NET 10 console project added to solution; references Infrastructure, Domain, Application projects; NuGet: Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0, Microsoft.Extensions.Configuration.Json 10.0.0, Microsoft.Extensions.Hosting 10.0.0
- [x] **5.2 DataMigrationService** (~700 lines) — full migration from single-DB to multi-tenant architecture: Step 1 (Apply master DB migrations), Step 2 (Copy Users to master, skip by Email match), Step 3 (Copy code tables to master: Currency by Code, VatRate by Name+Rate, NumberSequenceFormat by FormatPattern, ContentTemplate by Name+TemplateType), Steps 4-11 per company: ensure issuer in master → create CompanySystemSettings → CREATE DATABASE → apply migrations → copy code tables → copy issuer → copy customers (via invoice.IssuerId FK) → copy number sequences → copy invoices+items (FK remapping: IssuerId, ClientId by CompanyName, CurrencyId by Code, VatRateId by Name+Rate, OriginalInvoiceId for credit note self-references) → copy templates → copy AresCache → mark provisioned; dry-run mode, skip-provisioned flag, idempotent
- [x] **5.3 DataIntegrityVerifier** — automated verification: row count comparison (users, issuers, settings, currencies, VAT rates), per-tenant checks (exactly 1 issuer, has code tables, no orphaned invoice.ClientId/IssuerId FKs), cross-tenant total invoice count matches source; auto-runs after successful migration
- [x] **5.4 Program.cs entry point** — `--dry-run` and `--verify` command-line flags; appsettings.json with SourceConnection + MasterConnection; confirmation prompt before modifying databases; password masking in console output
- [x] Build: 0 errors, Tests: 219 pass (218 unit + 1 integration)

### Multi-Tenant Roadmap — Phase 6: Blazor UI Updates (2026-02-10)
- [x] **6.1-6.2 Already implemented** — ApiClientBase already sends X-Company-Id header via `GetImpersonatedCompanyIdAsync()`; MainLayout already has company switcher MudSelect for SysAdmin; CustomAuthenticationStateProvider stores ImpersonatedCompanyId in ProtectedSessionStorage; NavMenu hides invoicing for SysAdmin without impersonation
- [x] **6.3 CompanySystemSettings management page** — new `CompanySettings.razor` at `/company-settings` (SysAdmin only); `AppDataGrid` with tenant status columns (CompanyName, DatabaseName, Provisioned chip, Active/Inactive chip, MaxUsers, CustomConn flag); Create dialog (company selector + DB name + max users + notes); Edit dialog (max users + notes + connection string override); new `CompanySettingsApiService.cs` inheriting ApiClientBase (8 methods: GetAll, Get, Create, Update, Provision, Activate, Deactivate, Migrate)
- [x] **6.4 Tenant provisioning UI** — Provision button with confirmation dialog showing company name + DB name, loading spinner (snackbar), success/error feedback; Activate button (instant, no confirmation); Deactivate button with confirmation dialog warning about user access loss; Migrate DB button for manual EF Core migration
- [x] **6.5 Tenant status indicators** — Companies.razor grid: added "Provisioned" column with conditional chips (Provisioned=green, Not Provisioned=warning, Inactive=grey, No Settings=outlined default); loads settings in parallel via `CompanySettingsApiService`; CompanyDetail.razor header: tenant status chips next to Active/Inactive chip (shows DB name when provisioned)
- [x] **6.6 Localization** — 35 new CZ/EN resource keys (`Nav_TenantSettings`, `TenantSettings_*` x34); added to both SharedResource.resx and SharedResource.en.resx
- [x] **ApiClientBase extensions** — added `PostWithoutBodyBoolAsync` and `PutWithoutBodyBoolAsync` protected methods for action endpoints without request bodies (provision, activate, deactivate, migrate)
- [x] **NavMenu** — added "Tenant Management" link (`/company-settings`) under SysAdmin Settings section with DNS icon
- [x] **DI registration** — registered `CompanySettingsApiService` in BlazorUI Program.cs
- [x] Build: 0 errors, Tests: 219 pass (218 unit + 1 integration)

### PostgreSQL → Azure SQL (SQL Server) Migration (2026-02-11)
- [x] **NuGet packages** — replaced `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0` → `Microsoft.EntityFrameworkCore.SqlServer 10.0.0` in Infrastructure + MigrationTool csproj
- [x] **Domain entity** — changed `uint xmin` → `byte[]? RowVersion` on NumberSequence entity (SQL Server uses rowversion/timestamp for optimistic concurrency)
- [x] **DbContext configurations** — MasterDbContext: removed `HasColumnType("TEXT")`, fixed `HasFilter` from PostgreSQL double-quotes to SQL Server brackets; TenantDbContext: replaced xmin config (`HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken()`) → `.IsRowVersion()`; removed `HasColumnType("TEXT")`
- [x] **EF Core provider** — `UseNpgsql` → `UseSqlServer` in 6 locations: Program.cs (2), MasterDesignTimeFactory, TenantDesignTimeFactory, TenantDbContextFactory, TenantProvisioningService
- [x] **Connection strings** — updated all 5 config files from PostgreSQL format (`Host=;Port=;Database=;Username=;Password=`) to SQL Server format (`Server=;Database=;User Id=;Password=;TrustServerCertificate=true`)
- [x] **Connection string builders** — replaced `NpgsqlConnectionStringBuilder` + `NpgsqlConnection` with `SqlConnectionStringBuilder` + `SqlConnection` in TenantDbContextFactory, TenantProvisioningService, DataMigrationService, DataIntegrityVerifier
- [x] **Database creation SQL** — replaced `SELECT 1 FROM pg_database WHERE datname = 'x'` + `CREATE DATABASE "x"` (connecting to "postgres" system DB) with `SELECT DB_ID('x')` + `CREATE DATABASE [x]` (connecting to "master" system DB)
- [x] **Migrations** — deleted 6 old PostgreSQL migration files (3 Master + 3 Tenant); generated fresh SQL Server migrations (SqlServerValueGenerationStrategy.IdentityColumn, rowversion type)
- [x] **Tests** — updated AuditTrailTests (xmin → RowVersion assertion), TenantDbContextFactoryTests (PostgreSQL conn strings → SQL Server), TenantProvisioningServiceTests (PostgreSQL conn strings → SQL Server); updated comments in TenantDbContextFactoryTests, NumberSequenceService
- [x] Build: 0 errors, Tests: 219 pass (218 unit + 1 integration)

### TenantDbContext SysAdmin Fix + Dynamic Connection Resolution (2026-02-11)
- [x] **Fix 1: Middleware SysAdmin bypass removed** — SysAdmin without impersonation (no X-Company-Id header) now gets 403 Forbidden on tenant endpoints instead of being allowed through; descriptive error message tells SysAdmin to use impersonation; master-only paths (/api/company, /api/user, /api/auth) still accessible without impersonation
- [x] **Fix 2: Dynamic TenantDbContext connection resolution** — changed `AddDbContext<TenantDbContext>` DI registration from static template connection to per-request dynamic resolution: reads CompanyId from JWT claim → looks up CompanySystemSettings in master DB → builds tenant connection string (custom override or master template with swapped InitialCatalog); falls back to template connection for startup/migrations/EF CLI tools
- [x] **Tests updated** — renamed `SysAdmin_WithoutCompanyId_PassesThrough` → `SysAdmin_WithoutCompanyId_Returns403` (now expects 403); added `SysAdmin_WithoutCompanyId_MasterOnlyPath_PassesThrough` (verifies master paths still work)
- [x] Build: 0 errors, Tests: 219 pass (218 unit + 1 integration)

### ARES Service Resilient Caching + CompanyController MasterDbContext Migration (2026-02-11)
- [x] **ARES fix** — wrapped cache read/write in try/catch in `AresServiceImpl.cs`; cache failures are now non-fatal (skip cache, fetch directly from ARES API); fixes ARES failing when TenantDbContext has no real database connection
- [x] **EnableRetryOnFailure** — added to all `UseSqlServer` calls (Program.cs MasterDbContext + TenantDbContext, TenantDbContextFactory, TenantProvisioningService); 3 retries, 5-second max delay for transient SQL Server/Azure SQL errors
- [x] **CompanyController rewrite** — removed `IClientService` dependency (TenantDbContext-dependent); replaced with direct `MasterDbContext` queries + `IAresService` injection; all company CRUD (GetAll, GetPaged, GetById, Create, Update, Delete) now query master database directly; ARES endpoint uses `IAresService.GetCompanyInfoAsync()` directly; fixes "transient failure" error when SysAdmin creates companies without a tenant database
- [x] **DTO property fixes** — `filter.IsActive` → `filter.IncludeInactive` (inverted logic), `filter.SortDescending` → `filter.IsDescending`, `PagedResult.Page` → `PagedResult.PageNumber`, removed non-existent `UpdateClientDto.RegistrationNumber`
- [x] Build: 0 errors, Tests: 220 pass (219 unit + 1 integration)

### Companies CRUD Fixes + SysAdmin UX Improvements (2026-02-11)
- [x] **Step 1: Address/Contact in MasterDbContext** — removed `Ignore<Address>()` and `Ignore<Contact>()`; added `ConfigureAddress()` and `ConfigureContact()` methods (matching TenantDbContext schema); added `HasMany(Address/Contact).WithOne(Client).Cascade` nav property config in ConfigureClient; generated `AddAddressContactToMaster` migration (creates Address + Contact tables in master DB with proper FK cascade, indexes)
- [x] **Step 2: CompanyController CRUD handles Address/Contact** — `GetCompanyById()`: added `.Include(c => c.Address).Include(c => c.Contact)`; `GetAllCompanies()` + `GetCompaniesPaged()`: added `.Include(c => c.Address)` for grid display; `CreateCompany()`: iterates `createDto.Address` and `createDto.Contact` to add Address/Contact entities to new company; `UpdateCompany()`: loads with Include, replace-all strategy (remove existing → add new from DTO); `MapToDto()`: maps real Address/Contact collections instead of empty lists
- [x] **Step 3: CompanyDetail edit form Address/Contact** — `SaveCompany()` edit path now builds `UpdateClientDto.Address` (from `_address` form fields) and `UpdateClientDto.Contact` (from `_emailContact`/`_phoneContact` fields via `BuildUpdateContacts()` helper); previously only basic fields were sent on update, silently discarding address/contact changes
- [x] **Step 4: Fix LoadCompany "not found" error** — split `Task.WhenAll(company, settings)` into independent loads; settings loaded in separate try/catch — missing settings (expected for new companies) returns null without failing the whole page; previously, 404 from settings endpoint would propagate and show error on the company detail page
- [x] **Step 5: Auto-provision tenant on company creation** — `SaveCompany()` create path: after company creation, auto-creates CompanySystemSettings (generates DB name) + auto-provisions tenant DB (create DB, migrate, seed code tables, create issuer, create sequences); non-blocking — provisioning failure shows warning snackbar but doesn't prevent company creation or user invitation; added `Company_AutoProvisionSuccess`/`Company_AutoProvisionFailed` resource keys (CZ + EN)
- [x] **Step 6: SysAdmin nav — Content Templates + Number Sequences** — added ContentTemplates and NumberSequences links to SysAdmin Settings section with `@if (!_showInvoicing)` guard (same pattern as VatRates); when SysAdmin impersonates, these appear in Invoicing section; when not impersonating, in Settings section
- [x] Build: 0 errors, Tests: 219 pass (218 unit + 1 integration)

### Bug Fixes, Loading UX, System Settings, Logging & SysAdmin Dashboard (2026-02-11)
- [x] **Phase 1.1: Idempotent provisioning** — TenantProvisioningService.CopyCodeTablesAsync now DELETEs existing records (FK-safe order: NumberSequence→NumberSequenceFormat→ContentTemplate→Currency→VatRate) + DBCC CHECKIDENT reseed before inserting; CreateDefaultNumberSequencesAsync checks for existing defaults; CreateIssuerInTenantAsync checks by RegistrationNumber; removed throw for already-provisioned companies (now logs warning + re-runs)
- [x] **Phase 1.2: CompanyDetail redirect fix** — added OnParametersSetAsync with _previousId tracking to detect route parameter changes; removed duplicated logic from OnInitializedAsync
- [x] **Phase 1.3: Loading indicators** — added _saving flag + MudOverlay with MudProgressCircular for full-page loading feedback; disabled Save/Delete buttons during operations; try/finally pattern in SaveCompany and DeleteCompany
- [x] **Phase 2: System Configuration** — SystemConfiguration entity (SMTP+JWT settings, single-row pattern); ISystemConfigurationService/SystemConfigurationService with auto-create defaults; SystemConfigurationController (GET/PUT, SysAdmin only); SystemConfigurationApiService + SystemSettings.razor page (edit/save/cancel, MudOverlay); EmailService reads SMTP from DB first, falls back to appsettings.json; added to TenantContextMiddleware MasterOnlyPaths
- [x] **Phase 3: Logging Infrastructure** — AppLog entity (lightweight, NOT BaseEntity); DatabaseLoggerProvider with ConcurrentQueue (non-blocking); DatabaseLogger with noisy category filtering (EF/ASP.NET → Warning+); LogFlushService (BackgroundService, 5s interval, raw ADO.NET INSERT); LogCleanupService (BackgroundService, 1h interval, 48h retention for Debug/Info); AppLogController (paged + summary endpoints, SysAdmin only); AppLogApiService + Logs.razor page (server-side paging, level/date/search filters, row click detail expansion)
- [x] **Phase 4: SysAdmin Dashboard** — SysAdminDashboardDto (company stats + recent logs + log level summary); GET /api/dashboard/sysadmin endpoint; Home.razor detects SysAdmin without impersonation → shows 4 KPI cards + recent warning/error logs + "View All Logs" link; regular users see existing tenant dashboard
- [x] **Phase 5: NavMenu + Resources** — added System Settings and Logs nav links under SysAdmin section; ~37 new CZ/EN resource keys (SystemSettings_*, Log_*, Dashboard_SysAdmin*, Nav_SystemSettings, Nav_Logs)
- [x] **Phase 6: Migration + Tests** — EF migration `AddSystemConfigAndAppLogToMaster` (SystemConfiguration table + AppLog table with 3 indexes); SystemConfigurationServiceTests (5 tests: default creation, existing row, update, fresh DB update, idempotent get); updated TenantProvisioningServiceTests (idempotent re-provisioning); updated EmailServiceTests (ISystemConfigurationService mock)
- [x] Build: 0 errors, Tests: 225 pass (224 unit + 1 integration)

### QR Faktura + QR Platba, Bug Fixes & EF Query Optimization (2026-02-11)
- [x] **CRITICAL FIX: Tenant provisioning 500** — Root cause from AppLog: `FK_BillingSettings_NumberSequence_CustomInvoiceNumberSequenceId may cause cycles or multiple cascade paths`; SQL Server rejects two SetNull FK paths from BillingSettings→NumberSequence when Client→BillingSettings is Cascade; Fix: changed both FKs from `OnDelete(DeleteBehavior.SetNull)` to `OnDelete(DeleteBehavior.NoAction)` in TenantDbContext; regenerated InitTenant migration; dropped broken tenant_1 DB
- [x] **EF query warnings fix** — Added `AsSplitQuery()` to 9 queries across 4 services (ClientService x5, PdfExportService x1, TenantProvisioningService x1, CompanyController x2); Added `OrderBy(x => x.Id)` before `FirstOrDefaultAsync()` in 10 queries across 6 services (ClientService, ContentTemplateService, VatRateService, SystemConfigurationService, TenantProvisioningService, NumberSequenceService)
- [x] **Seed data: 3 new currencies** — CHF (Swiss Franc), HUF (Hungarian Forint, DecimalPlaces=0), RON (Romanian Leu); added to both MasterDbContext.SeedData() and TenantDbContext.SeedData(); EF migrations: regenerated InitTenant + new AddExtraCurrencies master migration
- [x] **QR Faktura (SIND format)** — Full Czech standard implementation: `SID*1.0*{KEY}:{VALUE}*...` with CRC32 checksum; SindBuilder fluent API with all attributes (ID, DD, AM, VS, ACC, CC, DT, VII, INI, VIR, INR, DUZP, TB0/T0, TB1/T1, TB2/T2, NTB, TD, X-SW); alphabetical key ordering; Crc32Calculator with pre-computed lookup table (polynomial 0xEDB88320)
- [x] **QR Platba (SPD format)** — SpdIntegrator combines SIND+SPD into one QR code: shared keys (ACC, AM, CC, DT) placed directly in SPD, VS→X-VS, remaining SIND URL-encoded as X-INV value; falls back to SIND-only when no IBAN available
- [x] **QR code generation** — QRCoder 1.7.0 NuGet; PngByteQRCode for server-side PNG generation; configurable module size (5-20 pixels)
- [x] **QrPaymentService** — loads invoice with all includes (Client, Issuer, Currency, InvoiceItem.VatRate); maps all invoice fields to SIND; VAT breakdown groups items by rate (21%→TB0/T0, 12%→TB1/T1, 0%→NTB); credit notes use TD:1
- [x] **API endpoints** — `GET {id}/qr` (PNG image), `GET {id}/qr/sind` (raw SIND string), `GET {id}/qr/spd` (SPD string with fallback)
- [x] **Blazor UI** — QR code card on InvoiceDetail (completed/paid invoices); async non-blocking load; "QR Platba+F" or "QR Faktura" heading based on IBAN availability; 3 resource keys (CZ+EN)
- [x] **20 QR payment unit tests** — CRC32 (known vector, empty, hex format); SindBuilder (required fields, alphabetical order, IBAN+SWIFT, nulls ignored, VAT breakdown, CRC consistency); SpdIntegrator (shared keys, URL encoding, remaining keys); QrPaymentService (SIND generation, not found, SPD with/without IBAN, PNG signature, VAT grouping, zero VAT→NTB, credit note→TD:1)
- [x] Build: 0 errors, Tests: 247 pass (246 unit + 1 integration)

### Dual-Context Code Tables, Dashboard Concurrency & Impersonation Fixes (2026-02-11)
- [x] **CRITICAL FIX: DashboardService DbContext concurrency** — ROOT CAUSE: `Task.WhenAll()` running 5 parallel queries on same scoped TenantDbContext instance (DbContext is NOT thread-safe); FIX: replaced with sequential `await` calls; eliminated "A second operation was started on this context instance" error during impersonation
- [x] **Dual-context pattern for code table services** — CurrencyService, VatRateService, ContentTemplateService, NumberSequenceService now inject BOTH MasterDbContext AND TenantDbContext + ITenantResolver; auto-select context based on `ITenantResolver.GetCurrentCompanyId()` (null → MasterDbContext, value → TenantDbContext); properties: `IsMasterContext`, `ActiveContext`, entity-specific `DbSet` accessors
- [x] **TenantContextMiddleware SysAdmin code table bypass** — added `SysAdminCodeTablePaths` array (/api/currency, /api/vatrate, /api/contenttemplate, /api/numbersequence/formats); SysAdmin without impersonation can now access code table endpoints (services auto-use MasterDbContext); other tenant endpoints still blocked with 403
- [x] **NumberSequenceService dual-context scoping** — dual-context ONLY for format operations (FormatSet, FormatContext); sequence operations always use TenantDbContext (sequences are per-tenant with counters)
- [x] **VatRateService FK guard** — DeleteVatRateAsync InvoiceItem FK check wrapped in `if (!IsMasterContext)` (InvoiceItem table only exists in tenant DB)
- [x] **Impersonation dropdown refresh** — CompanyDetail.razor: changed `Navigation.NavigateTo($"/companies/{result.Id}")` to `forceLoad: true` after company creation; forces MainLayout re-render and company list reload
- [x] **Invitation email template fix** — ContentTemplateService dual-context automatically resolves invitation email template queries to MasterDbContext when no tenant context (previously failed on non-existent template DB)
- [x] **4 test files updated** — CurrencyServiceTests, VatRateServiceTests, EmailTemplateServiceTests (ContentTemplateService), NumberSequenceServiceTests: added MasterDbContext InMemoryDatabase + ITenantResolver mock (returns companyId=1L for tenant context in tests)
- [x] Build: 0 errors, Tests: 247 pass (246 unit + 1 integration)

### UX Fixes — ARES Message, NavMenu, Client Redirect, VS Auto-fill, ARES Cache (2026-02-11)
- [x] **Fix 1: ARES fetch success message** — CompanyDetail.razor + ClientDetail.razor: changed `L["Msg_SaveSuccess"]` to `L["Ares_DataLoaded"]` after ARES fetch; `L["Ares_NotFound"]` for null result; added 2 resource keys (CZ + EN)
- [x] **Fix 2: Hide SysAdmin nav items during impersonation** — NavMenu.razor: Companies, TenantSettings, SystemSettings, Logs, master code tables now wrapped in `@if (!_showInvoicing)` — hidden when SysAdmin impersonates a company; Currencies always visible (dual-context); company users no longer see admin-only pages
- [x] **Fix 3: Client creation "Not Found" error** — ClientDetail.razor: added `OnParametersSetAsync()` with `_previousId` tracking (same pattern as CompanyDetail); Blazor reuses component on `/clients/create` → `/clients/1` navigation, `OnInitializedAsync` only fires once
- [x] **Fix 4: VariableSymbol auto-fill regression** — ROOT CAUSE: `LoadDocumentNumberPreview()` pre-fills `CustomDocumentNumber` → backend receives `CustomDocumentNumber` → `DocumentNumber != "DRAFT"` → VS auto-fill code inside the if-block is SKIPPED; FIX: moved VS auto-fill OUTSIDE the `if (DocumentNumber == "DRAFT")` block, runs unconditionally after document number is set
- [x] **Fix 5: ARES cache dual-context** — ROOT CAUSE: AresCacheRepository used TenantDbContext → falls back to non-existent `fakvio_tenant_template` → floods logs with connection errors; FIX: made AresCacheRepository dual-context (MasterDbContext + TenantDbContext + ITenantResolver); added AresCache DbSet + ConfigureAresCache to MasterDbContext; removed `Ignore<AresCache>()`; migration `AddAresCacheToMaster`
- [x] Build: 0 errors, Tests: 247 pass (246 unit + 1 integration)

### Invoice Save Navigation Fix + Unique Document Number Index (2026-02-11)
- [x] **Fix 1: Invoice "Not Found" after save** — InvoiceDetail.razor: replaced `OnInitializedAsync` with `OnParametersSetAsync` + `_previousId` tracking (same pattern as ClientDetail + CompanyDetail); Blazor reuses the component when navigating from `/invoices/create` (Id=0) to `/invoices/{newId}` (Id>0), `OnInitializedAsync` only fires once; also resets `_isEditing=false` and `_isDirty=false` on parameter change to prevent NavigationLock from firing
- [x] **Fix 2: Unique document number index** — TenantDbContext ConfigureInvoice: changed `HasIndex(e => e.DocumentNumber)` to `.IsUnique().HasFilter("[DocumentNumber] IS NOT NULL AND [DocumentNumber] <> 'DRAFT'")` — prevents duplicate invoice numbers at the database level; multiple "DRAFT" placeholders still allowed; tenant migration `AddUniqueDocumentNumberIndex`
- [x] Build: 0 errors, Tests: 247 pass (246 unit + 1 integration)

### NavigationLock + Duplicate Document Number Fixes (2026-02-11)
- [x] **Fix 1: NavigationLock "leave page?" prompt after save** — ROOT CAUSE: `_isDirty = false` set before `NavigateTo` but Blazor hasn't re-rendered the `<UnsavedChangesLock>` child component yet — `<NavigationLock>` is still in the DOM when navigation fires; FIX: added `StateHasChanged()` + `await Task.Yield()` after `_isDirty = false` to force Blazor to complete the render cycle and remove `<NavigationLock>` from the DOM before `NavigateTo`; applied consistently to all 5 detail pages: InvoiceDetail, ClientDetail, CompanyDetail, InvoiceTemplateDetail, ContentTemplateDetail
- [x] **Fix 2: Duplicate document numbers** — ROOT CAUSE: `LoadDocumentNumberPreview()` pre-filled `CustomDocumentNumber` with the preview number → backend received it as a custom number → skipped `GenerateNextNumberAsync()` → sequence counter never incremented → next invoice got the same preview number; LOG: `DbUpdateException: Cannot insert duplicate key (INV-2026001)` confirmed by unique index added earlier; FIX: changed preview to display as `Placeholder` text on MudTextField instead of pre-filling the field value; `CustomDocumentNumber` stays null → backend always calls `GenerateNextNumberAsync()` which properly increments the counter; VariableSymbol still pre-filled from preview digits
- [x] Build: 0 errors, Tests: 247 pass (246 unit + 1 integration)

### User-Friendly API Error Messages + Client Validation (2026-02-11)
- [x] **Fix 1: Raw API error shown to user** — ROOT CAUSE: `ApiClientBase` threw `HttpRequestException($"API request failed: {StatusCode} - {errorContent}")` with raw JSON body (`{"message":"Client with ID 0 not found"}`), and UI catch blocks displayed `ex.Message` directly; FIX: added `ExtractErrorMessage()` helper that parses JSON and extracts the `"message"` field (or `"title"` for ASP.NET ProblemDetails), falls back to raw content if not JSON; user now sees `"Client with ID 0 not found"` instead of `"API request failed: BadRequest - {"message":"Client with ID 0 not found"}"`
- [x] **Fix 2: Missing client validation** — InvoiceDetail.razor `SaveNewInvoice()` validated IssuerId and ItemCount but NOT ClientId — requests with `ClientId=0` reached the API and returned BadRequest; FIX: added `_createDto.ClientId <= 0` guard with localized message `Invoice_NoClientSelected` ("Vyberte prosím klienta" / "Please select a client")
- [x] Added 1 resource key (CZ + EN): `Invoice_NoClientSelected`
- [x] Build: 0 errors, Tests: 247 pass (246 unit + 1 integration)

### QR Platba in Default PDF Templates (2026-02-11)
- [x] **QR code in PDF** — PdfExportService now generates QR code (via IQrPaymentService) and embeds it as base64 inline PNG in the PDF HTML; new `{{QrCodeImage}}` placeholder replaced with `<img src="data:image/png;base64,...">` (150x150px); non-critical — if QR generation fails, PDF is generated without QR (logged as warning)
- [x] **Template layout** — payment info section now uses `display: flex; justify-content: space-between` layout: bank details on left, QR code on right with "QR Platba" caption; updated in 3 places: DefaultSeedData.GetDefaultInvoicePdfTemplate() (seed), PdfExportService.GetDefaultHtmlTemplate() (fallback), and documented in placeholder reference
- [x] **PdfExportService constructor** — added IQrPaymentService dependency (already registered in DI); QR code generated with `pixelsPerModule: 8` for PDF-quality resolution
- [x] **PdfExportServiceTests** — added Mock<IQrPaymentService> returning test PNG bytes; all 4 existing tests pass with new constructor
- [x] Build: 0 errors, Tests: 247 pass (246 unit + 1 integration)

### Simple QR Platba (SPD) — No X-INV (2026-02-11)
- [x] **New `BuildSimpleSpdString` method** — added to SpdIntegrator.cs; generates pure, banking-app-compatible SPD strings with payment data only (ACC, AM, CC, DT, MSG, X-VS); sanitizes IBAN (strips spaces/dashes); truncates MSG to 60 chars per SPD spec; no X-INV invoice data embedded
- [x] **QrPaymentService updated** — `GenerateSpdWithInvoiceAsync` and `GenerateQrCodeImageAsync` now call `BuildSimpleSpdString` instead of `BuildSpdWithInvoice`; no longer builds SIND for payment QR codes (SIND still available via `GenerateSindStringAsync` for standalone QR Faktura)
- [x] **Legacy `BuildSpdWithInvoice` kept** — method retained in SpdIntegrator for potential future use; XML doc comment updated noting it's no longer called by QrPaymentService
- [x] **Tests updated** — replaced 3 old combined SPD+SIND tests with 5 new simple SPD tests (valid input, null optional fields, IBAN sanitization, MSG truncation, alphabetical order); added 1 legacy test for backward compatibility of `BuildSpdWithInvoice`; updated service integration test to assert no X-INV in output; renamed no-trailing-asterisk test to use `BuildSimpleSpdString`
- [x] Build: 0 errors, Tests: 253 pass (252 unit + 1 integration)

### Company Bank Account Save Fix (2026-02-11)
- [x] **Root cause**: BillingSettings entity was `Ignore<BillingSettings>()` in MasterDbContext — no BillingSettings table existed in master DB; CompanyController (uses MasterDbContext) couldn't read/write bank account data; CompanyDetail.razor update path never included BillingSettings in the DTO
- [x] **MasterDbContext** — removed `Ignore<BillingSettings>()`; added `DbSet<BillingSettings>`; added `ConfigureBillingSettings()` with NumberSequence nav props ignored (tenant-only); added Client→BillingSettings one-to-one relationship with Cascade delete
- [x] **CompanyController** — added `.Include(c => c.BillingSettings)` to GetCompanyById and Update queries; added BillingSettings create-or-update logic in Create/Update methods; updated MapToDto to include all BillingSettings fields
- [x] **CompanyDetail.razor** — included `BillingSettings = new UpdateBillingSettingsDto { BankAccountNumber = _bankAccount }` in the update DTO
- [x] **EF migration** — `AddBillingSettingsToMaster` (creates BillingSettings table in master DB with FK to Client) — NOTE: migration later combined into `AddBillingSettingsAndSmtpToMaster`
- [x] Build: 0 errors, Tests: 253 pass (252 unit + 1 integration)

### Company Profile, 1:N Addresses, Bank Account Auto-fill (2026-02-11)
- [x] **AddressListEditor shared component** — new `AddressListEditor.razor` with MudSimpleTable (type, street, city, postal, country, default star); add/edit via `AddressDialog.razor` (MudDialog with EnumSelect for EAddressType); delete with confirmation; set-primary toggles IsPrimary across all addresses; read-only mode for view pages
- [x] **AddressDialog shared component** — new `AddressDialog.razor` for add/edit operations; fields: AddressType (EnumSelect), Street, AddressLine2, City, PostalCode, Country, IsPrimary switch; basic required field validation
- [x] **MyCompany.razor page** — new page at `/my-company` for non-SysAdmin users to view/edit their own company profile; loads issuer via `ClientApiService.GetIssuerAsync()`; 3-column cards (Company Info with ARES, Contacts, Billing Settings) + full-width AddressListEditor; save via `ClientApiService.UpdateAsync()` with all addresses/contacts/billing settings
- [x] **ClientDetail.razor 1:N addresses** — replaced single `CreateAddressDto _address` with `List<CreateAddressDto> _addresses`; removed old single-address card; added full-width `<AddressListEditor>` section between billing settings and invoice history; `InitCreateForm()` initializes with one default billing address; `EnableEditing()` maps ALL addresses from client; `BuildAddressAndContacts()` sends full list; `SaveClient()` update path sends all addresses as `UpdateAddressDto` list; `FetchFromAres()` merges ARES address into existing primary or adds new
- [x] **Invoice bank account from issuer** — `InitCreateDto()`: auto-fills `BankAccountNumber` from `_issuer?.BillingSettings?.BankAccountNumber`; `OnClientChanged()`: removed client bank account auto-fill (bank account is issuer's, not client's); `InitCreateDtoFromTemplate()`: template bank account takes priority, falls back to issuer's
- [x] **NavMenu update** — added "My Company" link (`/my-company`) in Settings group, visible when `_showInvoicing` is true
- [x] **15 resource keys (CZ + EN)** — Nav_MyCompany, MyCompany_Title, Address_List, Address_Add, Address_Edit, Address_ConfirmDelete, Address_DefaultBilling, Address_NoAddresses, Address_Type, Client_AddressLine2, EAddressType_Primary/Billing/Shipping/Correspondence
- [x] Build: 0 errors, Tests: 254 pass (253 unit + 1 integration)

### Paylibo QR Platba for Czech Bank Accounts (2026-02-11)
- [x] **Problem**: Invoices with Czech domestic bank account format (e.g., `1342333010/3030`) but no IBAN generated invalid QR codes — fell through to SIND-only (invoice data, no payment instructions)
- [x] **Solution**: Implemented paylibo.com REST API client (same approach as Monarc.Core project)
- [x] **PayliboQrOptions** — new `Application/QrPayment/PayliboQrOptions.cs` with reflection-based `ToString()` for query string building; supports Czech bank account prefix/number/bankCode, amount, currency, VS, due date, message
- [x] **IPayliboClient + PayliboClient** — REST client calling `https://api.paylibo.com/paylibo/generator/czech/image?{queryString}`; returns PNG bytes or empty array on failure (non-critical — invoice still works without QR)
- [x] **QrPaymentService rewrite** — 3-tier strategy: (1) IBAN available → local SPD/QRCoder (fastest), (2) Czech bank account → paylibo API, (3) Neither → SIND-only fallback; `ParseCzechBankAccount()` parses `prefix-account/bankCode` format
- [x] **DI registration** — `builder.Services.AddHttpClient<IPayliboClient, PayliboClient>()` in Program.cs (HttpClientFactory for proper lifecycle)
- [x] **6 new tests** — paylibo API called with correct params, paylibo failure falls back to SIND, IBAN skips paylibo, GenerateSpdWithInvoiceAsync Czech fallback, PayliboQrOptions ToString format, PayliboQrOptions omits null values
- [x] Build: 0 errors, Tests: 260 pass (259 unit + 1 integration)

### Phase A: Company-Level SMTP Settings (2026-02-11)
- [x] **Step 1: CompanySystemSettings entity** — added 7 nullable SMTP properties (SmtpHost, SmtpPort, SmtpUsername, SmtpPassword, SmtpSenderEmail, SmtpSenderName, SmtpUseSsl); all nullable — null means "use system SMTP fallback"
- [x] **Step 2: MasterDbContext EF config** — added SMTP column constraints (HasMaxLength for Host=500, Username=256, Password=500, SenderEmail=256, SenderName=200)
- [x] **Step 3: DTOs updated** — CompanySystemSettingsDto (read, HasSmtpPassword bool flag — password never exposed), UpdateCompanySystemSettingsDto (write, with validation attributes), CreateCompanySystemSettingsDto (optional SMTP at creation)
- [x] **Step 4: EmailService 3-tier SMTP resolution** — refactored from 2-tier (System→appsettings) to 3-tier (Company→System→appsettings); added MasterDbContext + ITenantResolver injection; new SmtpSettings record + ResolveSmtpSettingsAsync() method
- [x] **Step 5: CompanyController** — updated MapSettingsToDto with SMTP fields; added test-smtp POST endpoint (Admin,SysAdmin); SMTP password partial update pattern (null=keep, empty=clear, value=update)
- [x] **Step 6: MyCompany.razor** — added Email Settings section (Admin-only AuthorizeView); SMTP form fields, Load/Save/Test methods
- [x] **Step 7: CompanyDetail.razor** — added SMTP section for SysAdmin; same form fields, Save/Test methods
- [x] **Step 8: CompanySettingsApiService** — added GetByCompanyIdAsync + TestSmtpAsync methods
- [x] **Step 9: Resource keys** — 14 keys (CZ + EN): Smtp_Title, Smtp_Host, Smtp_Port, Smtp_Username, Smtp_Password, Smtp_SenderEmail, Smtp_SenderName, Smtp_UseSsl, Smtp_TestButton, Smtp_TestSuccess, Smtp_TestFailed, Smtp_InfoText, Smtp_PasswordPlaceholder
- [x] **Step 10: Unit tests** — rewrote EmailServiceTests with MasterDbContext dependency; 4 new SMTP resolution tests (CompanySmtp used, fallback to SystemSmtp, no CompanyId skips Tier1, all tiers empty falls to appsettings)
- [x] **EF Migration** — `AddBillingSettingsAndSmtpToMaster` (combined: 7 SMTP columns on CompanySystemSettings + BillingSettings table creation — replaces corrupted separate migrations)
- [x] Build: 0 errors (VS file lock warnings on API/BlazorUI only), Tests: 263 pass (262 unit + 1 integration)

### Phase B: Self-Registration + External OAuth Providers (2026-02-12)
- [x] **B1: Domain + Entity Extensions** — new `EExternalProvider` enum (None, Google, Microsoft, Facebook, Apple, Seznam); extended User entity with ExternalProvider, ExternalProviderId, IsEmailVerified, EmailVerificationToken, EmailVerificationTokenExpiresAt, IsExternalLogin (computed); made PasswordHash nullable (`string?`) for OAuth users
- [x] **B2: MasterDbContext EF config** — PasswordHash now optional; ExternalProvider default value; composite unique filtered index on (ExternalProvider, ExternalProviderId) with `[ExternalProvider] <> 0`; filtered index on EmailVerificationToken; IsEmailVerified index; seed data updated (SysAdmin: IsEmailVerified=true, ExternalProvider=None)
- [x] **B3: DTOs** — new RegisterRequest, RegisterResponse, VerifyEmailRequest, ExternalLoginCallbackDto; updated UserDto (ExternalProvider, IsExternalLogin, IsEmailVerified); updated LoginResponse (both API + Blazor side) with IsExternalLogin
- [x] **B4: Backend implementation** — IAuthService: added RegisterAsync, VerifyEmailAsync, ExternalLoginAsync; AuthService: 6 deps (MasterDbContext, IConfiguration, ITenantProvisioningService, IEmailService, IAresService, ILogger); RegisterAsync flow (email check → ARES lookup → Client + CompanySystemSettings + User creation → verification email); VerifyEmailAsync (token validation → mark verified → trigger provisioning); ExternalLoginAsync (3 scenarios: known OAuth user, link by email, unknown); LoginAsync updated for OAuth/verification checks; UserService: OAuth password change guard
- [x] **B5: AuthController** — POST /api/auth/register [AllowAnonymous], POST /api/auth/verify-email [AllowAnonymous], GET /api/auth/external-login [AllowAnonymous] (OAuth challenge), GET /api/auth/external-callback [AllowAnonymous] (OAuth callback → JWT redirect)
- [x] **B6: OAuth providers** — NuGet: Microsoft.AspNetCore.Authentication.Google/MicrosoftAccount/Facebook (10.0.3, NOT in shared framework in .NET 10); custom Seznam.cz OAuth handler (SeznamAuthenticationHandler, Options, Defaults, Extensions) using login.szn.cz endpoints; all 4 providers configured in Program.cs with callback paths; OAuth config section in appsettings.json (Apple skipped — needs separate NuGet)
- [x] **B7: Blazor UI** — Register.razor (OAuth pre-fill from query params, password fields, ARES lookup, OAuth provider buttons); VerifyEmail.razor (token validation → success/error); AuthCallback.razor (JWT storage + redirect); Login.razor updated (OAuth buttons + register link); AuthApiService extended (RegisterAsync, VerifyEmailAsync); Blazor RegisterRequest/RegisterResponse models
- [x] **B8: Localization** — ~35 resource keys (CZ + EN): Register_*, VerifyEmail_*, Login_NoAccount, Login_OrContinueWith, EExternalProvider_*, Common_* additions
- [x] **B9: ZMapper + UserService** — UserProfile.cs: added IsExternalLogin ignore; UserService.MapToDto: sets IsExternalLogin from computed property
- [x] **B10: Unit tests** — 14 new AuthService tests (Register: valid/duplicate email/ARES/token/admin role; VerifyEmail: valid/expired/invalid/provisioning; Login: unverified/OAuth; ExternalLogin: existing/link/unknown) + all 8 existing tests preserved; total test count: 278 (277 unit + 1 integration)
- [x] **EF Migration** — `AddSelfRegistrationAndOAuth` (PasswordHash nullable, 5 new columns, 3 new indexes, seed update)
- [x] Build: 0 errors, Tests: 278 pass (277 unit + 1 integration)

### Phase C: Two-Factor Authentication (2FA) (2026-02-12)
- [x] **C1: Domain + Entity Extensions** — new `ETwoFactorMethod` enum (None=0, Totp=1, Email=2); extended User entity with 9 2FA properties (TwoFactorEnabled, TwoFactorMethod, TotpSecretEncrypted, TwoFactorEnabledAt, TwoFactorEmailCode, TwoFactorEmailCodeExpiresAt, TwoFactorSessionToken, TwoFactorSessionTokenExpiresAt, FailedTwoFactorAttempts); added TwoFactorEmail=23 to EContentTemplateType
- [x] **C2: MasterDbContext + NuGet + DTOs** — EF config for all 2FA columns (defaults, max lengths, filtered index on TwoFactorSessionToken); seed TwoFactorEmail content template (Id=7); added Otp.NET v1.4.1 NuGet; 5 new DTOs (TwoFactorStatusDto, TotpSetupResponse, VerifyTotpSetupRequest, VerifyTwoFactorRequest, DisableTwoFactorRequest); extended LoginResponse (RequiresTwoFactor, TwoFactorSessionToken, TwoFactorMethod); extended UserDto (TwoFactorEnabled, TwoFactorMethod)
- [x] **C3: Core 2FA Service** — ITwoFactorService interface (8 methods); TwoFactorService (~350 lines): TOTP setup (OtpNet + QRCoder + Data Protection API encryption), email OTP (BCrypt hashed, 5-min expiry), session tokens (encrypted userId|timestamp, 5-min expiry), rate limiting (5 max attempts); updated AuthService.LoginAsync (2FA check → session token instead of JWT when enabled)
- [x] **C4: API Controller** — TwoFactorController (7 endpoints: GET status, POST totp/setup, POST totp/verify, POST email/enable, POST verify [AllowAnonymous], POST disable, POST admin/force-disable/{userId}); added /api/twofactor to TenantContextMiddleware MasterOnlyPaths; DI: AddDataProtection() + AddScoped<ITwoFactorService, TwoFactorService>()
- [x] **C5: Blazor UI** — TwoFactorApiService (inherits ApiClientBase, 6 methods); TwoFactorSettings.razor (/profile/two-factor) with status banner, TOTP/Email setup cards, TOTP setup dialog (QR + manual key + verify), disable dialog (password confirmation); TwoFactorVerification.razor (/login/verify-2fa) for login step 2 (6-digit code input); Login.razor 2FA redirect; NavMenu Two-Factor Auth link
- [x] **C6: Localization** — ~33 resource keys (CZ + EN): TwoFactor_*, Nav_TwoFactor, ETwoFactorMethod_*
- [x] **C7: Unit Tests** — 12 TwoFactorServiceTests (real Data Protection + InMemoryDatabase + NSubstitute): TOTP setup/verify, email 2FA enable/reject, session token, code verification (valid/invalid/expired/rate-limited), disable, status; 2 AuthServiceTests additions (2FA login flow + direct login)
- [x] Build: 0 errors, Tests: 291 pass (290 unit + 1 integration)

### Phase D: Grid Enhancements, Dashboard Charts, PDF Template Selection, Cloud Storage (2026-02-12)
- [x] **D1: PDF Template Selection** — added `long? contentTemplateId` parameter to `IPdfExportService.GenerateInvoicePdfAsync()`; PdfExportService resolves specific template by ID, falls back to default, then built-in HTML; updated InvoiceController `ExportToPdf` endpoint with `?templateId=` query param; Blazor InvoiceDetail split button (default PDF + template dropdown); Invoices grid PDF icon replaced with MudMenu for template selection; `ExportToPdfAsync(id, templateId)` overload in FakvioService; 3 new unit tests (specific template, null fallback, invalid throws)
- [x] **D2: Grid Multi-Select + Bulk Operations** — AppDataGrid.razor: added `MultiSelection`, `SelectedItems`, `SelectedItemsChanged` parameters for MudTable checkbox column; changed action icons from `Size.Small` to `Size.Medium` with flex layout; Invoices.razor: bulk toolbar with Selected count + Issue/MarkPaid/PDF/Email/Delete/Clear buttons; each filters eligible invoices, shows JS confirm, processes sequentially; `BulkOperationDto.cs` (Request/Result/Error DTOs); `InvoiceService`: BulkCompleteAsync/BulkMarkAsPaidAsync/BulkDeleteAsync (sequential processing, error collection); InvoiceController: 5 bulk endpoints (complete, mark-paid, delete, send-email, PDF ZIP); FakvioService: 5 bulk methods; 8 new unit tests in InvoiceServiceBulkTests.cs
- [x] **D3: Dashboard Charts** — DashboardDto: added InvoiceCountByStatus, InvoiceTotalByClient, InvoiceCountByClient dictionaries; DashboardService: 3 new LINQ aggregation queries (status grouping, top 10 clients by revenue, top 10 by count); Home.razor: 2 MudChart Donut side-by-side (Invoices by Status, Top Clients by Revenue); 3 new unit tests (status grouping, client revenue, empty DB chart data)
- [x] **D4: Google Drive + OneDrive Cloud Storage** — new `ECloudStorageProvider` enum (None=0, GoogleDrive=1, OneDrive=2); `CompanySystemSettings`: 12 new cloud storage properties (Enabled, AccessToken, RefreshToken, TokenExpiresAt, FolderId, FolderName × 2 providers); MasterDbContext: column configs (tokens maxLength 2000, folders maxLength 500); `IExternalCloudStorage` interface (7 methods: auth URL, exchange code, refresh, upload, list folders, test, disconnect); `ICloudStorageOrchestrator` interface (7 methods: upload to all, status, folders, auth, exchange, disconnect, set folder); `GoogleDriveStorageService`: OAuth 2.0 + Drive REST API v3 (direct HttpClient, no SDK); `OneDriveStorageService`: OAuth 2.0 + Microsoft Graph REST API (direct HttpClient); `CloudStorageOrchestrator`: routes to correct provider, fan-out upload to all enabled; `CloudStorageController` (7 endpoints: status, auth-url, callback, disconnect, folders, set-folder, test); TenantContextMiddleware: added `/api/cloud-storage` to MasterOnlyPaths; InvoiceController: auto-upload PDF to cloud after completion (non-blocking try/catch); DI: GoogleDriveStorageService + OneDriveStorageService + CloudStorageOrchestrator; `CloudStorageApiService` (Blazor, inherits ApiClientBase); `FolderPicker.razor` dialog (breadcrumb navigation, folder tree); `CloudStorageCallback.razor` (OAuth popup callback); MyCompany.razor: cloud storage section (2 provider cards with connect/disconnect/folder/test); ~25 localization keys (CZ + EN); 6 new CloudStorageOrchestratorTests
- [x] **EF Migration** — `AddCloudStorageSettings` (12 nullable columns on CompanySystemSettings: GoogleDrive/OneDrive × Enabled bit, AccessToken/RefreshToken nvarchar(2000), TokenExpiresAt datetime2, FolderId/FolderName nvarchar(500))
- [x] Build: 0 errors, Tests: 312 pass (311 unit + 1 integration) — 21 new tests

### 1:N Bank Accounts per Company (2026-02-13)
- [x] **Phase 1: Domain Layer** — new `BankAccount.cs` entity (ClientId FK, Label?, BankName?, AccountNumber required, IBAN?, SWIFT?, CurrencyCode?, IsDefault); added `ICollection<BankAccount> BankAccount` nav prop to Client; marked `BillingSettings.BankAccountNumber` as `[Obsolete]`
- [x] **Phase 2: DTOs** — new `BankAccountDto.cs` with 3 DTOs (BankAccountDto read, CreateBankAccountDto create with `[Required]` AccountNumber, UpdateBankAccountDto update all nullable); added BankAccount collections to ClientDto/CreateClientDto/UpdateClientDto
- [x] **Phase 3: Infrastructure** — TenantDbContext + MasterDbContext: `DbSet<BankAccount>`, `ConfigureBankAccount()` (indexes on ClientId and {ClientId, IsDefault}), `HasMany` with Cascade delete in ConfigureClient; ClientService: `.Include(c => c.BankAccount)` on ALL queries (GetAll, GetPaged, GetById, GetByRegistration, GetIssuer), bank account handling in Create/Update (replace-all strategy), `AddBankAccountAsync` with first-account-auto-default + clear-previous-default; ClientProfile: `CreateMap<BankAccount, BankAccountDto>()`; IClientService: added `AddBankAccountAsync` signature
- [x] **Phase 4: API** — ClientController: `POST /api/client/{id}/bank-account` endpoint
- [x] **Phase 5: Blazor UI** — new `BankAccountDialog.razor` (modal form following AddressDialog pattern); new `BankAccountListEditor.razor` (table with add/edit/delete/set-default following AddressListEditor pattern); new `BankAccountSelect.razor` (MudSelect dropdown for InvoiceDetail showing `Label — AccountNumber (CurrencyCode)`); InvoiceDetail.razor: replaced `<BankAccountInput>` with `<BankAccountSelect>` in create/edit modes, auto-selects default, populates BankAccountNumber/IBAN/SWIFT on selection; MyCompany.razor: added BankAccountListEditor section, removed BankAccountInput from billing card; BankAccountInput.razor: marked deprecated
- [x] **Phase 6: Localization** — 14 keys (CZ + EN): BankAccount_List, BankAccount_Add, BankAccount_Edit, BankAccount_ConfirmDelete, BankAccount_NoAccounts, BankAccount_Default, BankAccount_Label, BankAccount_BankName, BankAccount_AccountNumber, BankAccount_AccountNumberHelper, BankAccount_IBAN, BankAccount_SWIFT, BankAccount_Currency, BankAccount_SelectAccount
- [x] **Phase 7: Unit Tests** — 7 tests in `BankAccountServiceTests.cs`: CreateClient with bank accounts, auto-default first account, replace-all on update, GetIssuer includes accounts, AddBankAccount first-auto-default, AddBankAccount set-new-default-clears-old, AddBankAccount non-existent client
- [x] **Bug fix**: GetIssuerAsync was missing `.Include(c => c.BankAccount)` — bank accounts not returned for issuer
- [x] Build: 0 errors, Tests: 318 pass (317 unit + 1 integration) — 7 new tests
- [x] **Phase 8: EF Migrations** — Generated `AddBankAccount` migration for both TenantDbContext (`Migrations/Tenant/`) and MasterDbContext (`Migrations/Master/`); creates BankAccount table with PK, FK to Client (Cascade), indexes on ClientId and {ClientId, IsDefault}; includes data migration SQL to copy non-empty `BillingSettings.BankAccountNumber` → `BankAccount` rows with `IsDefault=true` and idempotency guard (`NOT EXISTS`)

### Address & BankAccount Editor UX Fix (2026-02-13)
- [x] **Row click to edit** — AddressListEditor + BankAccountListEditor: `<tr>` rows now clickable in edit mode (`@onclick` → opens edit dialog, `cursor: pointer`); `@onclick:stopPropagation` on action columns (star/edit/delete buttons) prevents double-fire
- [x] **ESC = Cancel** — `CloseOnEscapeKey = true` added to DialogOptions for all Address/BankAccount dialog calls
- [x] **Enter = Save** — `@onkeydown` handler on `<MudGrid>` in AddressDialog + BankAccountDialog; Enter key triggers Save()
- [x] **DefaultFocus** — `DefaultFocus="DefaultFocus.FirstChild"` on MudDialog so first field is auto-focused on open
- [x] Build: 0 errors, Tests: 318 pass

### Build Warnings Cleanup (2026-02-13)
- [x] **FolderPicker RZ10012** — fixed `MudDialogContent`/`MudDialogActions` (not real components) → wrapped in `<MudDialog>` with `<DialogContent>`/`<DialogActions>`
- [x] **MUD0002 Title (16 instances)** — changed PascalCase `Title=` to lowercase `title=` on MudIconButton across AddressListEditor, BankAccountListEditor, FolderPicker, Login, Register
- [x] **MUD0002 OnChange (7 instances)** — replaced deprecated `@bind-Value` + `OnChange` pattern with `Value` + `ValueChanged` on MudSelect in Currencies, InvoiceTemplates, ClientDetail, Invoices
- [x] **MUD0002 Dense (4 instances)** — replaced `Dense="true"` with `Margin="Margin.Dense"` on MudSelect, MudTextField, MudDatePicker in Logs
- [x] **CS8602 nullable (8 instances)** — added `?.` null-conditional operators in ContentTemplateDetail, ClientDetail, CompanyDetail, InvoiceTemplateDetail, InvoiceDetail
- [x] Build: **0 warnings** (down from 35), 0 errors, Tests: 319 pass (318 unit + 1 integration)

### Azure SQL Database Management — AzureOperationController (2026-02-26)
- [x] Created `AzureCreateDatabaseRequest` DTO — database name, company ID, free tier config, max size (Contracts)
- [x] Created `AzureDatabaseStatusDto` DTO — database status, SKU, free tier, location, restore date (Contracts)
- [x] Created `IAzureSqlService` interface — CreateDatabase, ListDatabases, GetDatabaseStatus, DeleteDatabase, CreateContainedUser (Application)
- [x] Created `AzureSqlService` implementation — Azure ARM API via Azure.ResourceManager.Sql, DefaultAzureCredential, identity name sanitization (Infrastructure)
- [x] Created `AzureOperationController` — 5 endpoints: provision-tenant (full flow), create-database, list, status, delete (API)
- [x] Added NuGet packages: Azure.Identity 1.17.1, Azure.ResourceManager 1.13.2, Azure.ResourceManager.Sql 1.3.0
- [x] Registered `IAzureSqlService` → `AzureSqlService` in DI (ServiceCollectionExtensions)
- [x] Added `AzureSettings` configuration section to appsettings.json
- [x] Created 35 unit tests: DTO validation, identity name sanitization (SQL injection prevention), controller flow (mocked services), CRUD endpoints
- [x] Build: 0 errors, Tests: 353 pass (353 total)

### CorrelationId Implementation (2026-02-26)
- [x] Added `CorrelationId` property to `AppLog` entity (Domain) — GUID string (36 chars) for end-to-end request tracing
- [x] Configured `CorrelationId` in MasterDbContext — `HasMaxLength(36)` + index for efficient grouping
- [x] Created EF Core migration `AddCorrelationIdToAppLog` (Master)
- [x] Created `CorrelationIdMiddleware` for API — reads/generates X-Correlation-Id header, stores in HttpContext.Items + AsyncLocal
- [x] Created `CorrelationIdMiddleware` for Functions (IFunctionsWorkerMiddleware) — same logic, supports both HTTP and timer triggers
- [x] Added `AsyncLocal<string?>` to `DatabaseLoggerProvider.CurrentCorrelationId` — ambient context for CorrelationId propagation
- [x] Updated `DatabaseLogger.Log()` to read `CurrentCorrelationId` from AsyncLocal into `AppLog.CorrelationId`
- [x] Updated `LogFlushService` SQL INSERT — added `[CorrelationId]` column + `@correlationId` parameter
- [x] Updated `TimerFunctions` SQL INSERT — same column + parameter addition
- [x] Updated `CorsMiddleware` (Functions) — added X-Correlation-Id to `Access-Control-Allow-Headers` + `Access-Control-Expose-Headers`
- [x] Updated API CORS config — added `.WithExposedHeaders("X-Correlation-Id")` for browser access
- [x] Created `CorrelationIdHandler` (DelegatingHandler) for Blazor WASM — adds unique GUID per outgoing API request
- [x] Registered `CorrelationIdHandler` in Blazor `Program.cs` (before `UnauthorizedRedirectHandler`)
- [x] Registered `CorrelationIdMiddleware` as first middleware in API `Program.cs` (before Swagger, CORS, Auth)
- [x] Registered `CorrelationIdMiddleware` as first middleware in Functions `Program.cs` (before CORS, JWT)
- [x] Created `CorrelationIdTelemetryInitializer` (ITelemetryInitializer) for Application Insights enrichment in Functions
- [x] Created 9 unit tests: header propagation, GUID generation, AsyncLocal flow, cleanup on exception, full middleware→logger flow
- [x] Build: 0 errors, Tests: 362 pass (362 total)

### MAUI Blazor Hybrid + Shared RCL (2026-02-28)
- [x] Created `Fakvio.UI.Shared` Razor Class Library (RCL) — shared Blazor components, services, models, resources
- [x] Moved 22 API services, 8 models, 31 pages, 12 shared components, 3 layout files, 2 resource files from BlazorUI to RCL
- [x] Created `ServiceCollectionExtensions.AddSharedUiServices()` — centralized DI registration (MudBlazor, auth, localization, localStorage, 22 API services, handlers, state)
- [x] Updated all namespaces from `Fakvio.BlazorUI` to `Fakvio.UI.Shared` in moved files
- [x] Added global usings for `Microsoft.Extensions.Logging`, `.Configuration`, `.DependencyInjection` (implicit in WASM SDK but not Razor SDK)
- [x] Refactored `Fakvio.BlazorUI` as thin WASM host — only Program.cs, index.html, PWA assets remain
- [x] Updated `index.html` CSS/JS paths to `_content/Fakvio.UI.Shared/...` (RCL static web assets)
- [x] Created `Fakvio.MauiApp` MAUI Blazor Hybrid project — Android, iOS, macOS, Windows multi-target
- [x] MauiProgram.cs with `AddMauiBlazorWebView()` + `AddSharedUiServices()` + platform-specific API URL
- [x] MainPage.xaml with BlazorWebView hosting shared App.razor component
- [x] MAUI-specific wwwroot/index.html (blazor.webview.js, no service worker)
- [x] Platform files: Android (MainActivity, MainApplication, AndroidManifest), iOS, macOS, Windows
- [x] Both projects added to Fakvio.sln
- [x] Build: All 13 non-MAUI projects compile with 0 errors (MAUI requires workload install: `dotnet workload install maui`)
- [x] Tests: All 362 unit tests pass

### AI Chat Assistant (2026-03-01)
- [x] **Domain entities**: ChatConversation, ChatMessage (BaseEntity), EChatRole enum (System/User/Assistant)
- [x] **DTOs**: ChatConversationDto, ChatConversationListDto, ChatMessageDto, SendMessageRequest/Response in Fakvio.Contracts
- [x] **TenantDbContext**: DbSets + Fluent API config (composite index UserId+LastMessageAt, cascade delete, Role as int)
- [x] **IAiProvider interface**: GetCompletionAsync + StreamCompletionAsync (IAsyncEnumerable<string>)
- [x] **4 AI providers**: ClaudeProvider (tryAGI/Anthropic SDK), OpenAiProvider (OpenAI SDK), GeminiProvider (REST), OllamaProvider (REST)
- [x] **AiProviderFactory**: resolves by name (case-insensitive), conditional registration based on API key presence
- [x] **ChatService**: conversation CRUD, message sending, SSE streaming, auto-title from first message
- [x] **ChatContextBuilder**: builds system prompt with tenant business data (clients, invoices, overdue stats)
- [x] **ChatController**: 6 endpoints (GET conversations, GET conversation/{id}, POST send, POST stream SSE, DELETE, GET providers)
- [x] **ChatApiService**: Blazor HTTP service inheriting ApiClientBase, SSE stream reader with IAsyncEnumerable
- [x] **4 UI components**: ChatPanel (main drawer), ChatMessageBubble, ChatInput, ChatConversationList
- [x] **MainLayout**: AI chat toggle button (SmartToy icon) + right MudDrawer (Anchor.End, 400px)
- [x] **Localization**: 10 Chat_ resource keys (CZ + EN)
- [x] **DI registration**: AddAiProviders() in Infrastructure, ChatApiService in UI.Shared
- [x] **Unit tests**: 23 new tests (13 ChatServiceTests, 7 AiProviderFactoryTests, 3 ChatContextBuilderTests)
- [x] **NuGet packages**: Anthropic v3.3.0, OpenAI v2.2.0-beta.4
- [x] Build: 0 errors (excl. MAUI workload), Tests: 368 pass (368 total)

### AI Chat Tool System — ARES Lookup + Client Creation (2026-03-02)
- [x] **IChatTool interface** + ChatToolResult record (Application layer): extensible tool abstraction with ToolName, Description, ParameterDescription, ExecuteAsync
- [x] **IChatToolExecutor interface** + ParsedToolCall record (Application layer): orchestrator for tool detection, instruction building, JSON parsing, dispatch
- [x] **AresLookupTool** (Infrastructure/Service/ChatTools): calls IAresService.GetCompanyInfoAsync, returns formatted company info (name, IČO, DIČ, VAT, address)
- [x] **CreateClientTool** (Infrastructure/Service/ChatTools): calls IClientService.CreateClientAsync with FetchFromAres=true, checks for duplicates first
- [x] **ChatToolExecutor** (Infrastructure/Service/ChatTools): regex-based intent detection (IČO pattern + CZ/EN keywords), JSON tool call parsing (handles markdown code blocks + preamble), tool dispatch via Dictionary<string, IChatTool>
- [x] **ChatService two-pass flow**: when tool intent detected → non-streaming first AI call with tool instructions → parse tool call → execute tool → streaming second AI call with tool results in context; zero latency overhead for regular messages
- [x] **DI registration**: 3 new services (IChatTool×2, IChatToolExecutor) in ServiceCollectionExtensions.cs
- [x] **TenantContextMiddleware test fix**: added TenantDbContext registration to test service provider (fixes previous session's Schema assignment change)
- [x] **Unit tests**: 30 new tests — ChatToolExecutorTests (15: intent detection, JSON parsing, tool dispatch), AresLookupToolTests (6: success, failure, missing params), CreateClientToolTests (7: create, duplicate, errors), ChatServiceTests (3: tool flow, fallback, skip)
- [x] Build: 0 errors (excl. MAUI workload), Tests: 414 pass (414 total)

### AI Chat → UI Interactivity: Navigation from Chat (2026-03-02)
- [x] **ChatUiAction DTO** (Contracts/Dto/Chat): Type, Url, Parameters, factory method Navigate(url) — extensible action command from server to Blazor client
- [x] **ChatToolResult.UiAction** (Application/Service): added optional UiAction property + SuccessWithAction() factory method to ChatToolResult record
- [x] **NavigateTool** (Infrastructure/Service/ChatTools): new IChatTool for navigation — resolves client names via IClientService.GetClientsPagedAsync, builds URLs for new_invoice, new_credit_note, client_detail, client_list, invoice_list, new_client; handles single match (navigate), multiple matches (disambiguate), no match (error)
- [x] **ChatToolExecutor extended**: added NavigationKeywordPattern [GeneratedRegex] (otevři, ukaž, přejdi, naviguj, zobraz, open, show, go to, new invoice, new client, etc.); DetectToolIntent now triggers on EITHER (IČO + keyword) OR (navigation keyword); BuildToolInstructions includes navigate tool with examples
- [x] **ChatService pending action**: _pendingUiAction field captured from toolResult.UiAction after tool execution; GetPendingUiAction() method on IChatService interface; scoped per-request (safe for concurrent users)
- [x] **ChatController SSE action event**: after [DONE], sends `event: action\ndata: {json}\n\n` if pending action exists — uses standard SSE event: field for type discrimination
- [x] **ChatApiService**: new ChatStreamEvent class (Text/Action discriminated union); StreamMessageAsync returns IAsyncEnumerable<ChatStreamEvent>; parses SSE event: lines, tracks currentEventType, yields text or action events; continues reading after [DONE] for action events
- [x] **ChatPanel navigation**: injects NavigationManager, captures pendingAction during streaming, executes NavigateTo(url) after 800ms delay (lets user see AI response first)
- [x] **DI registration**: NavigateTool added to ServiceCollectionExtensions.cs
- [x] **Unit tests**: 33 new tests — NavigateToolTests (14: basic nav, client resolution, ambiguity, errors), ChatToolExecutorTests (+5: navigation keyword detection), ChatServiceTests (+2: pending action set/null)
- [x] Build: 0 errors (excl. MAUI workload), Tests: 447 pass (447 total)

### AI Chat → Invoice Creation from Chat (2026-03-02)
- [x] **CreateInvoiceTool** (Infrastructure/Service/ChatTools): new IChatTool that actually creates invoices via IInvoiceService.CreateInvoiceAsync; resolves client by name (IClientService.GetClientsPagedAsync), gets issuer (GetIssuerAsync), default currency CZK (ICurrencyService.GetCurrencyByCodeAsync), default VAT rate 21% (IVatRateService.GetDefaultStandardRateAsync); parses items JSON array from AI parameters; after creation navigates to /invoices/{id}
- [x] **ChatToolExecutor extended**: added InvoiceCreationPattern [GeneratedRegex] for Czech/English invoice creation keywords (vytvoř/udělej/vystavit fakturu, create/make/generate invoice, faktura za/na + amount); DetectToolIntent now triggers on 3 paths: IČO+keyword, navigation keyword, or invoice creation pattern; BuildToolInstructions includes create_invoice tool with JSON examples
- [x] **DI registration**: CreateInvoiceTool added to ServiceCollectionExtensions.cs
- [x] **Unit tests**: 37 new tests — CreateInvoiceToolTests (24: single/multi item creation, navigate action, VAT defaults, quantity/unit defaults, currency resolution, client not found/ambiguous, missing params, error handling, notes), ChatToolExecutorTests (+13: invoice creation intent detection, available tools count, tool instructions)
- [x] Build: 0 errors (excl. MAUI workload), Tests: 484 pass (484 total)

## Pending

### Payment Matching (implemented 2026-04-24, see PLATBY-ZADANI.md)
- [x] M0: Domain entities + enums (BankAccountMailbox, BankTransaction, PaymentMatch, InboundEmail, MasterMailboxIndex, PaymentMatchingSystemSettings; EPaymentDirection, EImportSource, EMatchStatus, EMatchType, EParseStatus, EInvoiceStatus.PartiallyPaid, Invoice.PaidAmount)
- [x] M0b: EF Core configuration for new entities + DbSet registration in TenantDbContext/MasterDbContext
- [x] M1: AliasGenerator (CSPRNG, ~47 bit entropy) + BankAccountMailboxService with Activate/Deactivate/Regenerate lifecycle
- [x] M2: SysAdmin config — PaymentMatchingSystemSettingsService (DPAPI-encrypted IMAP password) + PaymentMatchingSysAdminController (GET/PUT/test-connection)
- [x] M3: ImapPollWorker (BackgroundService, MailKit, routes via MasterMailboxIndex, moves to Processed/Unrouted folders)
- [x] M4: InboundEmailProcessor — archives every email, respects IsActive + ActiveFrom, dedup via SHA-256 hash, calls parser + matcher
- [x] M5: AiBankEmailParser — strict JSON schema, CZ/SK bank formats, confidence threshold 0.8, prompt-injection mitigation
- [x] M6: PaymentMatchingService — full matcher algorithm (VS exact, VS+amount disambiguation, counterparty+window fallback), manual match/unmatch/ignore, PartiallyPaid / Paid / Overpayment handling
- [x] M7: Payments grid page + PaymentMatchingApiService + PaymentMatchingController (list / match / unmatch / ignore / unmatched-count)
- [x] M8: BankAccountMailboxCard reusable component (Activate/Deactivate/Regenerate + copy alias + stats)
- [x] M10: Localization CZ+EN — ~70 resource keys for Payments, EMatchStatus, EPaymentDirection, EParseStatus, BankMailbox, SysAdminPayment
- [x] M11: 51 unit tests (AliasGenerator, AiBankEmailParser, PaymentMatchingService, InboundEmailProcessor, BankAccountMailboxService) — all green
- [x] DI registration (tenant services + SysAdmin service + HostedService<ImapPollWorker>)
- [x] EF migrations generated (20260425152217 tenant, 20260425152226 master)
- [x] M9: InvoiceDetail Payments panel (progress bar + matches), MatchPaymentDialog, NavMenu entries (/payments + /sysadmin/payment-matching), dashboard KPI tile, PaymentDetail.razor
- [x] Integration tests (5 new tests in PaymentMatchingE2ETests — settings GET/PUT, anonymous block, unmatched-count, mailbox 404)
- [x] TenantContextMiddleware updated to allow SysAdmin payment-matching endpoint without impersonation
- [x] Effective feature flagging via PaymentMatchingSystemSettings.IsEnabled (master) + BankAccountMailbox.IsActive (per-tenant)
- [x] Azure Functions support: extracted `IImapPollService` (Application), `ImapPollService` (Infrastructure), `PaymentMatchingFunctions.RunImapPoll` ([TimerTrigger] every 5 min, honours configurable PollIntervalMinutes via LastRunAt check). API host's `ImapPollWorker` now thin wrapper. SysAdmin /run-now HTTP endpoint added. CLAUDE.md documents the API+Functions duplication pattern.
- [ ] App Insights custom metrics (payment_match_auto, payment_match_manual, payment_parse_failed, inbound_email_received) — currently logged via ILogger / DatabaseLoggerProvider; metrics dashboard would require Application Insights NuGet
- [ ] Playwright E2E tests for the UI flows (out of scope for this session)
- [ ] Integration + Playwright tests (scaffolded in PLATBY-ZADANI.md §11.2/11.3 — to be written)
- [ ] Golden-file email fixtures per bank (KB / ČSOB / Fio / ČS / Air / RB) — collect real (anonymized) emails
- [x] PostgreSQL advisory lock for ImapPollWorker (multi-replica singleton, crash-safe, zero new infra)
- [x] AI parser reads raw HTML directly (no local regex stripping — trust the model)

### Feature gap vs Fakturovač.cz (gap analysis 2026-04-25)

These items exist in Fakturovač and are useful for parity. Order = recommended priority
based on how often Czech customers ask for them. Each line is sized as a standalone ticket.

**Top priority (blockers for many B2B use-cases):**
- [ ] **Zálohové faktury (proforma) + auto-překlop na běžnou fakturu** — new `EDocumentType.AdvanceInvoice` (záloha) + `EDocumentType.TaxReceiptForAdvance` (daňový doklad o přijaté platbě). When the proforma is paid, system creates the tax receipt automatically and on the final invoice deducts the advance. Heavy lifting: domain model, status transitions, PDF templates per type, invoice-from-proforma button.
- [ ] **ISDOC export** + ISDOC attachment in invoice e-mail — Czech e-invoicing standard (ICAI XML schema), Pohoda/Money/Helios import it natively. Add `IIsdocExportService` next to `IPdfExportService`. The e-mail send flow attaches both PDF and ISDOC.
- [ ] **EPO XML pro DPH přiznání + kontrolní hlášení** — extend `VatReportService` with `ToEpoXmlAsync()` producing the official MFČR XML (XSD published by Finanční správa). Single-button "stáhnout pro EPO" on VAT Report page.
- [ ] **Přenesená daňová povinnost (PDP / Reverse Charge §92a–92e ZDPH)** — new `EVatRegime` enum on `InvoiceItem` (Standard / ReverseCharge / Exempt / OutOfScope). Affects DPH calculation (zero VAT row, transferred to recipient), special note on PDF/ISDOC, reflected in EPO XML. Mandatory for stavebnictví/IT/telco.
- [ ] **Recurring Invoices** — already in backlog, raise priority. New entity `RecurringInvoiceSchedule` (templateId, frequency, day-of-month, nextRunAt, isActive). Azure Function timer trigger (daily) generates invoices from templates whose `nextRunAt <= today`. Send via existing email service.

**Medium priority (nice parity items):**
- [ ] **Dodací listy (delivery notes)** — separate document type, simpler than invoice (no totals/VAT, just items list). Reuse `Invoice` table with `EDocumentType.DeliveryNote`.
- [ ] **Pokladní doklady / paragony** — simplified cash receipt format. Could be subtype of invoice or separate entity. Required field: cashier, optional VAT.
- [ ] **Webhooks** — already in backlog; useful for e-shop integrations (notify when invoice paid). Outbound HTTP POST with HMAC signature, retry policy, dead-letter queue.
- [ ] **Export do účetních systémů** — Pohoda XML, Money S3 XML, ABRA XML adapters. Each = separate `IAccountingExporter` implementation reading from the same Invoice DTOs.
- [ ] **ČNB kurzy auto-stahování** — daily Function that fetches the official ČNB exchange rate XML feed and updates `Currency.ExchangeRate` for non-CZK currencies. Used for foreign-currency invoices to compute CZK equivalent.
- [ ] **EU OSS / OSS DPH** — special VAT regime for cross-border B2C sales in EU (§110b ZDPH). Affects VAT rate (recipient country's rate), reporting (separate OSS return), invoice text. Big feature, mostly relevant for e-shops.

**Lower priority / nice-to-have:**
- [ ] **Klientský portál** — public link per client showing their invoices/payment history. Token-based access (no login). Useful for "send a link to your client" UX.
- [ ] **Datové schránky** (Czech mandatory e-government inbox) — integrace přes ISDS API. Send invoices directly to client's data box. Mostly relevant for B2G.
- [ ] **Sklad / inventory** — likely out of scope for an invoicing app, but Fakturovač/Pohoda do have it. If we add it: separate module with stock movements, low-stock alerts, FIFO/LIFO valuation.
- [ ] **Podepisování PDF elektronickým podpisem** — qualified signature (I.CA / Eviden) on issued invoices. Library: BouncyCastle PDF signing.

**Where Fakvio already wins (do not regress):**
- Multi-tenant architecture (Fakturovač is single-account)
- AI Chat assistant (Claude/OpenAI/Gemini/Ollama) with tools
- AI extractor for imported PDF invoices
- AI parser for bank notification e-mails (vs per-bank regex)
- MCP server exposure
- 2FA + OAuth (Google/Microsoft/Facebook/Seznam)
- MAUI Blazor Hybrid native apps (Android/iOS/macOS/Windows)
- Tax estimation calculator for OSVČ (CZ/SK)
- PostgreSQL multi-schema + Azure Functions deployment-ready
- Reusable Quill WYSIWYG editor for templates
- Reminders with automatic interest from late payment

### Future Enhancements (backlog, unrelated to Fakturovač)
- [ ] **Real-time Notifications (SignalR)** — push notifications for new invoices, approaching due dates, tenant provisioning status, payment confirmations
- [ ] **Reporting & Export** — CSV/Excel export of invoice/client lists, advanced reports (revenue by period, VAT summary, client aging), printable views
- [ ] **Audit Log UI** — entity change tracking (who changed what and when), Blazor viewer with diff display, filterable by entity/user/date
- [ ] **API Rate Limiting** — protect against abuse, per-tenant and per-endpoint limits, ASP.NET Core rate limiting middleware
- [ ] **Performance Optimization** — Redis distributed cache, response compression, lazy loading for large datasets, query optimization
- [ ] **Apple OAuth** — add Apple sign-in provider (requires separate NuGet package, Apple Developer account setup)

