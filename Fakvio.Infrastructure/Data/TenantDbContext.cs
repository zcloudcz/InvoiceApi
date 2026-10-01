using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Database context for individual TENANT schemas in the multi-tenant PostgreSQL architecture.
///
/// Architecture: Single PostgreSQL database with schema-per-tenant isolation.
/// Each company (tenant) gets its own schema (e.g., "tenant_42") within the shared database.
/// The tenant schema is self-contained — it has its own copy of all business data
/// AND its own copies of code tables (VatRate, Currency, NumberSequenceFormat, ContentTemplate).
///
/// Why self-contained? So that:
/// 1. Tenant queries never need to cross schemas (performance)
/// 2. Tenants can customize code table values (e.g., add custom VAT rates)
/// 3. Schema isolation prevents data leakage between tenants
///
/// What is NOT in tenant schema:
/// - Users (stored in master "public" schema — authentication is centralized)
/// - CompanySystemSettings (stored in master schema — infrastructure config)
///
/// Schema is resolved per-request by ITenantDbContextFactory,
/// which reads CompanySystemSettings.SchemaName from the master schema.
/// </summary>
public class TenantDbContext : DbContext
{
    /// <summary>
    /// Service that provides the currently authenticated user's ID.
    /// Used to auto-fill CreatedByUserId and UpdatedByUserId audit fields.
    /// Nullable because during migrations/seeding/provisioning, no user context exists.
    /// </summary>
    private readonly ICurrentUserService? _currentUserService;

    public TenantDbContext(DbContextOptions<TenantDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Constructor that accepts ICurrentUserService for audit trail support.
    /// </summary>
    public TenantDbContext(
        DbContextOptions<TenantDbContext> options,
        ICurrentUserService currentUserService)
        : base(options)
    {
        _currentUserService = currentUserService;
    }

    /// <summary>
    /// PostgreSQL schema name for this tenant context (e.g., "tenant_42").
    /// Set by ITenantDbContextFactory per-request based on CompanySystemSettings.SchemaName.
    /// When set, all tables in this context are created/queried within this schema.
    /// When null, the default schema ("public" or design-time template) is used.
    /// </summary>
    public string? Schema { get; set; }

    // ─── DbSets ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Clients and Issuers (both stored in same table, distinguished by IsIssuer flag).
    /// The issuer (IsIssuer = true) is the company that owns this tenant DB.
    /// Customers (IsIssuer = false) are the companies this tenant bills.
    /// </summary>
    public DbSet<Client> Client { get; set; }

    /// <summary>
    /// Client addresses (billing, shipping, etc.)
    /// </summary>
    public DbSet<Address> Address { get; set; }

    /// <summary>
    /// Client contact information (emails, phones, etc.)
    /// </summary>
    public DbSet<Contact> Contact { get; set; }

    /// <summary>
    /// Client-specific billing settings (payment terms, number sequences, bank accounts)
    /// </summary>
    public DbSet<BillingSettings> BillingSettings { get; set; }

    /// <summary>
    /// Invoices and credit notes (distinguished by DocumentType enum)
    /// Also includes InvoiceTemplates via TPH (Table-Per-Hierarchy)
    /// </summary>
    public DbSet<Invoice> Invoice { get; set; }

    /// <summary>
    /// Invoice templates — stored in same table as Invoice via TPH discriminator
    /// </summary>
    public DbSet<InvoiceTemplate> InvoiceTemplate { get; set; }

    /// <summary>
    /// Invoice line items (products/services on an invoice)
    /// </summary>
    public DbSet<InvoiceItem> InvoiceItem { get; set; }

    /// <summary>
    /// Number sequence formats — tenant's own copy (initially seeded from master)
    /// </summary>
    public DbSet<NumberSequenceFormat> NumberSequenceFormat { get; set; }

    /// <summary>
    /// Number sequences — tenant's counters for generating document numbers
    /// </summary>
    public DbSet<NumberSequence> NumberSequence { get; set; }

    /// <summary>
    /// Currencies — tenant's own copy (initially seeded from master)
    /// </summary>
    public DbSet<Currency> Currency { get; set; }

    /// <summary>
    /// VAT rates — tenant's own copy (initially seeded from master)
    /// </summary>
    public DbSet<VatRate> VatRate { get; set; }

    /// <summary>
    /// Content templates (PDF + email) — tenant's own copy (initially seeded from master)
    /// </summary>
    public DbSet<ContentTemplate> ContentTemplate { get; set; }

    /// <summary>
    /// Client bank accounts (1:N per client).
    /// Issuers list their payment destination accounts here; one is marked as default.
    /// </summary>
    public DbSet<BankAccount> BankAccount { get; set; }

    /// <summary>
    /// AI chat conversations — one per user session.
    /// Contains the conversation metadata (title, last message time).
    /// </summary>
    public DbSet<ChatConversation> ChatConversation { get; set; }

    /// <summary>
    /// AI chat messages — individual messages within a conversation.
    /// Includes user messages, assistant responses, and system prompts.
    /// </summary>
    public DbSet<ChatMessage> ChatMessage { get; set; }

    /// <summary>
    /// Received (incoming) invoices from suppliers — expense tracking.
    /// </summary>
    public DbSet<ReceivedInvoice> ReceivedInvoice { get; set; }

    /// <summary>
    /// Line items on received invoices.
    /// </summary>
    public DbSet<ReceivedInvoiceItem> ReceivedInvoiceItem { get; set; }

    /// <summary>
    /// Cached ARES lookups (company registry data)
    /// </summary>
    public DbSet<AresCache> AresCache { get; set; }

    /// <summary>
    /// File attachments — entity-agnostic file metadata.
    /// Actual file bytes are stored in Azure Blob Storage; this table holds metadata + blob path.
    /// Polymorphic FK via (EntityName, RecordId) — can attach files to any entity type.
    /// </summary>
    public DbSet<FileAttachment> FileAttachment { get; set; }

    // ─── Reminder (Dunning) ──────────────────────────────────────────────────

    /// <summary>
    /// Reminder settings — company-level defaults and per-client overrides.
    /// </summary>
    public DbSet<ReminderSettings> ReminderSettings { get; set; }

    /// <summary>
    /// Escalation level definitions for reminder settings.
    /// </summary>
    public DbSet<ReminderLevel> ReminderLevel { get; set; }

    /// <summary>
    /// Individual reminder records for overdue invoices.
    /// </summary>
    public DbSet<Reminder> Reminder { get; set; }

    /// <summary>
    /// Recurring invoice schedules — define automatic invoice generation from a template
    /// at a configurable cadence (Weekly/Monthly/Quarterly/Yearly).
    /// The Application-layer IRecurringInvoiceService fires all active schedules
    /// whose NextRunAt &lt;= UtcNow and advances the timestamp by one period.
    /// </summary>
    public DbSet<RecurringInvoiceSchedule> RecurringInvoiceSchedule { get; set; }

    // ─── Alerts ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Business alerts requiring user attention (e.g., overpaid proforma).
    /// Generic container — new alert types are added via EAlertType without schema changes.
    /// </summary>
    public DbSet<Alert> Alert { get; set; }

    // ─── Notifications ───────────────────────────────────────────────────────

    /// <summary>
    /// In-app notifications (e.g., "payment matched"). One row per business event.
    /// Per-user read state is tracked via <see cref="NotificationRecipient"/>.
    /// </summary>
    public DbSet<Notification> Notification { get; set; }

    /// <summary>
    /// Per-user read/unread state for each notification.
    /// </summary>
    public DbSet<NotificationRecipient> NotificationRecipient { get; set; }

    // ─── Invoice Email ─────────────────────────────────────────────────────

    /// <summary>
    /// Per-tenant invoice email mailbox. One per company — alias prefix "fak-".
    /// </summary>
    public DbSet<InvoiceMailbox> InvoiceMailbox { get; set; }

    /// <summary>
    /// Archive of inbound invoice emails with processing status and result links.
    /// </summary>
    public DbSet<InboundInvoiceEmail> InboundInvoiceEmail { get; set; }

    /// <summary>
    /// Reverse charge codes from MFČR číselník (kódy předmětu plnění PDP).
    /// Used in VAT control statement (kontrolní hlášení / EPO XML) sections A.1 and B.1.
    /// Each tenant has its own copy (seeded at provisioning) — allows per-tenant customisation
    /// if MFČR publishes an update before the application is patched.
    /// </summary>
    public DbSet<ReverseChargeCode> ReverseChargeCode { get; set; }

    // ─── Payment Matching (see PLATBY-ZADANI.md) ────────────────────────────

    /// <summary>
    /// Inbound email aliases linked to bank accounts (one per account).
    /// Permanent rows — never deleted, only deactivated.
    /// </summary>
    public DbSet<BankAccountMailbox> BankAccountMailbox { get; set; }

    /// <summary>
    /// Archive of every inbound email we actually processed.
    /// Kept for audit, reparse, and forensic review.
    /// </summary>
    public DbSet<InboundEmail> InboundEmail { get; set; }

    /// <summary>
    /// Parsed bank transactions (credit or debit) — the normalized shape we match against invoices.
    /// </summary>
    public DbSet<BankTransaction> BankTransaction { get; set; }

    /// <summary>
    /// Many-to-many links between BankTransaction and Invoice/ReceivedInvoice.
    /// </summary>
    public DbSet<PaymentMatch> PaymentMatch { get; set; }

    /// <summary>
    /// Registry of known counterparty accounts (insurance, tax office, …) used to
    /// recognize recurring payments that have no invoice.
    /// </summary>
    public DbSet<RecognizedCounterparty> RecognizedCounterparty { get; set; }

    // ─── Entity Configuration ─────────────────────────────────────────────────

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Schema isolation ──────────────────────────────────────────────────
        // Each tenant gets its own PostgreSQL schema (e.g., "tenant_42").
        // HasDefaultSchema ensures all tables, indexes, and sequences for this
        // context are created within the tenant's schema, preventing data leakage.
        if (!string.IsNullOrEmpty(Schema))
        {
            modelBuilder.HasDefaultSchema(Schema);
        }

        // ── Exclude master-only entities ───────────────────────────────────────
        // Tenant schemas don't have Users or CompanySystemSettings.
        // These entities don't have nav props pointing INTO tenant data,
        // but we exclude them explicitly for clarity and safety.
        modelBuilder.Ignore<User>();
        modelBuilder.Ignore<CompanySystemSettings>();
        modelBuilder.Ignore<SystemConfiguration>(); // Master DB only — global system settings
        modelBuilder.Ignore<AppLog>();               // Master DB only — application logs
        modelBuilder.Ignore<MasterMailboxIndex>();   // Master DB only — global alias → tenant lookup
        modelBuilder.Ignore<PaymentMatchingSystemSettings>(); // Master DB only — SysAdmin config

        ConfigureClient(modelBuilder);
        ConfigureAddress(modelBuilder);
        ConfigureContact(modelBuilder);
        ConfigureBankAccount(modelBuilder);
        ConfigureBillingSettings(modelBuilder);
        ConfigureCurrency(modelBuilder);
        ConfigureInvoice(modelBuilder);
        ConfigureInvoiceItem(modelBuilder);
        ConfigureNumberSequenceFormat(modelBuilder);
        ConfigureNumberSequence(modelBuilder);
        ConfigureAresCache(modelBuilder);
        ConfigureVatRate(modelBuilder);
        ConfigureContentTemplate(modelBuilder);
        ConfigureReceivedInvoice(modelBuilder);
        ConfigureReceivedInvoiceItem(modelBuilder);
        ConfigureChatConversation(modelBuilder);
        ConfigureChatMessage(modelBuilder);
        ConfigureFileAttachment(modelBuilder);
        ConfigureReminderSettings(modelBuilder);
        ConfigureReminderLevel(modelBuilder);
        ConfigureReminder(modelBuilder);

        ConfigureRecurringInvoiceSchedule(modelBuilder);

        ConfigureAlert(modelBuilder);

        ConfigureNotification(modelBuilder);
        ConfigureNotificationRecipient(modelBuilder);

        ConfigureInvoiceMailbox(modelBuilder);
        ConfigureInboundInvoiceEmail(modelBuilder);

        ConfigureBankAccountMailbox(modelBuilder);
        ConfigureInboundEmail(modelBuilder);
        ConfigureBankTransaction(modelBuilder);
        ConfigurePaymentMatch(modelBuilder);
        ConfigureRecognizedCounterparty(modelBuilder);

        ConfigureReverseChargeCode(modelBuilder);

        SeedData(modelBuilder);
    }

