using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
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
/// Tests the server-side bank account resolver (InvoiceService.ApplyBankAccountDefaultsAsync):
/// ordering of the default pick, explicit BankAccountId validation, explicit strings winning,
/// the copy exemption and the template path.
/// </summary>
public class InvoiceBankAccountResolverTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly InvoiceTemplateService _templateService;
    private int _seq; // unique document number (and thus variable symbol) per call

    public InvoiceBankAccountResolverTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new TenantDbContext(options);

        var numbers = Substitute.For<INumberSequenceService>();
        numbers.GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => $"INV2026{++_seq:000}");

        _service = new InvoiceService(_context, numbers, Substitute.For<ITenantReadinessService>(),
            Substitute.For<ILogger<InvoiceService>>());
        _templateService = new InvoiceTemplateService(_context, _service,
            Substitute.For<ILogger<InvoiceTemplateService>>());

        _context.Client.Add(new Client { Id = 1, CompanyName = "Customer", RegistrationNumber = "R1", IsActive = true });
        _context.Client.Add(new Client { Id = 2, CompanyName = "Me", RegistrationNumber = "R2", IsIssuer = true, IsActive = true });
        _context.Client.Add(new Client { Id = 3, CompanyName = "Other issuer", RegistrationNumber = "R3", IsIssuer = true, IsActive = true });
        _context.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true });
        _context.Currency.Add(new Currency { Id = 2, Code = "EUR", Name = "Euro", Symbol = "EUR", DecimalPlaces = 2, SortOrder = 2, IsActive = true });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void AddAccount(long id, string number, string? currency, bool isDefault, long clientId = 2)
    {
        _context.BankAccount.Add(new BankAccount
        {
            Id = id, ClientId = clientId, AccountNumber = number, IBAN = "IBAN" + number,
            SWIFT = "SW" + number, CurrencyCode = currency, IsDefault = isDefault
        });
        _context.SaveChanges();
    }

    private static CreateInvoiceDto Dto(long currencyId = 1) => new()
    {
        DocumentType = EDocumentType.Invoice, ClientId = 1, IssuerId = 2, CurrencyId = currencyId,
        InvoiceItem = [new() { OrderIndex = 1, Description = "x", Quantity = 1, Unit = "pcs", UnitPrice = 100 }]
    };

    [Fact]
    public async Task NoBankData_PrefersDefaultAccountMatchingCurrency()
    {
        AddAccount(10, "111/0100", "CZK", isDefault: false);
        AddAccount(11, "222/0100", "EUR", isDefault: true);
        AddAccount(12, "333/0100", "CZK", isDefault: true);

        var result = await _service.CreateInvoiceAsync(Dto());

        result.BankAccountNumber.ShouldBe("333/0100");
        result.IBAN.ShouldBe("IBAN333/0100");
    }

    [Fact]
    public async Task NoBankData_NoDefaultForCurrency_UsesAnyAccountWithCurrency()
    {
        AddAccount(10, "111/0100", "CZK", isDefault: false);
        AddAccount(11, "222/0100", "EUR", isDefault: true);

        var result = await _service.CreateInvoiceAsync(Dto());

        result.BankAccountNumber.ShouldBe("111/0100");
    }

    [Fact]
    public async Task NoBankData_NoCurrencyMatch_FallsBackToDefault()
    {
        AddAccount(10, "111/0100", "CZK", isDefault: false);
        AddAccount(11, "222/0100", "CZK", isDefault: true);

        var result = await _service.CreateInvoiceAsync(Dto(currencyId: 2)); // EUR

        result.BankAccountNumber.ShouldBe("222/0100");
    }

    [Fact]
    public async Task NoBankData_NoDefaultNoMatch_UsesFirstAccount()
    {
        AddAccount(11, "222/0100", null, isDefault: false);
        AddAccount(10, "111/0100", null, isDefault: false);

        var result = await _service.CreateInvoiceAsync(Dto());

        result.BankAccountNumber.ShouldBe("111/0100");
    }

    [Fact]
    public async Task NoAccountsAtAll_LeavesFieldsEmpty()
    {
        var result = await _service.CreateInvoiceAsync(Dto());

        result.BankAccountNumber.ShouldBeNull();
    }

    [Fact]
    public async Task ExplicitStringsWithoutId_Win()
    {
        AddAccount(10, "111/0100", "CZK", isDefault: true);
        var dto = Dto();
        dto.BankAccountNumber = "999/0800";

        var result = await _service.CreateInvoiceAsync(dto);

        result.BankAccountNumber.ShouldBe("999/0800");
    }

    [Fact]
    public async Task CashPayment_DoesNotAutoFill()
    {
        AddAccount(10, "111/0100", "CZK", isDefault: true);
        var dto = Dto();
        dto.PaymentMethod = EPaymentMethod.Cash;

        var result = await _service.CreateInvoiceAsync(dto);

        result.BankAccountNumber.ShouldBeNull();
    }

    [Fact]
    public async Task ExplicitBankAccountId_CopiesThatAccount()
    {
        AddAccount(10, "111/0100", "CZK", isDefault: true);
        AddAccount(11, "222/0100", "EUR", isDefault: false);
        var dto = Dto();
        dto.BankAccountId = 11;
        dto.BankAccountNumber = "ignored";

        var result = await _service.CreateInvoiceAsync(dto);

        result.BankAccountNumber.ShouldBe("222/0100");
        result.SWIFT.ShouldBe("SW222/0100");
    }

    [Fact]
    public async Task ForeignBankAccountId_IsRejected()
    {
        AddAccount(20, "777/0100", "CZK", isDefault: true, clientId: 3);
        var dto = Dto();
        dto.BankAccountId = 20;

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _service.CreateInvoiceAsync(dto));

        ex.Message.ShouldContain("Bank account");
    }

    [Fact]
    public async Task Update_WithBankAccountId_SwitchesAccount_AndRejectsForeign()
    {
        AddAccount(10, "111/0100", "CZK", isDefault: true);
        AddAccount(11, "222/0100", "CZK", isDefault: false);
        AddAccount(20, "777/0100", "CZK", isDefault: true, clientId: 3);
        var created = await _service.CreateInvoiceAsync(Dto());
        created.BankAccountNumber.ShouldBe("111/0100");

        var updated = await _service.UpdateInvoiceAsync(created.Id, new UpdateInvoiceDto { BankAccountId = 11 });
        updated!.BankAccountNumber.ShouldBe("222/0100");

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.UpdateInvoiceAsync(created.Id, new UpdateInvoiceDto { BankAccountId = 20 }));
    }

    [Fact]
    public async Task Copy_KeepsSourceAccountEvenWhenDefaultDiffers()
    {
        AddAccount(10, "111/0100", "CZK", isDefault: true);
        AddAccount(11, "222/0100", "CZK", isDefault: false);
        var source = await _service.CreateInvoiceAsync(Dto());
        await _service.UpdateInvoiceAsync(source.Id, new UpdateInvoiceDto { BankAccountId = 11 });
        var entity = await _context.Invoice.FirstAsync(i => i.Id == source.Id);
        entity.Status = EInvoiceStatus.Completed;
        await _context.SaveChangesAsync();

        var copy = await _service.CopyInvoiceAsync(source.Id);

        copy.BankAccountNumber.ShouldBe("222/0100");
    }

    [Fact]
    public async Task TemplatePath_WithoutAccount_FillsDefault_AndIdOverrides()
    {
        AddAccount(10, "111/0100", "CZK", isDefault: true);
        AddAccount(11, "222/0100", "CZK", isDefault: false);
        var template = await _templateService.CreateTemplateAsync(new CreateInvoiceTemplateDto
        {
            Name = "T", DocumentType = EDocumentType.Invoice, IssuerId = 2, CurrencyId = 1,
            InvoiceItem = [new() { OrderIndex = 1, Description = "x", Quantity = 1, Unit = "pcs", UnitPrice = 100 }]
        });

        var plain = await _templateService.CreateInvoiceFromTemplateAsync(template.Id, new CreateInvoiceFromTemplateDto { ClientId = 1 });
        var chosen = await _templateService.CreateInvoiceFromTemplateAsync(template.Id,
            new CreateInvoiceFromTemplateDto { ClientId = 1, BankAccountId = 11 });

        plain.BankAccountNumber.ShouldBe("111/0100");
        chosen.BankAccountNumber.ShouldBe("222/0100");
    }
}
