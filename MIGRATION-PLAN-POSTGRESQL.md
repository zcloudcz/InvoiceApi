# Migration Plan: Azure SQL Multi-DB → Azure PostgreSQL Single-DB Multi-Schema

## Overview

**Current architecture**: Azure SQL Server with separate databases per tenant (`invoiceapi_master`, `invoiceapi_tenant_42`, etc.)
**Target architecture**: Single Azure PostgreSQL database with schema-per-tenant isolation (`public` schema for master data, `tenant_{id}` schemas for tenant data)

## Why This Change?

- **Cost**: Single Azure PostgreSQL Flexible Server is significantly cheaper than multiple Azure SQL databases
- **Simplicity**: One connection string, one database, one backup — simpler ops
- **Performance**: Schema switching is faster than database connection switching
- **PostgreSQL ecosystem**: Better JSON, full-text search, extensions (PostGIS, etc.)

## Architecture Comparison

```
CURRENT (Azure SQL Multi-DB):
  Azure SQL Server
  ├── invoiceapi_master          ← MasterDbContext (Users, Companies, Settings)
  ├── invoiceapi_tenant_42       ← TenantDbContext (Invoices, Clients, etc.)
  ├── invoiceapi_tenant_99       ← TenantDbContext (Invoices, Clients, etc.)
  └── invoiceapi_tenant_template ← EF CLI design-time migrations

TARGET (Azure PostgreSQL Multi-Schema):
  Azure PostgreSQL Database: invoiceapi
  ├── public (schema)            ← MasterDbContext (Users, Companies, Settings)
  ├── tenant_42 (schema)         ← TenantDbContext (Invoices, Clients, etc.)
  ├── tenant_99 (schema)         ← TenantDbContext (Invoices, Clients, etc.)
  └── tenant_template (schema)   ← EF CLI design-time migrations
```

## Impact Analysis

### Files That Must Change (by category)

#### 1. NuGet Packages (3 .csproj files)
| File | Change |
|------|--------|
| `InvoiceApi.Infrastructure.csproj` | `SqlServer 10.0.1` → `Npgsql.EntityFrameworkCore.PostgreSQL` + remove `Azure.ResourceManager.Sql` |
| `InvoiceApi.API.csproj` | `SqlServer 10.0.1` → `Npgsql.EntityFrameworkCore.PostgreSQL` |
| `InvoiceApi.MigrationTool.csproj` | `SqlServer 10.0.1` → `Npgsql.EntityFrameworkCore.PostgreSQL` |

#### 2. DbContexts — EF Core Configuration (2 files)
| File | Change |
|------|--------|
| `MasterDbContext.cs` | `HasFilter("[Col]")` → `HasFilter("\"Col\" IS NOT NULL")` (4 filters) |
| `TenantDbContext.cs` | `.IsRowVersion()` → `.UseXminAsConcurrencyToken()`, `HasFilter` syntax |

#### 3. Connection String & Schema Resolution (4 files)
| File | Change |
|------|--------|
| `ServiceCollectionExtensions.cs` (Infra DI) | `UseSqlServer` → `UseNpgsql` + schema-based tenant resolution |
| `TenantDbContextFactory.cs` | `SqlConnectionStringBuilder` → `NpgsqlConnectionStringBuilder` + `HasDefaultSchema()` |
| `TenantProvisioningService.cs` | `CREATE DATABASE` → `CREATE SCHEMA` + `NpgsqlConnection` |
| `appsettings.json` (API) | SQL Server connection strings → PostgreSQL format |

#### 4. Design-Time Factories (2 files)
| File | Change |
|------|--------|
| `MasterDesignTimeFactory.cs` | `UseSqlServer` → `UseNpgsql` |
| `TenantDesignTimeFactory.cs` | `UseSqlServer` → `UseNpgsql` + `HasDefaultSchema("tenant_template")` |

#### 5. Azure SQL Service → Remove Entirely (3 files)
| File | Action |
|------|--------|
| `IAzureSqlService.cs` | **DELETE** (no more ARM API for DB management) |
| `AzureSqlService.cs` | **DELETE** |
| `AzureOperationController.cs` | **REWRITE** → `TenantOperationController` (schema-based provisioning) |

#### 6. Raw ADO.NET Connections (4 files)
| File | Change |
|------|--------|
| `TenantProvisioningService.cs` | `SqlConnection` → `NpgsqlConnection`, SQL syntax changes |
| `LogFlushService.cs` | `SqlConnection` → `NpgsqlConnection`, parameter syntax `@p` → `@p` (same) |
| `LogCleanupService.cs` | `SqlConnection` → `NpgsqlConnection` |
| `TimerFunctions.cs` (Functions) | `SqlConnection` → `NpgsqlConnection` |

