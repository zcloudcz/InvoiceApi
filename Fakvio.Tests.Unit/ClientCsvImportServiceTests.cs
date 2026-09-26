using System.Text;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Import;
using Fakvio.Infrastructure.Import;
using Fakvio.Infrastructure.Service;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="ClientCsvImportService"/> (N6.2) — CSV column mapping and IČO dedup.
///
/// Fixtures here are synthetic (no real Fakturoid/iDoklad export samples were available when this
/// was written) — replace with real exports once the owner supplies anonymized samples.
/// See DEVGUIDE.md §4.13 and Story N6 in research/plan-2026-W39-specs.md.
/// </summary>
public class ClientCsvImportServiceTests
{
    private readonly IClientService _clientService = Substitute.For<IClientService>();
    private readonly ClientCsvImportService _service;

    public ClientCsvImportServiceTests()
    {
        _service = new ClientCsvImportService(_clientService, Substitute.For<ILogger<ClientCsvImportService>>());
    }

    private static Stream ToStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    [Fact]
    public async Task PreviewAsync_NewClient_MapsFakturoidStyleColumns()
    {
        var csv = "Název;IČO;DIČ;Ulice;Město;PSČ;Email;Telefon\r\n" +
                  "ACME s.r.o.;12345678;CZ12345678;Hlavní 1;Praha;11000;info@acme.cz;123456789\r\n";
        _clientService.GetClientByRegistrationNumberAsync("12345678", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        var preview = await _service.PreviewAsync(ToStream(csv));

        preview.Rows.Count.ShouldBe(1);
        var row = preview.Rows[0];
        row.Status.ShouldBe(EClientImportRowStatus.New);
        row.Client.CompanyName.ShouldBe("ACME s.r.o.");
        row.Client.RegistrationNumber.ShouldBe("12345678");
        row.Client.TaxNumber.ShouldBe("CZ12345678");
        row.Client.IsVatPayer.ShouldBeTrue();
        row.Client.Address.Single().City.ShouldBe("Praha");
        row.Client.Contact.ShouldContain(c => c.ContactValue == "info@acme.cz");
        row.Client.FetchFromAres.ShouldBeFalse();
    }

    [Fact]
    public async Task PreviewAsync_IDokladStyleColumns_AreMapped()
    {
        // iDoklad-flavored header names (English-ish, different casing/spelling than Fakturoid).
        var csv = "Company Name,Registration Number,City,Postal Code\n" +
                  "Beta Inc,87654321,Brno,60200\n";
        _clientService.GetClientByRegistrationNumberAsync("87654321", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        var preview = await _service.PreviewAsync(ToStream(csv));

        preview.Rows.Single().Client.CompanyName.ShouldBe("Beta Inc");
        preview.Rows.Single().Client.Address.Single().City.ShouldBe("Brno");
    }

    [Fact]
    public async Task PreviewAsync_DuplicateInDatabase_IsFlagged()
    {
        var csv = "Název;IČO\r\nExisting Co;11111111\r\n";
        _clientService.GetClientByRegistrationNumberAsync("11111111", Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 42, CompanyName = "Existing Co" });

        var preview = await _service.PreviewAsync(ToStream(csv));

        var row = preview.Rows.Single();
        row.Status.ShouldBe(EClientImportRowStatus.Duplicate);
        row.ExistingClientId.ShouldBe(42L);
        preview.DuplicateCount.ShouldBe(1);
        preview.NewCount.ShouldBe(0);
    }

    [Fact]
    public async Task PreviewAsync_DuplicateWithinFile_IsFlaggedOnSecondOccurrence()
    {
        var csv = "Název;IČO\r\nFirst;22222222\r\nSecond;22222222\r\n";
        _clientService.GetClientByRegistrationNumberAsync("22222222", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        var preview = await _service.PreviewAsync(ToStream(csv));

        preview.Rows[0].Status.ShouldBe(EClientImportRowStatus.New);
        preview.Rows[1].Status.ShouldBe(EClientImportRowStatus.Duplicate);
        preview.Rows[1].Reason.ShouldContain("appears more than once");
    }

    [Fact]
    public async Task PreviewAsync_MissingCompanyName_IsInvalid()
    {
        var csv = "Název;IČO\r\n;33333333\r\n";

        var preview = await _service.PreviewAsync(ToStream(csv));

        preview.Rows.Single().Status.ShouldBe(EClientImportRowStatus.Invalid);
        preview.InvalidCount.ShouldBe(1);
        await _clientService.DidNotReceive().GetClientByRegistrationNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewAsync_UnknownColumns_AreIgnoredButReported()
    {
        var csv = "Název;Nějaký cizí sloupec\r\nAcme;xyz\r\n";

        var preview = await _service.PreviewAsync(ToStream(csv));

        preview.Rows.Single().Status.ShouldBe(EClientImportRowStatus.New);
        preview.UnknownColumns.ShouldContain("nejaky cizi sloupec");
    }

    [Fact]
    public async Task ConfirmAsync_CreatesOnlyNewClients_AndCountsResult()
    {
        var clients = new List<CreateClientDto>
        {
            new() { CompanyName = "New Co", RegistrationNumber = "44444444" }
        };
        _clientService.GetClientByRegistrationNumberAsync("44444444", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);
        _clientService.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 100, CompanyName = "New Co" });

        var result = await _service.ConfirmAsync(new ClientImportConfirmDto { Clients = clients });

        result.CreatedCount.ShouldBe(1);
        result.CreatedClientIds.ShouldBe(new[] { 100L });
        result.SkippedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ConfirmAsync_ReImportingSameFile_CreatesNothing()
    {
        // Simulates confirming the same CSV twice: the second time, the client already exists.
        var clients = new List<CreateClientDto>
        {
            new() { CompanyName = "Repeat Co", RegistrationNumber = "55555555" }
        };
        _clientService.GetClientByRegistrationNumberAsync("55555555", Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 7, CompanyName = "Repeat Co" });

        var result = await _service.ConfirmAsync(new ClientImportConfirmDto { Clients = clients });

        result.CreatedCount.ShouldBe(0);
        result.SkippedCount.ShouldBe(1);
        await _clientService.DidNotReceive().CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmAsync_DuplicateWithinRequest_SkipsSecondOccurrence()
    {
        var clients = new List<CreateClientDto>
        {
            new() { CompanyName = "First", RegistrationNumber = "66666666" },
            new() { CompanyName = "Second", RegistrationNumber = "66666666" }
        };
        _clientService.GetClientByRegistrationNumberAsync("66666666", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);
        _clientService.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 1, CompanyName = "First" });

        var result = await _service.ConfirmAsync(new ClientImportConfirmDto { Clients = clients });

        result.CreatedCount.ShouldBe(1);
        result.SkippedCount.ShouldBe(1);
    }

    [Fact]
    public async Task ConfirmAsync_ServiceThrows_RecordsErrorAndContinues()
    {
        var clients = new List<CreateClientDto>
        {
            new() { CompanyName = "Bad Co", RegistrationNumber = "77777777" },
            new() { CompanyName = "Good Co", RegistrationNumber = "88888888" }
        };
        _clientService.GetClientByRegistrationNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);
        _clientService.CreateClientAsync(Arg.Is<CreateClientDto>(c => c.CompanyName == "Bad Co"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ClientDto>(new InvalidOperationException("boom")));
        _clientService.CreateClientAsync(Arg.Is<CreateClientDto>(c => c.CompanyName == "Good Co"), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, CompanyName = "Good Co" });

        var result = await _service.ConfirmAsync(new ClientImportConfirmDto { Clients = clients });

        result.CreatedCount.ShouldBe(1);
        result.Errors.ShouldContain(e => e.Contains("Bad Co"));
    }
}
