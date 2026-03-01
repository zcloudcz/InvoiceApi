using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Custom model cache key factory for the multi-tenant PostgreSQL architecture.
///
/// EF Core caches the compiled model (entity types, table mappings, indexes, etc.) for performance.
/// By default, it uses a single cache entry per DbContext TYPE — meaning all TenantDbContext
/// instances share the same cached model, regardless of their Schema property.
///
/// Problem: TenantDbContext uses HasDefaultSchema(Schema) in OnModelCreating, so the model
/// is different for each schema (e.g., "tenant_42" vs "tenant_99"). Without this factory,
/// EF Core would cache the FIRST schema's model and reuse it for all tenants — causing
/// SQL queries to target the wrong schema.
///
/// Solution: Include the Schema value in the cache key. EF Core then maintains one cached
/// model per distinct schema, ensuring each tenant's queries target the correct schema.
///
/// Performance: Models are cached lazily — only schemas that have been accessed get a cached model.
/// Memory usage is proportional to the number of active tenant schemas (not total tenants).
/// </summary>
public class TenantModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <summary>
    /// Creates a cache key that includes the tenant schema name.
    /// Two TenantDbContext instances with different Schema values get different cache keys,
    /// so EF Core builds and caches separate models for each schema.
    /// </summary>
    public object Create(DbContext context, bool designTime)
    {
        // For TenantDbContext, include the Schema in the cache key
        if (context is TenantDbContext tenantContext)
        {
            return (context.GetType(), tenantContext.Schema ?? "public", designTime);
        }

        // For other contexts (MasterDbContext), use the default behavior (type + designTime)
        return (context.GetType(), designTime);
    }

    /// <summary>
    /// Non-designTime overload — delegates to the main Create method.
    /// </summary>
    public object Create(DbContext context)
        => Create(context, designTime: false);
}
