# Fakvio vs. Fakturovac.cz — Feature Comparison

> **Date**: 2026-03-02
> **Source**: https://www.fakturovac.cz/ + reviews (t15.cz, finmag.cz, websio.cz, 5nej.cz)

---

## Overview

| | **Fakvio** | **Fakturovac.cz** |
|---|---|---|
| **Target** | OSVČ, malé i střední firmy (CZ/EN) | OSVČ, mikrofirmy (CZ only) |
| **Pricing** | SaaS (vlastní hosting / Azure) | 100% zdarma, bez omezení |
| **Platform** | Blazor WASM (PWA) + MAUI (Android/iOS/Win/Mac) | Web only, no mobile app |
| **Tech stack** | .NET 10, Blazor, MudBlazor, PostgreSQL, EF Core | Neznámý |
| **Multi-tenant** | Ano (schema-per-tenant) | Ne (single-tenant SaaS) |
| **Jazyky UI** | CZ (fixní), EN resource soubory připraveny | CZ only |
| **Jazyky dokumentů** | CZ / EN (per-client) | CZ only |
| **Měny** | CZK, EUR, USD, GBP, PLN, CHF, HUF, RON | CZK only |

---

## Feature Matrix

Legend: ✅ = implemented, 🔶 = partial/planned, ❌ = not available

### Fakturace (Invoicing)

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| Vydané faktury (CRUD) | ✅ | ✅ | |
| Dobropisy (Credit Notes) | ✅ | ✅ | Fakturovac: "Opravné daňové doklady" |
| Zálohové faktury (Advance/Proforma) | ❌ | ✅ | **Fakvio chybí** — Fakturovac auto-generuje ostrou fakturu po úhradě |
| Přenesená daňová povinnost (Reverse Charge) | ❌ | ✅ | **Fakvio chybí** |
| Opakované faktury (Recurring) | 🔶 planned | ✅ | **Fakvio chybí** — Fakturovac: auto-vytvoření + distribuce |
| Šablony faktur (data blueprints) | ✅ | ✅ | Fakvio: plná šablona s položkami; Fakturovac: jednodušší |
| Hromadné operace (Bulk) | ✅ | ❌ | **Fakvio výhoda** — vydání, úhrada, PDF, email, smazání |
| Inline editace položek | ✅ | ❌ | Fakvio: MudTable inline editor s auto-výpočty |
| Číselné řady (Number Sequences) | ✅ | ❌ | Fakvio: formáty, sekvence, preview, default per typ |
| Výpočet splatnosti (Due Date) | ✅ | ✅ | Fakvio: 4 algoritmy; Fakturovac: per-zákazník |
| Variabilní symbol | ✅ | ✅ | |
| Stav faktury (workflow) | ✅ | ✅ | Fakvio: Draft→Completed→Paid→Credited→Deleted |

### PDF a dokumenty

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| PDF export | ✅ | ✅ | Fakvio: iText7 + HTML šablony s placeholders |
| HTML šablony (Quill WYSIWYG) | ✅ | ❌ | **Fakvio výhoda** — plně customizovatelné šablony |
| ISDOC formát | ❌ | ✅ | **Fakvio chybí** — český standard elektronické fakturace |
| ISDOC v PDF příloze | ❌ | ✅ | **Fakvio chybí** |
| Dodací list | ❌ | ✅ | **Fakvio chybí** |
| Příjmový pokladní doklad | ❌ | ✅ | **Fakvio chybí** |
| Per-client jazyk dokumentu | ✅ | ❌ | **Fakvio výhoda** — CZ/EN šablony dle jazyka klienta |
| Vlastní logo na faktuře | ✅ | ✅ | |
| Vlastní podpis | ❌ | ✅ | **Fakvio chybí** |

### QR kódy a platby

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| QR Faktura (SIND) | ✅ | ❌ | **Fakvio výhoda** — plný SIND standard s CRC32 |
| QR Platba (SPD) | ✅ | ✅ | Fakvio: 3-tier strategie (IBAN→Paylibo→SIND fallback) |
| QR kód na faktuře | ✅ | ✅ | |

### Emailing

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| Odeslání faktury emailem | ✅ | ✅ | |
| Šablony emailů (Handlebars) | ✅ | ✅ | Fakvio: plně customizovatelné HTML šablony |
| Automatické upomínky | 🔶 planned | ✅ | **Fakvio chybí** — Fakturovac: konfigurovatelné zpoždění, odklad 30 dnů |
| BCC distribuce | ❌ | ✅ | **Fakvio chybí** |
| SMTP per-tenant | ✅ | ❌ | **Fakvio výhoda** — 3-tier: firma→systém→appsettings |

