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
| `Fakvio.Tests.MigrationTool` | xUnit | Testy `Fakvio.MigrationTool` proti reálnému PostgreSQL (3). Vlastní projekt kvůli izolaci procesně globálního `Npgsql.EnableLegacyTimestampBehavior`. |
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

Co chrání: SMTP hesla, OAuth secrets, AI provider API keys, Azure Blob Storage connection strings uložené v `CompanySystemSettings` / `SystemConfiguration`.

#### Azure Blob Storage — test-connection endpoint

`POST /api/system-configuration/test-blob-connection` (SysAdmin only) —
volá `ISystemConfigurationService.TestAzureBlobConnectionAsync()`, která resolvuje connection string
stejnou 3-tier logikou jako `AzureBlobFileStorage` (DB → appsettings.json), vytvoří `BlobServiceClient`
a zavolá `GetPropertiesAsync`. Vždy vrátí HTTP 200 s `{ success: bool, error: string? }` tak, aby UI
mohl zobrazit error bez řešení HTTP status kódů. Stejný endpoint je dostupný i v Azure Functions
přes `SystemConfigurationFunctions.SystemConfiguration_TestBlobConnection`.

#### Credential health check endpoint

`GET /api/system-configuration/credential-health` (SysAdmin only) —
volá `ISystemConfigurationService.CheckCredentialHealthAsync()`, která prochází všechna zašifrovaná pole
v `SystemConfiguration`, `CompanySystemSettings` (per tenant) a `PaymentMatchingSystemSettings` a testuje
každé přes `ICredentialProtector.IsHealthy()`.

Vrátí HTTP 200 s `CredentialHealthDto`:
```json
{
  "healthy": false,
  "issues": [
    { "entity": "SystemConfiguration", "field": "SmtpPassword", "companyId": null, "status": "corrupt" },
    { "entity": "CompanySystemSettings", "field": "AiClaudeApiKey", "companyId": 42, "status": "corrupt" }
  ]
}
```

Pokud `healthy = false`, SystemSettings.razor zobrazí varovný banner. Endpoint je dostupný i v Azure
Functions přes `SystemConfigurationFunctions.SystemConfiguration_GetCredentialHealth`.

#### IsHealthy detection logic

`ICredentialProtector.IsHealthy(string?)`:
- `null`/`""` → `true` (nezkonfigurováno není chyba)
- Po `Decrypt()`: pokud výsledek začíná `"CfDJ8"` (base64 prefix Data Protection payloadu), decryption selhal → `false`
- Pokud `Decrypt()` vrátí referenčně stejný string (objekt se nezměnil) → `false`
- Jinak → `true`

| Co | Kde |
|----|-----|
| Encrypt | `Fakvio.Infrastructure/Service/CredentialProtector.cs:49-56` |
| Decrypt | `Fakvio.Infrastructure/Service/CredentialProtector.cs:59-80` |
| IsHealthy | `Fakvio.Infrastructure/Service/CredentialProtector.cs:82-110` |
| Algoritmus | Data Protection API (AES-256-CBC + HMACSHA256) |
| Purpose | `"Fakvio.Credentials.v1"` — **immutable**. Změna purpose = nemožnost decrypt všech existujících záznamů. |

**Zero-downtime migrace plaintext → encrypted**: Decrypt catchne `CryptographicException` a vrátí původní hodnotu (legacy plaintext). Při dalším save se hodnota uloží už zašifrovaně. Legacy plaintext `IsHealthy` vrátí `true` (je použitelný) — na rozdíl od corrupt ciphertextu který vrátí `false` (nepoužitelný garbage).

### 2.7 Data Protection key persistence (KRITICKÉ — issue #109)

**Data Protection klíče MUSÍ být persistované do DB**, jinak po každém restartu procesu (Azure cold start, deploy, recycle) vygeneruje nový key ring a všechna dříve zašifrovaná hesla jsou **nedešifrovatelná**.

| Co | Kde |
|----|-----|
| Registrace | `ServiceCollectionExtensions.cs` — `AddDataProtection().PersistKeysToDbContext<MasterDbContext>().SetApplicationName("Fakvio")` |
| DB tabulka | `DataProtectionKeys` v master schema (migrace `AddDataProtectionKeys_v109`) |
| MasterDbContext | Implementuje `IDataProtectionKeyContext`, `DbSet<DataProtectionKey> DataProtectionKeys` |
| NuGet | `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` |

**`SetApplicationName("Fakvio")`** je povinný — bez něj API host a Functions host mají **různé** application discriminator → generují různé klíče → navzájem nedešifrují.

**Symptom při chybějící persistenci**: `CredentialProtector.Decrypt()` zachytí `CryptographicException` a vrátí raw ciphertext jako "legacy plaintext" → služby (IMAP, SMTP) dostanou garbage místo hesla → `AuthenticationException: Incorrect authentication data`.

**Po opravě** (deploy s persistencí): existující hesla zašifrovaná ephemeral klíčem jsou ztracena. Uživatel musí **znovu uložit** všechna hesla (SMTP, IMAP, AI keys) v SysAdmin panelu.

**Pravidlo**: NIKDY neodstraňuj `PersistKeysToDbContext` ani neměň `ApplicationName`. Pokud musíš změnit ApplicationName, je to ekvivalent ztráty všech zašifrovaných dat — plánuj migrační okno.

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
| `TenantDbContext` | `tenant_{companyId}` (dynamic) | `Client`, `Invoice`, `InvoiceItem`, `InvoiceTemplate`, `NumberSequence`, `Reminder`, `ReceivedInvoice`, `Notification`, `NotificationRecipient`, code tables (tenant kopie). | Scoped, **`Schema` set per-request** middlewarem. |

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
   `ExportInvoiceAsync` pro vydané faktury, `ExportReceivedInvoiceAsync` pro přijaté
   (supplier = dodavatel faktury, customer = tenant issuer `Client.IsIssuer = true`;
   mapper `IsdocMapper.Map(ReceivedInvoice, Client?)` sdílí line/tax/payment buildery
   s vydanými fakturami). Endpointy: `GET /api/invoice/{id}/isdoc`,
   `GET /api/invoice/bulk/isdoc?ids=`, `GET /api/received-invoice/{id}/isdoc`,
   `GET /api/received-invoice/bulk/isdoc?ids=` (bulk = ZIP `.isdoc` souborů,
   selhané kusy se přeskakují; každý má Functions wrapper).
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

