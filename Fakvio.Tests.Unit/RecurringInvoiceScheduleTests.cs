using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the RecurringInvoiceSchedule domain entity.
///
/// Covers:
/// 1. BaseEntity inheritance — Id, CreatedAt/UpdatedAt, audit columns present.
/// 2. Default property values — IsActive, AutoSend, IntervalCount, OccurrenceCount.
/// 3. ERecurrenceFrequency enum values — all four variants exist and are distinct.
/// 4. Entity persists correctly through TenantDbContext (InMemoryDatabase).
/// 5. Nullable scheduling fields — DayOfMonth, DayOfWeek, LastRunAt, EndDate, MaxOccurrences, LastError.
/// 6. Frequency + IntervalCount combinations that downstream NextRunAt calculation will use.
///
/// NOTE: NextRunAt calculation logic lives in a separate Application-layer service (issue #52).
/// These tests only verify the entity structure and storage, NOT the scheduling algorithm.
/// </summary>
public class RecurringInvoiceScheduleTests : IDisposable
{
    // In-memory database context — each test class gets an isolated DB.
    private readonly TenantDbContext _context;

    public RecurringInvoiceScheduleTests()
    {
        // Unique DB name per test class prevents cross-test pollution.
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    // ── Helper: seed a minimal Client and InvoiceTemplate so FK constraints are satisfied ─

    private async Task<(Client client, InvoiceTemplate template)> SeedPrerequisitesAsync()
    {
        // Client — InMemoryDatabase enforces IsRequired() so RegistrationNumber and CompanyName must be set.
        var client = new Client
        {
            RegistrationNumber = "12345678",
            CompanyName = "Test Client s.r.o.",
            IsIssuer = false,
            IsActive = true,
            Language = "cs",
            CreatedAt = DateTime.UtcNow,
        };
        _context.Client.Add(client);
        await _context.SaveChangesAsync();

        // Issuer — required on Invoice (InvoiceTemplate inherits Invoice which requires IssuerId).
        var issuer = new Client
        {
            RegistrationNumber = "87654321",
            CompanyName = "My Company a.s.",
            IsIssuer = true,
            IsActive = true,
            Language = "cs",
            CreatedAt = DateTime.UtcNow,
        };
        _context.Client.Add(issuer);
        await _context.SaveChangesAsync();

        // Currency — required on Invoice.
        var currency = new Currency
        {
            Code = "CZK",
            Name = "Česká koruna",
            Symbol = "Kč",
            IsActive = true,
            SortOrder = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _context.Currency.Add(currency);
        await _context.SaveChangesAsync();

        // InvoiceTemplate — lives in the Invoice table via TPH discriminator.
        var template = new InvoiceTemplate
        {
            Name = "Monthly Hosting",
            IssuerId = issuer.Id,
            ClientId = client.Id,
            CurrencyId = currency.Id,
            IsActive = true,
            DueDateOffsetDays = 14,
            InvoiceItem = new List<InvoiceItem>(),
            CreatedAt = DateTime.UtcNow,
        };
        _context.InvoiceTemplate.Add(template);
        await _context.SaveChangesAsync();

        return (client, template);
    }

    // ── 1. ERecurrenceFrequency enum values ───────────────────────────────────

    [Fact]
    public void ERecurrenceFrequency_HasAllExpectedValues()
    {
        // Verify all four values are present and have stable int mappings.
        // Downstream job query and migration depend on these integer values never changing.
        ((int)ERecurrenceFrequency.Weekly).ShouldBe(1);
        ((int)ERecurrenceFrequency.Monthly).ShouldBe(2);
        ((int)ERecurrenceFrequency.Quarterly).ShouldBe(3);
        ((int)ERecurrenceFrequency.Yearly).ShouldBe(4);
    }

    [Fact]
    public void ERecurrenceFrequency_AllValuesAreDistinct()
    {
        // Ensures no accidental duplicate values sneak in via future refactoring.
        var values = Enum.GetValues<ERecurrenceFrequency>();
        var distinct = values.Select(v => (int)v).Distinct().ToList();
        distinct.Count.ShouldBe(values.Length);
    }

    // ── 2. Default property values ────────────────────────────────────────────

    [Fact]
    public void RecurringInvoiceSchedule_DefaultValues_AreCorrect()
    {
        // Arrange + Act: create entity with only mandatory fields filled.
        var schedule = new RecurringInvoiceSchedule
        {
            TemplateId = 1,
            ClientId = 1,
            Frequency = ERecurrenceFrequency.Monthly,
            NextRunAt = DateTimeOffset.UtcNow.AddDays(30),
        };

        // Assert: check CLR-level defaults before any DB interaction.
        schedule.IsActive.ShouldBeTrue();        // default true — schedule is active when created
        schedule.AutoSend.ShouldBeFalse();       // default false — safer, user reviews first
        schedule.IntervalCount.ShouldBe(1);      // default 1 — every period
        schedule.OccurrenceCount.ShouldBe(0);    // default 0 — never fired yet
        schedule.LastRunAt.ShouldBeNull();       // never fired
        schedule.EndDate.ShouldBeNull();         // runs indefinitely by default
        schedule.MaxOccurrences.ShouldBeNull();  // unlimited by default
        schedule.LastError.ShouldBeNull();       // no error yet
        schedule.DayOfMonth.ShouldBeNull();      // not specified
        schedule.DayOfWeek.ShouldBeNull();       // not specified
    }

    // ── 3. BaseEntity inheritance ─────────────────────────────────────────────

    [Fact]
    public void RecurringInvoiceSchedule_InheritsBaseEntity()
    {
        // RecurringInvoiceSchedule must inherit BaseEntity so that audit columns
        // (CreatedAt, UpdatedAt, CreatedByUserId, UpdatedByUserId) exist.
        var schedule = new RecurringInvoiceSchedule();

        // These properties come from BaseEntity — accessing them proves inheritance.
        schedule.Id.ShouldBe(0);                      // long, default 0
        schedule.CreatedAt.ShouldBe(default);          // DateTime
        schedule.UpdatedAt.ShouldBeNull();             // DateTime?
        schedule.CreatedByUserId.ShouldBeNull();       // long?
        schedule.UpdatedByUserId.ShouldBeNull();       // long?

        // Type check
        (schedule is Fakvio.Domain.Common.BaseEntity).ShouldBeTrue();
    }

    // ── 4. Persist and retrieve via TenantDbContext ───────────────────────────

    [Fact]
    public async Task RecurringInvoiceSchedule_CanBeSavedAndRetrieved()
    {
        // Arrange: seed required FK dependencies.
        var (client, template) = await SeedPrerequisitesAsync();

        var nextRunAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var schedule = new RecurringInvoiceSchedule
        {
            TemplateId = template.Id,
            ClientId = client.Id,
            Frequency = ERecurrenceFrequency.Monthly,
            IntervalCount = 1,
            DayOfMonth = 1,
            NextRunAt = nextRunAt,
            IsActive = true,
            AutoSend = false,
        };

        // Act
        _context.RecurringInvoiceSchedule.Add(schedule);
        await _context.SaveChangesAsync();

        // Assert: clear tracking and reload from "DB".
        _context.ChangeTracker.Clear();
        var loaded = await _context.RecurringInvoiceSchedule
            .FirstOrDefaultAsync(s => s.Id == schedule.Id);

        loaded.ShouldNotBeNull();
        loaded.TemplateId.ShouldBe(template.Id);
        loaded.ClientId.ShouldBe(client.Id);
        loaded.Frequency.ShouldBe(ERecurrenceFrequency.Monthly);
        loaded.IntervalCount.ShouldBe(1);
        loaded.DayOfMonth.ShouldBe(1);
        loaded.NextRunAt.ShouldBe(nextRunAt);
        loaded.IsActive.ShouldBeTrue();
        loaded.AutoSend.ShouldBeFalse();
    }

    // ── 5. Optional scheduling fields round-trip ──────────────────────────────

    [Fact]
    public async Task RecurringInvoiceSchedule_OptionalFields_PersistCorrectly()
    {
        // Arrange
        var (client, template) = await SeedPrerequisitesAsync();

        var endDate = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lastRunAt = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var schedule = new RecurringInvoiceSchedule
        {
            TemplateId = template.Id,
            ClientId = client.Id,
            Frequency = ERecurrenceFrequency.Weekly,
            DayOfWeek = DayOfWeek.Monday,    // fires every Monday
            NextRunAt = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
            LastRunAt = lastRunAt,
            EndDate = endDate,
            MaxOccurrences = 12,
            OccurrenceCount = 3,
            LastError = "SMTP timeout",
            IsActive = false,               // paused
            AutoSend = true,
        };

        _context.RecurringInvoiceSchedule.Add(schedule);
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var loaded = await _context.RecurringInvoiceSchedule
            .FirstOrDefaultAsync(s => s.Id == schedule.Id);

        // Assert all optional fields
        loaded.ShouldNotBeNull();
        loaded.Frequency.ShouldBe(ERecurrenceFrequency.Weekly);
        loaded.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        loaded.LastRunAt.ShouldBe(lastRunAt);
        loaded.EndDate.ShouldBe(endDate);
        loaded.MaxOccurrences.ShouldBe(12);
        loaded.OccurrenceCount.ShouldBe(3);
        loaded.LastError.ShouldBe("SMTP timeout");
        loaded.IsActive.ShouldBeFalse();
        loaded.AutoSend.ShouldBeTrue();
    }

    // ── 6. IntervalCount variations ───────────────────────────────────────────

    [Theory]
    [InlineData(ERecurrenceFrequency.Weekly, 1)]
    [InlineData(ERecurrenceFrequency.Weekly, 2)]    // bi-weekly
    [InlineData(ERecurrenceFrequency.Monthly, 1)]
    [InlineData(ERecurrenceFrequency.Monthly, 3)]   // quarterly via Monthly+3
    [InlineData(ERecurrenceFrequency.Quarterly, 1)]
    [InlineData(ERecurrenceFrequency.Yearly, 1)]
    public async Task RecurringInvoiceSchedule_FrequencyIntervalCount_Combinations_PersistCorrectly(
        ERecurrenceFrequency frequency, int intervalCount)
    {
        // Arrange
        var (client, template) = await SeedPrerequisitesAsync();

        var schedule = new RecurringInvoiceSchedule
        {
            TemplateId = template.Id,
            ClientId = client.Id,
            Frequency = frequency,
            IntervalCount = intervalCount,
            NextRunAt = DateTimeOffset.UtcNow.AddDays(7),
        };

        // Act
        _context.RecurringInvoiceSchedule.Add(schedule);
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var loaded = await _context.RecurringInvoiceSchedule
            .FirstOrDefaultAsync(s => s.Id == schedule.Id);

        // Assert
        loaded.ShouldNotBeNull();
        loaded.Frequency.ShouldBe(frequency);
        loaded.IntervalCount.ShouldBe(intervalCount);
    }

    // ── 7. DbSet is registered on TenantDbContext ──────────────────────────────

    [Fact]
    public void TenantDbContext_ExposesRecurringInvoiceScheduleDbSet()
    {
        // Verifies that DbSet<RecurringInvoiceSchedule> is properly exposed.
        // If missing, LINQ queries in the Application service layer would throw at startup.
        _context.RecurringInvoiceSchedule.ShouldNotBeNull();
    }

    // ── 8. Composite index (IsActive + NextRunAt) exists in EF model ───────────

    [Fact]
    public void TenantDbContext_HasCompositeIndex_IsActive_NextRunAt()
    {
        // The hot-path query "SELECT * WHERE IsActive = true AND NextRunAt <= @now" relies on
        // a composite index on (IsActive, NextRunAt). Verify that EF Core model metadata
        // includes this index so a code review removing it would be caught by this test.
        //
        // We inspect the IModel metadata rather than parsing raw SQL or running EXPLAIN —
        // InMemoryDatabase does not expose query plans, but the EF model is always available.
        var entityType = _context.Model.FindEntityType(typeof(RecurringInvoiceSchedule));
        entityType.ShouldNotBeNull();

        // Collect all index column sets as sorted name strings for easy comparison.
        var indexes = entityType.GetIndexes()
            .Select(ix => string.Join(",", ix.Properties.Select(p => p.Name).OrderBy(n => n)))
            .ToList();

        // The composite index must contain both columns.
        // EF Core stores index columns in declaration order, but we compare sorted names
        // to be robust against future column-order changes.
        indexes.ShouldContain("IsActive,NextRunAt",
            "Composite index (IsActive, NextRunAt) is required for the recurring-job hot-path query.");
    }

    // ── 9. IntervalCount = 0 saves without DB error (no check constraint yet) ──

    [Fact]
    public async Task RecurringInvoiceSchedule_ZeroIntervalCount_CanBeSaved_DocumentsAbsenceOfConstraint()
    {
        // IntervalCount = 0 is semantically invalid (would cause infinite loops in the
        // scheduler) but no CHECK constraint exists at the EF / DB level in v51.
        // This test documents the CURRENT behaviour — it saves — so that if a future
        // migration adds "CHECK (IntervalCount >= 1)" this test will catch the regression
        // in the entity tests and remind the team to update the constraint.
        //
        // NOTE: This is NOT a green-light to keep 0 as a valid value. Issue #52
        // (IRecurringInvoiceService) should add guard validation at the service layer.
        var (client, template) = await SeedPrerequisitesAsync();

        var schedule = new RecurringInvoiceSchedule
        {
            TemplateId = template.Id,
            ClientId = client.Id,
            Frequency = ERecurrenceFrequency.Monthly,
            IntervalCount = 0,      // semantically invalid — no DB constraint blocks this yet
            NextRunAt = DateTimeOffset.UtcNow.AddDays(30),
        };

        // Act: should NOT throw (InMemory DB has no check constraints).
        _context.RecurringInvoiceSchedule.Add(schedule);
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var loaded = await _context.RecurringInvoiceSchedule
            .FirstOrDefaultAsync(s => s.Id == schedule.Id);

        // Current behaviour: 0 is stored as-is. If this assertion ever fails, a DB
        // check constraint has been added (good!) and the test must be updated.
        loaded.ShouldNotBeNull();
        loaded.IntervalCount.ShouldBe(0);
    }

    // ── 10. NextRunAt / EndDate UTC offset round-trip ─────────────────────────

    [Fact]
    public async Task RecurringInvoiceSchedule_DateTimeOffset_WithNonZeroUtcOffset_PreservesUtcEquivalent()
    {
        // DateTimeOffset columns in PostgreSQL are stored as UTC (timestamp with time zone).
        // A value created with offset +02:00 must round-trip as the same instant in UTC.
        // This test catches mapping bugs where the timezone offset is silently dropped or
        // double-applied (e.g. a naive DateTime conversion losing the +02:00 shift).
        //
        // Using InMemoryDatabase — which stores the CLR DateTimeOffset as-is — verifies
        // that the entity wiring does not perform accidental offset stripping before persistence.
        var (client, template) = await SeedPrerequisitesAsync();

        // Simulate a user in UTC+2 (CEST) setting NextRunAt and EndDate.
        var cest = TimeSpan.FromHours(2);
        var nextRunAt = new DateTimeOffset(2026, 6, 15, 10, 0, 0, cest); // 10:00 CEST = 08:00 UTC
        var endDate   = new DateTimeOffset(2026, 12, 31, 23, 59, 0, cest);

        var schedule = new RecurringInvoiceSchedule
        {
            TemplateId = template.Id,
            ClientId = client.Id,
            Frequency = ERecurrenceFrequency.Monthly,
            NextRunAt = nextRunAt,
            EndDate = endDate,
        };

        _context.RecurringInvoiceSchedule.Add(schedule);
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();
        var loaded = await _context.RecurringInvoiceSchedule
            .FirstOrDefaultAsync(s => s.Id == schedule.Id);

        loaded.ShouldNotBeNull();

        // The UTC instant must be preserved regardless of what offset the loaded value carries.
        loaded.NextRunAt.ToUniversalTime().ShouldBe(nextRunAt.ToUniversalTime());
        loaded.EndDate!.Value.ToUniversalTime().ShouldBe(endDate.ToUniversalTime());

        // Double-check: the UTC equivalent hour is 08:00 (not 10:00 — offset not dropped).
        loaded.NextRunAt.ToUniversalTime().Hour.ShouldBe(8);
    }
}
