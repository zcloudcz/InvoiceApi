using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <c>InvoiceController.SendInvoiceEmail</c>. Regression guard: the controller used to
/// call <c>SendInvoiceEmailAsync(id, recipient, ct)</c> and silently drop Subject / Message, so a
/// custom subject never reached the recipient (API, Functions and MCP were all affected).
/// </summary>
public class InvoiceControllerSendEmailTests
{
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    private InvoiceController BuildController() => new(
        Substitute.For<IInvoiceService>(),
        Substitute.For<IPdfExportService>(),
        Substitute.For<IDocumentFileNameService>(),
        Substitute.For<IIsdocExportService>(),
        Substitute.For<IUblExportService>(),
        _emailService,
        Substitute.For<IQrPaymentService>(),
        Substitute.For<ICloudStorageOrchestrator>(),
        Substitute.For<IPaymentMatchingService>(),
        Substitute.For<ILogger<InvoiceController>>());

    [Fact]
    public async Task SendInvoiceEmail_PassesSubjectMessageAndAttachmentsToEmailService()
    {
        var dto = new SendInvoiceEmailDto
        {
            RecipientEmail = "client@example.com",
            Subject = "Custom subject",
            Message = "Hello\nthere",
            Attachments = [new EmailAttachmentDto { FileName = "note.txt", ContentBase64 = Convert.ToBase64String([1, 2, 3]) }]
        };

        var result = await BuildController().SendInvoiceEmail(7, dto, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        await _emailService.Received(1).SendInvoiceEmailAsync(
            7, "client@example.com", Arg.Any<CancellationToken>(),
            Arg.Is<SendInvoiceEmailOptions?>(o =>
                o != null
                && o.Subject == "Custom subject"
                && o.Message == "Hello\nthere"
                && o.Attachments != null
                && o.Attachments.Count == 1
                && o.Attachments[0].FileName == "note.txt"
                && o.Attachments[0].Content.SequenceEqual(new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public async Task SendInvoiceEmail_InvalidAttachment_Returns400AndSendsNothing()
    {
        var dto = new SendInvoiceEmailDto
        {
            RecipientEmail = "client@example.com",
            Attachments = [new EmailAttachmentDto { FileName = "virus.exe", ContentBase64 = Convert.ToBase64String([1]) }]
        };

        var result = await BuildController().SendInvoiceEmail(7, dto, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await _emailService.DidNotReceiveWithAnyArgs().SendInvoiceEmailAsync(default, default!, default, default);
    }
}
