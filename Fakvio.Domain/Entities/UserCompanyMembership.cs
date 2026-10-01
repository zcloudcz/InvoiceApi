using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;
namespace Fakvio.Domain.Entities;

/// <summary>Live company access. Deactivation immediately invalidates existing sessions and machine access.</summary>
public class UserCompanyMembership : BaseEntity
{
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public long CompanyId { get; set; }
    public Client Company { get; set; } = null!;
    public EUserRole Role { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? CreationOperationId { get; set; }
}
/// <summary>An existing account invitation, independent of password reset and login credentials.</summary>
public class CompanyMembershipInvitation : BaseEntity
{
    public long UserId { get; set; }
    public long CompanyId { get; set; }
    public EUserRole Role { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}
