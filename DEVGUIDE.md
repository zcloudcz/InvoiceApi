# Fakvio — Developer Cookbook (DEVGUIDE)

> Praktická příručka, jak věci v projektu **fungují** — ne API reference. Cílová skupina: nový vývojář / AI agent, který se musí rychle zorientovat. Vysvětluje **proč** byla zvolena daná řešení a **kde** v kódu žijí.
>
> **MAINTENANCE RULE (povinné):** Pokud přidáváš nový proces, metodu nebo měníš způsob, jakým se něco dělá (např. změna algoritmu pro hesla, nový background job, nová auth flow, nový pattern, změna placeholderů v šablonách, …), **musíš to zaznamenat zde**. Aktualizuj dotčenou sekci nebo doplň novou. Bez záznamu PR neprochází review.

Spárované zdroje pravdy:
- `CLAUDE.md` — pravidla pro AI agenty + workflow.
- `MEMORY.md` — krátkodobá pracovní paměť aktuálního tasku.
- **`DEVGUIDE.md` (tento soubor)** — dlouhodobé know-how o projektu.
- `README.md` — krátké onboarding intro pro lidi mimo tým.

Pokud máš pochybnost, **zde má pravdu DEVGUIDE.md**, ne komentáře v kódu (komentáře zastarávají).

---

## 1. Architektura a topologie

### 1.1 Solution layout

| Projekt | Typ | Zodpovědnost |
|---------|-----|--------------|
| `Fakvio.Domain` | Class lib | Entity classes (32 v `Entities/`), enums, `Common/` (BaseEntity, ValueObjects). Žádné závislosti na EF, ASP.NET, MudBlazor. |
| `Fakvio.Contracts` | Class lib (zero-NuGet) | DTOs, Pagination, DueDateCalculator. **Sdílí** server (`Application`) i klient (`UI.Shared`). Nesmí mít NuGet závislosti, jinak je rozbije WASM. |
| `Fakvio.Application` | Class lib | Service kontrakty (`Service/I*.cs`). **Stateless logika** — žádný EF context přímo, žádný HTTP. |
| `Fakvio.Infrastructure` | Class lib | Implementace Application interface. EF Core (3 DbContexty), repository, mapping (ZMapper), auth handlery, AI providers. Sem patří všechno, co sahá ven (DB, SMTP, IMAP, OAuth, Azure ARM). |
| `Fakvio.API` | ASP.NET Core | HTTP host. Controllery, middleware (`Middleware/`: CorrelationId → Auth → Impersonation → TenantContext), Swagger. Background workers (např. `ImapPollWorker`) hostuje přes `AddHostedService`. |
| `Fakvio.Functions` | Azure Functions Isolated Worker | Druhý hostovací model (Azure Functions). HTTP triggery + TimerTrigger úlohy. Vlastní middleware mirror. |
| `Fakvio.Functions.Generator` | Roslyn source generator | Generuje rouge boilerplate pro Functions (HTTP endpoint registrace). |
| `Fakvio.UI.Shared` | Razor Class Library (RCL) | **Všechny** Blazor stránky, komponenty, services, modely, resources. Sdílí WASM host i MAUI host. |
| `Fakvio.BlazorUI` | Blazor WebAssembly | Tenký WASM host. Pouze `Program.cs`, `index.html`, PWA assets. |
| `Fakvio.MauiApp` | MAUI Blazor Hybrid | Native shell pro Android/iOS/macOS/Windows. Sdílí komponenty přes `UI.Shared`. |
| `Fakvio.McpServer` | Console (.NET tool) | MCP server pro AI klienty. Stdio transport, ModelContextProtocol 1.0.0. |
| `Fakvio.MigrationTool` | Console | DB migrace, seed master schema, provisioning helper. |
| `Fakvio.AresService` | Class lib | Klient pro ARES (CZ obchodní rejstřík) — autonomní, bez EF. |
| `Fakvio.Tests.Unit` | xUnit | Unit testy (~756). Stack: xUnit + **NSubstitute** + **Shouldly** + EF InMemory. |
| `Fakvio.Tests.Integration` | xUnit | Integration testy (5). `InvoiceApiFactory : WebApplicationFactory<Program>`. |
| `Fakvio.Tests.Playwright` | NUnit | E2E browser testy (~48). Czech locale, Prague TZ. |

### 1.2 Hostovací modely (důležité)

Aplikace má **dva paralelní hosty**, které musí dělat totéž:

| Host | Použití | Background work | Tenant resolution |
|------|---------|-----------------|-------------------|
| **Fakvio.API** (ASP.NET Core) | Lokální dev, klasický App Service / VM deploy | `IHostedService` / `BackgroundService` | `TenantContextMiddleware` |
| **Fakvio.Functions** (Isolated Worker) | Azure Functions consumption plan deploy | `[TimerTrigger]` jen — `BackgroundService` se neškáluje na zero | mirror middleware (`Fakvio.Functions/Middleware/`) |

**Důsledek:** Každá pravidelná úloha musí existovat ve třech kusech — viz §6 (Background work pattern).

### 1.3 Vrstvy (závislosti shora dolů)

```
UI.Shared / BlazorUI / MauiApp ──┐
                                 ├──► Contracts ◄──┐
McpServer ──────────────────────►│                 │
API / Functions ─────────────────┴──► Application ─┤
                                       │           │
                                       ▼           │
                                 Infrastructure ───┘
                                       │
                                       ▼
                                     Domain
```

Pravidla:
- `Domain` nezná nikoho.
- `Contracts` nezná nic kromě BCL (žádné NuGet).
- `Application` zná pouze `Domain` + `Contracts` (interface only).
- `Infrastructure` smí všechno mimo UI.
- UI projekty znají `Contracts` a HTTP klienty — **nesmí** se odkazovat na `Application`/`Infrastructure`/`Domain` (kromě case kde stejný typ jde po HTTP).

---

## 2. Auth & Security — jak to funguje

### 2.1 Hesla (BCrypt, work factor 12)

| Co | Kde | Detail |
|----|-----|--------|
| Algoritmus | BCrypt (NuGet `BCrypt.Net-Next`) | Adaptive hash, salt embedded v hashu, work factor **12** (~100ms verify). |
| Hash | `Fakvio.Infrastructure/Service/AuthService.cs:520-523` | `BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12)`. |
| Verify | `Fakvio.Infrastructure/Service/AuthService.cs:529-540` | `BCrypt.Net.BCrypt.Verify(password, hash)`. Try/catch — corrupted hash → `false`, ne crash. |
| Storage | `User.PasswordHash` (`Fakvio.Domain/Entities/User.cs`) | Plain string sloupec. |

**Pravidlo pro změnu algoritmu:** Pokud někdy budeme migrovat na Argon2 / scrypt, musí proběhnout *gradual rehash* (verify staré → if OK → rehash novým + uložit). **Nikdy** rehashovat všechna hesla najednou — vyžadovalo by to znát plaintext.

### 2.2 JWT — generování + validace

