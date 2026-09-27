using System.Net;
using System.Text.Json;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>Tests for <see cref="SettingsTools"/> (N3.1, N3.2, N3.3).</summary>
public class SettingsToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    // ── ListNumberSequences ─────────────────────────────────────────────

    [Fact]
    public async Task ListNumberSequences_ReturnsSequencesAndFormats()
    {
        _api.GetNumberSequencesAsync(Arg.Any<EDocumentType?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<NumberSequenceDto> { new() { Id = 1, Name = "Faktury 2026", IsDefault = true } });
        _api.GetNumberSequenceFormatsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<NumberSequenceFormatDto> { new() { Id = 5, Name = "yyyyNNNN" } });

        var json = await SettingsTools.ListNumberSequences(_api);

        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("sequences")[0].GetProperty("name").GetString().ShouldBe("Faktury 2026");
        root.GetProperty("formats")[0].GetProperty("name").GetString().ShouldBe("yyyyNNNN");
    }

    [Fact]
    public async Task ListNumberSequences_ParsesDocumentTypeFilter()
    {
        _api.GetNumberSequencesAsync(Arg.Any<EDocumentType?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<NumberSequenceDto>());
        _api.GetNumberSequenceFormatsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<NumberSequenceFormatDto>());

        await SettingsTools.ListNumberSequences(_api, documentType: "CreditNote");

        await _api.Received(1).GetNumberSequencesAsync(EDocumentType.CreditNote, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListNumberSequences_UnknownDocumentType_ReturnsErrorWithoutCallingApi()
    {
        var json = await SettingsTools.ListNumberSequences(_api, documentType: "Nonsense");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("Unknown documentType");
        await _api.DidNotReceive().GetNumberSequencesAsync(Arg.Any<EDocumentType?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    // ── ListVatRates ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListVatRates_ReturnsActiveRates()
    {
        _api.GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VatRateDto> { new() { Id = 1, Rate = 21m, IsDefault = true } });

        var json = await SettingsTools.ListVatRates(_api);

        JsonDocument.Parse(json).RootElement[0].GetProperty("rate").GetDecimal().ShouldBe(21m);
    }

    [Fact]
    public async Task ListVatRates_InvalidDate_ReturnsErrorWithoutCallingApi()
    {
        var json = await SettingsTools.ListVatRates(_api, date: "not-a-date");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("Invalid date");
        await _api.DidNotReceive().GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    // ── CreateNumberSequence ─────────────────────────────────────────────

    [Fact]
    public async Task CreateNumberSequence_CreatesViaApi()
    {
        _api.CreateNumberSequenceAsync(Arg.Any<CreateNumberSequenceDto>(), Arg.Any<CancellationToken>())
            .Returns(new NumberSequenceDto { Id = 9, Name = "Faktury 2026", IsDefault = true });

        var json = await SettingsTools.CreateNumberSequence(
            _api, name: "Faktury 2026", documentType: "Invoice", numberSequenceFormatId: 5);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(9);
        await _api.Received(1).CreateNumberSequenceAsync(
            Arg.Is<CreateNumberSequenceDto>(d =>
                d.Name == "Faktury 2026" && d.DocumentType == EDocumentType.Invoice && d.NumberSequenceFormatId == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateNumberSequence_UnknownDocumentType_ReturnsErrorWithoutCallingApi()
    {
        var json = await SettingsTools.CreateNumberSequence(
            _api, name: "X", documentType: "Nonsense", numberSequenceFormatId: 1);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("Unknown documentType");
        await _api.DidNotReceive().CreateNumberSequenceAsync(Arg.Any<CreateNumberSequenceDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A read+write-scoped but role-restricted (User, not Admin/SysAdmin) key gets 403 →
    /// `forbidden`, never `internal_error` (N2.2).</summary>
    [Fact]
    public async Task CreateNumberSequence_UserRole403_ReturnsForbidden()
    {
        _api.CreateNumberSequenceAsync(Arg.Any<CreateNumberSequenceDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new FakvioApiException("API returned 403 Forbidden", HttpStatusCode.Forbidden, safeMessage: null));

        var json = await SettingsTools.CreateNumberSequence(
            _api, name: "X", documentType: "Invoice", numberSequenceFormatId: 1);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("forbidden");
    }

    // ── UpdateNumberSequence ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateNumberSequence_WithoutSetAsDefault_DoesNotCallSetDefault()
    {
        _api.UpdateNumberSequenceAsync(1, Arg.Any<UpdateNumberSequenceDto>(), Arg.Any<CancellationToken>())
            .Returns(new NumberSequenceDto { Id = 1, Name = "Renamed" });

        var json = await SettingsTools.UpdateNumberSequence(_api, id: 1, name: "Renamed");

        JsonDocument.Parse(json).RootElement.GetProperty("name").GetString().ShouldBe("Renamed");
        await _api.DidNotReceive().SetDefaultNumberSequenceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateNumberSequence_WithSetAsDefault_AlsoCallsSetDefault()
    {
        _api.UpdateNumberSequenceAsync(1, Arg.Any<UpdateNumberSequenceDto>(), Arg.Any<CancellationToken>())
            .Returns(new NumberSequenceDto { Id = 1, Name = "Faktury" });
        _api.SetDefaultNumberSequenceAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NumberSequenceDto { Id = 1, Name = "Faktury", IsDefault = true });

        var json = await SettingsTools.UpdateNumberSequence(_api, id: 1, setAsDefault: true);

        JsonDocument.Parse(json).RootElement.GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        await _api.Received(1).SetDefaultNumberSequenceAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateNumberSequence_NotFound_ReturnsError()
    {
        _api.UpdateNumberSequenceAsync(999, Arg.Any<UpdateNumberSequenceDto>(), Arg.Any<CancellationToken>())
            .Returns((NumberSequenceDto?)null);

        var json = await SettingsTools.UpdateNumberSequence(_api, id: 999, name: "X");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("not found");
    }

    // ── UpdateMyCompany ──────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMyCompany_NoFieldsSent_ReturnsErrorWithoutCallingApi()
    {
        var json = await SettingsTools.UpdateMyCompany(_api);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("Nothing to change");
        await _api.DidNotReceive().GetIssuerAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMyCompany_NoIssuerConfigured_ReturnsError()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns((ClientDto?)null);

        var json = await SettingsTools.UpdateMyCompany(_api, companyName: "Acme");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("No issuer");
    }

    [Fact]
    public async Task UpdateMyCompany_ChangesAddress_KeepsOtherAddressesInTheCollection()
    {
        var issuer = new ClientDto
        {
            Id = 2,
            CompanyName = "Acme",
            Address =
            [
                new() { Id = 1, Street = "Old street 1", City = "Praha", PostalCode = "11000", Country = "CZ", IsPrimary = true },
                new() { Id = 2, Street = "Warehouse 2", City = "Brno", PostalCode = "60200", Country = "CZ", IsPrimary = false }
            ]
        };
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(issuer);
        _api.UpdateClientAsync(2, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, CompanyName = "Acme" });

        await SettingsTools.UpdateMyCompany(_api, street: "New street 5");

        await _api.Received(1).UpdateClientAsync(2, Arg.Is<UpdateClientDto>(d =>
            d.Address!.Count == 2 &&
            d.Address[0].Street == "New street 5" && d.Address[0].IsPrimary == true &&
            d.Address.Any(a => a.Street == "Warehouse 2" && a.IsPrimary == false)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMyCompany_OnlyCompanyName_DoesNotTouchAddress()
    {
        var issuer = new ClientDto { Id = 2, CompanyName = "Old Name" };
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(issuer);
        _api.UpdateClientAsync(2, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, CompanyName = "New Name" });

        var json = await SettingsTools.UpdateMyCompany(_api, companyName: "New Name");

        JsonDocument.Parse(json).RootElement.GetProperty("companyName").GetString().ShouldBe("New Name");
        await _api.Received(1).UpdateClientAsync(2, Arg.Is<UpdateClientDto>(d =>
            d.CompanyName == "New Name" && d.Address == null), Arg.Any<CancellationToken>());
    }

    // ── AddBankAccount ───────────────────────────────────────────────────

    [Fact]
    public async Task AddBankAccount_AddsToIssuer()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 2, CompanyName = "Acme" });
        _api.AddBankAccountAsync(2, Arg.Any<CreateBankAccountDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto
            {
                Id = 2,
                BankAccount = [new() { AccountNumber = "1234567890/0100", IsDefault = true }]
            });

        var json = await SettingsTools.AddBankAccount(_api, accountNumber: "1234567890/0100");

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("bankAccount")[0].GetProperty("accountNumber").GetString()
            .ShouldBe("1234567890/0100");
        await _api.Received(1).AddBankAccountAsync(
            2, Arg.Is<CreateBankAccountDto>(d => d.AccountNumber == "1234567890/0100"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddBankAccount_NoIssuerConfigured_ReturnsIssuerMissingMessage()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns((ClientDto?)null);

        var json = await SettingsTools.AddBankAccount(_api, accountNumber: "1234567890/0100");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("No issuer");
    }
}