### Platby a párování

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| Ruční označení úhrady | ✅ | ✅ | |
| Automatické párování z banky | ❌ | ✅ | **Fakvio chybí** — Fakturovac: 13 bank (Fio, KB, ČSOB, ČS, Air Bank, mBank...) |
| Sledování splatnosti | ✅ | ✅ | |

### Správa klientů (Contacts)

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| Adresář zákazníků | ✅ | ✅ | |
| Adresář dodavatelů | ❌ | ✅ | **Fakvio chybí** — Fakturovac má suppliers registry |
| ARES napojení (auto-complete) | ✅ | ✅ | Fakvio: standalone AresService + cache |
| 1:N Adresy | ✅ | ❌ | **Fakvio výhoda** — dialog-based editor |
| 1:N Bankovní účty | ✅ | ❌ | **Fakvio výhoda** — dialog-based editor |
| Fakturační nastavení per-klient | ✅ | ✅ | Fakvio: DueDays, prefix/suffix, platební metoda, účet |
| Jazyk klienta | ✅ | ❌ | **Fakvio výhoda** |
| Historie faktur na kartě klienta | ✅ | ✅ | |

### Příjmy a výdaje

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| Evidence přijatých faktur (výdaje) | ❌ | ✅ | **Fakvio chybí** |
| Přílohy k dokladům | ❌ | ✅ | **Fakvio chybí** — skeny/fotky přijatých dokladů |
| Příjmy vs. výdaje dashboard | ❌ | ✅ | **Fakvio chybí** |
| Ceník položek (Price Lists) | ❌ | ✅ | **Fakvio chybí** — auto-napovídání položek |

### Dashboard a reporty

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| Dashboard s KPI kartami | ✅ | ✅ | Fakvio: počty faktur, klientů, nezaplacené, DPH |
| Grafy (stav faktur, top klienti) | ✅ | ✅ | Fakvio: MudChart (Donut) |
| SysAdmin dashboard | ✅ | ❌ | **Fakvio výhoda** — logy, statistiky tenantů |
| Statistiky | ✅ | ✅ | |
| XML export pro daňový portál | ❌ | ✅ | **Fakvio chybí** |
| Hromadný export dokladů | 🔶 planned | ✅ | **Fakvio chybí** — Fakturovac: bulk PDF download pro účetní |
| Podklady pro daňové přiznání | ❌ | ✅ | **Fakvio chybí** |

### Autentizace a uživatelé

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| JWT autentizace | ✅ | ❌ | |
| OAuth (Google, Microsoft, FB, Seznam) | ✅ | ❌ | **Fakvio výhoda** |
| Dvoufaktorové ověření (TOTP + Email) | ✅ | ❌ | **Fakvio výhoda** |
| Self-registrace s email verifikací | ✅ | ✅ | |
| Správa uživatelů + pozvánky | ✅ | ❌ | **Fakvio výhoda** — role-based (User/Admin/SysAdmin) |
| Impersonace (SysAdmin) | ✅ | ❌ | **Fakvio výhoda** |

### Multi-tenancy a infrastruktura

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| Multi-tenant (schema-per-tenant) | ✅ | ❌ | **Fakvio výhoda** — izolace dat per firma |
| Tenant provisioning (9 kroků) | ✅ | ❌ | |
| Per-tenant SMTP | ✅ | ❌ | |
| Migration tool (single→multi) | ✅ | ❌ | |
| Azure SQL management | ✅ | ❌ | |
| Systémová konfigurace (SMTP, JWT) | ✅ | ❌ | |

### AI funkce

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| AI Chat asistent | ✅ | ❌ | **Fakvio výhoda** — Claude, OpenAI, Gemini, Ollama |
| AI nástroje (ARES lookup, vytvoření klienta/faktury) | ✅ | ❌ | **Fakvio výhoda** — natural language |
| AI navigace v aplikaci | ✅ | ❌ | |
| SSE streaming odpovědí | ✅ | ❌ | |
| Kontextové info (klienti, faktury, DPH) | ✅ | ❌ | |

### Integrace

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| ARES (obchodní rejstřík) | ✅ | ✅ | |
| Google Drive (auto-upload PDF) | ✅ | ❌ | **Fakvio výhoda** |
| OneDrive (auto-upload PDF) | ✅ | ❌ | **Fakvio výhoda** |
| Bankovní API (13 bank) | ❌ | ✅ | **Fakvio chybí** — Fakturovac: automatické párování plateb |
| API pro třetí strany | ✅ | ❌ | **Fakvio výhoda** — REST API (17 controllerů) |
| Účetní software (Pohoda, Money S3) | ❌ | ❌ | Ani jeden |
| E-shop integrace | ❌ | ❌ | Ani jeden |
| Payment gateway | ❌ | ❌ | Ani jeden |

