# 📊 InvoiceApi - Komplexní analýza a návrh vylepšení

> **Datum vytvoření:** 8. ledna 2026
> **Verze aplikace:** 1.0 (po dokončení fáze 6)
> **Účel dokumentu:** Tento dokument poskytuje detailní analýzu architektury InvoiceApi aplikace, identifikuje kritické problémy, bezpečnostní rizika a navrhuje konkrétní vylepšení a rozšíření funkcí.

---

## 📖 O čem tento dokument je

InvoiceApi je fakturační systém s podporou multi-tenancy, navržený pro český trh s integrací ARES (automatické načítání firemních údajů), správou DPH, a kompletním lifecycle managementem faktur. Aplikace je postavena na .NET 10 s Clean Architecture přístupem.

Tento dokument obsahuje:
- **Architektonickou analýzu** - Detailní rozbor současného stavu
- **Kritické problémy** - Bezpečnostní a funkční chyby vyžadující okamžité řešení
- **Návrhy vylepšení** - Prioritizované doporučení pro zvýšení bezpečnosti, výkonu a kvality kódu
- **Nové funkce** - Rozšíření aplikace o pokročilé funkce (platby, reporting, automatizace)
- **Akční plán** - Konkrétní implementační plán rozdělený do sprintů

---

## 🏗️ Současná architektura aplikace

### Architektura vrstev

```
InvoiceApi/
├── InvoiceApi.Domain/          - Entity, enums, doménová logika
├── InvoiceApi.Application/     - DTOs, service interfaces, business logika
├── InvoiceApi.Infrastructure/  - EF Core, service implementace, data access
├── InvoiceApi.API/            - REST API controllers, authentication
├── InvoiceApi.BlazorUI/       - Blazor Server UI pro administraci
├── AresService/               - Integrace s českým obchodním rejstříkem
└── InvoiceApi.Tests.*/        - Unit a integration testy
```

### Klíčové entity a jejich vztahy

**User** → Uživatelé systému (Email, Role, CompanyId)
- Vazba na Company (Client s IsIssuer=true)
- Role: User (0), Admin (1), SysAdmin (2)

**Client** → Dual-purpose entita (zákazníci i vystavitelé faktur)
- `IsIssuer=true` → Společnost vystavující faktury
- `IsIssuer=false` → Zákazník
- Vlastnosti: IČO, DIČ, CompanyName, IsVatPayer
- Vztahy: N Address, N Contact, 1 BillingSettings

**Invoice** → Faktury a dobropisy
- Rozlišení podle `DocumentType` (Invoice/CreditNote)
- Stavy: Draft, Completed, Paid, Creditnoted, Deleted
- Vazby: Client (zákazník), Issuer (vystavitel), N InvoiceItem
- Automatické generování čísel faktur přes NumberSequence

**InvoiceItem** → Položky na faktuře
- Vazba na VatRate
- Denormalizované pole `VatRatePercentage` (zachování původní sazby i po změně)

**NumberSequence** → Generátor čísel dokumentů
- Podporuje formáty: yyyy/yy (rok), MM (měsíc), NNN (counter)
- Reset yearly/monthly

**VatRate** → Sazby DPH s platností od-do
- Standardní sazba 21%, snížená 12%, osvobozeno 0%

**AresCache** → Cache ARES API responses (30 dní expirace)

### Současný stav autentizace a autorizace

**Autentizace:**
- JWT Bearer Token (24h platnost)
- BCrypt pro hashování hesel (work factor 12)
- Claims: UserId, Email, Name, Role, CompanyId

**Autorizace:**
- `[Authorize]` atributy na kontrolerech
- Role-based: SysAdmin > Admin > User
- **⚠️ PROBLÉM:** Autorizace pouze v kontrolerech, ne v business vrstvě!

---

## 🔴 KRITICKÉ PROBLÉMY (VYŽADUJÍ OKAMŽITÉ ŘEŠENÍ)

### 1. ⚠️ Chybějící Multi-Tenancy izolace v business vrstvě

**Závažnost:** 🔴 KRITICKÁ
**Riziko:** Únik dat mezi společnostmi, porušení GDPR

**Problém:**
Služby (`ClientService`, `InvoiceService`) vrací data **všech** společností bez filtrování podle `CompanyId` uživatele. Autorizace je pouze v kontrolerech - pokud někdo obejde kontroler (např. přímé volání service), může přistupovat k datům jiných společností.

**Příklad problému:**
```csharp
// ClientService.GetAllClientsAsync() - NEFILTRUJE podle CompanyId!
public async Task<List<ClientDto>> GetAllClientsAsync(bool includeInactive = false)
{
    var query = _context.Client
        .Include(c => c.Address)
        .Include(c => c.Contact)
        .Include(c => c.BillingSettings);

    if (!includeInactive)
        query = query.Where(c => c.IsActive);

    // ❌ Chybí: .Where(c => c.IsIssuer || c.CompanyId == currentUserCompanyId)

    return await query.ToListAsync();
}
```

**Řešení:**
Implementovat **EF Core Global Query Filters** pro automatickou tenant izolaci:

```csharp
// 1. Vytvořit ICurrentUserService
public interface ICurrentUserService
{
    long? UserId { get; }
    long? CompanyId { get; }
    bool IsSysAdmin { get; }
}

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public long? UserId =>
        long.TryParse(_httpContextAccessor.HttpContext?.User
            .FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    public long? CompanyId =>
        long.TryParse(_httpContextAccessor.HttpContext?.User
            .FindFirst("CompanyId")?.Value, out var id) ? id : null;

    public bool IsSysAdmin =>
        _httpContextAccessor.HttpContext?.User
            .IsInRole("SysAdmin") ?? false;
}

// 2. V ApplicationDbContext přidat Global Query Filters
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    // Invoice - pouze faktury vlastní společnosti nebo kde je společnost klientem
    modelBuilder.Entity<Invoice>().HasQueryFilter(i =>
        _currentUserService.IsSysAdmin ||
        i.IssuerId == _currentUserService.CompanyId ||
        i.ClientId == _currentUserService.CompanyId);

    // Client - pouze vlastní klienti (pokud nejsem SysAdmin)
    modelBuilder.Entity<Client>().HasQueryFilter(c =>
        _currentUserService.IsSysAdmin ||
        c.IsIssuer); // Issuery vidí všichni pro výběr

    // User - pouze uživatelé vlastní společnosti
    modelBuilder.Entity<User>().HasQueryFilter(u =>
        _currentUserService.IsSysAdmin ||
        u.CompanyId == _currentUserService.CompanyId);

    // ... další entity
}

// 3. Registrace v Program.cs
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
```

**Priorita:** ⭐⭐⭐⭐⭐ (Implementovat IHNED)

---

### 2. 🔒 JWT Secret v konfiguračním souboru

**Závažnost:** 🔴 KRITICKÁ
**Riziko:** Kompromitace autentizace, možnost generování falešných tokenů

**Problém:**
JWT signing key je uložen v `appsettings.json` (řádek 38), který je verzován v Git a dostupný všem vývojářům.

```json
// appsettings.json - ❌ ŠPATNĚ
{
  "JwtSettings": {
    "SecretKey": "your-super-secret-key-that-should-be-at-least-32-characters-long",
    "Issuer": "InvoiceApi",
    "Audience": "InvoiceApiClient",
    "ExpiresInHours": 24
  }
}
```

**Řešení:**

**Development:**
```bash
# User Secrets (Git ignored)
dotnet user-secrets init --project InvoiceApi.API
dotnet user-secrets set "JwtSettings:SecretKey" "your-actual-secret-key-here" --project InvoiceApi.API
```

**Production:**
```csharp
// Program.cs - Load from environment or Key Vault
builder.Configuration.AddEnvironmentVariables();

// Nebo Azure Key Vault:
builder.Configuration.AddAzureKeyVault(
    new Uri($"https://{keyVaultName}.vault.azure.net/"),
    new DefaultAzureCredential());

// Validace že secret je načten:
var jwtSecret = builder.Configuration["JwtSettings:SecretKey"];
if (string.IsNullOrEmpty(jwtSecret))
    throw new InvalidOperationException("JWT Secret Key not configured!");
```

**Priorita:** ⭐⭐⭐⭐⭐ (Implementovat IHNED)

---

### 3. 🔢 Number Sequence Concurrency problém

**Závažnost:** 🔴 VYSOKÁ
**Riziko:** Duplicitní čísla faktur při současném vytváření

**Problém:**
`NumberSequence.CurrentNumber` nemá ochranu proti race conditions. Při současném vytváření faktur může dojít k:
```
Thread 1: Read CurrentNumber = 100
Thread 2: Read CurrentNumber = 100
Thread 1: Increment → 101, Save
Thread 2: Increment → 101, Save  ❌ Duplicita!
```

**Řešení:**
Optimistic concurrency control pomocí Timestamp:

```csharp
// 1. Přidat do NumberSequence entity
[Timestamp]
public byte[] RowVersion { get; set; }

// 2. V NumberSequenceService
public async Task<string> GetNextNumberAsync(EDocumentType documentType)
{
    const int maxRetries = 3;
    int retryCount = 0;

    while (retryCount < maxRetries)
    {
        try
        {
            var sequence = await _context.NumberSequence
                .FirstOrDefaultAsync(ns =>
                    ns.DocumentType == documentType &&
                    ns.IsDefault &&
                    ns.IsActive);

            if (sequence == null)
                throw new InvalidOperationException($"No default sequence for {documentType}");

            // Kontrola zda je potřeba reset (yearly/monthly)
            var format = await _context.NumberSequenceFormat
                .FindAsync(sequence.NumberSequenceFormatId);

            if (format.ResetsYearly && sequence.CurrentYear != DateTime.UtcNow.Year)
            {
                sequence.CurrentNumber = 1;
                sequence.CurrentYear = DateTime.UtcNow.Year;
            }
            else if (format.ResetsMonthly && sequence.CurrentMonth != DateTime.UtcNow.Month)
            {
                sequence.CurrentNumber = 1;
                sequence.CurrentMonth = DateTime.UtcNow.Month;
            }
            else
            {
                sequence.CurrentNumber++;
            }

            await _context.SaveChangesAsync(); // Pokud jiný thread změnil RowVersion, vyhodí DbUpdateConcurrencyException

            // Sestavit číslo podle formátu
            return FormatNumber(sequence, format);
        }
        catch (DbUpdateConcurrencyException)
        {
            retryCount++;
            if (retryCount >= maxRetries)
                throw new InvalidOperationException("Failed to generate unique number after multiple retries");

            await Task.Delay(100 * retryCount); // Exponential backoff
            _context.Entry(sequence).State = EntityState.Detached; // Reset tracked entity
        }
    }
}

// 3. Migrace
dotnet ef migrations add AddRowVersionToNumberSequence
```

