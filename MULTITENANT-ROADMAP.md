# Multi-Tenant Implementation Roadmap

> **Created:** 2026-02-10
> **Based on:** MULTITENANT.md (architecture), ANALYZA-A-NAVRH-VYLEPSENI-2026-01-08.md (critical issues), TODO.md (current state)
> **Current state:** 135 tests (134 unit + 1 integration), 0 errors, all features complete, TODO.md empty
> **Target:** Database-per-tenant PostgreSQL architecture with self-contained tenant DBs

---

## Executive Summary

The InvoiceApi project needs to transition from a single SQLite database with manual tenant filtering to a fully isolated database-per-tenant PostgreSQL architecture. This roadmap organizes the work into **7 phases** with clear dependencies, effort estimates, and acceptance criteria.

**Total estimated effort:** ~25-35 development days across 7 phases

```
Phase 0 ──► Phase 1 ──► Phase 2 ──► Phase 3 ──► Phase 4 ──► Phase 5 ──► Phase 6
Security     PostgreSQL   Foundation   Services    Lifecycle   Data        Blazor UI
Pre-reqs     Migration    Infra        Migration   & Middleware Migration  Updates
(3-4 days)   (3-4 days)   (4-5 days)   (3-4 days)  (4-5 days)  (3-4 days)  (4-5 days)
```

---

## Phase 0: Security Pre-requisites (Before Multi-Tenancy)

> **Goal:** Fix critical security issues identified in ANALYZA that become MORE dangerous in a multi-tenant system.
> **Effort:** 3-4 days
> **Priority:** CRITICAL — must be done BEFORE any multi-tenant work

### 0.1 JWT Secret Out of Config
| Item | Details |
|------|---------|
| **Problem** | JWT signing key hardcoded in `appsettings.json`, committed to Git |
| **Solution** | Move to User Secrets (dev) + Environment Variables (prod) |
| **Files** | `API/appsettings.json`, `API/Program.cs` |
| **Effort** | 0.5 day |
| **Tests** | Startup validation test (throws if secret missing) |

### 0.2 CORS Restriction
| Item | Details |
|------|---------|
| **Problem** | `AllowAnyOrigin()` — any domain can call API |
| **Solution** | Environment-specific CORS policies (dev: localhost, prod: specific domains) |
| **Files** | `API/Program.cs` |
| **Effort** | 0.5 day |
| **Tests** | Manual verification |

### 0.3 NumberSequence Concurrency Control
| Item | Details |
|------|---------|
| **Problem** | Race condition on `CurrentNumber` — duplicate document numbers possible |
| **Solution** | Add `[Timestamp] RowVersion` + retry logic with `DbUpdateConcurrencyException` |
| **Files** | `Domain/Entities/NumberSequence.cs`, `Infrastructure/Service/NumberSequenceService.cs`, EF migration |
| **Effort** | 1 day |
| **Tests** | 3-4 concurrency unit tests |

### 0.4 AsNoTracking for Read-Only Queries
| Item | Details |
|------|---------|
| **Problem** | All queries use change tracking even for read-only operations |
| **Solution** | Add `.AsNoTracking()` to all GET/List service methods |
| **Files** | All service files in `Infrastructure/Service/` |
| **Effort** | 0.5 day |
| **Tests** | Existing tests should still pass |

### 0.5 Audit Trail (CreatedByUserId)
| Item | Details |
|------|---------|
| **Problem** | `CreatedByUserId`/`UpdatedByUserId` never populated |
| **Solution** | Inject `ICurrentUserService` into `ApplicationDbContext.SaveChangesAsync()` |
| **Files** | `Infrastructure/Data/ApplicationDbContext.cs`, new `ICurrentUserService` |
| **Effort** | 0.5 day |
| **Tests** | 2 tests (auto-fill on create/update) |

### Phase 0 Acceptance Criteria
- [ ] JWT secret loaded from User Secrets, not appsettings.json
- [ ] CORS restricted to configured origins
- [ ] NumberSequence has RowVersion, concurrent tests pass
- [ ] All read-only queries use AsNoTracking
- [ ] CreatedByUserId/UpdatedByUserId auto-populated
- [ ] All 135+ existing tests still pass
- [ ] New tests: ~10 added

