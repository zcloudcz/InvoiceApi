using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace InvoiceApi.MigrationTool;

/// <summary>
/// Service that migrates data from a single SQL Server database (SourceDbContext)
/// to the multi-tenant architecture (MasterDbContext + per-tenant TenantDbContext).
///
/// Migration flow:
/// 1. Connect to source database (the existing single DB)
/// 2. Apply MasterDbContext migrations to the master database
/// 3. Copy Users → master DB
/// 4. Copy Client (IsIssuer=true) → master DB (with addresses + contacts)
/// 5. Copy code tables (Currency, VatRate, NumberSequenceFormat, ContentTemplate) → master DB
/// 6. For each issuer/company:
///    a. Create CompanySystemSettings in master DB
///    b. Create PostgreSQL tenant database
///    c. Apply TenantDbContext migrations
///    d. Copy the issuer + addresses + contacts → tenant DB
///    e. Copy customers (Clients linked via invoices) → tenant DB
///    f. Copy code tables → tenant DB
///    g. Copy business data (Invoices, InvoiceItems, InvoiceTemplates, NumberSequences) → tenant DB
///    h. Copy AresCache → tenant DB
/// 7. Verify data integrity
///
/// IMPORTANT: This tool is designed to be re-runnable (idempotent).
/// It skips companies that are already provisioned (SkipProvisionedCompanies=true).
/// </summary>
public class DataMigrationService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataMigrationService> _logger;

    // Counters for summary reporting
    private int _usersmigrated;
    private int _companiesMigrated;
    private int _customersMigrated;
    private int _invoicesMigrated;
    private int _templatesMigrated;
    private int _errors;

    public DataMigrationService(IConfiguration configuration, ILogger<DataMigrationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Executes the full migration from single-DB to multi-tenant architecture.
    /// </summary>
    public async Task<bool> MigrateAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== Starting Data Migration ===");

        var sourceConnectionString = _configuration.GetConnectionString("SourceConnection")
            ?? throw new InvalidOperationException("SourceConnection not configured.");
        var masterConnectionString = _configuration.GetConnectionString("MasterConnection")
            ?? throw new InvalidOperationException("MasterConnection not configured.");
        var dryRun = _configuration.GetValue<bool>("Migration:DryRun");
        var skipProvisioned = _configuration.GetValue<bool>("Migration:SkipProvisionedCompanies");
        var tenantPrefix = _configuration["Migration:TenantDatabasePrefix"] ?? "invoiceapi_tenant_";

        if (dryRun)
            _logger.LogWarning("DRY RUN mode — no data will be written");

        // Step 1: Create source and master DbContexts
        using var sourceContext = CreateSourceContext(sourceConnectionString);
        using var masterContext = CreateMasterContext(masterConnectionString);

        // Step 2: Apply master DB migrations
        _logger.LogInformation("Step 1: Applying master DB migrations...");
        if (!dryRun)
        {
            await masterContext.Database.MigrateAsync(cancellationToken);
            _logger.LogInformation("Master DB migrations applied successfully");
        }

        // Step 3: Copy Users → master DB
        _logger.LogInformation("Step 2: Migrating Users to master DB...");
        await MigrateUsersAsync(sourceContext, masterContext, dryRun, cancellationToken);

        // Step 4: Copy code tables → master DB
        _logger.LogInformation("Step 3: Migrating code tables to master DB...");
        await MigrateCodeTablesAsync(sourceContext, masterContext, dryRun, cancellationToken);

        // Step 5: Copy Issuers → master DB + create tenant DBs
        _logger.LogInformation("Step 4: Migrating companies and provisioning tenant databases...");
        var issuers = await sourceContext.Client
            .AsNoTracking()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .Include(c => c.BillingSettings)
            .Where(c => c.IsIssuer)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Found {Count} issuer companies to migrate", issuers.Count);

        foreach (var issuer in issuers)
        {
            try
            {
                await MigrateCompanyAsync(
                    issuer, sourceContext, masterContext,
                    masterConnectionString, tenantPrefix,
                    skipProvisioned, dryRun, cancellationToken);
            }
            catch (Exception ex)
            {
                _errors++;
                _logger.LogError(ex, "Failed to migrate company {CompanyId} ({CompanyName})",
                    issuer.Id, issuer.CompanyName);
            }
        }

        // Summary
        _logger.LogInformation("=== Migration Summary ===");
        _logger.LogInformation("Users migrated: {Count}", _usersmigrated);
        _logger.LogInformation("Companies migrated: {Count}", _companiesMigrated);
        _logger.LogInformation("Customers migrated: {Count}", _customersMigrated);
        _logger.LogInformation("Invoices migrated: {Count}", _invoicesMigrated);
        _logger.LogInformation("Templates migrated: {Count}", _templatesMigrated);
        _logger.LogInformation("Errors: {Count}", _errors);
        _logger.LogInformation("=== Migration {Status} ===",
            _errors == 0 ? "COMPLETED SUCCESSFULLY" : "COMPLETED WITH ERRORS");

        return _errors == 0;
    }

    #region User Migration

    /// <summary>
    /// Copies all User records from source DB to master DB.
    /// Skips users that already exist (matched by Email).
    /// </summary>
    private async Task MigrateUsersAsync(
        SourceDbContext source, MasterDbContext master,
        bool dryRun, CancellationToken ct)
    {
        var sourceUsers = await source.User
            .AsNoTracking()
            .ToListAsync(ct);

        var existingEmails = await master.User
            .Select(u => u.Email)
            .ToHashSetAsync(ct);

        var newUsers = sourceUsers
            .Where(u => !existingEmails.Contains(u.Email))
            .ToList();

        _logger.LogInformation("Users: {Total} in source, {Existing} already in master, {New} to migrate",
            sourceUsers.Count, existingEmails.Count, newUsers.Count);

        if (dryRun || newUsers.Count == 0) return;

        foreach (var user in newUsers)
        {
            master.User.Add(new User
            {
                Email = user.Email,
                PasswordHash = user.PasswordHash,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Role = user.Role,
                CompanyId = user.CompanyId,
                InvitationToken = user.InvitationToken,
                InvitationTokenExpiresAt = user.InvitationTokenExpiresAt,
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt,
                UpdatedAt = user.UpdatedAt,
                CreatedByUserId = user.CreatedByUserId,
                UpdatedByUserId = user.UpdatedByUserId
            });
        }

        await master.SaveChangesAsync(ct);
        _usersmigrated += newUsers.Count;
        _logger.LogInformation("Migrated {Count} users to master DB", newUsers.Count);
    }

    #endregion

    #region Code Table Migration

    /// <summary>
    /// Copies code tables (Currency, VatRate, NumberSequenceFormat, ContentTemplate) to master DB.
    /// Skips records that already exist (matched by unique fields like Code, Name, etc.).
    /// </summary>
    private async Task MigrateCodeTablesAsync(
        SourceDbContext source, MasterDbContext master,
        bool dryRun, CancellationToken ct)
    {
        // Currencies — match by Code
        var sourceCurrencies = await source.Currency.AsNoTracking().ToListAsync(ct);
        var existingCurrencyCodes = await master.Currency.Select(c => c.Code).ToHashSetAsync(ct);

        var newCurrencies = sourceCurrencies
            .Where(c => !existingCurrencyCodes.Contains(c.Code))
            .ToList();

        if (!dryRun && newCurrencies.Count > 0)
        {
            foreach (var c in newCurrencies)
            {
                master.Currency.Add(new Currency
                {
                    Code = c.Code,
                    Name = c.Name,
                    Symbol = c.Symbol,
                    DecimalPlaces = c.DecimalPlaces,
                    SortOrder = c.SortOrder,
                    DisplayFormat = c.DisplayFormat,
                    IsActive = c.IsActive,
                    CreatedAt = c.CreatedAt
                });
            }
            await master.SaveChangesAsync(ct);
        }
        _logger.LogInformation("Currencies: {New} new of {Total} total", newCurrencies.Count, sourceCurrencies.Count);

        // VatRates — match by Name + Rate
        var sourceRates = await source.VatRate.AsNoTracking().ToListAsync(ct);
        var existingRateKeys = (await master.VatRate.ToListAsync(ct))
            .Select(r => $"{r.Name}_{r.Rate}")
            .ToHashSet();

        var newRates = sourceRates
            .Where(r => !existingRateKeys.Contains($"{r.Name}_{r.Rate}"))
            .ToList();

        if (!dryRun && newRates.Count > 0)
        {
            foreach (var r in newRates)
            {
                master.VatRate.Add(new VatRate
                {
                    Name = r.Name,
                    Rate = r.Rate,
                    ValidFrom = r.ValidFrom,
                    ValidTo = r.ValidTo,
                    IsReduced = r.IsReduced,
                    IsDefault = r.IsDefault,
                    IsActive = r.IsActive,
                    CreatedAt = r.CreatedAt
                });
            }
            await master.SaveChangesAsync(ct);
        }
        _logger.LogInformation("VatRates: {New} new of {Total} total", newRates.Count, sourceRates.Count);

        // NumberSequenceFormats — match by FormatPattern
        var sourceFormats = await source.NumberSequenceFormat.AsNoTracking().ToListAsync(ct);
        var existingPatterns = await master.NumberSequenceFormat.Select(f => f.FormatPattern).ToHashSetAsync(ct);

        var newFormats = sourceFormats
            .Where(f => !existingPatterns.Contains(f.FormatPattern))
            .ToList();

        if (!dryRun && newFormats.Count > 0)
        {
            foreach (var f in newFormats)
            {
                master.NumberSequenceFormat.Add(new NumberSequenceFormat
                {
                    Name = f.Name,
                    FormatPattern = f.FormatPattern,
                    CounterDigits = f.CounterDigits,
                    ResetsYearly = f.ResetsYearly,
                    ResetsMonthly = f.ResetsMonthly,
                    IsActive = f.IsActive,
                    CreatedAt = f.CreatedAt
                });
            }
            await master.SaveChangesAsync(ct);
        }
        _logger.LogInformation("NumberSequenceFormats: {New} new of {Total} total", newFormats.Count, sourceFormats.Count);

        // ContentTemplates — match by Name + TemplateType
        var sourceTemplates = await source.ContentTemplate.AsNoTracking().ToListAsync(ct);
        var existingTemplateKeys = (await master.ContentTemplate.ToListAsync(ct))
            .Select(t => $"{t.Name}_{t.TemplateType}")
            .ToHashSet();

        var newTemplates = sourceTemplates
            .Where(t => !existingTemplateKeys.Contains($"{t.Name}_{t.TemplateType}"))
            .ToList();

        if (!dryRun && newTemplates.Count > 0)
        {
            foreach (var t in newTemplates)
            {
                master.ContentTemplate.Add(new ContentTemplate
                {
                    Name = t.Name,
                    Subject = t.Subject,
                    HtmlBody = t.HtmlBody,
                    TemplateType = t.TemplateType,
                    IsDefault = t.IsDefault,
                    IsActive = t.IsActive,
                    Description = t.Description,
                    CreatedAt = t.CreatedAt
                });
            }
            await master.SaveChangesAsync(ct);
        }
        _logger.LogInformation("ContentTemplates: {New} new of {Total} total", newTemplates.Count, sourceTemplates.Count);
    }

    #endregion

    #region Company Migration (Per-Tenant)

    /// <summary>
    /// Migrates a single company (issuer) to the multi-tenant architecture:
    /// 1. Creates/verifies the issuer record in master DB
    /// 2. Creates CompanySystemSettings in master DB
    /// 3. Creates and provisions the tenant database
    /// 4. Copies all business data (customers, invoices, templates, etc.) to tenant DB
    /// </summary>
    private async Task MigrateCompanyAsync(
        Client issuer,
        SourceDbContext source,
        MasterDbContext master,
        string masterConnectionString,
        string tenantPrefix,
        bool skipProvisioned,
        bool dryRun,
        CancellationToken ct)
    {
        _logger.LogInformation("--- Migrating company {Id}: {Name} ---", issuer.Id, issuer.CompanyName);

        // Step 1: Ensure issuer exists in master DB
        var masterIssuer = await master.Client
            .FirstOrDefaultAsync(c => c.RegistrationNumber == issuer.RegistrationNumber && c.IsIssuer, ct);

        if (masterIssuer == null && !dryRun)
        {
            masterIssuer = new Client
            {
                CompanyName = issuer.CompanyName,
                TradingName = issuer.TradingName,
                RegistrationNumber = issuer.RegistrationNumber,
                TaxNumber = issuer.TaxNumber,
                IsVatPayer = issuer.IsVatPayer,
                IsIssuer = true,
                IsActive = issuer.IsActive,
                LastAresFetchDate = issuer.LastAresFetchDate,
                CreatedAt = issuer.CreatedAt
            };
            master.Client.Add(masterIssuer);
            await master.SaveChangesAsync(ct);
            _logger.LogInformation("Created issuer in master DB: Id={Id}", masterIssuer.Id);
        }

        var masterCompanyId = masterIssuer?.Id ?? issuer.Id;

        // Step 2: Create or check CompanySystemSettings
        var existingSettings = await master.CompanySystemSettings
            .FirstOrDefaultAsync(s => s.CompanyId == masterCompanyId, ct);

        if (existingSettings != null && existingSettings.IsProvisioned && skipProvisioned)
        {
            _logger.LogInformation("Company {Id} already provisioned, skipping (SkipProvisionedCompanies=true)",
                masterCompanyId);
            return;
        }

        var databaseName = $"{tenantPrefix}{masterCompanyId}";

        if (existingSettings == null && !dryRun)
        {
            existingSettings = new CompanySystemSettings
            {
                CompanyId = masterCompanyId,
                DatabaseName = databaseName,
                IsProvisioned = false,
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            };
            master.CompanySystemSettings.Add(existingSettings);
            await master.SaveChangesAsync(ct);
            _logger.LogInformation("Created CompanySystemSettings for company {Id}", masterCompanyId);
        }

        if (dryRun)
        {
            _logger.LogInformation("[DRY RUN] Would provision tenant DB '{Database}' for company {Id}",
                databaseName, masterCompanyId);
            _companiesMigrated++;
            return;
        }

        // Step 3: Create tenant database + apply migrations
        var tenantConnectionString = BuildTenantConnectionString(masterConnectionString, databaseName);
        await CreateDatabaseIfNotExistsAsync(masterConnectionString, databaseName, ct);

        using var tenantContext = CreateTenantContext(tenantConnectionString);
        await tenantContext.Database.MigrateAsync(ct);
        _logger.LogInformation("Tenant database '{Database}' created and migrated", databaseName);

        // Step 4: Copy code tables to tenant DB
        await CopyCodeTablesToTenantAsync(source, tenantContext, ct);

        // Step 5: Copy the issuer to tenant DB
        await CopyIssuerToTenantAsync(issuer, tenantContext, ct);

        // Step 6: Copy customers (non-issuer clients related to this company's invoices)
        var sourceIssuerId = issuer.Id;
        await CopyCustomersToTenantAsync(source, tenantContext, sourceIssuerId, ct);

        // Step 7: Copy number sequences + formats
        await CopyNumberSequencesToTenantAsync(source, tenantContext, sourceIssuerId, ct);

        // Step 8: Copy invoices + invoice items
        await CopyInvoicesToTenantAsync(source, tenantContext, sourceIssuerId, ct);

        // Step 9: Copy invoice templates
        await CopyInvoiceTemplatesToTenantAsync(source, tenantContext, sourceIssuerId, ct);

        // Step 10: Copy AresCache
        await CopyAresCacheToTenantAsync(source, tenantContext, ct);

        // Step 11: Mark as provisioned in master DB
        existingSettings!.IsProvisioned = true;
        existingSettings.IsActive = true;
        existingSettings.ProvisionedAt = DateTime.UtcNow;
        existingSettings.ConnectionString = tenantConnectionString;
        await master.SaveChangesAsync(ct);

        _companiesMigrated++;
        _logger.LogInformation("Company {Id} ({Name}) migration COMPLETE", masterCompanyId, issuer.CompanyName);
    }

    #endregion

    #region Tenant Data Copy Methods

    /// <summary>
    /// Copies code tables from source DB to tenant DB.
    /// Uses a fresh copy (not referencing source IDs) — lets EF generate new IDs.
    /// Builds an ID mapping so we can remap FKs in business data.
    /// </summary>
    private async Task CopyCodeTablesToTenantAsync(
        SourceDbContext source, TenantDbContext tenant, CancellationToken ct)
    {
        // Check if code tables already exist in tenant (idempotent)
        if (await tenant.Currency.AnyAsync(ct))
        {
            _logger.LogInformation("Code tables already exist in tenant, skipping copy");
            return;
        }

        // Currencies
        var currencies = await source.Currency.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        foreach (var c in currencies)
        {
            tenant.Currency.Add(new Currency
            {
                Code = c.Code, Name = c.Name, Symbol = c.Symbol,
                DecimalPlaces = c.DecimalPlaces, SortOrder = c.SortOrder,
                DisplayFormat = c.DisplayFormat, IsActive = c.IsActive,
                CreatedAt = DateTime.UtcNow
            });
        }

        // VatRates
        var vatRates = await source.VatRate.AsNoTracking().Where(v => v.IsActive).ToListAsync(ct);
        foreach (var r in vatRates)
        {
            tenant.VatRate.Add(new VatRate
            {
                Name = r.Name, Rate = r.Rate, ValidFrom = r.ValidFrom, ValidTo = r.ValidTo,
                IsReduced = r.IsReduced, IsDefault = r.IsDefault, IsActive = r.IsActive,
                CreatedAt = DateTime.UtcNow
            });
        }

        // NumberSequenceFormats
        var formats = await source.NumberSequenceFormat.AsNoTracking().Where(f => f.IsActive).ToListAsync(ct);
        foreach (var f in formats)
        {
            tenant.NumberSequenceFormat.Add(new NumberSequenceFormat
            {
                Name = f.Name, FormatPattern = f.FormatPattern, CounterDigits = f.CounterDigits,
                ResetsYearly = f.ResetsYearly, ResetsMonthly = f.ResetsMonthly,
                IsActive = f.IsActive, CreatedAt = DateTime.UtcNow
            });
        }

        // ContentTemplates
        var templates = await source.ContentTemplate.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
        foreach (var t in templates)
        {
            tenant.ContentTemplate.Add(new ContentTemplate
            {
                Name = t.Name, Subject = t.Subject, HtmlBody = t.HtmlBody,
                TemplateType = t.TemplateType, IsDefault = t.IsDefault,
                IsActive = t.IsActive, Description = t.Description,
                CreatedAt = DateTime.UtcNow
            });
        }

        await tenant.SaveChangesAsync(ct);
        _logger.LogInformation("Copied code tables: {Currencies} currencies, {VatRates} rates, {Formats} formats, {Templates} templates",
            currencies.Count, vatRates.Count, formats.Count, templates.Count);
    }

    /// <summary>
    /// Copies the issuer (company) record to the tenant database.
    /// Includes addresses and contacts.
    /// </summary>
    private async Task CopyIssuerToTenantAsync(
        Client issuer, TenantDbContext tenant, CancellationToken ct)
    {
        // Check if issuer already exists
        if (await tenant.Client.AnyAsync(c => c.IsIssuer, ct))
        {
            _logger.LogInformation("Issuer already exists in tenant, skipping");
            return;
        }

        var tenantIssuer = new Client
        {
            CompanyName = issuer.CompanyName,
            TradingName = issuer.TradingName,
            RegistrationNumber = issuer.RegistrationNumber,
            TaxNumber = issuer.TaxNumber,
            IsVatPayer = issuer.IsVatPayer,
            IsIssuer = true,
            IsActive = true,
            LastAresFetchDate = issuer.LastAresFetchDate,
            CreatedAt = issuer.CreatedAt
        };

        // Copy addresses
        if (issuer.Address != null)
        {
            foreach (var addr in issuer.Address)
            {
                tenantIssuer.Address.Add(new Address
                {
                    AddressType = addr.AddressType,
                    Street = addr.Street, City = addr.City,
                    PostalCode = addr.PostalCode, Country = addr.Country,
                    AddressLine2 = addr.AddressLine2, IsPrimary = addr.IsPrimary,
                    CreatedAt = addr.CreatedAt
                });
            }
        }

        // Copy contacts
        if (issuer.Contact != null)
        {
            foreach (var contact in issuer.Contact)
            {
                tenantIssuer.Contact.Add(new Contact
                {
                    ContactType = contact.ContactType,
                    ContactValue = contact.ContactValue,
                    Label = contact.Label, IsPrimary = contact.IsPrimary,
                    CreatedAt = contact.CreatedAt
                });
            }
        }

        tenant.Client.Add(tenantIssuer);
        await tenant.SaveChangesAsync(ct);
        _logger.LogInformation("Copied issuer to tenant: {Name}", issuer.CompanyName);
    }

    /// <summary>
    /// Copies customer clients (non-issuers) from the source DB to the tenant DB.
    /// Only copies customers that are referenced by invoices from this issuer.
    /// Builds a mapping of source client IDs → tenant client IDs for invoice FK remapping.
    /// </summary>
    private async Task CopyCustomersToTenantAsync(
        SourceDbContext source, TenantDbContext tenant,
        long sourceIssuerId, CancellationToken ct)
    {
        // Find all unique client IDs referenced by this issuer's invoices
        var customerIds = await source.Invoice
            .AsNoTracking()
            .Where(i => i.IssuerId == sourceIssuerId && i.ClientId != null)
            .Select(i => i.ClientId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (customerIds.Count == 0)
        {
            _logger.LogInformation("No customers to copy (no invoices)");
            return;
        }

        // Load full customer data including addresses, contacts, billing settings
        var customers = await source.Client
            .AsNoTracking()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .Include(c => c.BillingSettings)
            .Where(c => customerIds.Contains(c.Id) && !c.IsIssuer)
            .ToListAsync(ct);

        // Also get the issuer ID in tenant DB (for remapping IssuerId on invoices)
        var tenantIssuer = await tenant.Client.FirstAsync(c => c.IsIssuer, ct);

        var count = 0;
        foreach (var customer in customers)
        {
            // Check if customer already exists (by RegistrationNumber or CompanyName)
            var exists = await tenant.Client.AnyAsync(c =>
                c.CompanyName == customer.CompanyName && !c.IsIssuer, ct);

            if (exists) continue;

            // First resolve currency ID in tenant DB (if customer has a preferred currency)
            long? tenantCurrencyId = null;
            if (customer.PreferredCurrencyId.HasValue)
            {
                var sourceCurrency = await source.Currency.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == customer.PreferredCurrencyId.Value, ct);
                if (sourceCurrency != null)
                {
                    var tenantCurrency = await tenant.Currency
                        .FirstOrDefaultAsync(c => c.Code == sourceCurrency.Code, ct);
                    tenantCurrencyId = tenantCurrency?.Id;
                }
            }

            var tenantCustomer = new Client
            {
                CompanyName = customer.CompanyName,
                TradingName = customer.TradingName,
                RegistrationNumber = customer.RegistrationNumber,
                TaxNumber = customer.TaxNumber,
                IsVatPayer = customer.IsVatPayer,
                IsIssuer = false,
                IsActive = customer.IsActive,
                PreferredCurrencyId = tenantCurrencyId,
                LastAresFetchDate = customer.LastAresFetchDate,
                CreatedAt = customer.CreatedAt
            };

            // Copy addresses
            if (customer.Address != null)
            {
                foreach (var addr in customer.Address)
                {
                    tenantCustomer.Address.Add(new Address
                    {
                        AddressType = addr.AddressType,
                        Street = addr.Street, City = addr.City,
                        PostalCode = addr.PostalCode, Country = addr.Country,
                        AddressLine2 = addr.AddressLine2, IsPrimary = addr.IsPrimary,
                        CreatedAt = addr.CreatedAt
                    });
                }
            }

            // Copy contacts
            if (customer.Contact != null)
            {
                foreach (var contact in customer.Contact)
                {
                    tenantCustomer.Contact.Add(new Contact
                    {
                        ContactType = contact.ContactType,
                        ContactValue = contact.ContactValue,
                        Label = contact.Label, IsPrimary = contact.IsPrimary,
                        CreatedAt = contact.CreatedAt
                    });
                }
            }

            tenant.Client.Add(tenantCustomer);
            await tenant.SaveChangesAsync(ct);

            // Copy billing settings (one-to-one, must be saved after client)
            if (customer.BillingSettings != null)
            {
                var bs = customer.BillingSettings;
                tenant.BillingSettings.Add(new BillingSettings
                {
                    ClientId = tenantCustomer.Id,
                    DueDays = bs.DueDays,
                    DueDateCalculationType = bs.DueDateCalculationType,
                    InvoiceNumberPrefix = bs.InvoiceNumberPrefix,
                    InvoiceNumberSuffix = bs.InvoiceNumberSuffix,
                    CreditNoteNumberPrefix = bs.CreditNoteNumberPrefix,
                    CreditNoteNumberSuffix = bs.CreditNoteNumberSuffix,
                    DefaultPaymentMethod = bs.DefaultPaymentMethod,
                    BankAccountNumber = bs.BankAccountNumber,
                    Notes = bs.Notes,
                    CreatedAt = bs.CreatedAt
                });
                await tenant.SaveChangesAsync(ct);
            }

            count++;
        }

        _customersMigrated += count;
        _logger.LogInformation("Copied {Count} customers to tenant", count);
    }

    /// <summary>
    /// Copies NumberSequences from source to tenant.
    /// Remaps NumberSequenceFormatId using FormatPattern matching.
    /// </summary>
    private async Task CopyNumberSequencesToTenantAsync(
        SourceDbContext source, TenantDbContext tenant,
        long sourceIssuerId, CancellationToken ct)
    {
        // Check if sequences already exist in tenant
        if (await tenant.NumberSequence.AnyAsync(ct))
        {
            _logger.LogInformation("Number sequences already exist in tenant, skipping");
            return;
        }

        // Load all sequences from source (they're not issuer-scoped in single-DB)
        var sourceSequences = await source.NumberSequence
            .AsNoTracking()
            .Include(s => s.NumberSequenceFormat)
            .ToListAsync(ct);

        // Build format ID mapping: source FormatPattern → tenant Format ID
        var tenantFormats = await tenant.NumberSequenceFormat.ToListAsync(ct);
        var formatMapping = new Dictionary<long, long>(); // source ID → tenant ID
        foreach (var sf in sourceSequences.Select(s => s.NumberSequenceFormat).Where(f => f != null).Distinct())
        {
            var tenantFormat = tenantFormats.FirstOrDefault(tf => tf.FormatPattern == sf!.FormatPattern);
            if (tenantFormat != null)
                formatMapping[sf!.Id] = tenantFormat.Id;
        }

        foreach (var seq in sourceSequences)
        {
            if (!formatMapping.TryGetValue(seq.NumberSequenceFormatId, out var tenantFormatId))
            {
                _logger.LogWarning("No matching format for sequence '{Name}' (format ID {FormatId}), using first available",
                    seq.Name, seq.NumberSequenceFormatId);
                tenantFormatId = tenantFormats.FirstOrDefault()?.Id ?? 0;
                if (tenantFormatId == 0) continue;
            }

            tenant.NumberSequence.Add(new NumberSequence
            {
                Name = seq.Name,
                DocumentType = seq.DocumentType,
                Prefix = seq.Prefix,
                Suffix = seq.Suffix,
                CurrentNumber = seq.CurrentNumber,
                IsDefault = seq.IsDefault,
                IsActive = seq.IsActive,
                NumberSequenceFormatId = tenantFormatId,
                CreatedAt = seq.CreatedAt
            });
        }

        await tenant.SaveChangesAsync(ct);
        _logger.LogInformation("Copied {Count} number sequences to tenant", sourceSequences.Count);
    }

    /// <summary>
    /// Copies invoices and their items from source to tenant.
    /// Remaps IssuerId, ClientId, CurrencyId, and OriginalInvoiceId to tenant IDs.
    /// </summary>
    private async Task CopyInvoicesToTenantAsync(
        SourceDbContext source, TenantDbContext tenant,
        long sourceIssuerId, CancellationToken ct)
    {
        // Load all invoices for this issuer (including credit notes)
        var sourceInvoices = await source.Invoice
            .AsNoTracking()
            .Include(i => i.InvoiceItem)
            .Where(i => i.IssuerId == sourceIssuerId)
            .OrderBy(i => i.Id) // Process in order so OriginalInvoiceId references resolve
            .ToListAsync(ct);

        if (sourceInvoices.Count == 0)
        {
            _logger.LogInformation("No invoices to copy for this issuer");
            return;
        }

        // Build ID mappings for FK remapping
        var tenantIssuer = await tenant.Client.FirstAsync(c => c.IsIssuer, ct);
        var tenantClients = await tenant.Client.Where(c => !c.IsIssuer).ToListAsync(ct);
        var sourceClients = await source.Client.AsNoTracking()
            .Where(c => !c.IsIssuer).ToListAsync(ct);

        // Build client mapping: source ID → tenant ID (by CompanyName match)
        var clientMapping = new Dictionary<long, long>();
        foreach (var sc in sourceClients)
        {
            var tc = tenantClients.FirstOrDefault(c => c.CompanyName == sc.CompanyName);
            if (tc != null)
                clientMapping[sc.Id] = tc.Id;
        }

        // Build currency mapping: source ID → tenant ID (by Code)
        var sourceCurrencies = await source.Currency.AsNoTracking().ToListAsync(ct);
        var tenantCurrencies = await tenant.Currency.ToListAsync(ct);
        var currencyMapping = new Dictionary<long, long>();
        foreach (var sc in sourceCurrencies)
        {
            var tc = tenantCurrencies.FirstOrDefault(c => c.Code == sc.Code);
            if (tc != null)
                currencyMapping[sc.Id] = tc.Id;
        }

        // Build VatRate mapping: source ID → tenant ID (by Name + Rate)
        var sourceVatRates = await source.VatRate.AsNoTracking().ToListAsync(ct);
        var tenantVatRates = await tenant.VatRate.ToListAsync(ct);
        var vatRateMapping = new Dictionary<long, long>();
        foreach (var sr in sourceVatRates)
        {
            var tr = tenantVatRates.FirstOrDefault(r => r.Name == sr.Name && r.Rate == sr.Rate);
            if (tr != null)
                vatRateMapping[sr.Id] = tr.Id;
        }

        // Track source invoice ID → tenant invoice ID for credit note OriginalInvoiceId
        var invoiceIdMapping = new Dictionary<long, long>();

        var count = 0;
        foreach (var inv in sourceInvoices)
        {
            // Remap client ID
            long? tenantClientId = null;
            if (inv.ClientId.HasValue && clientMapping.TryGetValue(inv.ClientId.Value, out var mappedClientId))
                tenantClientId = mappedClientId;

            // Remap currency ID
            long tenantCurrencyId = 0;
            if (currencyMapping.TryGetValue(inv.CurrencyId, out var mappedCurrencyId))
                tenantCurrencyId = mappedCurrencyId;
            else
            {
                tenantCurrencyId = tenantCurrencies.FirstOrDefault()?.Id ?? 0;
                _logger.LogWarning("Currency mapping not found for invoice {DocNumber}, using default", inv.DocumentNumber);
            }

            // Remap OriginalInvoiceId (for credit notes)
            long? tenantOriginalInvoiceId = null;
            if (inv.OriginalInvoiceId.HasValue &&
                invoiceIdMapping.TryGetValue(inv.OriginalInvoiceId.Value, out var mappedOrigId))
            {
                tenantOriginalInvoiceId = mappedOrigId;
            }

            var tenantInvoice = new Invoice
            {
                DocumentType = inv.DocumentType,
                Status = inv.Status,
                DocumentNumber = inv.DocumentNumber,
                IssueDate = inv.IssueDate,
                DueDate = inv.DueDate,
                TaxableSupplyDate = inv.TaxableSupplyDate,
                IssuerId = tenantIssuer.Id,
                ClientId = tenantClientId,
                CurrencyId = tenantCurrencyId,
                OriginalInvoiceId = tenantOriginalInvoiceId,
                PaymentMethod = inv.PaymentMethod,
                BankAccountNumber = inv.BankAccountNumber,
                IBAN = inv.IBAN,
                SWIFT = inv.SWIFT,
                VariableSymbol = inv.VariableSymbol,
                ConstantSymbol = inv.ConstantSymbol,
                SpecificSymbol = inv.SpecificSymbol,
                TotalBeforeVat = inv.TotalBeforeVat,
                TotalVat = inv.TotalVat,
                TotalWithVat = inv.TotalWithVat,
                Notes = inv.Notes,
                PaidAt = inv.PaidAt,
                IsSentByEmail = inv.IsSentByEmail,
                LastSentByEmailAt = inv.LastSentByEmailAt,
                IsExported = inv.IsExported,
                LastExportedAt = inv.LastExportedAt,
                CreatedAt = inv.CreatedAt,
                UpdatedAt = inv.UpdatedAt,
                CreatedByUserId = inv.CreatedByUserId,
                UpdatedByUserId = inv.UpdatedByUserId,
                InvoiceItem = new List<InvoiceItem>()
            };

            // Copy invoice items with remapped VatRateId
            if (inv.InvoiceItem != null)
            {
                foreach (var item in inv.InvoiceItem)
                {
                    long tenantVatRateId = 0;
                    if (item.VatRateId.HasValue &&
                        vatRateMapping.TryGetValue(item.VatRateId.Value, out var mappedVatId))
                    {
                        tenantVatRateId = mappedVatId;
                    }

                    tenantInvoice.InvoiceItem.Add(new InvoiceItem
                    {
                        ProductCode = item.ProductCode,
                        Description = item.Description,
                        Quantity = item.Quantity,
                        Unit = item.Unit,
                        UnitPrice = item.UnitPrice,
                        VatRateId = tenantVatRateId > 0 ? tenantVatRateId : null,
                        VatRatePercentage = item.VatRatePercentage,
                        TotalBeforeVat = item.TotalBeforeVat,
                        VatAmount = item.VatAmount,
                        TotalWithVat = item.TotalWithVat,
                        OrderIndex = item.OrderIndex,
                        Notes = item.Notes,
                        CreatedAt = item.CreatedAt
                    });
                }
            }

            tenant.Invoice.Add(tenantInvoice);
            await tenant.SaveChangesAsync(ct);

            // Track the ID mapping for credit note references
            invoiceIdMapping[inv.Id] = tenantInvoice.Id;
            count++;
        }

        _invoicesMigrated += count;
        _logger.LogInformation("Copied {Count} invoices (with items) to tenant", count);
    }

    /// <summary>
    /// Copies invoice templates from source to tenant.
    /// Remaps IssuerId, ClientId, CurrencyId, NumberSequenceId to tenant IDs.
    /// </summary>
    private async Task CopyInvoiceTemplatesToTenantAsync(
        SourceDbContext source, TenantDbContext tenant,
        long sourceIssuerId, CancellationToken ct)
    {
        var sourceTemplates = await source.InvoiceTemplate
            .AsNoTracking()
            .Include(t => t.InvoiceItem)
            .Where(t => t.IssuerId == sourceIssuerId)
            .ToListAsync(ct);

        if (sourceTemplates.Count == 0)
        {
            _logger.LogInformation("No invoice templates to copy for this issuer");
            return;
        }

        var tenantIssuer = await tenant.Client.FirstAsync(c => c.IsIssuer, ct);
        var tenantClients = await tenant.Client.Where(c => !c.IsIssuer).ToListAsync(ct);
        var sourceClients = await source.Client.AsNoTracking().Where(c => !c.IsIssuer).ToListAsync(ct);

        // Build mappings (same as invoice migration)
        var clientMapping = new Dictionary<long, long>();
        foreach (var sc in sourceClients)
        {
            var tc = tenantClients.FirstOrDefault(c => c.CompanyName == sc.CompanyName);
            if (tc != null)
                clientMapping[sc.Id] = tc.Id;
        }

        var tenantCurrencies = await tenant.Currency.ToListAsync(ct);
        var sourceCurrencies = await source.Currency.AsNoTracking().ToListAsync(ct);
        var currencyMapping = new Dictionary<long, long>();
        foreach (var sc in sourceCurrencies)
        {
            var tc = tenantCurrencies.FirstOrDefault(c => c.Code == sc.Code);
            if (tc != null)
                currencyMapping[sc.Id] = tc.Id;
        }

        var tenantVatRates = await tenant.VatRate.ToListAsync(ct);
        var sourceVatRates = await source.VatRate.AsNoTracking().ToListAsync(ct);
        var vatRateMapping = new Dictionary<long, long>();
        foreach (var sr in sourceVatRates)
        {
            var tr = tenantVatRates.FirstOrDefault(r => r.Name == sr.Name && r.Rate == sr.Rate);
            if (tr != null)
                vatRateMapping[sr.Id] = tr.Id;
        }

        var count = 0;
        foreach (var tpl in sourceTemplates)
        {
            long? tenantClientId = null;
            if (tpl.ClientId.HasValue && clientMapping.TryGetValue(tpl.ClientId.Value, out var mc))
                tenantClientId = mc;

            long tenantCurrencyId = 0;
            if (currencyMapping.TryGetValue(tpl.CurrencyId, out var mCur))
                tenantCurrencyId = mCur;
            else
                tenantCurrencyId = tenantCurrencies.FirstOrDefault()?.Id ?? 0;

            var tenantTemplate = new InvoiceTemplate
            {
                Name = tpl.Name,
                DocumentType = tpl.DocumentType,
                IssuerId = tenantIssuer.Id,
                ClientId = tenantClientId,
                CurrencyId = tenantCurrencyId,
                PaymentMethod = tpl.PaymentMethod,
                BankAccountNumber = tpl.BankAccountNumber,
                VariableSymbol = tpl.VariableSymbol,
                ConstantSymbol = tpl.ConstantSymbol,
                SpecificSymbol = tpl.SpecificSymbol,
                Notes = tpl.Notes,
                DueDateOffsetDays = tpl.DueDateOffsetDays,
                UsageCount = tpl.UsageCount,
                IsActive = tpl.IsActive,
                CreatedAt = tpl.CreatedAt,
                InvoiceItem = new List<InvoiceItem>()
            };

            // Copy template items
            if (tpl.InvoiceItem != null)
            {
                foreach (var item in tpl.InvoiceItem)
                {
                    long? tenantVatRateId = null;
                    if (item.VatRateId.HasValue && vatRateMapping.TryGetValue(item.VatRateId.Value, out var mv))
                        tenantVatRateId = mv;

                    tenantTemplate.InvoiceItem.Add(new InvoiceItem
                    {
                        ProductCode = item.ProductCode,
                        Description = item.Description,
                        Quantity = item.Quantity,
                        Unit = item.Unit,
                        UnitPrice = item.UnitPrice,
                        VatRateId = tenantVatRateId,
                        VatRatePercentage = item.VatRatePercentage,
                        TotalBeforeVat = item.TotalBeforeVat,
                        VatAmount = item.VatAmount,
                        TotalWithVat = item.TotalWithVat,
                        OrderIndex = item.OrderIndex,
                        Notes = item.Notes,
                        CreatedAt = item.CreatedAt
                    });
                }
            }

            tenant.InvoiceTemplate.Add(tenantTemplate);
            await tenant.SaveChangesAsync(ct);
            count++;
        }

        _templatesMigrated += count;
        _logger.LogInformation("Copied {Count} invoice templates to tenant", count);
    }

    /// <summary>
    /// Copies AresCache records to the tenant database.
    /// These are cached company registry lookups — useful to keep for performance.
    /// </summary>
    private static async Task CopyAresCacheToTenantAsync(
        SourceDbContext source, TenantDbContext tenant, CancellationToken ct)
    {
        if (await tenant.AresCache.AnyAsync(ct))
            return;

        var cacheEntries = await source.AresCache.AsNoTracking().ToListAsync(ct);
        foreach (var entry in cacheEntries)
        {
            tenant.AresCache.Add(new AresCache
            {
                RegistrationNumber = entry.RegistrationNumber,
                JsonData = entry.JsonData,
                FetchedAt = entry.FetchedAt,
                ExpiresAt = entry.ExpiresAt,
                CreatedAt = entry.CreatedAt
            });
        }

        if (cacheEntries.Count > 0)
            await tenant.SaveChangesAsync(ct);
    }

    #endregion

    #region Database Helpers

    /// <summary>
    /// Creates a SQL Server database if it doesn't already exist.
    /// Connects to the "master" system database to execute CREATE DATABASE.
    /// </summary>
    private async Task CreateDatabaseIfNotExistsAsync(
        string masterConnectionString, string databaseName, CancellationToken ct)
    {
        // Connect to the "master" system database to run CREATE DATABASE
        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = "master"
        };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        // Sanitize database name (alphanumeric + underscore only)
        var safeName = new string(databaseName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

        // Check if database exists using SQL Server's DB_ID() function
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = $"SELECT DB_ID('{safeName}')";
        var exists = await checkCmd.ExecuteScalarAsync(ct);

        if (exists == null || exists == DBNull.Value)
        {
            await using var createCmd = connection.CreateCommand();
            createCmd.CommandText = $"CREATE DATABASE [{safeName}]";
            await createCmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Created SQL Server database '{Name}'", safeName);
        }
    }

    /// <summary>
    /// Builds a tenant connection string from the master connection string
    /// by replacing the Database (Initial Catalog) name.
    /// </summary>
    private static string BuildTenantConnectionString(string masterConnectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Creates a SourceDbContext connected to the source (legacy single) database.
    /// </summary>
    private static SourceDbContext CreateSourceContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SourceDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new SourceDbContext(options);
    }

    /// <summary>
    /// Creates a MasterDbContext connected to the master database.
    /// </summary>
    private static MasterDbContext CreateMasterContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseSqlServer(connectionString, b => b.MigrationsAssembly("InvoiceApi.Infrastructure"))
            .Options;
        return new MasterDbContext(options);
    }

    /// <summary>
    /// Creates a TenantDbContext connected to a specific tenant database.
    /// </summary>
    private static TenantDbContext CreateTenantContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlServer(connectionString, b => b.MigrationsAssembly("InvoiceApi.Infrastructure"))
            .Options;
        return new TenantDbContext(options);
    }

    #endregion
}
