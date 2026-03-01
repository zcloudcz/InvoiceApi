# Blazor Server → WebAssembly Migration Analysis

> **Project**: Fakvio.BlazorUI
> **Current mode**: Blazor Server (`InteractiveServerRenderMode`, prerender: false)
> **Target**: Blazor WebAssembly (standalone) or Auto (hybrid)
> **Date**: 2026-02-12

---

## 1. Current Architecture Overview

```
Browser ←── SignalR WebSocket ──→ BlazorUI (Server)
                                      │
                                      │ HttpClient (https://localhost:7047)
                                      ▼
                                 Fakvio.API
                                      │
                                      ▼
                               SQL Server (multi-tenant)
```

**Key facts:**
- 47 `.razor` files (31 interactive pages, 9 shared components, layout, etc.)
- 16 typed API service classes inheriting `ApiClientBase`
- All data access via HTTP/JSON to the separate API server — **no DbContext in UI**
- MudBlazor v8.15.0 + Blazored.TextEditor v1.1.3 (Quill)
- JWT auth stored in `ProtectedSessionStorage` (server-side encrypted browser storage)
- Localization: `IStringLocalizer` with `.resx` files (CZ/EN), cookie-based culture switching

---

## 2. Dependency Chain Analysis

```
Fakvio.BlazorUI
├── MudBlazor 8.15.0              ✅ WASM-compatible
├── Blazored.TextEditor 1.1.3    ✅ WASM-compatible (JS interop / Quill)
└── Fakvio.Application        ❌ BLOCKER — contains EF Core 10.0.1
    ├── Microsoft.EntityFrameworkCore 10.0.1  ❌ cannot run in browser
    ├── Microsoft.Extensions.Logging.Abstractions 10.0.1  ✅
    ├── Fakvio.Domain          ✅ pure .NET (no packages)
    └── AresService                ✅ (logging abstractions only)
```

### What BlazorUI actually uses from Application:

| Category | Count | WASM-safe? |
|----------|-------|------------|
| DTOs (`Dto.*`) | 54 files | ✅ Pure POCOs |
| Pagination (`PagedResult<T>`, `PaginationParams`) | 2 files | ✅ |
| Enums (from `Domain.Enums`) | 11 enums | ✅ |
| Service interfaces (`IAuthService`, etc.) | 0 references | N/A — not used |
| `QueryableExtensions` | 0 references | N/A — not used |

**BlazorUI never calls server-side service interfaces.** It only consumes DTOs and enums for HTTP serialization/deserialization. The EF Core dependency is transitive and unnecessary.

---

## 3. Migration Blockers

### 🔴 BLOCKER 1: Application Project Reference (EF Core)

**Problem**: `Fakvio.Application.csproj` has `Microsoft.EntityFrameworkCore 10.0.1`. WASM cannot load EF Core assemblies — they depend on native SQL drivers.

**Solution**: Create a new **shared contracts library** and extract what BlazorUI needs:

```
Fakvio.Contracts/  (NEW — WASM-safe, zero package dependencies)
├── Dto/               (move all 54 DTO files from Application/Dto/)
│   ├── AppLog/
│   ├── Auth/
│   ├── Client/
│   ├── CloudStorage/
│   ├── CompanySettings/
│   ├── ContentTemplate/
│   ├── Currency/
│   ├── Dashboard/
│   ├── Email/
│   ├── Invoice/
│   ├── InvoiceTemplate/
│   ├── NumberSequence/
│   ├── SystemConfiguration/
│   ├── TwoFactor/
│   ├── User/
│   └── VatRate/
└── Common/
    └── Pagination/
        ├── PagedResult.cs
        └── PaginationParams.cs
```

**New reference graph:**
```
BlazorUI.Wasm  → Fakvio.Contracts  (DTOs + Pagination)
                → Fakvio.Domain     (Enums only — no packages)

Application    → Fakvio.Contracts  (replaces internal Dto/)
               → Fakvio.Domain
               → EF Core (stays server-only)

API            → Application → Contracts + Domain  (unchanged)
```

