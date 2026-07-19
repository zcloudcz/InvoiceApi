using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Database context for the MASTER schema ("public") in the multi-tenant PostgreSQL architecture.
///
/// Architecture: Single PostgreSQL database with schema-per-tenant isolation.
/// Master data lives in the "public" schema, each tenant gets "tenant_{companyId}" schema.
///
/// The master schema stores:
/// - Users (authentication, roles, company assignment)
/// - Companies (Client records where IsIssuer = true — the company registry)
/// - CompanySystemSettings (tenant infrastructure config: schema name, provisioning status)
/// - Code tables used as "source of truth" for provisioning new tenants:
///   VatRate, Currency, NumberSequenceFormat, ContentTemplate
///
/// The master schema does NOT store business data (invoices, invoice items, etc.)
/// — that lives in per-tenant schemas managed by TenantDbContext.
///
/// Implements IDataProtectionKeyContext so that ASP.NET Core Data Protection stores its
/// encryption key ring in PostgreSQL rather than in the ephemeral file system.
/// Without this, every app restart on Azure App Service or Azure Functions generates a
/// new key ring and can no longer decrypt credentials (IMAP passwords, OAuth tokens, etc.)
/// that were encrypted with the previous key ring.
///
/// Lifecycle:
/// 1. SysAdmin creates a company (Client with IsIssuer = true)
/// 2. CompanySystemSettings record added with SchemaName
/// 3. TenantProvisioningService creates tenant schema, applies migrations, copies code tables
/// 4. Users are added to master schema with CompanyId pointing to the company
/// 5. On login, AuthService checks tenant status (provisioned + active) before issuing JWT
/// </summary>
public class MasterDbContext : DbContext, IDataProtectionKeyContext
{
    /// <summary>
    /// Service that provides the currently authenticated user's ID.
    /// Used to auto-fill CreatedByUserId and UpdatedByUserId audit fields.
    /// Nullable because during migrations/seeding, no user context exists.
    /// </summary>
    private readonly ICurrentUserService? _currentUserService;

    public MasterDbContext(DbContextOptions<MasterDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Constructor that accepts ICurrentUserService for audit trail support.
    /// Used at runtime when the DI container provides the service.
    /// </summary>
    public MasterDbContext(
        DbContextOptions<MasterDbContext> options,
        ICurrentUserService currentUserService)
        : base(options)
    {
        _currentUserService = currentUserService;
    }

    // ─── DbSets ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Users in the system (all users across all tenants are stored in master DB).
    /// User.CompanyId is the FK to the Client (company) they belong to.
    /// </summary>
    public DbSet<User> User { get; set; }

    /// <summary>
    /// Per-user UI preferences (grid page size, …). One row per user, created lazily
    /// on first save. Lives in master DB so preferences follow the user across tenants.
    /// </summary>
    public DbSet<UserPreferences> UserPreferences { get; set; }

    /// <summary>
    /// Companies (Client records where IsIssuer = true).
    /// In the master DB, we only store issuer/company records — customers live in tenant DBs.
    /// NOTE: The Client table schema is the same, but master DB only contains IsIssuer = true records.
    /// </summary>
    public DbSet<Client> Client { get; set; }

    /// <summary>
    /// Multi-tenant infrastructure configuration for each company.
    /// Each company gets one record with its tenant database name, connection string, etc.
    /// </summary>
    public DbSet<CompanySystemSettings> CompanySystemSettings { get; set; }

    /// <summary>
    /// VAT rates — master copy used as source for provisioning new tenants.
    /// Each tenant gets its own copy of these rates.
    /// </summary>
    public DbSet<VatRate> VatRate { get; set; }

    /// <summary>
    /// Currencies — master copy used as source for provisioning new tenants.
    /// </summary>
    public DbSet<Currency> Currency { get; set; }

    /// <summary>
    /// Number sequence formats — master copy used as source for provisioning.
    /// </summary>
    public DbSet<NumberSequenceFormat> NumberSequenceFormat { get; set; }

    /// <summary>
    /// Content templates (PDF + email) — master copy used as source for provisioning.
    /// </summary>
    public DbSet<ContentTemplate> ContentTemplate { get; set; }

    /// <summary>
    /// Global system configuration (SMTP, JWT settings).
    /// Single-row table — always exactly one record. Auto-created on first access
    /// by SystemConfigurationService if the table is empty.
    /// </summary>
    public DbSet<SystemConfiguration> SystemConfiguration { get; set; }

    /// <summary>
    /// Application logs stored in the master database.
    /// Written asynchronously by DatabaseLoggerProvider + LogFlushService.
    /// SysAdmin can view and filter these on the /logs page.
    /// </summary>
    public DbSet<AppLog> AppLog { get; set; }

    /// <summary>
    /// Billing settings for companies (issuers) — bank account, payment method, due date config.
    /// Stored in master DB so SysAdmin can manage company billing settings (e.g., bank account)
    /// without needing a provisioned tenant database.
    /// NOTE: NumberSequence FKs are NOT configured here — NumberSequence is tenant-only.
    /// </summary>
    public DbSet<BillingSettings> BillingSettings { get; set; }

    /// <summary>
    /// Client bank accounts (1:N per client).
    /// Companies (issuers) store their bank accounts in the master DB so SysAdmin
    /// can manage them without a provisioned tenant database.
    /// </summary>
    public DbSet<BankAccount> BankAccount { get; set; }

    /// <summary>
    /// Cached ARES lookups (Czech business registry data).
    /// Stored in master DB so that ARES cache is available regardless of tenant context
    /// (e.g., when SysAdmin creates companies without impersonation).
    /// </summary>
    public DbSet<AresCache> AresCache { get; set; }

    /// <summary>
    /// Tax year configurations — annual rates and thresholds for CZ and SK.
    /// Stored in master DB (shared across all tenants).
    /// </summary>
    public DbSet<TaxYearConfig> TaxYearConfig { get; set; }

    // ─── ASP.NET Core Data Protection key ring ────────────────────────────────

    /// <summary>
    /// Stores ASP.NET Core Data Protection encryption keys in PostgreSQL so they
    /// survive application restarts, deployments, and Azure scale-out events.
    ///
    /// Without persistence: every restart generates a new key ring → all credentials
    /// encrypted with the old key ring (IMAP passwords, OAuth tokens, TOTP secrets)
    /// become unreadable → CredentialProtector.Decrypt catches CryptographicException
    /// and returns the raw ciphertext as "legacy plaintext" → MailKit receives garbage
    /// instead of the real password → AuthenticationException "Incorrect authentication data".
    ///
    /// Populated automatically by PersistKeysToDbContext<MasterDbContext>() in DI setup.
    /// The table is created via a dedicated master migration (AddDataProtectionKeys_v109).
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    // ─── Payment Matching (see PLATBY-ZADANI.md) ──────────────────────────────

    /// <summary>
    /// Global index mapping inbound alias → tenant schema.
    /// The IMAP worker uses this to route incoming emails to the correct tenant.
    /// Master-only table because the worker runs outside any tenant context.
    /// </summary>
    public DbSet<MasterMailboxIndex> MasterMailboxIndex { get; set; }

    /// <summary>
    /// System-wide configuration for payment matching (IMAP credentials, interval, domain).
    /// Single-row table (Id = 1), owned by SysAdmin.
    /// </summary>
    public DbSet<PaymentMatchingSystemSettings> PaymentMatchingSystemSettings { get; set; }

    // ─── Entity Configuration ─────────────────────────────────────────────────

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Exclude tenant-only entities ───────────────────────────────────────
        // EF Core follows navigation properties and discovers related entities.
        // Client has nav props to Address, Contact, BillingSettings, Invoice, etc.
        // We must explicitly exclude tenant-only entities so they don't appear
        // in master DB migrations.
        // NOTE: Address, Contact, and BillingSettings are NO LONGER ignored — companies (issuers)
        // need these stored in the master DB so SysAdmin can manage them without a tenant DB.
        modelBuilder.Ignore<Invoice>();
        modelBuilder.Ignore<InvoiceTemplate>();
        modelBuilder.Ignore<InvoiceItem>();
        modelBuilder.Ignore<NumberSequence>();

        // Payment matching tenant-only entities.
        modelBuilder.Ignore<BankAccountMailbox>();
        modelBuilder.Ignore<InboundEmail>();
        modelBuilder.Ignore<BankTransaction>();
        modelBuilder.Ignore<PaymentMatch>();
        modelBuilder.Ignore<RecognizedCounterparty>();

        // Invoice email tenant-only entities.
        modelBuilder.Ignore<InvoiceMailbox>();
        modelBuilder.Ignore<InboundInvoiceEmail>();

        ConfigureUser(modelBuilder);
        ConfigureUserPreferences(modelBuilder);
        ConfigureClient(modelBuilder);
        ConfigureAddress(modelBuilder);
        ConfigureContact(modelBuilder);
        ConfigureBankAccount(modelBuilder);
        ConfigureBillingSettings(modelBuilder);
        ConfigureCompanySystemSettings(modelBuilder);
        ConfigureVatRate(modelBuilder);
        ConfigureCurrency(modelBuilder);
        ConfigureNumberSequenceFormat(modelBuilder);
        ConfigureContentTemplate(modelBuilder);
        ConfigureSystemConfiguration(modelBuilder);
        ConfigureAppLog(modelBuilder);
        ConfigureAresCache(modelBuilder);
        ConfigureTaxYearConfig(modelBuilder);

        ConfigureMasterMailboxIndex(modelBuilder);
        ConfigurePaymentMatchingSystemSettings(modelBuilder);

        SeedData(modelBuilder);
    }

