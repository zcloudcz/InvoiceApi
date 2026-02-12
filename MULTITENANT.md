# Multi-Tenant Architecture: Analysis, Design & Implementation Plan

## Table of Contents
1. [Current State Analysis](#1-current-state-analysis)
2. [Target Architecture](#2-target-architecture)
3. [Database Split Strategy](#3-database-split-strategy)
4. [Company Data Synchronization](#4-company-data-synchronization)
5. [New & Modified Entities](#5-new--modified-entities)
6. [Tenant Lifecycle](#6-tenant-lifecycle)
7. [Service Layer Redesign](#7-service-layer-redesign)
8. [Authentication & Impersonation Changes](#8-authentication--impersonation-changes)
9. [Migration Strategy](#9-migration-strategy)
10. [Testing Strategy](#10-testing-strategy)
11. [Implementation Phases](#11-implementation-phases)
12. [Risks & Mitigations](#12-risks--mitigations)

---

## 1. Current State Analysis

### Current Architecture (Single SQLite Database)
```
┌──────────────────────────────────────────────────────────┐
│                    invoiceapi.db (SQLite)                 │
│                                                          │
│  ┌─────────┐ ┌─────────┐ ┌──────────┐ ┌──────────────┐  │
│  │ VatRate  │ │Currency │ │  User    │ │ContentTempl. │  │
│  └─────────┘ └─────────┘ └──────────┘ └──────────────┘  │
│  ┌─────────┐ ┌─────────┐ ┌──────────┐ ┌──────────────┐  │
│  │ Client  │ │ Invoice │ │InvItem   │ │NumberSequence│  │
│  │(Issuer) │ │         │ │          │ │              │  │
│  └─────────┘ └─────────┘ └──────────┘ └──────────────┘  │
│  ┌─────────┐ ┌─────────────────┐ ┌──────────────────┐   │
│  │AresCache│ │NumberSeqFormat  │ │ BillingSettings  │   │
│  └─────────┘ └─────────────────┘ └──────────────────┘   │
└──────────────────────────────────────────────────────────┘
```

### Problems with Current Architecture
1. **No data isolation** — All tenants share one DB. Cross-tenant data leakage risk if a controller forgets to filter by CompanyId.
2. **No tenant settings** — No place to store per-company connection strings, provisioning state, or system-level configuration.
3. **Manual filtering** — Controllers must manually read `CompanyId` claim and pass it to services. No automatic query filtering.
4. **Shared data (VatRate, Currency)** — Same tables used by everyone, but no concept of "master" vs "tenant" data.
5. **Connection string** — Single hardcoded `Data Source=invoiceapi.db` (SQLite).
6. **SQLite limitations** — No real concurrency, no server-based access, not suitable for production multi-tenant.

---

## 2. Target Architecture

### Core Principle: Self-Contained Tenant Databases

Each tenant database contains **everything** it needs to operate independently. All code tables (VatRate, Currency, NumberSequenceFormat, ContentTemplate) are **copied** from master on provisioning. This means:

- **Zero cross-DB queries** during normal tenant operations (invoices, clients, templates, etc.)
- **Zero cross-DB FK problems** — all FKs are local within each tenant DB
- **Cross-DB communication only for**: authentication (Users in master), company data sync, SysAdmin operations

### Database Provider: PostgreSQL, Database-Per-Tenant

```
┌──────────────────────────────────────────────────────────┐
│            MASTER DATABASE (PostgreSQL)                    │
│            invoiceapi_master                               │
│                                                           │
│  ┌───────────────────────┐  ┌───────────────────────────┐│
│  │ Client (IsIssuer=true)│  │ CompanySystemSettings     ││
│  │ = Company records     │  │  • DatabaseName            ││
│  │ (source of truth)     │  │  • ConnectionString        ││
│  └───────────────────────┘  │  • IsProvisioned           ││
│                             └───────────────────────────┘│
│  ┌──────────┐  ┌──────────────────────────────────────┐  │
│  │  User    │  │ Default code tables (templates for   │  │
│  │ (ALL     │  │ provisioning new tenants):            │  │
│  │  users)  │  │  • VatRate, Currency                  │  │
│  └──────────┘  │  • NumberSequenceFormat               │  │
│                │  • ContentTemplate                    │  │
│                └──────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────┘
         │                              │
         │ Auth only                    │ Copy on provisioning
         │ (login, JWT)                 │ + Company sync
         ▼                              ▼
┌────────────────────────┐  ┌────────────────────────┐
│ invoiceapi_tenant_acme │  │ invoiceapi_tenant_beta │  ...
│                        │  │                        │
│ ★ SELF-CONTAINED ★     │  │ ★ SELF-CONTAINED ★     │
│                        │  │                        │
│ Client (Issuer) ←sync  │  │ Client (Issuer) ←sync  │
│ Client (Customers)     │  │ Client (Customers)     │
│ Address, Contact       │  │ Address, Contact       │
│ BillingSettings        │  │ BillingSettings        │
│ Invoice, InvoiceItem   │  │ Invoice, InvoiceItem   │
│ InvoiceTemplate        │  │ InvoiceTemplate        │
│ NumberSequence         │  │ NumberSequence         │
│ NumberSequenceFormat   │  │ NumberSequenceFormat   │
│ ContentTemplate        │  │ ContentTemplate        │
│ VatRate                │  │ VatRate                │
│ Currency               │  │ Currency               │
│ AresCache              │  │ AresCache              │
│                        │  │                        │
│ ALL FKs are LOCAL ✓    │  │ ALL FKs are LOCAL ✓    │
│ Zero cross-DB queries  │  │ Zero cross-DB queries  │
└────────────────────────┘  └────────────────────────┘
```

### Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Tenant isolation | Database-per-tenant (PostgreSQL) | Complete physical isolation, independent backups, no cross-tenant risk |
| Code tables | ALL copied to tenant DB on provisioning | Zero cross-DB FKs, zero cross-DB queries during normal operations |
| Users | Master DB only | Central auth, single login, SysAdmin user management |
| Company (Issuer) | Both master AND tenant, kept in sync | Master = source of truth for tenant registry; Tenant = local copy for invoice FKs |
| DB provider | PostgreSQL, database-per-tenant | Production-grade, excellent EF Core support, per-tenant backup/restore |
| Cross-DB communication | Auth only + company sync | Minimal; tenant operates fully independently for all business logic |

---

## 3. Database Split Strategy

### MASTER DB Entities

| Entity | Purpose | Cross-DB Role |
|--------|---------|---------------|
| **User** | All system users | Only entity read cross-DB (during auth). `CompanyId` → Client.Id (IsIssuer). |
| **Client** (IsIssuer=true) | Company registry | Source of truth for company identity. Synced TO tenant on change. |
| **CompanySystemSettings** (NEW) | Tenant infrastructure config | 1:1 with Client (IsIssuer). ConnectionString, DatabaseName, provisioning status. |
| **VatRate** | Default VAT rates | Copied to tenant on provisioning. Master = template for new tenants. |
| **Currency** | Default currencies | Copied to tenant on provisioning. Master = template for new tenants. |
| **NumberSequenceFormat** | Default number formats | Copied to tenant on provisioning. Master = template for new tenants. |
| **ContentTemplate** | Default document templates | Copied to tenant on provisioning. Master = template for new tenants. |

### TENANT DB Entities

| Entity | Purpose | FK References |
|--------|---------|---------------|
| **Client** (IsIssuer=true) | Company data (synced from master) | Local — used by Invoice.IssuerId |
| **Client** (customers) | Tenant's customers | Local |
| **Address** | Client addresses | FK → Client (local) |
| **Contact** | Client contacts | FK → Client (local) |
| **BillingSettings** | Billing config | FK → Client (local) |
| **Invoice** | Invoices & credit notes | FK → Client, Currency (ALL LOCAL) |
| **InvoiceTemplate** | Invoice blueprints (TPH) | FK → Client (local) |
| **InvoiceItem** | Line items | FK → Invoice, VatRate (ALL LOCAL) |
| **NumberSequence** | Document counters | FK → NumberSequenceFormat (LOCAL) |
| **NumberSequenceFormat** | Format patterns (copied from master) | Local |
| **ContentTemplate** | Templates (copied from master) | Local |
| **VatRate** | VAT rates (copied from master) | Local — used by InvoiceItem.VatRateId |
| **Currency** | Currencies (copied from master) | Local — used by Invoice.CurrencyId |
| **AresCache** | ARES registry cache | Local |

### Cross-DB FK Summary: NONE

```
┌─────────────────────────────────────────────────────────────┐
│  ALL foreign keys are LOCAL within each tenant database.     │
│                                                              │
│  Invoice.CurrencyId      → Currency (local copy)       ✓ FK │
│  Invoice.IssuerId        → Client (local issuer copy)  ✓ FK │
│  Invoice.ClientId        → Client (local customer)     ✓ FK │
│  InvoiceItem.VatRateId   → VatRate (local copy)        ✓ FK │
│  NumberSequence.FormatId → NumberSequenceFormat (local) ✓ FK │
│  Client.PreferredCurrencyId → Currency (local copy)    ✓ FK │
│                                                              │
│  No navigation properties need to be removed.                │
│  No cross-DB validation needed.                              │
│  No IMasterDataCache needed for tenant operations.           │
└─────────────────────────────────────────────────────────────┘
```

### Cross-DB Communication Matrix

| Operation | Master DB | Tenant DB | Direction |
|-----------|-----------|-----------|-----------|
| User login | Read User, CompanySystemSettings | — | Master only |
| User CRUD | Read/Write User | — | Master only |
| SysAdmin: manage defaults | Read/Write VatRate, Currency, etc. | — | Master only |
| Tenant provisioning | Read defaults + CompanySystemSettings | Write copies of all code tables | Master → Tenant |
| Company data update (master) | Update Client (IsIssuer) | Update Client (IsIssuer) copy | Master → Tenant sync |
| Company data update (tenant) | Update Client (IsIssuer) | Update Client (IsIssuer) | Tenant → Master sync |
| Invoice CRUD | — | Read/Write (all local) | Tenant only |
| Client CRUD | — | Read/Write (all local) | Tenant only |
| Template editing | — | Read/Write (all local) | Tenant only |
| VatRate/Currency editing | — | Read/Write (local copies) | Tenant only |
| PDF export | — | Read (all local) | Tenant only |
| Email sending | — | Read (all local) | Tenant only |
| Dashboard | — | Read (all local) | Tenant only |

---

## 4. Company Data Synchronization

### The Problem

The Company (Client with IsIssuer=true) exists in **both** databases:
- **Master**: Source of truth for tenant registry (CompanySystemSettings references it).
- **Tenant**: Local copy used as `Invoice.Issuer` FK — needed for invoice generation, PDF export, etc.

When company data changes (name, address, tax number, etc.), both copies must stay in sync.

### Sync Strategy: Event-Driven with Service Layer

```
┌───────────────┐        ┌────────────────┐        ┌───────────────┐
│   Master DB   │ ──────→│ CompanySyncSvc │──────→ │   Tenant DB   │
│ Client update │  event │                │ write  │ Client update │
└───────────────┘        └────────────────┘        └───────────────┘

┌───────────────┐        ┌────────────────┐        ┌───────────────┐
│   Tenant DB   │ ──────→│ CompanySyncSvc │──────→ │   Master DB   │
│ Client update │  event │                │ write  │ Client update │
└───────────────┘        └────────────────┘        └───────────────┘
```

### ICompanySyncService

```csharp
/// <summary>
/// Keeps the Company (Client IsIssuer=true) data in sync between master and tenant DBs.
/// Called whenever company data is updated from either side.
///
/// Sync fields: CompanyName, TradingName, RegistrationNumber, TaxNumber, IsVatPayer
/// NOT synced: local-only data (Addresses, Contacts, BillingSettings — these are tenant-only)
/// </summary>
public interface ICompanySyncService
{
    /// <summary>
    /// Called when SysAdmin updates company data in master DB.
    /// Propagates changes to the tenant's local Client (IsIssuer) record.
    /// </summary>
    Task SyncMasterToTenantAsync(long companyId);

    /// <summary>
    /// Called when tenant user updates their own company data in tenant DB.
    /// Propagates changes back to the master Client (IsIssuer) record.
    /// </summary>
    Task SyncTenantToMasterAsync(long companyId);
}
```

### Synced Fields vs Local-Only Fields

| Field | Synced? | Notes |
|-------|---------|-------|
| CompanyName | Yes | Core identity — must match |
| TradingName | Yes | Core identity |
| RegistrationNumber | Yes | Core identity (IČO) |
| TaxNumber | Yes | Core identity (DIČ) |
| IsVatPayer | Yes | Affects invoice generation |
| **Address** | No | Tenant manages own addresses (could differ from registry) |
| **Contact** | No | Tenant manages own contacts |
| **BillingSettings** | No | Tenant-only (payment terms, bank accounts) |
| **PreferredCurrencyId** | No | Tenant-only (references local Currency copy) |
| IsActive | No | Master controls `CompanySystemSettings.IsActive`, tenant's Client.IsActive is always true |

### Sync Implementation

```csharp
public class CompanySyncService : ICompanySyncService
{
    private readonly MasterDbContext _masterContext;
    private readonly ITenantDbContextFactory _tenantFactory;

    public async Task SyncMasterToTenantAsync(long companyId)
    {
        // Read source of truth from master
        var masterClient = await _masterContext.Clients
            .FirstOrDefaultAsync(c => c.Id == companyId && c.IsIssuer);

        if (masterClient == null) return;

        // Open tenant context and update the local issuer copy
        using var tenantContext = await _tenantFactory.CreateContextAsync(companyId);
        var tenantIssuer = await tenantContext.Clients
            .FirstOrDefaultAsync(c => c.IsIssuer);

        if (tenantIssuer == null) return;

        // Copy synced fields
        tenantIssuer.CompanyName = masterClient.CompanyName;
        tenantIssuer.TradingName = masterClient.TradingName;
        tenantIssuer.RegistrationNumber = masterClient.RegistrationNumber;
        tenantIssuer.TaxNumber = masterClient.TaxNumber;
        tenantIssuer.IsVatPayer = masterClient.IsVatPayer;

        await tenantContext.SaveChangesAsync();
    }

    public async Task SyncTenantToMasterAsync(long companyId)
    {
        // Read from tenant
        using var tenantContext = await _tenantFactory.CreateContextAsync(companyId);
        var tenantIssuer = await tenantContext.Clients
            .FirstOrDefaultAsync(c => c.IsIssuer);

        if (tenantIssuer == null) return;

        // Update master
        var masterClient = await _masterContext.Clients
            .FirstOrDefaultAsync(c => c.Id == companyId && c.IsIssuer);

        if (masterClient == null) return;

        masterClient.CompanyName = tenantIssuer.CompanyName;
        masterClient.TradingName = tenantIssuer.TradingName;
        masterClient.RegistrationNumber = tenantIssuer.RegistrationNumber;
        masterClient.TaxNumber = tenantIssuer.TaxNumber;
        masterClient.IsVatPayer = tenantIssuer.IsVatPayer;

        await _masterContext.SaveChangesAsync();
    }
}
```

### When to Trigger Sync

| Trigger | Direction | Caller |
|---------|-----------|--------|
| SysAdmin edits company in master | Master → Tenant | CompanyController (master) calls `SyncMasterToTenantAsync` |
| Tenant user edits own company | Tenant → Master | ClientController (tenant) calls `SyncTenantToMasterAsync` when updating IsIssuer client |
| Tenant provisioning | Master → Tenant | TenantProvisioningService creates initial Issuer record from master data |

### Conflict Resolution

If both sides update simultaneously (unlikely but possible):
- **Last-write-wins** — the most recent `UpdatedAt` timestamp wins.
- In practice, SysAdmin edits are rare (company registration changes) and tenant edits are also rare (company name change).
- For V1, simple last-write-wins is sufficient. Can add optimistic concurrency (RowVersion) later if needed.

---

## 5. New & Modified Entities

### NEW: CompanySystemSettings (Master DB only)

```csharp
namespace InvoiceApi.Domain.Entities;

/// <summary>
/// System-level settings for a company (tenant).
/// 1:1 relationship with Client where IsIssuer = true.
/// Stores infrastructure configuration that is NOT business data:
/// connection strings, provisioning status, feature flags, etc.
///
/// Stored in MASTER database only — used to resolve tenant DB connections.
/// </summary>
public class CompanySystemSettings : BaseEntity
{
    /// <summary>
    /// FK to Client (where IsIssuer = true) in master DB.
    /// This is the company/tenant that these settings belong to.
    /// </summary>
    public long CompanyId { get; set; }

    /// <summary>
    /// Navigation to the Client record representing this company.
    /// </summary>
    public Client Company { get; set; } = null!;

    /// <summary>
    /// Unique database name for this tenant on the PostgreSQL server.
    /// Convention: "invoiceapi_tenant_{slug}"
    /// Example: "invoiceapi_tenant_acme", "invoiceapi_tenant_novak_sro"
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Full connection string override for this tenant.
    /// If null, the system constructs it from DatabaseName + default server config.
    /// Use this for tenants on a dedicated server or custom configuration.
    /// Example: "Host=dedicated-host;Database=invoiceapi_tenant_acme;..."
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// When was this tenant's database provisioned (created, migrated, defaults copied)?
    /// Null = not yet provisioned.
    /// </summary>
    public DateTime? ProvisionedAt { get; set; }

    /// <summary>
    /// Is this tenant's database provisioned and ready to use?
    /// False during initial setup or if provisioning failed.
    /// </summary>
    public bool IsProvisioned { get; set; } = false;

    /// <summary>
    /// Is this tenant active? Controls access at system level.
    /// Inactive tenants: users cannot log in, tenant DB not accessed.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Maximum number of users allowed for this tenant.
    /// Null = unlimited (or system default).
    /// </summary>
    public int? MaxUsers { get; set; }

    /// <summary>
    /// Optional notes for SysAdmin (e.g., "Trial until 2026-06-01", "Premium plan").
    /// </summary>
    public string? AdminNotes { get; set; }
}
```

### UNCHANGED Entities

Since all code tables are now local to each tenant DB, **no navigation properties need to be removed**:

```csharp
// Invoice.cs — NO CHANGES
public class Invoice : BaseEntity
{
    public long CurrencyId { get; set; }
    public Currency Currency { get; set; } = null!;  // KEPT — Currency is local in tenant DB
    // ... all other props unchanged ...
}

// InvoiceItem.cs — NO CHANGES
public class InvoiceItem : BaseEntity
{
    public long? VatRateId { get; set; }
    public VatRate? VatRate { get; set; }  // KEPT — VatRate is local in tenant DB
    // ... all other props unchanged ...
}

// NumberSequence.cs — NO CHANGES
public class NumberSequence : BaseEntity
{
    public long NumberSequenceFormatId { get; set; }
    public NumberSequenceFormat NumberSequenceFormat { get; set; } = null!;  // KEPT — local
}

// Client.cs — NO CHANGES
public class Client : BaseEntity
{
    public long? PreferredCurrencyId { get; set; }
    public Currency? PreferredCurrency { get; set; }  // KEPT — Currency is local in tenant DB
}

// User.cs — NO CHANGES
public class User : BaseEntity
{
    public long? CompanyId { get; set; }
    public Client? Company { get; set; }  // Points to Client (IsIssuer) in master DB
}
```

**This is the biggest win of copying all code tables: zero entity changes needed.**

---

## 6. Tenant Lifecycle

### 6.1 Provisioning (New Company Setup)

```
SysAdmin creates Client (IsIssuer=true) in master DB
        │
        ▼
SysAdmin creates CompanySystemSettings for this Client
        │
        ▼
TenantProvisioningService.ProvisionAsync(companyId)
        │
        ├── 1. Read CompanySystemSettings.DatabaseName
        │
        ├── 2. CREATE DATABASE invoiceapi_tenant_{name} on PostgreSQL server
        │
        ├── 3. Apply TenantDbContext migrations (create all tables)
        │
        ├── 4. Copy ALL code tables from master → tenant DB:
        │       ├── VatRate (all active records)
        │       ├── Currency (all active records)
        │       ├── NumberSequenceFormat (all active records)
        │       └── ContentTemplate (all default records)
        │       NOTE: Records get NEW local IDs in tenant DB
        │
        ├── 5. Create default NumberSequences (Invoice + CreditNote)
        │       using the locally-copied NumberSequenceFormat IDs
        │
        ├── 6. Create Issuer Client record in tenant DB
        │       (copy CompanyName, RegistrationNumber, TaxNumber, IsVatPayer
        │        from master Client where IsIssuer=true)
        │
        ├── 7. Update CompanySystemSettings:
        │       ProvisionedAt = DateTime.UtcNow
        │       IsProvisioned = true
        │
        └── 8. Log provisioning result
```

### 6.2 Provisioning Code

```csharp
public class TenantProvisioningService : ITenantProvisioningService
{
    private readonly MasterDbContext _masterContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TenantProvisioningService> _logger;

    public async Task ProvisionAsync(long companyId)
    {
        var settings = await _masterContext.CompanySystemSettings
            .Include(s => s.Company)
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);

        if (settings == null)
            throw new InvalidOperationException($"No CompanySystemSettings for company {companyId}");

        if (settings.IsProvisioned)
            throw new InvalidOperationException($"Company {companyId} is already provisioned");

        // 1. Create database on PostgreSQL server
        var connString = ResolveConnectionString(settings);
        await CreateDatabaseAsync(settings.DatabaseName);

        // 2. Apply migrations
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connString).Options;
        using var tenantContext = new TenantDbContext(options);
        await tenantContext.Database.MigrateAsync();

        // 3. Copy all code tables
        await CopyCodeTablesAsync(tenantContext);

        // 4. Create issuer record from master company data
        var issuer = new Client
        {
            CompanyName = settings.Company.CompanyName,
            TradingName = settings.Company.TradingName,
            RegistrationNumber = settings.Company.RegistrationNumber,
            TaxNumber = settings.Company.TaxNumber,
            IsVatPayer = settings.Company.IsVatPayer,
            IsIssuer = true,
            IsActive = true
        };
        tenantContext.Clients.Add(issuer);
        await tenantContext.SaveChangesAsync();

        // 5. Create default number sequences
        await CreateDefaultSequencesAsync(tenantContext);

        // 6. Mark as provisioned
        settings.IsProvisioned = true;
        settings.ProvisionedAt = DateTime.UtcNow;
        await _masterContext.SaveChangesAsync();

        _logger.LogInformation("Tenant provisioned: Company {CompanyId}, DB {DatabaseName}",
            companyId, settings.DatabaseName);
    }

    private async Task CopyCodeTablesAsync(TenantDbContext tenantContext)
    {
        // Copy VatRates
        var vatRates = await _masterContext.VatRates.AsNoTracking().ToListAsync();
        foreach (var vr in vatRates)
        {
            tenantContext.VatRates.Add(new VatRate
            {
                Name = vr.Name, Rate = vr.Rate, ValidFrom = vr.ValidFrom,
                ValidTo = vr.ValidTo, IsReduced = vr.IsReduced,
                IsDefault = vr.IsDefault, IsActive = vr.IsActive
            });
        }

        // Copy Currencies
        var currencies = await _masterContext.Currencies.AsNoTracking().ToListAsync();
        foreach (var c in currencies)
        {
            tenantContext.Currencies.Add(new Currency
            {
                Code = c.Code, Name = c.Name, Symbol = c.Symbol,
                DecimalPlaces = c.DecimalPlaces, IsActive = c.IsActive,
                SortOrder = c.SortOrder, DisplayFormat = c.DisplayFormat
            });
        }

        // Copy NumberSequenceFormats
        var formats = await _masterContext.NumberSequenceFormats.AsNoTracking().ToListAsync();
        foreach (var f in formats)
        {
            tenantContext.NumberSequenceFormats.Add(new NumberSequenceFormat
            {
                Name = f.Name, FormatPattern = f.FormatPattern,
                CounterDigits = f.CounterDigits, ResetsYearly = f.ResetsYearly,
                ResetsMonthly = f.ResetsMonthly, IsActive = f.IsActive
            });
        }

        // Copy ContentTemplates
        var templates = await _masterContext.ContentTemplates
            .Where(ct => ct.IsDefault && ct.IsActive)
            .AsNoTracking().ToListAsync();
        foreach (var t in templates)
        {
            tenantContext.ContentTemplates.Add(new ContentTemplate
            {
                Name = t.Name, Subject = t.Subject, HtmlBody = t.HtmlBody,
                TemplateType = t.TemplateType, IsDefault = t.IsDefault,
                IsActive = true, Description = t.Description
            });
        }

        await tenantContext.SaveChangesAsync();
    }
}
```

### 6.3 Deactivation & Reactivation

```
Deactivate:
  1. Set CompanySystemSettings.IsActive = false
  2. All users of this company → login blocked (checked at auth time)
  3. Tenant DB is NOT dropped — data preserved

Reactivate:
  1. Set CompanySystemSettings.IsActive = true
  2. Apply any pending tenant migrations
  3. Users can log in again
```

### 6.4 Code Table Updates After Provisioning

Once a tenant is provisioned, its code tables are **independent** from master:

| Scenario | Behavior |
|----------|----------|
| Tenant edits own VatRate | Only affects this tenant. Master unchanged. |
| Tenant adds custom Currency | Only in this tenant's DB. Other tenants unaffected. |
| SysAdmin updates master VatRate | Existing tenants NOT affected. Only new tenants get updated defaults. |
| Legal VAT rate change | SysAdmin updates master defaults. Optionally pushes update to all tenants via admin tool (Phase 2 feature). |

> **Future enhancement**: `SysAdmin → "Push defaults to all tenants"` button that copies current master code tables to all/selected tenants. Not needed for V1.

---

## 7. Service Layer Redesign

### 7.1 Two DbContexts

```csharp
/// <summary>
/// Master database context — central registry and defaults.
/// Contains: User, Client (IsIssuer), CompanySystemSettings,
/// and default code tables (VatRate, Currency, NumberSequenceFormat, ContentTemplate).
/// Connection: always the master DB (from config).
/// </summary>
public class MasterDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<Client> Clients { get; set; }                    // IsIssuer=true only
    public DbSet<CompanySystemSettings> CompanySystemSettings { get; set; }
    public DbSet<VatRate> VatRates { get; set; }                  // defaults
    public DbSet<Currency> Currencies { get; set; }               // defaults
    public DbSet<NumberSequenceFormat> NumberSequenceFormats { get; set; } // defaults
    public DbSet<ContentTemplate> ContentTemplates { get; set; }  // defaults
}

/// <summary>
/// Tenant database context — self-contained per-company data.
/// Connection string resolved at runtime from CompanySystemSettings.
/// Contains ALL tables needed for business operations — zero cross-DB dependencies.
/// </summary>
public class TenantDbContext : DbContext
{
    public DbSet<Client> Clients { get; set; }                    // issuer + customers
    public DbSet<Address> Addresses { get; set; }
    public DbSet<Contact> Contacts { get; set; }
    public DbSet<BillingSettings> BillingSettings { get; set; }
    public DbSet<Invoice> Invoices { get; set; }
    public DbSet<InvoiceTemplate> InvoiceTemplates { get; set; }
    public DbSet<InvoiceItem> InvoiceItems { get; set; }
    public DbSet<NumberSequence> NumberSequences { get; set; }
    public DbSet<NumberSequenceFormat> NumberSequenceFormats { get; set; }
    public DbSet<ContentTemplate> ContentTemplates { get; set; }
    public DbSet<VatRate> VatRates { get; set; }
    public DbSet<Currency> Currencies { get; set; }
    public DbSet<AresCache> AresCaches { get; set; }
}
```

### 7.2 Tenant Resolver & Context Factory

```csharp
/// <summary>
/// Reads current tenant from HTTP request (JWT CompanyId claim).
/// </summary>
public interface ITenantResolver
{
    long? GetCurrentCompanyId();
    bool IsSysAdmin();
}

/// <summary>
/// Creates TenantDbContext with correct connection string per company.
/// </summary>
public interface ITenantDbContextFactory
{
    TenantDbContext CreateContext();
    Task<TenantDbContext> CreateContextAsync(long companyId);
}

public class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly ITenantResolver _tenantResolver;
    private readonly MasterDbContext _masterContext;
    private readonly IConfiguration _configuration;

    public TenantDbContext CreateContext()
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new UnauthorizedAccessException("No company context");

        return CreateContextForCompany(companyId);
    }

    public async Task<TenantDbContext> CreateContextAsync(long companyId)
    {
        return CreateContextForCompany(companyId);
    }

    private TenantDbContext CreateContextForCompany(long companyId)
    {
        var settings = _masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefault(s => s.CompanyId == companyId && s.IsProvisioned);

        if (settings == null)
            throw new InvalidOperationException($"Tenant not provisioned: company {companyId}");

        var connString = !string.IsNullOrEmpty(settings.ConnectionString)
            ? settings.ConnectionString
            : BuildConnectionString(settings.DatabaseName);

        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(connString)
            .Options;

        return new TenantDbContext(options);
    }

    private string BuildConnectionString(string databaseName)
    {
        // Base template from config (without Database=...)
        var template = _configuration.GetConnectionString("TenantTemplate")
            ?? "Host=localhost;Port=5432;Username=app;Password=***";
        return $"{template};Database={databaseName}";
    }
}
```

### 7.3 Service Registration

```csharp
// ============================================
// MASTER DB — fixed connection string
// ============================================
builder.Services.AddDbContext<MasterDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("MasterConnection"),
        b => b.MigrationsAssembly("InvoiceApi.Infrastructure")));

// ============================================
// TENANT DB — resolved per-request
// ============================================
builder.Services.AddScoped<ITenantResolver, HttpContextTenantResolver>();
builder.Services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();
builder.Services.AddScoped<TenantDbContext>(sp =>
    sp.GetRequiredService<ITenantDbContextFactory>().CreateContext());

// ============================================
// SERVICES — clean separation
// ============================================

// Master-only services (auth, user management)
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUserService, UserService>();

// Company sync service (needs both contexts)
builder.Services.AddScoped<ICompanySyncService, CompanySyncService>();

// Tenant provisioning (SysAdmin, needs both contexts)
builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

// ALL business services use ONLY TenantDbContext — zero master dependency!
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<IVatRateService, VatRateService>();
builder.Services.AddScoped<ICurrencyService, CurrencyService>();
builder.Services.AddScoped<INumberSequenceService, NumberSequenceService>();
builder.Services.AddScoped<IContentTemplateService, ContentTemplateService>();
builder.Services.AddScoped<IInvoiceTemplateService, InvoiceTemplateService>();
builder.Services.AddScoped<IPdfExportService, PdfExportService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
```

### 7.4 Service Injection — Simplified

```csharp
/// <summary>
/// InvoiceService — uses ONLY TenantDbContext.
/// All data (Currency, VatRate, Client) is local in tenant DB.
/// Zero cross-DB queries, zero IMasterDataCache needed.
/// This is essentially UNCHANGED from current implementation —
/// just swap ApplicationDbContext → TenantDbContext.
/// </summary>
public class InvoiceService : IInvoiceService
{
    private readonly TenantDbContext _context;  // was ApplicationDbContext

    public InvoiceService(TenantDbContext context)
    {
        _context = context;
    }

    public async Task<InvoiceDto> GetByIdAsync(long id)
    {
        // All .Include() calls work — everything is local!
        var invoice = await _context.Invoices
            .Include(i => i.Currency)           // local FK ✓
            .Include(i => i.Issuer)             // local FK ✓
            .Include(i => i.Client)             // local FK ✓
            .Include(i => i.InvoiceItem)
                .ThenInclude(ii => ii.VatRate)  // local FK ✓
            .FirstOrDefaultAsync(i => i.Id == id);

        return invoice?.MapToInvoiceDto();
    }
}

/// <summary>
/// VatRateService — uses ONLY TenantDbContext (was MasterDbContext in previous design).
/// Each tenant has its own copy of VatRates.
/// Admin users can edit their tenant's rates independently.
/// </summary>
public class VatRateService : IVatRateService
{
    private readonly TenantDbContext _context;  // was ApplicationDbContext

    public VatRateService(TenantDbContext context)
    {
        _context = context;
    }

    // All methods unchanged — just use _context instead of old ApplicationDbContext
}

/// <summary>
/// AuthService — uses ONLY MasterDbContext (Users are master-only).
/// </summary>
public class AuthService : IAuthService
{
    private readonly MasterDbContext _masterContext;

    public AuthService(MasterDbContext masterContext)
    {
        _masterContext = masterContext;
    }

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        var user = await _masterContext.Users
            .FirstOrDefaultAsync(u => u.Email == email);

        if (user == null || !BCrypt.Verify(password, user.PasswordHash))
            return AuthResult.Fail("Invalid credentials");

        // Check tenant is active and provisioned
        if (user.CompanyId.HasValue)
        {
            var settings = await _masterContext.CompanySystemSettings
                .FirstOrDefaultAsync(s => s.CompanyId == user.CompanyId);

            if (settings == null || !settings.IsActive)
                return AuthResult.Fail("Company is inactive. Contact administrator.");

            if (!settings.IsProvisioned)
                return AuthResult.Fail("Company setup is not complete. Contact administrator.");
        }

        return AuthResult.Success(GenerateJwt(user));
    }
}

/// <summary>
/// ClientService — uses TenantDbContext.
/// When updating the Issuer client, also triggers company sync.
/// </summary>
public class ClientService : IClientService
{
    private readonly TenantDbContext _context;
    private readonly ICompanySyncService _companySyncService;
    private readonly ITenantResolver _tenantResolver;

    public async Task<ClientDto> UpdateAsync(long id, UpdateClientDto dto)
    {
        var client = await _context.Clients.FindAsync(id);
        // ... update fields ...
        await _context.SaveChangesAsync();

        // If this is the issuer, sync changes back to master
        if (client.IsIssuer)
        {
            var companyId = _tenantResolver.GetCurrentCompanyId();
            if (companyId.HasValue)
                await _companySyncService.SyncTenantToMasterAsync(companyId.Value);
        }

        return client.MapToClientDto();
    }
}
```

### 7.5 Impact on Existing Services

| Service | Current DI | New DI | Changes Needed |
|---------|-----------|--------|----------------|
| AuthService | ApplicationDbContext | **MasterDbContext** | Swap context + add tenant status check |
| UserService | ApplicationDbContext | **MasterDbContext** | Swap context only |
| InvoiceService | ApplicationDbContext | **TenantDbContext** | Swap context only (all data is local!) |
| ClientService | ApplicationDbContext | **TenantDbContext** + ICompanySyncService | Swap context + add sync trigger for issuer |
| VatRateService | ApplicationDbContext | **TenantDbContext** | Swap context only |
| CurrencyService | ApplicationDbContext | **TenantDbContext** | Swap context only |
| NumberSequenceService | ApplicationDbContext | **TenantDbContext** | Swap context only |
| ContentTemplateService | ApplicationDbContext | **TenantDbContext** | Swap context only |
| InvoiceTemplateService | ApplicationDbContext | **TenantDbContext** | Swap context only |
| PdfExportService | ApplicationDbContext | **TenantDbContext** | Swap context only |
| EmailService | ApplicationDbContext | **TenantDbContext** | Swap context only |
| DashboardService | ApplicationDbContext | **TenantDbContext** | Swap context only |

**Key insight**: Most services just swap `ApplicationDbContext` → `TenantDbContext`. No logic changes needed because all data is local. This is a mechanical refactor.

---

## 8. Authentication & Impersonation Changes

### 8.1 Login Flow

```
BEFORE:
  AuthService reads User from single DB → JWT with CompanyId claim

AFTER:
  AuthService reads User from MASTER DB → JWT with CompanyId claim
  + Checks CompanySystemSettings.IsActive and IsProvisioned
  (JWT structure unchanged — CompanyId still points to Client.Id where IsIssuer=true)
```

### 8.2 Impersonation Flow

```
BEFORE:
  SysAdmin sends X-Company-Id header → claim added → controllers filter by CompanyId

AFTER:
  SysAdmin sends X-Company-Id header → claim added (SAME)
  → TenantResolver reads CompanyId (SAME)
  → TenantDbContextFactory reads CompanySystemSettings → resolves connection string (NEW)
  → TenantDbContext connects to correct tenant DB (NEW)
  → All queries are completely isolated to that tenant DB (SAME behavior, better isolation)
```

`ImpersonationMiddleware` stays **identical**. No changes needed.

### 8.3 SysAdmin Without Impersonation

When SysAdmin has no `X-Company-Id` header:
- Only master-only endpoints work (User CRUD, Company/CompanySystemSettings management)
- Only master code table management (editing default VatRates, Currencies, templates for future tenants)
- Tenant endpoints → 403: "Select a company to impersonate first"

### 8.4 TenantContextMiddleware

```csharp
/// <summary>
/// Validates tenant context for endpoints that require it.
/// Runs AFTER ImpersonationMiddleware.
/// Master-only endpoints (auth, user, company management) are skipped.
/// </summary>
public class TenantContextMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly HashSet<string> MasterOnlyPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/auth",
        "/api/user",
        "/api/company"  // CompanySystemSettings + master code table management
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (MasterOnlyPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var companyIdClaim = context.User.FindFirst("CompanyId")?.Value;
            if (string.IsNullOrEmpty(companyIdClaim))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new
                    { Error = "No company context. SysAdmin must impersonate via X-Company-Id header." });
                return;
            }

            if (long.TryParse(companyIdClaim, out var companyId))
            {
                var masterDb = context.RequestServices.GetRequiredService<MasterDbContext>();
                var settings = await masterDb.CompanySystemSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.CompanyId == companyId);

                if (settings == null || !settings.IsProvisioned || !settings.IsActive)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new
                        { Error = "Company is not provisioned or inactive." });
                    return;
                }
            }
        }

        await _next(context);
    }
}
```

### 8.5 Middleware Pipeline

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseImpersonation();      // Existing — no changes
app.UseTenantContext();       // NEW — validates tenant status
app.MapControllers();
```

---

## 9. Migration Strategy

### 9.1 Two Migration Contexts

```
InvoiceApi.Infrastructure/
├── Migrations/
│   ├── Master/                    ← MasterDbContext migrations
│   │   ├── 001_InitialMaster.cs
│   │   └── MasterDbContextModelSnapshot.cs
│   └── Tenant/                    ← TenantDbContext migrations
│       ├── 001_InitialTenant.cs
│       └── TenantDbContextModelSnapshot.cs
├── Data/
│   ├── MasterDbContext.cs
│   ├── TenantDbContext.cs
│   ├── MasterDesignTimeFactory.cs   ← For EF CLI
│   └── TenantDesignTimeFactory.cs   ← For EF CLI
```

### 9.2 EF Commands

```bash
# Master migrations
dotnet ef migrations add InitialMaster \
  --context MasterDbContext \
  --output-dir Migrations/Master \
  --project InvoiceApi.Infrastructure \
  --startup-project InvoiceApi.API

# Tenant migrations
dotnet ef migrations add InitialTenant \
  --context TenantDbContext \
  --output-dir Migrations/Tenant \
  --project InvoiceApi.Infrastructure \
  --startup-project InvoiceApi.API
```

### 9.3 Startup Logic

```csharp
// 1. Migrate master DB
using (var scope = app.Services.CreateScope())
{
    var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
    await masterDb.Database.MigrateAsync();
    // Seed default code tables (VatRate, Currency, NumberSequenceFormat, ContentTemplate)
    // Seed SysAdmin user
}

// 2. Migrate all active tenant DBs
using (var scope = app.Services.CreateScope())
{
    var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
    var tenants = await masterDb.CompanySystemSettings
        .Where(s => s.IsProvisioned && s.IsActive)
        .AsNoTracking().ToListAsync();

    var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
    foreach (var tenant in tenants)
    {
        try
        {
            using var tenantDb = await factory.CreateContextAsync(tenant.CompanyId);
            await tenantDb.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to migrate tenant {CompanyId}", tenant.CompanyId);
            // Don't crash — other tenants should still work
        }
    }
}
```

### 9.4 Data Migration: SQLite → PostgreSQL + Multi-Tenant Split

```
Phase A: Provider Switch (SQLite → PostgreSQL)
──────────────────────────────────────────────
A1: Install Npgsql.EntityFrameworkCore.PostgreSQL NuGet
A2: Remove Microsoft.EntityFrameworkCore.Sqlite NuGet
A3: Update Program.cs (UseSqlite → UseNpgsql)
A4: Create fresh initial migration for PostgreSQL
A5: Export SQLite data → import to PostgreSQL (one-time migration tool)
A6: Verify all tests pass

Phase B: Multi-Tenant Split
────────────────────────────
B1: Create master DB (invoiceapi_master) with MasterDbContext migration
B2: Copy to master:
    - User (all rows)
    - Client WHERE IsIssuer = true
    - VatRate (all rows — these become "defaults")
    - Currency (all rows — these become "defaults")
    - NumberSequenceFormat (all rows)
    - ContentTemplate (default ones)
B3: Create CompanySystemSettings for each Client (IsIssuer=true)
B4: For each company:
    B4a: CREATE DATABASE invoiceapi_tenant_{name}
    B4b: Apply TenantDbContext migration
    B4c: Copy ALL data for this company:
         - Client (IsIssuer copy + all customers for this company)
         - All Invoices + InvoiceItems (where IssuerId = this company)
         - All InvoiceTemplates
         - NumberSequences + NumberSequenceFormats
         - ContentTemplates
         - VatRate, Currency (full copies)
         - Addresses, Contacts, BillingSettings
         - AresCache
B5: Verify data integrity per tenant (row counts, FK consistency)
B6: Update appsettings.json with PostgreSQL connection strings
B7: Deploy new code
```

---

## 10. Testing Strategy

### 10.1 Unit Tests — Tenant Resolution

```csharp
[Fact]
public void GetCurrentCompanyId_ReturnsClaimValue()
[Fact]
public void GetCurrentCompanyId_ReturnsNull_WhenSysAdminNotImpersonating()
[Fact]
public void IsSysAdmin_ReturnsTrue_WhenRoleIsSysAdmin()
```

### 10.2 Unit Tests — Company Sync

```csharp
[Fact]
public async Task SyncMasterToTenant_UpdatesIssuerInTenantDb()
{
    // Arrange: master Client has updated CompanyName
    // Act: SyncMasterToTenantAsync
    // Assert: tenant's Issuer Client has new CompanyName
}

[Fact]
public async Task SyncTenantToMaster_UpdatesMasterClient()
{
    // Arrange: tenant Issuer Client has updated TaxNumber
    // Act: SyncTenantToMasterAsync
    // Assert: master Client has new TaxNumber
}

[Fact]
public async Task SyncMasterToTenant_DoesNotAffectAddresses()
{
    // Arrange: tenant has custom addresses
    // Act: SyncMasterToTenantAsync
    // Assert: tenant addresses unchanged
}
```

### 10.3 Unit Tests — Business Services (Minimal Change)

Since services just swap `ApplicationDbContext` → `TenantDbContext`, existing unit tests need only DI changes:

```csharp
// BEFORE
var context = CreateInMemoryApplicationDbContext();
var service = new InvoiceService(context);

// AFTER
var context = CreateInMemoryTenantDbContext();  // Same schema, just different class
var service = new InvoiceService(context);

// All test assertions remain unchanged!
```

### 10.4 Integration Tests — Tenant Provisioning

```csharp
[Fact]
public async Task ProvisionTenant_CreatesDatabaseWithAllCodeTables()
{
    // Arrange: company + CompanySystemSettings in master
    // Act: ProvisionAsync(companyId)
    // Assert:
    //   - Tenant DB created
    //   - VatRate count matches master
    //   - Currency count matches master
    //   - NumberSequenceFormat count matches master
    //   - ContentTemplate defaults copied
    //   - Default NumberSequences created (Invoice + CreditNote)
    //   - Issuer Client created
    //   - CompanySystemSettings.IsProvisioned == true
}
```

### 10.5 Integration Tests — Full Tenant Isolation

```csharp
[Fact]
public async Task TwoTenants_HaveCompletelyIsolatedData()
{
    // Arrange: provision two tenants
    // Act: create invoice in tenant 1
    // Assert: invoice visible in tenant 1, invisible in tenant 2
}

[Fact]
public async Task Tenant_CanEditOwnVatRates_WithoutAffectingOtherTenants()
{
    // Arrange: provision two tenants (both have default 21% VatRate)
    // Act: tenant 1 changes rate to 23%
    // Assert: tenant 2 still has 21%
}
```

### 10.6 Integration Tests — Impersonation

```csharp
[Fact]
public async Task SysAdmin_SwitchesBetweenTenants_SeesCorrectData()
{
    // Arrange: two tenants with different invoices
    // Act: impersonate tenant 1, query invoices
    // Assert: only tenant 1's invoices

    // Act: impersonate tenant 2, query invoices
    // Assert: only tenant 2's invoices
}
```

### 10.7 Integration Tests — Auth

```csharp
[Fact]
public async Task Login_FailsWhenTenantNotProvisioned()
[Fact]
public async Task Login_FailsWhenTenantInactive()
[Fact]
public async Task Login_SucceedsWhenTenantProvisionedAndActive()
```

---

## 11. Implementation Phases

### Phase 0: SQLite → PostgreSQL
**Goal**: Switch DB provider before multi-tenancy. Clean separation of concerns.

| # | Task | Files | Effort |
|---|------|-------|--------|
| 0.1 | Add `Npgsql.EntityFrameworkCore.PostgreSQL` NuGet | Infrastructure.csproj | Small |
| 0.2 | Remove SQLite NuGet | Infrastructure.csproj | Small |
| 0.3 | Update `AddDbContext` (`UseSqlite` → `UseNpgsql`) | API/Program.cs | Small |
| 0.4 | Update DesignTimeFactory | Infrastructure/Data/ | Small |
| 0.5 | Create fresh initial migration for PostgreSQL | Infrastructure/Migrations/ | Medium |
| 0.6 | Write data migration script (SQLite → PostgreSQL) | Tool/script | Medium |
| 0.7 | Update appsettings.json | API/appsettings.json | Small |
| 0.8 | Verify all existing tests pass | Tests/ | Medium |

### Phase 1: Foundation Infrastructure
**Goal**: Add multi-tenant plumbing without breaking existing functionality.

| # | Task | Files | Effort |
|---|------|-------|--------|
| 1.1 | Create `CompanySystemSettings` entity | Domain/Entities/ | Small |
| 1.2 | Create `MasterDbContext` (Users, Clients IsIssuer, CompanySystemSettings, default code tables) | Infrastructure/Data/ | Medium |
| 1.3 | Create `TenantDbContext` (all tenant tables — same as current ApplicationDbContext minus User) | Infrastructure/Data/ | Medium |
| 1.4 | Create `ITenantResolver` + `HttpContextTenantResolver` | Domain/Interfaces/, Infrastructure/Service/ | Small |
| 1.5 | Create `ITenantDbContextFactory` + implementation | Domain/Interfaces/, Infrastructure/Service/ | Medium |
| 1.6 | Set up dual migration folders (Master/, Tenant/) | Infrastructure/Migrations/ | Small |
| 1.7 | Unit tests for infrastructure | Tests.Unit/ | Medium |

### Phase 2: Service Layer Migration
**Goal**: Swap ApplicationDbContext → MasterDbContext or TenantDbContext per service.

| # | Task | Files | Effort |
|---|------|-------|--------|
| 2.1 | `AuthService` → MasterDbContext + tenant status check | Infrastructure/Service/ | Medium |
| 2.2 | `UserService` → MasterDbContext | Infrastructure/Service/ | Small |
| 2.3 | `InvoiceService` → TenantDbContext | Infrastructure/Service/ | Small (just swap) |
| 2.4 | `ClientService` → TenantDbContext + ICompanySyncService | Infrastructure/Service/ | Medium |
| 2.5 | `VatRateService` → TenantDbContext | Infrastructure/Service/ | Small (just swap) |
| 2.6 | `CurrencyService` → TenantDbContext | Infrastructure/Service/ | Small (just swap) |
| 2.7 | `NumberSequenceService` → TenantDbContext | Infrastructure/Service/ | Small (just swap) |
| 2.8 | `ContentTemplateService` → TenantDbContext | Infrastructure/Service/ | Small (just swap) |
| 2.9 | All other services → TenantDbContext | Infrastructure/Service/ | Small (just swap) |
| 2.10 | Create `ICompanySyncService` + implementation | Domain/Interfaces/, Infrastructure/Service/ | Medium |
| 2.11 | Update unit tests (swap InMemory context type) | Tests.Unit/ | Medium |

### Phase 3: Tenant Provisioning & Middleware
**Goal**: Build tenant lifecycle and access control.

| # | Task | Files | Effort |
|---|------|-------|--------|
| 3.1 | Create `ITenantProvisioningService` + implementation | Domain/Interfaces/, Infrastructure/Service/ | Large |
| 3.2 | Create `TenantContextMiddleware` | API/Middleware/ | Medium |
| 3.3 | Create/Update `CompanyController` (CRUD + provisioning trigger) | API/Controller/ | Medium |
| 3.4 | Update `Program.cs` (dual DB registration, middleware pipeline, startup migrations) | API/Program.cs | Medium |
| 3.5 | Integration tests for provisioning + isolation | Tests.Integration/ | Large |

### Phase 4: Data Migration
**Goal**: Migrate existing data to multi-DB structure.

| # | Task | Files | Effort |
|---|------|-------|--------|
| 4.1 | Write migration tool (single DB → master + per-tenant DBs) | New console app or admin endpoint | Large |
| 4.2 | Test with production data copy | Manual testing | Large |
| 4.3 | Document rollback plan | Documentation | Small |

### Phase 5: Blazor UI Updates
**Goal**: Update UI for multi-tenant operation.

| # | Task | Files | Effort |
|---|------|-------|--------|
| 5.1 | Update `ApiClientBase` (pass `X-Company-Id` for SysAdmin) | BlazorUI/Services/ | Medium |
| 5.2 | Add company switcher for SysAdmin | BlazorUI/Shared/ | Medium |
| 5.3 | Add CompanySystemSettings management page | BlazorUI/Pages/ | Medium |
| 5.4 | Add "Provision Tenant" button for SysAdmin | BlazorUI/Pages/ | Small |
| 5.5 | End-to-end testing | Manual | Large |

---

## 12. Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| **Data loss during SQLite → PostgreSQL** | Critical | Full backup. Run on copy. Verify row counts. Keep SQLite as rollback. |
| **Data loss during multi-tenant split** | Critical | Run on copy. Verify per-tenant counts + FK integrity. Keep original DB. |
| **Company data out of sync** (master ↔ tenant) | High | ICompanySyncService. Trigger sync on every issuer update. Last-write-wins for V1. |
| **Tenant DB not provisioned** | Medium | TenantContextMiddleware blocks access. Login check in AuthService. Clear errors. |
| **Legal VatRate change needs propagation** | Medium | V1: tenants manage own rates. Future: "Push defaults" admin tool. |
| **Many tenant DBs → PostgreSQL resource usage** | Medium | PostgreSQL handles hundreds of DBs well. Monitor connection counts. |
| **Migration ordering: tenant migrations on all DBs** | Medium | Startup loop with per-tenant error handling. Log failures. |
| **SysAdmin forgets to impersonate** | Low | TenantContextMiddleware returns 403. UI shows company selector. |
| **Code table divergence across tenants** | Low | By design — tenants are independent. Master holds defaults for new tenants. |

---

## Appendix A: Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "MasterConnection": "Host=localhost;Port=5432;Database=invoiceapi_master;Username=app;Password=***",
    "TenantTemplate": "Host=localhost;Port=5432;Username=app;Password=***"
  },
  "JwtSettings": {
    "Secret": "...",
    "Issuer": "InvoiceApi",
    "Audience": "InvoiceApiClient",
    "ExpirationHours": 24
  }
}
```

### PostgreSQL Database Naming Convention

```
invoiceapi_master          ← Master DB (always exists)
invoiceapi_tenant_acme     ← Tenant: Acme s.r.o.
invoiceapi_tenant_novak    ← Tenant: Novák a syn s.r.o.
invoiceapi_tenant_xyz      ← Tenant: XYZ Corp
```

---

## Appendix B: File Structure Changes

```
InvoiceApi.Domain/
├── Entities/
│   ├── CompanySystemSettings.cs     ← NEW (1:1 with Client IsIssuer)
│   ├── Client.cs                    ← UNCHANGED
│   ├── Invoice.cs                   ← UNCHANGED (Currency nav KEPT — local FK)
│   ├── InvoiceItem.cs               ← UNCHANGED (VatRate nav KEPT — local FK)
│   ├── NumberSequence.cs            ← UNCHANGED (Format nav KEPT — local FK)
│   ├── User.cs                      ← UNCHANGED (Company nav KEPT — master FK)
│   └── (all other entities)         ← UNCHANGED
├── Interfaces/
│   ├── ITenantResolver.cs           ← NEW
│   ├── ITenantDbContextFactory.cs   ← NEW
│   ├── ITenantProvisioningService.cs ← NEW
│   └── ICompanySyncService.cs       ← NEW

InvoiceApi.Infrastructure/
├── Data/
│   ├── MasterDbContext.cs            ← NEW
│   ├── TenantDbContext.cs            ← NEW
│   ├── MasterDesignTimeFactory.cs    ← NEW (for EF CLI)
│   ├── TenantDesignTimeFactory.cs    ← NEW (for EF CLI)
│   └── ApplicationDbContext.cs       ← DELETED (replaced by Master + Tenant)
├── Migrations/
│   ├── Master/                       ← NEW
│   ├── Tenant/                       ← NEW
│   └── (old SQLite migrations)       ← ARCHIVED
├── Service/
│   ├── TenantDbContextFactoryImpl.cs ← NEW
│   ├── HttpContextTenantResolver.cs  ← NEW
│   ├── TenantProvisioningService.cs  ← NEW
│   ├── CompanySyncService.cs         ← NEW
│   ├── AuthService.cs               ← MODIFIED (MasterDbContext + tenant check)
│   ├── UserService.cs               ← MODIFIED (MasterDbContext)
│   ├── InvoiceService.cs            ← MODIFIED (just swap context type)
│   ├── ClientService.cs             ← MODIFIED (swap context + sync trigger)
│   └── (all other services)         ← MODIFIED (just swap context type)

InvoiceApi.API/
├── Middleware/
│   ├── ImpersonationMiddleware.cs    ← UNCHANGED
│   └── TenantContextMiddleware.cs    ← NEW
├── Program.cs                        ← MAJOR UPDATE (dual DB, middleware)
└── appsettings.json                  ← UPDATED (PostgreSQL connections)
```

---

## Appendix C: Sequence Diagrams

### Regular User — Invoice Operations (Tenant Only)
```
Browser → API → Auth (JWT) → ImpersonationMiddleware (skip)
  → TenantContextMiddleware (validate CompanyId + tenant active)
  → InvoiceController → InvoiceService(TenantDbContext)
  → TenantDbContext: resolved to invoiceapi_tenant_acme
  → Query: SELECT * FROM Invoice
            JOIN Currency ON ...      ← ALL LOCAL
            JOIN Client ON ...        ← ALL LOCAL
  → Response
```

### SysAdmin — Impersonation
```
Browser → API (+ X-Company-Id: 5) → Auth (JWT, SysAdmin)
  → ImpersonationMiddleware (add CompanyId=5 claim)
  → TenantContextMiddleware (validate company 5 provisioned + active)
  → InvoiceController → InvoiceService(TenantDbContext)
  → TenantDbContext: resolved to invoiceapi_tenant_5
  → Response (tenant 5's isolated data)
```

### SysAdmin — Master Operations (No Impersonation)
```
Browser → API (no X-Company-Id) → Auth (JWT, SysAdmin)
  → ImpersonationMiddleware (skip)
  → TenantContextMiddleware (skip — /api/user is master-only)
  → UserController → UserService(MasterDbContext)
  → invoiceapi_master
  → Response
```

### Login Flow
```
Browser → POST /api/auth/login
  → TenantContextMiddleware (skip — /api/auth is master-only)
  → AuthController → AuthService(MasterDbContext)
  → Query: User + CompanySystemSettings from invoiceapi_master
  → Verify: password, tenant active, tenant provisioned
  → Return: JWT token with CompanyId claim
```

### Company Data Sync (Tenant → Master)
```
Tenant user edits company name in tenant DB
  → ClientService.UpdateAsync() saves to TenantDbContext
  → Detects: client.IsIssuer == true
  → Calls: CompanySyncService.SyncTenantToMasterAsync(companyId)
  → Reads updated Client from TenantDbContext
  → Writes synced fields to MasterDbContext Client
  → Done — both sides in sync
```
