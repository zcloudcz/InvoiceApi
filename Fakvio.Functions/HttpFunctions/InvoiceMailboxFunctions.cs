// Manual Functions wrapper for InvoiceMailboxController.
// Route: api/invoice-mailbox — 4 endpoints: Get, Activate, Deactivate, Regenerate.

#nullable enable

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Fakvio.API.Controller;

namespace Fakvio.Functions.Generated;

public class InvoiceMailboxFunctions
{
    private readonly InvoiceMailboxController _controller;

    public InvoiceMailboxFunctions(InvoiceMailboxController controller)
    {
        _controller = controller;
    }

    [Function("InvoiceMailbox_Get")]
    public async Task<IActionResult> InvoiceMailbox_Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/invoice-mailbox")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.Get(ct));
    }

    [Function("InvoiceMailbox_Activate")]
    public async Task<IActionResult> InvoiceMailbox_Activate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/invoice-mailbox/activate")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.Activate(ct));
    }

    [Function("InvoiceMailbox_Deactivate")]
    public async Task<IActionResult> InvoiceMailbox_Deactivate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/invoice-mailbox/deactivate")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.Deactivate(ct));
    }

    [Function("InvoiceMailbox_Regenerate")]
    public async Task<IActionResult> InvoiceMailbox_Regenerate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/invoice-mailbox/regenerate")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.Regenerate(ct));
    }
}