**Priorita:** ⭐⭐⭐⭐⭐ (Implementovat IHNED)

---

### 4. 🌐 CORS povoluje všechny originy

**Závažnost:** 🔴 VYSOKÁ
**Riziko:** Cross-site request forgery (CSRF), neautorizovaný přístup k API

**Problém:**
```csharp
// Program.cs řádky 83-90 - ❌ NEBEZPEČNÉ
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()    // ❌ Jakákoli doména může volat API
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});
```

**Řešení:**
```csharp
// Program.cs - Správná CORS konfigurace
builder.Services.AddCors(options =>
{
    options.AddPolicy("Production", policy =>
    {
        policy.WithOrigins(
            "https://yourdomain.com",
            "https://app.yourdomain.com"
        )
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials(); // Pro cookies/JWT v headeru
    });

    options.AddPolicy("Development", policy =>
    {
        policy.WithOrigins(
            "http://localhost:5145",  // Blazor UI
            "http://localhost:3000"   // Případná React/Vue app
        )
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials();
    });
});

// Použití podle prostředí
app.UseCors(app.Environment.IsDevelopment() ? "Development" : "Production");
```

**Priorita:** ⭐⭐⭐⭐⭐ (Implementovat IHNED)

---

## 🟠 VYSOKÁ PRIORITA (zabezpečení & výkon)

### 5. 📄 Chybějící paginace na list endpointech

**Závažnost:** 🟠 VYSOKÁ
**Dopad:** Výkonnostní problémy při velkém množství dat, možné timeout při načítání

**Problém:**
Endpointy `GET /api/client`, `GET /api/invoice` vracejí **všechny** záznamy najednou.

**Řešení:**
```csharp
// 1. Vytvořit PagedResult DTO
public class PagedResult<T>
{
    public List<T> Items { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}

// 2. Extension method pro IQueryable
public static class QueryableExtensions
{
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<T>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = page,
            PageSize = pageSize
        };
    }
}

// 3. Použití v kontrolerech
[HttpGet]
[ProducesResponseType(typeof(PagedResult<ClientDto>), StatusCodes.Status200OK)]
public async Task<ActionResult<PagedResult<ClientDto>>> GetAllClients(
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 50,
    [FromQuery] bool includeInactive = false)
{
    if (page < 1) page = 1;
    if (pageSize < 1 || pageSize > 100) pageSize = 50; // Max 100 items per page

    var query = _context.Client
        .Include(c => c.Address)
        .Include(c => c.Contact)
        .Include(c => c.BillingSettings)
        .AsQueryable();

    if (!includeInactive)
        query = query.Where(c => c.IsActive);

    var pagedResult = await query.ToPagedResultAsync(page, pageSize);

    return Ok(new PagedResult<ClientDto>
    {
        Items = pagedResult.Items.Select(MapToDto).ToList(),
        TotalCount = pagedResult.TotalCount,
        PageNumber = pagedResult.PageNumber,
        PageSize = pagedResult.PageSize
    });
}
```

**Priorita:** ⭐⭐⭐⭐ (Sprint 2)

---

### 6. ✅ Chybějící validace requestů

**Závažnost:** 🟠 STŘEDNÍ
**Dopad:** Nevalidní data v databázi, špatné error messages

**Problém:**
DTOs nemají validační atributy, validace je až v service vrstvě.

**Řešení - FluentValidation:**
```bash
dotnet add package FluentValidation.AspNetCore
```

```csharp
// 1. Vytvořit validátory
public class CreateInvoiceDtoValidator : AbstractValidator<CreateInvoiceDto>
{
    public CreateInvoiceDtoValidator()
    {
        RuleFor(x => x.ClientId)
            .GreaterThan(0)
            .WithMessage("ClientId is required");

        RuleFor(x => x.IssuerId)
            .GreaterThan(0)
            .WithMessage("IssuerId is required");

        RuleFor(x => x.IssueDate)
            .NotEmpty()
            .WithMessage("IssueDate is required");

        RuleFor(x => x.DueDate)
            .GreaterThanOrEqualTo(x => x.IssueDate)
            .WithMessage("DueDate must be on or after IssueDate");

        RuleFor(x => x.Items)
            .NotEmpty()
            .WithMessage("Invoice must have at least one item")
            .Must(items => items.All(i => i.Quantity > 0))
            .WithMessage("All items must have positive quantity");

        RuleFor(x => x.Currency)
            .Must(c => new[] { "CZK", "EUR", "USD" }.Contains(c))
            .WithMessage("Invalid currency code. Allowed: CZK, EUR, USD");
    }
}

public class CreateClientDtoValidator : AbstractValidator<CreateClientDto>
{
    public CreateClientDtoValidator()
    {
        RuleFor(x => x.RegistrationNumber)
            .NotEmpty()
            .Length(8)
            .Matches(@"^\d{8}$")
            .WithMessage("IČO must be exactly 8 digits");

        RuleFor(x => x.CompanyName)
            .NotEmpty()
            .MaximumLength(200)
            .WithMessage("Company name is required and must not exceed 200 characters");

        RuleFor(x => x.TaxNumber)
            .Matches(@"^CZ\d{8,10}$")
            .When(x => !string.IsNullOrEmpty(x.TaxNumber))
            .WithMessage("DIČ must be in format CZ followed by 8-10 digits");

        RuleForEach(x => x.Contact)
            .SetValidator(new CreateContactDtoValidator());
    }
}

public class CreateContactDtoValidator : AbstractValidator<CreateContactDto>
{
    public CreateContactDtoValidator()
    {
        RuleFor(x => x.ContactValue)
            .EmailAddress()
            .When(x => x.ContactType == EContactType.Email || x.ContactType == EContactType.InvoiceEmail)
            .WithMessage("Invalid email format");

        RuleFor(x => x.ContactValue)
            .Matches(@"^\+?[\d\s()-]{9,}$")
            .When(x => x.ContactType == EContactType.Phone)
            .WithMessage("Invalid phone number format");
    }
}

// 2. Registrace v Program.cs
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddFluentValidationClientsideAdapters();
builder.Services.AddValidatorsFromAssemblyContaining<CreateInvoiceDtoValidator>();

// 3. Automatické 400 Bad Request s error details při nevalidním requestu
```

**Priorita:** ⭐⭐⭐⭐ (Sprint 2)

---

### 7. 🚦 Chybějící Rate Limiting

**Závažnost:** 🟠 VYSOKÁ
**Riziko:** Brute-force útoky na login, API abuse, DDoS

**Problém:**
Žádná ochrana proti opakovaným pokusům o přihlášení nebo nadměrnému volání API.

**Řešení:**
```bash
dotnet add package AspNetCoreRateLimit
```

```csharp
// 1. Program.cs - konfigurace
builder.Services.AddMemoryCache();

builder.Services.Configure<IpRateLimitOptions>(options =>
{
    options.EnableEndpointRateLimiting = true;
    options.StackBlockedRequests = false;
    options.HttpStatusCode = 429;
    options.RealIpHeader = "X-Real-IP";
    options.ClientIdHeader = "X-ClientId";

    options.GeneralRules = new List<RateLimitRule>
    {
        new RateLimitRule
        {
            Endpoint = "POST:/api/auth/login",
            Period = "1m",
            Limit = 5  // Max 5 login attempts per minute
        },
        new RateLimitRule
        {
            Endpoint = "POST:/api/auth/login",
            Period = "1h",
            Limit = 20  // Max 20 login attempts per hour
        },
        new RateLimitRule
        {
            Endpoint = "*",
            Period = "1s",
            Limit = 10  // Max 10 requests per second per IP
        },
        new RateLimitRule
        {
            Endpoint = "*",
            Period = "1m",
            Limit = 100  // Max 100 requests per minute per IP
        }
    };
});

builder.Services.AddInMemoryRateLimiting();
builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();

// 2. Middleware
app.UseIpRateLimiting();

// 3. Custom response při rate limit exceeded
builder.Services.Configure<IpRateLimitOptions>(options =>
{
    options.QuotaExceededResponse = new QuotaExceededResponse
    {
        Content = "{{ \"message\": \"Rate limit exceeded. Please try again later.\" }}",
        ContentType = "application/json",
        StatusCode = 429
    };
});
```

**Priorita:** ⭐⭐⭐⭐ (Sprint 1)

---

## 🟡 STŘEDNÍ PRIORITA (výkon & data integrita)

### 8. 🔍 Chybějící .AsNoTracking() pro read-only operace

**Dopad:** Zbytečná paměťová a CPU režie EF Core change trackingu

**Řešení:**
```csharp
// Všechny GET endpointy které jen čtou data:
public async Task<List<ClientDto>> GetAllClientsAsync(bool includeInactive = false)
{
    var query = _context.Client
        .AsNoTracking()  // ⬅️ PŘIDAT
        .Include(c => c.Address)
        .Include(c => c.Contact)
        .Include(c => c.BillingSettings);

    if (!includeInactive)
        query = query.Where(c => c.IsActive);

    return await query.Select(c => MapToDto(c)).ToListAsync();
}

// Nebo globálně pro DbContext (pokud většina operací je read-only):
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    // Pro operace které potřebují tracking:
    public void EnableTracking()
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
    }
}
```

**Priorita:** ⭐⭐⭐ (Sprint 2)

---

### 9. 💾 Chybějící Transaction Management

**Problém:**
Složité operace (vytvoření faktury s položkami) nejsou v transakci - možnost částečného uložení dat.