**Generování** (`AuthService.cs:463-512` `GenerateJwtTokenAsync`):
- Algoritmus: **HS256** (HMAC-SHA256), `SymmetricSecurityKey`.
- Claims: `NameIdentifier` (UserId), `Email`, `Name` (FullName), `Role` (enum string `SysAdmin`/`User`/...), `CompanyId` (pokud uživatel patří firmě).
- Expirace: `JwtSettings:ExpirationHours` (default 24h).

**Validace** (`Fakvio.Infrastructure/DependencyInjection/AuthenticationExtensions.cs:44-116`):
- Issuer: `"Fakvio"` (default), `ValidateIssuer = true`.
- Audience: `"FakvioClient"` (default), `ValidateAudience = true`.
- **`ClockSkew = TimeSpan.Zero`** (řádek ~78) — žádná 5min tolerance, jinak by stará tokeny žila déle než inzerujeme.
- **`RoleClaimType = ClaimTypes.Role`** (řádek ~97) — bez tohoto `[Authorize(Roles="SysAdmin")]` tiše vrací 403.

**Konfigurace** (`appsettings.json` → `JwtSettings`):
```json
{
  "JwtSettings": {
    "Secret": "MIN_32_CHARS_OR_LONGER",
    "Issuer": "Fakvio",
    "Audience": "FakvioClient",
    "ExpirationHours": 24
  }
}
```

**`JwtSettings:Secret` NIKDY nedávat do `appsettings.json` checked-in v repu.** Production: env var / Azure Key Vault. Dev: User Secrets (`UserSecretsId` v `Fakvio.API.csproj`).

### 2.3 2FA (Two-Factor Authentication)

**TOTP** (RFC 4226/6238):
- Secret: 20 bytes (160-bit) — generován v `TwoFactorService.cs:75-111` `InitiateTotpSetupAsync`.
- Storage: `User.TotpSecretEncrypted` — šifrováno **Data Protection API**, purpose `"TwoFactorAuth.TotpSecret"`.
- Algoritmus: AES-256-CBC + HMACSHA256 (default DPAPI), klíč rotuje DPAPI sám každých ~90 dní.

**Email OTP**:
- 6-digit kód, **BCrypt hashed** (work factor 12) ve `User.TwoFactorEmailCode`.
- Expirace: 5 min (`User.TwoFactorEmailCodeExpiresAt`).
- Rate limit: max 5 chybných pokusů per session — pak invaliduje session token.

**Session token** (mezi 1. a 2. faktorem):
- Formát: `"userId|timestamp"`, šifrováno DPAPI s purpose `"TwoFactorAuth.SessionToken"` (jiný purpose než TOTP — zabrání cross-purpose decrypt útoku).
- Storage: `User.TwoFactorSessionToken`, expirace 5 min.

### 2.4 OAuth (Google / Microsoft / Facebook / Seznam.cz)

**Setup**: `Fakvio.Infrastructure/DependencyInjection/AuthenticationExtensions.cs:127-174`. Provider se zaregistruje **jen pokud je nakonfigurovaný `ClientId`** — chybějící config tiše skipuje provider, ne crash.

**Konfigurace** (`OAuth:{Provider}`):
```json
{
  "OAuth": {
    "Google":    { "ClientId": "...", "ClientSecret": "..." },
    "Microsoft": { "ClientId": "...", "ClientSecret": "..." },
    "Facebook":  { "AppId":    "...", "AppSecret":    "..." },
    "Seznam":    { "ClientId": "...", "ClientSecret": "..." }
  }
}
```

**Callback URLs**:
- Generic: `/api/auth/external-callback` (`AuthController.cs:219`).
- Per-provider: `/api/auth/google-callback`, `/api/auth/microsoft-callback`, atd.

**Mapování claims → User** (`AuthController.cs:237-247`):
- Email, FirstName, LastName, ProviderId.
- `AuthService.cs:402-455`: pokud user s daným emailem existuje, **link** (přidá ExternalProviderId). Pokud ne, vrátí `null` → frontend přejde na registrační flow.

**Seznam.cz**: vlastní handler `Fakvio.Infrastructure/Authentication/SeznamAuthenticationHandler.cs` (Seznam nepodporuje standardní OIDC). SMTP s Seznam.cz vyžaduje speciální zacházení — viz §4.2.

### 2.5 Password reset

Reuse **InvitationToken** mechaniku (`User.InvitationToken` + `InvitationTokenExpiresAt`):
- Token: GUID string, expirace 48h.
- Generování: `IUserService.ForgotPasswordAsync` (`Fakvio.Application/Service/IUserService.cs:128`).
- **Anti-enumeration**: vrací `null` (success-like) i pro neexistující email, aby útočník neviděl rozdíl.
- Set: `IUserService.SetPasswordAsync` (`IUserService.cs:107`) — validuje token, expiraci, BCrypt hash, **vyčistí token** (one-shot).

### 2.6 CredentialProtector (šifrování secrets v DB)

Co chrání: SMTP hesla, OAuth secrets, AI provider API keys uložené v `CompanySystemSettings` / `SystemConfiguration`.

| Co | Kde |
|----|-----|
| Encrypt | `Fakvio.Infrastructure/Service/CredentialProtector.cs:49-56` |
| Decrypt | `Fakvio.Infrastructure/Service/CredentialProtector.cs:59-80` |
| Algoritmus | Data Protection API (AES-256-CBC + HMACSHA256) |
| Purpose | `"Fakvio.Credentials.v1"` — **immutable**. Změna purpose = nemožnost decrypt všech existujících záznamů. |

**Zero-downtime migrace plaintext → encrypted**: Decrypt catchne `CryptographicException` a vrátí původní hodnotu (legacy plaintext). Při dalším save se hodnota uloží už zašifrovaně.

---

## 3. Multi-tenant — jak data oddělujeme

### 3.1 Big picture

- **1 PostgreSQL instance**, **1 connection string** (`ConnectionStrings:DefaultConnection`).
- **N PostgreSQL schémat** — `public` (master) + `tenant_{companyId}` per zákazník.
- Izolace **per schéma**, ne per database. Levné na provisioning, drahé na cross-tenant queries (které nechceme dělat).

### 3.2 Tři DbContexty

| Context | Schéma | Co obsahuje | Lifetime |
|---------|--------|-------------|----------|
| `ApplicationDbContext` | (přechodný — postupně mizí) | Legacy single-tenant entity. Postupně se přesouvá do Master/Tenant. | Scoped |
| `MasterDbContext` | `public` | `User`, `Company` (issuer registry), `CompanySystemSettings`, `SystemConfiguration`, `AppLog`, code tables (master copy: `VatRate`, `Currency`, `NumberSequenceFormat`, `ContentTemplate`). | Scoped |
| `TenantDbContext` | `tenant_{companyId}` (dynamic) | `Client`, `Invoice`, `InvoiceItem`, `InvoiceTemplate`, `NumberSequence`, `Reminder`, `ReceivedInvoice`, code tables (tenant kopie). | Scoped, **`Schema` set per-request** middlewarem. |

