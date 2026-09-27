using System.Net.Sockets;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for the OAuth 2.1 storage model (ADR 0001, docs/adr/0001-mcp-oauth21.md,
/// task N5.2) against a REAL PostgreSQL schema — same reasoning as
/// <see cref="ApiKeyDatabaseConstraintTests"/>: unique indexes, FK cascades and column limits
/// are DDL, not something an InMemory provider can prove.
///
/// Each test class instance creates its own throwaway schema (see base pattern in
/// <see cref="ApiKeyDatabaseConstraintTests"/>) and shares the same collection so DDL never
/// runs concurrently against the same PostgreSQL instance.
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class OAuthDatabaseConstraintTests : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private const string ConnectionStringEnvVar = "FAKVIO_TEST_POSTGRES";

    private const string SkipReason =
        "PostgreSQL is not reachable — start it with 'docker compose up -d' " +
        "or point " + ConnectionStringEnvVar + " at another instance.";

    private const long OwnerUserId = 501_001L;

    private readonly string _schemaName = $"test_oauth_n52_{Guid.NewGuid():N}"[..40];
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;

    private NpgsqlDataSourceFactory? _dataSourceFactory;
    private bool _databaseAvailable;

    public async Task InitializeAsync()
    {
        _dataSourceFactory = new NpgsqlDataSourceFactory(new DatabaseOptions
        {
            ConnectionString = _connectionString,
            AuthMode = DatabaseAuthMode.Password
        });

        _databaseAvailable = await CanReachPostgreSqlAsync();
        if (!_databaseAvailable)
            return;

        await using (var createSchema = _dataSourceFactory.Root.CreateCommand(
                         $"CREATE SCHEMA \"{_schemaName}\""))
        {
            await createSchema.ExecuteNonQueryAsync();
        }

        await using var context = CreateMasterContext();
        await context.Database.MigrateAsync();

        await SeedOwnerAsync();
    }

    private async Task<bool> CanReachPostgreSqlAsync()
    {
        try
        {
            await using var connection = await _dataSourceFactory!.Root.OpenConnectionAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_databaseAvailable && _dataSourceFactory is not null)
        {
            await using var command = _dataSourceFactory.Root.CreateCommand(
                $"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE");
            await command.ExecuteNonQueryAsync();
        }

        if (_dataSourceFactory is not null)
            await _dataSourceFactory.DisposeAsync();
    }

    private MasterDbContext CreateMasterContext()
        => new(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(_dataSourceFactory!.GetForSchema(_schemaName, includePublicInSearchPath: false))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

    private async Task SeedOwnerAsync()
    {
        await using var context = CreateMasterContext();
        context.User.Add(new User
        {
            Id = OwnerUserId,
            Email = "oauth-owner@fakvio.test",
            PasswordHash = "not-a-real-hash",
            FirstName = "OAuth",
            LastName = "Owner",
            Role = EUserRole.User,
            IsActive = true,
            IsEmailVerified = true,
            ExternalProvider = EExternalProvider.None
        });
        await context.SaveChangesAsync();
    }

    private static OAuthGrant BuildGrant(long userId = OwnerUserId)
        => new()
        {
            UserId = userId,
            ClientId = "https://claude.ai/oauth/claude-code-client-metadata",
            ClientName = "Claude Code",
            Scopes = "read",
            Resource = "https://mcp.fakvio.cz/mcp",
            ExpiresAt = DateTime.UtcNow.AddDays(180)
        };

    // ─── OAuthGrant ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task DeletingOwner_CascadeDeletesTheirGrants()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await using (var context = CreateMasterContext())
        {
            context.OAuthGrant.Add(BuildGrant());
            await context.SaveChangesAsync();
        }

        await using (var deleteContext = CreateMasterContext())
        {
            var owner = await deleteContext.User.SingleAsync(u => u.Id == OwnerUserId);
            deleteContext.User.Remove(owner);
            await deleteContext.SaveChangesAsync();
        }

        await using var readContext = CreateMasterContext();
        (await readContext.OAuthGrant.CountAsync(g => g.UserId == OwnerUserId)).ShouldBe(0);
    }

    // ─── ApiKey ↔ OAuthGrant (T4, T6) ──────────────────────────────────────

    /// <summary>
    /// The mechanism T6 (confused deputy) and the "revoke on Integrations page" flow both
    /// depend on: an OAuth access token IS an ApiKey row, and revoking/deleting its grant must
    /// take the access token down with it — in one FK cascade, not a second cleanup step that
    /// could be forgotten.
    /// </summary>
    [SkippableFact]
    public async Task DeletingGrant_CascadeDeletesItsOAuthAccessTokens()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        long grantId;
        await using (var context = CreateMasterContext())
        {
            var grant = BuildGrant();
            context.OAuthGrant.Add(grant);
            await context.SaveChangesAsync();
            grantId = grant.Id;

            context.ApiKey.Add(new ApiKey
            {
                UserId = OwnerUserId,
                Name = "OAuth access token",
                KeyPrefix = "fak_oat_abcd",
                KeyHash = "b2F1dGgtYWNjZXNzLXRva2VuLWhhc2g=",
                Scopes = "read",
                OAuthGrantId = grantId,
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await context.SaveChangesAsync();
        }

        await using (var deleteContext = CreateMasterContext())
        {
            var grant = await deleteContext.OAuthGrant.SingleAsync(g => g.Id == grantId);
            deleteContext.OAuthGrant.Remove(grant);
            await deleteContext.SaveChangesAsync();
        }

        await using var readContext = CreateMasterContext();
        (await readContext.ApiKey.CountAsync(k => k.OAuthGrantId == grantId)).ShouldBe(0);
    }

    // ─── OAuthAuthorizationCode (T3) ───────────────────────────────────────

    [SkippableFact]
    public async Task SecondCodeWithTheSameHash_ViolatesUniqueIndex()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        const string sharedHash = "c2hhcmVkLWNvZGUtaGFzaC1mb3ItYm90aC1yb3dz";

        await using (var context = CreateMasterContext())
        {
            context.OAuthAuthorizationCode.Add(BuildCode(sharedHash));
            await context.SaveChangesAsync();
        }

        var act = async () =>
        {
            await using var context = CreateMasterContext();
            context.OAuthAuthorizationCode.Add(BuildCode(sharedHash));
            await context.SaveChangesAsync();
        };

        var exception = await Should.ThrowAsync<DbUpdateException>(act);
        exception.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    private static OAuthAuthorizationCode BuildCode(string hash)
        => new()
        {
            CodeHash = hash,
            UserId = OwnerUserId,
            ClientId = "https://claude.ai/oauth/claude-code-client-metadata",
            ClientName = "Claude Code",
            RedirectUri = "https://claude.ai/api/mcp/auth_callback",
            CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Scopes = "read",
            Resource = "https://mcp.fakvio.cz/mcp",
            ExpiresAt = DateTime.UtcNow.AddSeconds(60)
        };

    // ─── OAuthRefreshToken (T5) ────────────────────────────────────────────

    [SkippableFact]
    public async Task SecondRefreshTokenWithTheSameHash_ViolatesUniqueIndex()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        long grantId;
        await using (var context = CreateMasterContext())
        {
            var grant = BuildGrant();
            context.OAuthGrant.Add(grant);
            await context.SaveChangesAsync();
            grantId = grant.Id;
        }

        const string sharedHash = "c2hhcmVkLXJlZnJlc2gtdG9rZW4taGFzaA==";

        await using (var context = CreateMasterContext())
        {
            context.OAuthRefreshToken.Add(new OAuthRefreshToken
            {
                TokenHash = sharedHash,
                GrantId = grantId,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            });
            await context.SaveChangesAsync();
        }

        var act = async () =>
        {
            await using var context = CreateMasterContext();
            context.OAuthRefreshToken.Add(new OAuthRefreshToken
            {
                TokenHash = sharedHash,
                GrantId = grantId,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            });
            await context.SaveChangesAsync();
        };

        var exception = await Should.ThrowAsync<DbUpdateException>(act);
        exception.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [SkippableFact]
    public async Task DeletingGrant_CascadeDeletesItsRefreshTokens()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        long grantId;
        await using (var context = CreateMasterContext())
        {
            var grant = BuildGrant();
            context.OAuthGrant.Add(grant);
            await context.SaveChangesAsync();
            grantId = grant.Id;

            context.OAuthRefreshToken.Add(new OAuthRefreshToken
            {
                TokenHash = "cmVmcmVzaC10b2tlbi1jYXNjYWRlLXRlc3Q=",
                GrantId = grantId,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            });
            await context.SaveChangesAsync();
        }

        await using (var deleteContext = CreateMasterContext())
        {
            var grant = await deleteContext.OAuthGrant.SingleAsync(g => g.Id == grantId);
            deleteContext.OAuthGrant.Remove(grant);
            await deleteContext.SaveChangesAsync();
        }

        await using var readContext = CreateMasterContext();
        (await readContext.OAuthRefreshToken.CountAsync(t => t.GrantId == grantId)).ShouldBe(0);
    }

    // ─── OAuthCleanupService (§4.3 "Úklid") ────────────────────────────────

    [SkippableFact]
    public async Task CleanupService_DeletesExpiredRows_ButKeepsLiveOnes()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        long liveGrantId, oldGrantId;
        await using (var context = CreateMasterContext())
        {
            var liveGrant = BuildGrant();
            var oldRevokedGrant = BuildGrant();
            oldRevokedGrant.RevokedAt = DateTime.UtcNow - OAuthCleanupServiceTestWindow;
            oldRevokedGrant.RevokedReason = EOAuthGrantRevokedReason.User;

            context.OAuthGrant.AddRange(liveGrant, oldRevokedGrant);
            await context.SaveChangesAsync();
            liveGrantId = liveGrant.Id;
            oldGrantId = oldRevokedGrant.Id;

            // Expired access token — should be swept.
            context.ApiKey.Add(new ApiKey
            {
                UserId = OwnerUserId,
                Name = "Expired OAuth token",
                KeyPrefix = "fak_oat_expd",
                KeyHash = "ZXhwaXJlZC1vYXV0aC10b2tlbi1oYXNo",
                Scopes = "read",
                OAuthGrantId = liveGrantId,
                ExpiresAt = DateTime.UtcNow - TimeSpan.FromDays(2)
            });

            // Old authorization code — should be swept regardless of ConsumedAt.
            var oldCode = BuildCode("b2xkLWNvZGUtc3dlcHQtYnktY2xlYW51cA==");
            context.OAuthAuthorizationCode.Add(oldCode);
            await context.SaveChangesAsync();

            // Force CreatedAt into the past — BaseEntity sets it on save, so update afterward.
            await context.Database.ExecuteSqlRawAsync(
                $"UPDATE \"OAuthAuthorizationCode\" SET \"CreatedAt\" = now() - interval '2 days' WHERE \"Id\" = {oldCode.Id}");
        }

        var service = new Fakvio.Infrastructure.Service.OAuthCleanupService(
            new SingleContextScopeFactory(CreateMasterContext),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Fakvio.Infrastructure.Service.OAuthCleanupService>.Instance);

        await service.RunOnceAsync(CancellationToken.None);

        await using var readContext = CreateMasterContext();
        (await readContext.ApiKey.CountAsync(k => k.OAuthGrantId == liveGrantId)).ShouldBe(0);
        (await readContext.OAuthAuthorizationCode.CountAsync()).ShouldBe(0);
        (await readContext.OAuthGrant.CountAsync(g => g.Id == oldGrantId)).ShouldBe(0);
        // The live grant itself (not expired, not revoked) must survive the sweep.
        (await readContext.OAuthGrant.CountAsync(g => g.Id == liveGrantId)).ShouldBe(1);
    }

    private static readonly TimeSpan OAuthCleanupServiceTestWindow =
        Fakvio.Infrastructure.Service.OAuthCleanupService.GrantRetention + TimeSpan.FromDays(1);

    /// <summary>
    /// Minimal <see cref="IServiceScopeFactory"/> that always hands back a scope resolving to
    /// the SAME <see cref="MasterDbContext"/> factory function — enough for
    /// <see cref="Fakvio.Infrastructure.Service.OAuthCleanupService.RunOnceAsync"/>, which only
    /// resolves one context per scope, without pulling in the full DI container this test class
    /// does not otherwise need.
    /// </summary>
    private sealed class SingleContextScopeFactory : IServiceScopeFactory
    {
        private readonly Func<MasterDbContext> _factory;

        public SingleContextScopeFactory(Func<MasterDbContext> factory) => _factory = factory;

        public IServiceScope CreateScope() => new Scope(_factory());

        private sealed class Scope : IServiceScope
        {
            private readonly MasterDbContext _context;
            public Scope(MasterDbContext context) => _context = context;
            public IServiceProvider ServiceProvider => new Provider(_context);
            public void Dispose() => _context.Dispose();

            private sealed class Provider : IServiceProvider
            {
                private readonly MasterDbContext _context;
                public Provider(MasterDbContext context) => _context = context;
                public object? GetService(Type serviceType) =>
                    serviceType == typeof(MasterDbContext) ? _context : null;
            }
        }
    }
}