#### 7. Domain Entity (1 file)
| File | Change |
|------|--------|
| `NumberSequence.cs` | `byte[]? RowVersion` → `uint xmin` concurrency token |

#### 8. CompanySystemSettings Entity (1 file)
| File | Change |
|------|--------|
| `CompanySystemSettings.cs` | `DatabaseName` → `SchemaName`, remove `ConnectionString` (single DB) |

#### 9. Migration Tool (2 files)
| File | Change |
|------|--------|
| `DataMigrationService.cs` | All SQL Server refs → PostgreSQL equivalents |
| `DataIntegrityVerifier.cs` | `SqlConnectionStringBuilder` → `NpgsqlConnectionStringBuilder` |

#### 10. Functions Project (2 files)
| File | Change |
|------|--------|
| `DiagnosticFunctions.cs` | `SqlConnectionStringBuilder` → `NpgsqlConnectionStringBuilder` |
| `TimerFunctions.cs` | `SqlConnection` → `NpgsqlConnection`, SQL syntax |

#### 11. Tests (2+ files)
| File | Action |
|------|--------|
| `AzureSqlServiceTests.cs` | **DELETE** or rewrite for schema-based provisioning |
| `TenantProvisioningServiceTests.cs` | Update for schema-based provisioning |
| Integration tests | Update connection string handling |

#### 12. EF Migrations (all existing files)
| Folder | Action |
|--------|--------|
| `Migrations/Master/` (10 files) | **DELETE** → regenerate for PostgreSQL |
| `Migrations/Tenant/` (7 files) | **DELETE** → regenerate for PostgreSQL |

#### 13. Configuration & Middleware (2 files)
| File | Change |
|------|--------|
| `appsettings.json` | Connection strings + remove AzureSettings section |
| `TenantContextMiddleware.cs` | May need minor updates for schema validation |

## Implementation Steps

### Step 1: NuGet Package Migration

Replace SQL Server packages with PostgreSQL in all 3 projects:

```xml
<!-- REMOVE -->
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.1" />

<!-- ADD -->
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.1" />
```

In `Infrastructure.csproj`, also:
- **Remove**: `Azure.ResourceManager`, `Azure.ResourceManager.Sql`, `Azure.Identity` (unless used elsewhere)
- **Note**: `Azure.Identity` may still be needed if other Azure services are used — check dependencies

### Step 2: Domain Entity Changes

**`NumberSequence.cs`** — Change concurrency token:
```csharp
// BEFORE (SQL Server rowversion)
public byte[]? RowVersion { get; set; }

// AFTER (PostgreSQL xmin system column)
public uint RowVersion { get; set; }
```

**`CompanySystemSettings.cs`** — Rename DatabaseName → SchemaName:
```csharp
// BEFORE
public string DatabaseName { get; set; } = string.Empty;
public string? ConnectionString { get; set; }

// AFTER
public string SchemaName { get; set; } = string.Empty;
// ConnectionString removed — single database, schema isolation only
```

### Step 3: DbContext Configuration Changes

**`MasterDbContext.cs`** — Fix HasFilter syntax (4 places):
```csharp
// BEFORE (SQL Server square bracket syntax)
.HasFilter("[InvitationToken] IS NOT NULL");
.HasFilter("[ExternalProvider] <> 0")
.HasFilter("[EmailVerificationToken] IS NOT NULL");

// AFTER (PostgreSQL double-quote identifier syntax)
.HasFilter("\"InvitationToken\" IS NOT NULL");
.HasFilter("\"ExternalProvider\" <> 0")
.HasFilter("\"EmailVerificationToken\" IS NOT NULL");
```

**`TenantDbContext.cs`** — Fix concurrency + HasFilter:
```csharp
// BEFORE (SQL Server rowversion)
entity.Property(e => e.RowVersion).IsRowVersion();

// AFTER (PostgreSQL xmin concurrency token)
entity.UseXminAsConcurrencyToken();

// BEFORE (HasFilter)
.HasFilter("[DocumentNumber] IS NOT NULL AND [DocumentNumber] <> 'DRAFT'");

// AFTER
.HasFilter("\"DocumentNumber\" IS NOT NULL AND \"DocumentNumber\" <> 'DRAFT'");
```

### Step 4: Schema-Based Tenant Resolution

