# Upomínky (Payment Reminders / Dunning) — Architektura & Technický plán

## 1. Přehled

Automatický systém pro správu upomínek k fakturám po splatnosti. Každý den job projde nezaplacené faktury, porovná je s nastavením upomínek (company-level / client-level) a vytvoří záznamy o upomínkách. Upomínky lze odeslat emailem s PDF přílohou (faktura) a volitelně generovat jako PDF dokument (dopis s upomínkou).

### Klíčové vlastnosti
- **Multi-level escalation** — konfigurovatelný počet úrovní (1–5), každá s vlastním intervalem, poplatkem a šablonou
- **2-tier nastavení** — company-wide default + per-client override (klient vždy vyhrává)
- **Automatický denní job** — Azure Functions TimerTrigger, projde všechny tenanty
- **Handlebars šablony** — nové typy `ReminderPdf` a rozšířený `ReminderEmail` v `EContentTemplateType`
- **Audit trail** — každá upomínka = DB záznam s datem, úrovní, částkou, stavem odeslání
- **Zákonné úroky z prodlení** — automatický výpočet dle repo sazby ČNB + 8 p.b. (§ 1970 OZ)
- **Opt-out per client** — možnost u konkrétního klienta upomínky vypnout

---

## 2. Domain Layer

### 2.1 Entity: `ReminderSettings`

Společné nastavení upomínek — existuje na **dvou úrovních**:
1. **Company-level** — default pro všechny klienty tenanta (1:1 s tenantem, uloženo jako záznam s `ClientId = null`)
2. **Client-level** — override pro konkrétního klienta (1:1 s `ClientId`)

```
Fakvio.Domain/Entities/ReminderSettings.cs
```

| Property | Type | Description |
|---|---|---|
| `Id` | long | PK (BaseEntity) |
| `ClientId` | long? | Null = company default, non-null = per-client override |
| `IsEnabled` | bool | Master switch — false = žádné upomínky |
| `MaxReminderLevel` | int | Počet úrovní upomínek (1–5). Default: 3 |
| `GracePeriodDays` | int | Kolik dní po splatnosti počkat před 1. upomínkou. Default: 7 |
| `IncludeInterest` | bool | Připočítat zákonné úroky z prodlení. Default: false |
| `AttachInvoicePdf` | bool | Přiložit PDF faktury k emailu upomínky. Default: true |
| `AutoSendEmail` | bool | Automaticky odeslat email (false = jen vytvořit záznam, manuální odeslání). Default: true |

**Vztahy:**
- `Client?` — navigation property (null pro company default)
- `ICollection<ReminderLevel>` — 1:N, úrovně escalace

### 2.2 Entity: `ReminderLevel`

Definice jedné úrovně escalace v sekvenci upomínek.

```
Fakvio.Domain/Entities/ReminderLevel.cs
```

| Property | Type | Description |
|---|---|---|
| `Id` | long | PK (BaseEntity) |
| `ReminderSettingsId` | long | FK na `ReminderSettings` |
| `Level` | int | Pořadí úrovně (1, 2, 3, ...) |
| `DaysAfterPrevious` | int | Dny od předchozí úrovně (nebo od splatnosti u level 1). Default: 7 |
| `Subject` | string? | Custom email subject pro tuto úroveň (null = z šablony) |
| `FixedFeeCzk` | decimal | Pevný poplatek za tuto upomínku v CZK. Default: 0 |
| `EmailTemplateId` | long? | FK na `ContentTemplate` (type = ReminderEmail). Null = default šablona |
| `PdfTemplateId` | long? | FK na `ContentTemplate` (type = ReminderPdf). Null = default šablona |

**Příklad 3-level setup:**

| Level | DaysAfterPrevious | Efektivní den po splatnosti | Tón | FixedFee |
|---|---|---|---|---|
| 1 | 7 (= GracePeriodDays) | +7 | Přátelská připomínka | 0 CZK |
| 2 | 14 | +21 | Formální upomínka | 50 CZK |
| 3 | 14 | +35 | Poslední upozornění + předání k vymáhání | 200 CZK |