### PWA a mobilní přístup

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| PWA (Add to Home Screen) | ✅ | ❌ | **Fakvio výhoda** — offline cache, service worker |
| MAUI Hybrid (nativní app) | ✅ | ❌ | **Fakvio výhoda** — Android, iOS, macOS, Windows |
| Mobilní přístup | ✅ | ❌ | Fakturovac: žádná mobilní app |

### Logování a observabilita

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| Application logging (DB) | ✅ | ❌ | |
| CorrelationId tracing | ✅ | ❌ | End-to-end: Blazor→API→AppLog→App Insights |
| Audit trail (CreatedBy/UpdatedBy) | ✅ | ❌ | |
| Log viewer UI | ✅ | ❌ | Filtry, stránkování, detail |

### Testování

| Feature | **Fakvio** | **Fakturovac** | Notes |
|---|:---:|:---:|---|
| Unit testy | ✅ (484) | N/A | xUnit + NSubstitute + Shouldly |
| Integrační testy | ✅ (5) | N/A | WebApplicationFactory |

---

## Summary: What Fakvio Has That Fakturovac Doesn't

1. **AI Chat Assistant** — multi-provider (Claude/OpenAI/Gemini/Ollama) s nástroji pro vytvoření klientů a faktur
2. **Multi-tenant architecture** — schema-per-tenant izolace, provisioning, per-tenant SMTP
3. **OAuth + 2FA** — Google, Microsoft, Facebook, Seznam.cz login + TOTP/Email OTP
4. **Cloud Storage** — auto-upload PDF na Google Drive / OneDrive
5. **PWA + MAUI** — offline přístup, nativní mobilní/desktopová aplikace
6. **Customizovatelné HTML šablony** — Quill WYSIWYG editor, Handlebars placeholders
7. **Per-client document language** — CZ/EN šablony automaticky dle jazyka klienta
8. **Multi-currency** — 8 měn (CZK, EUR, USD, GBP, PLN, CHF, HUF, RON)
9. **QR Faktura (SIND)** — plný český standard s CRC32 kontrolním součtem
10. **REST API** — 17 controllerů, použitelné třetími stranami
11. **Hromadné operace** — bulk vydání, úhrada, PDF, email, mazání
12. **Číselné řady** — konfigurovatelné formáty a sekvence
13. **Role-based access** — User / Admin / SysAdmin s impersonací
14. **Observabilita** — logging, correlation ID, audit trail, App Insights

## Summary: What Fakturovac Has That Fakvio Doesn't

1. **Zálohové faktury** — proforma s auto-převodem na ostrou fakturu
2. **Opakované faktury** — automatické vytváření a distribuce v intervalu
3. **Automatické upomínky** — email remindery po splatnosti s konfigurovatelným zpožděním
4. **Bankovní API párování** — 13 českých bank, auto-označení úhrady
5. **ISDOC formát** — český standard elektronické fakturace + ISDOC v PDF příloze
6. **Evidence přijatých faktur** — tracking výdajů s přílohami (skeny/fotky)
7. **Ceník položek** — price listy s auto-napovídáním do faktur
8. **Dodací listy** — generování dodacích listů
9. **Příjmový pokladní doklad** — generování pokladních dokladů
10. **XML export pro daňový portál** — měsíční DPH hlášení
11. **Přenesená daňová povinnost** — reverse charge fakturace
12. **Podklady pro daňové přiznání** — download podkladů
13. **Adresář dodavatelů** — evidence dodavatelů (suppliers)
14. **100% zdarma** — žádné limity, žádné placené plány

---

## Priority Backlog for Fakvio

Based on this comparison, the highest-impact missing features to implement:

### High Priority (competitive parity)
1. **Zálohové faktury (Advance/Proforma Invoices)** — core Czech invoicing feature
2. **Opakované faktury (Recurring Invoices)** — already planned in TODO.md
3. **Automatické upomínky (Payment Reminders)** — already planned in TODO.md
4. **ISDOC export** — Czech e-invoicing standard, increasingly required
5. **Evidence přijatých faktur (Expense Tracking)** — income vs. expense overview

### Medium Priority (nice-to-have)
6. **Bankovní API párování** — start with Fio Banka (open API), then expand
7. **Ceník položek (Price Lists)** — auto-suggest items during invoice creation
8. **Přenesená daňová povinnost (Reverse Charge)** — required for some B2B transactions
9. **XML export pro daňový portál** — useful for accountants

### Low Priority (minor features)
10. **Dodací listy / Pokladní doklady** — additional document types
11. **Vlastní podpis na faktuře** — image upload + placement
12. **BCC distribuce emailů** — simple SMTP enhancement
13. **Adresář dodavatelů** — separate supplier registry
