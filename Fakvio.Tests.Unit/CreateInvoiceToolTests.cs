using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for CreateInvoiceTool.
/// Tests invoice creation, client resolution, currency/VAT defaults,
/// item parsing, error handling, and UI navigation after creation.
///
/// Uses NSubstitute to mock IInvoiceService, IClientService, ICurrencyService, IVatRateService.
/// </summary>
public class CreateInvoiceToolTests
{
    private readonly CreateInvoiceTool _tool;
    private readonly IInvoiceService _invoiceService;
    private readonly IClientService _clientService;
    private readonly ICurrencyService _currencyService;
    private readonly IVatRateService _vatRateService;

    // Standard test data — reused across tests.
    private readonly ClientDto _testClient = new()
    {
        Id = 42,
        CompanyName = "Alza.cz a.s.",
        RegistrationNumber = "27082440"
    };

    private readonly ClientDto _testIssuer = new()
    {
        Id = 1,
        CompanyName = "My Company s.r.o.",
        RegistrationNumber = "12345678",
        IsVatPayer = true
    };

    private readonly CurrencyDto _testCurrency = new()
    {
        Id = 1,
        Code = "CZK",
        Name = "Czech Koruna",
        Symbol = "Kč"
    };

    private readonly VatRateDto _testVatRate = new()
    {
        Id = 1,
        Name = "DPH 21%",
        Rate = 21m,
        IsDefault = true,
        IsActive = true
    };

    public CreateInvoiceToolTests()
    {
        _invoiceService = Substitute.For<IInvoiceService>();
        _clientService = Substitute.For<IClientService>();
        _currencyService = Substitute.For<ICurrencyService>();
        _vatRateService = Substitute.For<IVatRateService>();
        var logger = Substitute.For<ILogger<CreateInvoiceTool>>();

        // Default mock setup: single client match, issuer configured, CZK currency, 21% VAT.
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(new List<ClientDto> { _testClient }, 1, 1, 5));
        _clientService.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(_testIssuer);
        _currencyService.GetCurrencyByCodeAsync("CZK", Arg.Any<CancellationToken>())
            .Returns(_testCurrency);
        _vatRateService.GetDefaultStandardRateAsync(Arg.Any<CancellationToken>())
            .Returns(_testVatRate);