### 2.3 Entity: `Reminder`

Záznam o konkrétní odeslané/vytvořené upomínce k faktuře.

```
Fakvio.Domain/Entities/Reminder.cs
```

| Property | Type | Description |
|---|---|---|
| `Id` | long | PK (BaseEntity) |
| `InvoiceId` | long | FK na `Invoice` |
| `ClientId` | long | FK na `Client` (denormalizováno pro rychlý lookup) |
| `Level` | int | Úroveň upomínky (1, 2, 3, ...) |
| `Status` | `EReminderStatus` | Draft / Sent / Failed / Cancelled |
| `DueDate` | DateTime | Splatnost faktury (denormalizováno) |
| `ReminderDate` | DateTime | Datum vytvoření/odeslání upomínky |
| `InvoiceAmount` | decimal | Dlužná částka (denormalizováno) |
| `FeeCzk` | decimal | Poplatek za upomínku |
| `InterestCzk` | decimal | Vypočtené úroky z prodlení |
| `TotalCzk` | decimal | InvoiceAmount + FeeCzk + InterestCzk |
| `SentAt` | DateTime? | Kdy byl email odeslán (null = Draft/Failed) |
| `SentToEmail` | string? | Na jaký email byl odeslán |
| `ErrorMessage` | string? | Důvod selhání (pokud Status = Failed) |
| `Notes` | string? | Volitelné poznámky |

**Indexy:**
- `(InvoiceId, Level)` — unique, jedna úroveň upomínky per faktura
- `(ClientId)` — rychlý lookup všech upomínek klienta
- `(Status)` — filtrování Draft/Sent
- `(ReminderDate)` — řazení

### 2.4 Enum: `EReminderStatus`

```
Fakvio.Domain/Enums/EReminderStatus.cs
```

```csharp
public enum EReminderStatus
{
    Draft = 0,      // Vytvořena, čeká na odeslání (nebo manuální schválení)
    Sent = 1,       // Úspěšně odeslána emailem
    Failed = 2,     // Odeslání selhalo (SMTP chyba, neplatný email, ...)
    Cancelled = 3   // Zrušena uživatelem (faktura zaplacena mezitím, dobropis, ...)
}
```

### 2.5 Rozšíření `EContentTemplateType`

```csharp
// Existující:
ReminderEmail = 21,    // Email body pro upomínky (už existuje)

// Nový:
ReminderPdf = 3,       // PDF šablona pro upomínkový dopis
```

**Placeholders pro šablony upomínek:**

| Placeholder | Description |
|---|---|
| `{{CompanyName}}` | Jméno vystavovatele |
| `{{ClientName}}` | Jméno klienta/dlužníka |
| `{{InvoiceNumber}}` | Číslo faktury |
| `{{IssueDate}}` | Datum vystavení |
| `{{DueDate}}` | Datum splatnosti |
| `{{DaysOverdue}}` | Počet dní po splatnosti |
| `{{InvoiceAmount}}` | Dlužná částka |
| `{{CurrencyCode}}` | Měna (CZK, EUR, ...) |
| `{{ReminderLevel}}` | Úroveň upomínky (1, 2, 3) |
| `{{ReminderLevelText}}` | "První upomínka" / "Druhá upomínka" / "Poslední upozornění" |
| `{{Fee}}` | Poplatek za upomínku |
| `{{Interest}}` | Úroky z prodlení |
| `{{TotalDue}}` | Celková dlužná částka vč. poplatku a úroků |
| `{{InterestRate}}` | Aktuální sazba úroků z prodlení (%) |
| `{{PaymentDetails}}` | Platební údaje (účet, VS, ...) |
| `{{BankAccountNumber}}` | Číslo účtu |
| `{{IBAN}}` | IBAN |
| `{{VariableSymbol}}` | Variabilní symbol |

---

## 3. Application Layer

### 3.1 `IReminderService`

```
Fakvio.Application/Service/IReminderService.cs
```

