// ============================================================================
// NumberSequencesPageTests — bUnit coverage for the document-type label in the
// /number-sequences grid.
//
// The grid used to decide the label with "Invoice ? Invoice : Credit note", so the
// seeded Proforma (PF-) and tax-receipt (DPP-) sequences were shown as "Dobropis".
// A tenant whose real credit-note sequence was missing therefore saw what looked like
// a default credit-note sequence, while the readiness check kept reporting it missing.
//
// Rendered with the REAL resource files (Czech culture): the point is what the user
// reads, and a key-echoing localizer cannot show a wrong translation.
// ============================================================================

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Blazored.LocalStorage;
using Bunit;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the number-sequence grid on <see cref="NumberSequences"/>.
/// </summary>
public class NumberSequencesPageTests : BunitContext, IAsyncLifetime
{
    private readonly SequenceApiStub _api = new();

    // The culture is a thread-level setting and xUnit reuses threads across test classes,
    // so it is put back after each test instead of leaking into unrelated ones.
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

    // xUnit disposes test classes synchronously; route disposal through IAsyncLifetime
    // (same reason as IntegrationsPageTests).
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        CultureInfo.CurrentUICulture = _originalUiCulture;
        await base.DisposeAsync();
    }

    public NumberSequencesPageTests()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("cs-CZ");

        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var httpClient = new HttpClient(_api) { BaseAddress = new Uri("https://api.test.invalid") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        Services.AddSingleton(factory);
        Services.AddSingleton(Substitute.For<AuthenticationStateProvider>());
        Services.AddSingleton<NumberSequenceApiService>();

        // FakvioGrid dependencies (same wiring as InvoicesTypeFilterTests).
        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddSingleton<GridStateService>();
        var preferencesState = Substitute.For<UserPreferencesState>();
        preferencesState.Preferences.Returns(new UserPreferencesDto());
        preferencesState.EnsureLoadedAsync().Returns(new UserPreferencesDto());
        Services.AddSingleton(preferencesState);

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Theory]
    [InlineData(EDocumentType.Invoice, "Faktura")]
    [InlineData(EDocumentType.CreditNote, "Dobropis")]
    [InlineData(EDocumentType.Proforma, "Zálohová faktura (proforma)")]
    [InlineData(EDocumentType.TaxReceiptForAdvance, "Daňový doklad o přijaté platbě")]
    public void EachDocumentType_GetsItsOwnLabel(EDocumentType type, string expectedLabel)
    {
        _api.Sequences = [Sequence(type)];

        var cut = Render<NumberSequences>();

        cut.WaitForAssertion(() => ChipTexts(cut).ShouldBe([expectedLabel]));
    }

    [Fact]
    public void ProformaAndTaxReceiptSequences_AreNotPresentedAsCreditNotes()
    {
        // The exact production situation: only the seeded non-invoice defaults exist, no
        // credit-note sequence. Nothing in the grid may claim otherwise.
        _api.Sequences = [Sequence(EDocumentType.Proforma), Sequence(EDocumentType.TaxReceiptForAdvance)];

        var cut = Render<NumberSequences>();

        cut.WaitForAssertion(() => ChipTexts(cut).Count.ShouldBe(2));
        ChipTexts(cut).ShouldNotContain("Dobropis");
    }

    /// <summary>
    /// Texts of the document-type chips. The grid's other chips (status, default) carry
    /// fixed labels, so they are filtered out by content rather than by position.
    /// </summary>
    private static List<string> ChipTexts(IRenderedComponent<NumberSequences> cut)
    {
        string[] otherChips = ["Aktivní", "Neaktivní", "Výchozí"];
        return cut.FindAll(".mud-chip")
            .Select(c => c.TextContent.Trim())
            .Where(t => !otherChips.Contains(t))
            .ToList();
    }

    private static NumberSequenceDto Sequence(EDocumentType type) => new()
    {
        Id = (long)type,
        Name = $"Default {type} Sequence",
        DocumentType = type,
        Prefix = "X-",
        IsDefault = false,
        IsActive = true
    };

    /// <summary>Answers the two list endpoints the page loads on start; everything else is empty.</summary>
    private sealed class SequenceApiStub : HttpMessageHandler
    {
        public List<NumberSequenceDto> Sequences { get; set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            HttpContent content = path.EndsWith("/api/numbersequence", StringComparison.OrdinalIgnoreCase)
                ? JsonContent.Create(Sequences)
                : new StringContent("[]", Encoding.UTF8, "application/json");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
