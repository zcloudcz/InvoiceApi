using System.Security.Cryptography;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ApiKeyEntity = Fakvio.Domain.Entities.ApiKey;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// CRUD over the current user's API keys, stored in the MASTER database
/// (see <see cref="ApiKeyEntity"/> for why master and not a tenant schema).
///
/// The raw key never leaves this class except in the create response: it is
/// generated here, hashed here, and only the hash reaches the database.
/// </summary>
public class ApiKeyService : IApiKeyService
{
    /// <summary>
    /// Prefix of every raw key. Load-bearing, not decoration:
    /// the authentication scheme selector distinguishes an API key from a JWT by it
    /// (a JWT always starts with "eyJ"), and secret scanners can pattern-match it.
    /// </summary>
    private const string KeyPrefixLiteral = "fak_live_";

    /// <summary>
    /// Number of raw random bytes behind a key. 32 bytes = 256 bits of entropy.
    /// </summary>
    private const int KeyEntropyBytes = 32;

    /// <summary>
    /// How much of the raw key is kept in clear for display and log correlation.
    /// Short enough to be useless as a credential.
    /// </summary>
    private const int DisplayPrefixLength = 12;

    /// <summary>
    /// Must stay in sync with the varchar(100) column (see MasterDbContext.ConfigureApiKey)
    /// and with the [StringLength(100)] annotation on <see cref="CreateApiKeyDto"/>.
    /// </summary>
    private const int NameMaxLength = 100;

    private readonly MasterDbContext _context;
    private readonly ILogger<ApiKeyService> _logger;