**Klíčový trik**: `TenantModelCacheKeyFactory` (`Fakvio.Infrastructure/Data/TenantModelCacheKeyFactory.cs:24-48`). EF Core cachuje compiled model per `DbContext.Type` — bez tohoto factory by všechny tenanty sdílely jediný cached model s prvním viděným schématem. Cache key teď zahrnuje `Schema` → jeden compiled model per schéma, lazy.

### 3.3 Request flow (kdo nastaví schéma)

Pipeline pořadí v `Fakvio.API/Program.cs`:

1. `app.UseCorrelationId()` (řádek 146) — generuje/čte `X-Correlation-Id` header, ukládá do `HttpContext.Items` + AsyncLocal pro logger.
2. `app.UseAuthentication()` (řádek 169) — JWT validace, naplní `HttpContext.User`.
3. `app.UseImpersonation()` (řádek 174) — pokud Role=SysAdmin a header `X-Company-Id`, vytvoří/přepíše claim `CompanyId`.
4. `app.UseTenantContext()` (řádek 180) — z `CompanyId` claimu resolvuje schema name a nastaví na scoped `TenantDbContext`.

**TenantContextMiddleware** (`Fakvio.API/Middleware/TenantContextMiddleware.cs`):
- Řádek 35: `MasterOnlyPaths` — `/api/auth`, `/api/user`, `/api/company`, `/api/system-configuration`, `/api/logs`, `/api/twofactor`, `/api/cloud-storage`, `/api/email`, `/api/sysadmin/payment-matching`. **Skip** tenant kontroly.
- Řádek 56: `SysAdminCodeTablePaths` — code-table endpointy přístupné SysAdminovi i bez `X-Company-Id` (konfigurace systémových číselníků).
- Řádek 126: `await factory.ResolveSchemaAsync(companyId)` — jediný zdroj pravdy.
- Řádek 144: `await factory.EnsureMigratedAsync(companyId)` — lazy migrate per schema, cached per-process.

**ImpersonationMiddleware** (`Fakvio.API/Middleware/ImpersonationMiddleware.cs:17-79`):
- **Pouze SysAdmin** může impersonovat. Ne-SysAdmin header `X-Company-Id` se ignoruje (frontend ho stejně neposílá).
- Header → claim `CompanyId` (přepíše JWT claim, pokud je).
- **Security note**: middleware MUSÍ být *po* `UseAuthentication`, aby Role byl validní.

### 3.4 Functions middleware (zrcadlový mirror)

`Fakvio.Functions/Middleware/TenantContextMiddleware.cs:40-100` — stejný pattern, ale **pozor na dvě DI scope**:

```
httpContext.RequestServices    ← ASP.NET Core scope (HTTP triggery)
context.InstanceServices       ← Functions Worker scope (kde žije Function class)
```

**Schema musí být nastavena na obou** scope, jinak Function vidí jiný `TenantDbContext` než middleware. Factory to řeší automaticky — pokud měníš tento middleware, ověř že na obou scope teče stejný `Schema`.

### 3.5 Provisioning nového tenanta

`Fakvio.Infrastructure/Service/TenantProvisioningService.cs:51-165` `ProvisionTenantAsync`:

1. Načti `CompanySystemSettings` z master DB (musí už existovat).
2. Načti issuer Company z master DB.
3. **`CREATE SCHEMA tenant_{companyId}`** (řádek 101).
4. **Apply EF migrations** na nové schéma (řádek 108) — `Database.MigrateAsync()` s `Schema` setnutým.
5. **Copy code tables** z master → tenant: VatRate, Currency, NumberSequenceFormat, ContentTemplate (řádek 114).
6. Create issuer record v tenant schématu.
7. Create default NumberSequence pro **všechny 4 typy**: Invoice (`INV`), CreditNote (`CN`), Proforma (`PF-`), TaxReceiptForAdvance (`DPP-`) — viz `CreateDefaultNumberSequencesAsync` (#26).
8. Mark `IsProvisioned=true, IsActive=true, ProvisionedAt=UtcNow` v master DB.

**Idempotentní** (řádky 76-82) — bezpečné re-provision po částečné chybě.

**Schema permissions**: `EnsureSchemaPermissionsAsync()` (řádek 337-344) — `GRANT ALL ON ALL TABLES IN SCHEMA` + `ALTER DEFAULT PRIVILEGES`. Použij `NpgsqlDataSource`, ne raw connection string — funguje s Azure AD/Managed Identity tokens automaticky.

### 3.6 Azure SQL provisioning (alternativní deploy)

`Fakvio.Infrastructure/Service/AzureSqlService.cs` — pro Azure SQL deploy:
- ARM API přes typed HttpClient.
- **Sanitize identity name** — Azure SQL má omezení na názvy. Helper metoda v servise (kontrol když měníš).
- Endpointy v `Fakvio.API/Controller/AzureOperationController.cs`: provision-tenant, create-database, list, status, delete.

---

## 4. Use cases — end-to-end flows

### 4.1 Login flow (browser → API → localStorage)

1. Blazor `LoginPage` → `AuthApiService.LoginAsync` → `POST /api/auth/login`.
2. `AuthController.cs` → `AuthService.LoginAsync` (`AuthService.cs:94`):
   - Find user by email (master DB).
   - `VerifyPassword` (BCrypt).
   - Pokud `User.TwoFactorEnabled` → vrátí `requiresTwoFactor=true` + sessionToken (NE JWT). Frontend zobrazí 2FA prompt.
   - Jinak → `GenerateJwtTokenAsync` → vrátí JWT + UserDto.
3. Blazor `CustomAuthenticationStateProvider.MarkUserAsAuthenticatedAsync`:
   - Serializuje `LoginResponse` do **Blazored.LocalStorage** key `"UserSession"`.
   - Volá `NotifyAuthenticationStateChanged`.
4. `ApiClientBase.AddAuthorizationHeaderAsync` (`Fakvio.UI.Shared/Services/ApiClientBase.cs:49`):
   - Při každém request načte token z localStorage.
   - Přidá `Authorization: Bearer {token}`.
   - Pokud user je SysAdmin a má `ImpersonatedCompanyId` v localStorage → přidá `X-Company-Id: {id}`.

**401 handling**: `UnauthorizedRedirectHandler` (DelegatingHandler registrovaný v `Fakvio.BlazorUI/Program.cs:37`) intercepts 401 → vyčistí session → redirect `/login`.

### 4.2 Email + PDF (faktura odchází zákazníkovi)

`Fakvio.Infrastructure/Service/EmailService.cs:79-153` `SendInvoiceEmailAsync`:

1. Load invoice s navigation properties (Items, Client, Company).
2. **PDF**: `IPdfExportService.ExportInvoiceAsync` →
   - Resolve template: explicit ID → default per (DocumentType + ClientLanguage) → built-in fallback HTML (`PdfExportService.cs:76-115`).
   - Replace placeholders `{{Key}}` (regex `\{\{(.+?)\}\}`).
   - iText7 `HtmlConverter.ConvertToPdf` → A4 stream.
   - **DŮLEŽITÉ**: `pdfWriter.SetCloseStream(false)` (`PdfExportService.cs:139-177`) — bez toho iText zavře `MemoryStream` při dispose `PdfDocument` a nemůžeš číst bytes.
3. **ISDOC** (CZ standard pro elektronické faktury): `IIsdocExportService` → XML.
4. **Render template** pro tělo emailu: `IContentTemplateService.RenderTemplateAsync` (`ContentTemplateService.cs:235-252`) — placeholdery v subjectu i body.
5. **SMTP settings — 3-tier resolution** (`EmailService.cs:409-483`):
   1. `CompanySystemSettings.SmtpPasswordEncrypted` (master DB) — per-company SMTP, decrypt přes `ICredentialProtector`.
   2. `SystemConfiguration` (master DB) — system-wide SMTP přes `ISystemConfigurationService.GetSmtpPasswordAsync`.
   3. `appsettings.json` `SmtpSettings` — fallback.
6. **MailKit** (`EmailService.cs:273-366`):
   - Port → SocketOptions: 465 → `SslOnConnect`, 587 → `StartTls`, jiné → `None`.
   - Certificate validation s **CRL tolerance** — akceptuje self-signed pokud CRL nedostupné.
   - **SASL mechanism strip** (řádek 338-339): odstraní XOAUTH2 + NTLM kvůli Seznam.cz (jinak hlásí UnAuthenticated).
   - UTF-8 encoding pro PLAIN/LOGIN auth.
7. Build `MimeMessage` s PDF + ISDOC attachments → `SmtpClient.SendAsync`.

**Pokud měníš SMTP/Seznam config** → ověř SASL strip + CRL tolerance, nesahej na ně bez testu proti všem tří providerům (Gmail, Outlook, Seznam).

### 4.3 ContentTemplate (Handlebars-like placeholders)

| Co | Kde |
|----|-----|
| Entity | `Fakvio.Domain/Entities/ContentTemplate.cs` |
| Service | `Fakvio.Infrastructure/Service/ContentTemplateService.cs` |
| Regex | `\{\{(.+?)\}\}` (`ContentTemplateService.cs:292`) — compiled, source-generated. |
| Render | `RenderTemplateAsync(templateId, dict)` → `ReplacePlaceholders(html, dict)` (řádky 235-269). Trim klíče, neznámý placeholder = ponechán beze změny. |

**Typy** (`EContentTemplateType`):
- `InvoicePdf=1`, `CreditNotePdf=2`
- `InvoiceEmail=10`, `CreditNoteEmail=11`
- `InvitationEmail=20`, `ReminderEmail=21`, `PasswordResetEmail=22`

**Default chain** (`ContentTemplateService.cs:111-143`):
1. Exact (Type + Language + IsDefault + IsActive).
2. Any-language default (Type + IsDefault + IsActive).
3. Built-in inline HTML (EmailService řádky 545-591).

**Pokud přidáváš nový typ šablony**: 1) přidej hodnotu do `EContentTemplateType`, 2) přidej seed default template do migrace + provisioning copy step (`TenantProvisioningService` krok 5), 3) zaznamenej zde do tabulky.