**Effort**: ~2–3 days. Mechanical move of 56 files + update `using` statements + fix project references.

---

### 🔴 BLOCKER 2: ProtectedSessionStorage (Server-only)

**Problem**: `CustomAuthenticationStateProvider` uses `ProtectedSessionStorage` from `Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage`. This API encrypts data using server-side Data Protection keys. It does not exist in WASM.

**Stored keys:**
- `UserSession` — JSON with JWT token, user ID, email, role, company
- `ImpersonatedCompanyId` — SysAdmin impersonation context
- `ImpersonatedCompanyName` — display name

**Solution**: Replace with `Blazored.LocalStorage` (NuGet, WASM-compatible):

```csharp
// BEFORE (Server-only):
private readonly ProtectedSessionStorage _sessionStorage;
var result = await _sessionStorage.GetAsync<string>("UserSession");
var json = result.Success ? result.Value : null;

// AFTER (WASM-compatible):
private readonly ILocalStorageService _localStorage;
var json = await _localStorage.GetItemAsStringAsync("UserSession");
```

**Security implications**:
- `ProtectedSessionStorage` encrypts data server-side → session-bound, tamper-proof
- `localStorage` is plaintext in the browser → accessible to any JS on the page (XSS risk)
- **Mitigation**: JWT tokens have limited lifetime (configured in API), refresh tokens for rotation, Content-Security-Policy headers to block inline scripts

**Effort**: ~1–2 days. Rewrite `CustomAuthenticationStateProvider` (~150 lines) + install NuGet.

---

### 🔴 BLOCKER 3: Server-Side Culture Switching

**Problem**: Culture is set via a minimal API endpoint that writes a cookie and redirects:

```csharp
// Program.cs — server-side endpoint
app.MapGet("/Culture/SetCulture", (HttpContext context, string culture, string redirectUri) =>
{
    context.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName, ...);
    return Results.LocalRedirect(redirectUri);
});
```

WASM has no `HttpContext` and cannot set server-side cookies.

**Solution**: Client-side culture switching via JS interop + localStorage:

```csharp
// WASM approach:
await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "culture", "cs-CZ");
var culture = new CultureInfo(selectedCulture);
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;
// Reload page to apply
_navigationManager.NavigateTo(_navigationManager.Uri, forceLoad: true);
```

The `LanguageSwitcher.razor` component navigates to the server endpoint — it must be rewritten to use client-side storage.

**Effort**: ~0.5 days.

---

### 🟡 MEDIUM: Render Mode Changes (31 pages)

Every interactive page has:
```razor
@rendermode InteractiveServer
```

All 31 pages need to change to:
```razor
@rendermode InteractiveWebAssembly   // for pure WASM
// OR
@rendermode InteractiveAuto          // for hybrid (server-first, then WASM)
```

`App.razor` root also needs updating:
```razor
<!-- BEFORE -->
<Routes @rendermode="new InteractiveServerRenderMode(prerender: false)" />
<ReconnectModal />
<script src="@Assets["_framework/blazor.web.js"]"></script>

<!-- AFTER (WASM) -->
<Routes @rendermode="new InteractiveWebAssemblyRenderMode(prerender: false)" />
<!-- Remove ReconnectModal — no SignalR in WASM -->
<script src="_framework/blazor.webassembly.js"></script>
```

**Effort**: ~0.5 days (search-and-replace + manual review).

---

### 🟡 MEDIUM: Program.cs Restructuring