**Řešení:**
```csharp
public async Task<InvoiceDto> CreateInvoiceAsync(CreateInvoiceDto createDto)
{
    // Začít transakci
    using var transaction = await _context.Database.BeginTransactionAsync();

    try
    {
        // 1. Vytvořit invoice (bez položek a čísla)
        var invoice = new Invoice
        {
            ClientId = createDto.ClientId,
            IssuerId = createDto.IssuerId,
            DocumentType = createDto.DocumentType,
            Status = EInvoiceStatus.Draft,
            IssueDate = createDto.IssueDate,
            DueDate = createDto.DueDate,
            Currency = createDto.Currency,
            // ... další properties
        };

        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync(); // Save to get InvoiceId

        // 2. Vygenerovat číslo faktury (může selhat kvůli concurrency)
        invoice.DocumentNumber = await _numberSequenceService.GetNextNumberAsync(
            createDto.DocumentType);

        // 3. Přidat položky
        foreach (var itemDto in createDto.Items)
        {
            var vatRate = await _context.VatRate.FindAsync(itemDto.VatRateId);
            if (vatRate == null)
                throw new InvalidOperationException($"VatRate {itemDto.VatRateId} not found");

            var item = new InvoiceItem
            {
                InvoiceId = invoice.Id,
                Description = itemDto.Description,
                Quantity = itemDto.Quantity,
                Unit = itemDto.Unit,
                UnitPrice = itemDto.UnitPrice,
                VatRateId = itemDto.VatRateId,
                VatRatePercentage = vatRate.Rate, // Denormalizace
                TotalBeforeVat = itemDto.Quantity * itemDto.UnitPrice,
                // ... výpočet VatAmount a TotalWithVat
            };

            invoice.Items.Add(item);
        }

        // 4. Přepočítat totaly faktury
        invoice.TotalBeforeVat = invoice.Items.Sum(i => i.TotalBeforeVat);
        invoice.TotalVat = invoice.Items.Sum(i => i.VatAmount);
        invoice.TotalWithVat = invoice.Items.Sum(i => i.TotalWithVat);

        await _context.SaveChangesAsync();

        // 5. Commit transakce
        await transaction.CommitAsync();

        _logger.LogInformation("Invoice {DocumentNumber} created successfully", invoice.DocumentNumber);

        return MapToDto(invoice);
    }
    catch (Exception ex)
    {
        // Rollback při jakékoli chybě
        await transaction.RollbackAsync();
        _logger.LogError(ex, "Failed to create invoice");
        throw;
    }
}
```

**Priorita:** ⭐⭐⭐ (Sprint 2)

---

### 10. 🗑️ Soft delete nezohledněn v unique constraintech

**Problém:**
Po soft delete klienta (IsActive=false) nelze vytvořit nového klienta se stejným IČO.

**Řešení:**
```csharp
// ApplicationDbContext.OnModelCreating
modelBuilder.Entity<Client>()
    .HasIndex(c => new { c.RegistrationNumber, c.IsActive })
    .IsUnique()
    .HasFilter("[IsActive] = 1");  // SQL Server syntax
    // Pro PostgreSQL: .HasFilter("\"IsActive\" = true")
    // Pro SQLite: Nepodporuje filtered indexes, řešit v aplikační logice

// Pro Invoice (DocumentNumber)
modelBuilder.Entity<Invoice>()
    .HasIndex(i => new { i.DocumentNumber, i.IsActive })
    .IsUnique()
    .HasFilter("[IsActive] = 1");

// Migrace:
dotnet ef migrations add UpdateUniqueIndexesForSoftDelete
```

**Priorita:** ⭐⭐⭐ (Sprint 2)

---

### 11. 🚀 Auto-migrace při startu aplikace

**Problém:**
`Program.cs:99-103` automaticky aplikuje migrace při startu → race conditions při multi-instance deployment.

**Řešení:**
```csharp
// ❌ ODSTRANIT z Program.cs:
// using (var scope = app.Services.CreateScope())
// {
//     var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
//     await db.Database.MigrateAsync();
// }

// ✅ Místo toho:

// 1. Pro development (pouze):
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    // Raději EnsureCreated() než Migrate() pro dev
    if (!await db.Database.CanConnectAsync())
    {
        await db.Database.EnsureCreatedAsync();
        // Seed data
        await SeedDataAsync(db);
    }
}

// 2. Pro production - deployment script:
# deploy.sh
echo "Applying database migrations..."
dotnet ef database update --project InvoiceApi.Infrastructure --startup-project InvoiceApi.API --connection "$CONNECTION_STRING"

if [ $? -ne 0 ]; then
    echo "Migration failed!"
    exit 1
fi

echo "Starting application..."
dotnet InvoiceApi.API.dll
```

**Priorita:** ⭐⭐⭐ (Sprint 3)

---

## 🟢 NÍZKÁ PRIORITA (kvalita kódu & maintainability)

### 12. 📝 Audit trail nefunkční

**Problém:**
`BaseEntity` má `CreatedByUserId` a `UpdatedByUserId`, ale nikdy se neplní.

**Řešení:**
```csharp
// 1. ICurrentUserService (již vytvořen v #1)

// 2. ApplicationDbContext - Override SaveChangesAsync
private readonly ICurrentUserService _currentUserService;

public ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    ICurrentUserService currentUserService)
    : base(options)
{
    _currentUserService = currentUserService;
}

public override async Task<int> SaveChangesAsync(
    CancellationToken cancellationToken = default)
{
    var entries = ChangeTracker.Entries<BaseEntity>();
    var now = DateTime.UtcNow;
    var currentUserId = _currentUserService.UserId;

    foreach (var entry in entries)
    {
        if (entry.State == EntityState.Added)
        {
            entry.Entity.CreatedAt = now;
            entry.Entity.CreatedByUserId = currentUserId;
            entry.Entity.UpdatedAt = now;
            entry.Entity.UpdatedByUserId = currentUserId;
        }
        else if (entry.State == EntityState.Modified)
        {
            entry.Entity.UpdatedAt = now;
            entry.Entity.UpdatedByUserId = currentUserId;

            // Zajistit že CreatedAt/CreatedByUserId se nezmění
            entry.Property(nameof(BaseEntity.CreatedAt)).IsModified = false;
            entry.Property(nameof(BaseEntity.CreatedByUserId)).IsModified = false;
        }
    }

    return await base.SaveChangesAsync(cancellationToken);
}

// 3. Registrace v Program.cs
builder.Services.AddHttpContextAccessor(); // Required pro ICurrentUserService
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
```

**Bonus - Audit Log tabulka:**
```csharp
public class AuditLog : BaseEntity
{
    public long UserId { get; set; }
    public User User { get; set; }

    public string EntityType { get; set; } // "Invoice", "Client", etc.
    public long EntityId { get; set; }
    public string Action { get; set; } // "Create", "Update", "Delete", "View"

    public string Changes { get; set; } // JSON diff of changes
    public string IpAddress { get; set; }
    public string UserAgent { get; set; }
}

// Použití v kontrolerech nebo service vrstvě:
await _auditLogService.LogActionAsync(
    entityType: "Invoice",
    entityId: invoice.Id,
    action: "Create",
    changes: JsonSerializer.Serialize(createDto));
```

**Priorita:** ⭐⭐ (Sprint 3)

---

### 13. 🏥 Health Checks endpoint

**Účel:** Monitoring, Load Balancer health probes, Kubernetes liveness/readiness

**Řešení:**
```csharp
// 1. Install package
dotnet add package Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore

// 2. Program.cs
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>(
        name: "database",
        tags: new[] { "db", "ready" })
    .AddCheck<AresServiceHealthCheck>(
        name: "ares_service",
        tags: new[] { "external", "ready" })
    .AddCheck<NumberSequenceHealthCheck>(
        name: "number_sequence",
        tags: new[] { "critical", "ready" });

// 3. Custom health checks
public class AresServiceHealthCheck : IHealthCheck
{
    private readonly IAresService _aresService;

    public AresServiceHealthCheck(IAresService aresService)
    {
        _aresService = aresService;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Test ARES connectivity
            var result = await _aresService.GetCompanyInfoAsync("27074358", cancellationToken);

            if (result.IsSuccessful)
                return HealthCheckResult.Healthy("ARES service is responding");

            return HealthCheckResult.Degraded("ARES service returned error but is reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("ARES service is unavailable", ex);
        }
    }
}

// 4. Endpoints
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false // Only checks if app is running
});

// Response format:
// GET /health
// {
//   "status": "Healthy",
//   "totalDuration": "00:00:00.1234567",
//   "entries": {
//     "database": {
//       "status": "Healthy",
//       "duration": "00:00:00.0123456"
//     },
//     "ares_service": {
//       "status": "Degraded",
//       "duration": "00:00:01.2345678",
//       "description": "ARES service returned error but is reachable"
//     }
//   }
// }
```

**Priorita:** ⭐⭐ (Sprint 3)

---

## 🚀 NÁVRHY NOVÝCH FUNKCÍ A ROZŠÍŘENÍ

### **A. Pokročilé funkce fakturace**

#### 14. 🔄 Opakující se faktury (Recurring Invoices)

**Use case:** Měsíční pronájmy, SaaS předplatné, pravidelné služby

