using Fakvio.Application.Common.Extensions;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.User;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Service for user management operations
/// Handles CRUD operations with multi-tenant support
/// </summary>
public class UserService : IUserService
{
    private readonly MasterDbContext _context;
    private readonly IAuthService _authService;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly ILogger<UserService> _logger;

    public UserService(
        MasterDbContext context,
        IAuthService authService,
        ITenantProvisioningService provisioningService,
        ILogger<UserService> logger)
    {
        _context = context;
        _authService = authService;
        _provisioningService = provisioningService;
        _logger = logger;
    }

    /// <summary>
    /// Maps a User entity to UserDto using ZMapper v1.1.0.
    /// ZMapper now handles BaseEntity (Id, CreatedAt, UpdatedAt) automatically.
    /// Only navigation-derived and computed properties must be set manually.
    /// </summary>
    private static UserDto MapToDto(User entity)
    {
        var dto = entity.ToUserDto();
        // Navigation-derived and computed properties that ZMapper cannot map
        dto.CompanyName = entity.Company?.CompanyName;
        dto.FullName = entity.FullName; // computed property on entity
        dto.IsExternalLogin = entity.IsExternalLogin; // computed: ExternalProvider != None
        return dto;
    }

    /// <summary>
    /// Gets all users with optional company filtering
    /// SysAdmin can view all users, others only see users from their company
    /// </summary>
    public async Task<List<UserDto>> GetAllUsersAsync(long? companyId = null, bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only list — results are mapped to DTOs
        var query = _context.User
            .AsNoTracking()
            .Include(u => u.Company)
            .AsQueryable();

        // Filter by company if specified
        if (companyId.HasValue)
        {
            query = query.Where(u => u.CompanyId == companyId.Value);
        }

        // Filter by active status
        if (!includeInactive)
        {
            query = query.Where(u => u.IsActive);
        }

        var users = await query
            .OrderBy(u => u.LastName)
            .ThenBy(u => u.FirstName)
            .ToListAsync(cancellationToken);

        return users.Select(u => MapToDto(u)).ToList();
    }

    /// <summary>
    /// Gets paginated, filtered and sorted users
    /// </summary>
    public async Task<PagedResult<UserDto>> GetUsersPagedAsync(
        UserFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only paged query — results are mapped to DTOs
        var query = _context.User
            .AsNoTracking()
            .Include(u => u.Company)
            .AsQueryable();

        // Apply filters
        if (!filter.IncludeInactive)
            query = query.Where(u => u.IsActive);

        if (filter.Role.HasValue)
            query = query.Where(u => u.Role == filter.Role.Value);

        if (filter.CompanyId.HasValue)
            query = query.Where(u => u.CompanyId == filter.CompanyId.Value);

        if (filter.NeverLoggedIn.HasValue)
        {
            if (filter.NeverLoggedIn.Value)
                query = query.Where(u => u.LastLoginAt == null);
            else
                query = query.Where(u => u.LastLoginAt != null);
        }

        // Search filter - search across Email, FirstName, LastName, Company.CompanyName
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.ToLower();
            query = query.Where(u =>
                (u.Email != null && u.Email.ToLower().Contains(search)) ||
                (u.FirstName != null && u.FirstName.ToLower().Contains(search)) ||
                (u.LastName != null && u.LastName.ToLower().Contains(search)) ||
                (u.Company != null && u.Company.CompanyName != null && u.Company.CompanyName.ToLower().Contains(search)));
        }

        // Apply sorting
        var validSortFields = new[] { "Email", "FirstName", "LastName", "Role", "IsActive", "LastLoginAt", "CreatedAt", "UpdatedAt" };
        var sortBy = !string.IsNullOrWhiteSpace(filter.SortBy) && validSortFields.Contains(filter.SortBy, StringComparer.OrdinalIgnoreCase)
            ? filter.SortBy : "LastName";