---

## Phase 1: SQLite to PostgreSQL Migration

> **Goal:** Switch database provider. No multi-tenant logic yet — still single DB.
> **Effort:** 3-4 days
> **Depends on:** Phase 0 complete
> **Risk:** Data loss — requires careful backup and verification

### 1.1 NuGet Package Swap
| Item | Details |
|------|---------|
| **Action** | Add `Npgsql.EntityFrameworkCore.PostgreSQL`, remove `Microsoft.EntityFrameworkCore.Sqlite` |
| **Files** | `Infrastructure/InvoiceApi.Infrastructure.csproj` |
| **Effort** | 0.25 day |

### 1.2 DbContext Provider Change
| Item | Details |
|------|---------|
| **Action** | `UseSqlite(...)` → `UseNpgsql(...)` in Program.cs |
| **Files** | `API/Program.cs`, `appsettings.json` |
| **Effort** | 0.25 day |

### 1.3 DesignTimeFactory Update
| Item | Details |
|------|---------|
| **Action** | Update or create DesignTimeFactory for PostgreSQL |
| **Files** | `Infrastructure/Data/DesignTimeFactory.cs` (new or modified) |
| **Effort** | 0.25 day |

### 1.4 Fresh Initial Migration
| Item | Details |
|------|---------|
| **Action** | Delete old SQLite migrations, create fresh PostgreSQL initial migration |
| **Files** | `Infrastructure/Migrations/` (archive old, create new) |
| **Effort** | 0.5 day |
| **Note** | PostgreSQL-specific: `serial` IDs, `timestamp with time zone`, `text` vs `nvarchar` |

### 1.5 Data Migration Tool
| Item | Details |
|------|---------|
| **Action** | Create console tool/script to export SQLite data and import to PostgreSQL |
| **Files** | New console project or admin endpoint |
| **Effort** | 1-2 days |
| **Approach** | Read from SQLite → map → insert to PostgreSQL with identity insert |

### 1.6 PostgreSQL-Specific Adjustments
| Item | Details |
|------|---------|
| **Action** | Fix any SQLite-specific code (e.g., date functions, string comparisons) |
| **Files** | Services using raw SQL or SQLite-specific EF behaviors |
| **Effort** | 0.5 day |
| **Known issues** | SQLite case-insensitive by default, PostgreSQL case-sensitive; check `LIKE` queries |

### 1.7 Test Verification
| Item | Details |
|------|---------|
| **Action** | Run all tests against PostgreSQL (InMemory tests unaffected, integration test needs update) |
| **Files** | `Tests.Integration/` |
| **Effort** | 0.5 day |

### Phase 1 Acceptance Criteria
- [ ] Application runs on PostgreSQL (no SQLite dependency)
- [ ] All existing data migrated (row count verification per table)
- [ ] All 135+ tests pass (InMemory tests unchanged, integration test updated)
- [ ] `appsettings.json` has PostgreSQL connection string
- [ ] SQLite migrations archived (not deleted, for reference)
- [ ] Rollback plan documented (keep SQLite file as backup)

---

## Phase 2: Multi-Tenant Foundation Infrastructure

> **Goal:** Add tenant plumbing (new entity, dual DbContext, resolver, factory) WITHOUT breaking existing functionality.
> **Effort:** 4-5 days
> **Depends on:** Phase 1 complete

### 2.1 CompanySystemSettings Entity
| Item | Details |
|------|---------|
| **Action** | Create new entity for tenant infrastructure config |
| **Files** | `Domain/Entities/CompanySystemSettings.cs` |
| **Fields** | CompanyId (FK→Client), DatabaseName, ConnectionString?, ProvisionedAt?, IsProvisioned, IsActive, MaxUsers?, AdminNotes? |
| **Effort** | 0.25 day |