**Implementace:**
```csharp
// 1. Entita
public class RecurringInvoice : BaseEntity
{
    public long TemplateInvoiceId { get; set; }
    public Invoice TemplateInvoice { get; set; } // Šablona faktury

    public long ClientId { get; set; }
    public Client Client { get; set; }

    public long IssuerId { get; set; }
    public Client Issuer { get; set; }

    public ERecurrencePattern Pattern { get; set; } // Daily, Weekly, Monthly, Quarterly, Yearly
    public int Interval { get; set; } // Každých N dní/týdnů/měsíců
    public EDayOfWeek? DayOfWeek { get; set; } // Pro Weekly
    public int? DayOfMonth { get; set; } // Pro Monthly (1-31)

    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; } // Null = neomezené
    public DateTime NextGenerationDate { get; set; }

    public bool IsActive { get; set; }
    public bool AutoComplete { get; set; } // Automaticky označit jako Completed
    public bool AutoSendEmail { get; set; } // Automaticky odeslat emailem

    public int GeneratedInvoicesCount { get; set; }
    public int? MaxGenerations { get; set; } // Maximální počet opakování

    public List<Invoice> GeneratedInvoices { get; set; }
}

public enum ERecurrencePattern
{
    Daily = 0,
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,
    Yearly = 4
}

// 2. Background Service pro generování
public class RecurringInvoiceGeneratorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RecurringInvoiceGeneratorService> _logger;

    public RecurringInvoiceGeneratorService(
        IServiceProvider serviceProvider,
        ILogger<RecurringInvoiceGeneratorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RecurringInvoiceGenerator started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await GenerateDueInvoicesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating recurring invoices");
            }

            // Kontrolovat každou hodinu
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task GenerateDueInvoicesAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var invoiceService = scope.ServiceProvider.GetRequiredService<IInvoiceService>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        var dueRecurrences = await context.RecurringInvoice
            .Include(r => r.TemplateInvoice)
                .ThenInclude(t => t.Items)
            .Where(r =>
                r.IsActive &&
                r.NextGenerationDate <= DateTime.UtcNow &&
                (!r.MaxGenerations.HasValue || r.GeneratedInvoicesCount < r.MaxGenerations))
            .ToListAsync(cancellationToken);

        foreach (var recurrence in dueRecurrences)
        {
            try
            {
                // Vytvořit novou fakturu ze šablony
                var newInvoice = await invoiceService.CreateFromRecurrenceAsync(recurrence);

                recurrence.GeneratedInvoicesCount++;
                recurrence.NextGenerationDate = CalculateNextDate(recurrence);

                if (recurrence.AutoSendEmail)
                {
                    await emailService.SendInvoiceAsync(newInvoice.Id);
                }

                _logger.LogInformation(
                    "Generated recurring invoice {InvoiceNumber} from recurrence {RecurrenceId}",
                    newInvoice.DocumentNumber,
                    recurrence.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to generate invoice for recurrence {RecurrenceId}",
                    recurrence.Id);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private DateTime CalculateNextDate(RecurringInvoice recurrence)
    {
        var current = recurrence.NextGenerationDate;

        return recurrence.Pattern switch
        {
            ERecurrencePattern.Daily => current.AddDays(recurrence.Interval),
            ERecurrencePattern.Weekly => current.AddDays(7 * recurrence.Interval),
            ERecurrencePattern.Monthly => current.AddMonths(recurrence.Interval),
            ERecurrencePattern.Quarterly => current.AddMonths(3 * recurrence.Interval),
            ERecurrencePattern.Yearly => current.AddYears(recurrence.Interval),
            _ => throw new NotSupportedException()
        };
    }
}

// 3. API Endpoints
[HttpPost("recurring")]
public async Task<ActionResult<RecurringInvoiceDto>> CreateRecurringInvoice(
    [FromBody] CreateRecurringInvoiceDto dto)
{
    var recurrence = await _invoiceService.CreateRecurringInvoiceAsync(dto);
    return CreatedAtAction(nameof(GetRecurringInvoice), new { id = recurrence.Id }, recurrence);
}

[HttpGet("recurring/{id}/preview-next")]
public async Task<ActionResult<InvoiceDto>> PreviewNextRecurringInvoice(long id)
{
    // Preview jak bude vypadat příští faktura
    var preview = await _invoiceService.PreviewNextRecurringInvoiceAsync(id);
    return Ok(preview);
}
```

**Priorita:** ⭐⭐⭐⭐ (Sprint 5)

---

#### 15. 📧 Email notifications s šablonami

**Implementace:**
```csharp
// 1. Email Template entity
public class EmailTemplate : BaseEntity
{
    public string Name { get; set; }
    public EEmailType EmailType { get; set; }
    public long? CompanyId { get; set; } // Company-specific nebo null=global

    public string Subject { get; set; } // Supports {{InvoiceNumber}}, {{ClientName}}, {{DueDate}}, etc.
    public string BodyHtml { get; set; } // HTML template
    public string BodyText { get; set; } // Plain text fallback

    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
}

public enum EEmailType
{
    InvoiceCreated = 0,
    InvoiceCompleted = 1,
    PaymentReceived = 2,
    PaymentOverdue = 3,
    PaymentReminder = 4  // 7 days before due
}

// 2. Email Service
public interface IEmailService
{
    Task SendInvoiceAsync(long invoiceId, string recipientEmail = null);
    Task SendPaymentReminderAsync(long invoiceId);
    Task SendOverdueNoticeAsync(long invoiceId);
    Task SendBulkEmailsAsync(List<long> invoiceIds, EEmailType emailType);
}

public class EmailService : IEmailService
{
    private readonly ApplicationDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public async Task SendInvoiceAsync(long invoiceId, string recipientEmail = null)
    {
        var invoice = await _context.Invoice
            .Include(i => i.Client)
                .ThenInclude(c => c.Contact)
            .Include(i => i.Issuer)
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (invoice == null)
            throw new InvalidOperationException($"Invoice {invoiceId} not found");

        // Get recipient email
        var email = recipientEmail ??
            invoice.Client.Contact
                .FirstOrDefault(c => c.ContactType == EContactType.InvoiceEmail)?.ContactValue ??
            invoice.Client.Contact
                .FirstOrDefault(c => c.ContactType == EContactType.Email)?.ContactValue;

        if (string.IsNullOrEmpty(email))
            throw new InvalidOperationException("No email address found for client");

        // Get template
        var template = await _context.EmailTemplate
            .Where(t =>
                t.EmailType == EEmailType.InvoiceCreated &&
                t.IsActive &&
                (t.CompanyId == invoice.IssuerId || t.CompanyId == null))
            .OrderByDescending(t => t.CompanyId) // Company-specific first
            .FirstOrDefaultAsync();

        if (template == null)
            template = GetDefaultTemplate(EEmailType.InvoiceCreated);

        // Replace placeholders
        var placeholders = new Dictionary<string, string>
        {
            { "InvoiceNumber", invoice.DocumentNumber },
            { "ClientName", invoice.Client.CompanyName },
            { "IssuerName", invoice.Issuer.CompanyName },
            { "IssueDate", invoice.IssueDate.ToString("dd.MM.yyyy") },
            { "DueDate", invoice.DueDate.ToString("dd.MM.yyyy") },
            { "TotalAmount", invoice.TotalWithVat.ToString("N2") },
            { "Currency", invoice.Currency },
            { "VariableSymbol", invoice.VariableSymbol ?? "" },
            { "BankAccount", invoice.BankAccountNumber ?? "" }
        };

        var subject = ReplacePlaceholders(template.Subject, placeholders);
        var bodyHtml = ReplacePlaceholders(template.BodyHtml, placeholders);

        // Generate PDF
        var pdfBytes = await GenerateInvoicePdfAsync(invoice);

        // Send email
        await SendEmailAsync(
            to: email,
            subject: subject,
            bodyHtml: bodyHtml,
            attachments: new[]
            {
                new EmailAttachment
                {
                    FileName = $"{invoice.DocumentNumber}.pdf",
                    Content = pdfBytes,
                    ContentType = "application/pdf"
                }
            });

        // Log email sent
        invoice.IsSentByEmail = true;
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Invoice {InvoiceNumber} sent to {Email}",
            invoice.DocumentNumber,
            email);
    }

    private string ReplacePlaceholders(string template, Dictionary<string, string> placeholders)
    {
        foreach (var placeholder in placeholders)
        {
            template = template.Replace($"{{{{{placeholder.Key}}}}}", placeholder.Value);
        }
        return template;
    }

    private async Task SendEmailAsync(
        string to,
        string subject,
        string bodyHtml,
        EmailAttachment[] attachments = null)
    {
        // Using SendGrid/SMTP
        var apiKey = _configuration["SendGrid:ApiKey"];
        var client = new SendGridClient(apiKey);

        var from = new EmailAddress(
            _configuration["SendGrid:FromEmail"],
            _configuration["SendGrid:FromName"]);

        var msg = MailHelper.CreateSingleEmail(
            from,
            new EmailAddress(to),
            subject,
            plainTextContent: null,
            htmlContent: bodyHtml);

        if (attachments != null)
        {
            foreach (var attachment in attachments)
            {
                msg.AddAttachment(
                    attachment.FileName,
                    Convert.ToBase64String(attachment.Content),
                    attachment.ContentType);
            }
        }

        var response = await client.SendEmailAsync(msg);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Body.ReadAsStringAsync();
            throw new Exception($"Failed to send email: {response.StatusCode} - {body}");
        }
    }
}

// 3. Background service pro automatické připomínky
public class PaymentReminderService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Každý den ve 9:00
            var now = DateTime.Now;
            var next9Am = now.Date.AddHours(9);
            if (now > next9Am)
                next9Am = next9Am.AddDays(1);

            var delay = next9Am - now;
            await Task.Delay(delay, stoppingToken);

            await SendPaymentRemindersAsync(stoppingToken);
        }
    }

    private async Task SendPaymentRemindersAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();

        // Faktury splatné za 7 dní
        var reminderDate = DateTime.UtcNow.AddDays(7).Date;

        var invoicesDueSoon = await context.Invoice
            .Where(i =>
                i.Status == EInvoiceStatus.Completed &&
                i.DueDate.Date == reminderDate)
            .ToListAsync(cancellationToken);

        foreach (var invoice in invoicesDueSoon)
        {
            try
            {
                await emailService.SendPaymentReminderAsync(invoice.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to send payment reminder for invoice {InvoiceId}",
                    invoice.Id);
            }
        }

        // Faktury po splatnosti
        var overdueInvoices = await context.Invoice
            .Where(i =>
                i.Status == EInvoiceStatus.Completed &&
                i.DueDate < DateTime.UtcNow)
            .ToListAsync(cancellationToken);

        foreach (var invoice in overdueInvoices)
        {
            try
            {
                await emailService.SendOverdueNoticeAsync(invoice.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to send overdue notice for invoice {InvoiceId}",
                    invoice.Id);
            }
        }
    }
}
```

**Priorita:** ⭐⭐⭐⭐ (Sprint 5)

---

#### 16. 💳 Platební brána integrace a párování plateb

