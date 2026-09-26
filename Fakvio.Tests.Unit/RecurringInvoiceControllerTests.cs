using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.RecurringInvoice;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Controller-layer tests for RecurringInvoiceController — verifies HTTP status mapping and
/// that service exceptions become 400 with a readable message, per DEVGUIDE §11.3.
/// </summary>
public class RecurringInvoiceControllerTests
{
    private readonly IRecurringInvoiceService _service = Substitute.For<IRecurringInvoiceService>();
    private readonly RecurringInvoiceController _controller;

    public RecurringInvoiceControllerTests()
    {
        _controller = new RecurringInvoiceController(_service, Substitute.For<ILogger<RecurringInvoiceController>>());
    }

    private static RecurringInvoiceScheduleDto SampleDto(long id = 1) => new()
    {
        Id = id,
        TemplateId = 10,
        ClientId = 20,
        Frequency = ERecurrenceFrequency.Monthly,
        IntervalCount = 1,
        DayOfMonth = 15,
        NextRunAt = new DateTimeOffset(2026, 2, 15, 8, 0, 0, TimeSpan.Zero),
        IsActive = true,
    };

    [Fact]
    public async Task GetAll_NoTemplateFilter_CallsGetAllAsync()
    {
        _service.GetAllAsync(Arg.Any<CancellationToken>()).Returns([SampleDto()]);

        var result = await _controller.GetAll(templateId: null, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ((List<RecurringInvoiceScheduleDto>)ok.Value!).Count.ShouldBe(1);
        await _service.DidNotReceive().GetByTemplateAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAll_WithTemplateFilter_CallsGetByTemplateAsync()
    {
        _service.GetByTemplateAsync(10, Arg.Any<CancellationToken>()).Returns([SampleDto()]);

        var result = await _controller.GetAll(templateId: 10, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _service.Received(1).GetByTemplateAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        _service.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((RecurringInvoiceScheduleDto?)null);

        var result = await _controller.GetById(999, CancellationToken.None);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_ServiceThrows_Returns400WithMessage()
    {
        _service.CreateAsync(Arg.Any<CreateRecurringInvoiceScheduleDto>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Template with ID 999 not found."));

        var result = await _controller.Create(new CreateRecurringInvoiceScheduleDto(), CancellationToken.None);

        var badRequest = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.Value!.ToString().ShouldContain("Template with ID 999 not found.");
    }

    [Fact]
    public async Task Create_Success_Returns201()
    {
        _service.CreateAsync(Arg.Any<CreateRecurringInvoiceScheduleDto>(), Arg.Any<CancellationToken>())
            .Returns(SampleDto());

        var result = await _controller.Create(new CreateRecurringInvoiceScheduleDto(), CancellationToken.None);

        result.Result.ShouldBeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Pause_CallsSetActiveFalse()
    {
        _service.SetActiveAsync(1, false, Arg.Any<CancellationToken>()).Returns(SampleDto());

        var result = await _controller.Pause(1, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _service.Received(1).SetActiveAsync(1, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resume_CallsSetActiveTrue()
    {
        _service.SetActiveAsync(1, true, Arg.Any<CancellationToken>()).Returns(SampleDto());

        var result = await _controller.Resume(1, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        await _service.Received(1).SetActiveAsync(1, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_Success_Returns204()
    {
        var result = await _controller.Delete(1, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Delete_ServiceThrows_Returns400()
    {
        _service.DeleteAsync(999, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Recurring schedule with ID 999 not found."));

        var result = await _controller.Delete(999, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }
}
