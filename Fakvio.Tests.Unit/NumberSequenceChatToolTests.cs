using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the three number-sequence chat tools (issue #224):
///   - ListNumberSequencesTool   (read-only)
///   - CreateNumberSequenceTool  (confirmable write)
///   - UpdateNumberSequenceTool  (confirmable write)
///
/// Everything is mocked with NSubstitute — no database. What is verified is what the tools own:
/// parameter parsing, the DTO they hand to <see cref="INumberSequenceService"/>, the error paths,
/// and that a preview really writes nothing.
///
/// Junior note: the confirm gate itself lives in ChatToolExecutor and is tested in
/// <see cref="ChatToolExecutorTests"/>. Here the two halves are called directly —
/// <c>BuildPreviewAsync</c> is what the executor calls without <c>confirm: true</c>,
/// <c>ExecuteAsync</c> is what it calls with it.
/// </summary>
public class NumberSequenceChatToolTests
{
    // ─── Shared builders ──────────────────────────────────────────────────

    private static NumberSequenceFormatDto BuildFormat(long id = 1)
        => new()
        {
            Id = id,
            Name = "Standard yearly format (yyyyNNN)",
            FormatPattern = "yyyyNNN",
            CounterDigits = 3,
            ResetsYearly = true,
            IsActive = true
        };

    private static NumberSequenceDto BuildSequence(
        long id = 5,
        string name = "Faktury",
        string? prefix = "FV-",
        int currentNumber = 41,
        bool isDefault = true,
        EDocumentType documentType = EDocumentType.Invoice)
        => new()
        {
            Id = id,
            Name = name,
            DocumentType = documentType,
            Prefix = prefix,
            CurrentNumber = currentNumber,
            IsDefault = isDefault,
            NumberSequenceFormatId = 1,
            NumberSequenceFormat = BuildFormat(),
            IsActive = true
        };

    /// <summary>
    /// A service stub that knows one sequence and one format, and echoes writes back. Individual
    /// tests override the pieces they care about.
    /// </summary>
    private static INumberSequenceService StubService(NumberSequenceDto? sequence = null)
    {
        var stored = sequence ?? BuildSequence();
        var service = Substitute.For<INumberSequenceService>();

        service.GetAllSequencesAsync(Arg.Any<EDocumentType?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([stored]);
        service.GetAllFormatsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([BuildFormat()]);
        service.GetFormatByIdAsync(1, Arg.Any<CancellationToken>()).Returns(BuildFormat());
        service.GetSequenceByIdAsync(stored.Id, Arg.Any<CancellationToken>()).Returns(stored);
        service.GetDefaultSequenceAsync(Arg.Any<EDocumentType>(), Arg.Any<CancellationToken>())
            .Returns(stored.IsDefault ? stored : null);
        service.CreateSequenceAsync(Arg.Any<CreateNumberSequenceDto>(), Arg.Any<CancellationToken>())
            .Returns(stored);
        service.UpdateSequenceAsync(stored.Id, Arg.Any<UpdateNumberSequenceDto>(), Arg.Any<CancellationToken>())
            .Returns(stored);
        service.SetAsDefaultAsync(stored.Id, Arg.Any<CancellationToken>()).Returns(stored);

        return service;
    }

    private static ListNumberSequencesTool CreateListTool(INumberSequenceService service)
        => new(service, Substitute.For<ILogger<ListNumberSequencesTool>>());

    private static CreateNumberSequenceTool CreateCreateTool(INumberSequenceService service)
        => new(service, Substitute.For<ILogger<CreateNumberSequenceTool>>());

    private static UpdateNumberSequenceTool CreateUpdateTool(INumberSequenceService service)
        => new(service, Substitute.For<ILogger<UpdateNumberSequenceTool>>());

    /// <summary>Pulls the DTO the tool handed to CreateSequenceAsync.</summary>
    private static CreateNumberSequenceDto CapturedCreate(INumberSequenceService service)
        => (CreateNumberSequenceDto)service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(INumberSequenceService.CreateSequenceAsync))
            .GetArguments()[0]!;

    /// <summary>Pulls the DTO the tool handed to UpdateSequenceAsync.</summary>
    private static UpdateNumberSequenceDto CapturedUpdate(INumberSequenceService service)
        => (UpdateNumberSequenceDto)service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(INumberSequenceService.UpdateSequenceAsync))
            .GetArguments()[1]!;

    // ═══════════════════════════════════════════════════════════════════════
    //  ListNumberSequencesTool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task List_WithoutParameters_ListsSequencesAndFormats()
    {
        var service = StubService();

        var result = await CreateListTool(service).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("ID=5");
        result.OutputText.ShouldContain("Faktury");
        // The counter and the next number are both spelled out — the model must not report 41
        // as the number the next invoice will get.
        result.OutputText.ShouldContain("counter: 41 (next number 42)");
        // Formats carry the IDs create_number_sequence needs, so they belong in every listing.
        result.OutputText.ShouldContain("yyyyNNN");

        await service.Received(1).GetAllSequencesAsync(null, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ParsesDocumentTypeAndIncludeInactive()
    {
        var service = StubService();

        var result = await CreateListTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["document_type"] = "CreditNote",
            ["include_inactive"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("CreditNote");

        await service.Received(1)
            .GetAllSequencesAsync(EDocumentType.CreditNote, true, Arg.Any<CancellationToken>());
        await service.Received(1).GetAllFormatsAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_WithNoSequences_SaysSoInsteadOfReturningNothing()
    {
        var service = StubService();
        service.GetAllSequencesAsync(Arg.Any<EDocumentType?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await CreateListTool(service).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("none defined yet");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CreateNumberSequenceTool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_Preview_DescribesTheSequenceAndWritesNothing()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["name"] = "Faktury 2026",
            ["document_type"] = "Invoice",
            ["format_id"] = "1",
            ["prefix"] = "FV-",
            ["starting_number"] = "100"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Faktury 2026");
        // StartingNumber 100 means the counter starts at 99 — the preview has to show the number
        // the user will really get, not the stored counter.
        result.OutputText.ShouldContain("next number 100");
        // Not asked to be the default, and one already exists → the existing one is named.
        result.OutputText.ShouldContain("stays the default sequence");

        await service.DidNotReceive()
            .CreateSequenceAsync(Arg.Any<CreateNumberSequenceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_Preview_WarnsWhenTheDocumentTypeWouldStillHaveNoDefault()
    {
        var service = StubService(BuildSequence(isDefault: false));
        service.GetDefaultSequenceAsync(Arg.Any<EDocumentType>(), Arg.Any<CancellationToken>())
            .Returns((NumberSequenceDto?)null);

        var result = await CreateCreateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["name"] = "Faktury 2026",
            ["document_type"] = "Invoice",
            ["format_id"] = "1"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("would still have no numbering");
    }

    [Fact]
    public async Task Create_Preview_NamesTheDefaultItWouldReplace()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["name"] = "Faktury 2026",
            ["document_type"] = "Invoice",
            ["format_id"] = "1",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("replacing 'Faktury' (ID=5)");
    }

    [Fact]
    public async Task Create_UnknownFormat_FailsAndPointsAtTheListTool()
    {
        var service = StubService();
        service.GetFormatByIdAsync(9, Arg.Any<CancellationToken>()).Returns((NumberSequenceFormatDto?)null);

        var result = await CreateCreateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "Faktury 2026",
            ["document_type"] = "Invoice",
            ["format_id"] = "9"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("no numbering format with ID 9");
        result.ErrorMessage.ShouldContain("list_number_sequences");

        await service.DidNotReceive()
            .CreateSequenceAsync(Arg.Any<CreateNumberSequenceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_StartingNumberBelowOne_IsRefused()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "Faktury 2026",
            ["document_type"] = "Invoice",
            ["format_id"] = "1",
            ["starting_number"] = "0"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("must be 1 or higher");
    }

    [Fact]
    public async Task Create_MapsEveryParameterOntoTheDto()
    {
        var service = StubService();

        var result = await CreateCreateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["name"] = "  Faktury 2026  ",
            ["document_type"] = "creditnote",   // models are inconsistent about casing
            ["format_id"] = "1",
            ["prefix"] = "DB-",
            ["suffix"] = "/26",
            ["starting_number"] = "100",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        var dto = CapturedCreate(service);
        dto.Name.ShouldBe("Faktury 2026");
        dto.DocumentType.ShouldBe(EDocumentType.CreditNote);
        dto.NumberSequenceFormatId.ShouldBe(1);
        dto.Prefix.ShouldBe("DB-");
        dto.Suffix.ShouldBe("/26");
        dto.StartingNumber.ShouldBe(100);
        dto.IsDefault.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UpdateNumberSequenceTool
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Update_UnknownId_FailsAndPointsAtTheListTool()
    {
        var service = StubService();
        service.GetSequenceByIdAsync(99, Arg.Any<CancellationToken>()).Returns((NumberSequenceDto?)null);

        var result = await CreateUpdateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["id"] = "99",
            ["name"] = "Nová"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("no number sequence with ID 99");
    }

    [Fact]
    public async Task Update_ClearingTheDefaultFlag_IsRefused()
    {
        var service = StubService();

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "5",
            ["is_default"] = "false"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("only moved");

        await service.DidNotReceive().UpdateSequenceAsync(
            Arg.Any<long>(), Arg.Any<UpdateNumberSequenceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_WithoutAnyRealChange_Fails()
    {
        var service = StubService();

        // The name sent is the one already stored — nothing would happen, so the tool says so
        // rather than reporting a change that never took place.
        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "5",
            ["name"] = "Faktury"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("No change was requested");
    }

    [Fact]
    public async Task Update_NegativeCounter_IsRefused()
    {
        var service = StubService();

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "5",
            ["current_number"] = "-1"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("lowest valid value is 0");
    }

    [Fact]
    public async Task Update_Preview_WarnsWhenTheCounterGoesBackwards()
    {
        var service = StubService();

        var result = await CreateUpdateTool(service).BuildPreviewAsync(new Dictionary<string, string>
        {
            ["id"] = "5",
            ["current_number"] = "10"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("counter: 41 → 10 (next number 11)");
        result.OutputText.ShouldContain("WARNING");

        await service.DidNotReceive().UpdateSequenceAsync(
            Arg.Any<long>(), Arg.Any<UpdateNumberSequenceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_SendsOnlyTheFieldsTheModelSupplied()
    {
        var service = StubService();

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "5",
            ["name"] = "Faktury 2026",
            ["current_number"] = "100"
        });

        result.IsSuccess.ShouldBeTrue();

        var dto = CapturedUpdate(service);
        dto.Name.ShouldBe("Faktury 2026");
        dto.CurrentNumber.ShouldBe(100);
        // Untouched fields stay null — UpdateSequenceAsync applies every non-null property, so a
        // carried-over value would overwrite something the user never mentioned.
        dto.Prefix.ShouldBeNull();
        dto.Suffix.ShouldBeNull();
    }

    [Fact]
    public async Task Update_DefaultOnly_MovesTheFlagWithoutAPointlessWrite()
    {
        var service = StubService(BuildSequence(isDefault: false));

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "5",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        await service.Received(1).SetAsDefaultAsync(5, Arg.Any<CancellationToken>());
        await service.DidNotReceive().UpdateSequenceAsync(
            Arg.Any<long>(), Arg.Any<UpdateNumberSequenceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Update_AskingForADefaultThatIsAlreadyDefault_ChangesNothing()
    {
        var service = StubService();   // the stored sequence is already the default one

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "5",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("No change was requested");

        await service.DidNotReceive().SetAsDefaultAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The rename went through and only the default move found nothing. Reporting "no such
    /// sequence" would deny a write that already happened (review nit 3).
    /// </summary>
    [Fact]
    public async Task Update_WhenTheDefaultMoveFindsNothingAfterAWrite_ReportsThePartialWrite()
    {
        var service = StubService(BuildSequence(isDefault: false));
        service.SetAsDefaultAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((NumberSequenceDto?)null);

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "5",
            ["name"] = "Faktury vydané",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("was updated, but the default flag could not be moved");
    }

    /// <summary>
    /// Nothing was written before the default move, so here "no such sequence" IS the truth —
    /// the counterweight that keeps the message above from being used everywhere.
    /// </summary>
    [Fact]
    public async Task Update_WhenTheDefaultMoveFindsNothingWithoutAWrite_ReportsMissingSequence()
    {
        var service = StubService(BuildSequence(isDefault: false));
        service.SetAsDefaultAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((NumberSequenceDto?)null);

        var result = await CreateUpdateTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "5",
            ["is_default"] = "true"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("no number sequence with ID 5");
    }
}
