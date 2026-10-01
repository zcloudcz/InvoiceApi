using Fakvio.Contracts.Dto.CompanyMembership;
namespace Fakvio.Application.Service;

/// <summary>Membership operations for the current authenticated identity.</summary>
public interface ICompanyMembershipService
{
    Task<List<ManagedCompanyMembershipDto>> ListForUserAsync(long userId, CancellationToken ct = default);
    Task<ManagedCompanyMembershipDto> UpdateForUserAsync(long userId, long companyId, UpdateCompanyMembershipDto input, CancellationToken ct = default);
    Task<List<CompanyMembershipDto>> ListAsync(CancellationToken ct = default);
    Task<CompanyMembershipDto> CreateAsync(CreateMyCompanyDto input, CancellationToken ct = default);
    Task<CompanyMembershipDto> RetryProvisioningAsync(long companyId, CancellationToken ct = default);
    Task<CompanyInvitationDto> InviteAsync(InviteCompanyMemberDto input, CancellationToken ct = default);
    Task<CompanyMembershipDto> AcceptAsync(string token, CancellationToken ct = default);
}