### 4.4 Invoice lifecycle

```
[Draft] ──complete──► [Issued] ──mark-paid──► [Paid]
                                ▲                │
                                └── (auto via PaymentMatching)
                                                 │
                                                 ▼
                          (overdue + reminder cycle, viz §4.6)
```

| Akce | Endpoint | Co se stane |
|------|----------|-------------|
| Create | `POST /api/invoice` | Status=Draft. **Document number generován hned** (ne až při complete) — viz `INumberSequenceService`. |
| Complete (= "issue") | `POST /api/invoice/{id}/complete` | Status=Issued, IssuedAt=now. **Pozor na endpoint name** — backend používá `/complete`, ne `/issue`. |
| Mark paid | `POST /api/invoice/{id}/mark-paid` | Status=Paid, PaidAt=now. Endpoint je `/mark-paid`, ne `/mark-as-paid`. |
| Send email | `POST /api/invoice/{id}/send-email` | Viz §4.2. |

**Variable Symbol** (CZ banking):
- Max **10 číslic**, jen digits.
- Auto-fill z document number: `new string(docNumber.Where(char.IsDigit).Take(10).ToArray())`.
- Validace: DTO `[StringLength(10)] + [RegularExpression(@"^\d{0,10}$")]`.

### 4.5 Payment matching (IMAP → invoice mark paid)

`Fakvio.Infrastructure/Service/ImapPollService.cs:50-139`:

1. **AdvisoryLock** (`AdvisoryLock.cs:37`, `pg_try_advisory_lock`, key `0x46414B56494F5059L` = "FAKVIOPY") — zajistí 1 IMAP cyklus napříč všemi replicami (API + Functions současně).
2. Connect IMAP přes MailKit, fetch unseen messages.
3. Resolve alias → tenant (alias mailbox = `BankAccountMailbox.Alias`).
4. Per email:
   - **`InboundEmailProcessor`** (řádky 88-112): hash → idempotency, persist `InboundEmail` archive, mark `ParseStatus`.
   - **`IBankEmailParser.ParseAsync`** (řádek 122-126): AI-based extrakce (amount, date, VS, payer account).
   - **`PaymentMatchingService.MatchAsync`** (`PaymentMatchingService.cs:14-24`):
     - Rule 1: exact VS match (jediná unpaid invoice s daným VariableSymbol).
     - Rule 2: account + amount + due-date window ±7 dní.
     - Match → `PaymentMatch` entity, invoice `Status=Paid`.

**SysAdmin "Run now"**: `POST /api/sysadmin/payment-matching/run-now` (`PaymentMatchingSysAdminController.cs`) — volá tutéž `IImapPollService.RunCycleAsync()`.

### 4.6 Reminders (dunning) — daily 6 AM UTC

`Fakvio.Infrastructure/Service/ReminderService.cs:449-484` `ProcessOverdueInvoicesAsync`:

1. Per-tenant scope (`Fakvio.Functions/ReminderFunctions.cs:50-108`).
2. Find invoices: `Status=Completed AND DueDate < today AND Paid=false`.
3. Pro každou volá `ProcessSingleInvoiceAsync` (řádky 490-570):
   - Resolve effective settings: client override > company default.
   - Next level = `1 + count(existing reminders)`.
   - Effective date:
     - Level 1: `DueDate + GracePeriodDays`.
     - Level N: `LastReminderDate + DaysAfterPrevious[N]`.
   - Pokud čas dosažen: vytvoř `Reminder` (Draft) + fee + interest, optional auto-send.

**ReminderSettings** (per company nebo per client) + **ReminderLevel** array (Level 1/2/3 s `DaysAfterPrevious`, `FixedFeeCzk`, template ID).

### 4.7 AI Chat (Claude/OpenAI/Gemini/Ollama)

