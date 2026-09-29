using System.Net;
using System.Text.Json;
using Fakvio.Contracts.Dto.RecurringInvoice;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for <see cref="RecurringTools"/> (N4.6, DEVGUIDE §4.13) — mocked
/// <see cref="IFakvioApiClient"/>, same style as <see cref="TemplateToolsTests"/>.
/// </summary>
public class RecurringToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    // ── ListRecurringSchedules ───────────────────────────────────────────

    [Fact]
    public async Task ListRecurringSchedules_ReturnsSchedulesFromApi()
    {
        _api.GetRecurringSchedulesAsync(null, Arg.Any<CancellationToken>())
            .Returns([new RecurringInvoiceScheduleDto { Id = 1, TemplateName = "Monthly Hosting" }]);

        var json = await RecurringTools.ListRecurringSchedules(_api);

        var doc = JsonDocument.Parse(json);
        doc.RootElement[0].GetProperty("templateName").GetString().ShouldBe("Monthly Hosting");
    }

    [Fact]
    public async Task ListRecurringSchedules_TemplateIdFilter_IsForwardedToApi()
    {
        _api.GetRecurringSchedulesAsync(7, Arg.Any<CancellationToken>()).Returns([]);

        await RecurringTools.ListRecurringSchedules(_api, 7);

        await _api.Received(1).GetRecurringSchedulesAsync(7, Arg.Any<CancellationToken>());
    }

    // ── GetRecurringSchedule ──────────────────────────────────────────────

    [Fact]
    public async Task GetRecurringSchedule_Found_ReturnsSchedule()
    {
        _api.GetRecurringScheduleByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(new RecurringInvoiceScheduleDto { Id = 7, Frequency = ERecurrenceFrequency.Monthly });

        var json = await RecurringTools.GetRecurringSchedule(_api, 7);

        JsonDocument.Parse(json).RootElement.GetProperty("frequency").GetString().ShouldBe("Monthly");
    }

    [Fact]
    public async Task GetRecurringSchedule_NotFound_ReturnsErrorWithoutThrowing()
    {
        _api.GetRecurringScheduleByIdAsync(999, Arg.Any<CancellationToken>()).Returns((RecurringInvoiceScheduleDto?)null);

        var json = await RecurringTools.GetRecurringSchedule(_api, 999);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("999");
    }

    // ── CreateRecurringSchedule ───────────────────────────────────────────

    [Fact]
    public async Task CreateRecurringSchedule_NullSchedule_ReturnsErrorWithoutCallingApi()
    {
        var json = await RecurringTools.CreateRecurringSchedule(_api, schedule: null!);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("schedule is required");
        await _api.DidNotReceive().CreateRecurringScheduleAsync(
            Arg.Any<CreateRecurringInvoiceScheduleDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRecurringSchedule_ValidSchedule_PassesThroughAndReturnsResult()
    {
        var schedule = new CreateRecurringInvoiceScheduleDto
        {
            TemplateId = 1,
            ClientId = 2,
            Frequency = ERecurrenceFrequency.Monthly,
            DayOfMonth = 15,
            StartDate = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero)
        };
        _api.CreateRecurringScheduleAsync(schedule, Arg.Any<CancellationToken>())
            .Returns(new RecurringInvoiceScheduleDto { Id = 42, TemplateId = 1 });

        var json = await RecurringTools.CreateRecurringSchedule(_api, schedule);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(42);
        await _api.Received(1).CreateRecurringScheduleAsync(schedule, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRecurringSchedule_ApiThrows_ReturnsSanitizedError()
    {
        var schedule = new CreateRecurringInvoiceScheduleDto { TemplateId = 1, ClientId = 2, StartDate = DateTimeOffset.UtcNow };
        _api.CreateRecurringScheduleAsync(schedule, Arg.Any<CancellationToken>())
            .ThrowsAsync(new FakvioApiException("raw body", HttpStatusCode.BadRequest, "Client does not belong to this tenant."));

        var json = await RecurringTools.CreateRecurringSchedule(_api, schedule);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("validation_error");
        doc.RootElement.GetProperty("message").GetString().ShouldBe("Client does not belong to this tenant.");
    }

    // ── UpdateRecurringSchedule ───────────────────────────────────────────

    [Fact]
    public async Task UpdateRecurringSchedule_NullChanges_ReturnsErrorWithoutCallingApi()
    {
        var json = await RecurringTools.UpdateRecurringSchedule(_api, scheduleId: 5, changes: null!);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("changes is required");
        await _api.DidNotReceive().UpdateRecurringScheduleAsync(
            Arg.Any<long>(), Arg.Any<UpdateRecurringInvoiceScheduleDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateRecurringSchedule_ValidChanges_PassesThroughAndReturnsResult()
    {
        var changes = new UpdateRecurringInvoiceScheduleDto { IntervalCount = 2 };
        _api.UpdateRecurringScheduleAsync(5, changes, Arg.Any<CancellationToken>())
            .Returns(new RecurringInvoiceScheduleDto { Id = 5, IntervalCount = 2 });

        var json = await RecurringTools.UpdateRecurringSchedule(_api, 5, changes);

        JsonDocument.Parse(json).RootElement.GetProperty("intervalCount").GetInt32().ShouldBe(2);
        await _api.Received(1).UpdateRecurringScheduleAsync(5, changes, Arg.Any<CancellationToken>());
    }

    // ── PauseRecurringSchedule / ResumeRecurringSchedule ─────────────────

    [Fact]
    public async Task PauseRecurringSchedule_PassesThroughAndReturnsResult()
    {
        _api.PauseRecurringScheduleAsync(5, Arg.Any<CancellationToken>())
            .Returns(new RecurringInvoiceScheduleDto { Id = 5, IsActive = false });

        var json = await RecurringTools.PauseRecurringSchedule(_api, 5);

        JsonDocument.Parse(json).RootElement.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        await _api.Received(1).PauseRecurringScheduleAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeRecurringSchedule_PassesThroughAndReturnsResult()
    {
        _api.ResumeRecurringScheduleAsync(5, Arg.Any<CancellationToken>())
            .Returns(new RecurringInvoiceScheduleDto { Id = 5, IsActive = true });

        var json = await RecurringTools.ResumeRecurringSchedule(_api, 5);

        JsonDocument.Parse(json).RootElement.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        await _api.Received(1).ResumeRecurringScheduleAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeRecurringSchedule_ApiRejectsExhaustedSchedule_ReturnsSanitizedError()
    {
        // RecurringInvoiceService.SetActiveAsync(true) rejects resuming a schedule that already
        // reached its end condition (DEVGUIDE §4.13 point 6) — surfaces as a 400.
        _api.ResumeRecurringScheduleAsync(5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new FakvioApiException("raw body", HttpStatusCode.BadRequest, "Schedule has already reached its end condition."));

        var json = await RecurringTools.ResumeRecurringSchedule(_api, 5);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("validation_error");
    }

    // ── DeleteRecurringSchedule ───────────────────────────────────────────

    [Fact]
    public async Task DeleteRecurringSchedule_Succeeds_ReturnsSuccessPayload()
    {
        var json = await RecurringTools.DeleteRecurringSchedule(_api, 5);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("scheduleId").GetInt64().ShouldBe(5);
        await _api.Received(1).DeleteRecurringScheduleAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRecurringSchedule_UnknownId_ReturnsValidationError()
    {
        // RecurringInvoiceService.DeleteAsync throws InvalidOperationException for an unknown
        // schedule ID, and RecurringInvoiceController maps that to 400 — NOT 404 (unlike
        // GetById, which uses an explicit NotFound() result). McpToolError must therefore
        // classify this as validation_error, not not_found.
        _api.DeleteRecurringScheduleAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new FakvioApiException("raw body", HttpStatusCode.BadRequest, "Recurring schedule with ID 999 not found."));

        var json = await RecurringTools.DeleteRecurringSchedule(_api, 999);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("validation_error");
    }
}