**`TenantDbContext.cs`** — Add schema support:
```csharp
// Add a Schema property that can be set before OnModelCreating
public string? Schema { get; set; }

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // If a schema is set, all tables go into that schema
    if (!string.IsNullOrEmpty(Schema))
    {
        modelBuilder.HasDefaultSchema(Schema);
    }

    // ... rest of configuration
}
```

**`TenantDbContextFactory.cs`** — Replace InitialCatalog with schema:
```csharp
// BEFORE: SqlConnectionStringBuilder + InitialCatalog swap
var builder = new SqlConnectionStringBuilder(masterConnectionString)
{
    InitialCatalog = settings.DatabaseName
};
var options = new DbContextOptionsBuilder<TenantDbContext>()
    .UseSqlServer(builder.ConnectionString, ...)
    .Options;

// AFTER: Same connection string, different schema
var options = new DbContextOptionsBuilder<TenantDbContext>()
    .UseNpgsql(masterConnectionString, ...)
    .Options;
var context = new TenantDbContext(options) { Schema = settings.SchemaName };
```

**`ServiceCollectionExtensions.cs`** — Update DI registration:
```csharp
// BEFORE
options.UseSqlServer(configuration.GetConnectionString("MasterConnection"), ...)

// AFTER
options.UseNpgsql(configuration.GetConnectionString("MasterConnection"), ...)
```

For TenantDbContext dynamic registration — same connection string, set schema per-request.

### Step 5: Tenant Provisioning — Schema Instead of Database

**`TenantProvisioningService.cs`** — Major rewrite of database creation:
```csharp
// BEFORE: Create SQL Server database
var builder = new SqlConnectionStringBuilder(masterConnectionString) { InitialCatalog = "master" };
await using var connection = new SqlConnection(builder.ConnectionString);
// SELECT DB_ID('name') → CREATE DATABASE [name]

// AFTER: Create PostgreSQL schema
await using var connection = new NpgsqlConnection(masterConnectionString);
await connection.OpenAsync(cancellationToken);
// Check if schema exists
var checkCmd = connection.CreateCommand();
checkCmd.CommandText = "SELECT schema_name FROM information_schema.schemata WHERE schema_name = @name";
checkCmd.Parameters.AddWithValue("name", schemaName);
var exists = await checkCmd.ExecuteScalarAsync(cancellationToken);
if (exists is null)
{
    var createCmd = connection.CreateCommand();
    createCmd.CommandText = $"CREATE SCHEMA \"{safeName}\"";
    await createCmd.ExecuteNonQueryAsync(cancellationToken);
}
```

Migration application uses `context.Database.MigrateAsync()` with the schema set on the TenantDbContext.

### Step 6: Remove Azure SQL Management

- **DELETE** `IAzureSqlService.cs`
- **DELETE** `AzureSqlService.cs`
- **DELETE** `AzureSqlServiceTests.cs`
- **REWRITE** `AzureOperationController.cs` → `TenantOperationController.cs`:
  - Remove ARM API calls
  - Provision = `CREATE SCHEMA` + migrations + seed data
  - List = query `CompanySystemSettings` for all schemas
  - Delete = `DROP SCHEMA ... CASCADE` (with confirmation)
- Remove `AzureSettings` section from `appsettings.json`
- Remove `Azure.ResourceManager*` NuGet packages from Infrastructure.csproj

### Step 7: Raw ADO.NET Updates

All files using `SqlConnection` → `NpgsqlConnection`:
- `LogFlushService.cs` — bulk INSERT for AppLog
- `LogCleanupService.cs` — DELETE old logs
- `TimerFunctions.cs` — same timer logic
- `DiagnosticFunctions.cs` — connection string diagnostics

SQL syntax differences to watch:
- `TOP(n)` → `LIMIT n`
- `GETDATE()` → `NOW()`
- `SCOPE_IDENTITY()` → `RETURNING id` or `currval()`
- String concatenation: `+` → `||`
- Boolean: `1/0` → `TRUE/FALSE`

### Step 8: Connection String Format

```json
// BEFORE (SQL Server)
"MasterConnection": "Server=localhost;Database=invoiceapi_master;User Id=sa;Password=pass;TrustServerCertificate=true"

// AFTER (PostgreSQL)
"DefaultConnection": "Host=localhost;Database=invoiceapi;Username=invoiceapi;Password=pass"
```

Note: Only ONE connection string now — no separate master/template connections needed at runtime.
Keep `TenantTemplateConnection` for EF CLI design-time only (points to same DB, different schema).

### Step 9: Design-Time Factories

**`MasterDesignTimeFactory.cs`**:
```csharp
optionsBuilder.UseNpgsql(connectionString, b => b.MigrationsAssembly("InvoiceApi.Infrastructure"));
```

