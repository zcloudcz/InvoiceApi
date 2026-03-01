# Status migrace na Currency FK a Invoice Templates

## ✅ DOKONČENO

### Domain Layer
- ✅ Vytvořena `Currency` entita s ISO kódy, symboly, formáty
- ✅ Upravena `Invoice` entita - Currency změněno z string na FK (CurrencyId + navigation property)
- ✅ `Invoice` entity upraveny nullable properties pro Template support (DocumentNumber, IssueDate, DueDate, ClientId)
- ✅ Vytvořena `InvoiceTemplate` entita jako derived class z `Invoice`

### Data Layer (ApplicationDbContext)
- ✅ Přidán `DbSet<Currency>`
- ✅ Přidán `DbSet<InvoiceTemplate>` jako query helper
- ✅ `ConfigureCurrency()` - Currency table configuration
- ✅ `ConfigureInvoice()` - přidán TPH Discriminator (Invoice/Template)
- ✅ Currency FK relationship v Invoice
- ✅ ClientId změněn na nullable (.IsRequired(false))
- ✅ InvoiceTemplate specifické property konfigurace (Name, Description, Category)
- ✅ Seed data pro 5 měn (CZK, EUR, USD, GBP, PLN)

---

## 🔧 POTŘEBUJE OPRAVU - InvoiceService.cs

### Chyby k opravení:

1. **Řádek ~225, 349**: `invoice.Currency = createDto.Currency;`
   - **Oprava**: Změnit na `invoice.CurrencyId = createDto.CurrencyId;`
   - Vyžaduje také změnu v DTOs - `CreateInvoiceDto.Currency` na `CurrencyId` (long)

2. **Řádek ~119**: `.ToLower()` syntax error
   - **Kontext**: Pravděpodobně v Currency filter query
   - **Oprava**: Zkontrolovat použití `.ToLower()`

3. **Řádek ~523**: `ClientId` nullable error
   - **Oprava**: Přidat null check nebo použít `ClientId.Value`

4. **Řádek ~614, 632**: DateTime nullable conversion
   - **Oprava**: Přidat null checks nebo použít `.Value`

5. **Řádek ~643, 649**: `IssueDate.Year` - IssueDate je nyní nullable
   - **Oprava**: `invoice.IssueDate.Value.Year` nebo `invoice.IssueDate?.Year ?? DateTime.UtcNow.Year`

6. **Řádek ~669, 670, 672**: Various nullable conversions
   - **Oprava**: Přidat explicitní převody nebo null checks

7. **Řádek ~688**: `Currency` object to string conversion (v MapToDto)
   - **Oprava**: Změnit na `Currency = invoice.Currency.Code` v DTO mappingu

---

## 🔧 POTŘEBUJE OPRAVU - DTOs

### CreateInvoiceDto
```csharp
// BYLO:
public string Currency { get; set; } = "CZK";

// MÁ BÝT:
public long CurrencyId { get; set; } // FK to Currency table
```

### UpdateInvoiceDto
```csharp
// BYLO:
public string? Currency { get; set; }

// MÁ BÝT:
public long? CurrencyId { get; set; }
```

### InvoiceDto (response)
```csharp
// BYLO:
public string Currency { get; set; } = "CZK";

// MÁ BÝT:
public long CurrencyId { get; set; }
public string CurrencyCode { get; set; } = string.Empty; // e.g., "CZK"
public string CurrencySymbol { get; set; } = string.Empty; // e.g., "Kč"
// Optional: Include full Currency object
public CurrencyDto? Currency { get; set; }
```

---

## 📋 ZBÝVAJÍCÍ KROKY

### 1. Opravit DTOs (Priority: HIGH)
- [ ] Vytvořit `CurrencyDto`
- [ ] Upravit `CreateInvoiceDto` - Currency→CurrencyId
- [ ] Upravit `UpdateInvoiceDto` - Currency→CurrencyId
- [ ] Upravit `InvoiceDto` - přidat CurrencyCode, CurrencySymbol nebo celý Currency object

### 2. Opravit InvoiceService.cs (Priority: HIGH)
- [ ] Opravit všech 12 compiler errors
- [ ] UpdateTimestamps metoda - ošetřit nullable DateTime properties
- [ ] MapToDto - mapovat Currency object na DTO properly

### 3. Opravit další služby používající Invoice
- [ ] Zkontrolovat `ClientService` - používá Currency?
- [ ] Zkontrolovat `NumberSequenceService` - používá IssueDate?

