using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Per-user UI preferences stored in the MASTER database (user identity lives there,
/// and preferences must follow the user across tenants/impersonation).
/// Lazy upsert: no row until the user saves something; reads fall back to defaults.
/// </summary>
public class UserPreferencesService : IUserPreferencesService
{
    /// <summary>
    /// Allowed grid page sizes — must match FakvioGrid pager options.
    /// </summary>
    private static readonly int[] AllowedPageSizes = { 10, 25, 50, 100 };

    private readonly MasterDbContext _context;
    private readonly ILogger<UserPreferencesService> _logger;

    public UserPreferencesService(MasterDbContext context, ILogger<UserPreferencesService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UserPreferencesDto> GetAsync(long userId, CancellationToken ct = default)
    {
        var entity = await _context.UserPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        // No row yet → defaults (row is only created on first save)
        return entity is null ? new UserPreferencesDto() : MapToDto(entity);
    }

    /// <inheritdoc />
    public async Task<UserPreferencesDto> UpdateAsync(long userId, UserPreferencesDto dto, CancellationToken ct = default)
    {
        if (!AllowedPageSizes.Contains(dto.DefaultGridPageSize))
        {
            throw new ArgumentException(
                $"DefaultGridPageSize must be one of: {string.Join(", ", AllowedPageSizes)}.");
        }

        var entity = await _context.UserPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (entity is null)
        {
            entity = new UserPreferences { UserId = userId };
            _context.UserPreferences.Add(entity);
        }

        entity.DefaultGridPageSize = dto.DefaultGridPageSize;
        entity.DashboardLayoutJson = dto.DashboardLayout is null
            ? null
            : JsonSerializer.Serialize(dto.DashboardLayout);
        entity.SetupWizardDismissedAt = dto.SetupWizardDismissedAt;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} preferences updated (grid page size {PageSize})",
            userId, dto.DefaultGridPageSize);

        return MapToDto(entity);
    }

    private static UserPreferencesDto MapToDto(UserPreferences entity) => new()
    {
        DefaultGridPageSize = entity.DefaultGridPageSize,
        DashboardLayout = string.IsNullOrWhiteSpace(entity.DashboardLayoutJson)
            ? null
            // A corrupt/old-shape JSON value must not take the dashboard down — same
            // "decoration, never crash the host page" reasoning as ReadinessApiService.
            : TryDeserializeLayout(entity.DashboardLayoutJson),
        SetupWizardDismissedAt = entity.SetupWizardDismissedAt
    };

    private static List<DashboardWidgetLayoutItemDto>? TryDeserializeLayout(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DashboardWidgetLayoutItemDto>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
