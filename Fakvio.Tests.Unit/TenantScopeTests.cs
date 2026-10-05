using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Test wiring shared by the worker tests: factory stub + in-memory TenantDbContext.</summary>
internal static class TenantScopeTestExtensions
{
    /// <summary>Company N resolves to schema "tenant_N"; company 999 is "not provisioned" (null).</summary>
    public static IServiceCollection AddTenantScopeStubs(this IServiceCollection services)
    {
        services.AddDbContext<TenantDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped(_ =>
        {
            var factory = Substitute.For<ITenantDbContextFactory>();
            factory.ResolveSchemaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(ci => ci.ArgAt<long>(0) == 999 ? null : $"tenant_{ci.ArgAt<long>(0)}");
            return factory;
        });
        return services;
    }
}

public class TenantScopeTests
{
    [Fact]
    public async Task CreateTenantScopeAsync_SetsSchemaOnScopedTenantContext()
    {
        using var provider = new ServiceCollection().AddLogging().AddTenantScopeStubs().BuildServiceProvider();

        using var scope = await provider.GetRequiredService<IServiceScopeFactory>().CreateTenantScopeAsync(7);

        scope.ServiceProvider.GetRequiredService<TenantDbContext>().Schema.ShouldBe("tenant_7");
    }

    [Fact]
    public async Task CreateTenantScopeAsync_UnknownOrInactiveCompany_Throws()
    {
        using var provider = new ServiceCollection().AddLogging().AddTenantScopeStubs().BuildServiceProvider();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<IServiceScopeFactory>().CreateTenantScopeAsync(999));
    }
}