### 4. Vytvořit Migration (po opravě build errors)
```bash
dotnet ef migrations add AddCurrencyAndTemplateSupport \
  --project Fakvio.Infrastructure \
  --startup-project Fakvio.API
```

### 5. Upravit Migration pro Data Migration
Migration automaticky:
- Přidá Currency table
- Přidá CurrencyId column do Invoice
- Přidá InvoiceType discriminator
- Přidá InvoiceTemplate columns (Name, Description, etc.)

**MUSÍME RUČNĚ PŘIDAT** do Up() metody:
```csharp
// Set default CurrencyId for existing invoices
// Assume existing invoices use CZK (Id = 1)
migrationBuilder.Sql(@"
    UPDATE Invoice
    SET CurrencyId = 1
    WHERE CurrencyId IS NULL OR CurrencyId = 0
");

// Set InvoiceType discriminator for existing records
migrationBuilder.Sql(@"
    UPDATE Invoice
    SET InvoiceType = 'Invoice'
    WHERE InvoiceType IS NULL
");
```

### 6. Currency DTOs a Service
- [ ] `CurrencyDto`, `CreateCurrencyDto`, `UpdateCurrencyDto`
- [ ] `ICurrencyService` interface
- [ ] `CurrencyService` implementation (CRUD + pagination)
- [ ] `CurrencyController` (SysAdmin only)

### 7. InvoiceTemplate DTOs a Service
- [ ] `InvoiceTemplateDto`, `CreateInvoiceTemplateDto`, `UpdateInvoiceTemplateDto`
- [ ] `CreateInvoiceFromTemplateDto`
- [ ] `IInvoiceTemplateService` interface
- [ ] `InvoiceTemplateService` implementation
  - CRUD operations
  - `CreateInvoiceFromTemplateAsync()`
  - `CreateTemplateFromInvoiceAsync()`
- [ ] `InvoiceTemplateController`

### 8. Update Controllers
- [ ] `InvoiceController` - endpoints přijímají CurrencyId místo Currency string
- [ ] Přidat validaci - CurrencyId musí existovat v Currency table

### 9. Blazor UI Updates
- [ ] Currency management page (SysAdmin)
- [ ] Update Invoice create/edit forms - Currency dropdown (místo textbox)
- [ ] InvoiceTemplate management pages
- [ ] "Create from template" dialog

---

## 🎯 DOPORUČENÝ POSTUP

1. **Fáze 1**: Opravit DTOs a build errors (20 min)
2. **Fáze 2**: Vytvořit a upravit migration (15 min)
3. **Fáze 3**: Aplikovat migration a testovat (10 min)
4. **Fáze 4**: Implementovat Currency service & controller (30 min)
5. **Fáze 5**: Implementovat InvoiceTemplate service & controller (60 min)
6. **Fáze 6**: UI updates (60 min)

---

## ⚠️ BREAKING CHANGES

### API Contracts
- `POST /api/invoice` - request body nyní očekává `CurrencyId` (long) místo `Currency` (string)
- `PUT /api/invoice/{id}` - stejně jako výše
- `GET /api/invoice/{id}` - response nyní obsahuje `CurrencyId`, `CurrencyCode`, `CurrencySymbol`

### Database Schema
- Invoice table - přidán `CurrencyId` column (FK)
- Invoice table - odstraněn `Currency` column (string)
- Invoice table - přidán `InvoiceType` discriminator column
- Invoice table - přidány Template-specific columns
- Nová Currency table

---

## ✅ HOTOVO - Kompletní implementace dokončena

**Poslední aktualizace:** 2026-01-08 13:00
**Status:** ✅ **BUILD SUCCESSFUL** | ✅ **MIGRATION APPLIED** | ✅ **ALL TESTS PASSING (22/22)**

### Dokončené kroky - Core:
- ✅ Currency jako master data (5 měn: CZK, EUR, USD, GBP, PLN)
- ✅ Invoice.CurrencyId FK implementováno
- ✅ InvoiceTemplate jako derived class (TPH pattern)
- ✅ Client.PreferredCurrencyId přidáno
- ✅ Všechny DTOs opraveny (CurrencyId místo Currency string)
- ✅ InvoiceService opraveno (všechny build errors vyřešeny)
- ✅ Blazor UI aktualizováno (Invoices.razor)
- ✅ Testy opraveny a všechny procházejí
- ✅ Migration vytvořena a aplikována s data migration SQL

