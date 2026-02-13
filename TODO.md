# InvoiceApi TODO

## Completed

### Azure Functions Migration — InvoiceApi.Functions Project (2026-02-12)
- [x] Created `InvoiceApi.Functions` project (Azure Functions v4 Isolated Worker Model with ASP.NET Core Integration)
- [x] `InvoiceApi.Functions.csproj` — NuGet refs (Worker, Sdk, Http.AspNetCore, Timer) + project refs (API, Infrastructure, Application)
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
- [x] **4. Fix InvoiceApiService endpoint mismatches** — `IssueAsync` now calls `/complete` (was `/issue`); `MarkAsPaidAsync` now calls `/mark-paid` (was `/mark-as-paid`); removed dead `CancelAsync` method (no matching API endpoint)
- [x] **5. NumberSequences page** — created `NumberSequenceApiService.cs` (inherits ApiClientBase, all 8 endpoints); created `NumberSequences.razor` page (formats table + sequences table with preview, create dialogs, set-default, deactivate); registered service in BlazorUI Program.cs; added ~25 resource keys (`NumSeq_*`) to both .resx files
- [x] **6. InvoiceTemplates detail fixes** — moved `[Inject] IDialogService` from after @code to proper @inject directive; removed duplicate local `ClientDto`/`CurrencyDto` classes (now uses imported Application DTOs); added `@using InvoiceApi.Application.Dto.Client`; localized ~15 hardcoded Czech strings to resource keys (`L["Msg_Error"]`, `L["Msg_SaveSuccess"]`, `L["Msg_DeleteSuccess"]`, `L["Invoice_Created"]`, `L["Invoice_AddAtLeastOneItem"]`, `L["Msg_ConfirmDeleteTitle"]`, `L["Template_ConfirmDelete"]`)
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
- [x] **Phase 1B: Refactored 5 services to inherit ApiClientBase** — InvoiceApiService, ClientApiService, CompanyApiService, UserApiService, DashboardApiService; removed ~25 lines of duplicate auth/HttpClient logic from each
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
- [x] **5.1 MigrationTool console project** — new `InvoiceApi.MigrationTool` .NET 10 console project added to solution; references Infrastructure, Domain, Application projects; NuGet: Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0, Microsoft.Extensions.Configuration.Json 10.0.0, Microsoft.Extensions.Hosting 10.0.0
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
- [x] **Fix 5: ARES cache dual-context** — ROOT CAUSE: AresCacheRepository used TenantDbContext → falls back to non-existent `invoiceapi_tenant_template` → floods logs with connection errors; FIX: made AresCacheRepository dual-context (MasterDbContext + TenantDbContext + ITenantResolver); added AresCache DbSet + ConfigureAresCache to MasterDbContext; removed `Ignore<AresCache>()`; migration `AddAresCacheToMaster`
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
- [x] **D1: PDF Template Selection** — added `long? contentTemplateId` parameter to `IPdfExportService.GenerateInvoicePdfAsync()`; PdfExportService resolves specific template by ID, falls back to default, then built-in HTML; updated InvoiceController `ExportToPdf` endpoint with `?templateId=` query param; Blazor InvoiceDetail split button (default PDF + template dropdown); Invoices grid PDF icon replaced with MudMenu for template selection; `ExportToPdfAsync(id, templateId)` overload in InvoiceApiService; 3 new unit tests (specific template, null fallback, invalid throws)
- [x] **D2: Grid Multi-Select + Bulk Operations** — AppDataGrid.razor: added `MultiSelection`, `SelectedItems`, `SelectedItemsChanged` parameters for MudTable checkbox column; changed action icons from `Size.Small` to `Size.Medium` with flex layout; Invoices.razor: bulk toolbar with Selected count + Issue/MarkPaid/PDF/Email/Delete/Clear buttons; each filters eligible invoices, shows JS confirm, processes sequentially; `BulkOperationDto.cs` (Request/Result/Error DTOs); `InvoiceService`: BulkCompleteAsync/BulkMarkAsPaidAsync/BulkDeleteAsync (sequential processing, error collection); InvoiceController: 5 bulk endpoints (complete, mark-paid, delete, send-email, PDF ZIP); InvoiceApiService: 5 bulk methods; 8 new unit tests in InvoiceServiceBulkTests.cs
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

## Pending

### Multi-Tenant Roadmap — Remaining Tasks
- [ ] **Phase 6.7: End-to-End Testing** — Manual E2E testing: SysAdmin creates company → provisions → creates user → user logs in → creates invoice → SysAdmin switches companies → verifies isolation
- [x] **Cleanup: Remove ApplicationDbContext** — already completed during Phase 3 migration; all .cs files use MasterDbContext/TenantDbContext; AuditTrailTests already on MasterDbContext; no orphaned migration files

