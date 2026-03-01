using Fakvio.Contracts.Common.Pagination;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.User;

/// <summary>
/// Filter parameters for users list
/// </summary>
public class UserFilterDto : PaginationParams
{
    /// <summary>
    /// Search in email, first name, last name, company name
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Filter by role
    /// </summary>
    public EUserRole? Role { get; set; }

    /// <summary>
    /// Filter by company ID
    /// </summary>
    public long? CompanyId { get; set; }

    /// <summary>
    /// Include inactive users
    /// </summary>
    public bool IncludeInactive { get; set; } = false;

    /// <summary>
    /// Filter users who never logged in
    /// </summary>
    public bool? NeverLoggedIn { get; set; }
}