| Hodnota | Enum | Popis | Seed ID (Master/Tenant) |
|---------|------|-------|-------------------------|
| `1` | `InvoicePdf` | PDF běžné faktury (modrý accent) | 1 |
| `2` | `CreditNotePdf` | PDF dobropisu (červený accent) | 2 |
| `3` | `ReminderPdf` | PDF upomínky (dunning) | – |
| `4` | `AdvanceInvoicePdf` | PDF zálohové faktury — není daňový doklad (zelený accent `#3D7A4A`). Obsahuje banner „ZÁLOHOVKA NENÍ DAŇOVÝ DOKLAD". | 8 |
| `5` | `TaxReceiptForAdvancePdf` | PDF daňového dokladu o přijaté platbě (fialový accent `#6A3D9A`). Daňový doklad — zakládá VAT povinnost. | 9 |
| `10` | `InvoiceEmail` | Email faktury | 3 |
| `11` | `CreditNoteEmail` | Email dobropisu | 4 |
| `12` | `AdvanceInvoiceEmail` | Email zálohové faktury (CZ: upozornění, že zálohovka není daňový doklad) | 10 |
| `13` | `TaxReceiptForAdvanceEmail` | Email daňového dokladu o přijaté platbě | 11 |
| `20` | `InvitationEmail` | Pozvánka nového uživatele | 5 |
| `21` | `ReminderEmail` | Upomínka po splatnosti | 6 |
| `22` | `PasswordResetEmail` | Reset hesla | – |
| `23` | `TwoFactorEmail` | 2FA OTP kód | 7 |

**Mapping `EDocumentType → EContentTemplateType`** — jediné místo: `PdfExportService.ResolveTemplateType()` (PDF) a `PdfExportService.ResolveEmailTemplateType()` (email). Obě metody jsou `internal static` — EmailService volá `ResolveEmailTemplateType` přímo, žádné duplicitní switche.

**Default chain** (`ContentTemplateService.cs:111-143`):
1. Exact (Type + Language + IsDefault + IsActive).
2. Any-language default (Type + IsDefault + IsActive).
3. Built-in fallback HTML: pro PDF `PdfExportService.GetDefaultHtmlTemplate(documentType)` (volá `DefaultSeedData`), pro email `EmailService.BuildFallbackEmail()`.

**Vestavěné PDF šablony** (embedded resources `Fakvio.Infrastructure/Templates/*.html`, wildcard `Templates\*.html` v csproj):
- `InvoicePdfTemplate.html` → modrý `#5B7D9D`
- `CreditNotePdfTemplate.html` → červený `#A05050`
- `AdvanceInvoicePdfTemplate.html` → zelený `#3D7A4A` + „ZÁLOHOVÁ FAKTURA — NENÍ DAŇOVÝ DOKLAD" banner
- `TaxReceiptForAdvancePdfTemplate.html` → fialový `#6A3D9A`, titulek „DAŇOVÝ DOKLAD O PŘIJATÉ PLATBĚ"

**Pokud přidáváš nový typ šablony**: 1) přidej hodnotu do `EContentTemplateType` (zachovat číselnou stabilitu), 2) přidej embedded `.html` soubor do `Templates/` (wildcard ho zahrne automaticky), 3) přidej getter do `DefaultSeedData`, 4) přidej seed záznamy s unikátním Id do `MasterDbContext` + `TenantDbContext`, 5) rozšiř switche v `ResolveTemplateType` / `ResolveEmailTemplateType` / `GetDefaultHtmlTemplate` / `BuildFallbackEmail`, 6) přidej lokalizační klíče `ContentType_XYZ` do obou resx, 7) rozšiř switche `GetTemplateTypeColor` + `GetTemplateTypeLabel` v `ContentTemplates.razor`, 8) zaznamenej zde do tabulky, **9) spusť EF Core migrace pro oba kontexty** — `dotnet ef migrations add <Name> --context MasterDbContext --output-dir Migrations/Master --project Fakvio.Infrastructure --startup-project Fakvio.API` a totéž pro `TenantDbContext / Migrations/Tenant`. Bez tohoto kroku existující DB nedostane nové šablony přes `dotnet ef database update` a `TenantProvisioningService.SeedCodeTablesFromMasterAsync` je taky neuvidí (master je prázdný).

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

**Document number generation — two-phase resolution** (`InvoiceService.GenerateDocumentNumberAsync`):

Generování čísla dokladu je záměrně rozděleno na dvě nezávislé fáze:

1. **Sequence resolution** — která čítací sekvence se použije:
   - Priority (highest wins): `overrideSequenceId` (z šablony) → `client.BillingSettings.Custom*SequenceId` → default sekvence pro `DocumentType`.
   - Výsledek: ID sekvence pro `INumberSequenceService.GenerateNextNumberAsync`.

2. **Prefix/suffix resolution** — klientský label se přidá k číslu:
   - Vždy bere z `client.BillingSettings.InvoiceNumberPrefix/Suffix` (resp. `CreditNote*`), pokud klient má `BillingSettings`.
   - **Nezávisí** na výsledku fáze 1 — prefix/suffix je per-customer concern (např. `-EU` suffix pro EU klienty), ortogonální k tomu, jaká sekvence se počítá.
   - Důsledek: i když šablona protlačí vlastní `NumberSequenceId`, klientův prefix/suffix se stále aplikuje.

Toto oddělení opravuje bug z issue #63/#72, kde se `if (overrideSequenceId.HasValue) { ... } else if (client.BillingSettings != null) { ... }` způsobilo, že šablonový override zcela ignoroval klientský prefix/suffix.

### 4.4.1 VAT regime per line item (EVatRegime + PDP, issue #43/#45)

Each `InvoiceItem` carries `VatRegime : EVatRegime` (integer column, default 0 = Standard) and
optionally `ReverseChargeCodeId` (FK to `ReverseChargeCode` lookup, nullable).

