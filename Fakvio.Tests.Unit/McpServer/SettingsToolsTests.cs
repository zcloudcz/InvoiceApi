using System.Net;
using System.Text.Json;
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
}