        // Default: CreateInvoiceAsync returns a mock invoice with the provided data.
        _invoiceService.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var dto = callInfo.Arg<CreateInvoiceDto>();
                return new InvoiceDto
                {
                    Id = 100,
                    DocumentNumber = "FV-2026-001",
                    DocumentType = dto.DocumentType,
                    Status = EInvoiceStatus.Draft,
                    ClientId = dto.ClientId,
                    ClientName = _testClient.CompanyName,
                    IssuerId = dto.IssuerId,
                    IssuerName = _testIssuer.CompanyName,
                    IssueDate = dto.IssueDate ?? DateTime.UtcNow,
                    DueDate = (dto.IssueDate ?? DateTime.UtcNow).AddDays(14),
                    CurrencyId = dto.CurrencyId,
                    CurrencyCode = "CZK",
                    CurrencySymbol = "Kč",
                    TotalBeforeVat = dto.InvoiceItem.Sum(i => i.Quantity * i.UnitPrice),
                    TotalVat = dto.InvoiceItem.Sum(i => i.Quantity * i.UnitPrice * i.VatRatePercentage / 100),
                    TotalWithVat = dto.InvoiceItem.Sum(i => i.Quantity * i.UnitPrice * (1 + i.VatRatePercentage / 100)),
                    InvoiceItem = dto.InvoiceItem.Select((item, idx) => new InvoiceItemDto
                    {
                        Id = idx + 1,
                        OrderIndex = item.OrderIndex,
                        Description = item.Description,
                        Quantity = item.Quantity,
                        Unit = item.Unit,
                        UnitPrice = item.UnitPrice,
                        VatRateId = item.VatRateId,
                        VatRatePercentage = item.VatRatePercentage,
                        TotalBeforeVat = item.Quantity * item.UnitPrice,
                        VatAmount = item.Quantity * item.UnitPrice * item.VatRatePercentage / 100,
                        TotalWithVat = item.Quantity * item.UnitPrice * (1 + item.VatRatePercentage / 100)
                    }).ToList()
                };
            });

        _tool = new CreateInvoiceTool(
            _invoiceService, _clientService, _currencyService, _vatRateService, logger);
    }

    [Fact]
    public async Task CreateInvoice_NonNumericBankAccountId_FailsWithoutCreating()
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "quantity": 1, "unit_price": 100}]""",
            ["bank_account_id"] = "abc"
        };

        var result = await _tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeFalse();
        await _invoiceService.DidNotReceive().CreateInvoiceAsync(
            Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    // ─── Successful Creation Tests ──────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_SingleItem_CreatesSuccessfully()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Mléko", "quantity": 1, "unit_price": 999}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FV-2026-001");
        result.OutputText.ShouldContain("Alza.cz a.s.");
        result.OutputText.ShouldContain("Mléko");

        // Verify CreateInvoiceAsync was called with correct data.
        await _invoiceService.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(dto =>
                dto.DocumentType == EDocumentType.Invoice &&
                dto.ClientId == 42 &&
                dto.IssuerId == 1 &&
                dto.CurrencyId == 1 &&
                dto.InvoiceItem.Count == 1 &&
                dto.InvoiceItem[0].Description == "Mléko" &&
                dto.InvoiceItem[0].UnitPrice == 999m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_MultipleItems_CreatesSuccessfully()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Mléko", "quantity": 2, "unit_price": 30}, {"description": "Chléb", "quantity": 1, "unit_price": 45}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Mléko");
        result.OutputText.ShouldContain("Chléb");

        // Verify 2 items were sent.
        await _invoiceService.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(dto => dto.InvoiceItem.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_ReturnsNavigateAction_ToInvoiceDetail()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "quantity": 1, "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — should navigate to the created invoice.
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Type.ShouldBe("navigate");
        result.UiAction.Url.ShouldBe("/invoices/100");
    }

    [Fact]
    public async Task CreateInvoice_SetsDefaultVatRate_OnItems()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Product", "quantity": 1, "unit_price": 1000}]"""
        };

        // Act
        await _tool.ExecuteAsync(parameters);

        // Assert — VAT rate should be set on the items.
        await _invoiceService.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(dto =>
                dto.InvoiceItem[0].VatRateId == 1 &&
                dto.InvoiceItem[0].VatRatePercentage == 21m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_DefaultsQuantityToOne()
    {
        // Arrange — item without quantity specified.
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Service", "unit_price": 500}]"""
        };

        // Act
        await _tool.ExecuteAsync(parameters);

        // Assert — quantity should default to 1.
        await _invoiceService.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(dto =>
                dto.InvoiceItem[0].Quantity == 1m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_DefaultsUnitToKs()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Service", "unit_price": 500}]"""
        };

        // Act
        await _tool.ExecuteAsync(parameters);

        // Assert — unit should default to "ks" (Czech for pieces).
        await _invoiceService.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(dto =>
                dto.InvoiceItem[0].Unit == "ks"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_AcceptsPriceAlternativeNames()
    {
        // Arrange — AI might use "price" or "total_price" instead of "unit_price".
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Service", "price": 500}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _invoiceService.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(dto =>
                dto.InvoiceItem[0].UnitPrice == 500m),
            Arg.Any<CancellationToken>());
    }

    // ─── Currency Tests ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_DefaultsCurrencyToCzk()
    {
        // Arrange — no currency parameter provided.
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "unit_price": 100}]"""
        };

        // Act
        await _tool.ExecuteAsync(parameters);

        // Assert — should use CZK.
        await _currencyService.Received(1).GetCurrencyByCodeAsync("CZK", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_UsesExplicitCurrency()
    {
        // Arrange
        var eurCurrency = new CurrencyDto { Id = 2, Code = "EUR", Name = "Euro", Symbol = "€" };
        _currencyService.GetCurrencyByCodeAsync("EUR", Arg.Any<CancellationToken>())
            .Returns(eurCurrency);

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "unit_price": 100}]""",
            ["currency"] = "EUR"
        };

        // Act
        await _tool.ExecuteAsync(parameters);

        // Assert — should use EUR.
        await _invoiceService.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(dto => dto.CurrencyId == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_CurrencyNotFound_ReturnsFailure()
    {
        // Arrange
        _currencyService.GetCurrencyByCodeAsync("XYZ", Arg.Any<CancellationToken>())
            .Returns((CurrencyDto?)null);

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "unit_price": 100}]""",
            ["currency"] = "XYZ"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("XYZ");
        result.UiAction.ShouldBeNull();
    }

    // ─── Client Resolution Tests ────────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_ClientNotFound_ReturnsFailure()
    {
        // Arrange — no matching clients.
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(new List<ClientDto>(), 0, 1, 5));

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "NonExistentCompany",
            ["items"] = """[{"description": "Test", "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("NonExistentCompany");
        result.UiAction.ShouldBeNull();

        // Invoice should NOT be created.
        await _invoiceService.DidNotReceive().CreateInvoiceAsync(
            Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_MultipleClients_ReturnsAmbiguityMessage()
    {
        // Arrange — 3 matching clients.
        var clients = new List<ClientDto>
        {
            new() { Id = 1, CompanyName = "ABC Alpha", RegistrationNumber = "11111111" },
            new() { Id = 2, CompanyName = "ABC Beta", RegistrationNumber = "22222222" },
            new() { Id = 3, CompanyName = "ABC Gamma", RegistrationNumber = "33333333" }
        };
        _clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>(clients, 3, 1, 5));

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "ABC",
            ["items"] = """[{"description": "Test", "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — ambiguity, no navigation, no invoice created.
        result.IsSuccess.ShouldBeTrue(); // Not an error, just needs clarification.
        result.OutputText.ShouldContain("3 clients");
        result.OutputText.ShouldContain("ABC Alpha");
        result.OutputText.ShouldContain("ABC Beta");
        result.UiAction.ShouldBeNull();

        await _invoiceService.DidNotReceive().CreateInvoiceAsync(
            Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_SearchesOnlyCustomers_NotIssuers()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Test",
            ["items"] = """[{"description": "Service", "unit_price": 500}]"""
        };

        // Act
        await _tool.ExecuteAsync(parameters);

        // Assert — filter should exclude issuers.
        await _clientService.Received(1).GetClientsPagedAsync(
            Arg.Is<ClientFilterDto>(f => f.IsIssuer == false && f.Search == "Test"),
            Arg.Any<CancellationToken>());
    }

    // ─── Issuer Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_NoIssuerConfigured_ReturnsFailure()
    {
        // Arrange — no issuer found.
        _clientService.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("issuer");
        result.UiAction.ShouldBeNull();
    }

    // ─── Missing Parameter Tests ────────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_EmptyItemsArray_ReturnsFailure()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = "[]"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("non-empty");
    }

    [Fact]
    public async Task CreateInvoice_InvalidItemsJson_ReturnsFailure()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = "not valid json"
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("parse");
    }

    [Fact]
    public async Task CreateInvoice_ItemMissingDescription_ReturnsFailure()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("description");
    }

    [Fact]
    public async Task CreateInvoice_ItemMissingPrice_ReturnsFailure()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test"}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("price");
    }

    // ─── VAT Rate Tests ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_NoDefaultVatRate_ReturnsFailure()
    {
        // Arrange — VAT-paying issuer (_testIssuer.IsVatPayer = true) with no default VAT rate
        // configured. Issue #283: this used to fall back to a silent 0% instead of being rejected
        // — a tax document of a VAT payer must never guess a VAT rate.
        _vatRateService.GetDefaultStandardRateAsync(Arg.Any<CancellationToken>())
            .Returns((VatRateDto?)null);

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — rejected, nothing created.
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("VAT rate");
        result.UiAction.ShouldBeNull();
        await _invoiceService.DidNotReceive().CreateInvoiceAsync(
            Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_NoDefaultVatRate_NonVatPayerIssuer_CreatesWithZeroPercent()
    {
        // Arrange — the other half of the issue #283 split: a non-VAT payer has no rate to
        // configure in the first place (TenantReadinessService does not ask a non-payer for one),
        // so 0% is the correct value and the guard must stay off this path. Otherwise chat would
        // refuse an invoice the UI creates without complaint, telling the user to set up a rate
        // they are not supposed to have.
        _clientService.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto
            {
                Id = 1,
                CompanyName = "Non-payer s.r.o.",
                RegistrationNumber = "12345678",
                IsVatPayer = false
            });
        _vatRateService.GetDefaultStandardRateAsync(Arg.Any<CancellationToken>())
            .Returns((VatRateDto?)null);

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "quantity": 2, "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert — created, with no VAT rate reference and 0%.
        result.IsSuccess.ShouldBeTrue();
        await _invoiceService.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(dto =>
                dto.InvoiceItem[0].VatRateId == null &&
                dto.InvoiceItem[0].VatRatePercentage == 0m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_ExplicitZeroQuantity_ReturnsFailure()
    {
        // Arrange — issue #283: an explicit non-positive quantity used to silently become 1.
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "quantity": 0, "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("quantity");
        result.UiAction.ShouldBeNull();
        await _invoiceService.DidNotReceive().CreateInvoiceAsync(
            Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_ExplicitNegativeQuantity_ReturnsFailure()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "quantity": -3, "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("quantity");
        await _invoiceService.DidNotReceive().CreateInvoiceAsync(
            Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_QuotedZeroQuantityString_ReturnsFailure()
    {
        // Arrange — GetJsonDecimal also accepts a quoted number ("quantity": "0"), which models
        // send surprisingly often. The <= 0 rejection must fire on that path too, not just on a
        // bare JSON number.
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "quantity": "0", "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("quantity");
        await _invoiceService.DidNotReceive().CreateInvoiceAsync(
            Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    // ─── Error Handling Tests ───────────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_ServiceThrowsBusinessError_ReturnsFailure()
    {
        // Arrange
        _invoiceService.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns<InvoiceDto>(x => throw new InvalidOperationException("No number sequence configured"));

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("number sequence");
        result.UiAction.ShouldBeNull();
    }

    [Fact]
    public async Task CreateInvoice_ServiceThrowsUnexpectedError_ReturnsGenericFailure()
    {
        // Arrange
        _invoiceService.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns<InvoiceDto>(x => throw new NullReferenceException("Unexpected DB error"));

        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "unit_price": 100}]"""
        };

        // Act
        var result = await _tool.ExecuteAsync(parameters);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("unexpected error");
        result.UiAction.ShouldBeNull();
    }

    // ─── Notes Tests ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateInvoice_WithNotes_PassesNotesToDto()
    {
        // Arrange
        var parameters = new Dictionary<string, string>
        {
            ["client_name"] = "Alza",
            ["items"] = """[{"description": "Test", "unit_price": 100}]""",
            ["notes"] = "Urgent delivery requested"
        };

        // Act
        await _tool.ExecuteAsync(parameters);

        // Assert
        await _invoiceService.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(dto => dto.Notes == "Urgent delivery requested"),
            Arg.Any<CancellationToken>());
    }
}
