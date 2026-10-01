using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fakvio.Infrastructure.Service;

/// <summary>Creates additional companies for one identity and accepts password-independent invitations.</summary>
public sealed class CompanyMembershipService(MasterDbContext db, ICurrentUserService currentUser,
    ITenantResolver tenant, ITenantProvisioningService provisioning, IHttpContextAccessor http,
    ILogger<CompanyMembershipService> logger, NpgsqlDataSource? dataSource = null) : ICompanyMembershipService
{
    public async Task<List<CompanyMembershipDto>> ListAsync(CancellationToken ct = default)
    {
        var user = await RequireUser(ct);
        var query = db.UserCompanyMembership.Where(m => m.UserId == user.Id && m.IsActive && m.Company.IsActive && m.Company.IsIssuer);
        var principal = http.HttpContext?.User;
        if (principal?.HasClaim(c => c.Type == ApiKeyAuthenticationDefaults.ScopeClaimType) == true)
        {
            if (!long.TryParse(principal.FindFirstValue(ApiKeyAuthenticationDefaults.KeyIdClaimType), out var keyId))
                throw new UnauthorizedAccessException();
            var key = await db.ApiKey.AsNoTracking().SingleOrDefaultAsync(k => k.Id == keyId && k.UserId == user.Id, ct)
                ?? throw new UnauthorizedAccessException();
            var grants = key.OAuthGrantId.HasValue ? new[] { key.CompanyId ?? 0 } : key.AllowedCompanyIds;
            query = query.Where(m => grants.Contains(m.CompanyId));
        }
        return await Project(query, user.CompanyId).OrderBy(m => m.CompanyName).ThenBy(m => m.CompanyId).ToListAsync(ct);
    }

    public async Task<CompanyMembershipDto> CreateAsync(CreateMyCompanyDto input, CancellationToken ct = default)
    {
        var user = await RequireUser(ct);
        if (!user.IsEmailVerified || user.IsInvitationPending) throw new UnauthorizedAccessException("Verify your email before creating a company.");
        Validator.ValidateObject(input, new ValidationContext(input), true);
        if (input.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(input.CompanyName) || string.IsNullOrWhiteSpace(input.RegistrationNumber))
            throw new ValidationException("OperationId, company name and registration number are required.");
        var existing = await db.UserCompanyMembership.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == user.Id && m.CreationOperationId == input.OperationId, ct);
        if (existing is not null) return await ReadOwned(user, existing.CompanyId, ct);
        long companyId;
        await using (var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null)
        {
            try
            {
                var company = new Client { CompanyName = input.CompanyName.Trim(), RegistrationNumber = input.RegistrationNumber.Trim(),
                    TaxNumber = input.TaxNumber?.Trim(), IsIssuer = true, IsActive = true, IsVatPayer = input.IsVatPayer };
                if (!string.IsNullOrWhiteSpace(input.Street) || !string.IsNullOrWhiteSpace(input.City))
                    company.Address.Add(new Address { Street = input.Street?.Trim() ?? "", City = input.City?.Trim() ?? "",
                        PostalCode = input.PostalCode?.Trim() ?? "", Country = input.Country.ToUpperInvariant(), AddressType = EAddressType.Primary, IsPrimary = true });
                db.Client.Add(company);
                db.UserCompanyMembership.Add(new UserCompanyMembership { UserId = user.Id, Company = company, Role = EUserRole.Admin,
                    IsActive = true, CreationOperationId = input.OperationId });
                await db.SaveChangesAsync(ct);
                companyId = company.Id;
                db.CompanySystemSettings.Add(new CompanySystemSettings { CompanyId = companyId, SchemaName = $"tenant_{companyId}", IsActive = true });
                await db.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                if (transaction is not null) await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                existing = await db.UserCompanyMembership.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == user.Id && m.CreationOperationId == input.OperationId, ct);
                if (existing is not null) return await ReadOwned(user, existing.CompanyId, ct);
                throw new ValidationException("A company with this registration number already exists.");
            }
        }
        // Provisioning is recoverable, outside the identity transaction. A failure returns
        // the same durable company, with IsProvisioned=false and an explicit retry endpoint.
        await TryProvision(companyId, ct);
        return await ReadOwned(user, companyId, ct);
    }

    public async Task<CompanyMembershipDto> RetryProvisioningAsync(long companyId, CancellationToken ct = default)
    {
        await RequireMachineCompanyGrant(companyId, ct);
        var user = await RequireUser(ct);
        if (!await db.UserCompanyMembership.AnyAsync(m => m.UserId == user.Id && m.CompanyId == companyId
            && m.IsActive && m.Role == EUserRole.Admin && m.CreationOperationId != null && m.Company.IsActive && m.Company.IsIssuer, ct))
            throw new UnauthorizedAccessException("Only the creator may retry this company operation.");
        await TryProvision(companyId, ct);
        return await ReadOwned(user, companyId, ct);
    }

    private async Task RequireMachineCompanyGrant(long companyId, CancellationToken ct)
    {
        var principal = http.HttpContext?.User;
        if (principal?.HasClaim(c => c.Type == ApiKeyAuthenticationDefaults.ScopeClaimType) != true) return;
        if (!long.TryParse(principal.FindFirstValue(ApiKeyAuthenticationDefaults.KeyIdClaimType), out var keyId))
            throw new UnauthorizedAccessException();
        var key = await db.ApiKey.AsNoTracking().SingleOrDefaultAsync(k => k.Id == keyId && k.UserId == currentUser.GetCurrentUserId(), ct);
        if (key is null || (key.OAuthGrantId.HasValue ? key.CompanyId != companyId : !key.AllowedCompanyIds.Contains(companyId)))
            throw new UnauthorizedAccessException("The credential does not grant access to this company.");
    }
    private async Task TryProvision(long companyId, CancellationToken ct)
    {
        try
        {
            // Different API replicas can retry the same operation. A session lock prevents
            // overlapping schema creation/seeding and is released if the process crashes.
            await using var companyLock = db.Database.IsRelational()
                ? await AdvisoryLock.TryAcquireAsync(dataSource ?? throw new InvalidOperationException("PostgreSQL data source is required."), 0x434f4d5000000000L ^ companyId, ct)
                : null;
            if (db.Database.IsRelational() && companyLock is null) return;
            if (await db.CompanySystemSettings.AnyAsync(s => s.CompanyId == companyId && s.IsProvisioned, ct)) return;
            await provisioning.ProvisionTenantAsync(companyId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogError(ex, "Company {CompanyId} provisioning failed; retry is available", companyId); }
    }
    public async Task<CompanyInvitationDto> InviteAsync(InviteCompanyMemberDto input, CancellationToken ct = default)
    {
        var actor = await RequireUser(ct);
        if (actor.Role != EUserRole.SysAdmin || !tenant.IsSysAdmin()) throw new UnauthorizedAccessException();
        Validator.ValidateObject(input, new ValidationContext(input), true);
        if (input.Role is not (EUserRole.User or EUserRole.Admin)) throw new ValidationException("Invalid company role.");
        var email = input.Email.Trim().ToLowerInvariant();
        var invited = await db.User.AsNoTracking().SingleOrDefaultAsync(u => u.Email.ToLower() == email && u.IsActive, ct)
            ?? throw new ValidationException("An active existing account is required. Use the new-user invitation for a new email.");
        if (invited.Role == EUserRole.SysAdmin) throw new ValidationException("SysAdmin does not require company membership.");
        var company = await db.Client.AsNoTracking().SingleOrDefaultAsync(c => c.Id == input.CompanyId && c.IsIssuer && c.IsActive, ct)
            ?? throw new ValidationException("Company is not available.");
        if (await db.UserCompanyMembership.AnyAsync(m => m.UserId == invited.Id && m.CompanyId == company.Id && m.IsActive, ct))
            throw new ValidationException("This account already has access to the company.");
        await using var invitationTransaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.IsRelational())
        {
            // Serialize resends without blocking the KEY SHARE lock used by membership foreign keys.
            // FOR UPDATE would invert the accept path lock order and could deadlock.
            await db.User.FromSqlInterpolated($"SELECT * FROM \"User\" WHERE \"Id\" = {invited.Id} FOR NO KEY UPDATE").LoadAsync(ct);
        }
        var pending = await db.CompanyMembershipInvitation.Where(i => i.UserId == invited.Id && i.CompanyId == company.Id && i.ConsumedAt == null).ToListAsync(ct);
        foreach (var previous in pending) previous.ConsumedAt = DateTime.UtcNow;
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var invitation = new CompanyMembershipInvitation { UserId = invited.Id, CompanyId = company.Id, Role = input.Role,
            TokenHash = Hash(raw), ExpiresAt = DateTime.UtcNow.AddHours(48) };
        db.CompanyMembershipInvitation.Add(invitation); await db.SaveChangesAsync(ct);
        if (invitationTransaction is not null) await invitationTransaction.CommitAsync(ct);
        return new() { Id = invitation.Id, CompanyId = company.Id, CompanyName = company.CompanyName, Email = invited.Email,
            Role = invitation.Role, ExpiresAt = invitation.ExpiresAt, Token = raw };
    }

    public async Task<CompanyMembershipDto> AcceptAsync(string token, CancellationToken ct = default)
    {
        var user = await RequireUser(ct);
        if (http.HttpContext?.User.HasClaim(c => c.Type == ApiKeyAuthenticationDefaults.ScopeClaimType) == true)
            throw new UnauthorizedAccessException("Invitation acceptance requires an interactive session.");
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) throw new ValidationException("Invalid invitation.");
        var hash = Hash(token);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        // Lock the token row before checking it, so concurrent acceptance cannot race the membership insert.
        var invitation = db.Database.IsRelational()
            ? await db.CompanyMembershipInvitation.FromSqlInterpolated($"SELECT * FROM \"CompanyMembershipInvitation\" WHERE \"TokenHash\" = {hash} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await db.CompanyMembershipInvitation.SingleOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null || invitation.ConsumedAt != null || PostgresDateTime.ToUtc(invitation.ExpiresAt) <= DateTime.UtcNow)
            throw new ValidationException("Invitation is invalid or expired.");
        if (invitation.UserId != user.Id) throw new UnauthorizedAccessException("Sign in with the invited account.");
        if (!await db.Client.AnyAsync(c => c.Id == invitation.CompanyId && c.IsIssuer && c.IsActive, ct)) throw new UnauthorizedAccessException();
        if (invitation.Role is not (EUserRole.User or EUserRole.Admin)) throw new ValidationException("Invalid company role.");
        var membership = await db.UserCompanyMembership.SingleOrDefaultAsync(m => m.UserId == user.Id && m.CompanyId == invitation.CompanyId, ct);
        if (membership is null) db.UserCompanyMembership.Add(new() { UserId = user.Id, CompanyId = invitation.CompanyId, Role = invitation.Role });
        else if (!membership.IsActive) { membership.IsActive = true; membership.Role = invitation.Role; }
        invitation.ConsumedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { throw new ValidationException("Invitation has already been accepted."); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ValidationException("Company access changed concurrently. Retry invitation acceptance."); }
        return await ReadOwned(user, invitation.CompanyId, ct);
    }

    private IQueryable<CompanyMembershipDto> Project(IQueryable<UserCompanyMembership> query, long? defaultCompany)
        => query.Select(m => new CompanyMembershipDto { CompanyId = m.CompanyId, CompanyName = m.Company.CompanyName, Role = m.Role,
            IsDefault = m.CompanyId == defaultCompany,
            IsProvisioned = db.CompanySystemSettings.Any(s => s.CompanyId == m.CompanyId && s.IsActive && s.IsProvisioned) });
    private async Task<CompanyMembershipDto> ReadOwned(User user, long companyId, CancellationToken ct)
        => await Project(db.UserCompanyMembership.Where(m => m.UserId == user.Id && m.CompanyId == companyId
            && m.IsActive && m.Company.IsActive && m.Company.IsIssuer), user.CompanyId).SingleOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("Company membership is not active.");
    private async Task<User> RequireUser(CancellationToken ct)
        => await db.User.AsNoTracking().SingleOrDefaultAsync(u => u.Id == currentUser.GetCurrentUserId() && u.IsActive, ct)
            ?? throw new UnauthorizedAccessException("An active authenticated identity is required.");
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
