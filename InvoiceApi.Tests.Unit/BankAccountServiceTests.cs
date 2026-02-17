using AresService;
using InvoiceApi.Contracts.Dto.Client;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Unit tests for the 1:N BankAccount functionality in ClientService.
/// Covers creating clients with bank accounts, auto-default logic,
/// replace-all on update, AddBankAccountAsync, and cascade delete behavior.
/// Uses InMemoryDatabase for isolation — each test gets a fresh DB.
/// </summary>
public class BankAccountServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ClientService _service;
    private readonly ILogger<ClientService> _logger;
    private readonly IAresService _aresService;

    public BankAccountServiceTests()
    {
        // Each test gets a unique in-memory database to prevent cross-test interference
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<ClientService>>();
        _aresService = Substitute.For<IAresService>();

        _service = new ClientService(_context, _aresService, _logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Creates a minimal issuer client in the DB.
    /// Helper to set up test data for tests that need an existing client.
    /// </summary>
    private async Task<long> SeedIssuerAsync()
    {
        var client = new Client
        {
            RegistrationNumber = "12345678",
            CompanyName = "Test Company s.r.o.",
            IsIssuer = true,
            IsActive = true,
            Address = new List<Address>(),
            Contact = new List<Contact>(),
            BankAccount = new List<BankAccount>()
        };
        _context.Client.Add(client);
        await _context.SaveChangesAsync();
        return client.Id;
    }

    // ─── CreateClientAsync — bank account handling ───────────────────────────

    /// <summary>
    /// When creating a client with bank accounts, they should be saved and returned in the DTO.
    /// </summary>
    [Fact]
    public async Task CreateClient_WithBankAccounts_ShouldSaveAndReturnAccounts()
    {
        // Arrange — create a client with two bank accounts
        var createDto = new CreateClientDto
        {
            RegistrationNumber = "11111111",
            CompanyName = "Bank Test Company",
            IsIssuer = true,
            FetchFromAres = false,
            BankAccount = new List<CreateBankAccountDto>
            {
                new() { AccountNumber = "1234567890/0100", Label = "CZK", IBAN = "CZ123", CurrencyCode = "CZK", IsDefault = true },
                new() { AccountNumber = "9876543210/0300", Label = "EUR", IBAN = "CZ456", CurrencyCode = "EUR", IsDefault = false }
            }
        };

        // Act
        var result = await _service.CreateClientAsync(createDto);

        // Assert — both accounts saved, correct properties
        result.BankAccount.Count.ShouldBe(2);
        result.BankAccount.ShouldContain(a => a.AccountNumber == "1234567890/0100" && a.IsDefault);
        result.BankAccount.ShouldContain(a => a.AccountNumber == "9876543210/0300" && !a.IsDefault);
    }

    /// <summary>
    /// When creating a client with bank accounts but NONE marked as default,
    /// the first account should automatically become the default.
    /// </summary>
    [Fact]
    public async Task CreateClient_NoExplicitDefault_FirstAccountBecomesDefault()
    {
        // Arrange — two accounts, neither marked as default
        var createDto = new CreateClientDto
        {
            RegistrationNumber = "22222222",
            CompanyName = "Auto Default Company",
            IsIssuer = true,
            FetchFromAres = false,
            BankAccount = new List<CreateBankAccountDto>
            {
                new() { AccountNumber = "111/0100", IsDefault = false },
                new() { AccountNumber = "222/0200", IsDefault = false }
            }
        };

        // Act
        var result = await _service.CreateClientAsync(createDto);

        // Assert — first account is auto-defaulted, second is not
        result.BankAccount.Count.ShouldBe(2);
        var first = result.BankAccount.First(a => a.AccountNumber == "111/0100");
        var second = result.BankAccount.First(a => a.AccountNumber == "222/0200");
        first.IsDefault.ShouldBeTrue();
        second.IsDefault.ShouldBeFalse();
    }

    // ─── UpdateClientAsync — replace-all strategy ────────────────────────────

    /// <summary>
    /// When updating a client with BankAccount list provided, it should
    /// replace ALL existing bank accounts (same strategy as Address/Contact).
    /// </summary>
    [Fact]
    public async Task UpdateClient_WithBankAccounts_ReplacesAllExisting()
    {
        // Arrange — create client with one account
        var clientId = await SeedIssuerAsync();
        _context.BankAccount.Add(new BankAccount
        {
            ClientId = clientId,
            AccountNumber = "OLD/0100",
            IsDefault = true
        });
        await _context.SaveChangesAsync();

        // Act — update with two new accounts (replaces the old one)
        var updateDto = new UpdateClientDto
        {
            BankAccount = new List<UpdateBankAccountDto>
            {
                new() { AccountNumber = "NEW1/0100", Label = "New CZK", IsDefault = true },
                new() { AccountNumber = "NEW2/0200", Label = "New EUR", IsDefault = false }
            }
        };

        var result = await _service.UpdateClientAsync(clientId, updateDto);

        // Assert — old account gone, two new accounts exist
        result.ShouldNotBeNull();
        result!.BankAccount.Count.ShouldBe(2);
        result.BankAccount.ShouldNotContain(a => a.AccountNumber == "OLD/0100");
        result.BankAccount.ShouldContain(a => a.AccountNumber == "NEW1/0100" && a.IsDefault);
        result.BankAccount.ShouldContain(a => a.AccountNumber == "NEW2/0200" && !a.IsDefault);
    }

    // ─── GetIssuerAsync — includes bank accounts ─────────────────────────────

    /// <summary>
    /// GetIssuerAsync should include bank accounts in the response.
    /// This is critical because InvoiceDetail loads bank accounts from the issuer.
    /// </summary>
    [Fact]
    public async Task GetIssuer_IncludesBankAccounts()
    {
        // Arrange — create issuer with bank accounts
        var clientId = await SeedIssuerAsync();
        _context.BankAccount.Add(new BankAccount
        {
            ClientId = clientId,
            AccountNumber = "1234567890/0100",
            Label = "CZK Account",
            IBAN = "CZ65 0800 0000 0019 2000 0145",
            CurrencyCode = "CZK",
            IsDefault = true
        });
        await _context.SaveChangesAsync();

        // Act
        var issuer = await _service.GetIssuerAsync();

        // Assert — bank accounts are included in the response
        issuer.ShouldNotBeNull();
        issuer!.BankAccount.Count.ShouldBe(1);
        issuer.BankAccount[0].AccountNumber.ShouldBe("1234567890/0100");
        issuer.BankAccount[0].IBAN.ShouldBe("CZ65 0800 0000 0019 2000 0145");
        issuer.BankAccount[0].IsDefault.ShouldBeTrue();
    }

    // ─── AddBankAccountAsync ─────────────────────────────────────────────────

    /// <summary>
    /// AddBankAccountAsync should add the account and auto-set as default if it's the first one.
    /// </summary>
    [Fact]
    public async Task AddBankAccount_FirstAccount_AutoSetsDefault()
    {
        // Arrange — client with no bank accounts
        var clientId = await SeedIssuerAsync();

        // Act
        var result = await _service.AddBankAccountAsync(clientId, new CreateBankAccountDto
        {
            AccountNumber = "FIRST/0100",
            Label = "First Account",
            IsDefault = false // Explicitly not default, but should become default because it's first
        });

        // Assert — auto-default set
        result.ShouldNotBeNull();
        result!.BankAccount.Count.ShouldBe(1);
        result.BankAccount[0].IsDefault.ShouldBeTrue();
    }

    /// <summary>
    /// AddBankAccountAsync with IsDefault=true should clear default from existing accounts.
    /// </summary>
    [Fact]
    public async Task AddBankAccount_SetNewDefault_ClearsPreviousDefault()
    {
        // Arrange — client with one default bank account
        var clientId = await SeedIssuerAsync();
        _context.BankAccount.Add(new BankAccount
        {
            ClientId = clientId,
            AccountNumber = "EXISTING/0100",
            IsDefault = true
        });
        await _context.SaveChangesAsync();

        // Act — add new account as default
        var result = await _service.AddBankAccountAsync(clientId, new CreateBankAccountDto
        {
            AccountNumber = "NEW/0200",
            Label = "New Default",
            IsDefault = true
        });

        // Assert — new account is default, old account is no longer default
        result.ShouldNotBeNull();
        result!.BankAccount.Count.ShouldBe(2);
        result.BankAccount.ShouldContain(a => a.AccountNumber == "NEW/0200" && a.IsDefault);
        result.BankAccount.ShouldContain(a => a.AccountNumber == "EXISTING/0100" && !a.IsDefault);
    }

    /// <summary>
    /// AddBankAccountAsync should return null for a non-existent client.
    /// </summary>
    [Fact]
    public async Task AddBankAccount_NonExistentClient_ReturnsNull()
    {
        // Act
        var result = await _service.AddBankAccountAsync(999, new CreateBankAccountDto
        {
            AccountNumber = "TEST/0100"
        });

        // Assert
        result.ShouldBeNull();
    }
}
