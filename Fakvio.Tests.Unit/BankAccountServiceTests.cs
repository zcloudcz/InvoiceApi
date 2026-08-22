using AresService;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

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
                new() { AccountNumber = "1234567899/0100", Label = "CZK", IBAN = "CZ9501000000001234567899", CurrencyCode = "CZK", IsDefault = true },
                new() { AccountNumber = "9876543211/0300", Label = "EUR", IBAN = "CZ6903000000009876543211", CurrencyCode = "EUR", IsDefault = false }
            }
        };

        // Act
        var result = await _service.CreateClientAsync(createDto);

        // Assert — both accounts saved, correct properties
        result.BankAccount.Count.ShouldBe(2);
        result.BankAccount.ShouldContain(a => a.AccountNumber == "1234567899/0100" && a.IsDefault);
        result.BankAccount.ShouldContain(a => a.AccountNumber == "9876543211/0300" && !a.IsDefault);
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
                new() { AccountNumber = "1003/0100", IsDefault = false },
                new() { AccountNumber = "2006/0200", IsDefault = false }
            }
        };

        // Act
        var result = await _service.CreateClientAsync(createDto);

        // Assert — first account is auto-defaulted, second is not
        result.BankAccount.Count.ShouldBe(2);
        var first = result.BankAccount.First(a => a.AccountNumber == "1003/0100");
        var second = result.BankAccount.First(a => a.AccountNumber == "2006/0200");
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

    // ─── Issue #154: the write path refuses an unpayable bank connection ─────

    /// <summary>
    /// A Czech-shaped account number that fails the modulo 11 checksum must not be stored.
    ///
    /// Storing it was the first half of issue #154: nothing complained at write time, and the
    /// value only became visible much later as a QR code nobody could pay, on an invoice the
    /// customer had already received. The write path is the last moment the user still has the
    /// correct number in front of them, so that is where the failure belongs.
    /// </summary>
    [Fact]
    public async Task CreateClient_BankAccountFailingMod11_IsRejected()
    {
        var createDto = new CreateClientDto
        {
            RegistrationNumber = "22222222",
            CompanyName = "Typo Company",
            FetchFromAres = false,
            BankAccount = new List<CreateBankAccountDto>
            {
                new() { AccountNumber = "1234567890/0100", IsDefault = true }
            }
        };

        var ex = await Should.ThrowAsync<ArgumentException>(() => _service.CreateClientAsync(createDto));
        ex.Message.ShouldContain("1234567890/0100");

        // Nothing must have been persisted — a rejected account cannot leave a half-created client.
        (await _context.Client.CountAsync()).ShouldBe(0);
    }

    /// <summary>
    /// An IBAN with a broken check digit is rejected as well — it would produce a QR code
    /// that scans into a payment the bank then refuses.
    /// </summary>
    [Fact]
    public async Task AddBankAccount_IbanWithTypo_IsRejected()
    {
        var clientId = await SeedIssuerAsync();

        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            _service.AddBankAccountAsync(clientId, new CreateBankAccountDto
            {
                AccountNumber = "19-2000145399/0800",
                IBAN = "CZ6508000000192000145398"
            }));
        ex.Message.ShouldContain("IBAN");

        var client = await _context.Client.Include(c => c.BankAccount)
            .FirstAsync(c => c.Id == clientId);
        client.BankAccount.ShouldBeEmpty();
    }

    /// <summary>
    /// A correct Czech account number with a matching IBAN goes through untouched — the new
    /// validation must not turn into a wall for legitimate data.
    /// </summary>
    [Fact]
    public async Task AddBankAccount_ValidCzechAccountAndIban_IsAccepted()
    {
        var clientId = await SeedIssuerAsync();

        var result = await _service.AddBankAccountAsync(clientId, new CreateBankAccountDto
        {
            AccountNumber = "19-2000145399/0800",
            IBAN = "CZ65 0800 0000 1920 0014 5399"
        });

        result.ShouldNotBeNull();
        result!.BankAccount.ShouldContain(a => a.AccountNumber == "19-2000145399/0800");
    }

    /// <summary>
    /// A free-form foreign account number is still storable. There is no checksum to verify it
    /// against, and blocking it would break international issuers; it simply cannot produce a
    /// QR payment code, which is QrPaymentService's call to make, not the write path's.
    /// </summary>
    [Fact]
    public async Task AddBankAccount_ForeignFreeFormAccount_IsAccepted()
    {
        var clientId = await SeedIssuerAsync();

        var result = await _service.AddBankAccountAsync(clientId, new CreateBankAccountDto
        {
            AccountNumber = "DE-ACCOUNT-4711"
        });

        result.ShouldNotBeNull();
        result!.BankAccount.ShouldContain(a => a.AccountNumber == "DE-ACCOUNT-4711");
    }
}