**Implementace:**
```csharp
// 1. Payment entity
public class Payment : BaseEntity
{
    public long InvoiceId { get; set; }
    public Invoice Invoice { get; set; }

    public decimal Amount { get; set; }
    public string Currency { get; set; }
    public DateTime PaymentDate { get; set; }

    public EPaymentMethod PaymentMethod { get; set; }
    public EPaymentStatus Status { get; set; }

    // Pro online platby
    public string GatewayTransactionId { get; set; }
    public string GatewayName { get; set; } // "GoPay", "Stripe", "PayPal"
    public string GatewayResponse { get; set; } // JSON response
    public string GatewayCallbackUrl { get; set; }

    // Pro bankovní platby
    public string BankTransactionId { get; set; }
    public string VariableSymbol { get; set; }
    public string SpecificSymbol { get; set; }
    public string ConstantSymbol { get; set; }

    public string Notes { get; set; }
}

public enum EPaymentMethod
{
    Cash = 0,
    BankTransfer = 1,
    Card = 2,
    OnlinePayment = 3,
    PayPal = 4
}

public enum EPaymentStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    Refunded = 5
}

// 2. Bank Transaction entity (pro import výpisů)
public class BankTransaction : BaseEntity
{
    public long CompanyId { get; set; }
    public Company Company { get; set; }

    public string AccountNumber { get; set; }
    public DateTime TransactionDate { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; }

    public string VariableSymbol { get; set; }
    public string SpecificSymbol { get; set; }
    public string ConstantSymbol { get; set; }

    public string CounterpartyAccount { get; set; }
    public string CounterpartyName { get; set; }
    public string CounterpartyBankCode { get; set; }

    public string Message { get; set; }
    public string TransactionId { get; set; }

    public long? MatchedInvoiceId { get; set; }
    public Invoice MatchedInvoice { get; set; }
    public long? MatchedPaymentId { get; set; }
    public Payment MatchedPayment { get; set; }

    public bool IsMatched { get; set; }
    public DateTime? MatchedAt { get; set; }
    public long? MatchedByUserId { get; set; }
}

// 3. Payment Gateway Service
public interface IPaymentGatewayService
{
    Task<PaymentInitResult> InitiatePaymentAsync(long invoiceId, EPaymentGateway gateway);
    Task<bool> VerifyPaymentAsync(string transactionId, EPaymentGateway gateway);
    Task<PaymentCallbackResult> ProcessCallbackAsync(EPaymentGateway gateway, Dictionary<string, string> parameters);
    Task RefundPaymentAsync(long paymentId, decimal amount);
}

public class GoPayPaymentService : IPaymentGatewayService
{
    public async Task<PaymentInitResult> InitiatePaymentAsync(long invoiceId, EPaymentGateway gateway)
    {
        var invoice = await _context.Invoice
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        // GoPay API call
        var goPayClient = new GoPayClient(_configuration["GoPay:GoId"], _configuration["GoPay:ClientId"], _configuration["GoPay:ClientSecret"]);

        var payment = new GoPayPayment
        {
            Amount = (long)(invoice.TotalWithVat * 100), // v haléřích
            Currency = invoice.Currency,
            OrderNumber = invoice.DocumentNumber,
            OrderDescription = $"Úhrada faktury {invoice.DocumentNumber}",
            Callback = new GoPayCallback
            {
                ReturnUrl = $"{_configuration["App:BaseUrl"]}/payment/return",
                NotifyUrl = $"{_configuration["App:BaseUrl"]}/api/payment/callback/gopay"
            },
            Payer = new GoPayPayer
            {
                Email = invoice.Client.Contact.FirstOrDefault(c => c.ContactType == EContactType.Email)?.ContactValue,
                Contact = new GoPayContact
                {
                    FirstName = invoice.Client.CompanyName,
                    Email = invoice.Client.Contact.FirstOrDefault(c => c.ContactType == EContactType.Email)?.ContactValue
                }
            }
        };

        var response = await goPayClient.CreatePaymentAsync(payment);

        // Uložit do DB
        var paymentRecord = new Payment
        {
            InvoiceId = invoiceId,
            Amount = invoice.TotalWithVat,
            Currency = invoice.Currency,
            PaymentMethod = EPaymentMethod.OnlinePayment,
            Status = EPaymentStatus.Pending,
            GatewayName = "GoPay",
            GatewayTransactionId = response.Id.ToString(),
            GatewayCallbackUrl = response.GatewayUrl,
            PaymentDate = DateTime.UtcNow
        };

        _context.Payment.Add(paymentRecord);
        await _context.SaveChangesAsync();

        return new PaymentInitResult
        {
            PaymentId = paymentRecord.Id,
            GatewayUrl = response.GatewayUrl,
            TransactionId = response.Id.ToString()
        };
    }

    public async Task<PaymentCallbackResult> ProcessCallbackAsync(
        EPaymentGateway gateway,
        Dictionary<string, string> parameters)
    {
        // Verify signature
        var expectedSignature = CalculateSignature(parameters);
        if (parameters["signature"] != expectedSignature)
            throw new SecurityException("Invalid payment callback signature");

        var transactionId = parameters["id"];
        var status = parameters["state"];

        var payment = await _context.Payment
            .Include(p => p.Invoice)
            .FirstOrDefaultAsync(p => p.GatewayTransactionId == transactionId);

        if (payment == null)
            return new PaymentCallbackResult { Success = false, Message = "Payment not found" };

        // Update payment status
        payment.Status = status switch
        {
            "PAID" => EPaymentStatus.Completed,
            "CANCELED" => EPaymentStatus.Cancelled,
            "TIMEOUTED" => EPaymentStatus.Failed,
            _ => EPaymentStatus.Pending
        };

        payment.GatewayResponse = JsonSerializer.Serialize(parameters);

        // If paid, mark invoice as paid
        if (payment.Status == EPaymentStatus.Completed)
        {
            payment.Invoice.Status = EInvoiceStatus.Paid;
            payment.Invoice.PaidAt = DateTime.UtcNow;

            // Send confirmation email
            await _emailService.SendAsync(
                to: payment.Invoice.Client.Contact.FirstOrDefault(c => c.ContactType == EContactType.Email)?.ContactValue,
                subject: $"Platba za fakturu {payment.Invoice.DocumentNumber} byla přijata",
                body: "Děkujeme za úhradu faktury.");
        }

        await _context.SaveChangesAsync();

        return new PaymentCallbackResult
        {
            Success = true,
            PaymentStatus = payment.Status
        };
    }
}

// 4. Bank Statement Import Service
public interface IBankStatementService
{
    Task<ImportResult> ImportStatementAsync(Stream fileStream, EBankFormat format);
    Task<List<BankTransaction>> GetUnmatchedTransactionsAsync(long companyId);
    Task MatchTransactionToInvoiceAsync(long transactionId, long invoiceId);
    Task AutoMatchTransactionsAsync(long companyId);
}

public class BankStatementService : IBankStatementService
{
    public async Task<ImportResult> ImportStatementAsync(Stream fileStream, EBankFormat format)
    {
        var transactions = format switch
        {
            EBankFormat.Csv => await ParseCsvAsync(fileStream),
            EBankFormat.Gpc => await ParseGpcAsync(fileStream),
            EBankFormat.Abo => await ParseAboAsync(fileStream),
            _ => throw new NotSupportedException($"Format {format} is not supported")
        };

        var imported = 0;
        var duplicates = 0;

        foreach (var transaction in transactions)
        {
            // Check if already imported
            var exists = await _context.BankTransaction
                .AnyAsync(t =>
                    t.TransactionId == transaction.TransactionId &&
                    t.AccountNumber == transaction.AccountNumber);

            if (exists)
            {
                duplicates++;
                continue;
            }

            _context.BankTransaction.Add(transaction);
            imported++;
        }

        await _context.SaveChangesAsync();

        // Auto-match by variable symbol
        await AutoMatchTransactionsAsync(transactions.First().CompanyId);

        return new ImportResult
        {
            TotalProcessed = transactions.Count,
            Imported = imported,
            Duplicates = duplicates
        };
    }

    public async Task AutoMatchTransactionsAsync(long companyId)
    {
        var unmatchedTransactions = await _context.BankTransaction
            .Where(t =>
                t.CompanyId == companyId &&
                !t.IsMatched &&
                !string.IsNullOrEmpty(t.VariableSymbol))
            .ToListAsync();

        foreach (var transaction in unmatchedTransactions)
        {
            // Find invoice by variable symbol
            var invoice = await _context.Invoice
                .FirstOrDefaultAsync(i =>
                    i.IssuerId == companyId &&
                    i.VariableSymbol == transaction.VariableSymbol &&
                    i.Status != EInvoiceStatus.Paid);

            if (invoice != null)
            {
                // Check amount matches (with tolerance)
                var tolerance = 0.01m;
                if (Math.Abs(invoice.TotalWithVat - transaction.Amount) <= tolerance)
                {
                    await MatchTransactionToInvoiceAsync(transaction.Id, invoice.Id);
                }
            }
        }
    }

    public async Task MatchTransactionToInvoiceAsync(long transactionId, long invoiceId)
    {
        var transaction = await _context.BankTransaction.FindAsync(transactionId);
        var invoice = await _context.Invoice.FindAsync(invoiceId);

        if (transaction == null || invoice == null)
            throw new InvalidOperationException("Transaction or invoice not found");

        // Create payment record
        var payment = new Payment
        {
            InvoiceId = invoiceId,
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            PaymentDate = transaction.TransactionDate,
            PaymentMethod = EPaymentMethod.BankTransfer,
            Status = EPaymentStatus.Completed,
            BankTransactionId = transaction.TransactionId,
            VariableSymbol = transaction.VariableSymbol,
            Notes = $"Matched from bank transaction {transaction.TransactionId}"
        };

        _context.Payment.Add(payment);

        // Update transaction
        transaction.IsMatched = true;
        transaction.MatchedInvoiceId = invoiceId;
        transaction.MatchedPaymentId = payment.Id;
        transaction.MatchedAt = DateTime.UtcNow;
        transaction.MatchedByUserId = _currentUserService.UserId;

        // Update invoice
        invoice.Status = EInvoiceStatus.Paid;
        invoice.PaidAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Matched bank transaction {TransactionId} to invoice {InvoiceNumber}",
            transaction.TransactionId,
            invoice.DocumentNumber);
    }
}

// 5. API Endpoints
[HttpPost("{invoiceId}/pay")]
public async Task<ActionResult<PaymentInitResult>> InitiatePayment(
    long invoiceId,
    [FromBody] InitiatePaymentDto dto)
{
    var result = await _paymentGatewayService.InitiatePaymentAsync(invoiceId, dto.Gateway);
    return Ok(result);
}

[HttpPost("callback/{gateway}")]
[AllowAnonymous]
public async Task<IActionResult> PaymentCallback(
    string gateway,
    [FromForm] Dictionary<string, string> parameters)
{
    var gatewayEnum = Enum.Parse<EPaymentGateway>(gateway, ignoreCase: true);
    var result = await _paymentGatewayService.ProcessCallbackAsync(gatewayEnum, parameters);
    return Ok(result);
}

[HttpPost("import-statement")]
public async Task<ActionResult<ImportResult>> ImportBankStatement(
    IFormFile file,
    [FromQuery] EBankFormat format)
{
    using var stream = file.OpenReadStream();
    var result = await _bankStatementService.ImportStatementAsync(stream, format);
    return Ok(result);
}

[HttpGet("unmatched-transactions")]
public async Task<ActionResult<List<BankTransactionDto>>> GetUnmatchedTransactions()
{
    var companyId = GetCurrentUserCompanyId();
    var transactions = await _bankStatementService.GetUnmatchedTransactionsAsync(companyId);
    return Ok(transactions);
}

[HttpPost("match-transaction")]
public async Task<IActionResult> MatchTransaction(
    [FromBody] MatchTransactionDto dto)
{
    await _bankStatementService.MatchTransactionToInvoiceAsync(dto.TransactionId, dto.InvoiceId);
    return Ok(new { message = "Transaction matched successfully" });
}
```

