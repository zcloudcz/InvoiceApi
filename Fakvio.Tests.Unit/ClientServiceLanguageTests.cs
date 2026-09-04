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
///
/// The second half of the file covers the guard on that new write path: only "cs" and "en"
/// can be rendered, and neither DTO validation nor the Azure Functions host stops anything
/// else, so <c>ClientService</c> normalizes the code itself.
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

    [Theory]
    [InlineData("EN")]     // casing gets through both AllowedValues and [StringLength]
    [InlineData("  en  ")] // and a model can pad the value it sends
    public async Task CreateClient_NormalizesTheLanguageCode(string supplied)
    {
        var createDto = new CreateClientDto
        {
            RegistrationNumber = "10000004",
            CompanyName = "Language Casing Company",
            FetchFromAres = false,
            Language = supplied
        };

        var result = await _service.CreateClientAsync(createDto);

        result.Language.ShouldBe("en");
    }

    [Theory]
    [InlineData("")]      // [StringLength(5)] accepts an empty string
    [InlineData("de")]    // a valid ISO code the application has no labels or templates for
    [InlineData("cs-CZ")] // a culture name rather than an ISO 639-1 code
    public async Task CreateClient_WithUnrenderableLanguage_FallsBackToCzech(string supplied)
    {
        // A bad code must not reach PdfExportService: it only branches on "cs" and prints
        // English labels for anything else, while the template lookup falls back to the
        // any-language default — which yields English headings around a Czech body.
        var createDto = new CreateClientDto
        {
            RegistrationNumber = "10000005",
            CompanyName = "Unrenderable Language Company",
            FetchFromAres = false,
            Language = supplied
        };

        var result = await _service.CreateClientAsync(createDto);

        result.Language.ShouldBe("cs");
    }

    [Fact]
    public async Task UpdateClient_WithLanguage_RoundTripsToTheEntity()
    {
        // Arrange — client created with the DTO default ("cs")
        var client = await SeedClientAsync("cs");

        // Act — before the fix, this DTO field was never read, so the update was a silent no-op.
        var result = await _service.UpdateClientAsync(client.Id, new UpdateClientDto { Language = "en" });

        // Assert — both the returned DTO and the stored row reflect the change. The row is
        // re-read with AsNoTracking, because FindAsync would hand back the very instance the
        // service just mutated and would therefore assert nothing about what was saved.
        result.ShouldNotBeNull();
        result!.Language.ShouldBe("en");
        (await ReloadAsync(client.Id)).Language.ShouldBe("en");
    }

    [Fact]
    public async Task UpdateClient_WithoutLanguage_LeavesItUnchanged()
    {
        // Arrange
        var client = await SeedClientAsync("en");

        // Act — null Language means "don't change" (same convention as Color, TaxRegime, ...).
        var result = await _service.UpdateClientAsync(client.Id, new UpdateClientDto { CompanyName = "Renamed" });

        // Assert
        result.ShouldNotBeNull();
        result!.Language.ShouldBe("en");
        (await ReloadAsync(client.Id)).Language.ShouldBe("en");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("de")]
    [InlineData("cs-CZ")]
    public async Task UpdateClient_WithUnrenderableLanguage_LeavesItUnchanged(string supplied)
    {
        // "Unusable value is dropped" rather than "unusable value overwrites a working one" —
        // the same shape as the Enum.TryParse(...) ? value : null fields next to it. On the
        // Azure Functions host this is the ONLY check: it deserializes the DTO itself and runs
        // no model validation, so [StringLength(5)] never fires there.
        var client = await SeedClientAsync("en");

        var result = await _service.UpdateClientAsync(client.Id, new UpdateClientDto { Language = supplied });

        result.ShouldNotBeNull();
        result!.Language.ShouldBe("en");
        (await ReloadAsync(client.Id)).Language.ShouldBe("en");
    }

    [Theory]
    [InlineData("EN")]
    [InlineData("  en  ")]
    public async Task UpdateClient_NormalizesTheLanguageCode(string supplied)
    {
        var client = await SeedClientAsync("cs");

        var result = await _service.UpdateClientAsync(client.Id, new UpdateClientDto { Language = supplied });

        result.ShouldNotBeNull();
        result!.Language.ShouldBe("en");
        (await ReloadAsync(client.Id)).Language.ShouldBe("en");
    }

    /// <summary>Stores one client with the given language and returns the tracked entity.</summary>
    private async Task<Client> SeedClientAsync(string language)
    {
        var client = new Client
        {
            RegistrationNumber = "10000002",
            CompanyName = "Language Test Company",
            IsIssuer = false,
            IsActive = true,
            Language = language,
            Address = new List<Address>(),
            Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>()
        };

        _context.Client.Add(client);
        await _context.SaveChangesAsync();

        return client;
    }

    /// <summary>
    /// Reads the stored row back without the change tracker, so an assertion sees what was
    /// saved rather than the in-memory instance the service already mutated.
    /// </summary>
    private async Task<Client> ReloadAsync(long id)
        => await _context.Client.AsNoTracking().FirstAsync(client => client.Id == id);
}