- Provider abstrakce: `IAiProvider` (`Fakvio.Application/Service/IAiProvider.cs`).
- Implementace: `Fakvio.Infrastructure/AiProviders/` (Anthropic, OpenAI, Gemini, Ollama).
- API key storage: `CompanySystemSettings.AiApiKeyEncrypted` (per company) přes `CredentialProtector`.
- SSE streaming přes `ChatController.StreamAsync`.
- **MCP Tools**: chat má přístup k 21 tools přes `IChatToolExecutor` — invoice CRUD, client CRUD, reporting.

### 4.8 MCP Server (`Fakvio.McpServer`)

- Standalone .NET tool (PackAsTool), stdio transport.
- Auth: `FAKVIO_API_TOKEN` env var (JWT bearer).
- 21 tools: 8 invoice + 6 client + 3 template + 4 reporting.
- Konfigurace v Claude Desktop / Claude Code: spustí `fakvio-mcp-server` jako subprocess se stdio piping.

---

## 5. Datová vrstva

### 5.1 PostgreSQL specifika

| Téma | Pravidlo |
|------|----------|
| Concurrency | **`uint RowVersion` + `IsConcurrencyToken()` + `ValueGeneratedOnAddOrUpdate()`** mapováno na `xmin` system column. **NE** `UseXminAsConcurrencyToken()` (removed v Npgsql 10.x). |
| Filtered indexes | `HasFilter("\"ColumnName\" IS NOT NULL")` — **double quotes** (PostgreSQL), NE square brackets (SQL Server). |
| Schema-per-tenant | `HasDefaultSchema(Schema)` v `OnModelCreating`. |
| Provider | `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.0. |
| EF Core verze | 10.0.1. Pozor na MSB3277 warning kvůli Relational 10.0.0 ↔ 10.0.1 mismatch — funguje, jen warn. |

### 5.2 Migrace (`Fakvio.Infrastructure/Migrations/`)

- Dvě sady — pro Master a Tenant DbContext.
- **Tenant migrations** se aplikují **lazy** při prvním access tenanta (`TenantContextMiddleware` → `factory.EnsureMigratedAsync`).
- **Master migrations** se aplikují při startu API (Program.cs, dev only) nebo přes `Fakvio.MigrationTool`.

### 5.3 ZMapper (source-generated mapping)

- Verze 1.2.0. Single namespace: `using ZMapper;`.
- Profile classes v `Fakvio.Infrastructure/Mapping/` (8 profilů).
- BaseEntity properties mapují automaticky.
- **`ForMember Ignore` funguje. `ForMember MapFrom` NE** — navigation props se musí mapovat manuálně.
- Generated extension methods v namespace `Fakvio.Infrastructure.Mapping`.
- Doporučená global usings v `Fakvio.API.csproj`:
  ```xml
  <Using Include="ZMapper" />
  <Using Include="Fakvio.Infrastructure.Mapping" />
  ```

### 5.4 Audit, RowVersion, soft delete

- **Audit fields**: `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy` na `BaseEntity`. Naplňované v `MasterDbContext.SaveChangesAsync` / `TenantDbContext.SaveChangesAsync` override z `ICurrentUserService`.
- **Soft delete**: pole `IsDeleted` na entitách, kde dává smysl (Currency, …). Global query filter `HasQueryFilter(x => !x.IsDeleted)`.
- **Concurrency**: každá entita s rizikem race condition má `RowVersion` (xmin).

---

## 6. Background work pattern (POVINNÝ)

### 6.1 Pravidlo

Každá pravidelná úloha **MUSÍ** existovat ve třech kusech:

1. **Stateless service** (`Fakvio.Application/Service/I{X}Service.cs` + `Fakvio.Infrastructure/Service/{X}Service.cs`):
   - Jediná metoda `RunCycleAsync(CancellationToken)`.
   - **Žádný stav, žádný `Thread.Sleep`** — udělá jednu iteraci a vrátí se.
2. **Tenká `BackgroundService` obálka** (`Fakvio.Infrastructure/Service/{X}Worker.cs`):
   - `while (!ct.IsCancellationRequested) { await service.RunCycleAsync(ct); await Task.Delay(interval, ct); }`.
   - Registrovaná v API host přes `AddHostedService<{X}Worker>()`.
3. **`[TimerTrigger]` Function** (`Fakvio.Functions/{X}Functions.cs`):
   - CRON typicky kratší než požadovaný interval (např. tick každých 5 min pro úlohu, která má běžet každých 30) + service uvnitř kontroluje `LastRunAt` a brzy-spuštěné cykly skipuje.
   - Důvod: CRON v Function je **statický** (deploy-time), kdežto service uvnitř může číst interval z DB → dynamický interval i v Functions.

### 6.2 Mutual exclusion

**Vždy** použít PostgreSQL **advisory lock** (`Fakvio.Infrastructure/Service/AdvisoryLock.cs`):
- Session-bound (crash-safe — uvolní se při disconnectu).
- Nezávisí na externí službě (Redis, Blob lease).
- Klíč = stable `long` per task. Konvence: ASCII bytes hesla. Příklad — IMAP poll: `0x46414B56494F5059L` ("FAKVIOPY").
- Volat s `NpgsqlDataSource` (ne raw connection string) → kompatibilní s Managed Identity tokens.

### 6.3 Existující dvojice (registr — udržuj!)

| Úloha | Stateless service | Worker (API) | Function | Lock key |
|-------|-------------------|--------------|----------|----------|
| Log flush (DB → AppLog) | `LogFlushService` | (BackgroundService v Infrastructure) | `TimerFunctions.FlushLogs` (CRON `*/10 * * * * *`) | (žádný — local buffer) |
| Log cleanup | `LogCleanupService` | (BackgroundService) | `TimerFunctions.CleanupLogs` (CRON `0 0 0 * * *` daily) | — |
| Reminders / dunning | `IReminderService.ProcessOverdueInvoicesAsync` | — | `ReminderFunctions.ProcessReminders` (CRON `0 0 6 * * *`) | per-tenant scope |
| Payment matching (IMAP) | `IImapPollService.RunCycleAsync` | `ImapPollWorker` | `PaymentMatchingFunctions.RunImapPoll` (CRON `0 */5 * * * *`) | `0x46414B56494F5059L` |

### 6.4 Když přidáš novou periodickou úlohu

Krok-za-krokem:

1. Přidat interface do `Fakvio.Application/Service/I{X}Service.cs` s `RunCycleAsync`.
2. Implementovat ve `Fakvio.Infrastructure/Service/{X}Service.cs`. Pokud je *cross-host* exclusive, použít advisory lock + nový `long` klíč (zaregistrovat do tabulky výše).
3. Worker: `Fakvio.Infrastructure/Service/{X}Worker.cs : BackgroundService`. Registrovat v API DI (`AddHostedService<{X}Worker>`).
4. Function: `Fakvio.Functions/{X}Functions.cs` s `[TimerTrigger("CRON")]`. Volá tentýž service.
5. **Aktualizuj DEVGUIDE.md tabulku §6.3.**
6. Test: unit test `{X}Service` (logika), integration test že Worker volá service.

**Pokud chybí Functions varianta** → Azure deploy úlohu prostě neběží a ticha. **Pokud chybí worker** → lokální dev v `dotnet run` nikdy cyklus nevidí běžet.

---

## 7. UI patterns (Blazor + MudBlazor)

### 7.1 RCL `Fakvio.UI.Shared` — co kam patří

| Adresář | Obsah |
|---------|-------|
| `Components/Pages/` | Routable stránky (`@page`). |
| `Components/Layout/` | `MainLayout`, `NavMenu`. |
| `Components/Shared/` | Znovupoužitelné komponenty (`EnumSelect`, `BankAccountInput`, `InvoiceItemEditor`). |
| `Services/` | API client services (34 souborů), všechny dědí `ApiClientBase`. `CustomAuthenticationStateProvider`. `AppStateService`. |
| `Models/` | UI-only modely (form view models). DTO pro server volání žije v `Fakvio.Contracts`. |
| `Resources/` | `.resx` soubory pro lokalizaci. |
| `wwwroot/` | CSS, JS, ikony. |

**RCL gotchas**:
- SDK: **`Microsoft.NET.Sdk.Razor`**, NE `Microsoft.NET.Sdk.BlazorWebAssembly`.
- **NESMÍŠ** `<FrameworkReference Include="Microsoft.AspNetCore.App" />` — rozbije to WASM build (no browser-wasm runtime). Místo toho explicit NuGet packages.
- Implicit usings nepokrývají `Microsoft.Extensions.Logging`/`.Configuration`/`.DependencyInjection` — přidat jako `<Using Include="..." />` v csproj.
- Static web assets: pokud RCL má `StaticWebAssetBasePath="/"`, **odstranit duplicitní soubory** z hostujícího wwwroot (jinak conflict).
- Cesty v host `index.html`: `_content/Fakvio.UI.Shared/css/app.css`.

### 7.2 ApiClientBase pattern

`Fakvio.UI.Shared/Services/ApiClientBase.cs`:
- Dědí ho každý API service.
- Konstruktor: `IHttpClientFactory` → named client `"InvoiceAPI"` (registrován v `Fakvio.BlazorUI/Program.cs:25`).
- `AddAuthorizationHeaderAsync()` (řádek 49-57): JWT z `CustomAuthenticationStateProvider.GetTokenAsync()`.
- `AddImpersonationHeaderAsync()` (řádek 62): `X-Company-Id` pokud SysAdmin impersonuje.
- Error handling: 401 → `UnauthorizedRedirectHandler`, ostatní → `ApiException`.

**Když přidáváš nový API service**: dědit `ApiClientBase`, registrovat ve `Fakvio.UI.Shared/ServiceCollectionExtensions.cs` `AddSharedUiServices`.

### 7.3 MudBlazor v8 pravidla

| Co | Pravidlo |
|----|----------|
| Dialog visibility | `@bind-Visible` (NE `@bind-IsVisible` — renamed v v8). |
| Interactivity | `@rendermode InteractiveServer` (nebo WASM mode) je **POVINNÝ** pro klikací události. |
| MudDatePicker | Nelze `@bind-Date` + `DateChanged` zároveň (RZ10010). Použít `Date="..."` + `DateChanged` callback. |
| OnRowClick | Vyžaduje explicitní `T` parametr na MudTable kvůli method group resolution. |
| Dialog sizing | `DialogOptions.BackgroundClass` (NE `ClassBackground`) + `MaxWidth.False` + custom CSS `.dialog-large .mud-dialog { width: 85vw; }`. |
| Split button | `MudButtonGroup OverrideStyles="false"` + `MudButton` (primární) + `MudMenu` (šipka). |

### 7.4 Generic komponenty s `@typeparam`

`@bind-Value` **NEFUNGUJE** s generic Razor komponentami. Workaround: `Value` + `ValueChanged` na call site:

```razor
<EnumSelect TEnum="EPaymentMethod"
            Value="_val"
            ValueChanged="@(v => _val = v)" />
