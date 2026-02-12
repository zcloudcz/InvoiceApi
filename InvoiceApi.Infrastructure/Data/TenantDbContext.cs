using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.Infrastructure.Data;

/// <summary>
/// Database context for individual TENANT databases in the multi-tenant architecture (Azure SQL / SQL Server).
///
/// Each company (tenant) gets its own SQL Server database with this schema.
/// The tenant database is self-contained — it has its own copy of all business data
/// AND its own copies of code tables (VatRate, Currency, NumberSequenceFormat, ContentTemplate).
///
/// Why self-contained? So that:
/// 1. Tenant queries never need to cross databases (performance)
/// 2. Tenants can customize code table values (e.g., add custom VAT rates)
/// 3. If a tenant's DB is backed up/restored, everything is in one place
///
/// What is NOT in tenant DB:
/// - Users (stored in master DB — authentication is centralized)
/// - CompanySystemSettings (stored in master DB — infrastructure config)
///
/// Connection string is resolved per-request by ITenantDbContextFactory,
/// which reads CompanySystemSettings from the master DB.
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
    /// Cached ARES lookups (company registry data)
    /// </summary>
    public DbSet<AresCache> AresCache { get; set; }

    // ─── Entity Configuration ─────────────────────────────────────────────────

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Exclude master-only entities ───────────────────────────────────────
        // Tenant databases don't have Users or CompanySystemSettings.
        // These entities don't have nav props pointing INTO tenant data,
        // but we exclude them explicitly for clarity and safety.
        modelBuilder.Ignore<User>();
        modelBuilder.Ignore<CompanySystemSettings>();
        modelBuilder.Ignore<SystemConfiguration>(); // Master DB only — global system settings
        modelBuilder.Ignore<AppLog>();               // Master DB only — application logs

        ConfigureClient(modelBuilder);
        ConfigureAddress(modelBuilder);
        ConfigureContact(modelBuilder);
        ConfigureBillingSettings(modelBuilder);
        ConfigureCurrency(modelBuilder);
        ConfigureInvoice(modelBuilder);
        ConfigureInvoiceItem(modelBuilder);
        ConfigureNumberSequenceFormat(modelBuilder);
        ConfigureNumberSequence(modelBuilder);
        ConfigureAresCache(modelBuilder);
        ConfigureVatRate(modelBuilder);
        ConfigureContentTemplate(modelBuilder);

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

            entity.HasMany(e => e.Address)
                .WithOne(a => a.Client)
                .HasForeignKey(a => a.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Contact)
                .WithOne(c => c.Client)
                .HasForeignKey(c => c.ClientId)
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

            // SQL Server does not allow multiple cascade paths from the same table.
            // Client → BillingSettings is Cascade, so these FKs to NumberSequence
            // must use NoAction to avoid "cycles or multiple cascade paths" error.
            // Application code handles nullification when a NumberSequence is deleted.
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
            entity.HasIndex(e => e.DocumentNumber)
                .IsUnique()
                .HasFilter("[DocumentNumber] IS NOT NULL AND [DocumentNumber] <> 'DRAFT'");
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
            entity.Property(e => e.ProductCode).HasMaxLength(100);
            entity.Property(e => e.Notes).HasMaxLength(1000);

            entity.HasOne(e => e.VatRate).WithMany(v => v.InvoiceItems).HasForeignKey(e => e.VatRateId).OnDelete(DeleteBehavior.Restrict);
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

            // Optimistic concurrency — SQL Server rowversion (timestamp) column.
            // The database auto-updates this byte[] on every INSERT/UPDATE.
            // EF Core uses .IsRowVersion() which sets it as a concurrency token.
            entity.Property(e => e.RowVersion)
                .IsRowVersion();

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
            entity.HasIndex(e => new { e.TemplateType, e.IsDefault });

            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Subject).HasMaxLength(500);
            // SQL Server uses nvarchar(max) by default for string properties;
            // no explicit HasColumnType needed (was "TEXT" for PostgreSQL).
            entity.Property(e => e.HtmlBody).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(2000);
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

        // Seed default number sequences
        modelBuilder.Entity<NumberSequence>().HasData(
            new NumberSequence { Id = 1, Name = "Default Invoice Sequence", DocumentType = EDocumentType.Invoice, Prefix = "INV", CurrentNumber = 0, IsDefault = true, NumberSequenceFormatId = 1, IsActive = true, CreatedAt = seedDate },
            new NumberSequence { Id = 2, Name = "Default Credit Note Sequence", DocumentType = EDocumentType.CreditNote, Prefix = "CN", CurrentNumber = 0, IsDefault = true, NumberSequenceFormatId = 1, IsActive = true, CreatedAt = seedDate }
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

        // Seed content templates
        modelBuilder.Entity<ContentTemplate>().HasData(
            new ContentTemplate { Id = 1, Name = "Default Invoice PDF", HtmlBody = DefaultSeedData.GetDefaultInvoicePdfTemplate(), TemplateType = EContentTemplateType.InvoicePdf, IsDefault = true, IsActive = true, Description = "Default HTML template for rendering invoice PDFs.", CreatedAt = seedDate },
            new ContentTemplate { Id = 2, Name = "Default Credit Note PDF", HtmlBody = DefaultSeedData.GetDefaultCreditNotePdfTemplate(), TemplateType = EContentTemplateType.CreditNotePdf, IsDefault = true, IsActive = true, Description = "Default HTML template for rendering credit note PDFs.", CreatedAt = seedDate },
            new ContentTemplate { Id = 3, Name = "Default Invoice Email", Subject = "Invoice {{InvoiceNumber}} from {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Invoice {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached invoice <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><br/><p>Thank you for your business.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.InvoiceEmail, IsDefault = true, IsActive = true, Description = "Email body when sending an invoice.", CreatedAt = seedDate },
            new ContentTemplate { Id = 4, Name = "Default Credit Note Email", Subject = "Credit Note {{InvoiceNumber}} from {{CompanyName}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Credit Note {{InvoiceNumber}}</h2><p>Dear customer,</p><p>Please find the attached credit note <strong>{{InvoiceNumber}}</strong>.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><br/><p>Thank you for your business.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.CreditNoteEmail, IsDefault = true, IsActive = true, Description = "Email body when sending a credit note.", CreatedAt = seedDate },
            new ContentTemplate { Id = 5, Name = "Default Invitation Email", Subject = "Invitation to {{AppName}} — Set your password", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #1976D2;"">Welcome to {{AppName}}</h2><p>Hello <strong>{{FullName}}</strong>,</p><p>You have been invited to {{AppName}}. Please set your password by clicking the button below:</p><div style=""text-align: center; margin: 30px 0;""><a href=""{{InvitationLink}}"" style=""background-color: #1976D2; color: white; padding: 14px 28px; text-decoration: none; border-radius: 4px; font-size: 16px;"">Set Password</a></div><p style=""color: #666; font-size: 14px;"">This link is valid for 48 hours.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{AppName}}</p></div>", TemplateType = EContentTemplateType.InvitationEmail, IsDefault = true, IsActive = true, Description = "Email sent to new users.", CreatedAt = seedDate },
            new ContentTemplate { Id = 6, Name = "Default Payment Reminder", Subject = "Payment reminder — Invoice {{InvoiceNumber}}", HtmlBody = @"<div style=""font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto;""><h2 style=""color: #E65100;"">Payment Reminder</h2><p>Dear customer,</p><p>This is a friendly reminder that invoice <strong>{{InvoiceNumber}}</strong> is overdue.</p><p><strong>Total:</strong> {{TotalWithVat}} {{CurrencyCode}}</p><p><strong>Due date:</strong> {{DueDate}}</p><p>Please arrange payment at your earliest convenience.</p><br/><p>Thank you.</p><hr style=""border: none; border-top: 1px solid #eee; margin: 20px 0;"" /><p style=""color: #999; font-size: 12px;"">{{CompanyName}}</p></div>", TemplateType = EContentTemplateType.ReminderEmail, IsDefault = true, IsActive = true, Description = "Payment reminder for overdue invoices.", CreatedAt = seedDate }
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
}
