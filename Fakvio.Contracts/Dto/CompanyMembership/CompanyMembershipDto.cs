using System.ComponentModel.DataAnnotations;
using Fakvio.Domain.Enums;
namespace Fakvio.Contracts.Dto.CompanyMembership;

/// <summary>An authenticated identity's company access; never a platform SysAdmin assignment.</summary>
public class CompanyMembershipDto
{
    public long CompanyId { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public EUserRole Role { get; set; }
    public bool IsDefault { get; set; }
    public bool IsProvisioned { get; set; }
}
/// <summary>OperationId is retained across retries so an uncertain response cannot create another company.</summary>
public class CreateMyCompanyDto
{
    public Guid OperationId { get; set; }
    [Required, MaxLength(200)] public string CompanyName { get; set; } = string.Empty;
    [Required, MaxLength(20)] public string RegistrationNumber { get; set; } = string.Empty;
    [MaxLength(30)] public string? TaxNumber { get; set; }
    public bool IsVatPayer { get; set; }
    [MaxLength(200)] public string? Street { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(20)] public string? PostalCode { get; set; }
    [Required, StringLength(2, MinimumLength = 2)] public string Country { get; set; } = "CZ";
}
public class SwitchCompanyDto { public long CompanyId { get; set; } }
public class InviteCompanyMemberDto
{
    [Required, EmailAddress, MaxLength(255)] public string Email { get; set; } = string.Empty;
    public long CompanyId { get; set; }
    public EUserRole Role { get; set; }
}
public class CompanyInvitationDto
{
    public long Id { get; set; }
    public long CompanyId { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public EUserRole Role { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>Shown once to the issuing administrator; only its hash is stored.</summary>
    public string? Token { get; set; }
}
public class AcceptCompanyInvitationDto { [Required, MaxLength(200)] public string Token { get; set; } = string.Empty; }
