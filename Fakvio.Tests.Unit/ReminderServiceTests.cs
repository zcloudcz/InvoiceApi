using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ContentTemplate;
using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ReminderService — covers settings CRUD, reminder queries,
/// cancellation, dashboard data, and the dunning job (ProcessOverdueInvoicesAsync).
///
/// Uses InMemoryDatabase for the TenantDbContext and NSubstitute for external
/// dependencies (email, PDF, interest calculator, content templates).
/// </summary>
public class ReminderServiceTests : IDisposable
{
    // The in-memory database context simulating the tenant's PostgreSQL schema.
    private readonly TenantDbContext _context;

    // The service under test — handles all reminder/dunning logic.
    private readonly ReminderService _service;

    // NSubstitute mocks for external dependencies injected into ReminderService.
    private readonly IInterestCalculator _interestCalculator;
    private readonly IEmailService _emailService;
    private readonly IPdfExportService _pdfExportService;
    private readonly IContentTemplateService _contentTemplateService;
    private readonly ILogger<ReminderService> _logger;

    public ReminderServiceTests()
    {
        // Each test class gets a unique in-memory database to prevent cross-test pollution.
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        // Create NSubstitute mocks for all constructor dependencies.
        _interestCalculator = Substitute.For<IInterestCalculator>();
        _emailService = Substitute.For<IEmailService>();
        _pdfExportService = Substitute.For<IPdfExportService>();
        _contentTemplateService = Substitute.For<IContentTemplateService>();
        _logger = Substitute.For<ILogger<ReminderService>>();

        // Default mock: interest calculator always returns 100 CZK (simplifies dunning assertions).
        _interestCalculator
            .Calculate(Arg.Any<decimal>(), Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(100m);

        // Default mock: no email template found — forces fallback body in SendReminderAsync.
        _contentTemplateService
            .GetDefaultByTypeAsync(Arg.Any<EContentTemplateType>(), Arg.Any<CancellationToken>())
            .Returns((ContentTemplateDto?)null);

        // Instantiate the service under test with all mocks.
        _service = new ReminderService(
            _context,
            _interestCalculator,
            _emailService,
            _pdfExportService,
            _contentTemplateService,
            _logger);

        // Seed common reference data used across most tests.
        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds the minimum required entities for test scenarios:
    /// - A customer Client (Id=1) with an email Contact
    /// - An issuer Client (Id=2, IsIssuer=true)
    /// - A Currency (Id=1, Code="CZK")
    /// - A company-wide ReminderSettings (ClientId=null) with 3 escalation levels
    ///
    /// Entities are saved one-by-one to satisfy InMemoryDb's required-field validation.
    /// </summary>
    private void SeedTestData()
    {
        // Customer client with an email contact for sending reminders.
        _context.Client.Add(new Client
        {
            Id = 1,
            CompanyName = "Test Client",
            RegistrationNumber = "12345678",
            IsIssuer = false,
            IsActive = true,
            Contact = new List<Contact>
            {
                new() { ContactType = EContactType.Email, ContactValue = "client@test.com", IsPrimary = true }
            }
        });
        _context.SaveChanges();

        // Issuer (the user's own company) — required by Invoice.IssuerId.
        _context.Client.Add(new Client
        {
            Id = 2,
            CompanyName = "My Company",
            RegistrationNumber = "87654321",
            IsIssuer = true,
            IsActive = true
        });
        _context.SaveChanges();

        // Currency — required by Invoice.CurrencyId.
        _context.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kc" });
        _context.SaveChanges();

        // Company-wide default reminder settings with 3 escalation levels.
        _context.ReminderSettings.Add(new ReminderSettings
        {
            Id = 1,
            ClientId = null,       // null = company-wide default
            IsEnabled = true,
            MaxReminderLevel = 3,
            GracePeriodDays = 7,
            IncludeInterest = false,
            AttachInvoicePdf = true,
            AutoSendEmail = true,
            Levels = new List<ReminderLevel>
            {
                new() { Level = 1, DaysAfterPrevious = 7, FixedFeeCzk = 0 },
                new() { Level = 2, DaysAfterPrevious = 14, FixedFeeCzk = 50 },
                new() { Level = 3, DaysAfterPrevious = 14, FixedFeeCzk = 200 }
            }
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Helper: creates a standard overdue invoice for dunning tests.
    /// Status=Completed, DueDate 30 days ago, 50 000 CZK, with required fields set.
    /// </summary>
    private Invoice CreateOverdueInvoice(long id = 1, int daysOverdue = 30,
        EInvoiceStatus status = EInvoiceStatus.Completed)
    {
        return new Invoice
        {
            Id = id,
            ClientId = 1,
            IssuerId = 2,
            CurrencyId = 1,
            Status = status,
            DueDate = DateTime.UtcNow.AddDays(-daysOverdue),
            TotalWithVat = 50000m,
            DocumentNumber = $"INV{id:D3}",
            InvoiceItem = new List<InvoiceItem>()
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Settings CRUD Tests
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When no company settings exist, GetCompanySettingsAsync should auto-create
    /// default settings with 3 levels and return them.
    /// </summary>
    [Fact]
    public async Task GetCompanySettingsAsync_NoExisting_CreatesDefaults()
    {
        // Arrange — remove the seeded company settings so GetCompanySettingsAsync creates new ones.
        var existing = await _context.ReminderSettings
            .Include(s => s.Levels)
            .FirstOrDefaultAsync(s => s.ClientId == null);
        if (existing != null)
        {
            _context.ReminderLevel.RemoveRange(existing.Levels);
            _context.ReminderSettings.Remove(existing);
            await _context.SaveChangesAsync();
        }

        // Act
        var result = await _service.GetCompanySettingsAsync();

        // Assert — auto-created defaults should have 3 levels and sensible values.
        result.ShouldNotBeNull();
        result.ClientId.ShouldBeNull();
        result.IsEnabled.ShouldBeTrue();
        result.MaxReminderLevel.ShouldBe(3);
        result.GracePeriodDays.ShouldBe(7);
        result.Levels.Count.ShouldBe(3);
    }

    /// <summary>
    /// When company settings already exist, GetCompanySettingsAsync should return them
    /// without creating duplicates.
    /// </summary>
    [Fact]
    public async Task GetCompanySettingsAsync_Existing_ReturnsExisting()
    {
        // Act — seeded data already has company settings.
        var result = await _service.GetCompanySettingsAsync();

        // Assert
        result.ShouldNotBeNull();
        result.ClientId.ShouldBeNull();
        result.IsEnabled.ShouldBeTrue();
        result.MaxReminderLevel.ShouldBe(3);
        result.Levels.Count.ShouldBe(3);

        // Verify no duplicate was created.
        var count = await _context.ReminderSettings.CountAsync(s => s.ClientId == null);
        count.ShouldBe(1);
    }

    /// <summary>
    /// UpsertSettingsAsync with a new ClientId should create a client-level override.
    /// </summary>
    [Fact]
    public async Task UpsertSettingsAsync_NewClientOverride_CreatesOverride()
    {
        // Arrange — create a DTO for a per-client override (Client Id=1).
        var dto = new UpdateReminderSettingsDto
        {
            ClientId = 1,
            IsEnabled = true,
            MaxReminderLevel = 2,
            GracePeriodDays = 5,
            IncludeInterest = true,
            AttachInvoicePdf = false,
            AutoSendEmail = false,
            Levels = new List<UpdateReminderLevelDto>
            {
                new() { Level = 1, DaysAfterPrevious = 3, FixedFeeCzk = 0 },
                new() { Level = 2, DaysAfterPrevious = 10, FixedFeeCzk = 100 }
            }
        };

        // Act
        var result = await _service.UpsertSettingsAsync(dto);

        // Assert — should create a new override record with the specified values.
        result.ShouldNotBeNull();
        result.ClientId.ShouldBe(1);
        result.MaxReminderLevel.ShouldBe(2);
        result.GracePeriodDays.ShouldBe(5);
        result.IncludeInterest.ShouldBeTrue();
        result.Levels.Count.ShouldBe(2);
    }

    /// <summary>
    /// UpsertSettingsAsync on an existing record should update it in place, not create a duplicate.
    /// </summary>
    [Fact]
    public async Task UpsertSettingsAsync_ExistingSettings_UpdatesInPlace()
    {
        // Arrange — update the company-wide defaults (ClientId=null).
        var dto = new UpdateReminderSettingsDto
        {
            ClientId = null,
            IsEnabled = false,
            MaxReminderLevel = 5,
            GracePeriodDays = 14,
            IncludeInterest = true,
            AttachInvoicePdf = false,
            AutoSendEmail = false,
            Levels = new List<UpdateReminderLevelDto>
            {
                new() { Level = 1, DaysAfterPrevious = 5, FixedFeeCzk = 10 }
            }
        };

        // Act
        var result = await _service.UpsertSettingsAsync(dto);

        // Assert — the existing record should be updated, not duplicated.
        result.IsEnabled.ShouldBeFalse();
        result.MaxReminderLevel.ShouldBe(5);
        result.GracePeriodDays.ShouldBe(14);
        result.Levels.Count.ShouldBe(1);

        var count = await _context.ReminderSettings.CountAsync(s => s.ClientId == null);
        count.ShouldBe(1);
    }

    /// <summary>
    /// DeleteClientSettingsAsync should return true and remove the record when it exists.
    /// </summary>
    [Fact]
    public async Task DeleteClientSettingsAsync_Exists_ReturnsTrue()
    {
        // Arrange — create a client override first.
        _context.ReminderSettings.Add(new ReminderSettings
        {
            ClientId = 1,
            IsEnabled = true,
            MaxReminderLevel = 2,
            GracePeriodDays = 3,
            Levels = new List<ReminderLevel>
            {
                new() { Level = 1, DaysAfterPrevious = 3, FixedFeeCzk = 0 }
            }
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.DeleteClientSettingsAsync(1);

        // Assert
        result.ShouldBeTrue();
        var settings = await _context.ReminderSettings.FirstOrDefaultAsync(s => s.ClientId == 1);
        settings.ShouldBeNull();
    }

    /// <summary>
    /// DeleteClientSettingsAsync should return false when no override exists for the client.
    /// </summary>
    [Fact]
    public async Task DeleteClientSettingsAsync_NotFound_ReturnsFalse()
    {
        // Act — no override for ClientId=999.
        var result = await _service.DeleteClientSettingsAsync(999);

        // Assert
        result.ShouldBeFalse();
    }

    /// <summary>
    /// GetEffectiveSettingsAsync should return the client override when one exists,
    /// ignoring the company default.
    /// </summary>
    [Fact]
    public async Task GetEffectiveSettingsAsync_ClientOverrideExists_ReturnsClientSettings()
    {
        // Arrange — create a client-specific override with different values.
        _context.ReminderSettings.Add(new ReminderSettings
        {
            ClientId = 1,
            IsEnabled = false,
            MaxReminderLevel = 1,
            GracePeriodDays = 30,
            Levels = new List<ReminderLevel>
            {
                new() { Level = 1, DaysAfterPrevious = 30, FixedFeeCzk = 500 }
            }
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetEffectiveSettingsAsync(1);

        // Assert — should return the client override, not the company default.
        result.ShouldNotBeNull();
        result.ClientId.ShouldBe(1);
        result.IsEnabled.ShouldBeFalse();
        result.MaxReminderLevel.ShouldBe(1);
        result.GracePeriodDays.ShouldBe(30);
    }

    /// <summary>
    /// GetEffectiveSettingsAsync should fall back to the company default
    /// when no client-specific override exists.
    /// </summary>
    [Fact]
    public async Task GetEffectiveSettingsAsync_NoOverride_ReturnsCompanyDefault()
    {
        // Act — no override for ClientId=1, should fall back to company default.
        var result = await _service.GetEffectiveSettingsAsync(1);

        // Assert — should be the seeded company default.
        result.ShouldNotBeNull();
        result.ClientId.ShouldBeNull();
        result.IsEnabled.ShouldBeTrue();
        result.MaxReminderLevel.ShouldBe(3);
        result.Levels.Count.ShouldBe(3);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reminder Query Tests
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GetRemindersPagedAsync on an empty database should return an empty page
    /// with zero total count.
    /// </summary>
    [Fact]
    public async Task GetRemindersPagedAsync_EmptyDb_ReturnsEmptyPage()
    {
        // Arrange — no reminders in the database.
        var filter = new ReminderFilterDto { Page = 1, PageSize = 10 };

        // Act
        var result = await _service.GetRemindersPagedAsync(filter);

        // Assert
        result.ShouldNotBeNull();
        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    /// <summary>
    /// GetByInvoiceAsync should return reminders ordered by level (ascending).
    /// </summary>
    [Fact]
    public async Task GetByInvoiceAsync_WithReminders_ReturnsOrderedByLevel()
    {
        // Arrange — add an invoice and two reminders at different levels.
        var invoice = CreateOverdueInvoice();
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        _context.Reminder.Add(new Reminder
        {
            InvoiceId = 1, ClientId = 1, Level = 2,
            Status = EReminderStatus.Sent, DueDate = DateTime.UtcNow.AddDays(-30),
            ReminderDate = DateTime.UtcNow.AddDays(-10),
            InvoiceAmount = 50000m, TotalCzk = 50050m
        });
        await _context.SaveChangesAsync();

        _context.Reminder.Add(new Reminder
        {
            InvoiceId = 1, ClientId = 1, Level = 1,
            Status = EReminderStatus.Sent, DueDate = DateTime.UtcNow.AddDays(-30),
            ReminderDate = DateTime.UtcNow.AddDays(-20),
            InvoiceAmount = 50000m, TotalCzk = 50000m
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetByInvoiceAsync(1);

        // Assert — should be ordered Level 1, Level 2.
        result.Count.ShouldBe(2);
        result[0].Level.ShouldBe(1);
        result[1].Level.ShouldBe(2);
    }

    /// <summary>
    /// CancelReminderAsync should set the status to Cancelled and preserve notes.
    /// </summary>
    [Fact]
    public async Task CancelReminderAsync_DraftReminder_SetsCancelled()
    {
        // Arrange — add an invoice and a Draft reminder.
        var invoice = CreateOverdueInvoice();
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        _context.Reminder.Add(new Reminder
        {
            Id = 10, InvoiceId = 1, ClientId = 1, Level = 1,
            Status = EReminderStatus.Draft, DueDate = DateTime.UtcNow.AddDays(-30),
            ReminderDate = DateTime.UtcNow, InvoiceAmount = 50000m, TotalCzk = 50000m
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.CancelReminderAsync(10, "Invoice paid offline");

        // Assert
        result.ShouldNotBeNull();
        result.Status.ShouldBe(EReminderStatus.Cancelled);

        // Verify the entity was persisted.
        var entity = await _context.Reminder.FindAsync(10L);
        entity!.Status.ShouldBe(EReminderStatus.Cancelled);
        entity.Notes.ShouldBe("Invoice paid offline");
    }

    /// <summary>
    /// GetDashboardDataAsync should return a correct summary when reminders and
    /// overdue invoices exist in the database.
    /// </summary>
    [Fact]
    public async Task GetDashboardDataAsync_WithData_ReturnsSummary()
    {
        // Arrange — add an overdue invoice and reminders with various statuses.
        var invoice = CreateOverdueInvoice();
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        _context.Reminder.Add(new Reminder
        {
            InvoiceId = 1, ClientId = 1, Level = 1,
            Status = EReminderStatus.Sent, DueDate = DateTime.UtcNow.AddDays(-30),
            ReminderDate = DateTime.UtcNow.AddDays(-5),
            InvoiceAmount = 50000m, FeeCzk = 0, InterestCzk = 0, TotalCzk = 50000m
        });
        await _context.SaveChangesAsync();

        _context.Reminder.Add(new Reminder
        {
            InvoiceId = 1, ClientId = 1, Level = 2,
            Status = EReminderStatus.Draft, DueDate = DateTime.UtcNow.AddDays(-30),
            ReminderDate = DateTime.UtcNow,
            InvoiceAmount = 50000m, FeeCzk = 50, InterestCzk = 0, TotalCzk = 50050m
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetDashboardDataAsync();

        // Assert
        result.ShouldNotBeNull();
        result.DraftCount.ShouldBe(1);
        result.SentCount.ShouldBe(1);
        result.OverdueInvoiceCount.ShouldBeGreaterThanOrEqualTo(1);
        result.TotalOverdueAmount.ShouldBeGreaterThan(0);
        result.RecentReminders.ShouldNotBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dunning Job Tests (ProcessOverdueInvoicesAsync)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ProcessOverdueInvoicesAsync should create a Level 1 reminder for an overdue
    /// invoice that has no existing reminders and is past the grace period.
    /// </summary>
    [Fact]
    public async Task ProcessOverdueInvoicesAsync_OverdueInvoice_CreatesLevel1Reminder()
    {
        // Arrange — invoice is 30 days overdue, grace period is 7 days → should create reminder.
        var invoice = CreateOverdueInvoice();
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        // Act
        var created = await _service.ProcessOverdueInvoicesAsync();

        // Assert — one reminder should have been created.
        created.ShouldBe(1);

        var reminders = await _context.Reminder.Where(r => r.InvoiceId == 1).ToListAsync();
        reminders.Count.ShouldBe(1);
        reminders[0].Level.ShouldBe(1);
        // AutoSendEmail is true in seeded settings, so SendReminderAsync is called automatically.
        // The email mock succeeds → status transitions from Draft to Sent.
        reminders[0].Status.ShouldBe(EReminderStatus.Sent);
        reminders[0].InvoiceAmount.ShouldBe(50000m);
        reminders[0].FeeCzk.ShouldBe(0m); // Level 1 has 0 fee in seed data
    }

    /// <summary>
    /// ProcessOverdueInvoicesAsync should skip invoices when the effective settings
    /// have IsEnabled=false.
    /// </summary>
    [Fact]
    public async Task ProcessOverdueInvoicesAsync_SettingsDisabled_SkipsInvoice()
    {
        // Arrange — disable the company-wide settings.
        var settings = await _context.ReminderSettings
            .FirstAsync(s => s.ClientId == null);
        settings.IsEnabled = false;
        await _context.SaveChangesAsync();

        var invoice = CreateOverdueInvoice();
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        // Act
        var created = await _service.ProcessOverdueInvoicesAsync();

        // Assert — no reminders should be created.
        created.ShouldBe(0);
        var reminderCount = await _context.Reminder.CountAsync();
        reminderCount.ShouldBe(0);
    }

    /// <summary>
    /// ProcessOverdueInvoicesAsync should skip invoices that are overdue but still
    /// within the grace period (DueDate + GracePeriodDays > today).
    /// </summary>
    [Fact]
    public async Task ProcessOverdueInvoicesAsync_NotYetDue_SkipsInvoice()
    {
        // Arrange — invoice due 3 days ago, grace period is 7 days → not yet due for reminder.
        var invoice = CreateOverdueInvoice(daysOverdue: 3);
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        // Act
        var created = await _service.ProcessOverdueInvoicesAsync();

        // Assert — within grace period, no reminder created.
        created.ShouldBe(0);
    }

    /// <summary>
    /// ProcessOverdueInvoicesAsync should not create a new reminder when the maximum
    /// level has already been reached for the invoice.
    /// </summary>
    [Fact]
    public async Task ProcessOverdueInvoicesAsync_MaxLevelReached_SkipsInvoice()
    {
        // Arrange — invoice with all 3 levels already sent.
        var invoice = CreateOverdueInvoice(daysOverdue: 90);
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        // Add 3 existing reminders (max level = 3 in seeded settings).
        for (int level = 1; level <= 3; level++)
        {
            _context.Reminder.Add(new Reminder
            {
                InvoiceId = 1, ClientId = 1, Level = level,
                Status = EReminderStatus.Sent,
                DueDate = DateTime.UtcNow.AddDays(-90),
                ReminderDate = DateTime.UtcNow.AddDays(-90 + (level * 14)),
                InvoiceAmount = 50000m, TotalCzk = 50000m + (level * 50)
            });
            await _context.SaveChangesAsync();
        }

        // Act
        var created = await _service.ProcessOverdueInvoicesAsync();

        // Assert — max level reached, no new reminder.
        created.ShouldBe(0);
        var reminderCount = await _context.Reminder.CountAsync();
        reminderCount.ShouldBe(3); // Still only the 3 we added
    }

    /// <summary>
    /// ProcessOverdueInvoicesAsync should skip invoices with Status=Paid
    /// because only Completed invoices are considered overdue.
    /// </summary>
    [Fact]
    public async Task ProcessOverdueInvoicesAsync_PaidInvoice_Skips()
    {
        // Arrange — paid invoice should not trigger reminders.
        var invoice = CreateOverdueInvoice(status: EInvoiceStatus.Paid);
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        // Act
        var created = await _service.ProcessOverdueInvoicesAsync();

        // Assert — paid invoices are excluded from the overdue query.
        created.ShouldBe(0);
        var reminderCount = await _context.Reminder.CountAsync();
        reminderCount.ShouldBe(0);
    }

    /// <summary>
    /// ProcessOverdueInvoicesAsync should not create reminders for Draft invoices
    /// (only Completed invoices are eligible).
    /// </summary>
    [Fact]
    public async Task ProcessOverdueInvoicesAsync_DraftInvoice_Skips()
    {
        // Arrange — a draft invoice past due date should be ignored.
        var invoice = CreateOverdueInvoice(status: EInvoiceStatus.Draft);
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        // Act
        var created = await _service.ProcessOverdueInvoicesAsync();

        // Assert
        created.ShouldBe(0);
    }

    /// <summary>
    /// ProcessOverdueInvoicesAsync with IncludeInterest enabled should calculate
    /// interest via IInterestCalculator and include it in the reminder total.
    /// </summary>
    [Fact]
    public async Task ProcessOverdueInvoicesAsync_WithInterest_CalculatesInterest()
    {
        // Arrange — enable interest on company settings.
        var settings = await _context.ReminderSettings
            .FirstAsync(s => s.ClientId == null);
        settings.IncludeInterest = true;
        await _context.SaveChangesAsync();

        var invoice = CreateOverdueInvoice(daysOverdue: 30);
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        // Act
        var created = await _service.ProcessOverdueInvoicesAsync();

        // Assert — interest calculator should have been called, and result included in total.
        created.ShouldBe(1);
        _interestCalculator.Received(1).Calculate(
            Arg.Is(50000m), Arg.Any<DateTime>(), Arg.Any<DateTime>());

        var reminder = await _context.Reminder.FirstAsync(r => r.InvoiceId == 1);
        reminder.InterestCzk.ShouldBe(100m); // Mocked return value
        // Total = InvoiceAmount (50000) + Fee (0 for level 1) + Interest (100)
        reminder.TotalCzk.ShouldBe(50100m);
    }

    /// <summary>
    /// ProcessOverdueInvoicesAsync without interest enabled should set InterestCzk to 0.
    /// </summary>
    [Fact]
    public async Task ProcessOverdueInvoicesAsync_WithoutInterest_ZeroInterest()
    {
        // Arrange — IncludeInterest is false by default in seeded settings.
        var invoice = CreateOverdueInvoice(daysOverdue: 30);
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        // Act
        await _service.ProcessOverdueInvoicesAsync();

        // Assert
        var reminder = await _context.Reminder.FirstAsync(r => r.InvoiceId == 1);
        reminder.InterestCzk.ShouldBe(0m);
        // Total = InvoiceAmount (50000) + Fee (0) + Interest (0)
        reminder.TotalCzk.ShouldBe(50000m);
    }

    /// <summary>
    /// ProcessOverdueInvoicesAsync should handle multiple overdue invoices in a single run,
    /// creating one reminder per invoice.
    /// </summary>
    [Fact]
    public async Task ProcessOverdueInvoicesAsync_MultipleInvoices_CreatesMultipleReminders()
    {
        // Arrange — two distinct overdue invoices.
        _context.Invoice.Add(CreateOverdueInvoice(id: 10, daysOverdue: 30));
        await _context.SaveChangesAsync();
        _context.Invoice.Add(CreateOverdueInvoice(id: 11, daysOverdue: 45));
        await _context.SaveChangesAsync();

        // Act
        var created = await _service.ProcessOverdueInvoicesAsync();

        // Assert — should create one level-1 reminder per invoice.
        created.ShouldBe(2);
        var reminders = await _context.Reminder.ToListAsync();
        reminders.Count.ShouldBe(2);
        reminders.ShouldAllBe(r => r.Level == 1);
    }

    /// <summary>
    /// GetRemindersPagedAsync should respect client filter and return only
    /// reminders belonging to the specified client.
    /// </summary>
    [Fact]
    public async Task GetRemindersPagedAsync_ClientFilter_ReturnsFiltered()
    {
        // Arrange — add an invoice and a reminder for client 1.
        var invoice = CreateOverdueInvoice();
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        _context.Reminder.Add(new Reminder
        {
            InvoiceId = 1, ClientId = 1, Level = 1,
            Status = EReminderStatus.Sent, DueDate = DateTime.UtcNow.AddDays(-30),
            ReminderDate = DateTime.UtcNow, InvoiceAmount = 50000m, TotalCzk = 50000m
        });
        await _context.SaveChangesAsync();

        // Act — filter by a different client (should return nothing).
        var filter = new ReminderFilterDto { Page = 1, PageSize = 10, ClientId = 999 };
        var result = await _service.GetRemindersPagedAsync(filter);

        // Assert
        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }
}