**Server-side concepts that don't exist in WASM:**
- `WebApplication.CreateBuilder()` → `WebAssemblyHostBuilder.CreateDefault()`
- `app.UseRequestLocalization()` → client-side culture
- `app.UseAntiforgery()` → remove (WASM doesn't use server forms)
- `app.UseStatusCodePagesWithReExecute()` → client-side routing
- `app.MapStaticAssets()` → served by static host
- `app.MapRazorComponents<App>()` → `builder.RootComponents.Add<App>("#app")`
- Cookie authentication → JWT-only (already the case for API calls)

**WASM Program.cs** (conceptual):
```csharp
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

// HttpClient pointing to the API
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.Configuration["ApiSettings:BaseUrl"]
                          ?? "https://localhost:7047")
});

// Or named client via IHttpClientFactory
builder.Services.AddHttpClient("InvoiceAPI", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiSettings:BaseUrl"]
                                 ?? "https://localhost:7047");
});

// Auth
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();
builder.Services.AddBlazoredLocalStorage();

// MudBlazor
builder.Services.AddMudServices();

// Localization
builder.Services.AddLocalization();

// All 16 API services — unchanged
builder.Services.AddScoped<AuthApiService>();
builder.Services.AddScoped<VatRateApiService>();
// ... etc.

await builder.Build().RunAsync();
```

**Effort**: ~1 day.

---

### 🟢 LOW: Antiforgery Removal

`app.UseAntiforgery()` is server-side only. WASM uses JWT for all API calls. Remove the call.

---

### 🟢 LOW: ReconnectModal Removal

`<ReconnectModal />` handles SignalR disconnects. WASM doesn't use SignalR. Remove the component.

---

## 4. What Already Works in WASM (No Changes Needed)

| Feature | Reason |
|---------|--------|
| All 16 API services (`ApiClientBase` + children) | Use `HttpClient` — fully WASM-compatible |
| MudBlazor v8.15.0 | Officially supports WASM |
| Blazored.TextEditor v1.1.3 (Quill) | Pure JS interop — WASM-compatible |
| `IStringLocalizer` localization | .resx works in WASM |
| `IJSRuntime` calls (`confirm`, `downloadFile`) | Standard browser APIs |
| `NavigationManager` | Works identically in WASM |
| `IDialogService` (MudBlazor dialogs) | No server dependency |
| `<NavigationLock>` unsaved changes guard | Works in WASM |
| All shared components (EnumSelect, BankAccountInput, etc.) | No server dependencies |
| Generic `@typeparam` components with `Value`/`ValueChanged` | Pattern is render-mode agnostic |

---

## 5. Migration Strategies

### Option A: Standalone WASM (Recommended)

```
     ┌──────────────┐
     │   Browser     │
     │  Blazor WASM  │  ← .NET runtime + app DLLs (~12–18 MB initial, cached)
     │   (SPA)       │
     └──────┬───────┘
            │ HTTPS / JSON
            ▼
     ┌──────────────┐
     │  API Server   │  ← Fakvio.API (or Azure Functions)
     │  (or Azure    │
     │   Functions)  │
     └──────┬───────┘
            │
            ▼
     ┌──────────────┐
     │  SQL Server   │
     └──────────────┘
```

**Pros:**
- True SPA — no SignalR connection, no server circuit
- Can be hosted on Azure Static Web Apps / CDN / Blob Storage (cheapest)
- Scales infinitely (static files + API)
- Offline-capable with service workers (PWA)
- No per-connection server memory (Blazor Server uses ~250 KB per circuit)

**Cons:**
- Initial download ~12–18 MB (cached after first load)
- Cold start: .NET runtime initialization in browser (~2–4s on modern hardware)
- JWT in localStorage is XSS-vulnerable (mitigate with short expiry + CSP)
- No server-side pre-rendering (SEO irrelevant for this admin SaaS)
- Debugging is harder (browser DevTools + Source Maps)

**Deployment:**
```
Azure Static Web Apps (free tier)  ← WASM app
Azure Functions (consumption plan) ← API (already implemented)
Azure SQL                          ← Database
```

**Cost**: Near-zero for WASM hosting. API runs on consumption plan (pay-per-execution).

---

### Option B: Auto Mode (Hybrid — Server first, then WASM)

```
First visit:   Server renders via SignalR (instant)
Background:    WASM assets download in parallel
Next visit:    WASM runs client-side (no SignalR)
```

**Pros:**
- Instant first render (no WASM download wait)
- Subsequent visits are pure WASM (fast, no server)
- Best perceived performance

**Cons:**
- Two hosting models simultaneously → more complex deployment
- Must ensure ALL components work in BOTH Server and WASM modes
- `ProtectedSessionStorage` still doesn't work in WASM phase → must use `localStorage` everywhere
- Requires ASP.NET Core server for the Server rendering phase (not static hosting)
- More difficult to debug (which mode is active?)

**Deployment:**
```
Azure App Service (or Functions)  ← Hosts BOTH server rendering + API
Azure SQL                         ← Database
```

---

### Option C: Stay on Blazor Server (No Migration)

**Pros:**
- Zero effort — works today
- `ProtectedSessionStorage` encryption
- Thin client (no .NET download)
- Easy debugging (server-side breakpoints)

**Cons:**
- SignalR overhead (~250 KB per connection + latency)
- Cannot run without constant server connection
- Server costs scale with concurrent users
- No offline capability
- Higher Azure costs (always-on App Service required)

---

### Recommendation

| Criterion | Server | WASM | Auto |
|-----------|--------|------|------|
| Initial load speed | ⭐⭐⭐ | ⭐ | ⭐⭐⭐ |
| Subsequent load speed | ⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ |
| Hosting cost | ⭐ | ⭐⭐⭐ | ⭐⭐ |
| Scalability | ⭐ | ⭐⭐⭐ | ⭐⭐ |
| Offline capability | ❌ | ✅ | ✅ |
| Migration effort | ⭐⭐⭐ | ⭐⭐ | ⭐ |
| Security (token storage) | ⭐⭐⭐ | ⭐⭐ | ⭐⭐ |
| Debugging ease | ⭐⭐⭐ | ⭐ | ⭐ |

**For Fakvio (enterprise SaaS, always-online users, admin panel):**
- **Option A (Standalone WASM)** is recommended if the primary goal is **cost reduction** (Azure Static Web Apps + Functions consumption plan) or **scalability**.
- **Option C (Stay Server)** is recommended if there's **no strong driver** to change.
- **Option B (Auto)** adds complexity without significant benefit for this use case.

---

## 6. Implementation Plan (Option A — Standalone WASM)

### Phase 1: Create Shared Contracts Library (2–3 days)

1. Create `Fakvio.Contracts` project (class library, `net10.0`, zero packages)
2. Move all 54 DTOs from `Application/Dto/` → `Contracts/Dto/`
3. Move `PagedResult<T>` + `PaginationParams` → `Contracts/Common/Pagination/`
4. Update `Application.csproj` → reference `Contracts` (DTOs are now external)
5. Update `BlazorUI.csproj` → replace `Application` reference with `Contracts` + `Domain`
6. Fix all `using` statements across both projects
7. Build entire solution, run all 312 tests

**Risk**: Mechanical but tedious. High file count but no logic changes.

### Phase 2: Create WASM Host Project (1 day)

1. Create `Fakvio.BlazorUI.Wasm` project (or convert existing)
   - `Microsoft.NET.Sdk.BlazorWebAssembly`
   - References: `Contracts`, `Domain`
   - NuGet: `MudBlazor`, `Blazored.TextEditor`, `Blazored.LocalStorage`
2. Write `Program.cs` with `WebAssemblyHostBuilder`
3. Configure `HttpClient` with API base URL from `wwwroot/appsettings.json`
4. Register all 16 API services (copy from current `Program.cs`)
5. Create `wwwroot/appsettings.json` for runtime configuration

### Phase 3: Rewrite Authentication (1–2 days)

1. Install `Blazored.LocalStorage` NuGet
2. Rewrite `CustomAuthenticationStateProvider`:
   - Replace `ProtectedSessionStorage` → `ILocalStorageService`
   - Replace `GetAsync<T>()` → `GetItemAsStringAsync()`
   - Replace `SetAsync()` → `SetItemAsStringAsync()`
   - Replace `DeleteAsync()` → `RemoveItemAsync()`
3. Update `AuthApiService` if it references session storage
4. Test login → store JWT → API calls → logout flow

### Phase 4: Rewrite Culture Switching (0.5 days)

1. Remove `/Culture/SetCulture` minimal API endpoint
2. Rewrite `LanguageSwitcher.razor`:
   - Read/write culture to `localStorage` via JS interop
   - Set `CultureInfo.DefaultThreadCurrentCulture` in WASM
   - Force page reload to apply
3. Add culture detection on startup (read from localStorage or browser `navigator.language`)

### Phase 5: Update Render Modes & App Shell (0.5 days)

1. Change all 31 pages: `@rendermode InteractiveServer` → remove (WASM is implicit)
2. Update `App.razor`:
   - Remove `InteractiveServerRenderMode`
   - Remove `<ReconnectModal />`
   - Update script reference to `blazor.webassembly.js`
3. Update `index.html` (WASM uses `index.html`, not `App.razor` as host)

### Phase 6: Move Services & Components (1 day)

1. Copy all 16 API service classes to WASM project (or keep in shared library)
2. Copy all `.razor` components (pages + shared)
3. Copy all `.resx` localization files
4. Copy `wwwroot/` static assets (CSS, JS, favicon)
5. Verify `_Imports.razor` has correct `@using` statements

### Phase 7: CORS Configuration (0.5 days)

WASM runs in the browser at a different origin than the API. CORS must allow:
- Origin: `https://your-wasm-app.azurestaticapps.net` (or localhost for dev)
- Headers: `Authorization`, `Content-Type`, `X-Company-Id`
- Methods: GET, POST, PUT, DELETE, PATCH
- Credentials: true (if using cookies for refresh tokens)

The API already has CORS configured in `Program.cs` — just add the WASM origin to `CorsSettings:AllowedOrigins` in `appsettings.json`.

### Phase 8: Testing (3–5 days)

| Test area | What to verify |
|-----------|---------------|
| Authentication | Login, JWT storage, auto-logout on expiry, SysAdmin impersonation |
| All 31 pages | Render correctly, data loads, CRUD operations work |
| File downloads | PDF export via `downloadFile` JS interop |
| Quill editor | Blazored.TextEditor loads and saves HTML in content templates |
| Dialogs | MudBlazor dialogs (confirm, address editor, folder picker) |
| Localization | CZ/EN switching, all ~450 resource keys display correctly |
| Navigation guard | Unsaved changes warning before leaving pages |
| Error handling | API errors display correctly, 401/403 redirect to login |
| Performance | Initial load time, subsequent navigation, large table rendering |
| Browser compat | Chrome, Firefox, Edge (Safari if needed) |

---

## 7. Download Size Estimation

### WASM Initial Download (first visit, uncached)

| Component | Estimated Size |
|-----------|---------------|
| .NET runtime (`dotnet.native.wasm`) | ~4 MB (Brotli compressed) |
| BCL assemblies (System.*, Microsoft.*) | ~3 MB |
| MudBlazor | ~1.5 MB |
| Application assemblies (Contracts, Domain, Services) | ~0.5 MB |
| Quill.js + Blazored.TextEditor | ~0.5 MB |
| Other (localization, config, static assets) | ~0.5 MB |
| **Total** | **~10–12 MB** |

### Mitigation strategies:
- **Brotli compression**: Reduces download by ~60% (built-in for .NET WASM publish)
- **IL trimming**: `<PublishTrimmed>true</PublishTrimmed>` removes unused code
- **AOT compilation**: `<RunAOTCompilation>true</RunAOTCompilation>` increases size but speeds up execution
- **Lazy loading**: Load assemblies on-demand per page (advanced, not recommended initially)
- **Caching**: After first load, browser caches all assets. Subsequent visits are instant.

### Realistic user experience:
- **First visit**: 3–6 seconds loading (depends on connection speed)
- **Subsequent visits**: <1 second (cached assets, only delta downloads on updates)
- **In-app navigation**: Instant (SPA, no server round-trips)

---

## 8. Security Considerations

### JWT Token Storage in WASM

| Aspect | Server (current) | WASM (proposed) |
|--------|------------------|-----------------|
| Storage | `ProtectedSessionStorage` | `localStorage` |
| Encryption | Yes (Data Protection API) | No (plaintext) |
| XSS vulnerability | Low (encrypted) | Medium (readable by JS) |
| CSRF vulnerability | N/A (JWT in header) | N/A (JWT in header) |
| Session scope | Tab only (session storage) | All tabs (local storage) |

### Mitigations for WASM:
1. **Short JWT expiry** (15–30 min) with refresh token rotation
2. **Content-Security-Policy** headers to block inline scripts
3. **HttpOnly refresh token cookie** (set by API, not accessible to JS)
4. **Subresource Integrity** (SRI) for CDN scripts (Quill.js)
5. **Consider `sessionStorage`** instead of `localStorage` for tab-scoped sessions

### Recommended auth flow for WASM:
```
Login → API returns { accessToken (15 min), refreshToken (HttpOnly cookie) }
       → accessToken stored in memory (not localStorage!) or sessionStorage
       → refreshToken in HttpOnly cookie (XSS-proof)
       → On 401 → call /api/auth/refresh → get new accessToken
       → On refresh token expiry → redirect to /login
```

---

## 9. Effort Summary

| Phase | Task | Days |
|-------|------|------|
| 1 | Create `Fakvio.Contracts` (extract 56 files) | 2–3 |
| 2 | Create WASM host project + Program.cs | 1 |
| 3 | Rewrite authentication (ProtectedSessionStorage → localStorage) | 1–2 |
| 4 | Rewrite culture switching | 0.5 |
| 5 | Update render modes & app shell | 0.5 |
| 6 | Move services & components | 1 |
| 7 | CORS configuration | 0.5 |
| 8 | Testing (all 31 pages + edge cases) | 3–5 |
| | **Total** | **10–14 days** |

---

## 10. Decision Matrix

| Question | If YES → | If NO → |
|----------|----------|---------|
| Need cheapest Azure hosting? | WASM (Static Web Apps = free) | Server is fine |
| Need offline capability? | WASM (PWA) | Server is fine |
| Expect 100+ concurrent users? | WASM (no SignalR overhead) | Server handles <100 easily |
| Is initial load time critical? | Stay Server (instant) or Auto | WASM has 3–6s first load |
| Need SSR/SEO? | Stay Server or Auto | WASM (admin panel, no SEO needed) |
| Team familiar with WASM debugging? | WASM | Stay Server until trained |

---

## 11. Files That Change vs. Don't Change

### Changed (WASM migration):
- `BlazorUI.csproj` → new SDK, new references
- `Program.cs` → complete rewrite for `WebAssemblyHostBuilder`
- `App.razor` → remove Server-specific markup
- `CustomAuthenticationStateProvider.cs` → localStorage
- `LanguageSwitcher.razor` → client-side culture
- All 31 pages → remove `@rendermode InteractiveServer`

### Created (new):
- `Fakvio.Contracts/` → shared DTO library (56 files moved)
- `wwwroot/index.html` → WASM host page
- `wwwroot/appsettings.json` → client-side config (API URL)

### Unchanged:
- All 16 API service classes (`ApiClientBase` + children) — fully compatible
- All 9 shared components — no server dependencies
- All `.resx` localization files
- All JS interop (`download.js`) — browser API only
- Fakvio.API — unchanged
- Fakvio.Infrastructure — unchanged
- Fakvio.Domain — unchanged
- Fakvio.Functions — unchanged
- All 312 tests — unchanged

---

## 12. Conclusion

The Fakvio.BlazorUI is **well-architected for WASM migration** (70% compatible today):

✅ **Already WASM-ready**: API services, MudBlazor, Quill editor, localization, JS interop, shared components, navigation
❌ **Must change**: DTO project extraction, authentication storage, culture switching, render mode declarations

**The single biggest effort is Phase 1** (extracting DTOs to a shared library) — it's mechanical but touches many files. Everything else is localized to 3–5 files.

**Recommended approach**: If cost/scalability is a driver, proceed with **Option A (Standalone WASM)**. The architecture already separates concerns cleanly — the migration is straightforward, just tedious.