```csharp
public interface IReminderService
{
    // ─── Settings CRUD ──────────────────────────────────────────────────
    // Company-level default (ClientId = null) + per-client overrides.

    /// Get effective settings for a client (client-level if exists, else company default).
    Task<ReminderSettingsDto?> GetEffectiveSettingsAsync(long? clientId, CancellationToken ct = default);

    /// Get or create company-level default settings.
    Task<ReminderSettingsDto> GetCompanySettingsAsync(CancellationToken ct = default);

    /// Update company-level or client-level settings (upsert).
    Task<ReminderSettingsDto> UpsertSettingsAsync(UpdateReminderSettingsDto dto, CancellationToken ct = default);

    /// Delete client-level override (reverts to company default).
    Task<bool> DeleteClientSettingsAsync(long clientId, CancellationToken ct = default);

    // ─── Reminder CRUD + actions ────────────────────────────────────────

    /// List reminders with pagination + filtering (by client, invoice, status, date range).
    Task<PagedResult<ReminderDto>> GetRemindersPagedAsync(ReminderFilterDto filter, CancellationToken ct = default);

    /// List reminders for a specific invoice.
    Task<List<ReminderDto>> GetByInvoiceAsync(long invoiceId, CancellationToken ct = default);

    /// Manually send a draft reminder.
    Task<ReminderDto> SendReminderAsync(long reminderId, CancellationToken ct = default);

    /// Cancel a reminder (e.g., invoice was paid or dispute resolved).
    Task<ReminderDto> CancelReminderAsync(long reminderId, string? notes = null, CancellationToken ct = default);

    // ─── Dunning job (called by Azure Functions TimerTrigger) ───────────

    /// Process all overdue invoices for a single tenant. Creates and optionally sends reminders.
    /// Returns the number of reminders created.
    Task<int> ProcessOverdueInvoicesAsync(CancellationToken ct = default);
}
```

### 3.2 `IInterestCalculator`

```
Fakvio.Application/Service/IInterestCalculator.cs
```

```csharp
public interface IInterestCalculator
{
    /// Calculates statutory late payment interest per Czech Civil Code § 1970.
    /// Rate = CNB repo rate (1st day of half-year when delay started) + 8 percentage points.
    /// Interest = principal × (rate / 100) × daysOverdue / 365.
    decimal Calculate(decimal principal, DateTime dueDate, DateTime calculationDate);
}
```

---

## 4. Contracts Layer

### 4.1 DTOs

```
Fakvio.Contracts/Dto/Reminder/
├── ReminderDto.cs                   — Read DTO pro Reminder entity
├── ReminderFilterDto.cs             — Filter pro paged listing
├── ReminderSettingsDto.cs           — Read DTO pro ReminderSettings + levels
├── UpdateReminderSettingsDto.cs     — Write DTO pro upsert settings
├── ReminderLevelDto.cs              — Read DTO pro ReminderLevel
└── UpdateReminderLevelDto.cs        — Write DTO pro level upsert
```

**ReminderSettingsDto:**

| Property | Type |
|---|---|
| `Id` | long |
| `ClientId` | long? |
| `ClientName` | string? |
| `IsEnabled` | bool |
| `MaxReminderLevel` | int |
| `GracePeriodDays` | int |
| `IncludeInterest` | bool |
| `AttachInvoicePdf` | bool |
| `AutoSendEmail` | bool |
| `Levels` | `List<ReminderLevelDto>` |

**ReminderDto:**

| Property | Type |
|---|---|
| `Id` | long |
| `InvoiceId` | long |
| `InvoiceNumber` | string? |
| `ClientId` | long |
| `ClientName` | string? |
| `Level` | int |
| `Status` | EReminderStatus |
| `DueDate` | DateTime |
| `ReminderDate` | DateTime |
| `InvoiceAmount` | decimal |
| `FeeCzk` | decimal |
| `InterestCzk` | decimal |
| `TotalCzk` | decimal |
| `SentAt` | DateTime? |
| `SentToEmail` | string? |
| `ErrorMessage` | string? |
| `Notes` | string? |
| `CreatedAt` | DateTime |

---

## 5. Infrastructure Layer