### 2.2 ITenantResolver Interface + Implementation
| Item | Details |
|------|---------|
| **Action** | Create interface + `HttpContextTenantResolver` that reads CompanyId from JWT claims |
| **Files** | `Application/Interfaces/ITenantResolver.cs`, `Infrastructure/Service/HttpContextTenantResolver.cs` |
| **Methods** | `GetCurrentCompanyId(): long?`, `IsSysAdmin(): bool` |
| **Effort** | 0.5 day |
| **Tests** | 3 unit tests (returns claim, returns null for SysAdmin, IsSysAdmin check) |

### 2.3 MasterDbContext
| Item | Details |
|------|---------|
| **Action** | Create new DbContext for master database |
| **DbSets** | User, Client (IsIssuer only), CompanySystemSettings, VatRate, Currency, NumberSequenceFormat, ContentTemplate |
| **Files** | `Infrastructure/Data/MasterDbContext.cs`, `Infrastructure/Data/MasterDesignTimeFactory.cs` |
| **Effort** | 1 day |
| **Note** | Master DB has Client only for `IsIssuer=true` records (company registry) |

### 2.4 TenantDbContext
| Item | Details |
|------|---------|
| **Action** | Create new DbContext for tenant databases |
| **DbSets** | Client, Address, Contact, BillingSettings, Invoice, InvoiceTemplate, InvoiceItem, NumberSequence, NumberSequenceFormat, ContentTemplate, VatRate, Currency, AresCache |
| **Files** | `Infrastructure/Data/TenantDbContext.cs`, `Infrastructure/Data/TenantDesignTimeFactory.cs` |
| **Effort** | 1 day |
| **Key** | Same schema as current ApplicationDbContext minus User — self-contained |

### 2.5 ITenantDbContextFactory
| Item | Details |
|------|---------|
| **Action** | Create factory that resolves tenant connection string from CompanySystemSettings |
| **Files** | `Application/Interfaces/ITenantDbContextFactory.cs`, `Infrastructure/Service/TenantDbContextFactory.cs` |
| **Methods** | `CreateContext(): TenantDbContext` (from current request), `CreateContextAsync(companyId): Task<TenantDbContext>` (by ID) |
| **Effort** | 1 day |
| **Tests** | 3-4 tests (resolves correct DB, throws for unprovisioned, throws for inactive) |

### 2.6 Dual Migration Setup
| Item | Details |
|------|---------|
| **Action** | Set up separate migration folders for Master and Tenant contexts |
| **Files** | `Infrastructure/Migrations/Master/`, `Infrastructure/Migrations/Tenant/` |
| **EF Commands** | `dotnet ef migrations add Init --context MasterDbContext --output-dir Migrations/Master` |
| **Effort** | 0.5 day |

### 2.7 Foundation Unit Tests
| Item | Details |
|------|---------|
| **Action** | Test tenant resolver, factory, CompanySystemSettings validation |
| **Files** | `Tests.Unit/` |
| **Count** | ~10-12 new tests |
| **Effort** | 0.5 day |

### Phase 2 Acceptance Criteria
- [ ] `CompanySystemSettings` entity created with all fields
- [ ] `MasterDbContext` and `TenantDbContext` created with correct DbSets
- [ ] `ITenantResolver` resolves CompanyId from JWT claims
- [ ] `ITenantDbContextFactory` builds correct connection strings
- [ ] Both migration folders generate valid migrations
- [ ] `ApplicationDbContext` still works (not removed yet — coexists during transition)
- [ ] ~10-12 new unit tests pass
- [ ] Build: 0 errors

---

## Phase 3: Service Layer Migration

> **Goal:** Swap `ApplicationDbContext` to `MasterDbContext` or `TenantDbContext` per service.
> **Effort:** 3-4 days
> **Depends on:** Phase 2 complete
> **Key insight:** Most services just swap context type — no logic changes (all data is local in tenant DB)

### 3.1 Master-Only Services

| Service | Change | Effort |
|---------|--------|--------|
| `AuthService` | `ApplicationDbContext` → `MasterDbContext` + add tenant status check (IsProvisioned, IsActive) | 0.5 day |
| `UserService` | `ApplicationDbContext` → `MasterDbContext` | 0.25 day |

### 3.2 Tenant-Only Services (Mechanical Swap)