```

### 7.5 EnumSelect lokalizace

- Resource key formát: **`{EnumTypeName}_{EnumValue}`** (např. `EPaymentMethod_BankTransfer`).
- Fallback na `ToString()`: `IStringLocalizer.ResourceNotFound`.
- Nullable variant přes `Nullable=true` parameter (Clearable MudSelect s `TEnum?`).

### 7.6 Lokalizace (CZ default, EN fallback)

- Resources: `Fakvio.UI.Shared/Resources/SharedResource.resx` + `SharedResource.en.resx`.
- Marker class `Fakvio.UI.Shared/SharedResource.cs` — **MUSÍ** být v root namespace, NE v `.Resources` sub-namespace (jinak double-path trap → resource manager hledá `Resources/Resources/SharedResource.resx`).
- Setup: `services.AddLocalization(o => o.ResourcesPath = "Resources");` (`ServiceCollectionExtensions.cs:33`).
- WASM Program.cs: `CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("cs-CZ")`.

**API host culture detection** (pokud někdy povolíme browser auto-detect): `AcceptLanguageHeaderRequestCultureProvider` po `CookieRequestCultureProvider` — cookie = explicit choice, header = browser default.

### 7.7 NavigationLock (unsaved changes)

```razor
<NavigationLock OnBeforeInternalNavigation="HandleNav"
                ConfirmExternalNavigation="@_isDirty" />