### Dokončené kroky - Currency Management API:
- ✅ CurrencyDto, CreateCurrencyDto, UpdateCurrencyDto
- ✅ ICurrencyService interface
- ✅ CurrencyService implementation (CRUD + pagination)
- ✅ CurrencyController (SysAdmin only)
- ✅ DI registrace

### Dokončené kroky - InvoiceTemplate Management API:
- ✅ InvoiceTemplateDto, CreateInvoiceTemplateDto, UpdateInvoiceTemplateDto, CreateInvoiceFromTemplateDto
- ✅ IInvoiceTemplateService interface
- ✅ InvoiceTemplateService implementation
  - ✅ CRUD operations s pagination
  - ✅ CreateInvoiceFromTemplateAsync()
  - ✅ CreateTemplateFromInvoiceAsync()
- ✅ InvoiceTemplateController s API endpoints
- ✅ DI registrace

### API Endpoints přidány:

**Currency Management** (api/currency):
- GET /active - aktivní měny pro dropdowny
- GET / - stránkované měny s filtry
- GET /{id} - detail měny
- GET /code/{code} - měna podle kódu
- POST / - vytvoření měny (SysAdmin)
- PUT /{id} - aktualizace měny (SysAdmin)
- DELETE /{id} - smazání měny (SysAdmin)

**InvoiceTemplate Management** (api/invoicetemplate):
- GET /active - aktivní šablony
- GET / - stránkované šablony s filtry
- GET /{id} - detail šablony
- POST / - vytvoření šablony
- PUT /{id} - aktualizace šablony
- DELETE /{id} - smazání šablony (soft delete)
- POST /{id}/create-invoice - vytvoření faktury ze šablony
- POST /from-invoice/{invoiceId} - vytvoření šablony z faktury

### Dokončené kroky - Blazor UI:
- ✅ Blazor UI stránka pro Currency management (SysAdmin) - Currencies.razor
  - Full CRUD dialog s validací
  - Server-side pagination
  - Search a active/inactive filtering
- ✅ Blazor UI stránka pro InvoiceTemplate management - InvoiceTemplates.razor
  - List view s filtrací (search, document type, active/inactive)
  - "Create Invoice from Template" dialog - plně funkční
  - **Create dialog - PLNĚ FUNKČNÍ s editorem položek**
  - **Edit dialog - PLNĚ FUNKČNÍ s editorem položek včetně úpravy existujících položek**
- ✅ InvoiceItemEditor komponenta - Components/Shared/InvoiceItemEditor.razor
  - Přidání/úprava/mazání položek faktury
  - Validace vstupů (min. 1 položka required)
  - Výpočet součtů (bez DPH, DPH, s DPH) - real-time preview
  - Dropdown pro výběr DPH sazeb z API (načítání z /api/vatrate/active)
  - Automatické přiřazení DPH % při výběru sazby
  - Podpora jak pro Create tak Edit mode šablon
- ✅ Navigation menu aktualizováno
  - Měny pod Nastavení (SysAdmin only)
  - Šablony faktur pod Fakturace

### Kompletní feature set - Invoice Templates:
**Vytvoření šablony (2 způsoby):**

*Způsob 1: Vytvořit novou šablonu od základu*
1. Název, popis, kategorie
2. Výběr vystavitele (issuers)
3. Výběr měny
4. Nastavení splatnosti (dny)
5. **Editor položek faktury** - CRUD operace nad items
6. Validace min. 1 položky

*Způsob 2: Vytvořit šablonu z existující faktury*
1. Kliknutí na tlačítko "Vytvořit šablonu" u faktury v Invoices.razor
2. Zadání názvu, popisu a kategorie šablony
3. Automatické zkopírování všech položek a nastavení z faktury
4. ✅ **Implementováno v UI** - tlačítko vedle každé faktury

**Úprava šablony:**
1. Úprava všech metadata (název, popis, kategorie, splatnost, měna)
2. **Úprava položek** - plně funkční CRUD
3. Aktivní/Neaktivní přepínač
4. Validace min. 1 položky

**Použití šablony:**
1. Vytvoření faktury ze šablony
2. Výběr klienta
3. Nastavení data vystavení a splatnosti
4. Auto-complete option
5. Tracking usage (UsageCount, LastUsedAt)

**Zobrazení:**
- Server-side pagination
- Filtry: search, document type, active/inactive
- Statistiky použití v tabulce

### Zbývá implementovat (UI - budoucí rozšíření):
- Integrace BlazorHtmlEditor (https://github.com/zcloudcz/BlazorHtmlEditor) pro pokročilou editaci dokumentových šablon (WYSIWYG editor)