| EVatRegime | VatAmount | TotalWithVat | InformationalVatAmount | ReverseChargeCodeId |
|------------|-----------|--------------|------------------------|---------------------|
| `Standard` | `Base × Rate / 100` | `Base + VAT` | 0 | must be **null** |
| `ReverseCharge` | **0** | `Base` (no VAT billed) | `Base × Rate / 100` (shown on PDF/ISDOC for buyer self-assessment §92a ZDPH) | **required** |
| `Exempt` | 0 | `Base` | 0 | must be null |
| `OutOfScope` | 0 | `Base` | 0 | must be null |

**Invoice totals** (`InvoiceService.CreateInvoiceAsync` / `UpdateInvoiceAsync`):
- `Invoice.TotalVat` = sum of **Standard** items only — PDP does not add to the billed amount.
- `Invoice.TotalWithVat` = `TotalBeforeVat + TotalVat` (PDP items not counted in payment).

**InformationalVatAmount** is persisted in `InvoiceItem` (column `InformationalVatAmount numeric(18,2) DEFAULT 0`) but can also be computed on-the-fly from `TotalBeforeVat × VatRatePercentage / 100` during PDF/ISDOC export. It is never added to any total.

**Validation rules** (enforced in `InvoiceService.ValidateReverseChargeCodes`):
1. `VatRegime == ReverseCharge` → `ReverseChargeCodeId` must not be null.
2. `VatRegime != ReverseCharge` → `ReverseChargeCodeId` must be null.

**EPO reporting** (A.1 / B.1 in DPHKH1):
- PDP section is **TODO** — will be filled when VatReport populates A.1/B.1 from ReverseCharge items.
- `VatRegime` and `ReverseChargeCodeId` on `InvoiceItem` are the data source for that future work.

