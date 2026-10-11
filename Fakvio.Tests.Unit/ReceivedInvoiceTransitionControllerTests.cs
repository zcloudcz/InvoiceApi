using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Transport layer of the three received-invoice transitions that mirror the issued routes:
/// POST {id}/mark-unpaid, {id}/revert-to-draft, {id}/restore. The rules themselves live in DocumentTransitions
/// (DocumentStateMachineTests); here only 200 / 404 / 400 mapping is pinned.
/// </summary>
public class ReceivedInvoiceTransitionControllerTests
{
    private readonly IReceivedInvoiceService _service = Substitute.For<IReceivedInvoiceService>();
    private readonly ReceivedInvoiceController _controller;

    public ReceivedInvoiceTransitionControllerTests()
    {
        _controller = new ReceivedInvoiceController(
            _service,
            Substitute.For<IPaymentMatchingService>(),
            Substitute.For<IFileAttachmentService>(),
            Substitute.For<IIsdocExportService>(),
            Substitute.For<ILogger<ReceivedInvoiceController>>());
    }

    /// <summary>The three transitions as (controller call, service call) pairs, so each case runs the same checks.</summary>
    public static IEnumerable<object[]> Transitions() =>
    [
        ["mark-unpaid"],
        ["revert-to-draft"],
        ["restore"],
    ];

    private Task<ActionResult<ReceivedInvoiceDto>> CallController(string transition, long id) => transition switch
    {
        "mark-unpaid" => _controller.MarkAsUnpaid(id),
        "revert-to-draft" => _controller.RevertToDraft(id),
        "restore" => _controller.Restore(id),
        _ => throw new ArgumentOutOfRangeException(nameof(transition))
    };

    private Task<ReceivedInvoiceDto?> ServiceCall(string transition) => transition switch
    {
        "mark-unpaid" => _service.MarkAsUnpaidAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()),
        "revert-to-draft" => _service.RevertToDraftAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()),
        "restore" => _service.RestoreAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()),
        _ => throw new ArgumentOutOfRangeException(nameof(transition))
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task Transition_Success_Returns200WithDto(string transition)
    {
        var dto = new ReceivedInvoiceDto { Id = 7 };
        ServiceCall(transition).Returns(dto);

        var result = await CallController(transition, 7);

        result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBe(dto);
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task Transition_UnknownOrOtherSideId_Returns404(string transition)
    {
        ServiceCall(transition).Returns((ReceivedInvoiceDto?)null);

        var result = await CallController(transition, 7);

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public async Task Transition_RefusedByStateMachine_Returns400WithMessage(string transition)
    {
        ServiceCall(transition).ThrowsAsync(new InvalidOperationException("not allowed now"));

        var result = await CallController(transition, 7);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }
}
