using Fakvio.Application.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Extensions;

/// <summary>
/// Maps a <see cref="TenantNotReadyException"/> to the HTTP 400 body every readiness-gated
/// endpoint returns (#342). Was copy-pasted between <c>InvoiceController</c> and
/// <c>InvoiceTemplateController</c> — one shared helper keeps the shape from drifting when a
/// third gate is added.
/// </summary>
public static class TenantNotReadyExceptionExtensions
{
    /// <summary>
    /// Builds the <c>{ code, message, missingFields, issues }</c> 400 body. Logging stays with
    /// the caller — each endpoint logs a different action ("Cannot complete invoice", "Cannot
    /// auto-complete invoice from template", …), so that context would be lost in a shared helper.
    /// </summary>
    public static BadRequestObjectResult ToBadRequestResult(this TenantNotReadyException ex)
        => new(new
        {
            code = ex.Code,
            message = ex.Message,
            missingFields = ex.MissingFields,
            issues = ex.Issues
        });
}