        query = query.ApplySorting(sortBy, filter.IsDescending);

        // Get paged results
        var pagedResult = await query.ToPagedResultAsync(filter.Page, filter.PageSize, cancellationToken);

        // Map to DTOs
        return new PagedResult<UserDto>(
            pagedResult.Items.Select(u => MapToDto(u)).ToList(),
            pagedResult.TotalCount,
            pagedResult.PageNumber,
            pagedResult.PageSize);
    }

    /// <summary>
    /// Gets a specific user by ID
    /// </summary>
    public async Task<UserDto?> GetUserByIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        var user = await _context.User
            .AsNoTracking()
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        return user == null ? null : MapToDto(user);
    }

    /// <summary>
    /// Gets user by email address
    /// Used for login and email uniqueness validation
    /// </summary>
    public async Task<UserDto?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        // AsNoTracking: read-only lookup — result is mapped to DTO
        var user = await _context.User
            .AsNoTracking()
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        return user == null ? null : MapToDto(user);
    }

    /// <summary>
    /// Creates a new user
    /// Validates email uniqueness and company existence
    /// </summary>
    public async Task<UserDto> CreateUserAsync(CreateUserDto createDto, CancellationToken cancellationToken = default)
    {
        // Validate email uniqueness
        var existingUser = await _context.User
            .FirstOrDefaultAsync(u => u.Email == createDto.Email, cancellationToken);

        if (existingUser != null)
        {
            throw new InvalidOperationException($"User with email '{createDto.Email}' already exists.");
        }

        // Validate company exists if CompanyId is provided
        if (createDto.CompanyId.HasValue)
        {
            var companyExists = await _context.Client
                .AnyAsync(c => c.Id == createDto.CompanyId.Value && c.IsIssuer, cancellationToken);

            if (!companyExists)
            {
                throw new InvalidOperationException($"Company with ID {createDto.CompanyId.Value} not found or is not an issuer.");
            }
        }
        else if (createDto.Role != EUserRole.SysAdmin)
        {
            // Non-SysAdmin users must have a CompanyId
            throw new InvalidOperationException("Only SysAdmin users can have null CompanyId.");
        }

        // Create user entity
        var user = new User
        {
            Email = createDto.Email,
            PasswordHash = _authService.HashPassword(createDto.Password),
            FirstName = createDto.FirstName,
            LastName = createDto.LastName,
            Role = createDto.Role,
            CompanyId = createDto.CompanyId,
            IsActive = createDto.IsActive,
            CreatedAt = DateTime.UtcNow
        };

        _context.User.Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        // Reload with company data
        await _context.Entry(user)
            .Reference(u => u.Company)
            .LoadAsync(cancellationToken);

        return MapToDto(user);
    }

    /// <summary>
    /// Updates an existing user
    /// Only updates fields that are provided (not null)
    /// </summary>
    public async Task<UserDto?> UpdateUserAsync(long userId, UpdateUserDto updateDto, CancellationToken cancellationToken = default)
    {
        var user = await _context.User
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user == null)
        {
            return null;
        }

        // Validate email uniqueness if changing email
        if (updateDto.Email != null && updateDto.Email != user.Email)
        {
            var emailExists = await _context.User
                .AnyAsync(u => u.Email == updateDto.Email && u.Id != userId, cancellationToken);

            if (emailExists)
            {
                throw new InvalidOperationException($"User with email '{updateDto.Email}' already exists.");
            }

            user.Email = updateDto.Email;
        }

        // Update first name if provided
        if (updateDto.FirstName != null)
        {
            user.FirstName = updateDto.FirstName;
        }

        // Update last name if provided
        if (updateDto.LastName != null)
        {
            user.LastName = updateDto.LastName;
        }

        // Update role if provided
        if (updateDto.Role.HasValue)
        {
            user.Role = updateDto.Role.Value;
        }

        // Update company if provided
        if (updateDto.CompanyId.HasValue)
        {
            // Validate company exists
            var companyExists = await _context.Client
                .AnyAsync(c => c.Id == updateDto.CompanyId.Value && c.IsIssuer, cancellationToken);

            if (!companyExists)
            {
                throw new InvalidOperationException($"Company with ID {updateDto.CompanyId.Value} not found or is not an issuer.");
            }

            user.CompanyId = updateDto.CompanyId.Value;
        }

        // Update active status if provided
        if (updateDto.IsActive.HasValue)
        {
            user.IsActive = updateDto.IsActive.Value;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        // Reload company data
        await _context.Entry(user)
            .Reference(u => u.Company)
            .LoadAsync(cancellationToken);

        return MapToDto(user);
    }

    /// <summary>
    /// Changes user password
    /// Validates current password before allowing change
    /// </summary>
    public async Task<bool> ChangePasswordAsync(long userId, ChangePasswordDto changePasswordDto, CancellationToken cancellationToken = default)
    {
        var user = await _context.User.FindAsync(new object[] { userId }, cancellationToken);

        if (user == null)
        {
            return false;
        }

        // OAuth users don't have a local password — they can't change it
        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            throw new InvalidOperationException("Cannot change password for OAuth users.");
        }

        // Verify current password
        if (!_authService.VerifyPassword(changePasswordDto.CurrentPassword, user.PasswordHash))
        {
            throw new InvalidOperationException("Current password is incorrect.");
        }

        // Hash and update new password
        user.PasswordHash = _authService.HashPassword(changePasswordDto.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Admin password reset — sets a new password without requiring the current one.
    /// Only Admin and SysAdmin should call this (authorization enforced in controller).
    /// </summary>
    public async Task<bool> AdminResetPasswordAsync(long userId, string newPassword, CancellationToken cancellationToken = default)
    {
        var user = await _context.User.FindAsync(new object[] { userId }, cancellationToken);

        if (user == null)
        {
            return false;
        }

        // Hash and set the new password directly — no current password check
        user.PasswordHash = _authService.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Soft deletes a user by setting IsActive = false
    /// Preserves user data for audit trail
    /// </summary>
    public async Task<bool> DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.User.FindAsync(new object[] { userId }, cancellationToken);

        if (user == null)
        {
            return false;
        }

        // Soft delete - set IsActive to false
        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Invites a new user by creating their account with a temporary random password
    /// and generating a unique invitation token (GUID). The token expires in 48 hours.
    /// The user must click the invitation link and set their own password before they can log in.
    /// </summary>
    public async Task<UserDto> InviteUserAsync(InviteUserDto dto, CancellationToken cancellationToken = default)
    {
        // Validate email uniqueness — same check as in CreateUserAsync
        var existingUser = await _context.User
            .FirstOrDefaultAsync(u => u.Email == dto.Email, cancellationToken);

        if (existingUser != null)
        {
            throw new InvalidOperationException($"User with email '{dto.Email}' already exists.");
        }

        // Validate company exists if CompanyId is provided
        if (dto.CompanyId.HasValue)
        {
            var companyExists = await _context.Client
                .AnyAsync(c => c.Id == dto.CompanyId.Value && c.IsIssuer, cancellationToken);

            if (!companyExists)
            {
                throw new InvalidOperationException($"Company with ID {dto.CompanyId.Value} not found or is not an issuer.");
            }
        }
        else if (dto.Role != EUserRole.SysAdmin)
        {
            // Non-SysAdmin users must have a CompanyId
            throw new InvalidOperationException("Only SysAdmin users can have null CompanyId.");
        }

        // Generate a random temporary password hash — the user will never use this directly.
        // It's just a placeholder so the PasswordHash column isn't empty.
        var temporaryPasswordHash = _authService.HashPassword(Guid.NewGuid().ToString());

        // Generate unique invitation token (GUID) and set 48-hour expiration
        var invitationToken = Guid.NewGuid().ToString();
        var expiresAt = DateTime.UtcNow.AddHours(48);

        // Create the user entity with invitation fields set
        var user = new User
        {
            Email = dto.Email,
            PasswordHash = temporaryPasswordHash,
            FirstName = dto.FirstName,
            LastName = dto.LastName,
            Role = dto.Role,
            CompanyId = dto.CompanyId,
            IsActive = true,
            InvitationToken = invitationToken,
            InvitationTokenExpiresAt = expiresAt,
            IsInvitationPending = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.User.Add(user);
        await _context.SaveChangesAsync(cancellationToken);

        // Reload with company navigation property
        await _context.Entry(user)
            .Reference(u => u.Company)
            .LoadAsync(cancellationToken);

        return MapToDto(user);
    }

    /// <summary>
    /// Sets the password for an invited user using their invitation token.
    /// Finds the user by token, validates expiration, hashes the new password,
    /// and clears all invitation-related fields so the user can log in normally.
    /// </summary>
    public async Task<bool> SetPasswordAsync(SetPasswordDto dto, CancellationToken cancellationToken = default)
    {
        // Find user by invitation token
        var user = await _context.User
            .FirstOrDefaultAsync(u => u.InvitationToken == dto.Token, cancellationToken);

        // Token not found — invalid
        if (user == null)
        {
            return false;
        }

        // Token expired — no longer valid
        if (user.InvitationTokenExpiresAt.HasValue && user.InvitationTokenExpiresAt.Value < DateTime.UtcNow)
        {
            return false;
        }

        // Hash the new password, verify email, and clear invitation fields.
        // For self-registered users, setting the password also verifies their email
        // (the token was sent to their email address, so reaching this point proves ownership).
        user.PasswordHash = _authService.HashPassword(dto.NewPassword);
        user.InvitationToken = null;
        user.InvitationTokenExpiresAt = null;
        user.IsInvitationPending = false;
        user.IsEmailVerified = true; // Email is verified by setting password via emailed link
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        // Trigger tenant database provisioning after password is set (for self-registered users).
        // Provisioning creates the PostgreSQL schema, applies migrations, copies code tables, etc.
        if (user.CompanyId.HasValue)
        {
            try
            {
                _logger.LogInformation(
                    "Starting tenant provisioning for CompanyId={CompanyId} (triggered by password set, user={Email})",
                    user.CompanyId.Value, user.Email);

                await _provisioningService.ProvisionTenantAsync(user.CompanyId.Value, cancellationToken);

                _logger.LogInformation(
                    "Tenant provisioning SUCCEEDED for CompanyId={CompanyId}, user={Email}",
                    user.CompanyId.Value, user.Email);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already provisioned"))
            {
                // Already provisioned — this is fine (idempotent, e.g. SysAdmin provisioned first).
                _logger.LogInformation(
                    "Tenant already provisioned for CompanyId={CompanyId} — skipping",
                    user.CompanyId.Value);
            }
            catch (Exception ex)
            {
                // Provisioning failure should not fail password setting — can be retried by SysAdmin.
                _logger.LogError(ex,
                    "Tenant provisioning FAILED for CompanyId={CompanyId}, user={Email}: {Error}",
                    user.CompanyId.Value, user.Email, ex.Message);
            }
        }

        return true;
    }

    /// <summary>
    /// Validates whether an invitation token exists and has not expired.
    /// Used by the frontend to verify the token before showing the password form.
    /// </summary>
    public async Task<bool> ValidateInvitationTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var user = await _context.User
            .FirstOrDefaultAsync(u => u.InvitationToken == token, cancellationToken);

        // Token not found
        if (user == null)
        {
            return false;
        }

        // Token expired
        if (user.InvitationTokenExpiresAt.HasValue && user.InvitationTokenExpiresAt.Value < DateTime.UtcNow)
        {
            return false;
        }

        return true;
    }

}
