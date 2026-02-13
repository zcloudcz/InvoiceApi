# Mobile App Analysis — InvoiceApi BlazorUI → Mobile

> **Date:** 2026-02-13
> **Current state:** Blazor Server (.NET 10) + MudBlazor v8.15.0 + 18 API services + 47 pages + 9 shared components
> **Goal:** Convert existing BlazorUI project into a mobile application (iOS + Android)

---

## Table of Contents

1. [Current Architecture Summary](#1-current-architecture-summary)
2. [Mobile Strategy Options](#2-mobile-strategy-options)
3. [Recommended Approach: .NET MAUI Blazor Hybrid](#3-recommended-approach-net-maui-blazor-hybrid)
4. [Component-by-Component Migration Analysis](#4-component-by-component-migration-analysis)
5. [Breaking Changes & Blockers](#5-breaking-changes--blockers)
6. [New Mobile-Specific Requirements](#6-new-mobile-specific-requirements)
7. [Effort Estimation](#7-effort-estimation)
8. [Implementation Phases](#8-implementation-phases)
9. [Risk Assessment](#9-risk-assessment)
10. [Alternative: PWA Approach](#10-alternative-pwa-approach)

---

## 1. Current Architecture Summary

### BlazorUI Project Statistics

| Category | Count | Detail |
|----------|-------|--------|
| Razor Pages | 47 | Login, Dashboard, Invoices, Clients, Templates, Settings, SysAdmin |
| Shared Components | 9 | AppDataGrid, EnumSelect, BankAccountInput, InvoiceItemEditor, etc. |
| API Services | 18 | All inherit ApiClientBase (375 LOC), JWT auth + impersonation headers |
| Models/DTOs | 8 | LoginResponse, PagedResult<T>, etc. |
| Localization Keys | 450+ | Czech (cs-CZ) + English (en-US) via IStringLocalizer |
| Layout Components | 3 | MainLayout, NavMenu, ReconnectModal |
| JS Interop | 2 files | download.js (file download), ReconnectModal.razor.js |
| Total LOC (estimated) | ~8,000–10,000 | Razor + C# code-behind |

### Architecture Dependencies

- **Blazor Server** — all logic runs server-side, thin WebSocket connection to browser
- **ProtectedSessionStorage** — JWT token + impersonation state stored in encrypted browser session
- **Named HttpClient** — "InvoiceAPI" configured in DI with base URL from appsettings
- **MudBlazor v8.15.0** — Material Design components (tables, dialogs, forms, icons, snackbars)
- **Blazored.TextEditor** — Quill WYSIWYG editor for HTML template editing
- **Cookie-based culture** — language switching via `/Culture/SetCulture` endpoint
- **IJSRuntime** — file download (base64 → Blob → anchor click)

### Backend API (Unchanged)

The API project is **fully decoupled** — 13 REST controllers with JWT authentication. The mobile app will consume the same API endpoints. **No backend changes required** for basic mobile support.

---

## 2. Mobile Strategy Options

### Option A: .NET MAUI Blazor Hybrid ⭐ RECOMMENDED

| Aspect | Detail |
|--------|--------|
| **Concept** | Blazor components run inside a native MAUI WebView (BlazorWebView) |
| **Code reuse** | 70–85% of existing Razor components reusable directly |
| **Platforms** | iOS, Android, Windows, macOS (single codebase) |
| **Native access** | Full access to device APIs (camera, file system, biometrics, push notifications) |
| **Offline** | Possible with local SQLite cache |
| **Distribution** | App Store / Google Play |
| **Effort** | Medium (3–5 months for 1–2 developers) |

### Option B: Progressive Web App (PWA)

| Aspect | Detail |
|--------|--------|
| **Concept** | Add service worker + manifest to existing Blazor app for "installable" web experience |
| **Code reuse** | 95–100% (same codebase, just add PWA features) |
| **Platforms** | All browsers (iOS Safari has PWA limitations) |
| **Native access** | Limited (no push notifications on iOS, no file system, no biometrics) |
| **Offline** | Limited (Blazor Server requires WebSocket; Blazor WASM needed for true offline) |
| **Distribution** | URL-based (no App Store) |
| **Effort** | Low (2–4 weeks) |

### Option C: Native Apps (React Native / Flutter / Kotlin+Swift)

| Aspect | Detail |
|--------|--------|
| **Concept** | Complete rewrite of UI in a native mobile framework |
| **Code reuse** | 0% from BlazorUI (only API contracts shared) |
| **Platforms** | iOS + Android |
| **Native access** | Full |
| **Offline** | Full |
| **Distribution** | App Store / Google Play |
| **Effort** | Very High (6–12 months for 2+ developers) |

### Option D: Blazor WASM (WebAssembly) Standalone

| Aspect | Detail |
|--------|--------|
| **Concept** | Rewrite Blazor Server → Blazor WASM (client-side), then wrap in MAUI or deploy as PWA |
| **Code reuse** | 60–75% (major hosting model change, no ProtectedSessionStorage, no server-side rendering) |
| **Platforms** | All browsers, optionally MAUI wrapper |
| **Native access** | None (browser only) unless combined with MAUI |
| **Offline** | Possible (WASM runs client-side) |
| **Effort** | High (4–6 months — significant refactor of auth + state management) |

---

## 3. Recommended Approach: .NET MAUI Blazor Hybrid

### Why MAUI Blazor Hybrid?

1. **Maximum code reuse** — existing Razor components, services, and localization work inside BlazorWebView with minimal changes
2. **Same tech stack** — team stays in .NET/C#/Blazor ecosystem, no new framework to learn
3. **Native capabilities** — camera (scan invoices), biometric login, push notifications, file system access for PDF export
4. **Single codebase** — one MAUI project targets iOS + Android + Windows (+ macOS optional)
5. **Shared class library** — extract common Razor components + services into a shared RCL (Razor Class Library) usable by both BlazorUI (web) and MAUI (mobile)

### High-Level Architecture

```
┌─────────────────────────────────────────────────────┐
│                  Shared RCL (new)                    │
│  ┌──────────┐ ┌──────────┐ ┌───────────────────┐   │
│  │  Pages   │ │Components│ │  API Services      │   │
│  │ (Razor)  │ │ (Shared) │ │ (ApiClientBase)    │   │
│  └──────────┘ └──────────┘ └───────────────────┘   │
│  ┌──────────┐ ┌──────────┐ ┌───────────────────┐   │
│  │  Models  │ │Resources │ │  Interfaces        │   │
│  │  (DTOs)  │ │ (.resx)  │ │ (IAuthStorage)     │   │
│  └──────────┘ └──────────┘ └───────────────────┘   │
└──────────────────┬──────────────────┬───────────────┘
                   │                  │
        ┌──────────▼──────┐  ┌───────▼────────────┐
        │  BlazorUI (Web) │  │  MAUI App (Mobile)  │
        │  Blazor Server  │  │  BlazorWebView      │
        │  ProtectedSS    │  │  SecureStorage       │
        │  Cookie culture │  │  Preferences culture │
        │  JS download    │  │  Native file save    │
        └─────────────────┘  └──────────────────────┘
                   │                  │
                   └──────┬───────────┘
                          │
                 ┌────────▼────────┐
                 │   InvoiceApi    │
                 │   REST API      │
                 │  (unchanged)    │
                 └─────────────────┘
```

---

## 4. Component-by-Component Migration Analysis

### 4.1 Pages — What Migrates Directly (minimal changes)

These pages are standard MudBlazor forms/tables and will work in BlazorWebView with little to no modification:

| Page | Lines | Migration Effort | Notes |
|------|-------|-----------------|-------|
| Login.razor | ~150 | Low | Replace ProtectedSessionStorage → SecureStorage |
| Register.razor | ~120 | Low | Standard form |
| VerifyEmail.razor | ~50 | Low | Deep link handling needed |
| SetPassword.razor | ~80 | Low | Standard form |
| Home.razor (Dashboard) | ~200 | Low | Charts/stats render fine in WebView |
| Invoices.razor | ~400 | Medium | Large page, table pagination — needs responsive layout |
| InvoiceDetail.razor | ~500 | Medium | Complex form — needs scroll optimization for mobile |
| Clients.razor | ~250 | Low | Standard table |
| ClientDetail.razor | ~350 | Medium | Multi-section form, address editor |
| InvoiceTemplates.razor | ~200 | Low | Standard table |
| InvoiceTemplateDetail.razor | ~300 | Medium | InvoiceItemEditor needs touch optimization |
| ContentTemplates.razor | ~150 | Low | Standard table |
| ContentTemplateDetail.razor | ~250 | **High** | Quill editor — touch/mobile issues (see blockers) |
| VatRates.razor | ~150 | Low | Simple CRUD |
| Currencies.razor | ~120 | Low | Simple CRUD |
| NumberSequences.razor | ~200 | Low | Standard CRUD |
| MyCompany.razor | ~300 | Medium | Multi-section settings form |
| TwoFactorSettings.razor | ~200 | Medium | QR code display, TOTP setup |
| TwoFactorVerification.razor | ~100 | Low | Simple code input |

### 4.2 Pages — SysAdmin Only (defer to Phase 2+)

These pages are admin-only and not needed for the initial mobile release:

| Page | Recommendation |
|------|---------------|
| Companies.razor | Defer — SysAdmin manages via web |
| CompanyDetail.razor | Defer |
| CompanySettings.razor | Defer |
| SystemSettings.razor | Defer |
| Users.razor | Defer |
| Logs.razor | Defer |

### 4.3 Shared Components

| Component | Migration | Notes |
|-----------|-----------|-------|
| AppDataGrid.razor | **Medium** | MudTable works in WebView but needs responsive/horizontal scroll for small screens |
| EnumSelect.razor | None | Works as-is |
| BankAccountInput.razor | None | Works as-is |
| InvoiceItemEditor.razor | **Medium** | Inline editing on small screens is challenging — consider swipe-to-edit or modal editing on mobile |
| AddressListEditor.razor | Low | May need larger touch targets |
| AddressDialog.razor | Low | MudDialog works in WebView |
| LanguageSwitcher.razor | Low | Replace cookie-based → Preferences-based culture |
| UnsavedChangesLock.razor | **Medium** | NavigationLock works differently in MAUI — need MAUI back-button handler |
| FolderPicker.razor | **Medium** | Cloud storage folder browsing — needs platform-specific file picker |

### 4.4 Services

| Service | Migration | Notes |
|---------|-----------|-------|
| ApiClientBase.cs (375 LOC) | **Medium** | Replace ProtectedSessionStorage token retrieval → platform-agnostic IAuthTokenProvider |
| CustomAuthenticationStateProvider.cs (264 LOC) | **High** | Replace ProtectedSessionStorage → MAUI SecureStorage; different lifecycle |
| All 16 API services | None | Inherit from ApiClientBase — work as-is once base is fixed |
| AuthApiService.cs | Low | Doesn't inherit ApiClientBase — minimal changes |

### 4.5 Layout

| Component | Migration | Notes |
|-----------|-----------|-------|
| MainLayout.razor (272 LOC) | **High** | Complete redesign for mobile — hamburger menu, bottom nav, no app bar impersonation selector |
| NavMenu.razor (100 LOC) | **High** | Needs mobile-optimized navigation (bottom tab bar / drawer) |
| ReconnectModal.razor | **Remove** | Not needed — MAUI Blazor Hybrid doesn't use WebSocket |

---

## 5. Breaking Changes & Blockers

### 5.1 Critical Blockers

| # | Blocker | Impact | Solution | Effort |
|---|---------|--------|----------|--------|
| 1 | **ProtectedSessionStorage** | Auth state, impersonation, session data | Abstract to `ISecureStorageService` — web impl uses ProtectedSessionStorage, MAUI impl uses `SecureStorage` | 2–3 days |
| 2 | **Blazored.TextEditor (Quill)** | WYSIWYG HTML editor for ContentTemplateDetail | Quill runs in WebView but touch support is poor on mobile. Options: (a) use read-only view on mobile, (b) find mobile-friendly rich text editor, (c) accept degraded mobile experience | 3–5 days |
| 3 | **JS Interop — File Download** | `download.js` uses browser DOM (anchor element, Blob) | MAUI: use `FileSaver` from MAUI CommunityToolkit or native file save API | 1–2 days |
| 4 | **Cookie-based culture switching** | `/Culture/SetCulture` endpoint sets cookie — not applicable in MAUI | Use `Preferences.Set("culture", "cs-CZ")` and apply in `MauiProgram.cs` | 1 day |
| 5 | **OAuth Callback handling** | `AuthCallback.razor` expects browser redirect URL | MAUI needs custom URL scheme registration (`invoiceapi://auth/callback`) + platform-specific intent filters | 2–3 days |
| 6 | **Deep links** (email verification, password reset) | URLs open in browser, not in app | Register URL scheme + handle `App.OnAppLinkRequestReceived` | 1–2 days |

### 5.2 MudBlazor in MAUI WebView — Known Issues

| Issue | Severity | Workaround |
|-------|----------|------------|
| MudDialog backdrop may not cover full mobile screen | Low | CSS override for `100vh` / `100dvh` |
| MudDatePicker touch calendar usability | Low | Works but small touch targets — may need custom CSS |
| MudTable horizontal scroll on narrow screens | Medium | Wrap in `<div style="overflow-x:auto">` or use responsive column hiding |
| MudSelect dropdown positioning on virtual keyboard | Medium | Known MudBlazor issue — use `AnchorOrigin` adjustments |
| Font loading (Google Fonts Roboto) | Low | Bundle font files locally (no CDN in offline scenario) |

### 5.3 Platform-Specific Requirements

| Requirement | iOS | Android |
|-------------|-----|---------|
| Min version | iOS 16+ | Android 8.0+ (API 26) |
| WebView engine | WKWebView (Safari) | Chrome-based WebView |
| Status bar handling | Safe area insets required | Edge-to-edge + status bar color |
| Back button | No hardware back — swipe gesture | Hardware/software back button |
| Keyboard behavior | Auto-scroll to focused field | `windowSoftInputMode=adjustResize` |
| Push notifications | APNs | FCM |
| App size (estimated) | ~30–50 MB | ~25–40 MB |

---

## 6. New Mobile-Specific Requirements

### 6.1 Must-Have (MVP)

| Feature | Description | Effort |
|---------|-------------|--------|
| **Biometric login** | Fingerprint / Face ID to unlock app (optional per user) | 2–3 days |
| **Pull-to-refresh** | Standard mobile UX for list pages | 1 day |
| **Responsive layouts** | Forms must stack vertically, tables must scroll horizontally | 3–5 days |
| **Offline indicator** | Show connection status, disable actions when offline | 1 day |
| **Native PDF viewer** | Open PDF in system viewer instead of JS download | 1 day |
| **Mobile-optimized navigation** | Bottom tab bar for main sections + hamburger drawer | 3–5 days |
| **Splash screen + app icon** | Platform-specific assets | 1 day |
| **Secure token storage** | MAUI SecureStorage (Keychain/KeyStore) | 1–2 days (part of auth refactor) |

### 6.2 Nice-to-Have (Post-MVP)

| Feature | Description | Effort |
|---------|-------------|--------|
| Camera scan (OCR) | Scan paper invoices or business cards | 5–10 days |
| Push notifications | New invoice received, payment overdue | 3–5 days |
| Offline mode | Local SQLite cache, sync when online | 10–15 days |
| Share intent | Share PDF via WhatsApp, email, etc. | 1–2 days |
| Dark mode sync | Follow system dark/light preference | 1 day |
| Barcode/QR scan | Scan QR Faktura codes from other invoices | 2–3 days |

---

## 7. Effort Estimation

### 7.1 Project Setup & Infrastructure

| Task | Effort (days) |
|------|---------------|
| Create MAUI Blazor Hybrid project (`InvoiceApi.MobileApp`) | 1 |
| Create shared Razor Class Library (`InvoiceApi.Shared.UI`) | 1 |
| Extract common components from BlazorUI → shared RCL | 3–5 |
| Abstract storage interfaces (`ISecureStorageService`, `IAuthTokenProvider`) | 2–3 |
| Implement MAUI-specific storage (SecureStorage, Preferences) | 2 |
| Implement MAUI-specific file operations (PDF save/share) | 1–2 |
| Configure MAUI project (icons, splash, permissions, URL schemes) | 2 |
| Setup CI/CD for mobile builds (App Center / GitHub Actions) | 2–3 |
| **Subtotal** | **14–19 days** |

### 7.2 Layout & Navigation Redesign

| Task | Effort (days) |
|------|---------------|
| Mobile MainLayout (bottom nav + drawer) | 3–5 |
| Mobile NavMenu (tab bar with 4–5 tabs) | 2–3 |
| Responsive CSS overrides for all MudBlazor components | 3–5 |
| Safe area / notch handling (iOS) | 1 |
| Back button handling (Android) | 1 |
| **Subtotal** | **10–15 days** |

### 7.3 Page Migration (Core Business Pages)

| Page Group | Pages | Effort (days) |
|------------|-------|---------------|
| Auth (Login, Register, 2FA, OAuth) | 5 | 3–5 |
| Dashboard (Home) | 1 | 1–2 |
| Invoices (list + detail) | 2 | 5–8 |
| Clients (list + detail) | 2 | 3–5 |
| Templates (invoice + content) | 4 | 3–5 |
| Settings (MyCompany, VatRates, Currencies, NumberSequences) | 4 | 3–5 |
| **Subtotal** | **18 pages** | **18–30 days** |

### 7.4 Mobile-Specific Features (MVP)

| Feature | Effort (days) |
|---------|---------------|
| Biometric authentication | 2–3 |
| Pull-to-refresh on lists | 1 |
| Native PDF viewer integration | 1–2 |
| Offline connectivity indicator | 1 |
| Deep link handling | 1–2 |
| Culture/language via Preferences | 1 |
| **Subtotal** | **7–10 days** |

### 7.5 Testing & QA

| Task | Effort (days) |
|------|---------------|
| Unit tests for new platform abstractions | 3–5 |
| Manual testing on iOS simulator | 3–5 |
| Manual testing on Android emulator | 3–5 |
| Testing on physical devices (2+ iOS, 2+ Android) | 3–5 |
| Fix platform-specific bugs | 5–8 |
| **Subtotal** | **17–28 days** |

### 7.6 App Store Preparation

| Task | Effort (days) |
|------|---------------|
| App Store Connect setup (iOS) | 1 |
| Google Play Console setup (Android) | 1 |
| Privacy policy, screenshots, descriptions | 2 |
| App signing & certificate management | 1 |
| First submission + review iterations | 3–5 |
| **Subtotal** | **8–10 days** |

### 7.7 Total Effort Summary

| Phase | Optimistic | Realistic | Pessimistic |
|-------|-----------|-----------|-------------|
| Infrastructure & Setup | 14 days | 19 days | 25 days |
| Layout & Navigation | 10 days | 15 days | 20 days |
| Page Migration | 18 days | 25 days | 35 days |
| Mobile Features (MVP) | 7 days | 10 days | 14 days |
| Testing & QA | 17 days | 22 days | 30 days |
| App Store | 8 days | 10 days | 14 days |
| **TOTAL** | **74 days** | **101 days** | **138 days** |
| **Calendar (1 dev)** | **~3.5 months** | **~5 months** | **~7 months** |
| **Calendar (2 devs)** | **~2 months** | **~3 months** | **~4 months** |

---

## 8. Implementation Phases

### Phase 1: Foundation (Weeks 1–3)

- [ ] Create `InvoiceApi.Shared.UI` Razor Class Library
- [ ] Create `InvoiceApi.MobileApp` MAUI Blazor Hybrid project
- [ ] Define abstraction interfaces:
  - `ISecureStorageService` (token storage)
  - `IAuthTokenProvider` (JWT retrieval)
  - `ICultureService` (language management)
  - `IFileService` (save/share files)
- [ ] Implement MAUI-specific services (SecureStorage, Preferences, FileSaver)
- [ ] Implement web-specific services (ProtectedSessionStorage wrapper)
- [ ] Extract `ApiClientBase` + all 18 services → shared RCL
- [ ] Extract Models/DTOs → shared RCL
- [ ] Extract Resources (.resx) → shared RCL
- [ ] Verify shared RCL builds and is consumable by both projects

### Phase 2: Mobile Shell (Weeks 3–5)

- [ ] Design mobile navigation (bottom tab bar: Dashboard, Invoices, Clients, Settings, Profile)
- [ ] Create `MobileMainLayout.razor` with MudBlazor mobile patterns
- [ ] Create `MobileNavMenu.razor` with bottom tabs
- [ ] Implement responsive CSS overrides (breakpoints for small screens)
- [ ] Add safe area handling (iOS notch), status bar theming
- [ ] Implement Android back button handler
- [ ] Login page + biometric unlock
- [ ] OAuth callback with custom URL scheme

### Phase 3: Core Pages (Weeks 5–9)

- [ ] Dashboard (Home) — responsive card layout
- [ ] Invoices list — responsive table with horizontal scroll, pull-to-refresh
- [ ] Invoice detail — vertical form layout, native PDF open, email send
- [ ] Clients list — responsive table
- [ ] Client detail — vertical form layout
- [ ] My Company settings page

### Phase 4: Secondary Pages (Weeks 9–11)

- [ ] Invoice Templates (list + detail)
- [ ] Content Templates (list + Quill editor or read-only on mobile)
- [ ] VAT Rates, Currencies, Number Sequences
- [ ] Two-Factor Settings
- [ ] Profile / logout

### Phase 5: Polish & Testing (Weeks 11–14)

- [ ] End-to-end testing on iOS simulator + physical device
- [ ] End-to-end testing on Android emulator + physical device
- [ ] Performance optimization (lazy loading, virtualization)
- [ ] Offline connectivity indicator
- [ ] Error handling for no-network scenarios
- [ ] Accessibility review (font sizes, touch targets ≥ 44pt)

### Phase 6: Release (Weeks 14–16)

- [ ] App Store submission (iOS)
- [ ] Google Play submission (Android)
- [ ] Review feedback iterations
- [ ] Production monitoring setup
- [ ] User documentation / onboarding screens

---

## 9. Risk Assessment

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| MudBlazor rendering issues in MAUI WebView | Medium | High | Test early (Phase 1), have CSS fallback plan |
| Quill editor unusable on mobile | High | Medium | Accept read-only mode on mobile for templates, edit via web |
| iOS WebView performance (complex pages) | Medium | High | Profile early, virtualize long lists, lazy-load tabs |
| MAUI bugs / platform inconsistencies | Medium | Medium | Pin MAUI version, test on physical devices frequently |
| App Store rejection (first submission) | Medium | Low | Follow guidelines from start, prepare privacy policy early |
| SecureStorage migration from ProtectedSessionStorage | Low | High | Well-defined interface abstraction, thorough testing |
| Team .NET MAUI experience gap | Medium | Medium | Allocate learning time, start with simple pages |
| Blazor MAUI hot reload issues | Medium | Low | Workaround: manual rebuild for affected pages |

---

## 10. Alternative: PWA Approach

If a full native app is overkill for the current user base, a **PWA (Progressive Web App)** approach provides 80% of the mobile value at 20% of the cost:

### PWA Effort (requires Blazor WASM migration first)

| Task | Effort |
|------|--------|
| Migrate Blazor Server → Blazor WASM | 15–20 days |
| Add service worker + manifest.json | 1–2 days |
| Add offline page / caching strategy | 3–5 days |
| Responsive CSS for mobile browsers | 5–8 days |
| Test on iOS Safari + Chrome Android | 3–5 days |
| **Total** | **27–40 days (~1.5–2 months)** |

### PWA Limitations

- **No App Store presence** (no discoverability)
- **iOS Safari**: no push notifications, limited background sync, storage may be evicted after 7 days of inactivity
- **No biometric auth** (WebAuthn partial support)
- **No native file system** access
- **Blazor WASM**: larger initial download (~5–10 MB), slower startup vs. native
- **No camera/scanner** integration (limited `getUserMedia`)

### PWA Recommendation

Use PWA **only if**:
- Target audience primarily uses Android (PWA support is excellent)
- App Store distribution is not required
- Offline support is not critical
- Budget is strictly limited

---

## Summary & Recommendation

### Recommended: .NET MAUI Blazor Hybrid

| Metric | Value |
|--------|-------|
| **Code reuse from existing BlazorUI** | ~70–80% |
| **New code to write** | ~20–30% (layouts, platform abstractions, mobile UX) |
| **Realistic timeline (1 developer)** | ~5 months |
| **Realistic timeline (2 developers)** | ~3 months |
| **Biggest risk** | MudBlazor + Quill rendering quality in MAUI WebView |
| **Biggest win** | Same .NET/C#/Blazor stack, shared codebase, native device access |

### Key Decision Points Before Starting

1. **Which pages are mandatory for mobile MVP?** (Recommend: Dashboard, Invoices, Clients, MyCompany, Login/2FA)
2. **Is offline support required for MVP?** (Recommend: No — add in v2)
3. **Is SysAdmin functionality needed on mobile?** (Recommend: No — web only)
4. **Content template editing on mobile?** (Recommend: Read-only on mobile, edit via web)
5. **Target platforms?** (Recommend: iOS + Android; skip macOS/Windows desktop MAUI)

### Files to Create (New Projects)

```
InvoiceApi.Shared.UI/              ← New Razor Class Library
├── Components/
│   ├── Pages/                     ← Migrated from BlazorUI
│   └── Shared/                    ← Migrated from BlazorUI
├── Services/                      ← Migrated from BlazorUI
├── Models/                        ← Migrated from BlazorUI
├── Resources/                     ← Migrated from BlazorUI
├── Abstractions/
│   ├── ISecureStorageService.cs   ← NEW: platform-agnostic storage
│   ├── IAuthTokenProvider.cs      ← NEW: JWT token retrieval
│   ├── ICultureService.cs         ← NEW: language management
│   └── IFileService.cs            ← NEW: file save/share
└── InvoiceApi.Shared.UI.csproj

InvoiceApi.MobileApp/              ← New MAUI Blazor Hybrid
├── Platforms/
│   ├── Android/                   ← Android-specific config
│   └── iOS/                       ← iOS-specific config
├── Services/
│   ├── MauiSecureStorageService.cs
│   ├── MauiAuthTokenProvider.cs
│   ├── MauiCultureService.cs
│   └── MauiFileService.cs
├── Components/Layout/
│   ├── MobileMainLayout.razor     ← NEW: mobile-optimized layout
│   └── MobileNavMenu.razor        ← NEW: bottom tab navigation
├── MauiProgram.cs
├── MainPage.xaml                  ← BlazorWebView host
└── InvoiceApi.MobileApp.csproj
```