```

- Internal nav: callback s `JS.InvokeAsync<bool>("confirm", msg)` + `context.PreventNavigation()`.
- External nav: native browser dialog.
- **Nelze použít MudBlazor dialog** během intercept (synchronní browser API).
- Pattern: `_isDirty=true` na change, `=false` na save/cancel.

### 7.8 Inline editable grids (InvoiceItemEditor)

- MudTable s `MudTextField`/`MudNumericField`/`MudSelect` přímo v `<RowTemplate>`.
- `Value` + `ValueChanged` (NE `@bind-Value`) — trigger parent notify on each change.
- Jednodušší než `MudDataGrid EditMode` pro custom layouty.

---

## 8. Tests

### 8.1 Unit tests (`Fakvio.Tests.Unit`)

- Stack: **xUnit 2.9.3 + NSubstitute 5.3.0 + Shouldly 4.3.0**. (Migrovali jsme z Moq+FluentAssertions únor 2026 — všechny existující testy už jsou converted.)
- DB: `EntityFrameworkCore.InMemory` 10.0.0 s `Guid.NewGuid().ToString()` per test fixture (izolace).
- Each test class: `IDisposable` cleanup.

**NSubstitute patterns**:
```csharp
sub.Method(Arg.Any<string>()).Returns(callInfo => $"prefix:{callInfo.Arg<string>()}");
sub.MethodAsync(Arg.Any<T>()).Returns(value);  // auto-wraps Task, žádný .ReturnsAsync()
await sub.Received(1).MethodAsync(Arg.Any<T>());  // received check vyžaduje await pro async
```

**Shouldly gotchas**:
- `ShouldNotBeNullOrEmpty()` je **string-only**. Pro byte[]: `.ShouldNotBeNull()` + `.ShouldNotBeEmpty()`.
- Nullable: `DateTime?` nemůže `ShouldBeGreaterThan` přímo. Unwrap: `value!.Value.ShouldBeGreaterThan(x)`.
- `ShouldAllBe(predicate)` (NE `.OnlyContain`).
- Žádné "because" messages — Shouldly to nepodporuje.

### 8.2 Integration tests (`Fakvio.Tests.Integration`)

- `InvoiceApiFactory : WebApplicationFactory<Program>` (řádky ~31-80).
- Override v `ConfigureWebHost`:
  - InMemoryDatabase místo PostgreSQL.
  - Replace `ITenantProvisioningService` test double.
  - Remove `LogFlushService`, `LogCleanupService` (background services).
  - Environment `"Testing"` → skip migrations.
- `Program` má `public partial class Program { }` na konci `Fakvio.API/Program.cs:189` aby `WebApplicationFactory<Program>` mohl referencovat top-level statements typ.

### 8.3 E2E (`Fakvio.Tests.Playwright`)

- Stack: **Microsoft.Playwright.NUnit 1.52.0 + NUnit 4.3.2**.
- Base class: `FakvioPageTest : PageTest`.
- Auth: `AuthHelper.LoginAsAdminAsync()` — volá `POST /api/auth/login`, injectuje JWT do `localStorage['UserSession']` (PascalCase keys, matching Blazor JsonSerializer).
- Context: Czech locale, Prague TZ, base URL z `TestConfiguration`.
- SysAdmin impersonation: `localStorage['ImpersonatedCompanyId']='1'`.

### 8.4 Co testovat kde

| Co | Layer |
|----|-------|
| Domain logic, value objects, calculations | Unit |
| Service orchestrace s mocky externí services | Unit (NSubstitute) |
| EF queries, repository | Unit (InMemoryDatabase) |
| Controller → service → DB end-to-end | Integration (`WebApplicationFactory`) |
| User-visible flow (login, invoice CRUD UI) | Playwright |
| External API (SMTP, IMAP, OAuth, ARES) | Manuálně + smoke testy |

---

## 9. Deploy

### 9.1 GitHub Actions workflows (`.github/workflows/`)

| Soubor | Trigger | Co dělá |
|--------|---------|---------|
| `blazorui-deploy.yml` | Push `master`, manual, PR (path-filtered) | Build `Fakvio.BlazorUI` (WASM publish) → deploy GitHub Pages. Přidá CNAME, .nojekyll, kopie `index.html → 404.html` (client-side routing). |
| `master_zcloudinvoicingapi.yml` | Push `master` | Publish `Fakvio.Functions.csproj` → Azure Function App `zcloudinvoicingapi`. Auth přes managed identity (federated credentials). |

**Pozn.**: Pro `Fakvio.API` (klasický host) **není dedicated workflow** v repu — historicky se hostil přes externí App Service nebo manuálně. Pokud přidáš API workflow, zaznamenej zde.

### 9.2 Konfigurace (precedence shora dolů)

`Fakvio.API/Program.cs` (řádky 24-31) používá default `WebApplication.CreateBuilder()` precedence:

1. `appsettings.json` (committed, žádné secrets).
2. `appsettings.{Environment}.json` (Development.json committed jen pro dev defaults).
3. **User Secrets** (dev only, dle `<UserSecretsId>` v csproj).
4. **Environment variables** (production source pro secrets).
5. Command line args.

Žádný Azure App Configuration v Program.cs není — pokud přidáš, zaznamenej.

**Klíčové config sekce**:
- `ConnectionStrings:DefaultConnection` — PostgreSQL.
- `JwtSettings:*` — viz §2.2.
- `OAuth:*` — viz §2.4.
- `SmtpSettings:*` — fallback SMTP (per-company se bere z `CompanySystemSettings`).
- `CorsSettings:AllowedOrigins` — array. Načteno v `Program.cs:90`.

### 9.3 BlazorUI WASM deploy

- Hostováno na **GitHub Pages** s custom doménou (`CNAME` v repu).
- API endpoint v `Fakvio.BlazorUI/wwwroot/appsettings.json` (`ApiSettings:BaseUrl`) — production URL Azure Function Appu.
- Service worker pro PWA — pozor na cache invalidation při deployi.

### 9.4 Functions deploy (Azure)

- Consumption plan, Isolated Worker.
- TimerTrigger CRONy v UTC.
- DB connection: `ConnectionStrings:DefaultConnection` z Function App settings.
- Managed Identity pro DB + Key Vault (pokud nasazeno).
- Cold start: prvních ~3-5 sec request nemá tenant context cached → mírně pomalejší.

---

## 10. Observability — logging + correlation

### 10.1 CorrelationId

`Fakvio.API/Middleware/CorrelationIdMiddleware.cs:24-97`:
- Read header `X-Correlation-Id` nebo generate GUID.
- Store v `HttpContext.Items["CorrelationId"]`.
- **`AsyncLocal` na `DatabaseLoggerProvider.CurrentCorrelationId`** — flows automatically across `await`.
- Add do response header → frontend ho může logovat.
- **Cleared v `finally`** — prevent thread reuse leak.
- **MUSÍ být první middleware** — i CORS errors / auth failures pak mají correlation ID.

### 10.2 DatabaseLogger + AppLog

- `AppLog` entity v master DB (sloupce: Timestamp, Level, Category, Message, Exception, **CorrelationId**, **CompanyId**, UserId).
- `DatabaseLogger` ILogger implementation — buffer in-memory, flush periodicky (`LogFlushService`).
- Cleanup starých záznamů (>30 dní default) přes `LogCleanupService`.
- App Insights forwarding přes default `ApplicationInsightsLoggerProvider` (parallel sink).

### 10.3 Correlation v Functions

`Fakvio.Functions/Middleware/CorrelationIdMiddleware.cs` — stejný pattern, `AsyncLocal` flows přes Function invocation.

---

## 11. Decision trees (rozhodovací stromy)

### 11.1 "Přidávám novou periodickou úlohu"

```
1. Stateless service (Fakvio.Application + Fakvio.Infrastructure)
   └─ RunCycleAsync(CancellationToken)
2. Cross-host exclusive?
   ├─ ANO → AdvisoryLock + nový long key (zaregistrovat §6.3)
   └─ NE  → bez locku (per-replica iterace OK)
3. Worker (Infrastructure/Service/{X}Worker.cs : BackgroundService)
   └─ AddHostedService<{X}Worker> v Fakvio.API DI
4. Function (Fakvio.Functions/{X}Functions.cs s [TimerTrigger])
   └─ CRON kratší než target interval, service interní throttle přes LastRunAt
5. Aktualizuj DEVGUIDE §6.3 tabulku
6. Unit test service + integration test worker
```

### 11.2 "Přidávám nový code-table"

```
1. Master nebo tenant?
   ├─ Globální (sdílená napříč firmami, např. Currency, VatRate default) → Master DB
   │  └─ Při provisioningu kopíruj do tenant (TenantProvisioningService krok 5)
   └─ Per-tenant (např. NumberSequence) → Tenant DB
