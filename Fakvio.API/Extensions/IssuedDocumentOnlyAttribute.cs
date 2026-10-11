using Fakvio.Application.Service;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Fakvio.API.Extensions;

/// <summary>
/// Marks an <c>InvoiceController</c> action that exists ONLY for documents we issue (PDF from a template, issued
/// ISDOC / UBL, e-mailing, QR payment codes, credit note, proforma -> final invoice, tax receipt, advance queries,
/// and their bulk variants). The controller serves every document type, so a received (supplier) invoice can reach
/// these actions; this filter turns that into a clear 400 business error instead of the misleading "not found" (or a
/// 500) the issued-only services would produce for a received id.
///
/// The document type is taken from the STORED row. The document id is read from the route (<c>id</c>,
/// <c>invoiceId</c> or <c>proformaId</c>) and, for bulk actions, from the <c>ids</c> query string (comma separated);
/// a request that carries neither is left alone. An unknown id is not our business here - the action answers it.
///
/// Cost: ONE query for all ids of the request (not one per id). The list is deliberately NOT capped here: the
/// invoice grid keeps its selection across pages, so a bulk export can carry more than 100 ids.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IssuedDocumentOnlyAttribute : Attribute, IAsyncActionFilter
{
    private static readonly string[] RouteKeys = ["id", "invoiceId", "proformaId"];

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ids = new List<long>();
        foreach (var key in RouteKeys)
        {
            if (context.RouteData.Values.TryGetValue(key, out var raw) && long.TryParse(raw?.ToString(), out var id))
                ids.Add(id);
        }
        if (context.HttpContext.Request.Query.TryGetValue("ids", out var csv))
        {
            var bulkIds = csv.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s.Trim(), out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            ids.AddRange(bulkIds);
        }

        if (ids.Count > 0)
        {
            var service = context.HttpContext.RequestServices.GetRequiredService<IInvoiceService>();
            var receivedId = await service.FindReceivedDocumentIdAsync(ids, context.HttpContext.RequestAborted);
            if (receivedId.HasValue)
            {
                context.Result = new BadRequestObjectResult(new
                {
                    message = $"Document {receivedId} is a received invoice. This operation is available only for issued documents."
                });
                return;
            }
        }

        await next();
    }
}
