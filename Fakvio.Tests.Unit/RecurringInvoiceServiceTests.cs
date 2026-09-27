using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.RecurringInvoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for RecurringInvoiceService — schedule CRUD/validation (N4.1) and the generation
/// cycle (N4.2), including the idempotence guarantee (two RunCycleAsync calls covering the
/// same due period must create only one invoice).
/// </summary>
public class RecurringInvoiceServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly RecurringInvoiceService _service;
    private readonly INotificationService _notificationService;
    private readonly IEmailService _emailService;
    private const long TemplateId = 100; // InvoiceTemplate is a TPH row in the Invoice table.
    private const long ClientId = 1;
    private const long IssuerId = 2;

    public RecurringInvoiceServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            // The InMemory provider does not support transactions — RunCycleAsync wraps each
            // schedule in one, so silence the (harmless, in-memory-only) warning instead of
            // letting it fail the test run.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new TenantDbContext(options);

        var numberSequence = Substitute.For<INumberSequenceService>();
        var counter = 0;
        numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"INV2026{++counter:D3}");

        var invoiceService = new InvoiceService(
            _context, numberSequence, Substitute.For<ITenantReadinessService>(), Substitute.For<ILogger<InvoiceService>>());
        var templateService = new InvoiceTemplateService(_context, invoiceService, Substitute.For<ILogger<InvoiceTemplateService>>());
        _notificationService = Substitute.For<INotificationService>();
        _emailService = Substitute.For<IEmailService>();

        _service = new RecurringInvoiceService(
            _context, templateService, _notificationService, _emailService, Substitute.For<ILogger<RecurringInvoiceService>>());

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void SeedTestData()
    {
        _context.Client.Add(new Client { Id = ClientId, CompanyName = "Customer A", RegistrationNumber = "REG001", IsIssuer = false, IsActive = true });
        _context.Client.Add(new Client { Id = IssuerId, CompanyName = "My Company", RegistrationNumber = "REG002", IsIssuer = true, IsActive = true, IsVatPayer = false });
        _context.SaveChanges();

        _context.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true });
        _context.SaveChanges();

        _context.Set<InvoiceTemplate>().Add(new InvoiceTemplate
        {
            Id = TemplateId,
            Name = "Monthly hosting",
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Draft,
            DocumentNumber = "TEMPLATE",
            IssueDate = DateTime.UtcNow,
            DueDate = DateTime.UtcNow,
            TaxableSupplyDate = DateTime.UtcNow,
            IssuerId = IssuerId,
            CurrencyId = 1,
            IsActive = true,
            DueDateOffsetDays = 14,
            InvoiceItem = new List<InvoiceItem>
            {
                new() { OrderIndex = 1, Description = "Hosting", Quantity = 1, Unit = "month", UnitPrice = 500 }
            }
        });
        _context.SaveChanges();
    }

    private CreateRecurringInvoiceScheduleDto ValidMonthlyDto(DateTimeOffset? start = null) => new()
    {
        TemplateId = TemplateId,
        ClientId = ClientId,
        Frequency = ERecurrenceFrequency.Monthly,
        IntervalCount = 1,
        DayOfMonth = 15,
        StartDate = start ?? new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero),
    };

    // ── Create/validation ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidMonthlySchedule_Persists()
    {
        var result = await _service.CreateAsync(ValidMonthlyDto());

        result.Id.ShouldBeGreaterThan(0);
        result.TemplateName.ShouldBe("Monthly hosting");
        result.ClientName.ShouldBe("Customer A");
        result.NextRunAt.ShouldBe(new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero));
        result.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateAsync_UnknownTemplate_Throws()
    {
        var dto = ValidMonthlyDto();
        dto.TemplateId = 999;

        await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateAsync(dto));
    }

    [Fact]
    public async Task CreateAsync_UnknownClient_Throws()
    {
        var dto = ValidMonthlyDto();
        dto.ClientId = 999;

        await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateAsync(dto));
    }

    [Fact]
    public async Task CreateAsync_WeeklyWithoutDayOfWeek_Throws()
    {
        var dto = ValidMonthlyDto();
        dto.Frequency = ERecurrenceFrequency.Weekly;
        dto.DayOfMonth = null;
        dto.DayOfWeek = null;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateAsync(dto));
        ex.Message.ShouldContain("DayOfWeek");
    }

    [Fact]
    public async Task CreateAsync_MonthlyWithoutDayOfMonth_Throws()
    {
        var dto = ValidMonthlyDto();
        dto.DayOfMonth = null;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateAsync(dto));
        ex.Message.ShouldContain("DayOfMonth");
    }

    [Fact]
    public async Task CreateAsync_EndDateBeforeStart_Throws()
    {
        var dto = ValidMonthlyDto();
        dto.EndDate = dto.StartDate.AddDays(-1);

        await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateAsync(dto));
    }

    [Fact]
    public async Task CreateAsync_IntervalCountZero_Throws()
    {
        var dto = ValidMonthlyDto();
        dto.IntervalCount = 0;

        await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateAsync(dto));
    }

    // ── Update / SetActive / Delete ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ChangesAutoSendOnly_LeavesRestUnchanged()
    {
        var created = await _service.CreateAsync(ValidMonthlyDto());

        var updated = await _service.UpdateAsync(created.Id, new UpdateRecurringInvoiceScheduleDto { AutoSend = true });

        updated.AutoSend.ShouldBeTrue();
        updated.Frequency.ShouldBe(ERecurrenceFrequency.Monthly);
        updated.DayOfMonth.ShouldBe(15);
    }

    [Fact]
    public async Task SetActiveAsync_False_PausesSchedule()
    {
        var created = await _service.CreateAsync(ValidMonthlyDto());

        var paused = await _service.SetActiveAsync(created.Id, false);

        paused.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NeverRun_HardDeletes()
    {
        var created = await _service.CreateAsync(ValidMonthlyDto());

        await _service.DeleteAsync(created.Id);

        (await _service.GetByIdAsync(created.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_AlreadyRunOnce_DeactivatesInsteadOfDeleting()
    {
        var created = await _service.CreateAsync(ValidMonthlyDto());
        await _service.RunCycleAsync(companyId: 1, nowUtc: new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero));

        await _service.DeleteAsync(created.Id);

        var schedule = await _service.GetByIdAsync(created.Id);
        schedule.ShouldNotBeNull();
        schedule!.IsActive.ShouldBeFalse();
    }

    // ── RunCycleAsync — generation cycle ────────────────────────────────────────

    [Fact]
    public async Task RunCycleAsync_DueSchedule_GeneratesOneCompletedInvoiceAndAdvancesNextRunAt()
    {
        var created = await _service.CreateAsync(ValidMonthlyDto());
        var now = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

        var count = await _service.RunCycleAsync(companyId: 1, nowUtc: now);

        count.ShouldBe(1);
        var invoice = _context.Invoice.Single(i => i.ClientId == ClientId && i.Id != TemplateId);
        invoice.Status.ShouldBe(EInvoiceStatus.Completed); // Owner decision: issued straight away.
        invoice.IssueDate.ShouldBe(now.UtcDateTime.Date);

        var schedule = await _service.GetByIdAsync(created.Id);
        schedule!.OccurrenceCount.ShouldBe(1);
        schedule.NextRunAt.ShouldBe(new DateTimeOffset(2026, 2, 15, 8, 0, 0, TimeSpan.Zero));
        schedule.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task RunCycleAsync_CalledTwiceForSamePeriod_GeneratesOnlyOneInvoice()
    {
        await _service.CreateAsync(ValidMonthlyDto());
        var now = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);

        var firstRun = await _service.RunCycleAsync(companyId: 1, nowUtc: now);
        var secondRun = await _service.RunCycleAsync(companyId: 1, nowUtc: now);

        firstRun.ShouldBe(1);
        secondRun.ShouldBe(0); // NextRunAt already moved past `now` — nothing due anymore.
        _context.Invoice.Count(i => i.ClientId == ClientId && i.DocumentType == EDocumentType.Invoice && i.Id != TemplateId)
            .ShouldBe(1);
    }

    [Fact]
    public async Task RunCycleAsync_MissedPeriod_IssuesOnPlannedDateNotNow()
    {
        // Schedule was due a week ago (simulated downtime) — the invoice must be dated on the
        // ORIGINAL planned date, and only ONE invoice is produced for the missed period.
        var plannedDate = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        await _service.CreateAsync(ValidMonthlyDto(plannedDate));
        var now = plannedDate.AddDays(7);

        var count = await _service.RunCycleAsync(companyId: 1, nowUtc: now);

        count.ShouldBe(1);
        var invoice = _context.Invoice.Single(i => i.ClientId == ClientId && i.Id != TemplateId);
        invoice.IssueDate.ShouldBe(plannedDate.UtcDateTime.Date);
    }

    [Fact]
    public async Task RunCycleAsync_MaxOccurrencesReached_DeactivatesSchedule()
    {
        var dto = ValidMonthlyDto();
        dto.MaxOccurrences = 1;
        await _service.CreateAsync(dto);
        var now = dto.StartDate;

        await _service.RunCycleAsync(companyId: 1, nowUtc: now);

        var schedule = (await _service.GetAllAsync()).Single();
        schedule.IsActive.ShouldBeFalse();
        schedule.OccurrenceCount.ShouldBe(1);
    }

    [Fact]
    public async Task RunCycleAsync_EndDateReachedByNextRun_DeactivatesSchedule()
    {
        var dto = ValidMonthlyDto();
        dto.EndDate = dto.StartDate.AddMonths(1); // Next run (StartDate+1mo) is exactly >= EndDate.
        await _service.CreateAsync(dto);

        await _service.RunCycleAsync(companyId: 1, nowUtc: dto.StartDate);

        var schedule = (await _service.GetAllAsync()).Single();
        schedule.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task RunCycleAsync_InactiveSchedule_IsSkipped()
    {
        var created = await _service.CreateAsync(ValidMonthlyDto());
        await _service.SetActiveAsync(created.Id, false);

        var count = await _service.RunCycleAsync(companyId: 1, nowUtc: (await _service.GetByIdAsync(created.Id))!.NextRunAt);

        count.ShouldBe(0);
    }

    [Fact]
    public async Task RunCycleAsync_TemplateGenerationFails_RecordsLastErrorAndDoesNotAdvanceNextRunAt()
    {
        var created = await _service.CreateAsync(ValidMonthlyDto());

        // Deactivate the template out from under the schedule — CreateInvoiceFromTemplateAsync
        // throws InvalidOperationException("... is not active"), simulating a runtime failure.
        var template = _context.Set<InvoiceTemplate>().Single(t => t.Id == TemplateId);
        template.IsActive = false;
        await _context.SaveChangesAsync();

        var originalNextRunAt = created.NextRunAt;
        var count = await _service.RunCycleAsync(companyId: 1, nowUtc: originalNextRunAt);

        count.ShouldBe(0);
        var schedule = await _service.GetByIdAsync(created.Id);
        schedule!.LastError.ShouldNotBeNullOrEmpty();
        schedule.NextRunAt.ShouldBe(originalNextRunAt); // Not advanced — retried next cycle.
        schedule.OccurrenceCount.ShouldBe(0);

        await _notificationService.Received(1).CreateForAllUsersAsync(
            ENotificationType.RecurringInvoiceFailed,
            Arg.Any<string>(), Arg.Any<string>(), created.Id, "RecurringInvoiceSchedule", 1, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// PR #444 finding 4: a permanently broken schedule (template deactivated/deleted) fails on
    /// every hourly cycle. Users must be notified once per error state, not every hour — but
    /// again after the schedule recovered and then fails anew.
    /// </summary>
    [Fact]
    public async Task RunCycleAsync_SameFailureOnConsecutiveCycles_NotifiesOnlyOnce_UntilRecovered()
    {
        var created = await _service.CreateAsync(ValidMonthlyDto());
        var template = _context.Set<InvoiceTemplate>().Single(t => t.Id == TemplateId);
        template.IsActive = false;
        await _context.SaveChangesAsync();

        var now = created.NextRunAt;
        await _service.RunCycleAsync(companyId: 1, nowUtc: now);
        await _service.RunCycleAsync(companyId: 1, nowUtc: now.AddHours(1));
        await _service.RunCycleAsync(companyId: 1, nowUtc: now.AddHours(2));

        await _notificationService.Received(1).CreateForAllUsersAsync(
            ENotificationType.RecurringInvoiceFailed,
            Arg.Any<string>(), Arg.Any<string>(), created.Id, "RecurringInvoiceSchedule", 1, Arg.Any<CancellationToken>());

        // Recover (success clears LastError), then break again → a fresh notification.
        _context.ChangeTracker.Clear();
        _context.Set<InvoiceTemplate>().Single(t => t.Id == TemplateId).IsActive = true;
        await _context.SaveChangesAsync();
        (await _service.RunCycleAsync(companyId: 1, nowUtc: now.AddHours(3))).ShouldBe(1);

        _context.ChangeTracker.Clear();
        _context.Set<InvoiceTemplate>().Single(t => t.Id == TemplateId).IsActive = false;
        await _context.SaveChangesAsync();
        await _service.RunCycleAsync(companyId: 1, nowUtc: now.AddMonths(2));

        await _notificationService.Received(2).CreateForAllUsersAsync(
            ENotificationType.RecurringInvoiceFailed,
            Arg.Any<string>(), Arg.Any<string>(), created.Id, "RecurringInvoiceSchedule", 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCycleAsync_OneScheduleFails_OtherSchedulesStillRun()
    {
        var failingDto = ValidMonthlyDto();
        var failing = await _service.CreateAsync(failingDto);
        var template = _context.Set<InvoiceTemplate>().Single(t => t.Id == TemplateId);
        template.IsActive = false;
        await _context.SaveChangesAsync();

        // Second, healthy template + schedule sharing the same due date.
        _context.Set<InvoiceTemplate>().Add(new InvoiceTemplate
        {
            Id = 200, Name = "Other", DocumentType = EDocumentType.Invoice, Status = EInvoiceStatus.Draft,
            DocumentNumber = "TEMPLATE2", IssueDate = DateTime.UtcNow, DueDate = DateTime.UtcNow,
            TaxableSupplyDate = DateTime.UtcNow, IssuerId = IssuerId, CurrencyId = 1, IsActive = true,
            DueDateOffsetDays = 14,
            InvoiceItem = new List<InvoiceItem> { new() { OrderIndex = 1, Description = "X", Quantity = 1, Unit = "pcs", UnitPrice = 100 } }
        });
        await _context.SaveChangesAsync();
        await _service.CreateAsync(new CreateRecurringInvoiceScheduleDto
        {
            TemplateId = 200, ClientId = ClientId, Frequency = ERecurrenceFrequency.Monthly,
            IntervalCount = 1, DayOfMonth = 15, StartDate = failingDto.StartDate,
        });

        var count = await _service.RunCycleAsync(companyId: 1, nowUtc: failingDto.StartDate);

        count.ShouldBe(1); // Only the healthy schedule succeeded.
        var failedSchedule = await _service.GetByIdAsync(failing.Id);
        failedSchedule!.LastError.ShouldNotBeNullOrEmpty();
    }

    // ── AutoSend (N4.5) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunCycleAsync_AutoSendWithClientEmail_SendsInvoiceEmail()
    {
        _context.Contact.Add(new Contact { ClientId = ClientId, ContactType = EContactType.Email, ContactValue = "client@example.com" });
        await _context.SaveChangesAsync();

        var dto = ValidMonthlyDto();
        dto.AutoSend = true;
        var created = await _service.CreateAsync(dto);

        await _service.RunCycleAsync(companyId: 1, nowUtc: dto.StartDate);

        await _emailService.Received(1).SendInvoiceEmailAsync(Arg.Any<long>(), "client@example.com", Arg.Any<CancellationToken>());
        var schedule = await _service.GetByIdAsync(created.Id);
        schedule!.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task RunCycleAsync_AutoSendClientHasNoEmail_InvoiceStillGenerated_LastErrorSet_NextRunAtAdvances()
    {
        // ClientId has no Contact rows at all in this test.
        var dto = ValidMonthlyDto();
        dto.AutoSend = true;
        var created = await _service.CreateAsync(dto);

        var count = await _service.RunCycleAsync(companyId: 1, nowUtc: dto.StartDate);

        count.ShouldBe(1); // The invoice WAS generated — only the e-mail step failed.
        await _emailService.DidNotReceive().SendInvoiceEmailAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        var schedule = await _service.GetByIdAsync(created.Id);
        schedule!.LastError.ShouldContain("no e-mail address");
        schedule.NextRunAt.ShouldBe(new DateTimeOffset(2026, 2, 15, 8, 0, 0, TimeSpan.Zero)); // Advanced — not retried.
        schedule.OccurrenceCount.ShouldBe(1);
        _context.Invoice.Count(i => i.ClientId == ClientId && i.Id != TemplateId).ShouldBe(1); // Not duplicated.
    }

    [Fact]
    public async Task RunCycleAsync_AutoSendSmtpFailure_InvoiceStillGenerated_LastErrorSet_NextRunAtAdvances()
    {
        _context.Contact.Add(new Contact { ClientId = ClientId, ContactType = EContactType.Email, ContactValue = "client@example.com" });
        await _context.SaveChangesAsync();
        _emailService.SendInvoiceEmailAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("SMTP connection refused")));

        var dto = ValidMonthlyDto();
        dto.AutoSend = true;
        var created = await _service.CreateAsync(dto);

        var count = await _service.RunCycleAsync(companyId: 1, nowUtc: dto.StartDate);

        count.ShouldBe(1);
        var schedule = await _service.GetByIdAsync(created.Id);
        schedule!.LastError.ShouldContain("SMTP connection refused");
        schedule.NextRunAt.ShouldBe(new DateTimeOffset(2026, 2, 15, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task RunCycleAsync_AutoSendFalse_DoesNotCallEmailService()
    {
        var dto = ValidMonthlyDto(); // AutoSend defaults to false.
        await _service.CreateAsync(dto);

        await _service.RunCycleAsync(companyId: 1, nowUtc: dto.StartDate);

        await _emailService.DidNotReceive().SendInvoiceEmailAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Regression tests for Codex review findings ──────────────────────────────

    [Fact]
    public async Task RunCycleAsync_FailureAfterScheduleFieldsMutated_DoesNotLeakPartialMutationIntoLastError()
    {
        // Reproduces Codex review finding #1: force RecurrenceCalculator.Next to throw AFTER
        // RunOneScheduleAsync already mutated OccurrenceCount/LastRunAt on the tracked entity,
        // by corrupting the schedule's DayOfMonth directly (bypassing service validation).
        var created = await _service.CreateAsync(ValidMonthlyDto());
        var entity = await _context.RecurringInvoiceSchedule.SingleAsync(s => s.Id == created.Id);
        entity.DayOfMonth = null;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var count = await _service.RunCycleAsync(companyId: 1, nowUtc: created.NextRunAt);

        count.ShouldBe(0);
        var schedule = await _service.GetByIdAsync(created.Id);
        schedule!.OccurrenceCount.ShouldBe(0); // Must NOT leak the in-memory ++ from the failed attempt.
        schedule.LastRunAt.ShouldBeNull();
        schedule.NextRunAt.ShouldBe(created.NextRunAt); // Not advanced.
        schedule.LastError.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunCycleAsync_UnexpectedExceptionType_SanitizesLastError()
    {
        // A non-domain exception type (anything other than InvalidOperationException /
        // TenantNotReadyException) must never leak ex.Message verbatim into LastError —
        // it is broadcast through the API and to every user's notifications (Codex finding #6).
        var templateService = Substitute.For<IInvoiceTemplateService>();
        templateService
            .CreateInvoiceFromTemplateAsync(Arg.Any<long>(), Arg.Any<Fakvio.Contracts.Dto.InvoiceTemplate.CreateInvoiceFromTemplateDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Fakvio.Contracts.Dto.Invoice.InvoiceDto>(
                new TimeoutException("Host=db-prod-01.internal;Port=5432 connection timed out")));

        var service = new RecurringInvoiceService(
            _context, templateService, _notificationService, _emailService, Substitute.For<ILogger<RecurringInvoiceService>>());
        var created = await service.CreateAsync(ValidMonthlyDto());

        await service.RunCycleAsync(companyId: 1, nowUtc: created.NextRunAt);

        var schedule = await service.GetByIdAsync(created.Id);
        schedule!.LastError.ShouldNotContain("db-prod-01");
        schedule.LastError.ShouldContain(nameof(TimeoutException));
    }

    [Fact]
    public async Task SetActiveAsync_ResumeAfterMaxOccurrencesReached_Throws()
    {
        var dto = ValidMonthlyDto();
        dto.MaxOccurrences = 1;
        var created = await _service.CreateAsync(dto);
        await _service.RunCycleAsync(companyId: 1, nowUtc: dto.StartDate); // Deactivates itself.

        await Should.ThrowAsync<InvalidOperationException>(() => _service.SetActiveAsync(created.Id, true));
    }

    [Fact]
    public async Task SetActiveAsync_ResumeAfterEndDateReached_Throws()
    {
        var dto = ValidMonthlyDto();
        dto.EndDate = dto.StartDate.AddMonths(1);
        var created = await _service.CreateAsync(dto);
        await _service.RunCycleAsync(companyId: 1, nowUtc: dto.StartDate); // Deactivates itself.

        await Should.ThrowAsync<InvalidOperationException>(() => _service.SetActiveAsync(created.Id, true));
    }

    [Fact]
    public async Task UpdateAsync_LoweringMaxOccurrencesBelowCurrentCount_AutoDeactivates()
    {
        var created = await _service.CreateAsync(ValidMonthlyDto());
        await _service.RunCycleAsync(companyId: 1, nowUtc: created.NextRunAt); // OccurrenceCount -> 1.

        var updated = await _service.UpdateAsync(created.Id, new UpdateRecurringInvoiceScheduleDto { MaxOccurrences = 1 });

        updated.IsActive.ShouldBeFalse();
    }
}