| Service | Change | Effort |
|---------|--------|--------|
| `InvoiceService` | `ApplicationDbContext` → `TenantDbContext` | 0.25 day |
| `VatRateService` | `ApplicationDbContext` → `TenantDbContext` | 0.1 day |
| `CurrencyService` | `ApplicationDbContext` → `TenantDbContext` | 0.1 day |
| `NumberSequenceService` | `ApplicationDbContext` → `TenantDbContext` | 0.1 day |
| `ContentTemplateService` | `ApplicationDbContext` → `TenantDbContext` | 0.1 day |
| `InvoiceTemplateService` | `ApplicationDbContext` → `TenantDbContext` | 0.1 day |
| `PdfExportService` | `ApplicationDbContext` → `TenantDbContext` | 0.1 day |
| `EmailService` | `ApplicationDbContext` → `TenantDbContext` | 0.1 day |
| `DashboardService` | `ApplicationDbContext` → `TenantDbContext` | 0.1 day |

### 3.3 Mixed Services

| Service | Change | Effort |
|---------|--------|--------|
| `ClientService` | `ApplicationDbContext` → `TenantDbContext` + add `ICompanySyncService` trigger when updating issuer | 0.5 day |

### 3.4 ICompanySyncService
| Item | Details |
|------|---------|
| **Action** | Create company data sync service (master ↔ tenant) |
| **Files** | `Application/Interfaces/ICompanySyncService.cs`, `Infrastructure/Service/CompanySyncService.cs` |
| **Methods** | `SyncMasterToTenantAsync(companyId)`, `SyncTenantToMasterAsync(companyId)` |
| **Synced fields** | CompanyName, TradingName, RegistrationNumber, TaxNumber, IsVatPayer |
| **NOT synced** | Address, Contact, BillingSettings, PreferredCurrencyId |
| **Effort** | 1 day |
| **Tests** | 5-6 tests (sync both directions, field verification, addresses untouched) |

### 3.5 DI Registration Update
| Item | Details |
|------|---------|
| **Action** | Update `Program.cs` with dual DbContext registration and scoped TenantDbContext |
| **Files** | `API/Program.cs` |
| **Key pattern** | `AddDbContext<MasterDbContext>` (fixed conn), `AddScoped<TenantDbContext>` (resolved per-request) |
| **Effort** | 0.5 day |

### 3.6 Update Unit Tests
| Item | Details |
|------|---------|
| **Action** | Update test DI from `ApplicationDbContext` → `TenantDbContext`/`MasterDbContext` |
| **Files** | All test files in `Tests.Unit/` |
| **Key** | `CreateInMemoryApplicationDbContext()` → `CreateInMemoryTenantDbContext()` — same schema, different class |
| **Effort** | 0.5 day |
| **Risk** | Low — assertions remain unchanged |

### 3.7 Remove ApplicationDbContext
| Item | Details |
|------|---------|
| **Action** | Delete old unified DbContext (replaced by Master + Tenant) |
| **Files** | `Infrastructure/Data/ApplicationDbContext.cs` (DELETE) |
| **Effort** | 0.25 day |
| **Prerequisite** | All services and tests migrated to new contexts |

### Phase 3 Acceptance Criteria
- [ ] `AuthService` + `UserService` use `MasterDbContext`
- [ ] All 11 business services use `TenantDbContext`
- [ ] `ClientService` triggers company sync on issuer update
- [ ] `ICompanySyncService` syncs 5 fields bidirectionally
- [ ] `ApplicationDbContext` deleted — no references remain
- [ ] All 135+ existing tests updated and passing
- [ ] ~10-15 new sync/auth tests added
- [ ] Build: 0 errors

---

## Phase 4: Tenant Lifecycle & Middleware

> **Goal:** Build tenant provisioning, access control middleware, and lifecycle management.
> **Effort:** 4-5 days
> **Depends on:** Phase 3 complete