**Priorita:** ⭐⭐⭐⭐ (Sprint 4)

---

### **B. Reporting & Analytics**

#### 17. 📊 Dashboard s KPI metrikami

**Implementace:**
```csharp
// 1. Dashboard Stats DTO
public class DashboardStats
{
    // Revenue
    public decimal TotalRevenueThisMonth { get; set; }
    public decimal TotalRevenueLastMonth { get; set; }
    public decimal RevenueGrowthPercentage { get; set; }

    public decimal TotalRevenueThisYear { get; set; }
    public decimal TotalRevenueLastYear { get; set; }

    // Invoices
    public int InvoicesIssuedThisMonth { get; set; }
    public int InvoicesIssuedLastMonth { get; set; }
    public int InvoicesPaidThisMonth { get; set; }

    public decimal AverageInvoiceValue { get; set; }
    public decimal AveragePaymentTime { get; set; } // Days

    // Outstanding
    public int DraftInvoicesCount { get; set; }
    public int UnpaidInvoicesCount { get; set; }
    public decimal UnpaidInvoicesTotal { get; set; }

    public int OverdueInvoicesCount { get; set; }
    public decimal OverdueInvoicesTotal { get; set; }

    // Top clients
    public List<TopClient> TopClientsByRevenue { get; set; }
    public List<TopClient> TopClientsByInvoiceCount { get; set; }

    // Trends
    public List<MonthlyRevenue> RevenueByMonth { get; set; } // Last 12 months
    public List<MonthlyRevenue> InvoicesByMonth { get; set; }

    // VAT
    public decimal VatCollectedThisQuarter { get; set; }
    public decimal VatCollectedLastQuarter { get; set; }
}

public class TopClient
{
    public long ClientId { get; set; }
    public string ClientName { get; set; }
    public decimal TotalRevenue { get; set; }
    public int InvoiceCount { get; set; }
}

public class MonthlyRevenue
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Revenue { get; set; }
    public int InvoiceCount { get; set; }
}

// 2. Service method
public async Task<DashboardStats> GetDashboardStatsAsync(long companyId)
{
    var now = DateTime.UtcNow;
    var thisMonthStart = new DateTime(now.Year, now.Month, 1);
    var lastMonthStart = thisMonthStart.AddMonths(-1);
    var thisYearStart = new DateTime(now.Year, 1, 1);
    var lastYearStart = new DateTime(now.Year - 1, 1, 1);

    var invoices = await _context.Invoice
        .Where(i => i.IssuerId == companyId)
        .ToListAsync();

    // This month revenue
    var thisMonthRevenue = invoices
        .Where(i => i.IssueDate >= thisMonthStart && i.Status == EInvoiceStatus.Paid)
        .Sum(i => i.TotalWithVat);

    // Last month revenue
    var lastMonthRevenue = invoices
        .Where(i => i.IssueDate >= lastMonthStart && i.IssueDate < thisMonthStart && i.Status == EInvoiceStatus.Paid)
        .Sum(i => i.TotalWithVat);

    // Growth percentage
    var growthPercentage = lastMonthRevenue > 0
        ? ((thisMonthRevenue - lastMonthRevenue) / lastMonthRevenue) * 100
        : 0;

    // Top clients
    var topClientsByRevenue = invoices
        .Where(i => i.Status == EInvoiceStatus.Paid)
        .GroupBy(i => new { i.ClientId, i.Client.CompanyName })
        .Select(g => new TopClient
        {
            ClientId = g.Key.ClientId,
            ClientName = g.Key.CompanyName,
            TotalRevenue = g.Sum(i => i.TotalWithVat),
            InvoiceCount = g.Count()
        })
        .OrderByDescending(c => c.TotalRevenue)
        .Take(10)
        .ToList();

    // Revenue by month (last 12 months)
    var revenueByMonth = invoices
        .Where(i => i.IssueDate >= now.AddMonths(-12) && i.Status == EInvoiceStatus.Paid)
        .GroupBy(i => new { i.IssueDate.Year, i.IssueDate.Month })
        .Select(g => new MonthlyRevenue
        {
            Year = g.Key.Year,
            Month = g.Key.Month,
            Revenue = g.Sum(i => i.TotalWithVat),
            InvoiceCount = g.Count()
        })
        .OrderBy(m => m.Year).ThenBy(m => m.Month)
        .ToList();

    return new DashboardStats
    {
        TotalRevenueThisMonth = thisMonthRevenue,
        TotalRevenueLastMonth = lastMonthRevenue,
        RevenueGrowthPercentage = growthPercentage,

        InvoicesIssuedThisMonth = invoices.Count(i => i.IssueDate >= thisMonthStart),
        InvoicesIssuedLastMonth = invoices.Count(i => i.IssueDate >= lastMonthStart && i.IssueDate < thisMonthStart),
        InvoicesPaidThisMonth = invoices.Count(i => i.PaidAt >= thisMonthStart),

        AverageInvoiceValue = invoices.Any() ? invoices.Average(i => i.TotalWithVat) : 0,
        AveragePaymentTime = invoices.Where(i => i.PaidAt.HasValue).Any()
            ? invoices.Where(i => i.PaidAt.HasValue).Average(i => (i.PaidAt.Value - i.IssueDate).TotalDays)
            : 0,

        DraftInvoicesCount = invoices.Count(i => i.Status == EInvoiceStatus.Draft),
        UnpaidInvoicesCount = invoices.Count(i => i.Status == EInvoiceStatus.Completed),
        UnpaidInvoicesTotal = invoices.Where(i => i.Status == EInvoiceStatus.Completed).Sum(i => i.TotalWithVat),

        OverdueInvoicesCount = invoices.Count(i => i.Status == EInvoiceStatus.Completed && i.DueDate < now),
        OverdueInvoicesTotal = invoices.Where(i => i.Status == EInvoiceStatus.Completed && i.DueDate < now).Sum(i => i.TotalWithVat),

        TopClientsByRevenue = topClientsByRevenue,
        RevenueByMonth = revenueByMonth
    };
}

// 3. API Endpoint
[HttpGet("dashboard")]
public async Task<ActionResult<DashboardStats>> GetDashboard()
{
    var companyId = GetCurrentUserCompanyId();
    var stats = await _invoiceService.GetDashboardStatsAsync(companyId);
    return Ok(stats);
}
```

**Priorita:** ⭐⭐⭐⭐ (Sprint 6)

---

#### 18. 📄 PDF generování a exporty

