using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.CompanyMembership;

/// <summary>SysAdmin view includes revoked memberships so access can be reviewed and restored deliberately.</summary>
public sealed class ManagedCompanyMembershipDto : CompanyMembershipDto
{
    public bool IsActive { get; set; }
    public bool IsCompanyActive { get; set; }
}

/// <summary>Both values are mandatory: an incomplete request must never silently revoke access.</summary>
public sealed class UpdateCompanyMembershipDto
{
    [Required] public EUserRole? Role { get; set; }
    [Required] public bool? IsActive { get; set; }
}
