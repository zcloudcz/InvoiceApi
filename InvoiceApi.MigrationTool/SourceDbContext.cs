using InvoiceApi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.MigrationTool;

/// <summary>
/// Read-only database context for the LEGACY single-database schema.
///
/// Used exclusively by the MigrationTool to read data from the old single SQL Server
/// database (before the multi-tenant split). This context replaces the removed
/// ApplicationDbContext — it has the same DbSets and configuration but lives only
/// in the MigrationTool project since it's no longer needed at runtime.
///
/// All queries from this context should use AsNoTracking() for performance,
/// as we only read data to copy it into the new master/tenant databases.
/// </summary>
public class SourceDbContext : DbContext
{
    public SourceDbContext(DbContextOptions<SourceDbContext> options)
        : base(options)
    {
    }

    // ─── DbSets — mirrors the old ApplicationDbContext schema ─────────────────

    public DbSet<Client> Client { get; set; }
    public DbSet<Address> Address { get; set; }
    public DbSet<Contact> Contact { get; set; }
    public DbSet<BillingSettings> BillingSettings { get; set; }
    public DbSet<Invoice> Invoice { get; set; }
    public DbSet<InvoiceTemplate> InvoiceTemplate { get; set; }
    public DbSet<InvoiceItem> InvoiceItem { get; set; }
    public DbSet<Currency> Currency { get; set; }
    public DbSet<NumberSequenceFormat> NumberSequenceFormat { get; set; }
    public DbSet<NumberSequence> NumberSequence { get; set; }
    public DbSet<AresCache> AresCache { get; set; }
    public DbSet<VatRate> VatRate { get; set; }
    public DbSet<ContentTemplate> ContentTemplate { get; set; }
    public DbSet<User> User { get; set; }
    public DbSet<CompanySystemSettings> CompanySystemSettings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Minimal configuration — just enough for EF Core to read the old schema.
        // No seed data needed since this is read-only.

        // Client
        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasMany(e => e.Address).WithOne(a => a.Client).HasForeignKey(a => a.ClientId);
            entity.HasMany(e => e.Contact).WithOne(c => c.Client).HasForeignKey(c => c.ClientId);
            entity.HasOne(e => e.BillingSettings).WithOne(b => b.Client).HasForeignKey<BillingSettings>(b => b.ClientId);
            entity.HasOne(e => e.PreferredCurrency).WithMany().HasForeignKey(e => e.PreferredCurrencyId).IsRequired(false);
        });

        // Invoice (TPH: Invoice + InvoiceTemplate in same table)
        modelBuilder.Entity<Invoice>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasDiscriminator<string>("InvoiceType")
                .HasValue<Invoice>("Invoice")
                .HasValue<InvoiceTemplate>("Template");
            entity.HasOne(e => e.Client).WithMany().HasForeignKey(e => e.ClientId).IsRequired(false);
            entity.HasOne(e => e.Issuer).WithMany().HasForeignKey(e => e.IssuerId).IsRequired(true);
            entity.HasOne(e => e.Currency).WithMany().HasForeignKey(e => e.CurrencyId);
            entity.HasOne(e => e.OriginalInvoice).WithMany(i => i.CreditNote).HasForeignKey(e => e.OriginalInvoiceId);
            entity.HasMany(e => e.InvoiceItem).WithOne(i => i.Invoice).HasForeignKey(i => i.InvoiceId);
        });

        // InvoiceTemplate
        modelBuilder.Entity<InvoiceTemplate>(entity =>
        {
            entity.HasOne(e => e.NumberSequence).WithMany().HasForeignKey(e => e.NumberSequenceId).IsRequired(false);
        });

        // InvoiceItem
        modelBuilder.Entity<InvoiceItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.VatRate).WithMany(v => v.InvoiceItems).HasForeignKey(e => e.VatRateId);
        });

        // NumberSequence
        modelBuilder.Entity<NumberSequence>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.NumberSequenceFormat).WithMany().HasForeignKey(e => e.NumberSequenceFormatId);
        });

        // CompanySystemSettings
        modelBuilder.Entity<CompanySystemSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Company).WithOne().HasForeignKey<CompanySystemSettings>(e => e.CompanyId);
        });

        // Simple entities — just primary keys
        modelBuilder.Entity<Address>().HasKey(e => e.Id);
        modelBuilder.Entity<Contact>().HasKey(e => e.Id);
        modelBuilder.Entity<BillingSettings>().HasKey(e => e.Id);
        modelBuilder.Entity<Currency>().HasKey(e => e.Id);
        modelBuilder.Entity<NumberSequenceFormat>().HasKey(e => e.Id);
        modelBuilder.Entity<AresCache>().HasKey(e => e.Id);
        modelBuilder.Entity<VatRate>().HasKey(e => e.Id);
        modelBuilder.Entity<ContentTemplate>().HasKey(e => e.Id);
        modelBuilder.Entity<User>().HasKey(e => e.Id);
    }
}