**Implementace:**
```csharp
// Install package
dotnet add package QuestPDF

// 1. PDF Generator Service
public interface IReportService
{
    Task<byte[]> GenerateInvoicePdfAsync(long invoiceId);
    Task<byte[]> GenerateSalesReportAsync(DateTime from, DateTime to, EReportFormat format);
    Task<byte[]> GenerateVatReportAsync(int year, int quarter);
    Task<byte[]> GenerateClientStatementAsync(long clientId, DateTime from, DateTime to);
    Task<byte[]> ExportInvoicesToExcelAsync(InvoiceFilterDto filter);
}

public class ReportService : IReportService
{
    public async Task<byte[]> GenerateInvoicePdfAsync(long invoiceId)
    {
        var invoice = await _context.Invoice
            .Include(i => i.Client)
                .ThenInclude(c => c.Address)
            .Include(i => i.Client.Contact)
            .Include(i => i.Issuer)
                .ThenInclude(c => c.Address)
            .Include(i => i.Issuer.Contact)
            .Include(i => i.Items)
                .ThenInclude(item => item.VatRate)
            .FirstOrDefaultAsync(i => i.Id == invoiceId);

        if (invoice == null)
            throw new InvalidOperationException($"Invoice {invoiceId} not found");

        // QuestPDF document
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);

                page.Header().Element(ComposeHeader);
                page.Content().Element(ComposeContent);
                page.Footer().Element(ComposeFooter);
            });
        });

        void ComposeHeader(IContainer container)
        {
            container.Row(row =>
            {
                // Logo
                row.ConstantItem(100).Height(50).Placeholder();

                // Issuer info
                row.RelativeItem().Column(column =>
                {
                    column.Item().Text(invoice.Issuer.CompanyName).FontSize(16).Bold();
                    column.Item().Text(invoice.Issuer.Address.FirstOrDefault()?.Street ?? "");
                    column.Item().Text($"{invoice.Issuer.Address.FirstOrDefault()?.PostalCode} {invoice.Issuer.Address.FirstOrDefault()?.City}");
                    column.Item().Text($"IČO: {invoice.Issuer.RegistrationNumber}");
                    if (!string.IsNullOrEmpty(invoice.Issuer.TaxNumber))
                        column.Item().Text($"DIČ: {invoice.Issuer.TaxNumber}");
                });
            });
        }

        void ComposeContent(IContainer container)
        {
            container.PaddingVertical(40).Column(column =>
            {
                // Document title
                column.Item().Text(invoice.DocumentType == EDocumentType.Invoice ? "FAKTURA" : "DOBROPIS")
                    .FontSize(20).Bold().AlignCenter();

                // Invoice details
                column.Item().PaddingVertical(10).Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text($"Číslo: {invoice.DocumentNumber}").Bold();
                        col.Item().Text($"Datum vystavení: {invoice.IssueDate:dd.MM.yyyy}");
                        col.Item().Text($"Datum splatnosti: {invoice.DueDate:dd.MM.yyyy}");
                        if (!string.IsNullOrEmpty(invoice.VariableSymbol))
                            col.Item().Text($"Variabilní symbol: {invoice.VariableSymbol}");
                    });

                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text("Odběratel:").Bold();
                        col.Item().Text(invoice.Client.CompanyName);
                        col.Item().Text(invoice.Client.Address.FirstOrDefault()?.Street ?? "");
                        col.Item().Text($"{invoice.Client.Address.FirstOrDefault()?.PostalCode} {invoice.Client.Address.FirstOrDefault()?.City}");
                        col.Item().Text($"IČO: {invoice.Client.RegistrationNumber}");
                        if (!string.IsNullOrEmpty(invoice.Client.TaxNumber))
                            col.Item().Text($"DIČ: {invoice.Client.TaxNumber}");
                    });
                });

                // Items table
                column.Item().PaddingVertical(20).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(3); // Description
                        columns.RelativeColumn(1); // Quantity
                        columns.RelativeColumn(1); // Unit
                        columns.RelativeColumn(1); // Unit Price
                        columns.RelativeColumn(1); // VAT
                        columns.RelativeColumn(1); // Total
                    });

                    // Header
                    table.Header(header =>
                    {
                        header.Cell().Element(CellStyle).Text("Popis");
                        header.Cell().Element(CellStyle).Text("Množství");
                        header.Cell().Element(CellStyle).Text("Jedn.");
                        header.Cell().Element(CellStyle).Text("Cena/jedn.");
                        header.Cell().Element(CellStyle).Text("DPH");
                        header.Cell().Element(CellStyle).Text("Celkem");

                        static IContainer CellStyle(IContainer container) => container
                            .BorderBottom(1)
                            .BorderColor(Colors.Grey.Medium)
                            .PaddingVertical(5);
                    });

                    // Items
                    foreach (var item in invoice.Items.OrderBy(i => i.OrderIndex))
                    {
                        table.Cell().Text(item.Description);
                        table.Cell().Text(item.Quantity.ToString("N2"));
                        table.Cell().Text(item.Unit);
                        table.Cell().Text(item.UnitPrice.ToString("N2"));
                        table.Cell().Text($"{item.VatRatePercentage}%");
                        table.Cell().Text(item.TotalWithVat.ToString("N2"));
                    }
                });

                // Totals
                column.Item().AlignRight().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("Cena bez DPH:");
                        row.ConstantItem(100).Text(invoice.TotalBeforeVat.ToString("N2") + " " + invoice.Currency);
                    });

                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text("DPH:");
                        row.ConstantItem(100).Text(invoice.TotalVat.ToString("N2") + " " + invoice.Currency);
                    });

                    col.Item().BorderTop(2).PaddingTop(5).Row(row =>
                    {
                        row.RelativeItem().Text("Celkem k úhradě:").Bold().FontSize(14);
                        row.ConstantItem(100).Text(invoice.TotalWithVat.ToString("N2") + " " + invoice.Currency).Bold().FontSize(14);
                    });
                });

                // Payment info
                if (!string.IsNullOrEmpty(invoice.BankAccountNumber))
                {
                    column.Item().PaddingTop(20).Column(col =>
                    {
                        col.Item().Text("Platební údaje:").Bold();
                        col.Item().Text($"Číslo účtu: {invoice.BankAccountNumber}");
                        if (!string.IsNullOrEmpty(invoice.VariableSymbol))
                            col.Item().Text($"Variabilní symbol: {invoice.VariableSymbol}");
                        if (!string.IsNullOrEmpty(invoice.SpecificSymbol))
                            col.Item().Text($"Specifický symbol: {invoice.SpecificSymbol}");
                    });
                }
            });
        }

        void ComposeFooter(IContainer container)
        {
            container.AlignCenter().Text(text =>
            {
                text.Span("Vytvořeno pomocí InvoiceApi - ");
                text.Span($"Strana ").CurrentPageNumber();
                text.Span(" z ").TotalPages();
            });
        }

        return document.GeneratePdf();
    }

    public async Task<byte[]> ExportInvoicesToExcelAsync(InvoiceFilterDto filter)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Faktury");

        // Headers
        worksheet.Cell(1, 1).Value = "Číslo faktury";
        worksheet.Cell(1, 2).Value = "Datum vystavení";
        worksheet.Cell(1, 3).Value = "Datum splatnosti";
        worksheet.Cell(1, 4).Value = "Klient";
        worksheet.Cell(1, 5).Value = "Stav";
        worksheet.Cell(1, 6).Value = "Celkem bez DPH";
        worksheet.Cell(1, 7).Value = "DPH";
        worksheet.Cell(1, 8).Value = "Celkem";
        worksheet.Cell(1, 9).Value = "Měna";

        // Style header
        var headerRange = worksheet.Range(1, 1, 1, 9);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        // Data
        var invoices = await _invoiceService.GetAllInvoicesAsync(filter);
        int row = 2;

        foreach (var invoice in invoices)
        {
            worksheet.Cell(row, 1).Value = invoice.DocumentNumber;
            worksheet.Cell(row, 2).Value = invoice.IssueDate;
            worksheet.Cell(row, 3).Value = invoice.DueDate;
            worksheet.Cell(row, 4).Value = invoice.ClientName;
            worksheet.Cell(row, 5).Value = invoice.Status.ToString();
            worksheet.Cell(row, 6).Value = invoice.TotalBeforeVat;
            worksheet.Cell(row, 7).Value = invoice.TotalVat;
            worksheet.Cell(row, 8).Value = invoice.TotalWithVat;
            worksheet.Cell(row, 9).Value = invoice.Currency;
            row++;
        }

        // Auto-fit columns
        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}

// 3. API Endpoints
[HttpGet("{id}/pdf")]
public async Task<IActionResult> GetInvoicePdf(long id)
{
    var pdf = await _reportService.GenerateInvoicePdfAsync(id);
    var invoice = await _invoiceService.GetInvoiceByIdAsync(id);

    return File(pdf, "application/pdf", $"{invoice.DocumentNumber}.pdf");
}

[HttpGet("export/excel")]
public async Task<IActionResult> ExportInvoicesToExcel([FromQuery] InvoiceFilterDto filter)
{
    var excel = await _reportService.ExportInvoicesToExcelAsync(filter);
    return File(excel,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        $"invoices-{DateTime.Now:yyyyMMdd}.xlsx");
}

[HttpGet("reports/sales")]
public async Task<IActionResult> GetSalesReport(
    [FromQuery] DateTime from,
    [FromQuery] DateTime to,
    [FromQuery] EReportFormat format = EReportFormat.Pdf)
{
    var report = await _reportService.GenerateSalesReportAsync(from, to, format);
    var contentType = format == EReportFormat.Pdf
        ? "application/pdf"
        : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    return File(report, contentType, $"sales-report-{from:yyyyMMdd}-{to:yyyyMMdd}.{format.ToString().ToLower()}");
}
```

**Priorita:** ⭐⭐⭐⭐ (Sprint 6)

---

### **C. Compliance & Legislativa**

#### 19. 🇨🇿 EET integrace (Elektronická evidence tržeb)

**Poznámka:** EET bylo pozastaveno v roce 2020, ale architektura je zde pro případnou reaktivaci.

```csharp
// 1. EET Record entity
public class EetRecord : BaseEntity
{
    public long InvoiceId { get; set; }
    public Invoice Invoice { get; set; }

    public string FiscalIdentificationCode { get; set; } // FIK
    public string BusinessPremisesId { get; set; } // Id provozovny
    public string CashRegisterId { get; set; } // Id pokladny

    public DateTime SentAt { get; set; }
    public string RequestXml { get; set; }
    public string ResponseXml { get; set; }

    public bool IsSuccessful { get; set; }
    public string ErrorMessage { get; set; }
    public string WarningMessage { get; set; }

    // BKP (Bezpečnostní kód poplatníka)
    public string SecurityCode { get; set; }

    // PKP (Podpisový kód poplatníka)
    public string SignatureCode { get; set; }
}

// 2. EET Service (pokud bude znovu aktivováno)
public interface IEetService
{
    Task<EetRecord> SendToEetAsync(long invoiceId);
    Task<bool> VerifyFikAsync(string fik);
}
```

**Priorita:** ⭐ (Pouze pokud EET bude reaktivováno)

---

#### 20. 🔒 GDPR Compliance nástroje

```csharp
// 1. Data Access Log
public class DataAccessLog : BaseEntity
{
    public long UserId { get; set; }
    public User User { get; set; }

    public string EntityType { get; set; } // "Client", "Invoice", "User"
    public long EntityId { get; set; }
    public string Action { get; set; } // "View", "Edit", "Delete", "Export"

    public DateTime AccessedAt { get; set; }
    public string IpAddress { get; set; }
    public string UserAgent { get; set; }
    public string RequestPath { get; set; }
}

// 2. GDPR Service
public interface IGdprService
{
    Task<byte[]> ExportClientDataAsync(long clientId); // Export all client data (JSON/PDF)
    Task AnonymizeClientAsync(long clientId); // Replace personal data with "ANONYMIZED"
    Task DeleteClientDataAsync(long clientId); // Complete deletion (if legally allowed)
    Task<List<DataAccessLog>> GetClientDataAccessLogAsync(long clientId);
}

public class GdprService : IGdprService
{
    public async Task<byte[]> ExportClientDataAsync(long clientId)
    {
        var client = await _context.Client
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .Include(c => c.BillingSettings)
            .FirstOrDefaultAsync(c => c.Id == clientId);

        var invoices = await _context.Invoice
            .Include(i => i.Items)
            .Where(i => i.ClientId == clientId)
            .ToListAsync();

        var exportData = new
        {
            ExportDate = DateTime.UtcNow,
            Client = client,
            Invoices = invoices,
            AccessLogs = await GetClientDataAccessLogAsync(clientId)
        };

        var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        return Encoding.UTF8.GetBytes(json);
    }

    public async Task AnonymizeClientAsync(long clientId)
    {
        var client = await _context.Client
            .Include(c => c.Contact)
            .FirstOrDefaultAsync(c => c.Id == clientId);

        if (client == null)
            throw new InvalidOperationException("Client not found");

        // Check if client has unpaid invoices
        var hasUnpaidInvoices = await _context.Invoice
            .AnyAsync(i =>
                i.ClientId == clientId &&
                i.Status != EInvoiceStatus.Paid);

        if (hasUnpaidInvoices)
            throw new InvalidOperationException("Cannot anonymize client with unpaid invoices");

        // Anonymize
        client.CompanyName = "ANONYMIZED";
        client.TradingName = null;
        client.TaxNumber = null;
        client.RegistrationNumber = $"ANON{clientId:D8}";

        foreach (var contact in client.Contact)
        {
            contact.ContactValue = contact.ContactType == EContactType.Email
                ? $"anonymized{clientId}@example.com"
                : "ANONYMIZED";
        }

        await _context.SaveChangesAsync();

        _logger.LogWarning("Client {ClientId} has been anonymized", clientId);
    }

    public async Task<List<DataAccessLog>> GetClientDataAccessLogAsync(long clientId)
    {
        return await _context.DataAccessLog
            .Include(l => l.User)
            .Where(l =>
                l.EntityType == "Client" &&
                l.EntityId == clientId)
            .OrderByDescending(l => l.AccessedAt)
            .ToListAsync();
    }
}

// 3. Middleware pro logování přístupů
public class DataAccessLoggingMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context, ApplicationDbContext dbContext)
    {
        await _next(context);

        // Log only successful GET/PUT/DELETE on sensitive endpoints
        if (context.Response.StatusCode == 200 &&
            (context.Request.Method == "GET" ||
             context.Request.Method == "PUT" ||
             context.Request.Method == "DELETE"))
        {
            var path = context.Request.Path.Value;

            // Extract entity type and ID from path
            // Example: /api/client/123 -> EntityType="Client", EntityId=123
            if (TryParseEntityFromPath(path, out var entityType, out var entityId))
            {
                var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (long.TryParse(userId, out var userIdLong))
                {
                    dbContext.DataAccessLog.Add(new DataAccessLog
                    {
                        UserId = userIdLong,
                        EntityType = entityType,
                        EntityId = entityId,
                        Action = context.Request.Method,
                        AccessedAt = DateTime.UtcNow,
                        IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                        UserAgent = context.Request.Headers["User-Agent"].ToString(),
                        RequestPath = path
                    });

                    await dbContext.SaveChangesAsync();
                }
            }
        }
    }
}

// 4. API Endpoints
[HttpGet("gdpr/export/{clientId}")]
[Authorize(Roles = "SysAdmin")]
public async Task<IActionResult> ExportClientData(long clientId)
{
    var data = await _gdprService.ExportClientDataAsync(clientId);
    return File(data, "application/json", $"client-{clientId}-export.json");
}

[HttpPost("gdpr/anonymize/{clientId}")]
[Authorize(Roles = "SysAdmin")]
public async Task<IActionResult> AnonymizeClient(long clientId)
{
    await _gdprService.AnonymizeClientAsync(clientId);
    return Ok(new { message = "Client anonymized successfully" });
}

[HttpGet("gdpr/access-log/{clientId}")]
public async Task<ActionResult<List<DataAccessLogDto>>> GetAccessLog(long clientId)
{
    var logs = await _gdprService.GetClientDataAccessLogAsync(clientId);
    return Ok(logs);
}
```