### 5.1 `ReminderService` — Dunning Logic

Hlavní logika v `ProcessOverdueInvoicesAsync()`:

```
FOR EACH invoice WHERE Status IN (Completed) AND NOT Paid AND DueDate < today:
    1. Resolve effective settings (client override > company default)
    2. IF settings.IsEnabled == false → SKIP
    3. Get existing reminders for this invoice (ORDER BY Level DESC)
    4. Determine current level = max existing level + 1
    5. IF current level > settings.MaxReminderLevel → SKIP (escalation maxed out)
    6. Calculate effective date for next level:
       - Level 1: DueDate + GracePeriodDays
       - Level N: LastReminderDate + Level[N].DaysAfterPrevious
    7. IF today < effective date → SKIP (not yet time for next reminder)
    8. Calculate fee (from ReminderLevel)
    9. Calculate interest (if settings.IncludeInterest)
   10. CREATE Reminder record (Status = Draft or Sent based on AutoSendEmail)
   11. IF AutoSendEmail:
       - Resolve email template (level-specific or default ReminderEmail)
       - Render with Handlebars placeholders
       - Optionally attach invoice PDF (if settings.AttachInvoicePdf)
       - Send via IEmailService
       - Update Status = Sent, SentAt, SentToEmail
       - On failure: Status = Failed, ErrorMessage
```

### 5.2 `InterestCalculator`

```
Fakvio.Infrastructure/Service/InterestCalculator.cs
```

Implementuje výpočet zákonných úroků z prodlení dle § 1970 občanského zákoníku:
- Sazba = repo sazba ČNB k 1. dni pololetí, ve kterém prodlení vzniklo + 8 p.b.
- Úrok = jistina × (sazba / 100) × počet_dní_prodlení / 365
- Hardcoded tabulka repo sazeb ČNB (aktualizovat 2× ročně: 1.1. a 1.7.)
- Fallback: pokud sazba pro dané období chybí, použij poslední známou

### 5.3 EF Core Configuration

**TenantDbContext** — nové DbSets:
- `DbSet<ReminderSettings> ReminderSettings`
- `DbSet<ReminderLevel> ReminderLevel`
- `DbSet<Reminder> Reminder`

**Fluent config:**
- `ReminderSettings`: unique index `(ClientId)` kde `ClientId IS NOT NULL` + jeden záznam s `ClientId = null` per tenant
- `ReminderLevel`: composite unique `(ReminderSettingsId, Level)`
- `Reminder`: unique `(InvoiceId, Level)`, indexes on `(ClientId)`, `(Status)`, `(ReminderDate)`

### 5.4 DI Registration

```csharp
services.AddScopedWithLogging<IReminderService, ReminderService>();
services.AddSingleton<IInterestCalculator, InterestCalculator>();
```

---

## 6. API Layer

### 6.1 `ReminderController`

```
Fakvio.API/Controller/ReminderController.cs
[Route("api/reminder")]
```

| Endpoint | Method | Description |
|---|---|---|
| `GET /api/reminder/settings` | GET | Get company-level default settings |
| `GET /api/reminder/settings/client/{clientId}` | GET | Get effective settings for a client |
| `PUT /api/reminder/settings` | PUT | Upsert company or client settings |
| `DELETE /api/reminder/settings/client/{clientId}` | DELETE | Delete client override |
| `GET /api/reminder/paged` | GET | List reminders (paged, filtered) |
| `GET /api/reminder/invoice/{invoiceId}` | GET | List reminders for an invoice |
| `POST /api/reminder/{id}/send` | POST | Manually send a draft reminder |
| `POST /api/reminder/{id}/cancel` | POST | Cancel a reminder |

### 6.2 `ReminderSettingsController` (optional, or merge into above)

Pokud by settings CRUD narůstal, může být separátní controller.

---

## 7. Azure Functions — Daily Job

### 7.1 `ReminderTimerFunction`

```
Fakvio.Functions/TimerFunctions.cs  (přidání do existujícího souboru)
```