### 4.1 TenantProvisioningService
| Item | Details |
|------|---------|
| **Action** | Create service that provisions new tenant databases |
| **Files** | `Application/Interfaces/ITenantProvisioningService.cs`, `Infrastructure/Service/TenantProvisioningService.cs` |
| **Flow** | 1. CREATE DATABASE → 2. Apply migrations → 3. Copy code tables → 4. Create issuer → 5. Create default sequences → 6. Mark provisioned |
| **Code tables copied** | VatRate, Currency, NumberSequenceFormat, ContentTemplate (defaults only) |
| **Effort** | 2 days |
| **Tests** | 5-6 integration tests |

### 4.2 TenantContextMiddleware
| Item | Details |
|------|---------|
| **Action** | Create middleware that validates tenant context for non-master endpoints |
| **Files** | `API/Middleware/TenantContextMiddleware.cs` |
| **Logic** | Skip for `/api/auth`, `/api/user`, `/api/company`; verify CompanyId claim exists; verify tenant provisioned + active |
| **Effort** | 0.5 day |
| **Tests** | 4-5 tests (skip master paths, block no-company, block inactive, pass valid) |

### 4.3 CompanyController Updates
| Item | Details |
|------|---------|
| **Action** | Add CompanySystemSettings CRUD + provisioning trigger endpoint |
| **Files** | `API/Controller/CompanyController.cs` |
| **Endpoints** | `GET/POST/PUT /api/company/settings`, `POST /api/company/{id}/provision`, `PUT /api/company/{id}/activate`, `PUT /api/company/{id}/deactivate` |
| **Effort** | 1 day |
| **Auth** | SysAdmin only |

### 4.4 Middleware Pipeline Update
| Item | Details |
|------|---------|
| **Action** | Wire new middleware into pipeline |
| **Files** | `API/Program.cs` |
| **Order** | `UseAuthentication()` → `UseAuthorization()` → `UseImpersonation()` → `UseTenantContext()` → `MapControllers()` |
| **Effort** | 0.25 day |

### 4.5 Startup Migration Logic
| Item | Details |
|------|---------|
| **Action** | Auto-migrate master DB + all active tenant DBs on startup |
| **Files** | `API/Program.cs` |
| **Logic** | Migrate master → query provisioned tenants → migrate each (with per-tenant error handling) |
| **Effort** | 0.5 day |

### 4.6 Integration Tests
| Item | Details |
|------|---------|
| **Action** | Full provisioning + isolation tests |
| **Files** | `Tests.Integration/` |
| **Tests** | Provision tenant, verify code tables copied, verify isolation between tenants, verify SysAdmin impersonation switches DBs |
| **Count** | ~10-15 integration tests |
| **Effort** | 1 day |

### Phase 4 Acceptance Criteria
- [ ] `TenantProvisioningService` creates DB, copies code tables, creates issuer
- [ ] `TenantContextMiddleware` blocks unauthorized tenant access
- [ ] SysAdmin can provision, activate, deactivate tenants via API
- [ ] Startup auto-migrates all tenant DBs (with error handling)
- [ ] Login fails for inactive/unprovisioned tenants
- [ ] ~15-20 new tests (unit + integration)
- [ ] Build: 0 errors

---

## Phase 5: Data Migration (Production)

> **Goal:** Migrate existing single-DB data to master + per-tenant databases.
> **Effort:** 3-4 days
> **Depends on:** Phase 4 complete
> **Risk:** HIGH — production data involved. Requires staging environment testing.

### 5.1 Migration Tool
| Item | Details |
|------|---------|
| **Action** | Create console tool that reads current single PostgreSQL DB and splits into master + tenant DBs |
| **Files** | New console project `InvoiceApi.MigrationTool/` |
| **Logic** | Extract Users → master; Extract Client (IsIssuer) → master + tenant; Extract code tables → master (defaults) + tenant (copies); Extract business data → tenant only |
| **Effort** | 1.5 days |

### 5.2 Data Integrity Verification
| Item | Details |
|------|---------|
| **Action** | Automated verification of migrated data |
| **Checks** | Row counts per table per tenant, FK consistency, issuer sync, code table completeness |
| **Effort** | 0.5 day |

### 5.3 Staging Test
| Item | Details |
|------|---------|
| **Action** | Run migration on copy of production data |
| **Effort** | 0.5 day |
| **Process** | Backup → migrate → verify → test application against migrated data |

