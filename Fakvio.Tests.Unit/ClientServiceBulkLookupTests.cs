using AresService;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="ClientService.GetClientIdsByRegistrationNumbersAsync"/> — the bulk
/// existence check added for the CSV client import (Story N6) so preview/confirm don't do one
/// DB round trip (with the full client graph loaded) per row.
/// </summary>
public class ClientServiceBulkLookupTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ClientService _service;

    public ClientServiceBulkLookupTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _service = new ClientService(_context, Substitute.For<IAresService>(), Substitute.For<ILogger<ClientService>>());
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task ReturnsIdsOnlyForRegistrationNumbersThatExist()
    {
        _context.Client.Add(new Client { CompanyName = "Acme", RegistrationNumber = "11111111" });
        _context.Client.Add(new Client { CompanyName = "Beta", RegistrationNumber = "22222222" });
        await _context.SaveChangesAsync();

        var result = await _service.GetClientIdsByRegistrationNumbersAsync(["11111111", "99999999"]);

        result.ShouldContainKey("11111111");
        result.ShouldNotContainKey("99999999");
        result.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EmptyInput_ReturnsEmptyDictionary_WithoutQuerying()
    {
        var result = await _service.GetClientIdsByRegistrationNumbersAsync([]);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task BlankAndDuplicateEntries_AreIgnored()
    {
        _context.Client.Add(new Client { CompanyName = "Acme", RegistrationNumber = "33333333" });
        await _context.SaveChangesAsync();

        var result = await _service.GetClientIdsByRegistrationNumbersAsync(["33333333", "33333333", "", "  "]);

        result.Count.ShouldBe(1);
        result["33333333"].ShouldBe(_context.Client.Single().Id);
    }
}