```csharp
/// <summary>
/// Runs daily at 6:00 AM UTC. Iterates all active tenants,
/// creates a scoped TenantDbContext for each, and calls
/// IReminderService.ProcessOverdueInvoicesAsync().
///
/// CRON: "0 0 6 * * *" (second minute hour dayOfMonth month dayOfWeek)
///
/// Why 6 AM? Most Czech businesses start at 8 AM CET (6 AM UTC in winter,
/// 4 AM UTC in summer). Running at 6 UTC ensures reminders are created
/// and emails sent before the workday starts in both CET and CEST.
/// </summary>
[Function("ProcessReminders")]
public async Task ProcessReminders(
    [TimerTrigger("0 0 6 * * *")] TimerInfo timer)
{
    // 1. Get all active, provisioned tenants from master DB
    // 2. For each tenant:
    //    a. Create scoped TenantDbContext with the tenant's schema
    //    b. Resolve IReminderService within the scope
    //    c. Call ProcessOverdueInvoicesAsync()
    //    d. Log result count
    // 3. Aggregate and log total (e.g., "Created 42 reminders across 15 tenants")
}
```

### 7.2 Multi-tenant iteration pattern

Stejný pattern jako existující `LogCleanup` — iterace přes `CompanySystemSettings` z master DB:

```csharp
var tenants = await _masterContext.CompanySystemSettings
    .AsNoTracking()
    .Where(s => s.IsProvisioned && s.IsActive)
    .ToListAsync(ct);

foreach (var tenant in tenants)
{
    using var scope = _serviceProvider.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
    var tenantContext = await factory.CreateContextAsync(tenant.CompanyId, ct);
    // ... resolve service, process reminders ...
}
```

---

## 8. UI Layer

### 8.1 Nové stránky

**`/reminders`** — Přehled upomínek (MudDataGrid, server-side pagination):
- Filtry: Status, Client, Date range, Level
- Sloupce: Datum, Faktura, Klient, Úroveň, Částka, Stav, Akce (Send/Cancel)
- DateColumnFilter na ReminderDate

**`/reminders/settings`** — Nastavení upomínek:
- **Company default** karta — formulář s globálním nastavením + tabulka levels
- **Per-client overrides** tabulka — kteří klienti mají vlastní nastavení + odkaz na edit

### 8.2 Integrace do existujících stránek

**`InvoiceDetail.razor`** — nová sekce "Upomínky" (pod přílohami):
- Timeline/seznam upomínek odeslané k této faktuře
- Tlačítko "Vytvořit upomínku ručně"

**`ClientDetail.razor`** — nová sekce "Nastavení upomínek":
- Indikátor: "Výchozí nastavení" nebo "Vlastní nastavení"
- Tlačítko "Upravit nastavení upomínek" → dialog s formulářem

### 8.3 Blazor API Services

```
Fakvio.UI.Shared/Services/ReminderApiService.cs
```

Extends `ApiClientBase`. Metody: `GetSettingsAsync`, `UpsertSettingsAsync`, `GetRemindersPagedAsync`, `GetByInvoiceAsync`, `SendAsync`, `CancelAsync`.

### 8.4 Shared komponenty

**`ReminderTimeline.razor`** — timeline/list upomínek pro fakturu (použito v InvoiceDetail):
```razor
<ReminderTimeline InvoiceId="_invoice.Id" />
```

**`ReminderSettingsEditor.razor`** — formulář pro editaci nastavení + levels (reusable v settings page i client detail dialogu):
```razor
<ReminderSettingsEditor ClientId="null" />  @* Company default *@
<ReminderSettingsEditor ClientId="_client.Id" />  @* Per-client *@
```

**`ReminderLevelEditor.razor`** — inline editable tabulka úrovní (similar pattern to InvoiceItemEditor):
| Úroveň | Dny po předchozí | Poplatek (CZK) | Email šablona | PDF šablona |
|---|---|---|---|---|
| 1 | 7 | 0 | Default | — |
| 2 | 14 | 50 | — | — |
| 3 | 14 | 200 | Poslední upozornění | Dopis - upomínka |

---

## 9. Šablony

### 9.1 Rozšíření `EContentTemplateType`

