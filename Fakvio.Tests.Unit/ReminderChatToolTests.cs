using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the three reminder (dunning) chat tools (issue #227):
///   - ListRemindersTool          (read-only)
///   - GetReminderSettingsTool    (read-only)
///   - UpdateReminderSettingsTool (confirmable write)
///
/// Everything is mocked with NSubstitute — no database. What is verified is what the tools own:
/// parameter parsing, the filter / DTO they hand to <see cref="IReminderService"/>, the error
/// paths, and that a preview really writes nothing.
///
/// Junior note: the confirm gate itself lives in ChatToolExecutor and is tested in
/// <see cref="ChatToolExecutorTests"/>. Here the two halves are called directly —
/// <c>BuildPreviewAsync</c> is what the executor calls without <c>confirm: true</c>,
/// <c>ExecuteAsync</c> is what it calls with it.
/// </summary>
public class ReminderChatToolTests
{
    // ─── Shared builders ──────────────────────────────────────────────────

    private static ReminderSettingsDto BuildSettings(long? clientId = null, string? clientName = null)
        => new()
        {
            Id = 1,
            ClientId = clientId,
            ClientName = clientName,
            IsEnabled = true,
            MaxReminderLevel = 3,
            GracePeriodDays = 7,
            IncludeInterest = false,
            AttachInvoicePdf = true,
            AutoSendEmail = true,
            Levels =
            [
                new ReminderLevelDto { Id = 11, Level = 1, DaysAfterPrevious = 7, FixedFeeCzk = 0 },
                new ReminderLevelDto { Id = 12, Level = 2, DaysAfterPrevious = 14, FixedFeeCzk = 50, Subject = "Druhá upomínka" },
                new ReminderLevelDto { Id = 13, Level = 3, DaysAfterPrevious = 14, FixedFeeCzk = 200, EmailTemplateId = 9 }
            ]
        };

    private static ReminderDto BuildReminder(
        long id = 5,
        EReminderStatus status = EReminderStatus.Draft,
        string? sentToEmail = null,
        string? errorMessage = null)
        => new()
        {
            Id = id,
            InvoiceId = 100,
            InvoiceNumber = "2026-0042",
            ClientId = 3,
            ClientName = "ABC s.r.o.",
            Level = 2,
            Status = status,
            DueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            ReminderDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            InvoiceAmount = 12_100m,
            FeeCzk = 50m,
            InterestCzk = 12.5m,
            TotalCzk = 12_162.5m,
            SentToEmail = sentToEmail,
            ErrorMessage = errorMessage
        };

    /// <summary>Reminder service stub that returns one page with the supplied rows.</summary>
    private static IReminderService StubList(params ReminderDto[] reminders)
    {
        var service = Substitute.For<IReminderService>();
        service.GetRemindersPagedAsync(Arg.Any<ReminderFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ReminderDto>([.. reminders], reminders.Length, 1, 10));
        return service;
    }

    private static ListRemindersTool CreateListTool(IReminderService service)
        => new(service, Substitute.For<ILogger<ListRemindersTool>>());

    private static GetReminderSettingsTool CreateGetSettingsTool(
        IReminderService reminderService,
        IClientService? clientService = null)
        => new(reminderService,
            clientService ?? Substitute.For<IClientService>(),
            Substitute.For<ILogger<GetReminderSettingsTool>>());

    private static UpdateReminderSettingsTool CreateUpdateSettingsTool(IReminderService service)
        => new(service, Substitute.For<ILogger<UpdateReminderSettingsTool>>());

    /// <summary>Pulls the filter the list tool handed to the service.</summary>
    private static ReminderFilterDto CapturedFilter(IReminderService service)
        => (ReminderFilterDto)service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IReminderService.GetRemindersPagedAsync))
            .GetArguments()[0]!;

    /// <summary>Pulls the DTO the update tool handed to the service.</summary>
    private static UpdateReminderSettingsDto CapturedUpsert(IReminderService service)
        => (UpdateReminderSettingsDto)service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IReminderService.UpsertSettingsAsync))
            .GetArguments()[0]!;

    // ─── list_reminders ───────────────────────────────────────────────────

    [Fact]
    public async Task ListReminders_WithoutParameters_UsesFirstPageAndNoFilters()
    {
        var service = StubList(BuildReminder());

        var result = await CreateListTool(service).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();

        var filter = CapturedFilter(service);
        filter.Page.ShouldBe(1);
        filter.PageSize.ShouldBe(10);
        filter.Status.ShouldBeNull();
        filter.Level.ShouldBeNull();
        filter.Search.ShouldBeNull();
        filter.DateFrom.ShouldBeNull();
        filter.DateTo.ShouldBeNull();
    }

    [Fact]
    public async Task ListReminders_MapsEveryParameterOntoTheFilter()
    {
        var service = StubList(BuildReminder());

        await CreateListTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["status"] = "sent",          // lower case on purpose — models are inconsistent about casing
            ["level"] = "2",
            ["search"] = "  ABC  ",
            ["date_from"] = "2026-03-01",
            ["date_to"] = "15.3.2026",    // Czech form a user dictated and the model forwarded
            ["page"] = "3",
            ["page_size"] = "25"
        });

        var filter = CapturedFilter(service);
        filter.Status.ShouldBe(EReminderStatus.Sent);
        filter.Level.ShouldBe(2);
        filter.Search.ShouldBe("ABC");
        filter.Page.ShouldBe(3);
        filter.PageSize.ShouldBe(25);
        filter.DateFrom.ShouldBe(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        filter.DateTo.ShouldBe(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));

        // Npgsql rejects any other kind against 'timestamp with time zone' columns.
        filter.DateFrom!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("page", "0")]
    [InlineData("page", "-4")]
    public async Task ListReminders_IgnoresNonsensicalPageNumbers(string key, string value)
    {
        var service = StubList(BuildReminder());

        await CreateListTool(service).ExecuteAsync(new Dictionary<string, string> { [key] = value });

        CapturedFilter(service).Page.ShouldBe(1);
    }

    [Fact]
    public async Task ListReminders_ClampsAnAbsurdPageSize()
    {
        var service = StubList(BuildReminder());

        await CreateListTool(service).ExecuteAsync(new Dictionary<string, string> { ["page_size"] = "5000" });

        // The clamp lives in PaginationParams; the tool must not defeat it by assigning blindly.
        CapturedFilter(service).PageSize.ShouldBe(100);
    }

    [Fact]
    public async Task ListReminders_WithUnreadableDate_FailsInsteadOfDroppingTheFilter()
    {
        var service = StubList(BuildReminder());

        var result = await CreateListTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["date_from"] = "last March" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("date_from");

        // A silently widened query would report the whole history as "March".
        await service.DidNotReceive().GetRemindersPagedAsync(Arg.Any<ReminderFilterDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListReminders_WithNoMatches_SaysSoInsteadOfPrintingAnEmptyList()
    {
        var result = await CreateListTool(StubList()).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No reminders match");
    }

    [Fact]
    public async Task ListReminders_RendersTheRowWithInvoiceClientLevelAndAmount()
    {
        var result = await CreateListTool(StubList(BuildReminder())).ExecuteAsync([]);

        result.OutputText.ShouldContain("ID=5");
        result.OutputText.ShouldContain("invoice 2026-0042");
        result.OutputText.ShouldContain("ABC s.r.o.");
        result.OutputText.ShouldContain("level 2");
        result.OutputText.ShouldContain("Draft");
        result.OutputText.ShouldContain("due: 2026-03-01");
        result.OutputText.ShouldContain("reminded: 2026-03-15");
        result.OutputText.ShouldContain("12,162.50 CZK");
    }

    [Fact]
    public async Task ListReminders_ReportsWhereASentReminderWent()
    {
        var reminder = BuildReminder(status: EReminderStatus.Sent, sentToEmail: "fakturace@abc.cz");

        var result = await CreateListTool(StubList(reminder)).ExecuteAsync([]);

        result.OutputText.ShouldContain("sent to: fakturace@abc.cz");
    }

    [Fact]
    public async Task ListReminders_ReportsWhyAReminderFailed()
    {
        var reminder = BuildReminder(status: EReminderStatus.Failed, errorMessage: "SMTP timeout");

        var result = await CreateListTool(StubList(reminder)).ExecuteAsync([]);

        // "Failed" without a reason leaves the model inventing one.
        result.OutputText.ShouldContain("error: SMTP timeout");
    }

    // ─── get_reminder_settings ────────────────────────────────────────────

    [Fact]
    public async Task GetReminderSettings_WithoutAClient_ReadsTheCompanyDefault()
    {
        var service = Substitute.For<IReminderService>();
        service.GetEffectiveSettingsAsync(null, Arg.Any<CancellationToken>()).Returns(BuildSettings());

        var result = await CreateGetSettingsTool(service).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("company-wide default");
        result.OutputText.ShouldContain("Grace period after due date: 7 days");
        result.OutputText.ShouldContain("Level 2: 14 days after the previous step");
        result.OutputText.ShouldContain("fee: 50.00 CZK");
    }

    [Fact]
    public async Task GetReminderSettings_NeverCreatesADefaultRecord()
    {
        var service = Substitute.For<IReminderService>();
        service.GetEffectiveSettingsAsync(null, Arg.Any<CancellationToken>()).Returns(BuildSettings());

        await CreateGetSettingsTool(service).ExecuteAsync([]);

        // GetCompanySettingsAsync auto-creates a record — a write, which a read tool must not do.
        await service.DidNotReceive().GetCompanySettingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetReminderSettings_WithNothingStored_SaysNoRemindersAreGenerated()
    {
        var service = Substitute.For<IReminderService>();
        service.GetEffectiveSettingsAsync(null, Arg.Any<CancellationToken>()).Returns((ReminderSettingsDto?)null);

        var result = await CreateGetSettingsTool(service).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No reminder (dunning) settings are stored");
    }

    [Fact]
    public async Task GetReminderSettings_ResolvesTheClientAndAsksForItsEffectiveSettings()
    {
        var clientService = Substitute.For<IClientService>();
        clientService.GetClientByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 42, CompanyName = "ABC s.r.o." });

        var reminderService = Substitute.For<IReminderService>();
        reminderService.GetEffectiveSettingsAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildSettings(clientId: 42, clientName: "ABC s.r.o."));

        var result = await CreateGetSettingsTool(reminderService, clientService)
            .ExecuteAsync(new Dictionary<string, string> { ["id"] = "42" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("override for client 'ABC s.r.o.' (ID 42)");
        await reminderService.Received(1).GetEffectiveSettingsAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetReminderSettings_SaysWhenTheClientFallsBackToTheCompanyDefault()
    {
        var clientService = Substitute.For<IClientService>();
        clientService.GetClientByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 42, CompanyName = "ABC s.r.o." });

        var reminderService = Substitute.For<IReminderService>();
        // 2-tier resolution: no override for this client, so the company default comes back.
        reminderService.GetEffectiveSettingsAsync(42, Arg.Any<CancellationToken>()).Returns(BuildSettings());

        var result = await CreateGetSettingsTool(reminderService, clientService)
            .ExecuteAsync(new Dictionary<string, string> { ["id"] = "42" });

        result.OutputText.ShouldContain("This client has no own override");
    }

    [Fact]
    public async Task GetReminderSettings_WithAnUnknownClient_FailsWithoutReadingSettings()
    {
        var clientService = Substitute.For<IClientService>();
        clientService.GetClientByIdAsync(42, Arg.Any<CancellationToken>()).Returns((ClientDto?)null);

        var reminderService = Substitute.For<IReminderService>();

        var result = await CreateGetSettingsTool(reminderService, clientService)
            .ExecuteAsync(new Dictionary<string, string> { ["id"] = "42" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("42");
        await reminderService.DidNotReceive()
            .GetEffectiveSettingsAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetReminderSettings_WithABlankClientName_StillReadsTheCompanyDefault()
    {
        var reminderService = Substitute.For<IReminderService>();
        reminderService.GetEffectiveSettingsAsync(null, Arg.Any<CancellationToken>()).Returns(BuildSettings());

        // A model that sends "name": "" must not turn into a substring matching every client.
        var result = await CreateGetSettingsTool(reminderService)
            .ExecuteAsync(new Dictionary<string, string> { ["name"] = "   " });

        result.IsSuccess.ShouldBeTrue();
        await reminderService.Received(1).GetEffectiveSettingsAsync(null, Arg.Any<CancellationToken>());
    }

    // ─── update_reminder_settings ─────────────────────────────────────────

    /// <summary>Reminder service stub that reports the given settings and echoes every upsert back.</summary>
    private static IReminderService StubSettings(ReminderSettingsDto? stored)
    {
        var service = Substitute.For<IReminderService>();
        service.GetEffectiveSettingsAsync(null, Arg.Any<CancellationToken>()).Returns(stored);
        service.GetCompanySettingsAsync(Arg.Any<CancellationToken>()).Returns(stored ?? BuildSettings());
        service.UpsertSettingsAsync(Arg.Any<UpdateReminderSettingsDto>(), Arg.Any<CancellationToken>())
            .Returns(stored ?? BuildSettings());
        return service;
    }

    [Fact]
    public async Task UpdateReminderSettings_Preview_DescribesTheChangeAndWritesNothing()
    {
        var service = StubSettings(BuildSettings());

        var result = await CreateUpdateSettingsTool(service).BuildPreviewAsync(
            new Dictionary<string, string> { ["grace_period_days"] = "14", ["auto_send_email"] = "false" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("grace period: 7 → 14 days");
        result.OutputText.ShouldContain("send e-mails automatically: Yes → No");

        await service.DidNotReceive()
            .UpsertSettingsAsync(Arg.Any<UpdateReminderSettingsDto>(), Arg.Any<CancellationToken>());

        // GetCompanySettingsAsync creates the record when none exists — also a write.
        await service.DidNotReceive().GetCompanySettingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateReminderSettings_Preview_WithNothingStored_ListsTheRequestedValues()
    {
        var service = StubSettings(null);

        var result = await CreateUpdateSettingsTool(service).BuildPreviewAsync(
            new Dictionary<string, string> { ["is_enabled"] = "false" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("standard defaults would be created");
        result.OutputText.ShouldContain("is_enabled = No");
        await service.DidNotReceive().GetCompanySettingsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateReminderSettings_WithoutAnySetting_IsRefused()
    {
        var service = StubSettings(BuildSettings());
        var tool = CreateUpdateSettingsTool(service);

        var preview = await tool.BuildPreviewAsync([]);
        var executed = await tool.ExecuteAsync([]);

        preview.IsSuccess.ShouldBeFalse();
        executed.IsSuccess.ShouldBeFalse();
        executed.ErrorMessage.ShouldContain("No change was requested");
        await service.DidNotReceive()
            .UpsertSettingsAsync(Arg.Any<UpdateReminderSettingsDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateReminderSettings_RepeatingTheStoredValue_IsNotAChange()
    {
        var service = StubSettings(BuildSettings());

        // Stored grace period is already 7 — the preview must not claim an edit.
        var result = await CreateUpdateSettingsTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["grace_period_days"] = "7" });

        result.IsSuccess.ShouldBeFalse();
        await service.DidNotReceive()
            .UpsertSettingsAsync(Arg.Any<UpdateReminderSettingsDto>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("max_reminder_level", "0")]
    [InlineData("max_reminder_level", "6")]
    [InlineData("grace_period_days", "-1")]
    [InlineData("grace_period_days", "400")]
    public async Task UpdateReminderSettings_RejectsOutOfRangeNumbers(string key, string value)
    {
        var service = StubSettings(BuildSettings());
        var tool = CreateUpdateSettingsTool(service);
        var parameters = new Dictionary<string, string> { [key] = value };

        var preview = await tool.BuildPreviewAsync(parameters);
        var executed = await tool.ExecuteAsync(parameters);

        // Both entry points guard, so a model that jumps straight to confirm: true cannot
        // store a value that silently switches dunning off.
        preview.IsSuccess.ShouldBeFalse();
        preview.ErrorMessage.ShouldContain(key);
        executed.IsSuccess.ShouldBeFalse();
        executed.ErrorMessage.ShouldContain(key);
        await service.DidNotReceive()
            .UpsertSettingsAsync(Arg.Any<UpdateReminderSettingsDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateReminderSettings_Execute_SendsTheChangedScalarsForTheCompanyScope()
    {
        var service = StubSettings(BuildSettings());

        var result = await CreateUpdateSettingsTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["is_enabled"] = "false",
            ["max_reminder_level"] = "2",
            ["include_interest"] = "true"
        });

        result.IsSuccess.ShouldBeTrue();

        var dto = CapturedUpsert(service);
        dto.ClientId.ShouldBeNull();               // company default, never an accidental override
        dto.IsEnabled.ShouldBeFalse();
        dto.MaxReminderLevel.ShouldBe(2);
        dto.IncludeInterest.ShouldBeTrue();

        // Untouched settings keep the stored value instead of falling back to the DTO defaults.
        dto.GracePeriodDays.ShouldBe(7);
        dto.AttachInvoicePdf.ShouldBeTrue();
        dto.AutoSendEmail.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateReminderSettings_Execute_CarriesTheEscalationLevelsOverUntouched()
    {
        var service = StubSettings(BuildSettings());

        await CreateUpdateSettingsTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["is_enabled"] = "false" });

        // UpsertSettingsAsync REPLACES the level collection — an empty list would delete all
        // three levels and leave the dunning job unable to generate anything.
        var dto = CapturedUpsert(service);
        dto.Levels.Count.ShouldBe(3);
        dto.Levels[1].Level.ShouldBe(2);
        dto.Levels[1].DaysAfterPrevious.ShouldBe(14);
        dto.Levels[1].FixedFeeCzk.ShouldBe(50m);
        dto.Levels[1].Subject.ShouldBe("Druhá upomínka");
        dto.Levels[2].EmailTemplateId.ShouldBe(9);
    }

    [Fact]
    public async Task UpdateReminderSettings_Execute_CreatesTheDefaultRecordWhenNoneExists()
    {
        var service = StubSettings(null);
        service.GetCompanySettingsAsync(Arg.Any<CancellationToken>()).Returns(BuildSettings());

        var result = await CreateUpdateSettingsTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["auto_send_email"] = "false" });

        result.IsSuccess.ShouldBeTrue();

        // On the write path creating the record is right — the user asked for a setting and
        // there has to be something to hold it.
        await service.Received(1).GetCompanySettingsAsync(Arg.Any<CancellationToken>());
        CapturedUpsert(service).AutoSendEmail.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateReminderSettings_Execute_ReportsTheSavedSettingsBack()
    {
        var saved = BuildSettings();
        saved.IsEnabled = false;

        var service = StubSettings(BuildSettings());
        service.UpsertSettingsAsync(Arg.Any<UpdateReminderSettingsDto>(), Arg.Any<CancellationToken>())
            .Returns(saved);

        var result = await CreateUpdateSettingsTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["is_enabled"] = "false" });

        result.OutputText.ShouldContain("Reminder settings updated.");
        result.OutputText.ShouldContain("Reminders enabled: No");
    }

    [Fact]
    public void UpdateReminderSettings_IsConfirmable_SoTheExecutorGatesIt()
    {
        // The gate is central; a tool that stops implementing the interface would write blind.
        CreateUpdateSettingsTool(Substitute.For<IReminderService>())
            .ShouldBeAssignableTo<IConfirmableChatTool>();
    }

    [Fact]
    public void ReadOnlyReminderTools_AreNotConfirmable()
    {
        var service = Substitute.For<IReminderService>();

        CreateListTool(service).ShouldNotBeAssignableTo<IConfirmableChatTool>();
        CreateGetSettingsTool(service).ShouldNotBeAssignableTo<IConfirmableChatTool>();
    }
}