    public ApiKeyService(MasterDbContext context, ILogger<ApiKeyService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Hashes a raw API key for storage and lookup.
    ///
    /// SHA-256 — a deliberate deviation from the BCrypt(wf12) rule that DEVGUIDE §2.1
    /// sets for passwords. The reasons the rule exists do not apply here:
    ///
    /// 1. Adaptive slow hashing defends low-entropy human input against offline brute
    ///    force. An API key is 32 bytes from a CSPRNG — brute force is infeasible at
    ///    any hash speed, so slowing the hash buys nothing.
    /// 2. BCrypt wf12 costs ~100 ms of CPU per verify, and a key is verified on every
    ///    single request. One AI turn issues dozens of MCP tool calls; on a Functions
    ///    consumption plan that is both a real bill and a trivial DoS amplifier.
    /// 3. BCrypt is salted, therefore non-deterministic, therefore not indexable.
    ///    SHA-256 is deterministic, so KeyHash carries a unique index and a lookup is
    ///    one indexed equality query instead of a full scan with a verify per row.
    ///
    /// If we ever store something lower-entropy in this column, this reasoning breaks
    /// and the algorithm has to change with it.
    /// </summary>
    public static string ComputeHash(string rawKey)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey)));

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKeyDto>> GetAllAsync(long userId, CancellationToken ct = default)
    {
        return await _context.ApiKey
            .AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyDto
            {
                Id = k.Id,
                Name = k.Name,
                KeyPrefix = k.KeyPrefix,
                Scopes = k.Scopes,
                CompanyId = k.CompanyId,
                AllowedCompanyIds = k.AllowedCompanyIds,
                CreatedAt = k.CreatedAt,
                ExpiresAt = k.ExpiresAt,
                LastUsedAt = k.LastUsedAt,
                RevokedAt = k.RevokedAt
            })
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<CreatedApiKeyDto> CreateAsync(long userId, CreateApiKeyDto dto, CancellationToken ct = default)
    {
        // Fail fast at the boundary — the controller only checks model annotations,
        // and the Functions host has no model validation at all.
        var name = (dto.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            throw new ArgumentException("API key name is required.");

        // Without this the value would reach the varchar(100) column and PostgreSQL
        // would answer 22001 ("value too long") — a 500 instead of a 400 on the
        // Functions host, which has no model validation to stop it earlier.
        if (name.Length > NameMaxLength)
            throw new ArgumentException($"API key name must be at most {NameMaxLength} characters.");

        var scopes = NormalizeScopes(dto.Scopes);

        if (dto.ExpiresAt is not null && dto.ExpiresAt <= DateTime.UtcNow)
            throw new ArgumentException("ExpiresAt must be in the future.");

        var user = await _context.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new ArgumentException("User does not exist.");
        if (!user.IsActive)
            throw new ArgumentException("Inactive users cannot create credentials.");

        var companyId = dto.CompanyId ?? user.CompanyId;
        var companyIds = (dto.AllowedCompanyIds ?? []).Distinct().Order().ToArray();
        if (companyIds.Length > 100 || companyIds.Any(id => id <= 0))
            throw new ArgumentException("Select at most 100 valid companies.");

        // Omission grants one company, never all of the user's present or future memberships.
        if (companyIds.Length == 0 && companyId.HasValue)
            companyIds = [companyId.Value];
        if (companyId.HasValue && !companyIds.Contains(companyId.Value))
            throw new ArgumentException("The default company must be included in the company grants.");
        if (user.Role != EUserRole.SysAdmin)
        {
            if (!companyId.HasValue || companyIds.Length == 0)
                throw new ArgumentException("A company is required.");
            var permitted = await _context.UserCompanyMembership.AsNoTracking()
                .CountAsync(m => m.UserId == userId && m.IsActive &&
                    m.Company.IsActive && m.Company.IsIssuer &&
                    (m.Role == EUserRole.User || m.Role == EUserRole.Admin) && companyIds.Contains(m.CompanyId), ct);
            if (permitted != companyIds.Length)
                throw new ArgumentException("Every selected company must have an active membership.");
        }

        var rawKey = GenerateRawKey();

        var entity = new ApiKeyEntity
        {
            UserId = userId,
            CompanyId = companyId,
            AllowedCompanyIds = companyIds,
            Name = name,
            KeyPrefix = rawKey[..DisplayPrefixLength],
            KeyHash = ComputeHash(rawKey),
            Scopes = scopes,
            ExpiresAt = dto.ExpiresAt
        };

        _context.ApiKey.Add(entity);
        await _context.SaveChangesAsync(ct);

        // Prefix only — logging the key (or even its hash) would put a usable
        // credential into the log store.
        _logger.LogInformation(
            "API key {KeyPrefix} created for user {UserId} with scopes {Scopes}, expires {ExpiresAt}",
            entity.KeyPrefix, userId, entity.Scopes, entity.ExpiresAt);

        return new CreatedApiKeyDto
        {
            Id = entity.Id,
            Name = entity.Name,
            KeyPrefix = entity.KeyPrefix,
            Scopes = entity.Scopes,
            CompanyId = entity.CompanyId,
            AllowedCompanyIds = entity.AllowedCompanyIds,
            CreatedAt = entity.CreatedAt,
            ExpiresAt = entity.ExpiresAt,
            Key = rawKey
        };
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(long userId, long id, CancellationToken ct = default)
    {
        // UserId is part of the predicate, so someone else's id is indistinguishable
        // from a non-existent one — no way to probe which ids exist.
        var entity = await _context.ApiKey
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId, ct);

        if (entity is null || entity.RevokedAt is not null)
            return false;

        entity.RevokedAt = DateTime.UtcNow;
        entity.RevokedByUserId = userId;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("API key {KeyPrefix} of user {UserId} revoked", entity.KeyPrefix, userId);
        return true;
    }

    /// <summary>
    /// "fak_live_" + 43 Base64Url characters from 32 CSPRNG bytes.
    /// Base64Url (not plain Base64) so the key survives being pasted into URLs,
    /// env files and JSON config without escaping.
    /// </summary>
    private static string GenerateRawKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(KeyEntropyBytes);
        return KeyPrefixLiteral + Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Standard Base64 with the URL-safe alphabet and no '=' padding.
    /// </summary>
    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Parses the requested scopes and returns the canonical stored form.
    /// "write" implies "read" — a write-only key could not read back what it wrote,
    /// which no client wants and which the scope middleware would have to special-case.
    /// </summary>
    private static string NormalizeScopes(string? requested)
    {
        var parts = (requested ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            throw new ArgumentException("At least one scope is required (read or read,write).");

        var canWrite = false;
        foreach (var part in parts)
        {
            // Enum.TryParse alone is not an allow-list: it also accepts the underlying
            // number, so "1" would mean Write and "999" would produce an EApiKeyScope
            // that has no member at all — both would end up in the Scopes column
            // without ever being named. Requiring the input to equal a declared name
            // closes both holes (Enum.GetName returns null for an undefined value),
            // and the list of names still lives in exactly one place: the enum.
            if (!Enum.TryParse<EApiKeyScope>(part, ignoreCase: true, out var scope)
                || !string.Equals(Enum.GetName(scope), part, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown scope '{part}'. Allowed scopes: read, write.");

            canWrite |= scope == EApiKeyScope.Write;
        }

        return canWrite ? "read,write" : "read";
    }
}