### 5.4 Rollback Plan
| Item | Details |
|------|---------|
| **Action** | Document rollback procedure |
| **Content** | Restore from backup, revert application code, connection string rollback |
| **Effort** | 0.25 day |

### 5.5 Production Execution
| Item | Details |
|------|---------|
| **Action** | Execute migration on production |
| **Process** | Maintenance window → backup → migrate → verify → deploy new code → smoke test |
| **Effort** | 0.5 day |

### Phase 5 Acceptance Criteria
- [ ] Migration tool handles all entity types
- [ ] Per-table row count verification passes
- [ ] FK integrity verification passes per tenant DB
- [ ] Application works against migrated multi-tenant data
- [ ] Rollback plan documented and tested
- [ ] Zero data loss verified

---

## Phase 6: Blazor UI Updates

> **Goal:** Update Blazor UI for multi-tenant operation (company switcher, provisioning UI).
> **Effort:** 4-5 days
> **Depends on:** Phase 4 complete (can run in parallel with Phase 5)

### 6.1 ApiClientBase Tenant Header
| Item | Details |
|------|---------|
| **Action** | Automatically pass `X-Company-Id` header when SysAdmin is impersonating |
| **Files** | `BlazorUI/Services/ApiClientBase.cs` |
| **Logic** | Read `ImpersonatedCompanyId` from session storage → add to every HTTP request |
| **Effort** | 0.5 day |

### 6.2 SysAdmin Company Switcher
| Item | Details |
|------|---------|
| **Action** | Add company selector dropdown in MainLayout for SysAdmin users |
| **Files** | `BlazorUI/Shared/CompanySwitcher.razor` (new), `BlazorUI/Shared/MainLayout.razor` |
| **Behavior** | Shows all provisioned companies → sets `ImpersonatedCompanyId` in session → triggers page reload |
| **Effort** | 1 day |

### 6.3 CompanySystemSettings Management Page
| Item | Details |
|------|---------|
| **Action** | Create SysAdmin page for managing tenant settings |
| **Files** | `BlazorUI/Pages/CompanySettings.razor` (new), `BlazorUI/Services/CompanySettingsApiService.cs` (new) |
| **Features** | List all companies with status, edit settings, view provisioning status |
| **Effort** | 1 day |

### 6.4 Tenant Provisioning UI
| Item | Details |
|------|---------|
| **Action** | Add "Provision" button with confirmation dialog for SysAdmin |
| **Files** | `BlazorUI/Pages/CompanySettings.razor` or `CompanyDetail.razor` |
| **Flow** | Click Provision → confirmation → loading spinner → success/error feedback |
| **Effort** | 0.5 day |

### 6.5 Tenant Status Indicators
| Item | Details |
|------|---------|
| **Action** | Show provisioning status, active/inactive, DB name on company pages |
| **Files** | `BlazorUI/Pages/Companies.razor`, `CompanyDetail.razor` |
| **UI** | Chips/badges: "Provisioned" (green), "Not Provisioned" (red), "Inactive" (grey) |
| **Effort** | 0.5 day |

### 6.6 Localization
| Item | Details |
|------|---------|
| **Action** | Add CZ/EN resource keys for all new UI elements |
| **Files** | `SharedResource.resx`, `SharedResource.en.resx` |
| **Count** | ~30-40 new keys |
| **Effort** | 0.5 day |

### 6.7 End-to-End Testing
| Item | Details |
|------|---------|
| **Action** | Manual E2E testing of full multi-tenant flow |
| **Scenarios** | SysAdmin creates company → provisions → creates user → user logs in → creates invoice → SysAdmin switches companies → verifies isolation |
| **Effort** | 1 day |

### Phase 6 Acceptance Criteria
- [ ] SysAdmin can switch between companies in UI
- [ ] API requests include correct `X-Company-Id` header
- [ ] CompanySystemSettings page shows all tenants with status
- [ ] Provision button creates new tenant DB
- [ ] All existing UI features work in multi-tenant context
- [ ] ~30-40 new localization keys (CZ + EN)

---

## Dependencies & Parallel Work

