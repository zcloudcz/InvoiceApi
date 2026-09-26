using System.Text.Json;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
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
}
