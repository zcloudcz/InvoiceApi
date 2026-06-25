// Manual Functions wrapper for InboundInvoiceEmailController.
// Route: api/inbound-invoice-email — 4 endpoints: GetList, GetDetail, Ignore.

#nullable enable

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Fakvio.API.Controller;
using Fakvio.Contracts.Dto.InvoiceEmail;

namespace Fakvio.Functions.Generated;

public class InboundInvoiceEmailFunctions
{
    private readonly InboundInvoiceEmailController _controller;

    public InboundInvoiceEmailFunctions(InboundInvoiceEmailController controller)
    {
        _controller = controller;
    }

    [Function("InboundInvoiceEmail_GetList")]
    public async Task<IActionResult> InboundInvoiceEmail_GetList(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/inbound-invoice-email")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var filter = new InboundInvoiceEmailFilterDto();
        if (int.TryParse(req.Query["page"], out var page)) filter.Page = page;
        if (int.TryParse(req.Query["pageSize"], out var pageSize)) filter.PageSize = pageSize;
        if (int.TryParse(req.Query["status"], out var status)) filter.Status = (Fakvio.Domain.Enums.EInvoiceEmailStatus)status;
        if (int.TryParse(req.Query["direction"], out var direction)) filter.Direction = (Fakvio.Domain.Enums.EInvoiceDirection)direction;
        if (req.Query.TryGetValue("search", out var search)) filter.Search = search.ToString();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetList(filter, ct));
    }

    [Function("InboundInvoiceEmail_GetDetail")]
    public async Task<IActionResult> InboundInvoiceEmail_GetDetail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/inbound-invoice-email/{id:long}")] HttpRequest req,
        string id)
    {
        if (!long.TryParse(id, out var idParsed))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'id'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetDetail(idParsed, ct));
    }

    [Function("InboundInvoiceEmail_Ignore")]
    public async Task<IActionResult> InboundInvoiceEmail_Ignore(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/inbound-invoice-email/{id:long}/ignore")] HttpRequest req,
        string id)
    {
        if (!long.TryParse(id, out var idParsed))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'id'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.Ignore(idParsed, ct));
    }
}
