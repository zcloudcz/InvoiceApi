# Mobilní aplikace InvoiceApi — Analýza možností (únor 2026)

## Aktuální stav UI

| Parametr | Hodnota |
|----------|---------|
| Framework | Blazor WebAssembly Standalone (.NET 10.0) |
| UI knihovna | MudBlazor v8.15.0 |
| Autentizace | JWT + Blazored.LocalStorage |
| Lokalizace | CZ/EN (IStringLocalizer, ~450 klíčů) |
| Stránky | 31 stránek (pages) |
| Sdílené komponenty | 12 (EnumSelect, BankAccountInput, InvoiceItemEditor, ...) |
| API služby | 15 HttpClient služeb (ApiClientBase) |
| Editor šablon | Blazored.TextEditor (Quill WYSIWYG) |
| QR kódy | QRCoder (SIND/SPD české standardy) |
| Testy | 323 (318 unit + 5 integration) |
| PWA infrastruktura | **Neexistuje** |

**Klíčová změna oproti předchozí analýze**: Migrace z Blazor Server na **Standalone WASM** je dokončena. Aplikace nyní běží kompletně v prohlížeči — to zásadně mění výhodnost jednotlivých přístupů.

---

## Možnost 1: PWA (Progressive Web App) ⭐ DOPORUČENO

### Popis
Přidání PWA vrstvy na stávající Blazor WASM aplikaci. Blazor WASM je technicky už "klient-side" aplikace běžící v prohlížeči — přidání PWA vyžaduje pouze manifest, service worker a ikony.

### Pracnost
**1–2 dny** (minimální změny)

### Co je potřeba implementovat

1. **`wwwroot/manifest.json`** — metadata aplikace (název, ikony, barvy, display: standalone)
2. **`wwwroot/service-worker.js`** — cachování statických assetů pro offline režim
3. **`wwwroot/service-worker.published.js`** — produkční verze s verzovaným cachováním
4. **Ikony** — sada PNG ikon (192×192, 512×512 minimum) pro instalaci
5. **`index.html`** — přidat `<link rel="manifest">` + meta tagy pro iOS
6. **`Program.cs`** — žádné změny (WASM je už client-side)

### Výhody
| Výhoda | Detail |
|--------|--------|
| **Minimální pracnost** | Stávající kód se nemění, jen se přidá PWA vrstva |
| **Žádný nový projekt** | Vše zůstává v `InvoiceApi.BlazorUI` |
| **Instalovatelná** | Uživatel si "nainstaluje" appku z prohlížeče (Chrome, Edge, Safari) |
| **Offline-first** | Service worker cachuje WASM bundle + statické soubory |
| **Automatické aktualizace** | Při připojení se stáhne nová verze (service worker update) |
| **Jeden codebase** | Desktop + mobil + tablet = stejný kód |
| **App Store nepotřeba** | Distribuce přes URL (možný i TWA pro Google Play) |
| **Push notifikace** | Web Push API (Chrome, Edge; omezené na iOS 16.4+) |
| **MudBlazor responsive** | MudBlazor je responsive out-of-the-box (Breakpoints, MudHidden) |

### Nevýhody
| Nevýhoda | Detail |
|----------|--------|
| **Žádný přístup k nativním API** | Nelze: NFC, Bluetooth, pokročilé kamery, kontakty |
| **iOS omezení** | Safari PWA má limity — max 50 MB storage, omezený push (od iOS 16.4) |
| **Není v App Store** | Uživatelé musí přidat přes prohlížeč (nižší důvěra pro B2C) |
| **Offline limitace** | API volání nefungují offline — potřeba queue/sync strategie pro data |
| **WASM bundle velikost** | ~15-30 MB initial download (cachováno po první návštěvě) |

### Vhodnost pro InvoiceApi
**VYSOKÁ** — InvoiceApi je B2B fakturační systém. Uživatelé pracují primárně online (faktury, klienti, šablony). PWA pokryje 95 % mobilních use-cases:
- Prohlížení faktur v terénu
- Kontrola dashboardu
- Rychlý přehled klientů
- QR kódy faktur na telefonu

### Implementační kroky

```
1. Vytvořit wwwroot/manifest.json
2. Vytvořit wwwroot/icon-192.png a icon-512.png
3. Vytvořit wwwroot/service-worker.js (dev) + service-worker.published.js (prod)
4. Upravit wwwroot/index.html — přidat manifest link + meta tagy
5. Otestovat: Chrome DevTools → Application → Manifest/Service Workers
6. Otestovat: "Add to Home Screen" na Android/iOS
7. Optimalizovat MudBlazor responsive breakpoints pro mobil
```

---

## Možnost 2: MAUI Blazor Hybrid

### Popis
Vytvoření nového .NET MAUI projektu (`InvoiceApi.MauiApp`), který hostuje Blazor komponenty ve WebView. Sdílené komponenty a služby se přesunou do sdílené knihovny (`InvoiceApi.Shared`).