    /// <summary>
    /// UserPreferences — per-user UI preferences, 1:1 with User (unique UserId).
    /// Cascade delete: removing a user removes their preferences row.
    /// </summary>
    private void ConfigureUserPreferences(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserPreferences>(entity =>
        {
            entity.HasKey(e => e.Id);

            // One preferences row per user
            entity.HasIndex(e => e.UserId).IsUnique();

            entity.HasOne(e => e.User)
                .WithOne()
                .HasForeignKey<UserPreferences>(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.DefaultGridPageSize).HasDefaultValue(10);
        });
    }

    /// <summary>
    /// MasterMailboxIndex — global lookup used by IMAP worker to resolve alias → tenant schema.
    /// InboundAlias unique ACROSS all tenants (worker reads master DB first).
    /// </summary>
    private void ConfigureMasterMailboxIndex(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MasterMailboxIndex>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Unique only among non-retired aliases — retired ones may share an alias
            // that was later regenerated. Filtered index to allow collisions among retired entries.
            entity.HasIndex(e => e.InboundAlias)
                .IsUnique()
                .HasFilter("\"IsAliasRetired\" = false");

            entity.HasIndex(e => e.TenantSchema);

            entity.Property(e => e.InboundAlias).IsRequired().HasMaxLength(40);
            entity.Property(e => e.TenantSchema).IsRequired().HasMaxLength(100);

            // MailboxType discriminator — determines which processor handles this alias.
            // Default Payment for backward compatibility with existing rows.
            entity.Property(e => e.MailboxType)
                .HasConversion<int>()
                .HasDefaultValue(Domain.Enums.EMailboxType.Payment);

            // Invoice mailbox FK — only populated when MailboxType = Invoice.
            // No DB-level FK constraint (cross-schema reference).
        });
    }

    /// <summary>
    /// PaymentMatchingSystemSettings — single-row configuration owned by SysAdmin.
    /// Password is pre-encrypted before persistence (Data Protection API).
    /// </summary>
    private void ConfigurePaymentMatchingSystemSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentMatchingSystemSettings>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ImapHost).HasMaxLength(255);
            entity.Property(e => e.ImapUsername).HasMaxLength(320);
            entity.Property(e => e.ImapPasswordEncrypted).HasMaxLength(4000);
            entity.Property(e => e.ImapFolder).HasMaxLength(100).HasDefaultValue("INBOX");
            entity.Property(e => e.ProcessedFolder).HasMaxLength(100).HasDefaultValue("Processed");
            entity.Property(e => e.UnroutedFolder).HasMaxLength(100).HasDefaultValue("Unrouted");
            entity.Property(e => e.InboundDomain).HasMaxLength(255).HasDefaultValue("fakvio.cz");
            entity.Property(e => e.PollIntervalMinutes).HasDefaultValue(30);
            entity.Property(e => e.InboundEmailRetentionDays).HasDefaultValue(1825);
            entity.Property(e => e.LastRunStatus).HasMaxLength(500);
        });
    }

    /// <summary>
    /// User table configuration.
    /// </summary>
    private void ConfigureUser(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => e.CompanyId);
            entity.HasIndex(e => e.Role);
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.Email)
                .IsRequired()
                .HasMaxLength(256);

            // PasswordHash is nullable — OAuth users don't have a local password.
            // Local password users always have a hash set during registration or invitation.
            entity.Property(e => e.PasswordHash)
                .IsRequired(false)
                .HasMaxLength(500);

            entity.Property(e => e.FirstName)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.LastName)
                .IsRequired()
                .HasMaxLength(100);

            // PostgreSQL filtered index syntax uses double-quoted identifiers for column names
            entity.HasIndex(e => e.InvitationToken)
                .IsUnique()
                .HasFilter("\"InvitationToken\" IS NOT NULL");

            entity.Property(e => e.InvitationToken)
                .HasMaxLength(100);

            entity.Property(e => e.InvitationTokenExpiresAt)
                .IsRequired(false);

            entity.Property(e => e.IsInvitationPending)
                .HasDefaultValue(false);

            // ── External OAuth provider configuration ────────────────────────
            // ExternalProvider + ExternalProviderId together uniquely identify an OAuth user.
            // The filtered unique index excludes "None" (local password users) to allow
            // multiple users with ExternalProvider = None and null ExternalProviderId.
            entity.Property(e => e.ExternalProvider)
                .HasDefaultValue(EExternalProvider.None);

            entity.Property(e => e.ExternalProviderId)
                .HasMaxLength(500);

            entity.HasIndex(e => new { e.ExternalProvider, e.ExternalProviderId })
                .HasFilter("\"ExternalProvider\" <> 0")
                .IsUnique();

            // ── Email verification configuration ─────────────────────────────
            // Self-registered users must verify their email before logging in.
            // Token is a GUID string, filtered index for fast lookup during verification.
            entity.Property(e => e.EmailVerificationToken)
                .HasMaxLength(100);

            entity.HasIndex(e => e.EmailVerificationToken)
                .HasFilter("\"EmailVerificationToken\" IS NOT NULL");

            entity.HasIndex(e => e.IsEmailVerified);

            // ── Two-Factor Authentication configuration ─────────────────────
            // 2FA fields are stored in the master DB (User table) so the second login
            // step can be validated before any tenant context is established.

            entity.Property(e => e.TwoFactorEnabled)
                .HasDefaultValue(false);

            entity.HasIndex(e => e.TwoFactorEnabled);

            entity.Property(e => e.TwoFactorMethod)
                .HasConversion<int>()
                .HasDefaultValue(ETwoFactorMethod.None);

            entity.Property(e => e.TotpSecretEncrypted)
                .HasMaxLength(500);

            entity.Property(e => e.TwoFactorEmailCode)
                .HasMaxLength(100);

            // Session token — encrypted "userId|timestamp" for linking password step to code step.
            // Filtered index for fast lookup during 2FA verification.
            entity.Property(e => e.TwoFactorSessionToken)
                .HasMaxLength(500);

            entity.HasIndex(e => e.TwoFactorSessionToken)
                .HasFilter("\"TwoFactorSessionToken\" IS NOT NULL");

            entity.Property(e => e.FailedTwoFactorAttempts)
                .HasDefaultValue(0);

            // FK to Client (company) — only IsIssuer = true records are in master DB
            entity.HasOne(e => e.Company)
                .WithMany()
                .HasForeignKey(e => e.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// Client table configuration — in master DB, this holds only companies (IsIssuer = true).
    /// The schema is the same as the tenant Client table, but only company records live here.
    /// </summary>
    private void ConfigureClient(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.RegistrationNumber).IsUnique();
            entity.HasIndex(e => e.IsIssuer);
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.RegistrationNumber)
                .IsRequired()
                .HasMaxLength(20);

            entity.Property(e => e.CompanyName)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.TaxNumber)
                .HasMaxLength(50);

            entity.Property(e => e.TradingName)
                .HasMaxLength(500);

            // ISO 639-1 language code for document generation (e.g., "cs", "en").
            entity.Property(e => e.Language).IsRequired().HasMaxLength(5).HasDefaultValue("cs");

            entity.Property(e => e.Color).HasMaxLength(9);

            // Company addresses and contacts are stored in the master DB
            // so SysAdmin can manage them without needing a tenant database.
            entity.HasMany(e => e.Address)
                .WithOne(a => a.Client)
                .HasForeignKey(a => a.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Contact)
                .WithOne(c => c.Client)
                .HasForeignKey(c => c.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Bank accounts: 1:N with cascade delete — same as Address/Contact pattern.
            entity.HasMany(e => e.BankAccount)
                .WithOne(b => b.Client)
                .HasForeignKey(b => b.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // One-to-one: Client → BillingSettings (bank account, payment method, etc.)
            entity.HasOne(e => e.BillingSettings)
                .WithOne(b => b.Client)
                .HasForeignKey<BillingSettings>(b => b.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Tax regime fields — store enum values as strings for readability.
            // Must match TenantDbContext.ConfigureClient exactly — both contexts
            // share the same Client entity and the column type must be consistent.
            entity.Property(e => e.TaxRegime)
                .HasConversion<string?>()
                .HasMaxLength(30);

            entity.Property(e => e.ActivityType)
                .HasConversion<string?>()
                .HasMaxLength(30);

            entity.Property(e => e.FlatRateBand)
                .HasConversion<string?>()
                .HasMaxLength(10);

            // Currency FK for schema compatibility
            entity.HasOne(e => e.PreferredCurrency)
                .WithMany()
                .HasForeignKey(e => e.PreferredCurrencyId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
        });
    }

    /// <summary>
    /// Address table configuration — same schema as TenantDbContext.
    /// Companies (issuers) store their address in the master DB so SysAdmin
    /// can manage it without a provisioned tenant database.
    /// </summary>
    private void ConfigureAddress(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Address>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => e.AddressType);

            entity.Property(e => e.Street).IsRequired().HasMaxLength(500);
            entity.Property(e => e.City).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PostalCode).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Country).IsRequired().HasMaxLength(100);
            entity.Property(e => e.AddressLine2).HasMaxLength(500);
        });
    }

    /// <summary>
    /// Contact table configuration — same schema as TenantDbContext.
    /// Companies (issuers) store their contacts in the master DB so SysAdmin
    /// can manage them without a provisioned tenant database.
    /// </summary>
    private void ConfigureContact(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Contact>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => e.ContactType);

            entity.Property(e => e.ContactValue).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Label).HasMaxLength(200);
        });
    }

    /// <summary>
    /// BankAccount table configuration — same schema as TenantDbContext.
    /// Companies (issuers) store their bank accounts in the master DB so SysAdmin
    /// can manage them without a provisioned tenant database.
    /// </summary>
    private void ConfigureBankAccount(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => new { e.ClientId, e.IsDefault });

            entity.Property(e => e.Label).HasMaxLength(200);
            entity.Property(e => e.BankName).HasMaxLength(200);
            entity.Property(e => e.AccountNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.IBAN).HasMaxLength(50);
            entity.Property(e => e.SWIFT).HasMaxLength(20);
            entity.Property(e => e.CurrencyCode).HasMaxLength(3);
        });
    }

    /// <summary>
    /// BillingSettings table configuration — same schema as TenantDbContext but WITHOUT
    /// NumberSequence FK relationships (NumberSequence is tenant-only).
    /// Companies (issuers) store billing settings (bank account, payment method, etc.)
    /// in the master DB so SysAdmin can manage them without a tenant database.
    /// The NumberSequence FK columns still exist as plain long? properties but have no
    /// FK constraints in the master DB — they're only enforced in the tenant DB.
    /// </summary>
    private void ConfigureBillingSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BillingSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ClientId).IsUnique();

            entity.Property(e => e.InvoiceNumberPrefix).HasMaxLength(50);
            entity.Property(e => e.InvoiceNumberSuffix).HasMaxLength(50);
            entity.Property(e => e.CreditNoteNumberPrefix).HasMaxLength(50);
            entity.Property(e => e.CreditNoteNumberSuffix).HasMaxLength(50);
            entity.Property(e => e.DefaultPaymentMethod).HasConversion<int?>();
            entity.Property(e => e.BankAccountNumber).HasMaxLength(100);
            entity.Property(e => e.Notes).HasMaxLength(2000);

            // NumberSequence is tenant-only — ignore nav props in master context.
            // The FK columns (CustomInvoiceNumberSequenceId, CustomCreditNoteNumberSequenceId)
            // remain as plain nullable long columns without FK constraints.
            entity.Ignore(e => e.CustomInvoiceNumberSequence);
            entity.Ignore(e => e.CustomCreditNoteNumberSequence);
        });
    }

    /// <summary>
    /// CompanySystemSettings table configuration — one-to-one with Client (company).
    /// </summary>
    private void ConfigureCompanySystemSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CompanySystemSettings>(entity =>
        {
            entity.HasKey(e => e.Id);

            // One-to-one: each company gets exactly one settings record
            entity.HasIndex(e => e.CompanyId).IsUnique();

            entity.HasIndex(e => e.IsProvisioned);
            entity.HasIndex(e => e.IsActive);

            entity.HasOne(e => e.Company)
                .WithOne()
                .HasForeignKey<CompanySystemSettings>(e => e.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(e => e.SchemaName)
                .IsRequired()
                .HasMaxLength(63); // PostgreSQL NAMEDATALEN - 1 limit

            entity.Property(e => e.AdminNotes)
                .HasMaxLength(2000);

            // ── Company-specific SMTP settings (all optional) ────────────────
            // These columns store per-company SMTP configuration.
            // When SmtpHost is non-empty, EmailService uses these instead of
            // the system-wide SMTP from SystemConfiguration.
            entity.Property(e => e.SmtpHost).HasMaxLength(500);
            entity.Property(e => e.SmtpUsername).HasMaxLength(256);
            entity.Property(e => e.SmtpPassword).HasMaxLength(500);
            entity.Property(e => e.SmtpSenderEmail).HasMaxLength(256);
            entity.Property(e => e.SmtpSenderName).HasMaxLength(200);

            // ── Google Drive cloud storage settings (all optional) ────────────
            // OAuth tokens are stored encrypted in production.
            // MaxLength for tokens is generous — Google/MS tokens can be long.
            entity.Property(e => e.GoogleDriveAccessToken).HasMaxLength(2000);
            entity.Property(e => e.GoogleDriveRefreshToken).HasMaxLength(2000);
            entity.Property(e => e.GoogleDriveFolderId).HasMaxLength(500);
            entity.Property(e => e.GoogleDriveFolderName).HasMaxLength(500);

            // ── OneDrive cloud storage settings (all optional) ────────────────
            entity.Property(e => e.OneDriveAccessToken).HasMaxLength(2000);
            entity.Property(e => e.OneDriveRefreshToken).HasMaxLength(2000);
            entity.Property(e => e.OneDriveFolderId).HasMaxLength(500);
            entity.Property(e => e.OneDriveFolderName).HasMaxLength(500);

            // ── Azure Blob Storage settings (all optional) ──────────────────
            // Connection string encrypted at rest via ICredentialProtector.
            entity.Property(e => e.AzureBlobConnectionString).HasMaxLength(2000);
            entity.Property(e => e.AzureBlobContainerPrefix).HasMaxLength(100);
            entity.Property(e => e.AzureBlobContainerName).HasMaxLength(63);
        });
    }

    /// <summary>
    /// VatRate table configuration.
    /// </summary>
    private void ConfigureVatRate(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VatRate>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.IsDefault);
            entity.HasIndex(e => e.ValidFrom);
            entity.HasIndex(e => e.ValidTo);
            entity.HasIndex(e => new { e.IsDefault, e.IsReduced });

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(e => e.Rate)
                .HasPrecision(5, 2);
        });
    }

    /// <summary>
    /// Currency table configuration.
    /// </summary>
    private void ConfigureCurrency(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Currency>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.Code).IsUnique();
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.SortOrder);

            entity.Property(e => e.Code)
                .IsRequired()
                .HasMaxLength(3);

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.Symbol)
                .IsRequired()
                .HasMaxLength(10);

            entity.Property(e => e.DisplayFormat)
                .HasMaxLength(50);
        });
    }

    /// <summary>
    /// NumberSequenceFormat table configuration.
    /// </summary>
    private void ConfigureNumberSequenceFormat(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NumberSequenceFormat>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(e => e.FormatPattern)
                .IsRequired()
                .HasMaxLength(100);
        });
    }

    /// <summary>
    /// ContentTemplate table configuration.
    /// </summary>
    private void ConfigureContentTemplate(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContentTemplate>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.TemplateType);
            entity.HasIndex(e => e.IsDefault);
            entity.HasIndex(e => e.IsActive);
            // Composite index updated to include Language — supports "one default per (type, language)" queries.
            entity.HasIndex(e => new { e.TemplateType, e.Language, e.IsDefault });

            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(e => e.Subject)
                .HasMaxLength(500);

            // PostgreSQL uses "text" type by default for string properties without MaxLength.
            entity.Property(e => e.HtmlBody)
                .IsRequired();

            entity.Property(e => e.Description)
                .HasMaxLength(2000);

            // ISO 639-1 language code — determines which language this template is written in.
            entity.Property(e => e.Language).IsRequired().HasMaxLength(5).HasDefaultValue("cs");
        });
    }

    /// <summary>
    /// AppLog table configuration — lightweight log entries.
    /// NOT derived from BaseEntity, so no audit trail columns.
    /// Indexes on Level and Timestamp for efficient filtering.
    /// </summary>
    private void ConfigureAppLog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppLog>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Index on Level for filtering (e.g., "show only Error logs")
            entity.HasIndex(e => e.Level);
            // Index on Timestamp for date range queries and cleanup job
            entity.HasIndex(e => e.Timestamp);
            // Composite index for the most common query: filter by level + time range
            entity.HasIndex(e => new { e.Level, e.Timestamp });

            entity.Property(e => e.Level)
                .IsRequired()
                .HasMaxLength(20);

            entity.Property(e => e.Source)
                .IsRequired()
                .HasMaxLength(500);

            // Message uses text — no length limit (default for string in PostgreSQL)
            entity.Property(e => e.Message)
                .IsRequired();

            // Exception also text — can be very long for nested exceptions
            entity.Property(e => e.RequestPath)
                .HasMaxLength(500);

            // CorrelationId — GUID string (36 chars) that links all log entries from a single request.
            // Indexed for efficient grouping/filtering: "show me all logs for this request."
            entity.Property(e => e.CorrelationId)
                .HasMaxLength(36);

            // Index for fast lookup — the primary use case is filtering/grouping logs by CorrelationId
            // to trace a complete request flow across services.
            entity.HasIndex(e => e.CorrelationId);
        });
    }

    /// <summary>
    /// SystemConfiguration table configuration — single-row settings table.
    /// Contains SMTP and JWT settings managed by SysAdmin.
    /// No FK relationships — this is a standalone configuration table.
    /// </summary>
    private void ConfigureSystemConfiguration(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SystemConfiguration>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.SmtpHost)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.SmtpUsername)
                .HasMaxLength(500);

            entity.Property(e => e.SmtpPassword)
                .HasMaxLength(500);

            entity.Property(e => e.SmtpSenderEmail)
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.SmtpSenderName)
                .IsRequired()
                .HasMaxLength(200);

            // ── Azure Blob Storage settings (all optional) ──────────────────
            // System-wide blob storage config. Connection string encrypted at rest.
            entity.Property(e => e.AzureBlobConnectionString).HasMaxLength(2000);
            entity.Property(e => e.AzureBlobContainerPrefix).HasMaxLength(100);
            entity.Property(e => e.AzureBlobContainerName).HasMaxLength(63);
        });
    }

    /// <summary>
    /// AresCache table configuration — cached ARES (Czech business registry) lookups.
    /// Same schema as TenantDbContext.ConfigureAresCache — shared ARES cache across all contexts.
    /// </summary>
    private void ConfigureAresCache(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AresCache>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RegistrationNumber).IsUnique();
            entity.HasIndex(e => e.ExpiresAt);

            entity.Property(e => e.RegistrationNumber).IsRequired().HasMaxLength(20);
            entity.Property(e => e.JsonData).IsRequired();
            entity.Property(e => e.CompanyName).HasMaxLength(500);
            entity.Property(e => e.TaxNumber).HasMaxLength(50);
            entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
        });
    }

    /// <summary>
    /// TaxYearConfig table configuration — annual tax rates per country.
    /// Unique index on (Year, Country) ensures one config per year/country.
    /// </summary>
    private void ConfigureTaxYearConfig(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaxYearConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Year, e.Country }).IsUnique();

            entity.Property(e => e.Country).IsRequired().HasMaxLength(5);
            entity.Property(e => e.CurrencyCode).IsRequired().HasMaxLength(5);

            entity.Property(e => e.AverageMonthlyWage).HasPrecision(18, 2);
            entity.Property(e => e.LivingMinimum).HasPrecision(18, 2);
            entity.Property(e => e.IncomeTaxRate).HasPrecision(5, 2);
            entity.Property(e => e.ProgressiveTaxRate).HasPrecision(5, 2);
            entity.Property(e => e.ProgressiveThreshold).HasPrecision(18, 2);
            entity.Property(e => e.BasicTaxpayerCredit).HasPrecision(18, 2);
            entity.Property(e => e.SocialInsuranceRate).HasPrecision(5, 2);
            entity.Property(e => e.SocialAssessmentBasePercent).HasPrecision(5, 2);
            entity.Property(e => e.MinMonthlySocialMain).HasPrecision(18, 2);
            entity.Property(e => e.MinMonthlySocialSecondary).HasPrecision(18, 2);
            entity.Property(e => e.MaxSocialAssessmentBase).HasPrecision(18, 2);
            entity.Property(e => e.HealthInsuranceRate).HasPrecision(5, 2);
            entity.Property(e => e.HealthAssessmentBasePercent).HasPrecision(5, 2);
            entity.Property(e => e.MinMonthlyHealthMain).HasPrecision(18, 2);
            entity.Property(e => e.MaxHealthAssessmentBase).HasPrecision(18, 2);
            entity.Property(e => e.FlatRateBand1Monthly).HasPrecision(18, 2);
            entity.Property(e => e.FlatRateBand2Monthly).HasPrecision(18, 2);
            entity.Property(e => e.FlatRateBand3Monthly).HasPrecision(18, 2);
            entity.Property(e => e.LumpSum80Cap).HasPrecision(18, 2);
            entity.Property(e => e.LumpSum60Cap).HasPrecision(18, 2);
            entity.Property(e => e.LumpSum40Cap).HasPrecision(18, 2);
            entity.Property(e => e.LumpSum30Cap).HasPrecision(18, 2);
        });
    }

    /// <summary>
    /// Seeds initial data: SysAdmin user, default code tables (VatRates, Currencies, etc.)
    /// </summary>
    private void SeedData(ModelBuilder modelBuilder)
    {
        var seedDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Seed default SysAdmin user — pre-verified, local password login
        modelBuilder.Entity<User>().HasData(
            new User
            {
                Id = 1,
                Email = "admin@zcloud.cz",
                PasswordHash = "$2a$12$aI/Mx3cUBwheuL1U1laUee1OLR92DaWxdu3SLMauc5zWy7VoVwEAu", // Invoice123
                FirstName = "System",
                LastName = "Administrator",
                Role = EUserRole.SysAdmin,
                CompanyId = null,
                IsActive = true,
                IsEmailVerified = true, // SysAdmin is pre-verified
                ExternalProvider = EExternalProvider.None,
                CreatedAt = seedDate
            }
        );

        // Seed VAT rates (Czech DPH)
        modelBuilder.Entity<VatRate>().HasData(
            new VatRate { Id = 1, Name = "DPH 21% - standardní sazba", Rate = 21.00m, ValidFrom = new DateTime(2013, 1, 1, 0, 0, 0, DateTimeKind.Utc), IsReduced = false, IsDefault = true, IsActive = true, CreatedAt = seedDate },
            new VatRate { Id = 2, Name = "DPH 12% - snížená sazba", Rate = 12.00m, ValidFrom = new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc), IsReduced = true, IsDefault = true, IsActive = true, CreatedAt = seedDate },
            new VatRate { Id = 3, Name = "DPH 0% - osvobozeno od daně", Rate = 0.00m, ValidFrom = new DateTime(2013, 1, 1, 0, 0, 0, DateTimeKind.Utc), IsReduced = false, IsDefault = false, IsActive = true, CreatedAt = seedDate }
        );

        // Seed currencies
        modelBuilder.Entity<Currency>().HasData(
            new Currency { Id = 1, Code = "CZK", Name = "Česká koruna", Symbol = "Kč", DecimalPlaces = 2, IsActive = true, SortOrder = 1, DisplayFormat = "{0:N2} Kč", CreatedAt = seedDate },
            new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "€", DecimalPlaces = 2, IsActive = true, SortOrder = 2, DisplayFormat = "€{0:N2}", CreatedAt = seedDate },
            new Currency { Id = 3, Code = "USD", Name = "US Dollar", Symbol = "$", DecimalPlaces = 2, IsActive = true, SortOrder = 3, DisplayFormat = "${0:N2}", CreatedAt = seedDate },
            new Currency { Id = 4, Code = "GBP", Name = "British Pound", Symbol = "£", DecimalPlaces = 2, IsActive = true, SortOrder = 4, DisplayFormat = "£{0:N2}", CreatedAt = seedDate },
            new Currency { Id = 5, Code = "PLN", Name = "Polish Złoty", Symbol = "zł", DecimalPlaces = 2, IsActive = true, SortOrder = 5, DisplayFormat = "{0:N2} zł", CreatedAt = seedDate },
            new Currency { Id = 6, Code = "CHF", Name = "Swiss Franc", Symbol = "CHF", DecimalPlaces = 2, IsActive = true, SortOrder = 6, DisplayFormat = "CHF {0:N2}", CreatedAt = seedDate },
            new Currency { Id = 7, Code = "HUF", Name = "Hungarian Forint", Symbol = "Ft", DecimalPlaces = 0, IsActive = true, SortOrder = 7, DisplayFormat = "{0:N0} Ft", CreatedAt = seedDate },
            new Currency { Id = 8, Code = "RON", Name = "Romanian Leu", Symbol = "lei", DecimalPlaces = 2, IsActive = true, SortOrder = 8, DisplayFormat = "{0:N2} lei", CreatedAt = seedDate }
        );

        // Seed number sequence formats
        modelBuilder.Entity<NumberSequenceFormat>().HasData(
            new NumberSequenceFormat { Id = 1, Name = "Standard yearly format (yyyyNNN)", FormatPattern = "yyyyNNN", CounterDigits = 3, ResetsYearly = true, ResetsMonthly = false, IsActive = true, CreatedAt = seedDate },
            new NumberSequenceFormat { Id = 2, Name = "Short yearly format (yyNNN)", FormatPattern = "yyNNN", CounterDigits = 3, ResetsYearly = true, ResetsMonthly = false, IsActive = true, CreatedAt = seedDate },
            new NumberSequenceFormat { Id = 3, Name = "Monthly format (yyMMNNN)", FormatPattern = "yyMMNNN", CounterDigits = 3, ResetsYearly = true, ResetsMonthly = true, IsActive = true, CreatedAt = seedDate },
            new NumberSequenceFormat { Id = 4, Name = "Continuous format (NNNNNN)", FormatPattern = "NNNNNN", CounterDigits = 6, ResetsYearly = false, ResetsMonthly = false, IsActive = true, CreatedAt = seedDate }
        );

        // Seed TaxYearConfig — annual tax rates for CZ and SK.
        // These are real-world rates for 2025 and 2026 used to calculate tax obligations.
        modelBuilder.Entity<TaxYearConfig>().HasData(
            // ── Czech Republic 2025 ──────────────────────────────────────────
            new TaxYearConfig
            {
                Id = 1, Year = 2025, Country = "CZ", CurrencyCode = "CZK",
                AverageMonthlyWage = 43_967m, LivingMinimum = 4_860m,
                IncomeTaxRate = 15m, ProgressiveTaxRate = 23m,
                ProgressiveThreshold = 36 * 43_967m, // 36x average wage
                BasicTaxpayerCredit = 30_840m,
                SocialInsuranceRate = 29.2m, SocialAssessmentBasePercent = 50m,
                MinMonthlySocialMain = 3_852m, MinMonthlySocialSecondary = 0m,
                MaxSocialAssessmentBase = 48 * 43_967m * 12, // 48x annual average wage
                HealthInsuranceRate = 13.5m, HealthAssessmentBasePercent = 50m,
                MinMonthlyHealthMain = 2_968m, MaxHealthAssessmentBase = 0m, // No cap
                FlatRateBand1Monthly = 7_498m, FlatRateBand2Monthly = 16_000m, FlatRateBand3Monthly = 26_000m,
                LumpSum80Cap = 1_600_000m, LumpSum60Cap = 1_200_000m,
                LumpSum40Cap = 800_000m, LumpSum30Cap = 600_000m
            },
            // ── Czech Republic 2026 ──────────────────────────────────────────
            new TaxYearConfig
            {
                Id = 2, Year = 2026, Country = "CZ", CurrencyCode = "CZK",
                AverageMonthlyWage = 45_617m, LivingMinimum = 4_860m,
                IncomeTaxRate = 15m, ProgressiveTaxRate = 23m,
                ProgressiveThreshold = 36 * 45_617m,
                BasicTaxpayerCredit = 30_840m,
                SocialInsuranceRate = 29.2m, SocialAssessmentBasePercent = 50m,
                MinMonthlySocialMain = 4_096m, MinMonthlySocialSecondary = 0m,
                MaxSocialAssessmentBase = 48 * 45_617m * 12,
                HealthInsuranceRate = 13.5m, HealthAssessmentBasePercent = 50m,
                MinMonthlyHealthMain = 3_079m, MaxHealthAssessmentBase = 0m,
                FlatRateBand1Monthly = 8_716m, FlatRateBand2Monthly = 16_000m, FlatRateBand3Monthly = 26_000m,
                LumpSum80Cap = 1_600_000m, LumpSum60Cap = 1_200_000m,
                LumpSum40Cap = 800_000m, LumpSum30Cap = 600_000m
            },
            // ── Slovakia 2025 ────────────────────────────────────────────────
            new TaxYearConfig
            {
                Id = 3, Year = 2025, Country = "SK", CurrencyCode = "EUR",
                AverageMonthlyWage = 1_430m, LivingMinimum = 268.88m,
                IncomeTaxRate = 15m, ProgressiveTaxRate = 25m,
                ProgressiveThreshold = 176.8m * 268.88m, // 176.8x living minimum
                BasicTaxpayerCredit = 21 * 268.88m, // 21x living minimum (nezdaniteľná časť)
                SocialInsuranceRate = 33.15m, SocialAssessmentBasePercent = 50m,
                MinMonthlySocialMain = 216.13m, MinMonthlySocialSecondary = 0m,
                MaxSocialAssessmentBase = 7 * 1_430m * 12, // 7x average annual wage
                HealthInsuranceRate = 14m, HealthAssessmentBasePercent = 50m,
                MinMonthlyHealthMain = 97.80m, MaxHealthAssessmentBase = 0m,
                FlatRateBand1Monthly = 0m, FlatRateBand2Monthly = 0m, FlatRateBand3Monthly = 0m, // Not applicable in SK
                LumpSum80Cap = 0m, // SK uses only 60% lump sum
                LumpSum60Cap = 20_000m, LumpSum40Cap = 0m, LumpSum30Cap = 0m
            },
            // ── Slovakia 2026 ────────────────────────────────────────────────
            new TaxYearConfig
            {
                Id = 4, Year = 2026, Country = "SK", CurrencyCode = "EUR",
                AverageMonthlyWage = 1_500m, LivingMinimum = 273.99m,
                IncomeTaxRate = 15m, ProgressiveTaxRate = 25m,
                ProgressiveThreshold = 176.8m * 273.99m,
                BasicTaxpayerCredit = 21 * 273.99m,
                SocialInsuranceRate = 33.15m, SocialAssessmentBasePercent = 50m,
                MinMonthlySocialMain = 225m, MinMonthlySocialSecondary = 0m,
                MaxSocialAssessmentBase = 7 * 1_500m * 12,
                HealthInsuranceRate = 14m, HealthAssessmentBasePercent = 50m,
                MinMonthlyHealthMain = 105m, MaxHealthAssessmentBase = 0m,
                FlatRateBand1Monthly = 0m, FlatRateBand2Monthly = 0m, FlatRateBand3Monthly = 0m,
                LumpSum80Cap = 0m, LumpSum60Cap = 20_000m, LumpSum40Cap = 0m, LumpSum30Cap = 0m
            }
        );

        // Seed content templates (PDF + email) — all defaults are in Czech ("cs") language.
        modelBuilder.Entity<ContentTemplate>().HasData(
            new ContentTemplate { Id = 1, Name = "Default Invoice PDF", Subject = null, HtmlBody = DefaultSeedData.GetDefaultInvoicePdfTemplate(), TemplateType = EContentTemplateType.InvoicePdf, IsDefault = true, IsActive = true, Language = "cs", Description = "Default HTML template for rendering invoice PDFs.", CreatedAt = seedDate },
            new ContentTemplate { Id = 2, Name = "Default Credit Note PDF", Subject = null, HtmlBody = DefaultSeedData.GetDefaultCreditNotePdfTemplate(), TemplateType = EContentTemplateType.CreditNotePdf, IsDefault = true, IsActive = true, Language = "cs", Description = "Default HTML template for rendering credit note PDFs.", CreatedAt = seedDate },
            new ContentTemplate { Id = 3, Name = "Default Invoice Email", Subject = "Invoice {{InvoiceNumber}} from {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Invoice {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached invoice <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><br/><p>Thank you for your business.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.InvoiceEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email body when sending an invoice.", CreatedAt = seedDate },
            new ContentTemplate { Id = 4, Name = "Default Credit Note Email", Subject = "Credit Note {{InvoiceNumber}} from {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Credit Note {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached credit note <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Thank you for your business.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.CreditNoteEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email body when sending a credit note.", CreatedAt = seedDate },
            new ContentTemplate { Id = 5, Name = "Default Invitation Email", Subject = "Invitation to {{AppName}} — Set your password", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Welcome to {{AppName}}</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>You have been invited to {{AppName}}. Please set your password by clicking the button below:</p><div style=""text-align: center; margin: 30px 0;""><a href=""{{InvitationLink}}"" style=""background-color: #1976D2; color: white; padding: 14px 28px; text-decoration: none; border-radius: 4px; font-size: 16px;"">Set Password</a></div><p style=""color: #666; font-size: 14px;"">This link is valid for 48 hours.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{AppName}}</p></div>", TemplateType = EContentTemplateType.InvitationEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email sent to new users.", CreatedAt = seedDate },
            new ContentTemplate { Id = 6, Name = "Default Payment Reminder", Subject = "Payment reminder — Invoice {{InvoiceNumber}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #E65100;"">Payment Reminder</h2><p>Dear customer,</p><p>This is a friendly reminder that invoice <strong>{{InvoiceNumber}}</strong> is overdue.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><p>Please arrange payment at your earliest convenience.</p><br/><p>Thank you.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.ReminderEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Payment reminder for overdue invoices.", CreatedAt = seedDate },
            new ContentTemplate { Id = 7, Name = "Default Two-Factor Email", Subject = "Your verification code — {{AppName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Verification Code</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>Your two-factor authentication code is:</p><div style=""text-align: center; margin: 30px 0;""><span style=""background-color: #f5f5f5; padding: 16px 32px; font-size: 32px; font-weight: bold; letter-spacing: 8px; border-radius: 8px; border: 2px solid #1976D2;"">{{Code}}</span></div><p style=""color: #666; font-size: 14px;"">This code is valid for <strong>{{ExpirationMinutes}} minutes</strong>.</p><p style=""color: #666; font-size: 14px;"">If you did not request this code, please ignore this email.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{AppName}}</p></div>", TemplateType = EContentTemplateType.TwoFactorEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email sent with a 6-digit OTP code for Two-Factor Authentication.", CreatedAt = seedDate },
            // ── Proforma + TaxReceiptForAdvance templates (issue #27) ──
            new ContentTemplate { Id = 8, Name = "Default Advance Invoice PDF", Subject = null, HtmlBody = DefaultSeedData.GetDefaultAdvanceInvoicePdfTemplate(), TemplateType = EContentTemplateType.AdvanceInvoicePdf, IsDefault = true, IsActive = true, Language = "cs", Description = "HTML šablona pro PDF zálohové faktury (není daňový doklad). Placeholdery stejné jako InvoicePdf.", CreatedAt = seedDate },
            new ContentTemplate { Id = 9, Name = "Default Tax Receipt for Advance PDF", Subject = null, HtmlBody = DefaultSeedData.GetDefaultTaxReceiptForAdvancePdfTemplate(), TemplateType = EContentTemplateType.TaxReceiptForAdvancePdf, IsDefault = true, IsActive = true, Language = "cs", Description = "HTML šablona pro PDF daňového dokladu o přijaté platbě. Tento doklad JE daňovým dokladem a zakládá povinnost DPH.", CreatedAt = seedDate },
            new ContentTemplate { Id = 10, Name = "Default Advance Invoice Email", Subject = "Zálohová faktura {{InvoiceNumber}} od {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #3D7A4A;"">Zálohová faktura {{InvoiceNumber}}</h2><p>Vážený zákazníku,</p><p>v příloze naleznete zálohovou fakturu <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Částka k úhradě:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Datum splatnosti:</strong> {{DueDate}}</p><br/><p>Upozorňujeme, že zálohová faktura není daňovým dokladem. Daňový doklad Vám bude vystaven po přijetí platby.</p><p>Děkujeme.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.AdvanceInvoiceEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email odesílaný spolu se zálohovou fakturou. Upozorňuje příjemce, že zálohovka není daňový doklad.", CreatedAt = seedDate },
            new ContentTemplate { Id = 11, Name = "Default Tax Receipt for Advance Email", Subject = "Daňový doklad o přijaté platbě {{InvoiceNumber}} od {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #6A3D9A;"">Daňový doklad o přijaté platbě {{InvoiceNumber}}</h2><p>Vážený zákazníku,</p><p>v příloze naleznete daňový doklad o přijaté platbě <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Uhrazená částka:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Děkujeme za platbu.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.TaxReceiptForAdvanceEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email odesílaný spolu s daňovým dokladem o přijaté platbě.", CreatedAt = seedDate }
        );
    }

    // ─── Audit Trail ──────────────────────────────────────────────────────────

    public override int SaveChanges()
    {
        UpdateTimestamps();
        NormalizeDateTimesToUtc();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        NormalizeDateTimesToUtc();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Automatically sets timestamps and audit user IDs on every save operation.
    /// </summary>
    private void UpdateTimestamps()
    {
        var currentUserId = _currentUserService?.GetCurrentUserId();

        var entries = ChangeTracker.Entries()
            .Where(e => e.Entity is Domain.Common.BaseEntity &&
                       (e.State == EntityState.Added || e.State == EntityState.Modified));

        foreach (var entry in entries)
        {
            var entity = (Domain.Common.BaseEntity)entry.Entity;

            if (entry.State == EntityState.Added)
            {
                entity.CreatedAt = DateTime.UtcNow;
                if (currentUserId.HasValue)
                {
                    entity.CreatedByUserId = currentUserId.Value;
                }
            }

            entity.UpdatedAt = DateTime.UtcNow;
            if (currentUserId.HasValue)
            {
                entity.UpdatedByUserId = currentUserId.Value;
            }
        }
    }

    /// <summary>
    /// Npgsql 10.x requires DateTime values with DateTimeKind.Utc for "timestamp with time zone" columns.
    /// Blazor date pickers send DateTimeKind.Unspecified — this method normalizes all DateTime properties
    /// on added/modified entities to UTC before they reach the database driver.
    /// </summary>
    private void NormalizeDateTimesToUtc()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is DateTime dt && dt.Kind != DateTimeKind.Utc)
                {
                    // Treat Unspecified/Local as UTC — the app already uses UtcNow everywhere,
                    // and Blazor WASM date pickers produce Unspecified values that are logically UTC.
                    property.CurrentValue = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }
            }
        }
    }
}