**Priorita:** ⭐⭐⭐ (GDPR compliance je důležité)

---

## 📋 PRIORITIZOVANÝ AKČNÍ PLÁN

### **Sprint 1 (týden 1-2): Kritické bezpečnostní opravy** ⭐⭐⭐⭐⭐

**Cíl:** Zajistit bezpečnost aplikace před nasazením do produkce

- [ ] **1. Multi-tenancy izolace (Global Query Filters)**
  - Implementovat `ICurrentUserService`
  - Přidat Global Query Filters do `ApplicationDbContext`
  - Testovat data isolation
  - **Estimate:** 8 hodin

- [ ] **2. JWT Secret do User Secrets/Environment**
  - Přesunout JWT secret z appsettings.json
  - Konfigurovat User Secrets pro development
  - Dokumentovat production setup (Key Vault)
  - **Estimate:** 2 hodiny

- [ ] **3. Number Sequence Concurrency Fix**
  - Přidat `[Timestamp]` do NumberSequence
  - Implementovat retry logiku s exponential backoff
  - Migrace databáze
  - **Estimate:** 4 hodiny

- [ ] **4. CORS konfigurace**
  - Nahradit `AllowAnyOrigin()` konkrétními doménami
  - Separate policies pro Development/Production
  - **Estimate:** 1 hodina

- [ ] **5. Rate Limiting**
  - Instalovat AspNetCoreRateLimit
  - Konfigurovat limity pro /api/auth/login (5/min, 20/hour)
  - Globální limit 100 req/min per IP
  - **Estimate:** 3 hodiny

**Celková estimate:** ~18 hodin (2-3 dny)

---

### **Sprint 2 (týden 3-4): Performance & Validace** ⭐⭐⭐⭐

**Cíl:** Optimalizovat výkon a přidat validaci

- [ ] **6. Paginace**
  - Vytvořit `PagedResult<T>` DTO
  - Extension method `ToPagedResultAsync()`
  - Implementovat ve všech list endpointech
  - Aktualizovat Swagger dokumentaci
  - **Estimate:** 6 hodin

- [ ] **7. FluentValidation**
  - Instalovat FluentValidation.AspNetCore
  - Vytvořit validátory pro všechny DTOs
  - Registrovat v Program.cs
  - Testovat error responses
  - **Estimate:** 8 hodin

- [ ] **8. AsNoTracking**
  - Přidat `.AsNoTracking()` do read-only queries
  - Performance testy před/po
  - **Estimate:** 2 hodiny

- [ ] **9. Transaction Management**
  - Obalit CreateInvoice, UpdateInvoice do transakcí
  - Implementovat rollback handling
  - **Estimate:** 4 hodiny

- [ ] **10. Soft Delete Unique Constraints**
  - Aktualizovat indexes s filtered indexy
  - Migrace
  - **Estimate:** 2 hodiny

**Celková estimate:** ~22 hodin (3 dny)

---

### **Sprint 3 (týden 5-6): Audit & Monitoring** ⭐⭐⭐

**Cíl:** Přidat audit trail a monitoring

- [ ] **11. Audit Trail plnění**
  - Implementovat auto-fill CreatedByUserId/UpdatedByUserId
  - Override SaveChangesAsync v DbContext
  - **Estimate:** 4 hodiny

- [ ] **12. Health Checks**
  - Instalovat Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore
  - Vytvořit custom health checks (ARES, NumberSequence)
  - Endpoints: /health, /health/ready, /health/live
  - **Estimate:** 4 hodiny

- [ ] **13. Structured Logging (Serilog)**
  - Instalovat Serilog
  - Konfigurovat sinks (Console, File, Seq/Application Insights)
  - Request logging middleware
  - **Estimate:** 4 hodiny

- [ ] **14. Exception Handling Middleware**
  - Global exception handler
  - Standardizované error responses
  - Logging všech exceptions
  - **Estimate:** 3 hodiny

- [ ] **15. Odstranit auto-migration**
  - Vytvořit deployment script
  - Dokumentovat migration proces
  - **Estimate:** 2 hodiny

**Celková estimate:** ~17 hodin (2 dny)

---

### **Sprint 4 (týden 7-8): Platby & Bankovní integrace** ⭐⭐⭐⭐

**Cíl:** Automatizovat platby a párování

- [ ] **16. Payment Entity**
  - Vytvořit Payment entity
  - Migrace
  - **Estimate:** 2 hodiny

- [ ] **17. BankTransaction Entity**
  - Vytvořit BankTransaction entity
  - Migrace
  - **Estimate:** 2 hodiny

- [ ] **18. Bank Statement Import**
  - Parser pro CSV formát (Fio, ČSOB, KB)
  - Parser pro GPC/ABO formáty
  - Import endpoint
  - **Estimate:** 12 hodin

- [ ] **19. Auto-matching**
  - Matching algorithm (variable symbol)
  - Manual match endpoint
  - UI pro unmatched transactions
  - **Estimate:** 8 hodin

- [ ] **20. Payment Gateway (GoPay)**
  - GoPay SDK integration
  - Payment initiation
  - Callback handling
  - **Estimate:** 12 hodin

**Celková estimate:** ~36 hodin (5 dnů)

---

### **Sprint 5 (týden 9-10): Email & Automatizace** ⭐⭐⭐⭐

**Cíl:** Automatizovat komunikaci a opakující se úkoly

- [ ] **21. Email Templates**
  - EmailTemplate entity
  - Template management UI
  - Placeholder system
  - **Estimate:** 6 hodin

- [ ] **22. Email Service**
  - SendGrid/SMTP integration
  - PDF attachment generation
  - Email logging
  - **Estimate:** 8 hodin

- [ ] **23. Recurring Invoices**
  - RecurringInvoice entity
  - Background service
  - Recurrence calculation logic
  - Management endpoints
  - **Estimate:** 12 hodin

- [ ] **24. Payment Reminders**
  - Background service pro reminders
  - Email templates pro reminders/overdue
  - Schedule configuration
  - **Estimate:** 6 hodin

**Celková estimate:** ~32 hodin (4 dny)

---

### **Sprint 6 (týden 11-12): Reporting & Analytics** ⭐⭐⭐⭐

**Cíl:** Business intelligence a reporting

- [ ] **25. Dashboard KPI**
  - Implementovat `GetDashboardStatsAsync()`
  - Revenue, invoices, top clients analytics
  - Chart data endpoints
  - **Estimate:** 8 hodin

- [ ] **26. PDF Generation**
  - Instalovat QuestPDF
  - Invoice PDF template (Czech design)
  - Sales report PDF
  - **Estimate:** 12 hodin

- [ ] **27. Excel Export**
  - Instalovat ClosedXML
  - Export invoices to Excel
  - Export clients to Excel
  - **Estimate:** 6 hodin

- [ ] **28. VAT Report**
  - Quarterly VAT calculation
  - PDF/Excel export
  - Summary by VAT rate
  - **Estimate:** 6 hodin

**Celková estimate:** ~32 hodin (4 dny)

---

## 🎯 CELKOVÉ SHRNUTÍ

### **Kritické problémy k vyřešení IHNED:**
1. ⚠️ Multi-tenancy izolace
2. 🔒 JWT Secret v konfiguračním souboru
3. 🔢 Number Sequence concurrency
4. 🌐 CORS AllowAnyOrigin
5. 🚦 Chybějící Rate Limiting

### **Doporučený postup:**
1. **Týdny 1-2:** Zabezpečení (Sprint 1)
2. **Týdny 3-4:** Performance (Sprint 2)
3. **Týdny 5-6:** Monitoring (Sprint 3)
4. **Týdny 7-8:** Platby (Sprint 4)
5. **Týdny 9-10:** Automatizace (Sprint 5)
6. **Týdny 11-12:** Reporting (Sprint 6)

### **Celková časová náročnost:**
- **Kritické opravy:** ~18 hodin
- **Všechny sprinty:** ~157 hodin (~20 pracovních dnů)

---

## 📚 DALŠÍ DOPORUČENÍ

### **Testování:**
- Unit testy pro business logiku (služby)
- Integration testy pro API endpointy
- Load testing (k6, JMeter)
- Security testing (OWASP ZAP)

### **CI/CD:**
- GitHub Actions / Azure DevOps pipeline
- Automated testing
- Database migration validation
- Deployment scripts

### **Documentation:**
- OpenAPI/Swagger kompletní
- README s setup instrukcemi
- Architecture Decision Records (ADRs)

### **Infrastructure:**
- Docker containerization
- Kubernetes manifests
- Azure Application Insights
- Redis cache pro distributed scenarios

---

**Konec dokumentu**

> Tento dokument byl vytvořen 8. ledna 2026 jako výsledek komplexní analýzy InvoiceApi aplikace. Doporučení jsou prioritizována podle závažnosti a business value.