### Pracnost
**3–6 týdnů** (záleží na rozsahu nativních funkcí)

### Architektura

```
InvoiceApi.sln
├── InvoiceApi.Contracts          (DTO, sdíleno — beze změny)
├── InvoiceApi.Shared.UI          (NOVÝ — sdílené Razor komponenty)
│   ├── Components/Pages/         (přesunuté z BlazorUI)
│   ├── Components/Shared/        (přesunuté z BlazorUI)
│   └── Services/                 (API služby, AuthState)
├── InvoiceApi.BlazorUI           (WASM host — odkazuje Shared.UI)
├── InvoiceApi.MauiApp            (NOVÝ — MAUI Blazor Hybrid host)
│   ├── MauiProgram.cs
│   ├── MainPage.xaml             (BlazorWebView)
│   └── Platforms/                (Android, iOS, Windows)
└── ...
```

### Co je potřeba implementovat

1. **`InvoiceApi.Shared.UI`** — nový RCL (Razor Class Library) projekt
   - Přesunout všech 31 stránek + 12 komponent z BlazorUI
   - Přesunout 15 API služeb
   - Abstrahovat storage (IStorageService: LocalStorage vs SecureStorage)
   - Abstrahovat navigation (NavigationManager vs MAUI Shell)
2. **`InvoiceApi.MauiApp`** — nový MAUI projekt
   - `MauiProgram.cs` — DI registrace, HttpClient s base URL
   - `MainPage.xaml` — BlazorWebView
   - Platform-specific konfigurace (Android manifest, iOS Info.plist)
   - SecureStorage pro JWT token
   - Biometric auth (volitelně)
3. **`InvoiceApi.BlazorUI`** — refaktor na tenký host
   - Odkazuje `Shared.UI` místo vlastních komponent
   - Zůstává `index.html`, `Program.cs`, WASM-specifický kód

### Výhody
| Výhoda | Detail |
|--------|--------|
| **Nativní přístup** | Kamera, NFC, Bluetooth, biometrika, soubory, kontakty |
| **App Store distribuce** | Google Play, Apple App Store (důvěra uživatelů) |
| **Offline s SQLite** | Lokální databáze pro plnohodnotný offline režim |
| **SecureStorage** | Bezpečné ukládání JWT tokenů (Keychain/Keystore) |
| **Push notifikace** | Firebase (Android) + APNS (iOS) — plná podpora |
| **Sdílený kód** | ~80 % Blazor kódu sdíleno mezi WASM a MAUI |

### Nevýhody
| Nevýhoda | Detail |
|----------|--------|
| **Vysoká pracnost** | 3-6 týdnů refaktoru + nový projekt |
| **Dva hosty** | Údržba WASM hostu + MAUI hostu |
| **MAUI nestabilita** | MAUI stále má výkonnostní problémy, pomalý startup na Android |
| **Build pipeline** | Potřeba macOS pro iOS build (Xcode, Apple Developer Account) |
| **App Store review** | Apple review process (týdny), developer fee ($99/rok) |
| **Blazored.TextEditor** | Quill editor nemusí fungovat v MAUI WebView (JS interop problémy) |
| **MudBlazor v MAUI** | Funguje, ale občasné rendering issues ve WebView |

### Vhodnost pro InvoiceApi
**STŘEDNÍ** — MAUI Hybrid dává smysl jen pokud jsou potřeba nativní funkce (kamera pro sken dokumentů, NFC pro platby). Pro čistě datovou fakturační aplikaci je to overengineering.

---

## Možnost 3: Nativní mobilní aplikace (Flutter / React Native / Kotlin/Swift)

### Popis
Kompletní přepsání UI do nativního mobilního frameworku. Backend API zůstává beze změny.

### Pracnost
**3–6 měsíců** (kompletní rewrite)

### Výhody
| Výhoda | Detail |
|--------|--------|
| **Nejlepší UX** | Nativní animace, gesta, platformní konvence |
| **Nejlepší výkon** | Žádný WebView overhead |
| **Plný přístup k HW** | Vše, co platforma nabízí |

### Nevýhody
| Nevýhoda | Detail |
|----------|--------|
| **Kompletní rewrite** | 31 stránek, 12 komponent, 15 služeb = od nuly |
| **Nový jazyk/framework** | Dart (Flutter), JS/TS (React Native), Kotlin/Swift |
| **Dva codebasy** | Web (Blazor) + mobil (nativní) = dvojnásobná údržba |
| **Ztráta investice** | Existující Blazor kód se nevyužije |
| **Nový tým/skillset** | .NET vývojáři musí znát Flutter/React Native |

### Vhodnost pro InvoiceApi
**NÍZKÁ** — Pro B2B fakturační systém nemá smysl investovat do kompletního přepisu. Blazor WASM + PWA pokryje mobilní potřeby.

