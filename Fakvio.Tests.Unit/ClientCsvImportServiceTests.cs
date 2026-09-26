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
/// See DEVGUIDE.md §4.14.
/// </summary>
public class ClientCsvImportServiceTests
{
    private readonly IClientService _clientService = Substitute.For<IClientService>();
    private readonly ClientCsvImportService _service;

    public ClientCsvImportServiceTests()
    {
        _service = new ClientCsvImportService(_clientService, Substitute.For<ILogger<ClientCsvImportService>>());

        // Default: bulk existence check finds nothing — most tests override this per-case.
        StubExisting();
    }

    private static Stream ToStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    /// <summary>Stubs the bulk IČO existence check used by both PreviewAsync and ConfirmAsync.</summary>
    private void StubExisting(params (string Ico, long Id)[] existing)
    {
        _clientService.GetClientIdsByRegistrationNumbersAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(existing.ToDictionary(e => e.Ico, e => e.Id));
    }

    [Fact]
    public async Task PreviewAsync_NewClient_MapsFakturoidStyleColumns()
    {
        var csv = "Název;IČO;DIČ;Ulice;Město;PSČ;Email;Telefon\r\n" +
                  "ACME s.r.o.;12345678;CZ12345678;Hlavní 1;Praha;11000;info@acme.cz;123456789\r\n";

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

        var preview = await _service.PreviewAsync(ToStream(csv));

        preview.Rows.Single().Client.CompanyName.ShouldBe("Beta Inc");
        preview.Rows.Single().Client.Address.Single().City.ShouldBe("Brno");
    }

    [Fact]
    public async Task PreviewAsync_DuplicateInDatabase_IsFlagged()
    {
        var csv = "Název;IČO\r\nExisting Co;11111111\r\n";
        StubExisting(("11111111", 42));

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
    }

    /// <summary>
    /// The DB requires RegistrationNumber (unique, non-null — see TenantDbContext), even though
    /// CreateClientDto documents it as optional for physical persons. A row without one would
    /// pass preview as "New" but always fail on confirm, so preview must flag it up front.
    /// </summary>
    [Fact]
    public async Task PreviewAsync_MissingRegistrationNumber_IsInvalid()
    {
        var csv = "Název;IČO\r\nNo Ico Co;\r\n";

        var preview = await _service.PreviewAsync(ToStream(csv));

        var row = preview.Rows.Single();
        row.Status.ShouldBe(EClientImportRowStatus.Invalid);
        row.Reason.ShouldContain("IČO");
    }

    [Fact]
    public async Task PreviewAsync_UnknownColumns_AreIgnoredButReported()
    {
        var csv = "Název;Nějaký cizí sloupec\r\nAcme;xyz\r\n";

        var preview = await _service.PreviewAsync(ToStream(csv));

        preview.Rows.Single().Status.ShouldBe(EClientImportRowStatus.Invalid); // no IČO column in this fixture
        preview.UnknownColumns.ShouldContain("nejaky cizi sloupec");
    }

    [Fact]
    public async Task ConfirmAsync_CreatesOnlyNewClients_AndCountsResult()
    {
        var clients = new List<CreateClientDto>
        {
            new() { CompanyName = "New Co", RegistrationNumber = "44444444" }
        };
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
        StubExisting(("55555555", 7));

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
        _clientService.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 1, CompanyName = "First" });

        var result = await _service.ConfirmAsync(new ClientImportConfirmDto { Clients = clients });

        result.CreatedCount.ShouldBe(1);
        result.SkippedCount.ShouldBe(1);
    }

    /// <summary>
    /// A race the bulk pre-check can't catch (the DB row appeared *during* this confirm call, not
    /// before it started) still surfaces as a duplicate outcome (Skipped), not a hard failure —
    /// ClientService.CreateClientAsync's own guard throws InvalidOperationException for this case.
    /// </summary>
    [Fact]
    public async Task ConfirmAsync_ConcurrentDuplicateFromCreateClientAsync_IsSkippedNotError()
    {
        var clients = new List<CreateClientDto> { new() { CompanyName = "Racer", RegistrationNumber = "99999999" } };
        _clientService.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ClientDto>(new InvalidOperationException("Client with registration number 99999999 already exists")));

        var result = await _service.ConfirmAsync(new ClientImportConfirmDto { Clients = clients });

        result.SkippedCount.ShouldBe(1);
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConfirmAsync_ServiceThrows_RecordsSafeErrorAndContinues()
    {
        var clients = new List<CreateClientDto>
        {
            new() { CompanyName = "Bad Co", RegistrationNumber = "77777777" },
            new() { CompanyName = "Good Co", RegistrationNumber = "88888888" }
        };
        _clientService.CreateClientAsync(Arg.Is<CreateClientDto>(c => c.CompanyName == "Bad Co"), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ClientDto>(new ApplicationException("Npgsql: column companies.internal_secret does not exist")));
        _clientService.CreateClientAsync(Arg.Is<CreateClientDto>(c => c.CompanyName == "Good Co"), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, CompanyName = "Good Co" });

        var result = await _service.ConfirmAsync(new ClientImportConfirmDto { Clients = clients });

        result.CreatedCount.ShouldBe(1);
        result.Errors.ShouldContain(e => e.Contains("Bad Co"));
        // The raw exception message (which can carry EF/PostgreSQL/schema details) must never reach the caller.
        result.Errors.ShouldNotContain(e => e.Contains("Npgsql") || e.Contains("internal_secret"));
    }
}