```csharp
ReminderPdf = 3,      // NOVÝ — PDF dopis s upomínkou
ReminderEmail = 21,    // UŽ EXISTUJE — email body upomínky
```

### 9.2 Default šablony (seed data)

**Default Reminder Email** (Czech, friendly tone):
```html
<div style="font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;">
    <h2 style="color: #E65100;">Upomínka č. {{ReminderLevel}} — Faktura {{InvoiceNumber}}</h2>
    <p>Vážený zákazníku,</p>
    <p>dovolujeme si Vás upozornit, že faktura <strong>{{InvoiceNumber}}</strong>
       je {{DaysOverdue}} dní po splatnosti.</p>
    <table style="width: 100%; border-collapse: collapse; margin: 20px 0;">
        <tr><td>Dlužná částka:</td><td><strong>{{InvoiceAmount}} {{CurrencyCode}}</strong></td></tr>
        <tr><td>Splatnost:</td><td>{{DueDate}}</td></tr>
        {{#if Interest}}<tr><td>Úroky z prodlení:</td><td>{{Interest}} CZK</td></tr>{{/if}}
        {{#if Fee}}<tr><td>Poplatek za upomínku:</td><td>{{Fee}} CZK</td></tr>{{/if}}
    </table>
    <p>Prosíme o úhradu na účet <strong>{{BankAccountNumber}}</strong>,
       VS: <strong>{{VariableSymbol}}</strong>.</p>
    <p>Pokud jste platbu již odeslali, považujte tento email za bezpředmětný.</p>
    <hr style="border: none; border-top: 1px solid #eee; margin: 20px 0;" />
    <p style="color: #999; font-size: 12px;">{{CompanyName}}</p>
</div>
```

**Default Reminder PDF** — formální dopis na hlavičkovém papíře s razítkem firmy, adresou klienta, tabulkou dlužných faktur, informací o úrocích a poplatcích.

### 9.3 Multi-level šablony

Každá úroveň (`ReminderLevel`) může mít vlastní `EmailTemplateId` a `PdfTemplateId`. Pokud je null, použije se default šablona pro daný typ (`EContentTemplateType.ReminderEmail` / `ReminderPdf`). Tón šablon typicky eskaluje:

| Level | Tón | Typický subject |
|---|---|---|
| 1 | Přátelská připomínka | "Připomínka — Faktura {{InvoiceNumber}} po splatnosti" |
| 2 | Formální upomínka | "2. upomínka — Faktura {{InvoiceNumber}}" |
| 3 | Poslední upozornění | "POSLEDNÍ UPOZORNĚNÍ — Faktura {{InvoiceNumber}} — {{DaysOverdue}} dní po splatnosti" |

---

## 10. Úroky z prodlení

### 10.1 Zákonná sazba (§ 1970 OZ, § 2 nařízení vlády č. 351/2013 Sb.)

```
Sazba = repo sazba ČNB (1. den pololetí vzniku prodlení) + 8 p.b.
```

### 10.2 Repo sazby ČNB (hardcoded lookup tabulka)

| Období | Repo sazba | Úrok z prodlení |
|---|---|---|
| 2H 2024 | 4.75% | 12.75% |
| 1H 2025 | 4.00% | 12.00% |
| 2H 2025 | 3.75% | 11.75% |
| 1H 2026 | 3.50% | 11.50% |

**Udržovatelnost**: Tabulka se aktualizuje 2× ročně. Implementace jako `Dictionary<(int Year, int HalfYear), decimal>` v `InterestCalculator`. Pro neznámé období (budoucnost) se použije poslední známá sazba.

### 10.3 Výpočet

```
Úrok = jistina × (sazba / 100) × počet_dní / 365
```

Příklad: Faktura 50 000 CZK, 30 dní po splatnosti, sazba 11.50%:
```
Úrok = 50 000 × (11.50 / 100) × 30 / 365 = 472.60 CZK
```

---

## 11. Workflow

### 11.1 Automatický (daily job)