    // ─── Entity Configuration Methods ─────────────────────────────────────────

    private void ConfigureClient(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RegistrationNumber).IsUnique();
            entity.HasIndex(e => e.IsIssuer);
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.RegistrationNumber).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CompanyName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.TaxNumber).HasMaxLength(50);
            entity.Property(e => e.TradingName).HasMaxLength(500);

            // ISO 639-1 language code for document generation (e.g., "cs", "en").
            entity.Property(e => e.Language).IsRequired().HasMaxLength(5).HasDefaultValue("cs");

            // CSS hex color for row highlighting in grids (e.g., "#FF5722").
            entity.Property(e => e.Color).HasMaxLength(9);

            // Peppol endpoint ID override (ADR 0002, F1.8) — "<scheme>:<value>", e.g. "0245:2020123456".
            entity.Property(e => e.PeppolId).HasMaxLength(64);

            entity.HasMany(e => e.Address)
                .WithOne(a => a.Client)
                .HasForeignKey(a => a.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Contact)
                .WithOne(c => c.Client)
                .HasForeignKey(c => c.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            // Bank accounts: 1:N relationship with cascade delete.
            // Safe to cascade — BankAccount has no outgoing FKs that could create multiple cascade paths.
            entity.HasMany(e => e.BankAccount)
                .WithOne(b => b.Client)
                .HasForeignKey(b => b.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.BillingSettings)
                .WithOne(b => b.Client)
                .HasForeignKey<BillingSettings>(b => b.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.PreferredCurrency)
                .WithMany()
                .HasForeignKey(e => e.PreferredCurrencyId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            // Tax regime fields — store enum values as strings for readability.
            entity.Property(e => e.TaxRegime)
                .HasConversion<string?>()
                .HasMaxLength(30);

            entity.Property(e => e.ActivityType)
                .HasConversion<string?>()
                .HasMaxLength(30);

            entity.Property(e => e.FlatRateBand)
                .HasConversion<string?>()
                .HasMaxLength(10);
        });
    }

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
    /// BankAccount table configuration — 1:N per client.
    /// Stores bank account details (account number, IBAN, SWIFT, currency).
    /// One account per client can be marked as IsDefault.
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

            // Prevent cascading deletes from creating ambiguous paths.
            // Client → BillingSettings is Cascade, so these FKs to NumberSequence
            // use NoAction for safety. Application code handles cleanup.
            entity.HasOne(e => e.CustomInvoiceNumberSequence)
                .WithMany()
                .HasForeignKey(e => e.CustomInvoiceNumberSequenceId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.CustomCreditNoteNumberSequence)
                .WithMany()
                .HasForeignKey(e => e.CustomCreditNoteNumberSequenceId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }

    private void ConfigureCurrency(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Currency>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Code).IsUnique();
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.SortOrder);

            entity.Property(e => e.Code).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Symbol).IsRequired().HasMaxLength(10);
            entity.Property(e => e.DisplayFormat).HasMaxLength(50);
        });
    }

    private void ConfigureInvoice(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasDiscriminator<string>("InvoiceType")
                .HasValue<Invoice>("Invoice")
                .HasValue<InvoiceTemplate>("Template");

            // Unique filtered index: prevents duplicate document numbers.
            // Excludes "DRAFT" placeholder (new invoices start as "DRAFT" and get a real number on creation).
            // Multiple DRAFTs are allowed, but once a real number is assigned it must be unique.
            // PostgreSQL filtered index syntax uses double-quoted identifiers
            // Unique document number — excludes NULLs, "DRAFT" placeholders, and soft-deleted invoices.
            // Deleted invoices (Status=5) must NOT block new invoices from reusing the same number.
            // PostgreSQL filtered index syntax uses double-quoted identifiers.
            entity.HasIndex(e => e.DocumentNumber)
                .IsUnique()
                .HasFilter("\"DocumentNumber\" IS NOT NULL AND \"DocumentNumber\" <> 'DRAFT' AND \"Status\" <> 5");
            entity.HasIndex(e => e.DocumentType);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => e.IssuerId);
            entity.HasIndex(e => e.IssueDate);
            entity.HasIndex(e => e.DueDate);

            entity.Property(e => e.DocumentNumber).HasMaxLength(100);
            entity.HasOne(e => e.Currency).WithMany().HasForeignKey(e => e.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(e => e.TotalBeforeVat).HasPrecision(18, 2);
            entity.Property(e => e.TotalVat).HasPrecision(18, 2);
            entity.Property(e => e.TotalWithVat).HasPrecision(18, 2);
            // PaidAmount: denormalized sum of PaymentMatch rows — kept in sync by PaymentMatchingService.
            entity.Property(e => e.PaidAmount).HasPrecision(18, 2).HasDefaultValue(0m);
            entity.Property(e => e.VariableSymbol).HasMaxLength(50);
            entity.Property(e => e.ConstantSymbol).HasMaxLength(50);
            entity.Property(e => e.SpecificSymbol).HasMaxLength(50);
            entity.Property(e => e.BankAccountNumber).HasMaxLength(100);
            entity.Property(e => e.IBAN).HasMaxLength(50);
            entity.Property(e => e.SWIFT).HasMaxLength(50);
            entity.Property(e => e.PaymentMethod).HasConversion<int?>();
            entity.Property(e => e.Notes).HasMaxLength(5000);

            entity.HasOne(e => e.Client).WithMany().HasForeignKey(e => e.ClientId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            entity.HasOne(e => e.Issuer).WithMany().HasForeignKey(e => e.IssuerId).OnDelete(DeleteBehavior.Restrict).IsRequired(true);
            entity.HasOne(e => e.OriginalInvoice).WithMany(i => i.CreditNote).HasForeignKey(e => e.OriginalInvoiceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(e => e.InvoiceItem).WithOne(i => i.Invoice).HasForeignKey(i => i.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InvoiceTemplate>(entity =>
        {
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.HasOne(e => e.NumberSequence).WithMany().HasForeignKey(e => e.NumberSequenceId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.IsActive);
        });
    }

    private void ConfigureInvoiceItem(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InvoiceItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.InvoiceId);

            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Unit).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Quantity).HasPrecision(18, 4);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);
            entity.Property(e => e.VatRatePercentage).HasPrecision(5, 2);
            entity.Property(e => e.TotalBeforeVat).HasPrecision(18, 2);
            entity.Property(e => e.VatAmount).HasPrecision(18, 2);
            entity.Property(e => e.TotalWithVat).HasPrecision(18, 2);
            entity.Property(e => e.InformationalVatAmount).HasPrecision(18, 2);
            entity.Property(e => e.ProductCode).HasMaxLength(100);
            entity.Property(e => e.Notes).HasMaxLength(1000);

            // VatRegime: stored as integer, NOT NULL, default 0 (Standard).
            // HasConversion<int>() tells EF Core to map the enum to its underlying integer value.
            // HasDefaultValue ensures existing rows (before migration) default to Standard without
            // requiring a value in the UPDATE statement — no data migration needed.
            entity.Property(e => e.VatRegime)
                .HasConversion<int>()
                .HasDefaultValue(EVatRegime.Standard)
                .IsRequired();

            entity.HasOne(e => e.VatRate).WithMany(v => v.InvoiceItems).HasForeignKey(e => e.VatRateId).OnDelete(DeleteBehavior.Restrict);

            // ReverseChargeCode is a reference data lookup (číselník).
            // On delete: Restrict — we must not let MFČR code rows be deleted if any invoice
            // item references them. Historical invoices must remain intact.
            entity.HasOne(e => e.ReverseChargeCode)
                .WithMany()
                .HasForeignKey(e => e.ReverseChargeCodeId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private void ConfigureNumberSequenceFormat(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NumberSequenceFormat>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.IsActive);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.FormatPattern).IsRequired().HasMaxLength(100);
        });
    }

    private void ConfigureNumberSequence(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NumberSequence>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DocumentType, e.IsDefault });
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Prefix).HasMaxLength(50);
            entity.Property(e => e.Suffix).HasMaxLength(50);

            // Optimistic concurrency — PostgreSQL xmin system column.
            // xmin is a hidden column containing the transaction ID that last modified the row.
            // In Npgsql 10.x, a uint property configured as IsConcurrencyToken() + ValueGeneratedOnAddOrUpdate()
            // is automatically mapped to the xmin column by convention.
            // On every UPDATE, EF Core checks this value — if another request modified the row
            // since we read it, SaveChanges throws DbUpdateConcurrencyException.
            entity.Property(e => e.RowVersion)
                .IsConcurrencyToken()
                .ValueGeneratedOnAddOrUpdate();

            entity.HasOne(e => e.NumberSequenceFormat)
                .WithMany()
                .HasForeignKey(e => e.NumberSequenceFormatId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

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

            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Rate).HasPrecision(5, 2);
        });
    }

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

            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Subject).HasMaxLength(500);
            // PostgreSQL uses "text" type by default for string properties without MaxLength.
            entity.Property(e => e.HtmlBody).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(2000);

            // ISO 639-1 language code — determines which language this template is written in.
            entity.Property(e => e.Language).IsRequired().HasMaxLength(5).HasDefaultValue("cs");
        });
    }

    /// <summary>
    /// ReceivedInvoice table configuration — incoming invoices from suppliers (expenses).
    /// Follows same patterns as Invoice configuration for consistency.
    /// </summary>
    private void ConfigureReceivedInvoice(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReceivedInvoice>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.SupplierId);
            entity.HasIndex(e => e.IssueDate);
            entity.HasIndex(e => e.DueDate);
            entity.HasIndex(e => e.ReceivedDate);
            entity.HasIndex(e => e.TaxableSupplyDate);

            entity.Property(e => e.DocumentNumber).HasMaxLength(100);
            entity.Property(e => e.VariableSymbol).HasMaxLength(50);
            entity.Property(e => e.BankAccountNumber).HasMaxLength(100);
            entity.Property(e => e.IBAN).HasMaxLength(50);
            entity.Property(e => e.SWIFT).HasMaxLength(50);
            entity.Property(e => e.PaymentMethod).HasConversion<int?>();
            entity.Property(e => e.Notes).HasMaxLength(5000);
            entity.Property(e => e.AttachmentFileName).HasMaxLength(500);
            entity.Property(e => e.AttachmentContentType).HasMaxLength(100);

            entity.Property(e => e.TotalBeforeVat).HasPrecision(18, 2);
            entity.Property(e => e.TotalVat).HasPrecision(18, 2);
            entity.Property(e => e.TotalWithVat).HasPrecision(18, 2);

            // Supplier is a Client record — Restrict to prevent accidental deletion of supplier with invoices.
            entity.HasOne(e => e.Supplier)
                .WithMany()
                .HasForeignKey(e => e.SupplierId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Currency)
                .WithMany()
                .HasForeignKey(e => e.CurrencyId)
                .OnDelete(DeleteBehavior.Restrict);

            // Cascade delete items when received invoice is removed
            entity.HasMany(e => e.Items)
                .WithOne(i => i.ReceivedInvoice)
                .HasForeignKey(i => i.ReceivedInvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// ReceivedInvoiceItem table configuration — line items on received invoices.
    /// Mirrors InvoiceItem configuration for consistency.
    /// </summary>
    private void ConfigureReceivedInvoiceItem(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReceivedInvoiceItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ReceivedInvoiceId);

            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Unit).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Quantity).HasPrecision(18, 4);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);
            entity.Property(e => e.VatRatePercentage).HasPrecision(5, 2);
            entity.Property(e => e.TotalBeforeVat).HasPrecision(18, 2);
            entity.Property(e => e.VatAmount).HasPrecision(18, 2);
            entity.Property(e => e.TotalWithVat).HasPrecision(18, 2);
            entity.Property(e => e.ProductCode).HasMaxLength(100);
            entity.Property(e => e.Notes).HasMaxLength(1000);

            entity.HasOne(e => e.VatRate)
                .WithMany()
                .HasForeignKey(e => e.VatRateId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// ChatConversation table configuration.
    /// Conversations are per-user, ordered by last message time.
    /// </summary>
    private void ConfigureChatConversation(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatConversation>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Index for listing a user's conversations sorted by recency.
            entity.HasIndex(e => new { e.UserId, e.LastMessageAt });
            entity.HasIndex(e => e.IsArchived);

            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);

            // Cascade delete: removing a conversation removes all its messages.
            entity.HasMany(e => e.Messages)
                .WithOne(m => m.Conversation)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// ChatMessage table configuration.
    /// Messages belong to a conversation and are ordered by CreatedAt.
    /// </summary>
    private void ConfigureChatMessage(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ConversationId);

            // Store the enum as int in the database.
            entity.Property(e => e.Role).HasConversion<int>();
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.ProviderUsed).HasMaxLength(50);
        });
    }

    /// <summary>
    /// Configures the FileAttachment entity — entity-agnostic file metadata.
    /// Uses (EntityName, RecordId) as a composite index for fast lookups,
    /// and a unique index on FileGuid to prevent duplicate blob references.
    /// No FK constraint on RecordId because it can reference any entity type.
    /// </summary>
    private void ConfigureFileAttachment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileAttachment>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Composite index — fast lookup for "all files attached to Invoice #42"
            entity.HasIndex(e => new { e.EntityName, e.RecordId });

            // Unique index — prevents duplicate blob references
            entity.HasIndex(e => e.FileGuid).IsUnique();

            entity.Property(e => e.EntityName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.OriginalFileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(200);
            entity.Property(e => e.BlobPath).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Description).HasMaxLength(500);
        });
    }

    /// <summary>
    /// Configures ReminderSettings entity — company-level defaults and per-client overrides.
    /// ClientId = null means company default; non-null = per-client override.
    /// </summary>
    private void ConfigureReminderSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReminderSettings>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Filtered unique index — at most one override per client.
            // Company default (ClientId = null) is not covered by this index.
            entity.HasIndex(e => e.ClientId)
                .IsUnique()
                .HasFilter("\"ClientId\" IS NOT NULL");

            entity.Property(e => e.MaxReminderLevel).HasDefaultValue(3);
            entity.Property(e => e.GracePeriodDays).HasDefaultValue(7);
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.AttachInvoicePdf).HasDefaultValue(true);
            entity.Property(e => e.AutoSendEmail).HasDefaultValue(true);

            // 1:N relationship with escalation levels — cascade delete removes levels when settings are deleted.
            entity.HasMany(e => e.Levels)
                .WithOne(l => l.ReminderSettings)
                .HasForeignKey(l => l.ReminderSettingsId)
                .OnDelete(DeleteBehavior.Cascade);

            // Optional relationship to Client — restrict delete to prevent orphaned settings.
            entity.HasOne(e => e.Client)
                .WithMany()
                .HasForeignKey(e => e.ClientId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
        });
    }

    /// <summary>
    /// Configures ReminderLevel entity — escalation step definitions.
    /// Unique constraint on (ReminderSettingsId, Level) prevents duplicate levels.
    /// </summary>
    private void ConfigureReminderLevel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReminderLevel>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Composite unique — one level number per settings record.
            entity.HasIndex(e => new { e.ReminderSettingsId, e.Level }).IsUnique();

            entity.Property(e => e.DaysAfterPrevious).HasDefaultValue(7);
            entity.Property(e => e.Subject).HasMaxLength(500);
            entity.Property(e => e.FixedFeeCzk).HasPrecision(18, 2).HasDefaultValue(0m);

            // Optional FK to email template.
            entity.HasOne(e => e.EmailTemplate)
                .WithMany()
                .HasForeignKey(e => e.EmailTemplateId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            // Optional FK to PDF template.
            entity.HasOne(e => e.PdfTemplate)
                .WithMany()
                .HasForeignKey(e => e.PdfTemplateId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });
    }

    /// <summary>
    /// Configures Reminder entity — individual dunning records per invoice.
    /// Unique constraint on (InvoiceId, Level) ensures one reminder per escalation level per invoice.
    /// Indexes on ClientId, Status, and ReminderDate support common query patterns.
    /// </summary>
    private void ConfigureReminder(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reminder>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Composite unique — one reminder per level per invoice.
            entity.HasIndex(e => new { e.InvoiceId, e.Level }).IsUnique();

            // Fast lookup by client (dashboard: "all reminders for client X").
            entity.HasIndex(e => e.ClientId);

            // Filter by status (list: "show all Draft reminders").
            entity.HasIndex(e => e.Status);

            // Sort/filter by date (list: "reminders in date range").
            entity.HasIndex(e => e.ReminderDate);

            entity.Property(e => e.InvoiceAmount).HasPrecision(18, 2);
            entity.Property(e => e.FeeCzk).HasPrecision(18, 2);
            entity.Property(e => e.InterestCzk).HasPrecision(18, 2);
            entity.Property(e => e.TotalCzk).HasPrecision(18, 2);
            entity.Property(e => e.SentToEmail).HasMaxLength(500);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.Notes).HasMaxLength(2000);

            // Status stored as string for readability in DB.
            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(20);

            // FK to Invoice — restrict delete (can't delete invoice with reminders).
            entity.HasOne(e => e.Invoice)
                .WithMany()
                .HasForeignKey(e => e.InvoiceId)
                .OnDelete(DeleteBehavior.Restrict);

            // FK to Client — restrict delete (can't delete client with reminders).
            entity.HasOne(e => e.Client)
                .WithMany()
                .HasForeignKey(e => e.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    // ─── Recurring Invoices ───────────────────────────────────────────────────

    /// <summary>
    /// RecurringInvoiceSchedule — one row per active or paused schedule.
    ///
    /// Key design decisions:
    /// - FK to InvoiceTemplate (Restrict): you cannot delete a template that has an active schedule.
    ///   Deactivate the schedule first, then optionally archive the template.
    /// - FK to Client (Restrict): same reasoning — do not lose scheduling state when a client
    ///   is being reorganised. Deactivate the schedule first.
    /// - RowVersion uses the xmin concurrency pattern (Npgsql 10.x convention):
    ///   IsConcurrencyToken() + ValueGeneratedOnAddOrUpdate() maps to the xmin system column.
    /// - DayOfWeek is stored as int (PostgreSQL integer) to avoid enum name mismatch across
    ///   DB versions. The application enum System.DayOfWeek already starts at 0 (Sunday).
    /// - Frequency is stored as int for forward-compatibility (adding new values is additive).
    /// - NextRunAt / LastRunAt / EndDate use DateTimeOffset to preserve UTC everywhere.
    /// </summary>
    private void ConfigureRecurringInvoiceSchedule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecurringInvoiceSchedule>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Index for the job query: SELECT * WHERE IsActive = true AND NextRunAt <= @now
            // This is the hot path — runs every timer tick.
            entity.HasIndex(e => new { e.IsActive, e.NextRunAt });

            // Support listing schedules per template or per client in the UI.
            entity.HasIndex(e => e.TemplateId);
            entity.HasIndex(e => e.ClientId);

            // Frequency stored as int — forwards-compatible, avoids enum/string mismatch.
            entity.Property(e => e.Frequency).HasConversion<int>();

            // DayOfWeek stored as int — System.DayOfWeek values (0=Sunday..6=Saturday).
            // Nullable because it is only relevant when Frequency = Weekly.
            entity.Property(e => e.DayOfWeek).HasConversion<int?>();

            // Default values mirrored from the entity property defaults so that
            // explicit HasDefaultValue here is only needed where EF Core cannot infer it
            // from a CLR-default initialiser on the entity (i.e., non-zero / non-false).
            entity.Property(e => e.IntervalCount).HasDefaultValue(1);
            entity.Property(e => e.OccurrenceCount).HasDefaultValue(0);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
            entity.Property(e => e.AutoSend).HasDefaultValue(false);

            // LastError: free-form text, capped at 2000 chars (matches Reminder.ErrorMessage).
            entity.Property(e => e.LastError).HasMaxLength(2000);

            // Optimistic concurrency via PostgreSQL xmin system column.
            // Npgsql 10.x convention: uint property + IsConcurrencyToken() + ValueGeneratedOnAddOrUpdate()
            // maps automatically to xmin. EF Core checks this on every UPDATE.
            entity.Property(e => e.RowVersion)
                .IsConcurrencyToken()
                .ValueGeneratedOnAddOrUpdate();

            // FK to InvoiceTemplate — Restrict: cannot delete a template in use by a schedule.
            // InvoiceTemplate is a TPH row in the Invoice table; FK targets the Invoice PK.
            entity.HasOne(e => e.Template)
                .WithMany()
                .HasForeignKey(e => e.TemplateId)
                .OnDelete(DeleteBehavior.Restrict);

            // FK to Client — Restrict: cannot delete a client in use by a schedule.
            entity.HasOne(e => e.Client)
                .WithMany()
                .HasForeignKey(e => e.ClientId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    // ─── Alert configuration ──────────────────────────────────────────────────

    /// <summary>
    /// Alert table configuration.
    /// Indexes support the two main query patterns:
    ///   1. Dashboard: "all unresolved alerts for this tenant, newest first"
    ///   2. Idempotency check: "open alert of type X for entity Y"
    /// Type is stored as int for forward-compatibility (adding new values is additive).
    /// </summary>
    private void ConfigureAlert(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Alert>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Dashboard query: open alerts sorted by creation date.
            entity.HasIndex(e => e.ResolvedAt);

            // Idempotency check: one open alert per (type, entity) combination.
            entity.HasIndex(e => new { e.Type, e.RelatedEntityId, e.RelatedEntityType });

            // Type stored as int — forwards-compatible (adding new EAlertType values is additive).
            entity.Property(e => e.Type).HasConversion<int>();

            entity.Property(e => e.RelatedEntityType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(2000);
        });
    }

    // ─── Payment Matching configuration ──────────────────────────────────────

    /// <summary>
    /// BankAccountMailbox — one alias per bank account.
    /// Permanent row, never deleted. Alias is unique within the tenant schema.
    /// </summary>
    private void ConfigureBankAccountMailbox(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankAccountMailbox>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Unique per tenant — the worker also validates against MasterMailboxIndex globally.
            entity.HasIndex(e => e.InboundAlias).IsUnique();

            // Exactly one mailbox per bank account.
            entity.HasIndex(e => e.BankAccountId).IsUnique();

            // Filter index for "active mailboxes" scans.
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.InboundAlias).IsRequired().HasMaxLength(40);

            // FK to BankAccount — Restrict: you cannot delete a bank account that has a mailbox.
            // Deactivate the mailbox first.
            entity.HasOne(e => e.BankAccount)
                .WithMany()
                .HasForeignKey(e => e.BankAccountId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>
    /// InboundEmail — raw archive of processed emails.
    /// Keeps text + HTML bodies (up to ~1 MB each) for audit and reparse.
    /// </summary>
    private void ConfigureInboundEmail(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboundEmail>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Dedupe — unique per mailbox. Worker uses this to ignore duplicate delivery.
            entity.HasIndex(e => new { e.BankAccountMailboxId, e.DeduplicationHash }).IsUnique();

            // Filter for "NeedsReview / Failed" queues shown in UI.
            entity.HasIndex(e => e.ParseStatus);

            // List ordering.
            entity.HasIndex(e => e.ServerReceivedAt);

            entity.Property(e => e.MessageId).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ImapUid).HasMaxLength(50);
            entity.Property(e => e.FromAddress).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FromDisplayName).HasMaxLength(500);
            entity.Property(e => e.ToAddress).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Subject).HasMaxLength(1000);
            // Bodies are left unbounded (PostgreSQL text type) — truncation to 1 MB happens in service code.
            entity.Property(e => e.DeduplicationHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ParseError).HasMaxLength(4000);

            // FK to BankAccountMailbox — Restrict: we never delete mailboxes, so this is defensive.
            entity.HasOne(e => e.BankAccountMailbox)
                .WithMany()
                .HasForeignKey(e => e.BankAccountMailboxId)
                .OnDelete(DeleteBehavior.Restrict);

            // FK to BankTransaction — SetNull: transaction can be deleted without losing the email archive.
            entity.HasOne(e => e.BankTransaction)
                .WithMany()
                .HasForeignKey(e => e.BankTransactionId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });
    }

    /// <summary>
    /// BankTransaction — one normalized row per parsed transaction.
    /// Unique per (BankAccountId, DeduplicationHash) for idempotent ingest.
    /// </summary>
    private void ConfigureBankTransaction(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankTransaction>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Idempotency — same transaction re-ingested will violate this index.
            entity.HasIndex(e => new { e.BankAccountId, e.DeduplicationHash }).IsUnique();

            // List query: "all tx on this account ordered newest first".
            entity.HasIndex(e => new { e.BankAccountId, e.TransactionDate });

            // Dashboard counter: "how many unmatched payments".
            entity.HasIndex(e => e.MatchStatus);

            entity.Property(e => e.DeduplicationHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.CurrencyCode).IsRequired().HasMaxLength(3);
            entity.Property(e => e.VariableSymbol).HasMaxLength(20);
            entity.Property(e => e.ConstantSymbol).HasMaxLength(20);
            entity.Property(e => e.SpecificSymbol).HasMaxLength(20);
            entity.Property(e => e.CounterpartyAccount).HasMaxLength(100);
            entity.Property(e => e.CounterpartyName).HasMaxLength(500);
            entity.Property(e => e.Message).HasMaxLength(2000);
            entity.Property(e => e.TransactionCode).HasMaxLength(50);
            entity.Property(e => e.ParserConfidence).HasPrecision(4, 3);
            entity.Property(e => e.ParserModel).HasMaxLength(100);
            // RawPayload unbounded (PostgreSQL text).
            entity.Property(e => e.Direction).HasConversion<int>();
            entity.Property(e => e.ImportSource).HasConversion<int>();
            entity.Property(e => e.MatchStatus).HasConversion<int>();

            // FK to BankAccount — Restrict to prevent accidental loss of accounting history.
            entity.HasOne(e => e.BankAccount)
                .WithMany()
                .HasForeignKey(e => e.BankAccountId)
                .OnDelete(DeleteBehavior.Restrict);

            // FK to RecognizedCounterparty — SetNull: deleting a registry entry
            // must not delete accounting history; the service also resets MatchStatus.
            entity.HasIndex(e => e.RecognizedCounterpartyId);
            entity.HasOne(e => e.RecognizedCounterparty)
                .WithMany()
                .HasForeignKey(e => e.RecognizedCounterpartyId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });
    }

    /// <summary>
    /// RecognizedCounterparty — tenant registry of known counterparty accounts
    /// (insurance, tax office, …). Standalone reference data, no outgoing FKs.
    /// </summary>
    private void ConfigureRecognizedCounterparty(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RecognizedCounterparty>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Matcher hot path: WHERE IsActive = true (table is tiny, index cheap).
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.Label).IsRequired().HasMaxLength(200);
            // Account is optional since card-payment support — service validation
            // requires at least one of (CounterpartyAccount, CounterpartyNamePattern).
            entity.Property(e => e.CounterpartyAccount).HasMaxLength(100);
            entity.Property(e => e.CounterpartyNamePattern).HasMaxLength(200);
            entity.Property(e => e.VariableSymbol).HasMaxLength(20);
            entity.Property(e => e.SpecificSymbol).HasMaxLength(20);
            entity.Property(e => e.ConstantSymbol).HasMaxLength(20);
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.Property(e => e.Category).HasConversion<int>();
        });
    }

    /// <summary>
    /// PaymentMatch — many-to-many link rows between BankTransaction and invoices.
    /// Composite FK constraint: exactly one of (InvoiceId, ReceivedInvoiceId) must be non-null.
    /// </summary>
    private void ConfigurePaymentMatch(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentMatch>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Fast sum for Invoice.PaidAmount recalc.
            entity.HasIndex(e => e.InvoiceId);
            entity.HasIndex(e => e.ReceivedInvoiceId);
            entity.HasIndex(e => e.BankTransactionId);

            entity.Property(e => e.MatchedAmount).HasPrecision(18, 2);
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.Property(e => e.MatchedBy).HasConversion<int>();

            // FK to BankTransaction — Cascade: if we delete a transaction, drop its matches too.
            entity.HasOne(e => e.BankTransaction)
                .WithMany(t => t.PaymentMatch)
                .HasForeignKey(e => e.BankTransactionId)
                .OnDelete(DeleteBehavior.Cascade);

            // FK to Invoice — Restrict: cannot delete an invoice with payments attached.
            entity.HasOne(e => e.Invoice)
                .WithMany(i => i.PaymentMatch)
                .HasForeignKey(e => e.InvoiceId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            // FK to ReceivedInvoice — Restrict: same reasoning.
            entity.HasOne(e => e.ReceivedInvoice)
                .WithMany()
                .HasForeignKey(e => e.ReceivedInvoiceId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);

            // Database-level check: at least one of (InvoiceId, ReceivedInvoiceId) must be non-null.
            // Enforced via raw SQL — EF Core has no fluent API for CHECK constraints.
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_PaymentMatch_Target",
                "\"InvoiceId\" IS NOT NULL OR \"ReceivedInvoiceId\" IS NOT NULL"));
        });
    }

    /// <summary>
    /// Configures the ReverseChargeCode entity.
    ///
    /// Key design decisions:
    /// - Unique index on Code: MFČR codes are unique strings ("1", "1a", "3", etc.)
    /// - Index on IsActive: supports the GetAllActiveAsync hot path (WHERE IsActive = true)
    /// - ValidFrom/ValidTo are DateOnly — legislative validity is date-only, no time component
    /// - No FK relationships: code table is standalone reference data
    /// </summary>
    private void ConfigureReverseChargeCode(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReverseChargeCode>(entity =>
        {
            entity.HasKey(e => e.Id);

            // Unique constraint: each MFČR code string must appear at most once.
            entity.HasIndex(e => e.Code).IsUnique();

            // Index for the GetAllActiveAsync query (WHERE IsActive = true AND ValidFrom <= today ...).
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.Code).IsRequired().HasMaxLength(8);
            entity.Property(e => e.NameCs).IsRequired().HasMaxLength(500);
            entity.Property(e => e.NameEn).HasMaxLength(500);
            entity.Property(e => e.ParagraphRef).IsRequired().HasMaxLength(10);
        });
    }

    // ─── Notification configuration ─────────────────────────────────────────

    /// <summary>
    /// Notification table configuration.
    /// Indexes support:
    ///   1. Navigation: "find notification for entity X" via (RelatedEntityId, RelatedEntityType)
    ///   2. Listing: newest first via CreatedAt descending
    /// Type is stored as int for forward-compatibility.
    /// </summary>
    private void ConfigureNotification(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.RelatedEntityId, e.RelatedEntityType });
            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Message).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.RelatedEntityType).IsRequired().HasMaxLength(100);
        });
    }

    /// <summary>
    /// NotificationRecipient table configuration.
    /// Unique constraint on (NotificationId, UserId) prevents duplicate recipients.
    /// Composite index on (UserId, ReadAt) supports the "unread count for user X" fast path.
    /// </summary>
    private void ConfigureNotificationRecipient(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationRecipient>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.NotificationId, e.UserId }).IsUnique();
            entity.HasIndex(e => new { e.UserId, e.ReadAt });

            entity.HasOne(e => e.Notification)
                .WithMany(n => n.Recipients)
                .HasForeignKey(e => e.NotificationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    // ─── Invoice Email configuration ────────────────────────────────────

    /// <summary>
    /// InvoiceMailbox — one per tenant, "fak-" prefix alias for receiving invoices by email.
    /// </summary>
    private void ConfigureInvoiceMailbox(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InvoiceMailbox>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.InboundAlias).IsUnique();
            entity.HasIndex(e => e.IsActive);

            entity.Property(e => e.InboundAlias).IsRequired().HasMaxLength(40);
        });
    }

    /// <summary>
    /// InboundInvoiceEmail — archive of invoice emails with processing status.
    /// </summary>
    private void ConfigureInboundInvoiceEmail(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboundInvoiceEmail>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.InvoiceMailboxId, e.DeduplicationHash }).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ServerReceivedAt);

            entity.Property(e => e.MessageId).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ImapUid).HasMaxLength(50);
            entity.Property(e => e.FromAddress).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FromDisplayName).HasMaxLength(500);
            entity.Property(e => e.ToAddress).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Subject).HasMaxLength(1000);
            entity.Property(e => e.DeduplicationHash).IsRequired().HasMaxLength(64);
            entity.Property(e => e.StatusError).HasMaxLength(2000);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Direction).HasConversion<int?>();
            entity.Property(e => e.ClassificationConfidence).HasPrecision(4, 3);

            entity.HasOne(e => e.InvoiceMailbox)
                .WithMany()
                .HasForeignKey(e => e.InvoiceMailboxId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.ReceivedInvoice)
                .WithMany()
                .HasForeignKey(e => e.ReceivedInvoiceId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            entity.HasOne(e => e.Invoice)
                .WithMany()
                .HasForeignKey(e => e.InvoiceId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
        });
    }

    /// <summary>
    /// Seeds default data for new tenant databases.
    /// Code tables (VatRate, Currency, NumberSequenceFormat, ContentTemplate)
    /// are seeded here as fallback — the TenantProvisioningService copies
    /// current master data, which overrides these seed values.
    /// Number sequences are seeded with default invoice + credit note counters.
    /// </summary>
    private void SeedData(ModelBuilder modelBuilder)
    {
        var seedDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Seed default number sequence formats
        modelBuilder.Entity<NumberSequenceFormat>().HasData(
            new NumberSequenceFormat { Id = 1, Name = "Standard yearly format (yyyyNNN)", FormatPattern = "yyyyNNN", CounterDigits = 3, ResetsYearly = true, ResetsMonthly = false, IsActive = true, CreatedAt = seedDate },
            new NumberSequenceFormat { Id = 2, Name = "Short yearly format (yyNNN)", FormatPattern = "yyNNN", CounterDigits = 3, ResetsYearly = true, ResetsMonthly = false, IsActive = true, CreatedAt = seedDate },
            new NumberSequenceFormat { Id = 3, Name = "Monthly format (yyMMNNN)", FormatPattern = "yyMMNNN", CounterDigits = 3, ResetsYearly = true, ResetsMonthly = true, IsActive = true, CreatedAt = seedDate },
            new NumberSequenceFormat { Id = 4, Name = "Continuous format (NNNNNN)", FormatPattern = "NNNNNN", CounterDigits = 6, ResetsYearly = false, ResetsMonthly = false, IsActive = true, CreatedAt = seedDate }
        );

        // Seed default number sequences — one per document type that generates a document number.
        // Proforma and TaxReceiptForAdvance get their own counters so the numbering series
        // stays separate from standard invoices and credit notes.
        //
        // Prefix conventions used in this seed (Czech standards):
        //   INV   = faktura-daňový doklad   (historical, no trailing dash — kept for backward compat)
        //   CN    = dobropis                (historical, no trailing dash — kept for backward compat)
        //   PF-   = proforma / zálohová faktura
        //   DPP-  = daňový doklad o přijaté platbě (tax receipt for advance)
        //
        // Note: INV/CN without a dash are the original values from InitTenant. Unifying them to
        // INV-/CN- is tracked as a separate issue and is intentionally out of scope for #26.
        modelBuilder.Entity<NumberSequence>().HasData(
            new NumberSequence { Id = 1, Name = "Default Invoice Sequence", DocumentType = EDocumentType.Invoice, Prefix = "INV", CurrentNumber = 0, IsDefault = true, NumberSequenceFormatId = 1, IsActive = true, CreatedAt = seedDate },
            new NumberSequence { Id = 2, Name = "Default Credit Note Sequence", DocumentType = EDocumentType.CreditNote, Prefix = "CN", CurrentNumber = 0, IsDefault = true, NumberSequenceFormatId = 1, IsActive = true, CreatedAt = seedDate },
            new NumberSequence { Id = 3, Name = "Default Proforma Sequence", DocumentType = EDocumentType.Proforma, Prefix = "PF-", CurrentNumber = 0, IsDefault = true, NumberSequenceFormatId = 1, IsActive = true, CreatedAt = seedDate },
            new NumberSequence { Id = 4, Name = "Default Tax Receipt for Advance Sequence", DocumentType = EDocumentType.TaxReceiptForAdvance, Prefix = "DPP-", CurrentNumber = 0, IsDefault = true, NumberSequenceFormatId = 1, IsActive = true, CreatedAt = seedDate }
        );

        // Seed VAT rates
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

        // Seed reverse charge codes (kódy předmětu plnění PDP) from MFČR číselník.
        //
        // Source: Příloha č. 6 zákona č. 235/2004 Sb. o dani z přidané hodnoty (ZDPH)
        //         and GFŘ guidance D-59.
        // URL:    https://www.financnisprava.cz/cs/dane/dane/dan-z-pridane-hodnoty/kontrolni-hlaseni
        //
        // Mapping of codes to paragraphs (as of 2025):
        //   §92b — gold (zlato)                                               codes: 1, 1a
        //   §92c — waste/scrap, mobile devices, emission allowances, etc.     codes: 3, 3a, 4, 5, 6, 7
        //   §92d — construction and assembly work                              code:  11
        //   §92e — transfer of greenhouse gas emission allowances              codes: 12, 13, 14, 21, 25
        //
        // ValidFrom for all seed rows: 2016-01-01 (date §92e and the current číselník took effect).
        // ValidTo = null for all rows: codes remain valid until MFČR publishes a new revision.
        var rccSeedDate = new DateOnly(2016, 1, 1);
        modelBuilder.Entity<ReverseChargeCode>().HasData(
            // §92b — gold
            new ReverseChargeCode { Id = 1, Code = "1",  NameCs = "Zlato",                                                                      NameEn = "Gold",                                                     ParagraphRef = "§92b", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 2, Code = "1a", NameCs = "Investiční zlato",                                                           NameEn = "Investment gold",                                          ParagraphRef = "§92b", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },

            // §92c — waste, scrap, mobile devices, CPUs, emission allowances
            new ReverseChargeCode { Id = 3, Code = "3",  NameCs = "Odpady a šrot",                                                              NameEn = "Waste and scrap",                                          ParagraphRef = "§92c", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 4, Code = "3a", NameCs = "Emisní povolenky",                                                           NameEn = "Greenhouse gas emission allowances",                       ParagraphRef = "§92c", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 5, Code = "4",  NameCs = "Obiloviny a technické plodiny",                                              NameEn = "Cereals and industrial crops",                             ParagraphRef = "§92c", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 6, Code = "5",  NameCs = "Mobilní telefony",                                                           NameEn = "Mobile phones",                                            ParagraphRef = "§92c", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 7, Code = "6",  NameCs = "Integrované obvody a desky plošných spojů",                                  NameEn = "Integrated circuits and printed circuit boards",           ParagraphRef = "§92c", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 8, Code = "7",  NameCs = "Přenosná zařízení pro automatické zpracování dat (laptopy, tablety apod.)",  NameEn = "Portable automatic data-processing devices (laptops etc.)",ParagraphRef = "§92c", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },

            // §92d — construction and assembly work
            new ReverseChargeCode { Id = 9,  Code = "11", NameCs = "Stavební nebo montážní práce",                                             NameEn = "Construction or assembly work",                            ParagraphRef = "§92d", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },

            // §92e — transfer of emission allowances + other special supplies
            new ReverseChargeCode { Id = 10, Code = "12", NameCs = "Převod povolenek na emise skleníkových plynů",                             NameEn = "Transfer of greenhouse gas emission allowances",           ParagraphRef = "§92e", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 11, Code = "13", NameCs = "Dodání elektřiny obchodníkovi",                                            NameEn = "Supply of electricity to a trader",                        ParagraphRef = "§92e", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 12, Code = "14", NameCs = "Dodání plynu obchodníkovi",                                                NameEn = "Supply of gas to a trader",                                ParagraphRef = "§92e", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 13, Code = "21", NameCs = "Poskytnutí pracovní síly v oblasti stavebnictví",                          NameEn = "Provision of labour in construction",                      ParagraphRef = "§92e", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate },
            new ReverseChargeCode { Id = 14, Code = "25", NameCs = "Dodání nemovité věci, pokud se plátce rozhodl uplatnit daň",               NameEn = "Supply of immovable property where the taxable person opted to tax", ParagraphRef = "§92e", ValidFrom = rccSeedDate, ValidTo = null, IsActive = true, CreatedAt = seedDate }
        );

        // Seed content templates — all defaults are in Czech ("cs") language.
        modelBuilder.Entity<ContentTemplate>().HasData(
            new ContentTemplate { Id = 1, Name = "Default Invoice PDF", HtmlBody = DefaultSeedData.GetDefaultInvoicePdfTemplate(), TemplateType = EContentTemplateType.InvoicePdf, IsDefault = true, IsActive = true, Language = "cs", Description = "Default HTML template for rendering invoice PDFs.", CreatedAt = seedDate },
            new ContentTemplate { Id = 2, Name = "Default Credit Note PDF", HtmlBody = DefaultSeedData.GetDefaultCreditNotePdfTemplate(), TemplateType = EContentTemplateType.CreditNotePdf, IsDefault = true, IsActive = true, Language = "cs", Description = "Default HTML template for rendering credit note PDFs.", CreatedAt = seedDate },
            new ContentTemplate { Id = 3, Name = "Default Invoice Email", Subject = "Invoice {{InvoiceNumber}} from {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Invoice {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached invoice <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><br/><p>Thank you for your business.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.InvoiceEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email body when sending an invoice.", CreatedAt = seedDate },
            new ContentTemplate { Id = 4, Name = "Default Credit Note Email", Subject = "Credit Note {{InvoiceNumber}} from {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Credit Note {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached credit note <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Thank you for your business.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.CreditNoteEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email body when sending a credit note.", CreatedAt = seedDate },
            new ContentTemplate { Id = 5, Name = "Default Invitation Email", Subject = "Invitation to {{AppName}} — Set your password", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Welcome to {{AppName}}</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>You have been invited to {{AppName}}. Please set your password by clicking the button below:</p><div style=""text-align: center; margin: 30px 0;""><a href=""{{InvitationLink}}"" style=""background-color: #1976D2; color: white; padding: 14px 28px; text-decoration: none; border-radius: 4px; font-size: 16px;"">Set Password</a></div><p style=""color: #666; font-size: 14px;"">This link is valid for 48 hours.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{AppName}}</p></div>", TemplateType = EContentTemplateType.InvitationEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email sent to new users.", CreatedAt = seedDate },
            new ContentTemplate { Id = 6, Name = "Default Payment Reminder", Subject = "Payment reminder — Invoice {{InvoiceNumber}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #E65100;"">Payment Reminder</h2><p>Dear customer,</p><p>This is a friendly reminder that invoice <strong>{{InvoiceNumber}}</strong> is overdue.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><p>Please arrange payment at your earliest convenience.</p><br/><p>Thank you.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.ReminderEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Payment reminder for overdue invoices.", CreatedAt = seedDate },
            // ── Proforma + TaxReceiptForAdvance templates (issue #27) ──
            new ContentTemplate { Id = 8, Name = "Default Advance Invoice PDF", HtmlBody = DefaultSeedData.GetDefaultAdvanceInvoicePdfTemplate(), TemplateType = EContentTemplateType.AdvanceInvoicePdf, IsDefault = true, IsActive = true, Language = "cs", Description = "HTML šablona pro PDF zálohové faktury (není daňový doklad). Placeholdery stejné jako InvoicePdf.", CreatedAt = seedDate },
            new ContentTemplate { Id = 9, Name = "Default Tax Receipt for Advance PDF", HtmlBody = DefaultSeedData.GetDefaultTaxReceiptForAdvancePdfTemplate(), TemplateType = EContentTemplateType.TaxReceiptForAdvancePdf, IsDefault = true, IsActive = true, Language = "cs", Description = "HTML šablona pro PDF daňového dokladu o přijaté platbě. Tento doklad JE daňovým dokladem a zakládá povinnost DPH.", CreatedAt = seedDate },
            new ContentTemplate { Id = 10, Name = "Default Advance Invoice Email", Subject = "Zálohová faktura {{InvoiceNumber}} od {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #3D7A4A;"">Zálohová faktura {{InvoiceNumber}}</h2><p>Vážený zákazníku,</p><p>v příloze naleznete zálohovou fakturu <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Částka k úhradě:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Datum splatnosti:</strong> {{DueDate}}</p><br/><p>Upozorňujeme, že zálohová faktura není daňovým dokladem. Daňový doklad Vám bude vystaven po přijetí platby.</p><p>Děkujeme.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.AdvanceInvoiceEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email odesílaný spolu se zálohovou fakturou.", CreatedAt = seedDate },
            new ContentTemplate { Id = 11, Name = "Default Tax Receipt for Advance Email", Subject = "Daňový doklad o přijaté platbě {{InvoiceNumber}} od {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #6A3D9A;"">Daňový doklad o přijaté platbě {{InvoiceNumber}}</h2><p>Vážený zákazníku,</p><p>v příloze naleznete daňový doklad o přijaté platbě <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Uhrazená částka:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Děkujeme za platbu.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.TaxReceiptForAdvanceEmail, IsDefault = true, IsActive = true, Language = "cs", Description = "Email odesílaný spolu s daňovým dokladem o přijaté platbě.", CreatedAt = seedDate }
        );
    }

    // ─── Audit Trail ──────────────────────────────────────────────────────────

    public override int SaveChanges()
    {
        UpdateTimestamps();
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