```
Phase 0 (Security)
    │
    ▼
Phase 1 (PostgreSQL)
    │
    ▼
Phase 2 (Foundation)
    │
    ▼
Phase 3 (Services)
    │
    ▼
Phase 4 (Lifecycle)
    │
    ├─────────────────────┐
    ▼                     ▼
Phase 5 (Data Migration)  Phase 6 (Blazor UI)
    │                     │
    └─────────────────────┘
              │
              ▼
         Production Deploy
```

**Phases 5 and 6 can run in parallel** — UI development doesn't require production data migration.

---

## Files Changed Summary

### NEW Files (~15)
| File | Phase |
|------|-------|
| `Domain/Entities/CompanySystemSettings.cs` | 2 |
| `Application/Interfaces/ITenantResolver.cs` | 2 |
| `Application/Interfaces/ITenantDbContextFactory.cs` | 2 |
| `Application/Interfaces/ITenantProvisioningService.cs` | 4 |
| `Application/Interfaces/ICompanySyncService.cs` | 3 |
| `Application/Interfaces/ICurrentUserService.cs` | 0 |
| `Infrastructure/Data/MasterDbContext.cs` | 2 |
| `Infrastructure/Data/TenantDbContext.cs` | 2 |
| `Infrastructure/Data/MasterDesignTimeFactory.cs` | 2 |
| `Infrastructure/Data/TenantDesignTimeFactory.cs` | 2 |
| `Infrastructure/Service/HttpContextTenantResolver.cs` | 2 |
| `Infrastructure/Service/TenantDbContextFactory.cs` | 2 |
| `Infrastructure/Service/TenantProvisioningService.cs` | 4 |
| `Infrastructure/Service/CompanySyncService.cs` | 3 |
| `Infrastructure/Service/CurrentUserService.cs` | 0 |
| `API/Middleware/TenantContextMiddleware.cs` | 4 |
| `BlazorUI/Shared/CompanySwitcher.razor` | 6 |
| `BlazorUI/Pages/CompanySettings.razor` | 6 |
| `BlazorUI/Services/CompanySettingsApiService.cs` | 6 |

### MODIFIED Files (~25)
| File | Phase | Change |
|------|-------|--------|
| `Domain/Entities/NumberSequence.cs` | 0 | Add RowVersion |
| `Infrastructure/InvoiceApi.Infrastructure.csproj` | 1 | Npgsql ↔ SQLite swap |
| `Infrastructure/Data/ApplicationDbContext.cs` | 0-3 | Phase 0: audit trail → Phase 3: DELETE |
| `Infrastructure/Service/AuthService.cs` | 3 | MasterDbContext + tenant check |
| `Infrastructure/Service/UserService.cs` | 3 | MasterDbContext |
| `Infrastructure/Service/InvoiceService.cs` | 3 | TenantDbContext |
| `Infrastructure/Service/ClientService.cs` | 3 | TenantDbContext + sync |
| `Infrastructure/Service/VatRateService.cs` | 3 | TenantDbContext |
| `Infrastructure/Service/CurrencyService.cs` | 3 | TenantDbContext |
| `Infrastructure/Service/NumberSequenceService.cs` | 0,3 | Concurrency + TenantDbContext |
| `Infrastructure/Service/ContentTemplateService.cs` | 3 | TenantDbContext |
| `Infrastructure/Service/InvoiceTemplateService.cs` | 3 | TenantDbContext |
| `Infrastructure/Service/PdfExportService.cs` | 3 | TenantDbContext |
| `Infrastructure/Service/EmailService.cs` | 3 | TenantDbContext |
| `Infrastructure/Service/DashboardService.cs` | 3 | TenantDbContext |
| `API/Program.cs` | 0-4 | Major updates each phase |
| `API/appsettings.json` | 0-1 | JWT secret removal, PostgreSQL conn |
| `API/Controller/CompanyController.cs` | 4 | Provisioning endpoints |
| `BlazorUI/Services/ApiClientBase.cs` | 6 | X-Company-Id header |
| `BlazorUI/Shared/MainLayout.razor` | 6 | Company switcher |
| `BlazorUI/Pages/Companies.razor` | 6 | Status indicators |

