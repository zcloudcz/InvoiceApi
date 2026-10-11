using System.Reflection;
using Fakvio.API.Controller;
using Fakvio.API.Extensions;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// The unified <c>InvoiceController</c> serves every document type; the operations that exist only for documents we
/// issue refuse a received document with a 400 business error (<see cref="IssuedDocumentOnlyAttribute"/>).
/// </summary>
public class IssuedDocumentOnlyAttributeTests
{
    // The operation matrix (DEVGUIDE §5.6): every one of these actions is issued-only.
    private static readonly string[] IssuedOnlyActions =
    [
        nameof(InvoiceController.ExportToPdf), nameof(InvoiceController.ExportIsdoc), nameof(InvoiceController.SendInvoiceEmail),
        nameof(InvoiceController.GetEmailDefaults), nameof(InvoiceController.GetQrCode),
        nameof(InvoiceController.GetSindString), nameof(InvoiceController.GetSpdString),
        nameof(InvoiceController.ExportUbl), nameof(InvoiceController.BulkExportPdf),
        nameof(InvoiceController.BulkExportIsdoc), nameof(InvoiceController.BulkExportUbl),
        nameof(InvoiceController.CreateCreditNote), nameof(InvoiceController.GetCreditNotesForInvoice),
        nameof(InvoiceController.IssueFinalInvoice), nameof(InvoiceController.GetRemainingAdvance),
        nameof(InvoiceController.GetFinalInvoicesForProforma), nameof(InvoiceController.IssueTaxReceipt),
        nameof(InvoiceController.GetTaxReceiptsForProforma),
    ];

    [Fact]
    public void EveryIssuedOnlyAction_CarriesTheGuard()
    {
        foreach (var name in IssuedOnlyActions)
        {
            typeof(InvoiceController).GetMethod(name)!
                .GetCustomAttribute<IssuedDocumentOnlyAttribute>()
                .ShouldNotBeNull($"{name} must refuse a received document");
        }
    }

    [Fact]
    public void TheUnifiedActions_DoNotCarryTheGuard()
    {
        string[] unified =
        [
            nameof(InvoiceController.GetInvoiceById), nameof(InvoiceController.UpdateInvoice), nameof(InvoiceController.CompleteInvoice),
            nameof(InvoiceController.MarkAsPaid), nameof(InvoiceController.MarkAsUnpaid), nameof(InvoiceController.RevertToDraft),
            nameof(InvoiceController.DeleteInvoice), nameof(InvoiceController.RestoreInvoice), nameof(InvoiceController.CopyInvoice),
            nameof(InvoiceController.GetPayments), nameof(InvoiceController.FindAutoMatch),
            nameof(InvoiceController.BulkComplete), nameof(InvoiceController.BulkMarkAsPaid), nameof(InvoiceController.BulkDelete),
        ];
        foreach (var name in unified)
            typeof(InvoiceController).GetMethod(name)!.GetCustomAttribute<IssuedDocumentOnlyAttribute>().ShouldBeNull(name);
    }

    [Fact]
    public async Task ReceivedId_InRoute_Returns400WithMessage_AndSkipsTheAction()
    {
        var (result, nextCalled) = await RunAsync(routeId: 10, types: new() { [10] = EDocumentType.ReceivedInvoice });

        nextCalled.ShouldBeFalse();
        var bad = result.ShouldBeOfType<BadRequestObjectResult>();
        bad.Value!.ToString()!.ShouldContain("received invoice");
    }

    [Theory]
    [InlineData(EDocumentType.Invoice)]
    [InlineData(EDocumentType.CreditNote)]
    [InlineData(EDocumentType.Proforma)]
    [InlineData(EDocumentType.TaxReceiptForAdvance)]
    public async Task IssuedId_RunsTheAction(EDocumentType type)
    {
        var (result, nextCalled) = await RunAsync(routeId: 10, types: new() { [10] = type });

        nextCalled.ShouldBeTrue();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task UnknownId_IsLeftToTheAction_ToAnswer404()
    {
        var (_, nextCalled) = await RunAsync(routeId: 10, types: new());

        nextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task BulkIds_AnyReceivedOne_RefusesTheWholeRequest()
    {
        var (result, nextCalled) = await RunAsync(routeId: null, ids: "1, 2,3",
            types: new() { [1] = EDocumentType.Invoice, [2] = EDocumentType.Proforma, [3] = EDocumentType.ReceivedInvoice });

        nextCalled.ShouldBeFalse();
        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    private static async Task<(IActionResult? Result, bool NextCalled)> RunAsync(
        long? routeId, Dictionary<long, EDocumentType> types, string? ids = null)
    {
        var service = Substitute.For<IInvoiceService>();
        service.GetDocumentTypeAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => types.TryGetValue(call.ArgAt<long>(0), out var t) ? (EDocumentType?)t : null);

        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(service).BuildServiceProvider()
        };
        if (ids != null) http.Request.QueryString = new QueryString($"?ids={Uri.EscapeDataString(ids)}");
        var route = new RouteData();
        if (routeId.HasValue) route.Values["id"] = routeId.Value.ToString();

        var executing = new ActionExecutingContext(
            new ActionContext(http, route, new ActionDescriptor()), new List<IFilterMetadata>(),
            new Dictionary<string, object?>(), controller: new object());

        var nextCalled = false;
        await new IssuedDocumentOnlyAttribute().OnActionExecutionAsync(executing, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(executing, new List<IFilterMetadata>(), new object()));
        });

        return (executing.Result, nextCalled);
    }
}
