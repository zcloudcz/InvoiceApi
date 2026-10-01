using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Feedback;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Stores feedback centrally. Every personal query includes BOTH owner and company,
/// even though MasterDb does not have the automatic tenant isolation of TenantDb.
/// </summary>
public sealed class FeedbackService(MasterDbContext db, ICurrentUserService currentUser,
    ITenantResolver tenant, ILogger<FeedbackService> logger) : IFeedbackService
{
    // EF translates this expression into SELECT columns, without loading tracked entities.
    private static readonly Expression<Func<FeedbackReport, FeedbackDto>> Projection = r => new FeedbackDto
    {
        Id = r.Id, Type = r.Type, Subject = r.Subject, Description = r.Description,
        Page = r.Page, AppVersion = r.AppVersion, UserId = r.UserId, CompanyId = r.CompanyId,
        Status = r.Status, PublicResponse = r.PublicResponse, CreatedAt = r.CreatedAt, UpdatedAt = r.UpdatedAt
    };

    public async Task<FeedbackDto> CreateAsync(CreateFeedbackDto input, CancellationToken ct = default)
    {
        var (userId, companyId) = await RequireOwnerAsync(ct);
        if (!Enum.IsDefined(input.Type)) throw new ValidationException("Invalid feedback type.");
        var report = new FeedbackReport
        {
            Type = input.Type, Subject = RequiredText(input.Subject, 200, "Subject"),
            Description = RequiredText(input.Description, 10000, "Description"),
            Page = SafePage(input.Page), AppVersion = OptionalText(input.AppVersion, 100, "AppVersion"),
            UserId = userId, CompanyId = companyId, Status = EFeedbackStatus.New
        };
        db.FeedbackReport.Add(report);
        await db.SaveChangesAsync(ct);
        // Report text and page context can contain private information: log only the identifier.
        logger.LogInformation("Feedback report {FeedbackId} created", report.Id);
        return await db.FeedbackReport.Where(r => r.Id == report.Id).Select(Projection).SingleAsync(ct);
    }

    public async Task<PagedResult<FeedbackDto>> ListMineAsync(FeedbackFilterDto filter, CancellationToken ct = default)
    {
        var (userId, companyId) = await RequireOwnerAsync(ct);
        return await PageAsync(db.FeedbackReport.Where(r => r.UserId == userId && r.CompanyId == companyId), filter, ct);
    }

    public async Task<FeedbackDto?> GetMineAsync(long id, CancellationToken ct = default)
    {
        var (userId, companyId) = await RequireOwnerAsync(ct);
        return await db.FeedbackReport.Where(r => r.Id == id && r.UserId == userId && r.CompanyId == companyId)
            .Select(Projection).SingleOrDefaultAsync(ct);
    }

    public async Task<PagedResult<FeedbackDto>> ListAllAsync(FeedbackFilterDto filter, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        return await PageAsync(db.FeedbackReport, filter, ct);
    }

    public async Task<FeedbackDto?> GetAnyAsync(long id, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        return await db.FeedbackReport.Where(r => r.Id == id).Select(Projection).SingleOrDefaultAsync(ct);
    }

    public async Task<FeedbackDto?> UpdateAsync(long id, UpdateFeedbackStatusDto input, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);
        if (!Enum.IsDefined(input.Status)) throw new ValidationException("Invalid feedback status.");
        var response = OptionalText(input.PublicResponse, 10000, "PublicResponse");
        var report = await db.FeedbackReport.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (report is null) return null;
        report.Status = input.Status;
        report.PublicResponse = response;
        await db.SaveChangesAsync(ct); // MasterDbContext fills UpdatedAt and UpdatedByUserId.
        logger.LogInformation("Feedback report {FeedbackId} status changed to {Status}", id, input.Status);
        return await db.FeedbackReport.Where(r => r.Id == id).Select(Projection).SingleAsync(ct);
    }

    private async Task<(long UserId, long CompanyId)> RequireOwnerAsync(CancellationToken ct)
    {
        var user = await RequireUserAsync(ct);
        var companyId = tenant.GetCurrentCompanyId();
        // A stale token, forged tenant header, or unbound machine credential must not grant access.
        if (companyId is not > 0 ||
            (!(tenant.IsSysAdmin() && user.Role == EUserRole.SysAdmin) && !await db.UserCompanyMembership.AnyAsync(m => m.UserId == user.Id && m.CompanyId == companyId && m.IsActive, ct)) ||
            !await db.Client.AnyAsync(c => c.Id == companyId && c.IsIssuer && c.IsActive, ct))
            throw new UnauthorizedAccessException("An authorized company context is required.");
        return (user.Id, companyId.Value);
    }

    private async Task<User> RequireUserAsync(CancellationToken ct)
    {
        var userId = currentUser.GetCurrentUserId();
        if (userId is not > 0) throw new UnauthorizedAccessException("An authenticated user is required.");
        return await db.User.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, ct)
            ?? throw new UnauthorizedAccessException("An active user is required.");
    }

    private async Task RequireAdminAsync(CancellationToken ct)
    {
        var user = await RequireUserAsync(ct);
        if (!tenant.IsSysAdmin() || user.Role != EUserRole.SysAdmin)
            throw new UnauthorizedAccessException("SysAdmin access is required.");
    }

    private static async Task<PagedResult<FeedbackDto>> PageAsync(IQueryable<FeedbackReport> query, FeedbackFilterDto filter, CancellationToken ct)
    {
        // Validate before multiplying so a huge page cannot overflow into a negative SQL offset.
        if (filter.Page < 1 || filter.PageSize is < 1 or > 100 || (long)(filter.Page - 1) * filter.PageSize > int.MaxValue)
            throw new ValidationException("Page must be positive and page size must be between 1 and 100.");
        if (filter.Type.HasValue && !Enum.IsDefined(filter.Type.Value)) throw new ValidationException("Invalid feedback type.");
        if (filter.Status.HasValue && !Enum.IsDefined(filter.Status.Value)) throw new ValidationException("Invalid feedback status.");
        if (filter.Type.HasValue) query = query.Where(r => r.Type == filter.Type.Value);
        if (filter.Status.HasValue) query = query.Where(r => r.Status == filter.Status.Value);
        var count = await query.CountAsync(ct);
        var items = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).Select(Projection).ToListAsync(ct);
        return new(items, count, filter.Page, filter.PageSize);
    }

    private static string RequiredText(string? text, int limit, string field)
        => OptionalText(text, limit, field) ?? throw new ValidationException($"{field} is required.");

    private static string? OptionalText(string? text, int limit, string field)
    {
        var value = text?.Trim();
        if (value?.Length > limit) throw new ValidationException($"{field} must not exceed {limit} characters.");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? SafePage(string? page)
    {
        if (string.IsNullOrEmpty(page)) return null;
        if (page.Length > 2048 || !page.StartsWith('/') || page.StartsWith("//") || page.Contains('\\') || page.Any(char.IsControl))
            throw new ValidationException("Page must be a local path without backslashes or control characters.");
        // Query/fragment can carry credentials. Never persist either, even if a client sends them.
        var safe = page.Split('?', '#')[0];
        var decoded = Uri.UnescapeDataString(safe);
        if (decoded.StartsWith("//") || decoded.Contains('\\') || decoded.Any(char.IsControl))
            throw new ValidationException("Page contains an unsafe escaped path.");
        return safe;
    }
}
