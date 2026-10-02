using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// THE way for background code (BackgroundService workers, IMAP poll, import jobs) to get a DI
/// scope that is bound to one tenant.
///
/// Why it exists: <see cref="ITenantDbContextFactory.CreateContextForCompanyAsync"/> returns a NEW
/// context and never touches the DI-scoped <see cref="TenantDbContext"/>. Services resolved from
/// a scope (IInvoiceService, IReminderService, ...) inject that scoped context, and in a worker
/// nothing (no HTTP request -> no TenantContextMiddleware) sets its Schema. Schema = null means
/// no HasDefaultSchema -> queries hit "public" -> "relation does not exist" (or, worse, silently
/// the wrong data).
///
/// Usage:
/// <code>
/// using var scope = await _scopeFactory.CreateTenantScopeAsync(companyId, ct);
/// var service = scope.ServiceProvider.GetRequiredService&lt;IReminderService&gt;();
/// </code>
/// Do NOT set TenantDbContext.Schema by hand in workers — call this instead.
/// </summary>
public static class TenantScope
{
    /// <summary>
    /// Creates a scope whose scoped <see cref="TenantDbContext"/> points at the company's schema
    /// (the schema is also migrated if this process has not done so yet).
    /// Throws <see cref="InvalidOperationException"/> if the company is unknown, not provisioned or
    /// inactive. The caller owns (disposes) the returned scope.
    /// </summary>
    public static async Task<IServiceScope> CreateTenantScopeAsync(
        this IServiceScopeFactory scopeFactory, long companyId, CancellationToken ct = default)
    {
        var scope = scopeFactory.CreateScope();
        try
        {
            var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();

            var schema = await factory.ResolveSchemaAsync(companyId, ct);
            if (string.IsNullOrEmpty(schema))
            {
                throw new InvalidOperationException(
                    $"Cannot create tenant scope for CompanyId {companyId}: not found, not provisioned, or inactive.");
            }

            await factory.EnsureMigratedAsync(companyId, ct);

            // Must happen before anything uses the context: EF builds (and caches) the model on first use.
            scope.ServiceProvider.GetRequiredService<TenantDbContext>().Schema = schema;
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