2. Entity v Fakvio.Domain/Entities/
3. Konfigurace v MasterDbContext / TenantDbContext
4. Migrace (Master nebo Tenant)
5. Service IFooService + FooService
6. Pokud globální + tenant kopie: dual-context service (master/tenant auto-select dle current tenant scope)
7. Controller (s atributem ověřujícím master vs tenant scope)
8. UI service v UI.Shared/Services/
9. Lokalizace klíčů (CZ + EN .resx)
```

### 11.3 "Přidávám nový API endpoint"

```
1. Master nebo tenant scope?
   └─ Pokud master → uveď cestu do MasterOnlyPaths v TenantContextMiddleware
2. JWT Authorize?
   ├─ Public (login, password reset) → [AllowAnonymous]
   ├─ Tenant user → [Authorize] (default)
   └─ SysAdmin only → [Authorize(Roles="SysAdmin")]
3. Tenant kontext potřebný?
   └─ Inject ITenantResolver → GetCurrentCompanyId()
4. Audit?
   └─ Service ukládá entity → BaseEntity audit fields naplní DbContext SaveChanges override automaticky
5. DTO (Contracts) → ZMapper profile (Infrastructure/Mapping)
6. Frontend service v UI.Shared/Services/ (dědit ApiClientBase)
7. Test: unit (service) + integration (controller end-to-end)
```

### 11.4 "Měním algoritmus pro hesla / tokens / encryption"

```
1. ZMĚNA NESMÍ ROZBÍT EXISTUJÍCÍ DATA. Plánuj migrační cestu:
   ├─ Hesla (BCrypt) → gradual rehash on next login (verify staré → if OK → rehash + save)
   ├─ Encrypted secrets (DPAPI purpose) → NEMĚŇ purpose. Pokud musíš, dual-decrypt window
   └─ JWT signing key → rotace (přijímat oba klíče po overlap window)
2. Zvyš version v storage (např. "Fakvio.Credentials.v1" → v2)
3. UPDATE DEVGUIDE §2 — popis nového algoritmu, work factor, klíčové file:line
4. Mark deprecated old algorithm v komentáři kódu + ETA na odstranění
5. Test: unit testy verify staré hashe + nové hashe + smíšený stav
```

---

## 12. Známé gotchas (rychlý lookup)

### .NET 10 / NuGet
- `Microsoft.Extensions.Configuration.Memory` package neexistuje samostatně — `AddInMemoryCollection` je v hlavním `Microsoft.Extensions.Configuration`.
- EF Core 10.0.1 + Npgsql 10.0.0 — MSB3277 warn (Relational version mismatch), funguje.

### iText7 PDF
- `BouncyCastle` adapter NuGet `itext7.bouncy-castle-adapter` MUSÍ být referenced v projektu, kde se PDF generuje (jinak runtime fail).
- `pdfWriter.SetCloseStream(false)` — viz §4.2.

### EF + PostgreSQL
- `xmin` concurrency: `uint RowVersion` + `IsConcurrencyToken()` + `ValueGeneratedOnAddOrUpdate()`. NE `UseXminAsConcurrencyToken()`.
- `HasFilter` syntax: `"ColumnName" IS NOT NULL` (NE square brackets).

### Lokalizace
- Marker class v root namespace, ne v `.Resources` sub-namespace.
- WASM: hardcoded `cs-CZ` v Program.cs. API: `RequestLocalizationOptions` s providers v pořadí Cookie → AcceptLanguage → Default.

### MudBlazor v8
- `@bind-Visible` na MudDialog (renamed z `IsVisible`).
- `@rendermode InteractiveServer` REQUIRED pro klikací události.
- `BlazorHtmlEditor` NuGet je Monaco code editor, **NE** WYSIWYG. Používáme `Tizzani.MudBlazor.HtmlEditor` (ev. dříve `Blazored.TextEditor` / Quill).

### MAUI Hybrid
- `dotnet workload install maui` před prvním buildem, jinak SDK not found.
- WebView používá `blazor.webview.js` (ne `blazor.webassembly.js`), žádný service worker.

### Tests
- InMemoryDatabase enforcuje `IsRequired()` z fluent config — `Client.RegistrationNumber`, `Invoice.Issuer` musí být setnuty v test seedu.
- Save entities **one-by-one** s `SaveChanges()`, ne `AddRange` (deterministická ID generation).

---

## 13. Maintenance — kdy aktualizovat tento dokument

**POVINNÉ aktualizace** (PR bez nich neprochází review):

| Změna | Sekce |
|-------|-------|
| Nový/změněný algoritmus pro hesla, JWT, encryption | §2 |
| Nový OAuth provider | §2.4 |
| Nový multi-tenant pattern, změna middleware pořadí | §3 |
| Nová periodická úloha (3-pack) | §6.3 (tabulka!) |
| Nový endpoint kategorie (master-only / tenant code-table) | §3.3 + §11.3 |
| Nový email/PDF placeholder | §4.3 (typy) |
| Nový hostovací model (např. native API workflow) | §1.2 + §9.1 |
| Změna config zdroje (Key Vault, App Configuration) | §9.2 |
| Nový code-table pattern (master / tenant / dual-context) | §11.2 |
| Nový background lock klíč | §6.3 (tabulka klíčů) |
| Změna observability stacku (App Insights → jiný) | §10 |

**Volitelné** ale doporučené:
- Nový NuGet upgrade s breaking change → §12 gotchas.
- Nový MudBlazor / EF / .NET upgrade s API změnou → §7 / §5 / §12.

**Když mažeš funkcionalitu**: smaž odpovídající sekci. Ne nechávej "deprecated" odkazy bez ETA.

---

## 14. Reference — soubory, které stojí za přečtení

Nový vývojář / AI agent — pro rychlou orientaci přečti v tomto pořadí:

1. `CLAUDE.md` (root + ZCLOUD/ + InvoiceApi/) — pravidla pro AI + workflow.
2. **DEVGUIDE.md** (tento) — jak to funguje.
3. `Fakvio.API/Program.cs` — middleware pipeline, DI registrace.
4. `Fakvio.API/Middleware/TenantContextMiddleware.cs` — multi-tenant resolution.
5. `Fakvio.Infrastructure/Service/AuthService.cs` — JWT + 2FA + OAuth core.
6. `Fakvio.Infrastructure/Service/TenantProvisioningService.cs` — schema lifecycle.
7. `Fakvio.Infrastructure/Service/ImapPollService.cs` — exemplární background work pattern.
8. `Fakvio.UI.Shared/ServiceCollectionExtensions.cs` + `Services/ApiClientBase.cs` — UI registry.
9. `Fakvio.UI.Shared/Services/CustomAuthenticationStateProvider.cs` — auth na klientu.

Pro hlubší investigation použij `Explore` agenta s konkrétní otázkou — ne grep naslepo.