### DELETED Files (~2)
| File | Phase |
|------|-------|
| `Infrastructure/Data/ApplicationDbContext.cs` | 3 |
| `Infrastructure/Migrations/*.cs` (old SQLite) | 1 (archived) |

---

## Test Coverage Plan

| Phase | New Tests | Running Total | Type |
|-------|-----------|---------------|------|
| Current | — | 135 | 134 unit + 1 integration |
| Phase 0 | ~10 | ~145 | Unit (concurrency, audit) |
| Phase 1 | ~2 | ~147 | Integration (PostgreSQL) |
| Phase 2 | ~12 | ~159 | Unit (resolver, factory) |
| Phase 3 | ~15 | ~174 | Unit (sync, auth, context swap) |
| Phase 4 | ~20 | ~194 | Unit + Integration (provisioning, isolation) |
| Phase 5 | ~5 | ~199 | Integration (migration verification) |
| Phase 6 | ~5 | ~204 | Unit (header injection, switcher) |
| **Total** | **~69** | **~204** | |

---

## Risk Register

| # | Risk | Severity | Likelihood | Phase | Mitigation |
|---|------|----------|------------|-------|------------|
| R1 | Data loss during SQLite→PostgreSQL | Critical | Low | 1 | Full backup, run on copy, row count verification, keep SQLite |
| R2 | Data loss during multi-tenant split | Critical | Low | 5 | Run on copy, per-tenant FK verification, rollback plan |
| R3 | Company data out of sync (master↔tenant) | High | Medium | 3 | ICompanySyncService, trigger on every issuer update |
| R4 | PostgreSQL resource usage with many DBs | Medium | Low | 4 | PostgreSQL handles 100s of DBs, monitor connections |
| R5 | Tenant migration failure on startup | Medium | Medium | 4 | Per-tenant error handling, don't crash — log and continue |
| R6 | SysAdmin forgets to impersonate | Low | Medium | 4,6 | TenantContextMiddleware returns 403, UI shows selector |
| R7 | Legal VatRate change needs propagation | Medium | Low | Post-launch | V1: tenants manage own. Future: "Push defaults" admin tool |
| R8 | InMemory test compatibility | Low | Low | 3 | InMemory works with both context types — same schema |
| R9 | Connection pooling across many tenant DBs | Medium | Medium | 4 | Configure Npgsql connection pool per-DB, monitor exhaustion |
| R10 | Concurrent provisioning of same company | Low | Low | 4 | Check `IsProvisioned` + database-level lock |

---

## Post-Launch Enhancements (Not in Roadmap)

These are future improvements documented in MULTITENANT.md but NOT required for V1:

1. **"Push defaults to all tenants"** — SysAdmin tool to propagate VatRate/Currency changes to existing tenants
2. **Optimistic concurrency on company sync** — RowVersion-based conflict resolution (V1 uses last-write-wins)
3. **Tenant DB backup/restore** — Per-tenant backup management UI
4. **Connection pooling optimization** — NpgsqlDataSource pooling per tenant
5. **Health check endpoint** — Per-tenant DB connectivity check
6. **Rate limiting** — Per-tenant API rate limits
7. **FluentValidation** — Request validation at API boundary
8. **Tenant-scoped logging** — Structured logging with TenantId context

---

## Quick Reference: Key Architecture Decisions

| Decision | Choice | Reference |
|----------|--------|-----------|
| Isolation model | Database-per-tenant | MULTITENANT.md §2 |
| DB provider | PostgreSQL | MULTITENANT.md §2 |
| Code tables | Copied to each tenant (self-contained) | MULTITENANT.md §2-3 |
| Users location | Master DB only | MULTITENANT.md §3 |
| Company sync | Service-based, last-write-wins | MULTITENANT.md §4 |
| Entity changes | Zero — all FKs stay local | MULTITENANT.md §5 |
| Service migration | Mechanical swap (AppDbContext → Tenant/MasterDbContext) | MULTITENANT.md §7 |
| Auth flow | JWT unchanged, add tenant status check | MULTITENANT.md §8 |
| Impersonation | Unchanged — middleware resolves to different DB | MULTITENANT.md §8 |
