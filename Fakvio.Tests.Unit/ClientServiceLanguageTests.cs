using AresService;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Regression tests for issue #306: <c>Language</c> on <see cref="CreateClientDto"/> and
/// <see cref="UpdateClientDto"/> was never read by <c>ClientService</c>, so the MCP
/// <c>UpdateClient</c> tool accepted a <c>language</c> value, reported success and changed
/// nothing. Uses an InMemory database, same pattern as <c>BankAccountServiceTests</c>.
/// </summary>
public class ClientServiceLanguageTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ClientService _service;

    public ClientServiceLanguageTests()
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
    public async Task CreateClient_WithLanguage_IsSaved()
    {
        var createDto = new CreateClientDto
        {
            RegistrationNumber = "10000001",
            CompanyName = "Language Test Company",
            FetchFromAres = false,
            Language = "en"
        };

        var result = await _service.CreateClientAsync(createDto);

        result.Language.ShouldBe("en");
    }

    [Fact]
    public async Task UpdateClient_WithLanguage_RoundTripsToTheEntity()
    {
        // Arrange — client created with the DTO default ("cs")
        var client = new Client
        {
            RegistrationNumber = "10000002",
            CompanyName = "Language Update Company",
            IsIssuer = false,
            IsActive = true,
            Language = "cs",
            Address = new List<Address>(),
            Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>()
        };
        _context.Client.Add(client);
        await _context.SaveChangesAsync();

        // Act — before the fix, this DTO field was never read, so the update was a silent no-op.
        var result = await _service.UpdateClientAsync(client.Id, new UpdateClientDto { Language = "en" });

        // Assert — both the returned DTO and the persisted entity reflect the change.
        result.ShouldNotBeNull();
        result!.Language.ShouldBe("en");
        (await _context.Client.FindAsync(client.Id))!.Language.ShouldBe("en");
    }

    [Fact]
    public async Task UpdateClient_WithoutLanguage_LeavesItUnchanged()
    {
        // Arrange
        var client = new Client
        {
            RegistrationNumber = "10000003",
            CompanyName = "Language Untouched Company",
            IsIssuer = false,
            IsActive = true,
            Language = "en",
            Address = new List<Address>(),
            Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>()
        };
        _context.Client.Add(client);
        await _context.SaveChangesAsync();

        // Act — null Language means "don't change" (same convention as Color, TaxRegime, ...).
        var result = await _service.UpdateClientAsync(client.Id, new UpdateClientDto { CompanyName = "Renamed" });

        // Assert
        result.ShouldNotBeNull();
        result!.Language.ShouldBe("en");
    }
}