**Calculation helper**: `InvoiceService.CalculateItemVat(InvoiceItem item)` — called from both
`CreateInvoiceAsync` and `UpdateInvoiceAsync` for DRY calculation (issue #45, §9 KISS/DRY rule).

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
     - Rule 3 (fallback, oba směry): **rozpoznané protistrany** — viz §4.5.2.
     - Match → `PaymentMatch` entity, invoice `Status=Paid`.

**SysAdmin "Run now"**: `POST /api/sysadmin/payment-matching/run-now` (`PaymentMatchingSysAdminController.cs`) — volá tutéž `IImapPollService.RunCycleAsync()`.

### 4.5.2 Rozpoznané protistrany (RecognizedCounterparty)

Tenant-evidence známých účtů (pojišťovny, FÚ…) pro kategorizaci pravidelných
plateb bez faktury (sociální/zdravotní pojištění, DPH…). Migrace `Add_RecognizedCounterparty_v53`.

- **Entita** `RecognizedCounterparty`: Label, volitelný CounterpartyAccount (porovnává se přes
  `NormalizeAccount`), volitelný `CounterpartyNamePattern` (case-insensitive substring proti
  CounterpartyName I Message — platby kartou nemají účet), volitelné VS/SS/KS (null = wildcard),
  `EPaymentCategory? Category`, IsActive. Aspoň jedno z (účet, vzor) povinné (service validace,
  migrace `Add_CardPaymentRecognition_v54`). Specificita: účet +2, vzor +1, symbol +1.
- **Platby kartou**: AI prompt (`AiBankEmailParser.SystemPrompt`) extrahuje merchant →
  `counterparty_name` a `transaction_code` („Kód transakce") → `BankTransaction.TransactionCode`.
  TransactionCode je součást `ComputeTransactionHash` (InboundEmailProcessor) — bez něj by se
  dvě stejné denní platby kartou stejnému obchodníkovi slily do jedné (dedup).
- **Přiřazení** = nullable FK `BankTransaction.RecognizedCounterpartyId` (SetNull on delete)
  + `EMatchStatus.Recognized = 6`. ZÁMĚRNĚ ne přes `PaymentMatch` — ten nese `MatchedAmount`
  pro settlement faktur (CHECK constraint, PaidAmount přepočty); rozpoznání je kategorizace, ne úhrada.
- **Pravidlo** `TryRecognizeAsync` (PaymentMatchingService): fallback po pravidlech faktur
  v `MatchIncomingAsync` i `MatchOutgoingAsync` (vratka DPH chodí příchozí). Shoda = normalizovaný
  účet + každý vyplněný symbol záznamu == symbol transakce. Víc shod → vyhrává nejspecifičtější
  (počet vyplněných symbolů); remíza s různými labely → `NeedsReview`.
- **Faktura vyhrává**: `ManualMatchAsync` / `ConfirmAutoMatchAsync` nulují FK.
- **Rescan**: `IPaymentMatchingService.RescanUnmatchedAsync` — bere jen `Unmatched`;
  volá se automaticky z `RecognizedCounterpartyService.Create/Update` (aktivní záznam)
  a přes `POST /api/recognized-counterparties/rescan`. Delete/deaktivace záznamu resetuje
  jeho transakce zpět na `Unmatched`.
- **API**: `RecognizedCounterpartyController` (`api/recognized-counterparties`, CRUD + rescan),
  `PaymentMatchingController` `POST transactions/{id}/assign-recognized|unassign-recognized`.
  Functions wrappery: `RecognizedCounterpartyFunctions` + 2 v HTTP `PaymentMatchingFunctions`.
- **UI**: `RecognizedCounterpartyEditor.razor` (sekce na MyCompany, samostatný persist),
  `AssignRecognizedDialog.razor` (picker na /payments), chip + akce na Payments/PaymentDetail.

### 4.5.1 PaymentMatch lookup — proforma ↔ DPP cross-link (#31)

`IPaymentMatchingService.GetPaymentsForInvoiceAsync(long invoiceId)` vrací unified seznam plateb pro danou fakturu:

- **Přímé platby**: `PaymentMatch.InvoiceId == invoiceId`.
- **Cross-link** (Proforma ↔ DPP):
  - Pro `Proforma`: zahrne i `PaymentMatch` na všech DPP (`TaxReceiptForAdvance`) spojených přes `OriginalInvoiceId`.
  - Pro `TaxReceiptForAdvance`: zahrne i `PaymentMatch` na originating Proformě a všech jejích DPP.
  - Výsledkem je, že Proforma panel a DPP panel zobrazují **totožnou množinu plateb**.

**Proč OriginalInvoiceId a ne VariableSymbol?**
VariableSymbol je lidský string — může se duplikovat. `OriginalInvoiceId` je DB FK s garantovanou cardinalitou (1 DPP → 1 Proforma).

**Scénář 1**: Platba uložena na Proformě (starý stav před #29), ale DPP existuje → DPP panel vidí tu platbu.

**Scénář 2**: Platba uložena na DPP (nový stav po #29) → Proforma panel vidí tu platbu.

**Scénář 3**: Dvě DPP na jednu Proformu (dvě zálohy) → každý DPP panel vidí obě platby (přes společnou proformu).

Implementace: `PaymentMatchingService.cs` (metoda `GetPaymentsForInvoiceAsync`).

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
- **Chat Tools**: 11 tools registrovaných v DI jako `IChatTool`, orchestrováno přes `IChatToolExecutor`.
  Registrace v `ServiceCollectionExtensions.cs`; přidání nového toolu = implementace `IChatTool` + řádek v DI.

#### Chat AI Tools matice

| Tool | Třída | Entita | Operace | Klíčové parametry |
|------|-------|--------|---------|--------------------|
| `ares_lookup` | `AresLookupTool` | ARES (Czech registry) | Read (external API) | `registration_number` (IČO) |
| `create_client` | `CreateClientTool` | Client | Create | `registration_number` (IČO) — data z ARES |
| `create_invoice` | `CreateInvoiceTool` | Invoice (vydaná) | Create | `client_name`, `items` (JSON), `currency`, `notes` |
| `import_invoice` | `ImportInvoiceTool` | Invoice / ReceivedInvoice | Create | vydaná vs přijatá auto-detekce z IČO; `document_number`, `items`, data atd. |
| `export_invoice` | `ExportInvoiceTool` | Invoice (vydaná) | Read → Download | `document_number`, `client_name` |
| `navigate` | `NavigateTool` | — | Navigation | `target` (new\_invoice, client\_list, …), `client_name` |
| `get_received_invoice` | `GetReceivedInvoiceTool` | ReceivedInvoice | Read (detail) | `id` nebo `document_number`; vrátí položky, DPH, celkové částky, cross-check |
| `list_received_invoices` | `ListReceivedInvoicesTool` | ReceivedInvoice | Read (paged list) | `status`, `supplier_name`, `issue_date_from/to`, `min/max_amount`, `currency`, `overdue` |
| `search_received_invoices` | `SearchReceivedInvoicesTool` | ReceivedInvoice | Search | `query` (fulltext: číslo dokladu, dodavatel, VS, částka), `limit` |
| `attach_file` | `AttachFileTool` | Invoice / ReceivedInvoice / Client | Write (upload) | `entity_name`, `record_id`, `file_name`, `file_content_base64` (Base64 bytes), `content_type`, `description` |
| `list_attachments` | `ListAttachmentsTool` | Invoice / ReceivedInvoice / Client | Read (list) | `entity_name`, `record_id`; vrátí jméno, velikost, datum, popis pro každý soubor |

##### Co zatím NENÍ pokryto chat tools (jen MCP Server)
- Reminders (dunning) — přístupné přes SysAdmin UI, ne přes chat
- PaymentMatch / BankTransaction — přístupné přes SysAdmin UI
- NumberSequence, BankAccount, VatRate, Currency, ContentTemplate — read-only přes MCP server (`Fakvio.McpServer`)

### 4.8 In-app notifikace (per-user)

Notifikační systém oddělený od Alertů — alerty jsou tenant-wide s resolve lifecycle, notifikace jsou per-user s read/unread lifecycle.

**Architektura:**

| Vrstva | Kde | Co dělá |
|--------|-----|---------|
| Entity | `Fakvio.Domain/Entities/Notification.cs` + `NotificationRecipient.cs` | Event + per-user read state |
| Enum | `Fakvio.Domain/Enums/ENotificationType.cs` | `PaymentMatched = 1` (rozšiřitelný) |
| Interface | `Fakvio.Application/Service/INotificationService.cs` | Create, get, mark read |
| Implementace | `Fakvio.Infrastructure/Service/NotificationService.cs` | Cross-context (MasterDbContext pro user list, TenantDbContext pro data) |
| API | `Fakvio.API/Controller/NotificationController.cs` | 5 endpointů (viz níže) |
| Blazor client | `Fakvio.UI.Shared/Services/NotificationApiService.cs` | Dědí `ApiClientBase` |
| UI | `Fakvio.UI.Shared/Components/Notification/NotificationBell.razor` | Bell icon + dropdown popover v AppBar |
| UI stránka | `Fakvio.UI.Shared/Components/Pages/Notifications.razor` | `/notifications` — stránkovaný seznam |

**Datový model:**

- `Notification` (TenantDbContext): `Type` (int), `Title` (max 500), `Message` (max 2000), `RelatedEntityId` + `RelatedEntityType` (polymorfní odkaz na Invoice, ReceivedInvoice atd.).
- `NotificationRecipient` (TenantDbContext): `NotificationId` (FK, cascade), `UserId` (plain long — cross-context, User žije v master), `ReadAt` (null = unread).
- Indexy: `(RelatedEntityId, RelatedEntityType)`, `CreatedAt`, unique `(NotificationId, UserId)`, `(UserId, ReadAt)`.

**API endpointy** (všechny `[Authorize]`, scoped na current user):

| Endpoint | Popis |
|----------|-------|
| `GET /api/notification` | Stránkovaný seznam, filtry: `?unreadOnly=true&type=1` |
| `GET /api/notification/unread-count` | Int pro badge |
| `GET /api/notification/dashboard` | Unread count + 10 recent |
| `POST /api/notification/{id}/read` | Mark single as read (204) |
| `POST /api/notification/read-all` | Mark all as read (204) |

**CreateForAllUsersAsync flow:**
1. `ITenantResolver.GetCurrentCompanyId()` → `companyId`.
2. `MasterDbContext.Set<User>().Where(u.CompanyId == companyId && u.IsActive)` → user IDs.
3. Vytvoří 1× `Notification` + N× `NotificationRecipient`.

**Integrace:** `PaymentMatchingService` volá `CreateForAllUsersAsync` po úspěšném matchi:
- `ConfirmAutoMatchAsync` (auto match) — Invoice i ReceivedInvoice branch.
- `ManualMatchAsync` (manuální match).
- `ImapPollService` ručně instantiuje `PaymentMatchingService` — konstruktor musí dostat `INotificationService`.

**UI polling:** bell icon fetchuje unread count v `OnAfterRenderAsync` + na `NavigationManager.LocationChanged`. Žádný SignalR (v1).

**Rozšíření:** přidat nový `ENotificationType` + volání `CreateForAllUsersAsync` v příslušném servisu. Žádná schema změna.

### 4.9 MCP Server (`Fakvio.McpServer`)

- Standalone .NET tool (PackAsTool), stdio transport.
- Auth: `FAKVIO_API_TOKEN` env var (JWT bearer).
- 21 tools: 8 invoice + 6 client + 3 template + 4 reporting.
- Konfigurace v Claude Desktop / Claude Code: spustí `fakvio-mcp-server` jako subprocess se stdio piping.

### 4.10 Invoice by Email (IMAP → auto-import)

Rozšíření stávajícího IMAP pipeline (§4.5) o druhý typ aliasu — "fak-" prefix pro příjem faktur emailem.

**Sdílená infrastruktura** (beze změn): stejný IMAP server, credentials (`PaymentMatchingSystemSettings`), `InboundAliasRouter`, advisory lock, `ImapPollService` cyklus.

**Routing:** `MasterMailboxIndex.MailboxType` (enum `EMailboxType`: Payment=1, Invoice=2) rozhoduje v `ImapPollService.HandleMessageAsync` který processor se zavolá.

**Architektura:**

| Vrstva | Kde | Co dělá |
|--------|-----|---------|
| Entity (tenant) | `InvoiceMailbox.cs` | Per-tenant "fak-" alias, 1 per company |
| Entity (tenant) | `InboundInvoiceEmail.cs` | Email archiv + status + link na vytvořený doklad |
| Entity (master) | `MasterMailboxIndex.cs` | Rozšířen o `MailboxType` + `TenantInvoiceMailboxId` |
| Enum | `EMailboxType`, `EInvoiceEmailStatus`, `EInvoiceDirection` | Routing, processing status, received/issued |
| Processor | `InvoiceEmailProcessor.cs` | Orchestrátor: archiv → extract → classify → import → notify |
| Classifier | `InvoiceEmailClassifier.cs` | AI klasifikace směru (přijatá/vydaná) s IČO fast path |
| ISDOC parser | `IsdocImportParser.cs` | ISDOC 6.0.2 XML → `InvoiceExtractedData` (bez AI) |
| Mailbox CRUD | `InvoiceMailboxService.cs` | Activate/deactivate/regenerate alias |
| API | `InvoiceMailboxController.cs`, `InboundInvoiceEmailController.cs` | Mailbox management + inbox list/detail/retry/ignore |
| UI | `InvoiceMailboxCard.razor`, `InboundInvoiceEmails.razor` | Company Settings card + inbox stránka |

**Processing pipeline (`InvoiceEmailProcessor`):**

```
1. Guard (mailbox active, email within ActiveFrom window)
2. Dedup: SHA-256(mailboxId | messageId | imapUid)
3. Archive: persist InboundInvoiceEmail (status=Pending)
4. Extract attachments: PDF bytes + ISDOC XML z MimeMessage
5. Parse invoice data (priority chain):
   a. ISDOC XML → IsdocImportParser (.isdoc plain + .isdocx ZIP)
   b. PDF → InvoiceImportService pipeline (QR → AI → regex)
6. Duplicate detection: DocumentNumber + supplier IČO → existující doklad?
   → YES: přidat přílohy k existujícímu, notifikace "Příloha přidána"
   → NO: pokračovat na krok 7
7. Classify direction:
   a. IČO comparison (deterministic, confidence=1.0)
   b. AI fallback (IInvoiceEmailClassifier)
8. Auto-create client (IClientService + ARES)
9. Create doklad: ReceivedInvoice nebo Invoice
10. Attach PDF/ISDOC jako FileAttachment
11. Notify: CreateForAllUsersAsync (InvoiceEmailImported / InvoiceEmailNeedsReview)
```

**Error handling:** `GetFullExceptionMessage()` rozbalí celý InnerException chain (EF Core wrappuje skutečnou chybu za generickou zprávu). Zobrazeno v UI detail panelu.

**Retry:** `POST /api/inbound-invoice-email/{id}/retry` resetuje Failed/NeedsReview → Pending.

**Deactivace:** Retiruje master index entry → alias router přestane routovat → žádná tichá ztráta emailů.

**Klíčový DI pattern:** `ImapPollService` konstruuje `InvoiceEmailProcessor` ručně s explicitním `TenantDbContext` (ne z DI scope). DI scope nemá tenant schema nastavené — stejný pattern jako `InboundEmailProcessor` pro platby.

**Pokud přidáváš nový typ emailového zpracování:** rozšiř `EMailboxType`, přidej nový processor, a přidej branch do `ImapPollService.HandleMessageAsync`.

### 4.11 EPO XML export (DPHDP3 + DPHKH1)

Česká daňová přiznání ve formátu EPO Finanční správy ČR.

**Architektura:**

| Vrstva | Kde | Co dělá |
|--------|-----|---------|
| Interface | `Fakvio.Application/Service/IVatReportService.cs` | `ExportEpoVatReturnAsync` + `ExportEpoControlStatementAsync` |
| Implementace | `Fakvio.Infrastructure/Service/VatReportService.cs` | Agregace dokladů, DPHDP3 / DPHKH1 XML stavba, XSD validace |
| XSD schémata | `Fakvio.Infrastructure/Resources/Epo/{rok}/` | `dphdp3_epo2.xsd` a `dphkh1_epo2.xsd` — copy-to-output |
| API endpoint | `Fakvio.API/Controller/VatReportController.cs` | `GET /api/vat-report/epo/return` a `epo/control-statement` |
| EPO README | `Fakvio.Infrastructure/Resources/Epo/EPO-README.md` | Roční update postup, sandbox doc |

**DPHDP3 struktura:**
```
Pisemnost
  DPHDP3
    VetaD   — period metadata (rok, mesic/ctvrt, dapdph_forma)
    VetaP   — taxpayer (dic, c_ufo, c_pracufo, typ_ds)
    Veta1?  — output VAT rows (standard + reduced)
    Veta4?  — input VAT rows + row 51 total
```

**DPHKH1 struktura:**
```
Pisemnost
  DPHKH1
    VetaD   — period metadata (rok, mesic/ctvrt, khdph_forma)
    VetaP   — taxpayer
    VetaA4* — output invoices ≥ 10 000 CZK incl. VAT with CZ DIČ
    VetaA5? — aggregate of all other output invoices
    VetaB2* — input invoices ≥ 10 000 CZK incl. VAT with CZ DIČ
    VetaB3? — aggregate of all other input invoices
```

**EPO header settings:**
Načítány z `CompanySystemSettings` (master DB): `EpoTaxOfficeCode` (c_ufo), `EpoTaxOfficeBranchCode` (c_pracufo), `EpoContactPhone`, `EpoContactEmail`, `EpoAuthorizedPersonName`.
Chybí-li c_ufo nebo c_pracufo → `EpoHeaderIncompleteException` → HTTP 400 `EPO_HEADER_INCOMPLETE`.

**Roční update XSD:**
Viz `Fakvio.Infrastructure/Resources/Epo/EPO-README.md` — stažení z `adisspr.mfcr.cz`, pojmenování, verifikace.

**Sandbox smoke test (issue #41):**
`EpoSandboxSmokeTests` v `Fakvio.Tests.Integration` — viz EPO-README.md.
Spuštění:
```powershell
$env:RUN_EPO_SANDBOX_TESTS = "true"
dotnet test Fakvio.Tests.Integration --filter "FullyQualifiedName~EpoSandboxSmokeTests"
```
Gate: env `RUN_EPO_SANDBOX_TESTS=true`. Sandbox: `https://adisepo.mfcr.cz/adis/jepo/epo/ePodani/podani.faces`.

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

### 5.5 FileAttachment — přílohy entit + hromadné stažení

- Polymorfní vazba `EntityName` + `RecordId` (bez FK) — přílohu lze připojit
  k jakékoliv entitě bez změny kódu. Bytes v Azure Blob (`IFileStorage`),
  metadata v tenant DB.
- **Endpointy** (`FileAttachmentController`, entity-agnostic):
  - `GET /api/file-attachment/{id}/download` — jeden soubor,
  - `GET /api/file-attachment/{entityName}/{recordId}/download-all` — **vždy ZIP**
    všech příloh záznamu (404 když žádné). Klient zná `AttachmentCount`, takže
    případ jedné přílohy řeší sám přes single endpoint (zachová jméno souboru).
- **Bulk přes záznamy**: `GET /api/received-invoice/bulk/attachments?ids=...` —
  ZIP se složkou per faktura (`{DocumentNumber}/{soubor}`), cap 100 ids,
  cizí/neznámé ids tiše skip (tenant izolace). ZIP stavět přes
  `ZipArchiveHelper` (`Fakvio.Application/Common/Helpers/`) — řeší kolize jmen
  („scan (2).pdf") a sanitizaci path segmentů.
- **AttachmentCount v grid DTO — anti-N+1 pattern**: počty příloh pro stránku
  gridu načítej JEDNÍM grouped dotazem nad ids stránky a doplň do DTO při
  mapování (viz `ReceivedInvoiceService.GetPagedAsync`). Nikdy ne per-row dotaz.
- Nové endpointy vždy dostávají i **Azure Functions wrapper**
  (`Fakvio.Functions/HttpFunctions/`) — viz §6 dual-host pravidlo.

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

### 6.5 IMAP routing — header fallback chain (issue #67)

#### Proč fallback chain?

Bankovní notifikace mohou přicházet přes catch-all mailboxy, Postfix forwardy, nebo přes více MTA relay hopů. V takových případech alias v `To:` headeru neodpovídá skutečnému příjemci — skutečná adresa je v sekundárních headerech.

#### Kde žije resolver

`Fakvio.Infrastructure/Service/InboundAliasRouter.cs` — **jediný bod pravdy** pro routing logiku.

- Volán z `ImapPollService.HandleMessageAsync` (API host `ImapPollWorker`)
- Volán z `PaymentMatchingFunctions.RunImapPoll` (Azure Functions host)
- Stateless — žádný stav, DI lifetime = Singleton-equivalent (jedna instance per `ImapPollService`)

#### Pořadí headerů (first-active-match-wins)

| Pořadí | Header | Poznámka |
|--------|--------|----------|
| 1 | `Delivered-To` | Může se opakovat (iteruj všechny výskyty) |
| 2 | `X-Original-To` | Postfix catch-all: původní RCPT TO |
| 3 | `Envelope-To` / `X-Envelope-To` | Exim, někteří cloud sendři |
| 4 | `Received: … for <addr>` | Poslední Received: s "for" klíčovým slovem = nejstarší inbound hop |
| 5 | `To:` mailboxes | Všechny (ne jen první) |
| 6 | `Cc:` mailboxes | Jako poslední záchrana |

#### Chování retired aliasů

Retired alias (`MasterMailboxIndex.IsAliasRetired = true`) **nezastaví** chain — resolver pokračuje dalším kandidátem. Do `Unrouted` se jde až když **žádný** kandidát nenamatchuje aktivní alias.

#### Návratová hodnota

`AliasResolution` record: `MatchedAlias` (local-part), `MatchedHeader` (pro audit), `MasterIndexEntry` (pro tenant scope).

Při `null` výsledku resolver zaloguje všechny prošlé kandidátní adresy na úrovni `Information`.

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
| Row secondary actions | Použij `MudMenu Icon="@Icons.Material.Filled.MoreVert"` jako three-dot menu v posledním sloupci gridu pro sekundární akce (např. "Vytvořit šablonu"). Primární akce zůstávají jako `MudIconButton`. |

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

### 7.9 Grid row actions — primary vs. secondary

`TemplateColumn` actions column pattern in `MudDataGrid`:

- **Primary actions** (1–5 icons): `MudIconButton` or `MudTooltip` + `MudIconButton` inline.
  Visibility is controlled by `@if` guards on Status/DocumentType.
- **Secondary / overflow actions**: `MudMenu` with `Icon="@Icons.Material.Filled.MoreVert"` (three-dot button) placed last in the cell.
  Use this when an action is infrequent or would clutter the primary row.

Example — "Create from template" moved to the three-dot overflow menu in `Invoices.razor`:
```razor
<MudMenu Icon="@Icons.Material.Filled.MoreVert" Size="Size.Small" Color="Color.Default"
         title="@L["Label_Actions"].Value" Dense="true">
    <MudMenuItem OnClick="@(() => OpenCreateTemplateDialog(context.Item))"
                 Icon="@Icons.Material.Filled.BookmarkAdd">
        @L["Invoice_CreateFromTemplate"]
    </MudMenuItem>
</MudMenu>
```

- Copy / duplicate actions: wrap `MudIconButton` in `MudTooltip` for discoverability.
- After a mutating action (copy, restore, delete): call `await SearchInvoices()` to refresh the grid — no navigation.

### 7.10 FakvioGrid — POVINNÝ grid pattern

Každá list stránka (přehled s tabulkou dat) používá **`FakvioGrid<T>`**
(`Fakvio.UI.Shared\Components\Shared\FakvioGrid.razor`) — tenký wrapper nad
`MudDataGrid`, který vlastní standardní chování: Dense/Hover/Striped,
`ColumnFilterRow` filtrování, `SortMode.Single`, resize + drag-drop reordering,
hideable sloupce, persistence viditelnosti sloupců (`GridStatePersistence`,
klíč `GridKey`), standardní pager a lokalizovaný "no records".

```razor
<FakvioGrid T="InvoiceDto" GridKey="invoices" ServerData="LoadServerData" @ref="_grid">
    <Columns>
        <PropertyColumn Property="x => x.DocumentNumber" Title="..." />
    </Columns>
</FakvioGrid>
```

Pravidla:
- **Kdy použít:** všechny list stránky — server-side (`ServerData=`) i client-side (`Items=`).
  `ServerData` a `Items` jsou vzájemně výlučné (wrapper vyhodí výjimku).
- **Kdy NE:** malé embedded seznamy na detail stránkách zůstávají `MudTable`
  (žádný paging/sort/filter/persistence → wrapper nic nepřináší).
- **`GridKey`** = kebab-case název route (`"received-invoices"`, `"invoice-templates"`).
  Embedded gridy: prefix stránky (`"client-detail-invoices"`).
- **Reload po akci:** `await _grid.ReloadAsync()` (místo `_dataGrid.ReloadServerData()`).
- **Escape hatch:** `_grid.Grid` vrací vnitřní `MudDataGrid<T>`.
- **Page size:** výchozí počet řádků čte FakvioGrid z uživatelské preference
  (`UserPreferencesState`, jednorázově cacheované GET `/api/user-preferences`;
  entita `UserPreferences` v master DB, 1:1 k User). Stránka může přebít
  parametrem `RowsPerPage`. Nové preference přidávej do `UserPreferencesDto` +
  `UserPreferencesService` (validace) + stránky `/preferences`.
- **Client-side gridy (`Items=`):** řazení i filtrování fungují nativně zdarma —
  NEvypínej `Sortable` na PropertyColumn bez důvodu (TemplateColumn bez
  `SortBy` sortable být nemůže, tam je `Sortable="false"` správně).

**Konvence propagace column filtrů (server-side):** v `LoadServerData` JEDEN řádek
per filtrovatelný sloupec — jinak filter řádek v UI existuje, ale nic nedělá:

```csharp
var name   = state.GetStringFilter("Name");                       // string sloupce
var status = state.GetEnumFilter<RowDto, EStatus>("Status");      // enum sloupce
var active = state.GetBoolFilter("IsActive");                     // bool sloupce
var (from, to) = state.GetDateRange("IssueDate");                 // DateColumnFilter
```

Helpers: `Models\DateGridFilterExtensions.cs` (string + date),
`Models\GridFilterExtensions.cs` (enum + bool). Každý takto čtený filtr musí mít
odpovídající parametr ve filter DTO / API service a `Where` v backend service —
a **sloupec uvedený v backend `validSortFields`**, pokud je sortable
(jinak sort tiše spadne na default).

### 7.11 Responsive / mobile konvence

RCL hostují web (WASM) i MAUI hybrid → responsivita v `Fakvio.UI.Shared` platí
pro obě. Breakpoint = MudBlazor **xs < 600px** (media query `max-width:599.98px`
v `wwwroot\css\app.css`, sekce „Responsive / mobile").

| Prvek | Pravidlo |
|---|---|
| Primární akce v toolbaru/filter baru | **POVINNĚ `ResponsiveButton`** (`Components\Shared\ResponsiveButton.razor`) — na mobilu jen ikona ("+"), label = tooltip. NE holý `MudButton` s textem. |
| Řádky s více tlačítky | vždy `d-flex flex-wrap gap-2` |
| Cokoliv postradatelného | utility třída `.hide-xs` |
| Grid sloupce | sekundární sloupce (datumy, kódy, města) označ `HideSmall="true"` — MudBlazor razítkuje `mud-table-cell-hide` na header/filter/body buňky, app.css je pod 600px skryje. Ponech identifikaci + částku + stav + akce. Nebojuje s persistencí ani column menu (čisté CSS). |
| Dialogy | pod 600px automaticky fullscreen (CSS). Opt-out: `dialog-keep-size`. |
| AppBar | title text, jméno uživatele, dark-mode a logout ikona se na xs skrývají (logout je v profil menu); tříd `appbar-*` se nedotýkej bez přeměření na 375px |

**Ověření:** Playwright `Tests\Navigation\MobileLayoutTests.cs` (viewport 375×812 —
overflow, ikonová tlačítka, AppBar, skryté sloupce). Při změně layoutu je pusť
proti běžícímu stacku.

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

#### 8.2.2 Testy migračního nástroje (`Fakvio.Tests.MigrationTool`)

Samostatný projekt, protože na `Fakvio.MigrationTool` do issue #136 neměl `ProjectReference`
žádný testovací projekt — jeho kompozici (skládání jména tenant schématu, `search_path`)
tedy nešlo připnout. Vzor: `TenantSchemaCanonicalizationTests`.

- Stejný throwaway-schema pattern jako `TenantIssuerProvisioningDatabaseTests`, jen se **třemi**
  schématy na instanci třídy (source / master / tenant). Úklid maže vše, co má v názvu GUID běhu.
- Test žene celý `DataMigrationService.MigrateAsync()`, ne jednotlivé helpery — jinak by se
  kompozice minula stejně jako unit testy nad `SchemaNames.Sanitize`.
- **Nereferencuje `Fakvio.API`** záměrně. `Npgsql.EnableLegacyTimestampBehavior` je procesně
  globální `AppContext` přepínač, který Npgsql přečte jednou a zmrazí. V assembly, kde se bootuje
  API host, ho přepne ten test, co běžel dřív (to je podstata #194). Tady ho nastavuje výhradně
  fixture, takže výsledek nezávisí na pořadí testů.
- Přepínač je tu vypnutý, tedy **jinak než ve `Fakvio.MigrationTool/Program.cs`**. Zapnutý mapuje
  `DateTime` na `timestamp without time zone`, zatímco snapshoty migrací mají `timestamp with time
  zone` — model pak nesedí se snapshotem a `MigrateAsync()` spadne na `PendingModelChangesWarning`.
  Produkční cesty to tlumí přes `ConfigureWarnings` (viz `ServiceCollectionExtensions`,
  `TenantProvisioningService`), kontexty v MigrationToolu ne.

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
| Kompozice v `Fakvio.MigrationTool` (jména schémat, `search_path`) | `Fakvio.Tests.MigrationTool` proti reálnému PostgreSQL (§8.2.2) |
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

### 10.4 Client-side logging (WASM → AppLog)

Chyby vzniklé v Blazor WASM klientovi by jinak skončily jen v browser konzoli. Pipeline:

- **`ClientLoggerService : IClientLogger`** (`UI.Shared/Services/ClientLoggerService.cs`) —
  POST `/api/logs/client` (endpoint `AppLogController.LogFromClient`, `[AllowAnonymous]`,
  defenzivní — nikdy nevrací chybu). Fire-and-forget, nikdy nehází, žádná rekurze.
- **`ApiClientBase`** — všechny HTTP chyby (4xx = Warning, 5xx = Error) i transport výjimky
  automaticky forwarduje přes `ForwardToServerLog`. Wiring dělá `AddApiClient<T>` v
  `ServiceCollectionExtensions` — **novou UI service vždy registruj přes `AddApiClient<T>`,
  ne `AddScoped<T>`**, jinak se forwarding nezapojí.
- **`AuthApiService`** — dědí `ApiClientBase`, ale staví vlastní requesty (X-Captcha-Token).
  Chyby forwarduje ručně přes protected `ForwardToServerLog`/`LogClientException`.
  Nikdy neloguje request body (obsahuje hesla).
- **`LoggingErrorBoundary`** (MainLayout) — neodchycené render výjimky → AppLog.
- **`IUiErrorHandler`** (`UI.Shared/Services/UiErrorHandler.cs`) — centrální handler pro
  catch bloky ve stránkách: `catch (Exception ex) { ErrorHandler.Handle(ex, "Page.Action"); }`
  Zobrazí snackbar + forwarduje NE-`ApiException` chyby (ApiException už zalogoval
  `ApiClientBase` — nedupluj). **Nové catch bloky v UI piš přes `IUiErrorHandler`**,
  existující `Snackbar.Add` catch bloky konvertuj průběžně při úpravách dané stránky.

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

### Data Protection
- **Key persistence je POVINNÁ** — `PersistKeysToDbContext<MasterDbContext>()`. Bez ní každý restart = nový key ring = všechna zašifrovaná hesla ztracena (issue #109). Viz §2.7.
- `SetApplicationName("Fakvio")` — API i Functions MUSÍ sdílet stejný název, jinak navzájem nedešifrují.
- Po ztrátě key ringu uživatel musí znovu uložit VŠECHNA hesla (SMTP, IMAP, AI keys).

### EF Core Identity Map caching
- `FindAsync` bez `Include` cachuje entitu v identity mapu. Pozdější query s `Include` vrátí cached instanci s `null` navigation properties (issue #104). Fix: `AsNoTracking()` na read-only queries, nebo explicitní `Include` na `FindAsync`.

### Non-idempotent seed migrace
- `migrationBuilder.InsertData` s hardcoded `Id` selže na `PK duplicate` pokud data už existují (issue #97/#109). Vždy použít raw SQL `INSERT ... ON CONFLICT (Id) DO NOTHING` nebo `DELETE + InsertData` guard.

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
| Změna Data Protection persistence / ApplicationName | §2.7 |
| Nová seed migrace s hardcoded Id | §12 (non-idempotent seed) |
| Nový code-table pattern (master / tenant / dual-context) | §11.2 |
| Nový background lock klíč | §6.3 (tabulka klíčů) |
| Změna observability stacku (App Insights → jiný) | §10 |
| Nová list stránka s gridem / změna grid patternu | §7.10 (FakvioGrid) |
| Nová/změněná funkce **viditelná uživateli** (stránka, akce, stav, export) | **USERGUIDE.md** |
| Nová/změněná funkce **viditelná SysAdminovi** (nastavení, provider, log, provisioning) | **ADMINGUIDE.md** |

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
