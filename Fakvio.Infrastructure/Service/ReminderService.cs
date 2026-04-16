using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of IReminderService — manages payment reminders (dunning).
///
/// Key responsibilities:
/// 1. Settings CRUD — company-wide defaults and per-client overrides (2-tier resolution).
/// 2. Reminder listing — paged queries with filtering by status, client, date range.
/// 3. Manual actions — send draft reminders, cancel reminders.
/// 4. Dunning job — ProcessOverdueInvoicesAsync scans all overdue invoices and creates reminders.
/// 5. Dashboard data — summary statistics for the dashboard widget.
///
/// Uses TenantDbContext for multi-tenant isolation (each tenant = separate PostgreSQL schema).
/// </summary>
public class ReminderService : IReminderService
{
    private readonly TenantDbContext _context;
    private readonly IInterestCalculator _interestCalculator;
    private readonly IEmailService _emailService;
    private readonly IPdfExportService _pdfExportService;
    private readonly IContentTemplateService _contentTemplateService;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(
        TenantDbContext context,
        IInterestCalculator interestCalculator,
        IEmailService emailService,
        IPdfExportService pdfExportService,
        IContentTemplateService contentTemplateService,
        ILogger<ReminderService> logger)
    {
        _context = context;
        _interestCalculator = interestCalculator;
        _emailService = emailService;
        _pdfExportService = pdfExportService;
        _contentTemplateService = contentTemplateService;
        _logger = logger;
    }

    // ─── Mapping helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a Reminder entity (with includes) to ReminderDto.
    /// ZMapper handles direct properties; navigation-derived fields set manually.
    /// </summary>
    private static ReminderDto MapToDto(Reminder entity)
    {
        var dto = entity.ToReminderDto();
        dto.InvoiceNumber = entity.Invoice?.DocumentNumber;
        dto.ClientName = entity.Client?.CompanyName;
        return dto;
    }

    /// <summary>
    /// Maps ReminderSettings entity (with levels) to ReminderSettingsDto.
    /// </summary>
    private static ReminderSettingsDto MapSettingsToDto(ReminderSettings entity)
    {
        return new ReminderSettingsDto
        {
            Id = entity.Id,
            ClientId = entity.ClientId,
            ClientName = entity.Client?.CompanyName,
            IsEnabled = entity.IsEnabled,
            MaxReminderLevel = entity.MaxReminderLevel,
            GracePeriodDays = entity.GracePeriodDays,
            IncludeInterest = entity.IncludeInterest,
            AttachInvoicePdf = entity.AttachInvoicePdf,
            AutoSendEmail = entity.AutoSendEmail,
            Levels = entity.Levels
                .OrderBy(l => l.Level)
                .Select(l => l.ToReminderLevelDto())
                .ToList()
        };
    }