---

## Srovnávací tabulka

| Kritérium | PWA | MAUI Hybrid | Nativní (Flutter/RN) |
|-----------|-----|-------------|---------------------|
| **Pracnost** | 1–2 dny | 3–6 týdnů | 3–6 měsíců |
| **Sdílení kódu** | 100 % | ~80 % | 0 % (API sdíleno) |
| **Offline** | Základní (cache) | Plný (SQLite) | Plný |
| **Nativní API** | Omezené (Web API) | Plné | Plné |
| **App Store** | Ne (TWA volitelně) | Ano | Ano |
| **Údržba** | Minimální | Střední (2 hosty) | Vysoká (2 codebasy) |
| **Startup čas** | ~2-3s (první load) | ~3-5s (MAUI + WebView) | <1s |
| **iOS podpora** | Dobrá (Safari PWA) | Dobrá (MAUI iOS) | Výborná |
| **Android podpora** | Výborná (Chrome PWA) | Dobrá (MAUI Android) | Výborná |
| **Push notifikace** | Omezené (iOS 16.4+) | Plné | Plné |
| **Biometrika** | Ne | Ano | Ano |
| **Náklady** | $0 | $99/rok (Apple) | $99/rok (Apple) + $25 (Google) |

---

## Doporučená strategie: PWA → (volitelně) MAUI Hybrid

### Fáze 1: PWA (okamžitě, 1–2 dny)
Přidáním PWA vrstvy na stávající Blazor WASM získáme:
- Instalovatelnou aplikaci na Android i iOS
- Offline přístup k cachovaným stránkám
- Full-screen režim bez prohlížečového UI
- Zero-cost deployment (žádné App Store poplatky)

**Toto je ideální první krok** — minimální práce, maximální hodnota.

### Fáze 2: Mobilní optimalizace (1–2 týdny, volitelně)
Po PWA nasazení optimalizovat UI pro mobilní zařízení:
- MudBlazor responsive breakpoints (MudHidden, xs/sm/md)
- Swipe gesta pro navigaci (pokud MudBlazor podporuje)
- Touch-friendly velikosti tlačítek a inputů
- Optimalizace WASM bundle size (lazy loading, trimming)
- Bottom navigation pro mobilní rozložení

### Fáze 3: MAUI Hybrid (pouze pokud je business požadavek)
Pokud zákazníci vyžadují:
- Přítomnost v App Store (firemní politika)
- Offline editaci faktur s následnou synchronizací
- Sken dokumentů kamerou (OCR)
- Biometrickou autentizaci
- Push notifikace na iOS < 16.4

Teprve pak investovat do MAUI Hybrid. **Nedoporučuji předčasně** — PWA pokryje většinu potřeb B2B fakturačního systému.

---

## Technické poznámky

### PWA a Blazor WASM — specifika
- Blazor WASM Standalone **nativně podporuje PWA** — při vytvoření projektu s `--pwa` flagem se generuje automaticky. Protože jsme migrovali bez flagu, musíme přidat ručně.
- .NET WASM bundle se cachuje service workerem — po první návštěvě je reload téměř okamžitý
- `dotnet publish` generuje `service-worker-assets.js` s hashy všech statických souborů pro cache-busting

### Blazored.TextEditor (Quill) v mobilním režimu
- Quill WYSIWYG editor **funguje na mobilních prohlížečích** — PWA nebude mít problém
- V MAUI WebView mohou být problémy s JS interopem a touch event handlingem

### MudBlazor responsive
- MudBlazor má vestavěný responsive grid (MudGrid, MudItem) s breakpointy
- `MudHidden` komponent pro podmíněné zobrazení podle velikosti obrazovky
- `MudAppBar` + `MudDrawer` = hamburger menu pattern pro mobilní navigaci (už implementováno v MainLayout)

### WASM bundle optimalizace
- Aktuální .NET 10 WASM: ~15-20 MB (komprimováno ~5-8 MB s Brotli)
- `<PublishTrimmed>true</PublishTrimmed>` — trim nepoužívaného kódu
- `<BlazorEnableCompression>true</BlazorEnableCompression>` — Brotli komprese
- Lazy loading assemblies přes `LazyAssemblyLoader` pro stránky, které se nepoužívají často

---

## Závěr

**PWA je jasná volba pro InvoiceApi.** Po dokončení migrace na Blazor WASM Standalone je přidání PWA trivální záležitost s obrovskou přidanou hodnotou. Uživatelé získají "mobilní appku" bez nutnosti App Store, s offline přístupem k cachovaným datům a full-screen režimem.

MAUI Hybrid zůstává jako budoucí možnost pro případ, že se ukáže business potřeba nativních funkcí — ale s aktuálním rozsahem aplikace (fakturace, klienti, šablony, dashboard) není důvod do něj investovat předčasně.
