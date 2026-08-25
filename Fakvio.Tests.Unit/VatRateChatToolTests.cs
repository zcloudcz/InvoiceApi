using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the three VAT-rate chat tools (issue #224):
///   - ListVatRatesTool   (read-only)
///   - CreateVatRateTool  (confirmable write)
///   - UpdateVatRateTool  (confirmable write)
///
/// Everything is mocked with NSubstitute — no database. What is verified is what the tools own:
/// parameter parsing, the DTO they hand to <see cref="IVatRateService"/>, the error paths, and
/// that a preview really writes nothing.
///
/// Junior note: the confirm gate itself lives in ChatToolExecutor and is tested in
/// <see cref="ChatToolExecutorTests"/>. Here the two halves are called directly.
/// </summary>
public class VatRateChatToolTests
{
    // ─── Shared builders ──────────────────────────────────────────────────

    private static VatRateDto BuildRate(
        long id = 3,
        string name = "DPH 21% základní",
        decimal rate = 21m,
        bool isReduced = false,
        bool isDefault = true,
        DateTime? validTo = null)
        => new()
        {
            Id = id,
            Name = name,
            Rate = rate,
            ValidFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidTo = validTo,
            IsReduced = isReduced,
            IsDefault = isDefault,
            IsActive = true
        };

    /// <summary>
    /// A service stub that knows one rate and echoes writes back. Individual tests override the
    /// pieces they care about.
    /// </summary>
    private static IVatRateService StubService(VatRateDto? rate = null)
    {
        var stored = rate ?? BuildRate();
        var service = Substitute.For<IVatRateService>();

        service.GetAllVatRatesAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns([stored]);
        service.GetVatRateByIdAsync(stored.Id, Arg.Any<CancellationToken>()).Returns(stored);
        service.GetDefaultStandardRateAsync(Arg.Any<CancellationToken>())
            .Returns(stored is { IsDefault: true, IsReduced: false } ? stored : null);
        service.GetDefaultReducedRateAsync(Arg.Any<CancellationToken>())
            .Returns(stored is { IsDefault: true, IsReduced: true } ? stored : null);
        service.CreateVatRateAsync(Arg.Any<CreateVatRateDto>(), Arg.Any<CancellationToken>()).Returns(stored);
        service.UpdateVatRateAsync(stored.Id, Arg.Any<UpdateVatRateDto>(), Arg.Any<CancellationToken>())
            .Returns(stored);
        service.SetAsDefaultAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(stored);

        return service;
    }

    private static ListVatRatesTool CreateListTool(IVatRateService service)
        => new(service, Substitute.For<ILogger<ListVatRatesTool>>());

    private static CreateVatRateTool CreateCreateTool(IVatRateService service)
        => new(service, Substitute.For<ILogger<CreateVatRateTool>>());

    private static UpdateVatRateTool CreateUpdateTool(IVatRateService service)
        => new(service, Substitute.For<ILogger<UpdateVatRateTool>>());

    /// <summary>Pulls the DTO the tool handed to CreateVatRateAsync.</summary>
    private static CreateVatRateDto CapturedCreate(IVatRateService service)
        => (CreateVatRateDto)service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IVatRateService.CreateVatRateAsync))
            .GetArguments()[0]!;

    /// <summary>Pulls the DTO the tool handed to UpdateVatRateAsync.</summary>
    private static UpdateVatRateDto CapturedUpdate(IVatRateService service)
        => (UpdateVatRateDto)service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IVatRateService.UpdateVatRateAsync))
            .GetArguments()[1]!;

    // ═══════════════════════════════════════════════════════════════════════
    //  ListVatRatesTool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task List_RendersRateWithIdKindAndValidity()
    {
        var service = StubService();

        var result = await CreateListTool(service).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("ID=3");
        // Trailing zeros are stripped — the stored 21.00 must not read as a different rate.
        result.OutputText.ShouldContain("21 %");
        result.OutputText.ShouldContain("standard");
        result.OutputText.ShouldContain("valid 2024-01-01 → open-ended");
        result.OutputText.ShouldContain("default");

        await service.Received(1).GetAllVatRatesAsync(false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_PassesIncludeInactiveThrough()
    {
        var service = StubService();

        var result = await CreateListTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["include_inactive"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).GetAllVatRatesAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_WithNoRates_SaysSoInsteadOfReturningNothing()
    {
        var service = StubService();
        service.GetAllVatRatesAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await CreateListTool(service).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("none defined yet");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CreateVatRateTool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_Preview_DescribesTheRateAndWritesNothing()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["name"] = "DPH 12% snížená",
            ["rate"] = "12",
            ["valid_from"] = "2026-01-01",
            ["is_reduced"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("12 %");
        result.OutputText.ShouldContain("reduced");
        result.OutputText.ShouldContain("valid 2026-01-01");
        result.OutputText.ShouldContain("The current default reduced rate stays unchanged");

        await service.DidNotReceive()
            .CreateVatRateAsync(Arg.Any<CreateVatRateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_Preview_NamesTheDefaultItWouldReplace()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["name"] = "DPH 20% základní",
            ["rate"] = "20",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("replacing 'DPH 21% základní' (ID=3)");
    }

    [Fact]
    public async Task Create_WithoutValidFrom_StartsToday()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "DPH 12% snížená",
            ["rate"] = "12",
            ["is_reduced"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        var dto = CapturedCreate(service);
        dto.ValidFrom.ShouldBe(DateTime.UtcNow.Date);
        // Npgsql rejects any other Kind against a "timestamp with time zone" column.
        dto.ValidFrom.Kind.ShouldBe(DateTimeKind.Utc);
        dto.ValidTo.ShouldBeNull();
        dto.IsReduced.ShouldBeTrue();
        dto.IsActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    public async Task Create_PercentageOutsideZeroToHundred_IsRefused(string rate)
    {
        var service = StubService();

        var result = await CreateCreateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "Nesmysl",
            ["rate"] = rate
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("between 0 and 100");

        await service.DidNotReceive()
            .CreateVatRateAsync(Arg.Any<CreateVatRateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_UnreadableDate_FailsInsteadOfSilentlyIgnoringIt()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "DPH 12% snížená",
            ["rate"] = "12",
            ["valid_from"] = "leden 2026"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Invalid valid_from");
    }

    [Fact]
    public async Task Create_ValidityWindowBackwards_IsRefused()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "DPH 12% snížená",
            ["rate"] = "12",
            ["valid_from"] = "2026-06-01",
            ["valid_to"] = "2026-01-01"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("backwards");
    }

    [Fact]
    public async Task Create_AsDefault_CreatesFirstAndMovesTheFlagAfterwards()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "DPH 20% základní",
            ["rate"] = "20",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        // CreateVatRateAsync refuses a second default of the same kind, so the flag is never sent
        // with the create — SetAsDefaultAsync unsets the previous holder instead.
        CapturedCreate(service).IsDefault.ShouldBeFalse();
        await service.Received(1).SetAsDefaultAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithoutDefaultFlag_DoesNotTouchTheDefault()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "DPH 20% základní",
            ["rate"] = "20"
        });

        result.IsSuccess.ShouldBeTrue();
        await service.DidNotReceive().SetAsDefaultAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UpdateVatRateTool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_UnknownId_FailsAndPointsAtTheListTool()
    {
        var service = StubService();
        service.GetVatRateByIdAsync(99, Arg.Any<CancellationToken>()).Returns((VatRateDto?)null);

        var result = await CreateUpdateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["id"] = "99",
            ["rate"] = "15"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("no VAT rate with ID 99");
    }

    [Fact]
    public async Task Update_ClearingTheDefaultFlag_IsRefused()
    {
        var service = StubService();

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["is_default"] = "false"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("only moved");

        await service.DidNotReceive().UpdateVatRateAsync(
            Arg.Any<long>(), Arg.Any<UpdateVatRateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_WithoutAnyRealChange_Fails()
    {
        var service = StubService();

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["rate"] = "21"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("No change was requested");
    }

    [Fact]
    public async Task Update_Preview_ShowsWasAndWillBeAndWritesNothing()
    {
        var service = StubService();

        var result = await CreateUpdateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["rate"] = "19",
            ["valid_to"] = "2026-12-31"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("rate: 21 % → 19 %");
        result.OutputText.ShouldContain("valid to: (none) → 2026-12-31");

        await service.DidNotReceive().UpdateVatRateAsync(
            Arg.Any<long>(), Arg.Any<UpdateVatRateDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_CarriesOverEveryFieldTheModelDidNotSend()
    {
        var service = StubService();

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["rate"] = "19"
        });

        result.IsSuccess.ShouldBeTrue();

        // UpdateVatRateAsync overwrites every property from the DTO, so anything not merged in
        // would be silently erased.
        var dto = CapturedUpdate(service);
        dto.Rate.ShouldBe(19m);
        dto.Name.ShouldBe("DPH 21% základní");
        dto.ValidFrom.ShouldBe(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        dto.IsReduced.ShouldBeFalse();
        dto.IsDefault.ShouldBeTrue();
        dto.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_MovingTheDefault_UsesSetAsDefaultAfterTheUpdate()
    {
        var service = StubService(BuildRate(isDefault: false));

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        // The flag is carried over as stored; only SetAsDefaultAsync raises it, because
        // UpdateVatRateAsync would refuse a second default of the same kind.
        CapturedUpdate(service).IsDefault.ShouldBeFalse();
        await service.Received(1).SetAsDefaultAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_PercentageOutsideZeroToHundred_IsRefused()
    {
        var service = StubService();

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["rate"] = "150"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("between 0 and 100");
    }

    [Fact]
    public async Task Update_ValidityWindowBackwards_IsRefused()
    {
        var service = StubService();

        // Only valid_to is sent, so it is compared against the STORED valid_from (2024-01-01).
        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["valid_to"] = "2023-01-01"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("backwards");
    }

    /// <summary>
    /// "Překlop 21 % na sníženou" over the default standard rate. The merge carries IsDefault
    /// along, so without the guard the flag would follow the rate to the other kind and leave the
    /// standard kind with no default at all — or, when the reduced kind already has one, the
    /// service would throw. Both are refused by the tool, in the preview, before the user confirms.
    /// </summary>
    [Fact]
    public async Task Update_ChangingKindOfTheDefaultRate_IsRefusedInThePreview()
    {
        var service = StubService(BuildRate(isReduced: false, isDefault: true));

        var result = await CreateUpdateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["is_reduced"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("default one of its kind");
        result.ErrorMessage.ShouldContain("is_default: true");
    }

    [Fact]
    public async Task Update_ChangingKindOfTheDefaultRate_WritesNothing()
    {
        var service = StubService(BuildRate(isReduced: false, isDefault: true));

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["name"] = "DPH 15% snížená",
            ["is_reduced"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();

        // Not even the rename goes through — the whole call is refused, so the record cannot end
        // up half-changed.
        await service.DidNotReceive().UpdateVatRateAsync(
            Arg.Any<long>(), Arg.Any<UpdateVatRateDto>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().SetAsDefaultAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Counterweight to the two tests above: a rate that does NOT hold the default flag may change
    /// kind freely. Catches a guard written too wide.
    /// </summary>
    [Fact]
    public async Task Update_ChangingKindOfANonDefaultRate_IsAllowed()
    {
        var service = StubService(BuildRate(isReduced: false, isDefault: false));

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["is_reduced"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();
        CapturedUpdate(service).IsReduced.ShouldBeTrue();
    }

    /// <summary>
    /// The two-step write is not atomic: the field update can succeed and the default move still
    /// find nothing to promote. The report must name both halves instead of claiming the default
    /// moved (review nit 3).
    /// </summary>
    [Fact]
    public async Task Update_WhenTheDefaultMoveFindsNothing_ReportsThePartialWrite()
    {
        var service = StubService(BuildRate(isDefault: false));
        service.SetAsDefaultAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns((VatRateDto?)null);

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "3",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("was updated, but the default flag could not be moved");
    }
}