```
06:00 UTC — Azure Functions TimerTrigger "ProcessReminders"
    │
    ├─ Pro každý aktivní tenant:
    │   │
    │   ├─ Načti všechny Completed + nezaplacené faktury
    │   │
    │   ├─ Pro každou fakturu po splatnosti:
    │   │   ├─ Resolve settings (client override > company default)
    │   │   ├─ Zkontroluj, zda je čas na další úroveň
    │   │   ├─ Vytvoř Reminder (Draft nebo Sent)
    │   │   └─ Odešli email (pokud AutoSendEmail)
    │   │
    │   └─ Log: "Tenant {id}: Created {n} reminders"
    │
    └─ Log: "Total: {n} reminders across {t} tenants"
```

### 11.2 Manuální

1. Uživatel jde na `/reminders` nebo `InvoiceDetail`
2. Vidí faktury s Draft/neslanými upomínkami
3. Klikne "Odeslat" → POST `/api/reminder/{id}/send`
4. Nebo vytvoří upomínku ručně mimo automatický cyklus

### 11.3 Zastavení upomínek

Upomínky se **automaticky zastaví** když:
- Faktura je zaplacená (`Status = Paid`) → job ji přeskočí
- Faktura je zrušená (`Status = Deleted`)
- Dosažena `MaxReminderLevel` → job ji přeskočí
- `ReminderSettings.IsEnabled = false`

Uživatel může ručně zrušit konkrétní upomínku → `Status = Cancelled` + poznámka.

---

## 12. Localization

### Nové resource klíče (CZ + EN)

```
// Settings
Reminder_Settings, Reminder_CompanyDefault, Reminder_ClientOverride
Reminder_IsEnabled, Reminder_MaxLevel, Reminder_GracePeriod
Reminder_IncludeInterest, Reminder_AttachPdf, Reminder_AutoSend

// Levels
Reminder_Level, Reminder_DaysAfterPrevious, Reminder_Fee
Reminder_EmailTemplate, Reminder_PdfTemplate

// List / Detail
Reminder_Title, Reminder_Invoice, Reminder_Client
Reminder_Status, Reminder_Date, Reminder_Amount
Reminder_Interest, Reminder_Total, Reminder_SentAt
Reminder_SentTo, Reminder_Error, Reminder_Notes

// Actions
Reminder_Send, Reminder_Cancel, Reminder_CreateManual

// Status enum
EReminderStatus_Draft, EReminderStatus_Sent
EReminderStatus_Failed, EReminderStatus_Cancelled

// Level names
Reminder_Level1, Reminder_Level2, Reminder_Level3
(= "1. upomínka", "2. upomínka", "Poslední upozornění")
```

---

## 13. Bezpečnost

- Všechny endpointy `[Authorize]` — tenant isolation automatická přes JWT CompanyId
- Upomínky žijí v **tenant DB** — zero cross-tenant exposure
- Email se odesílá přes existující `IEmailService` (respektuje SMTP 3-tier fallback)
- PDF se generuje přes existující `IPdfExportService`
- Úroky z prodlení se **pouze informují** — neúčtují se automaticky jako nová faktura

---

## 14. Testing

### 14.1 Unit Tests

```
Tests/Reminder/ReminderServiceTests.cs        — 15+ testů
Tests/Reminder/InterestCalculatorTests.cs      — 8+ testů
```

**ReminderService test scenarios:**
- Faktura po splatnosti, settings enabled → vytvoří level 1 reminder
- Faktura s existující level 1 reminder, čas na level 2 → vytvoří level 2
- Faktura s existující level 1 reminder, ještě ne čas → přeskočí
- MaxReminderLevel dosažena → přeskočí
- Settings disabled → přeskočí
- Client override nastavení → použije client settings
- Faktura zaplacena → přeskočí
- AutoSendEmail = true → odešle email
- AutoSendEmail = false → jen Draft
- Email send failure → Status = Failed + ErrorMessage
- Interest calculation correct
- Fee from ReminderLevel applied correctly
- Cancel reminder → status change
- Manual send draft → status Sent