    /// <summary>
    /// Base query for Reminder with necessary includes.
    /// AsNoTracking for read-only operations.
    /// </summary>
    private IQueryable<Reminder> BaseQuery()
    {
        return _context.Reminder
            .AsNoTracking()
            .Include(r => r.Invoice)
            .Include(r => r.Client);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Settings CRUD
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ReminderSettingsDto?> GetEffectiveSettingsAsync(long? clientId, CancellationToken ct = default)
    {
        // If clientId is set, try to find a client-level override first.
        if (clientId.HasValue)
        {
            var clientSettings = await _context.ReminderSettings
                .AsNoTracking()
                .Include(s => s.Client)
                .Include(s => s.Levels)
                .FirstOrDefaultAsync(s => s.ClientId == clientId.Value, ct);

            if (clientSettings != null)
                return MapSettingsToDto(clientSettings);
        }

        // Fall back to company-wide default (ClientId = null).
        var companySettings = await _context.ReminderSettings
            .AsNoTracking()
            .Include(s => s.Levels)
            .FirstOrDefaultAsync(s => s.ClientId == null, ct);

        return companySettings != null ? MapSettingsToDto(companySettings) : null;
    }

    /// <inheritdoc />
    public async Task<ReminderSettingsDto> GetCompanySettingsAsync(CancellationToken ct = default)
    {
        var settings = await _context.ReminderSettings
            .Include(s => s.Levels)
            .FirstOrDefaultAsync(s => s.ClientId == null, ct);

        // Auto-create with defaults if not found.
        if (settings == null)
        {
            settings = new ReminderSettings
            {
                ClientId = null,
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
            };
            _context.ReminderSettings.Add(settings);
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Created default company reminder settings with 3 levels");
        }

        return MapSettingsToDto(settings);
    }

    /// <inheritdoc />
    public async Task<ReminderSettingsDto> UpsertSettingsAsync(UpdateReminderSettingsDto dto, CancellationToken ct = default)
    {
        // Find existing settings for this scope (company default or client override).
        var existing = dto.ClientId.HasValue
            ? await _context.ReminderSettings
                .Include(s => s.Levels)
                .Include(s => s.Client)
                .FirstOrDefaultAsync(s => s.ClientId == dto.ClientId.Value, ct)
            : await _context.ReminderSettings
                .Include(s => s.Levels)
                .FirstOrDefaultAsync(s => s.ClientId == null, ct);

        if (existing == null)
        {
            // Create new settings record.
            existing = new ReminderSettings { ClientId = dto.ClientId };
            _context.ReminderSettings.Add(existing);
        }

        // Update scalar properties.
        existing.IsEnabled = dto.IsEnabled;
        existing.MaxReminderLevel = dto.MaxReminderLevel;
        existing.GracePeriodDays = dto.GracePeriodDays;
        existing.IncludeInterest = dto.IncludeInterest;
        existing.AttachInvoicePdf = dto.AttachInvoicePdf;
        existing.AutoSendEmail = dto.AutoSendEmail;

        // Replace levels entirely — remove old, add new.
        _context.ReminderLevel.RemoveRange(existing.Levels);
        existing.Levels.Clear();

        foreach (var levelDto in dto.Levels.OrderBy(l => l.Level))
        {
            existing.Levels.Add(new ReminderLevel
            {
                Level = levelDto.Level,
                DaysAfterPrevious = levelDto.DaysAfterPrevious,
                Subject = levelDto.Subject,
                FixedFeeCzk = levelDto.FixedFeeCzk,
                EmailTemplateId = levelDto.EmailTemplateId,
                PdfTemplateId = levelDto.PdfTemplateId,
            });
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Upserted reminder settings for {Scope} with {LevelCount} levels",
            dto.ClientId.HasValue ? $"client {dto.ClientId}" : "company default",
            dto.Levels.Count);

        return MapSettingsToDto(existing);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteClientSettingsAsync(long clientId, CancellationToken ct = default)
    {
        var settings = await _context.ReminderSettings
            .Include(s => s.Levels)
            .FirstOrDefaultAsync(s => s.ClientId == clientId, ct);

        if (settings == null)
            return false;

        _context.ReminderLevel.RemoveRange(settings.Levels);
        _context.ReminderSettings.Remove(settings);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted client-level reminder settings for client {ClientId}", clientId);
        return true;
    }

    /// <inheritdoc />
    public async Task<List<ReminderSettingsDto>> GetClientOverridesAsync(CancellationToken ct = default)
    {
        var overrides = await _context.ReminderSettings
            .AsNoTracking()
            .Include(s => s.Client)
            .Include(s => s.Levels)
            .Where(s => s.ClientId != null)
            .OrderBy(s => s.Client!.CompanyName)
            .ToListAsync(ct);

        return overrides.Select(MapSettingsToDto).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Reminder CRUD + actions
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<PagedResult<ReminderDto>> GetRemindersPagedAsync(ReminderFilterDto filter, CancellationToken ct = default)
    {
        var query = BaseQuery();

        // Apply filters.
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLower();
            query = query.Where(r =>
                (r.Invoice.DocumentNumber != null && r.Invoice.DocumentNumber.ToLower().Contains(search)) ||
                r.Client.CompanyName.ToLower().Contains(search) ||
                (r.Notes != null && r.Notes.ToLower().Contains(search)));
        }

        if (filter.Status.HasValue)
            query = query.Where(r => r.Status == filter.Status.Value);

        if (filter.ClientId.HasValue)
            query = query.Where(r => r.ClientId == filter.ClientId.Value);

        if (filter.InvoiceId.HasValue)
            query = query.Where(r => r.InvoiceId == filter.InvoiceId.Value);

        if (filter.Level.HasValue)
            query = query.Where(r => r.Level == filter.Level.Value);

        if (filter.DateFrom.HasValue)
            query = query.Where(r => r.ReminderDate >= filter.DateFrom.Value);

        if (filter.DateTo.HasValue)
            query = query.Where(r => r.ReminderDate <= filter.DateTo.Value);

        // Sorting.
        query = filter.SortBy?.ToLower() switch
        {
            "level" => filter.IsDescending ? query.OrderByDescending(r => r.Level) : query.OrderBy(r => r.Level),
            "status" => filter.IsDescending ? query.OrderByDescending(r => r.Status) : query.OrderBy(r => r.Status),
            "invoiceamount" => filter.IsDescending ? query.OrderByDescending(r => r.InvoiceAmount) : query.OrderBy(r => r.InvoiceAmount),
            "totalczk" => filter.IsDescending ? query.OrderByDescending(r => r.TotalCzk) : query.OrderBy(r => r.TotalCzk),
            "duedate" => filter.IsDescending ? query.OrderByDescending(r => r.DueDate) : query.OrderBy(r => r.DueDate),
            _ => filter.IsDescending ? query.OrderByDescending(r => r.ReminderDate) : query.OrderBy(r => r.ReminderDate),
        };

        var totalCount = await query.CountAsync(ct);
        var items = await query
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct);

        return new PagedResult<ReminderDto>(
            items.Select(MapToDto).ToList(),
            totalCount,
            filter.Page,
            filter.PageSize);
    }

    /// <inheritdoc />
    public async Task<List<ReminderDto>> GetByInvoiceAsync(long invoiceId, CancellationToken ct = default)
    {
        var reminders = await BaseQuery()
            .Where(r => r.InvoiceId == invoiceId)
            .OrderBy(r => r.Level)
            .ToListAsync(ct);

        return reminders.Select(MapToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<ReminderDto?> GetByIdAsync(long reminderId, CancellationToken ct = default)
    {
        var reminder = await BaseQuery()
            .FirstOrDefaultAsync(r => r.Id == reminderId, ct);

        return reminder != null ? MapToDto(reminder) : null;
    }

    /// <inheritdoc />
    public async Task<ReminderDto> SendReminderAsync(long reminderId, CancellationToken ct = default)
    {
        var reminder = await _context.Reminder
            .Include(r => r.Invoice)
                .ThenInclude(i => i.Client)
            .Include(r => r.Invoice)
                .ThenInclude(i => i.Currency)
            .Include(r => r.Client)
                .ThenInclude(c => c.Contact)
            .FirstOrDefaultAsync(r => r.Id == reminderId, ct)
            ?? throw new InvalidOperationException($"Reminder {reminderId} not found");

        if (reminder.Status != EReminderStatus.Draft)
            throw new InvalidOperationException($"Reminder {reminderId} is {reminder.Status}, expected Draft");

        // Find the client's email address.
        var email = reminder.Client.Contact
            .Where(c => c.ContactType == EContactType.Email)
            .Select(c => c.ContactValue)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(email))
        {
            reminder.Status = EReminderStatus.Failed;
            reminder.ErrorMessage = "No email address found for client";
            await _context.SaveChangesAsync(ct);
            _logger.LogWarning("Reminder {ReminderId} failed — no email for client {ClientId}",
                reminderId, reminder.ClientId);
            return MapToDto(reminder);
        }

        try
        {
            // Resolve email template — level-specific or default ReminderEmail.
            var templateDto = await _contentTemplateService.GetDefaultByTypeAsync(
                EContentTemplateType.ReminderEmail, ct);

            // Build placeholder values for the template.
            var placeholders = BuildPlaceholders(reminder);

            // Render the template body with placeholders.
            string body;
            var subject = $"Payment Reminder — Invoice {reminder.Invoice.DocumentNumber}";

            if (templateDto != null)
            {
                var rendered = await _contentTemplateService.RenderTemplateAsync(templateDto.Id, placeholders, ct);
                body = rendered.HtmlBody;
                if (!string.IsNullOrEmpty(rendered.Subject))
                    subject = rendered.Subject;
            }
            else
            {
                body = BuildFallbackEmailBody(reminder);
            }

            // Optionally attach the invoice PDF.
            byte[]? pdfBytes = null;
            if (await ShouldAttachPdf(reminder.ClientId, ct))
            {
                try
                {
                    pdfBytes = await _pdfExportService.GenerateInvoicePdfAsync(reminder.InvoiceId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate PDF attachment for reminder {ReminderId}", reminderId);
                    // Continue without attachment — email is more important.
                }
            }

            // Send the email.
            await _emailService.SendEmailAsync(email, subject, body, pdfBytes,
                pdfBytes != null ? $"Invoice_{reminder.Invoice.DocumentNumber}.pdf" : null, ct);

            // Update status to Sent.
            reminder.Status = EReminderStatus.Sent;
            reminder.SentAt = DateTime.UtcNow;
            reminder.SentToEmail = email;

            _logger.LogInformation("Reminder {ReminderId} sent to {Email} for invoice {InvoiceId}",
                reminderId, email, reminder.InvoiceId);
        }
        catch (Exception ex)
        {
            reminder.Status = EReminderStatus.Failed;
            reminder.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            _logger.LogError(ex, "Failed to send reminder {ReminderId}", reminderId);
        }

        await _context.SaveChangesAsync(ct);
        return MapToDto(reminder);
    }

    /// <inheritdoc />
    public async Task<ReminderDto> CancelReminderAsync(long reminderId, string? notes = null, CancellationToken ct = default)
    {
        var reminder = await _context.Reminder
            .Include(r => r.Invoice)
            .Include(r => r.Client)
            .FirstOrDefaultAsync(r => r.Id == reminderId, ct)
            ?? throw new InvalidOperationException($"Reminder {reminderId} not found");

        reminder.Status = EReminderStatus.Cancelled;
        if (!string.IsNullOrWhiteSpace(notes))
            reminder.Notes = notes;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Reminder {ReminderId} cancelled with notes: {Notes}", reminderId, notes);
        return MapToDto(reminder);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dunning job — ProcessOverdueInvoicesAsync
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<int> ProcessOverdueInvoicesAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var remindersCreated = 0;

        // Get all overdue invoices: Completed status, past due date, not paid.
        var overdueInvoices = await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
                .ThenInclude(c => c.Contact)
            .Include(i => i.Currency)
            .Where(i => i.Status == EInvoiceStatus.Completed
                        && i.DueDate.HasValue
                        && i.DueDate.Value < today)
            .ToListAsync(ct);

        _logger.LogInformation("Found {Count} overdue invoices to process", overdueInvoices.Count);

        foreach (var invoice in overdueInvoices)
        {
            try
            {
                var created = await ProcessSingleInvoiceAsync(invoice, today, ct);
                if (created)
                    remindersCreated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing reminders for invoice {InvoiceId}", invoice.Id);
                // Continue with next invoice — don't let one failure stop the entire batch.
            }
        }

        _logger.LogInformation("Dunning job complete: created {Count} reminders", remindersCreated);
        return remindersCreated;
    }

    /// <summary>
    /// Process a single overdue invoice — check settings, determine next level, create reminder.
    /// Returns true if a reminder was created.
    /// </summary>
    private async Task<bool> ProcessSingleInvoiceAsync(Invoice invoice, DateTime today, CancellationToken ct)
    {
        // 1. Resolve effective settings (client override > company default).
        var settings = await GetEffectiveSettingsEntity(invoice.ClientId, ct);
        if (settings == null || !settings.IsEnabled)
            return false;

        // 2. Get existing reminders for this invoice (ordered by level descending).
        var existingReminders = await _context.Reminder
            .Where(r => r.InvoiceId == invoice.Id)
            .OrderByDescending(r => r.Level)
            .ToListAsync(ct);

        // 3. Determine current level.
        var currentLevel = existingReminders.Count > 0
            ? existingReminders.First().Level + 1
            : 1;

        // 4. Check if max level reached.
        if (currentLevel > settings.MaxReminderLevel)
            return false;

        // 5. Calculate effective date for next level.
        DateTime effectiveDate;
        if (currentLevel == 1)
        {
            // Level 1: DueDate + GracePeriodDays.
            effectiveDate = invoice.DueDate!.Value.AddDays(settings.GracePeriodDays);
        }
        else
        {
            // Level N: last reminder date + DaysAfterPrevious for this level.
            var lastReminder = existingReminders.First();
            var levelConfig = settings.Levels.FirstOrDefault(l => l.Level == currentLevel);
            var daysAfter = levelConfig?.DaysAfterPrevious ?? 7;
            effectiveDate = lastReminder.ReminderDate.AddDays(daysAfter);
        }

        // 6. Check if it's time for the next reminder.
        if (today < effectiveDate)
            return false;

        // 7. Calculate fee from level config.
        var levelDef = settings.Levels.FirstOrDefault(l => l.Level == currentLevel);
        var fee = levelDef?.FixedFeeCzk ?? 0m;

        // 8. Calculate interest if enabled.
        var interest = settings.IncludeInterest
            ? _interestCalculator.Calculate(invoice.TotalWithVat, invoice.DueDate!.Value, today)
            : 0m;

        // 9. Create the reminder record.
        var reminder = new Reminder
        {
            InvoiceId = invoice.Id,
            ClientId = invoice.ClientId!.Value,
            Level = currentLevel,
            Status = EReminderStatus.Draft,
            DueDate = invoice.DueDate!.Value,
            ReminderDate = today,
            InvoiceAmount = invoice.TotalWithVat,
            FeeCzk = fee,
            InterestCzk = interest,
            TotalCzk = invoice.TotalWithVat + fee + interest,
        };

        _context.Reminder.Add(reminder);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created level {Level} reminder for invoice {InvoiceId} (client {ClientId}, amount {Amount})",
            currentLevel, invoice.Id, invoice.ClientId, reminder.TotalCzk);

        // 10. Auto-send email if configured.
        if (settings.AutoSendEmail)
        {
            await SendReminderAsync(reminder.Id, ct);
        }

        return true;
    }

    /// <summary>
    /// Resolve effective ReminderSettings entity (with Levels loaded) for a client.
    /// Client-level override takes priority over company default.
    /// </summary>
    private async Task<ReminderSettings?> GetEffectiveSettingsEntity(long? clientId, CancellationToken ct)
    {
        if (clientId.HasValue)
        {
            var clientSettings = await _context.ReminderSettings
                .Include(s => s.Levels)
                .FirstOrDefaultAsync(s => s.ClientId == clientId.Value, ct);

            if (clientSettings != null)
                return clientSettings;
        }

        return await _context.ReminderSettings
            .Include(s => s.Levels)
            .FirstOrDefaultAsync(s => s.ClientId == null, ct);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Dashboard
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public async Task<ReminderDashboardDto> GetDashboardDataAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var thirtyDaysAgo = today.AddDays(-30);

        var allReminders = await _context.Reminder.AsNoTracking().ToListAsync(ct);

        // Overdue invoices: Completed, past due, not paid.
        var overdueInvoices = await _context.Invoice
            .AsNoTracking()
            .Where(i => i.Status == EInvoiceStatus.Completed
                        && i.DueDate.HasValue
                        && i.DueDate.Value < today)
            .ToListAsync(ct);

        // Recent reminders for the table.
        var recentReminders = await BaseQuery()
            .OrderByDescending(r => r.ReminderDate)
            .Take(5)
            .ToListAsync(ct);

        return new ReminderDashboardDto
        {
            DraftCount = allReminders.Count(r => r.Status == EReminderStatus.Draft),
            SentCount = allReminders.Count(r => r.Status == EReminderStatus.Sent),
            FailedCount = allReminders.Count(r => r.Status == EReminderStatus.Failed),
            OverdueInvoiceCount = overdueInvoices.Count,
            TotalOverdueAmount = overdueInvoices.Sum(i => i.TotalWithVat),
            TotalFees = allReminders.Where(r => r.Status == EReminderStatus.Sent).Sum(r => r.FeeCzk),
            TotalInterest = allReminders.Where(r => r.Status == EReminderStatus.Sent).Sum(r => r.InterestCzk),
            RemindersLast30Days = allReminders.Count(r => r.ReminderDate >= thirtyDaysAgo),
            RecentReminders = recentReminders.Select(MapToDto).ToList(),
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helper methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Check if the invoice PDF should be attached based on effective settings.
    /// </summary>
    private async Task<bool> ShouldAttachPdf(long clientId, CancellationToken ct)
    {
        var settings = await GetEffectiveSettingsEntity(clientId, ct);
        return settings?.AttachInvoicePdf ?? true;
    }

    /// <summary>
    /// Build Handlebars placeholder values for template rendering.
    /// </summary>
    private Dictionary<string, string> BuildPlaceholders(Reminder reminder)
    {
        var daysOverdue = (DateTime.UtcNow.Date - reminder.DueDate).Days;
        var interestRate = _interestCalculator.GetInterestRate(reminder.DueDate.AddDays(1));

        return new Dictionary<string, string>
        {
            ["CompanyName"] = "", // Issuer name — resolved by template service from invoice context.
            ["ClientName"] = reminder.Client?.CompanyName ?? "",
            ["InvoiceNumber"] = reminder.Invoice?.DocumentNumber ?? "",
            ["IssueDate"] = reminder.Invoice?.IssueDate?.ToString("d") ?? "",
            ["DueDate"] = reminder.DueDate.ToString("d"),
            ["DaysOverdue"] = daysOverdue.ToString(),
            ["InvoiceAmount"] = reminder.InvoiceAmount.ToString("N2"),
            ["CurrencyCode"] = reminder.Invoice?.Currency?.Code ?? "CZK",
            ["ReminderLevel"] = reminder.Level.ToString(),
            ["ReminderLevelText"] = GetLevelText(reminder.Level),
            ["Fee"] = reminder.FeeCzk.ToString("N2"),
            ["Interest"] = reminder.InterestCzk.ToString("N2"),
            ["TotalDue"] = reminder.TotalCzk.ToString("N2"),
            ["InterestRate"] = interestRate.ToString("N2"),
            ["PaymentDetails"] = "",
            ["BankAccountNumber"] = reminder.Invoice?.BankAccountNumber ?? "",
            ["IBAN"] = reminder.Invoice?.IBAN ?? "",
            ["VariableSymbol"] = reminder.Invoice?.VariableSymbol ?? "",
        };
    }

    /// <summary>
    /// Get a human-readable level label (used in templates).
    /// </summary>
    private static string GetLevelText(int level) => level switch
    {
        1 => "First Reminder",
        2 => "Second Reminder",
        3 => "Final Warning",
        _ => $"Reminder #{level}"
    };

    /// <summary>
    /// Fallback email body when no template is available.
    /// </summary>
    private static string BuildFallbackEmailBody(Reminder reminder)
    {
        return $"""
            <p>This is a payment reminder for invoice <strong>{reminder.Invoice?.DocumentNumber}</strong>.</p>
            <p>Amount due: <strong>{reminder.TotalCzk:N2} CZK</strong></p>
            <p>Due date: {reminder.DueDate:d}</p>
            <p>Please arrange payment at your earliest convenience.</p>
            """;
    }
}
