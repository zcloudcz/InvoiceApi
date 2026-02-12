# InvoiceApi - Fakturační systém

Moderní fakturační systém postavený na **.NET 10.0** s Clean Architecture, REST API backendem a Blazor Server frontendem.

## Architektura

Projekt využívá **Clean Architecture (Onion Architecture)** s jasným oddělením vrstev:

```
InvoiceApi.BlazorUI        ← Prezentační vrstva (Blazor + MudBlazor)
       │ HTTP/REST
InvoiceApi.API             ← API vrstva (ASP.NET Core Web API)
       │
InvoiceApi.Application     ← Aplikační vrstva (služby, DTOs, rozhraní)
       │
InvoiceApi.Infrastructure  ← Infrastrukturní vrstva (EF Core, repozitáře)
       │
InvoiceApi.Domain          ← Doménová vrstva (entity, enumy)
```

Doplňkové projekty:
- **AresService** - Integrace s českým registrem ARES
- **InvoiceApi.Tests.Unit** - Unit testy
- **InvoiceApi.Tests.Integration** - Integrační testy

## Technologie

| Vrstva | Technologie |
|--------|-------------|
| Backend | ASP.NET Core 10.0 Web API |
| Frontend | Blazor Server + MudBlazor 8.15 |
| Databáze | SQLite + Entity Framework Core 10.0 |
| Autentizace | JWT Bearer Token + BCrypt |
| PDF export | iText7 + iText7.pdfhtml (HTML → PDF) |
| Emailing | MailKit (SMTP) |
| HTML editor | Blazored.TextEditor (Quill WYSIWYG) |
| Dokumentace API | Swagger / OpenAPI |
| Integrace | ARES (český obchodní rejstřík) |
| Testy | xUnit + Moq + FluentAssertions |

## Hlavní funkce

- **Správa faktur** - CRUD, stavy (Draft → Completed → Paid), dobropisy, server-side stránkování a filtrování
- **PDF export** - generování PDF faktur z HTML šablon (iText7), stažení přes JS interop
- **Emailing** - odesílání faktur emailem s PDF přílohou přes SMTP (MailKit)
- **Dashboard** - přehled s reálnými statistikami (faktury za měsíc, nezaplacené částky, faktury po splatnosti)
- **Správa klientů** - evidence odběratelů, adresy, kontakty, detail klienta s historií faktur
- **Správa firem (issuerů)** - firmy vystavující faktury
- **Šablony faktur** - WYSIWYG HTML editor (Quill), Handlebars placeholdery, výchozí/vlastní šablony
- **Číselné řady** - konfigurovatelné formáty číslování dokladů
- **Sazby DPH** - správa sazeb s platností od/do
- **Multi-měna** - podpora CZK, EUR, USD, GBP, PLN
- **ARES integrace** - automatické doplnění údajů firmy dle IČO s cachováním

## API Endpoints

| Controller | Prefix | Popis |
|------------|--------|-------|
| AuthController | `/api/auth` | Přihlášení, validace a refresh tokenu |
| InvoiceController | `/api/invoice` | Faktury - CRUD, stavy, dobropisy, PDF export, email |
| ClientController | `/api/client` | Odběratelé - CRUD, adresy, kontakty, ARES |
| CompanyController | `/api/company` | Firmy/issueři (pouze SysAdmin) |
| DashboardController | `/api/dashboard` | Dashboard statistiky a přehledy |
| UserController | `/api/user` | Správa uživatelů |
| VatRateController | `/api/vatrate` | Sazby DPH |
| NumberSequenceController | `/api/numbersequence` | Číselné řady |
| CurrencyController | `/api/currency` | Měny |
| InvoiceTemplateController | `/api/invoicetemplate` | Šablony faktur |

## Doménový model

Klíčové entity:

- **Invoice** / **InvoiceTemplate** (TPH dědičnost) - faktura/šablona s položkami, vazbou na klienta a issuera
- **InvoiceItem** - řádky faktury (popis, množství, cena, DPH)
- **Client** - odběratel i issuer (rozlišeno příznakem `IsIssuer`)
- **Address** - adresy s typem (sídlo, doručovací apod.)
- **Contact** - kontakty (email, telefon) s typem a štítkem
- **BillingSettings** - platební podmínky, bankovní účty
- **User** - uživatel s rolí a vazbou na firmu
- **VatRate** - sazba DPH s platností
- **Currency** - měna (ISO 4217)
- **NumberSequence** / **NumberSequenceFormat** - číselné řady dokladů
- **AresCache** - cache ARES dotazů

## Autorizace

Tříúrovňový systém rolí:

| Role | Oprávnění |
|------|-----------|
| **User** | Práce s fakturami a klienty v rámci své firmy |
| **Admin** | Správa uživatelů a nastavení vlastní firmy |
| **SysAdmin** | Plný přístup – firmy, měny, systémová nastavení |

## Spuštění

### Požadavky
- .NET 10.0 SDK

### API server
```bash
cd InvoiceApi.API
dotnet run
```
API bude dostupné na `https://localhost:7001` se Swagger UI na root URL.

### Blazor UI
```bash
cd InvoiceApi.BlazorUI
dotnet run
```

### Výchozí přihlašovací údaje
- **Email:** `admin@zcloud.cz`
- **Heslo:** `Invoice123`

### Seedovaná data
Při startu se automaticky vytvoří:
- Výchozí SysAdmin uživatel
- Formáty číselných řad
- Číselné řady pro faktury a dobropisy
- České sazby DPH (21 %, 12 %, 0 %)
- Měny (CZK, EUR, USD, GBP, PLN)

## Klíčové patterny

- **Clean Architecture** - doména bez závislostí, inverze závislostí
- **Repository pattern** - abstrakce datového přístupu
- **Service layer** - veškerá business logika v servisní vrstvě
- **DTOs** - entity se nikdy neexponují přes API
- **Soft deletes** - příznak `IsActive` místo mazání
- **Automatické timestampy** - `CreatedAt` / `UpdatedAt` přes `SaveChanges`
- **Stránkování, filtrování, řazení** - standardizované přes `PagedResult<T>`
- **Multi-tenancy** - izolace dat na úrovni firmy přes `CompanyId` v JWT

## Testy

```bash
dotnet test
```

Aktuálně **34 unit testů** pokrývajících:
- **VatRateServiceTests** (16) - CRUD, výchozí sazby, validace, soft delete
- **PdfExportServiceTests** (4) - generování PDF, výchozí šablona, chybové stavy
- **EmailServiceTests** (3) - odesílání emailů, PDF příloha, SMTP konfigurace
- **DashboardServiceTests** (5) - agregace dat, počty klientů, nezaplacené částky, faktury po splatnosti

## Konfigurace

- **API:** `InvoiceApi.API/appsettings.json` - connection string, JWT nastavení, SMTP nastavení
- **Blazor UI:** `InvoiceApi.BlazorUI/appsettings.json` - URL API serveru

### SMTP nastavení (emailing)

V `appsettings.json` API projektu nakonfigurujte sekci `SmtpSettings`:

```json
{
  "SmtpSettings": {
    "Host": "smtp.example.com",
    "Port": 587,
    "Username": "user@example.com",
    "Password": "your-password",
    "SenderEmail": "invoices@example.com",
    "SenderName": "InvoiceApi",
    "UseSsl": true
  }
}
```

## Struktura solution

```
InvoiceApi.sln
├── InvoiceApi.Domain/              # Entity, enumy, base classes
├── InvoiceApi.Application/         # Rozhraní služeb, DTOs, společné utility
├── InvoiceApi.Infrastructure/      # EF Core, implementace služeb, migrace
├── InvoiceApi.API/                 # REST API, controllery, Program.cs
├── InvoiceApi.BlazorUI/            # Blazor Server UI, komponenty, stránky
├── AresService/                    # ARES integrace (samostatná knihovna)
├── InvoiceApi.Tests.Unit/          # Unit testy
└── InvoiceApi.Tests.Integration/   # Integrační testy
```