**InterestCalculator test scenarios:**
- Známé období (1H 2026) → správná sazba 11.50%
- Přechod pololetí (prodlení začne v 1H, počítáme v 2H)
- Neznámé budoucí období → fallback na poslední známou sazbu
- Nulová jistina → nulový úrok
- Jednoduchý výpočet → 50000 CZK, 30 dní, 11.50% = 472.60 CZK
- Přestupný rok (366 dní)
- Datum splatnosti = dnes → 0 dní → 0 úrok
- Datum splatnosti v budoucnosti → 0 úrok

---

## 15. NuGet Packages

Žádné nové NuGet balíčky — vše používá existující infrastrukturu (MailKit, iText7, EF Core).

---

## 16. EF Migrations

```bash
# Tenant migration — ReminderSettings, ReminderLevel, Reminder tables + EContentTemplateType seed
dotnet ef migrations add AddReminders -c TenantDbContext -p Fakvio.Infrastructure -s Fakvio.API
```

---

## 17. Implementační pořadí

| Step | Layer | Popis | Závisí na |
|---|---|---|---|
| **1** | Domain | `EReminderStatus`, `ReminderSettings`, `ReminderLevel`, `Reminder` entity | — |
| **2** | Domain | Rozšíření `EContentTemplateType` + `ReminderPdf = 3` | — |
| **3** | Contracts | DTOs: `ReminderDto`, `ReminderFilterDto`, `ReminderSettingsDto`, `UpdateReminderSettingsDto`, `ReminderLevelDto` | Step 1 |
| **4** | Application | `IReminderService`, `IInterestCalculator` | Steps 1, 3 |
| **5** | Infrastructure | TenantDbContext: DbSets + Fluent config | Step 1 |
| **6** | Infrastructure | EF Migration `AddReminders` | Step 5 |
| **7** | Infrastructure | `InterestCalculator` + unit testy | Step 4 |
| **8** | Infrastructure | `ReminderService` — settings CRUD | Steps 4, 5 |
| **9** | Infrastructure | `ReminderService` — `ProcessOverdueInvoicesAsync` dunning logic | Steps 7, 8 |
| **10** | Infrastructure | DI registration | Steps 8, 9 |
| **11** | Infrastructure | Default šablony (seed data): `ReminderPdf` + rozšířený `ReminderEmail` | Step 2 |
| **12** | API | `ReminderController` | Steps 4, 3 |
| **13** | Functions | `ProcessReminders` TimerTrigger | Step 9 |
| **14** | UI | `ReminderApiService` | Step 3 |
| **15** | UI | `/reminders` stránka (grid + filtry) | Step 14 |
| **16** | UI | `/reminders/settings` stránka | Step 14 |
| **17** | UI | `ReminderTimeline.razor` + integrace do `InvoiceDetail` | Step 14 |
| **18** | UI | `ReminderSettingsEditor.razor` + integrace do `ClientDetail` | Step 14 |
| **19** | Localization | CZ + EN resource klíče | Step 15 |
| **20** | Tests | Unit testy: `ReminderServiceTests`, `InterestCalculatorTests` | Steps 7, 9 |

---

## 18. Blob struktura upomínek

Pokud se upomínka generuje jako PDF (dopis), ukládá se jako file attachment:

```
fakvio-files/
└── 42/
    └── {fileGuid}.pdf     ← FileAttachment(EntityName="Reminder", RecordId=reminderId)
```

Stejný systém jako pro ostatní entity — `FileAttachmentManager` na `ReminderDetail` stránce funguje out-of-the-box.

---

## 19. Budoucí rozšíření

- **SMS upomínky** — přidání SMS provideru (Twilio, SMS Operator) vedle emailu
- **Automatické účtování úroků** — vytvoření nové faktury s úroky z prodlení
- **Integrace s vymáháním** — export do CSV/XML pro inkasní agentury
- **Pre-due reminders** — přátelská připomínka X dní PŘED splatností
- **Dashboard widget** — počet overdue faktur, odeslaných upomínek, inkasovaná částka po upomínce
- **Webhook/notification** — notifikace do Slacku/Teams při vytvoření upomínky