**`TenantDesignTimeFactory.cs`**:
```csharp
optionsBuilder.UseNpgsql(connectionString, b => b.MigrationsAssembly("InvoiceApi.Infrastructure"));
// Schema set via HasDefaultSchema in TenantDbContext when Schema property is set
```

### Step 10: Delete Old Migrations & Generate New Ones

```bash
# Delete all existing SQL Server migrations
rm -rf InvoiceApi.Infrastructure/Migrations/Master/
rm -rf InvoiceApi.Infrastructure/Migrations/Tenant/

# Generate fresh PostgreSQL migrations
cd InvoiceApi.Infrastructure
dotnet ef migrations add InitialPostgreSql --context MasterDbContext --output-dir Migrations/Master --startup-project ../InvoiceApi.API
dotnet ef migrations add InitialPostgreSql --context TenantDbContext --output-dir Migrations/Tenant --startup-project ../InvoiceApi.API
```

### Step 11: Update MigrationTool

- Replace all `SqlConnectionStringBuilder` → `NpgsqlConnectionStringBuilder`
- Replace all `SqlConnection` → `NpgsqlConnection`
- Replace `UseSqlServer` → `UseNpgsql`
- Replace `CREATE DATABASE` → `CREATE SCHEMA`
- Replace `DB_ID` check → `information_schema.schemata` query
- Update data migration logic for schema-based approach

### Step 12: Update Tests

- Delete `AzureSqlServiceTests.cs` (25 tests for removed service)
- Update `TenantProvisioningServiceTests.cs` for schema-based provisioning
- Update integration tests' DbContext replacement for PostgreSQL/InMemory
- All InMemoryDatabase tests should remain unchanged (provider-agnostic)
- Add new tests for schema provisioning

### Step 13: Update DTOs

- `AzureCreateDatabaseRequest` → `CreateTenantSchemaRequest` (or similar)
- `AzureDatabaseStatusDto` → `TenantSchemaStatusDto` (or similar)
- Update `Contracts/Dto/AzureOperation/` folder → `Contracts/Dto/TenantOperation/`

## Migration Sequence (Execution Order)

1. **NuGet packages** (Step 1) — foundation change
2. **Domain entities** (Step 2) — NumberSequence.RowVersion, CompanySystemSettings
3. **DbContexts** (Steps 3-4) — MasterDbContext, TenantDbContext + schema support
4. **Design-time factories** (Step 9) — for migration generation
5. **Delete old migrations, generate new** (Step 10)
6. **DI registration** (Step 4) — ServiceCollectionExtensions
7. **TenantDbContextFactory** (Step 4) — schema resolution
8. **TenantProvisioningService** (Step 5) — CREATE SCHEMA
9. **Remove Azure SQL** (Step 6) — delete files, rewrite controller
10. **Raw ADO.NET** (Step 7) — logging services, functions
11. **Config** (Step 8) — appsettings.json
12. **MigrationTool** (Step 11) — schema-based migration
13. **DTOs** (Step 13) — rename Azure → Tenant
14. **Tests** (Step 12) — update/delete
15. **Build verification** — `dotnet build InvoiceApi.sln`
16. **Test verification** — `dotnet test`

## Risk Assessment

| Risk | Mitigation |
|------|------------|
| Data loss during migration | This is a fresh schema approach — no data migration needed (dev environment) |
| EF Core PostgreSQL provider differences | Test all LINQ queries — `.ToLower().Contains()` works in both providers |
| Schema isolation not enforced | PostgreSQL `search_path` or `SET search_path TO tenant_X` ensures isolation |
| Concurrency token behavior change | `xmin` is auto-updated like `rowversion` — same optimistic concurrency semantics |
| InMemoryDatabase tests break | InMemoryDatabase is provider-agnostic — no changes needed |
| Azure Functions need PostgreSQL driver | `Npgsql` is already included transitively via EF Core PostgreSQL |

## File Count Summary

| Action | Count |
|--------|-------|
| Files to MODIFY | ~20 |
| Files to DELETE | ~22 (migrations + Azure SQL service + tests) |
| Files to CREATE | ~3 (new migrations + renamed controller) |
| **Total files affected** | **~45** |

## Version Alignment

All Npgsql/EF Core packages must be the same version to avoid runtime `FileNotFoundException`:
- `Npgsql.EntityFrameworkCore.PostgreSQL` → latest compatible with EF Core 10.0.1
- `Microsoft.EntityFrameworkCore.Design` → 10.0.1 (unchanged)
- `Microsoft.EntityFrameworkCore.Tools` → 10.0.1 (unchanged)
