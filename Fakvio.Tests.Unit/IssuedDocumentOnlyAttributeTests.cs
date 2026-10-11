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

    // Actions that work for EVERY document type, or that never receive a document id at all (lists, create,
    // previews). Putting an action here is a decision: "a received invoice may go through this action".
    private static readonly string[] EveryTypeActions =
    [
        nameof(InvoiceController.GetAllInvoices), nameof(InvoiceController.GetInvoicesPaged),
        nameof(InvoiceController.GetInvoiceById), nameof(InvoiceController.GetInvoiceByDocumentNumber),
        nameof(InvoiceController.CreateInvoice), nameof(InvoiceController.UpdateInvoice), nameof(InvoiceController.CompleteInvoice),
        nameof(InvoiceController.MarkAsPaid), nameof(InvoiceController.MarkAsUnpaid), nameof(InvoiceController.RevertToDraft),
        nameof(InvoiceController.DeleteInvoice), nameof(InvoiceController.RestoreInvoice), nameof(InvoiceController.CopyInvoice),
        nameof(InvoiceController.GetPayments), nameof(InvoiceController.FindAutoMatch),
        nameof(InvoiceController.BulkComplete), nameof(InvoiceController.BulkMarkAsPaid), nameof(InvoiceController.BulkDelete),
        // No document id at all: previews the OSS country for a client + issuer pair.
        nameof(InvoiceController.GetOssCountry),
        // Ids arrive in the BODY and each one is answered separately: a received id does not fail the whole
        // request, its own item fails with a clear message (see the operation matrix).
        nameof(InvoiceController.BulkSendEmail),
    ];

    /// <summary>
    /// Every HTTP action of the controller, found by reflection (a public method declared on the controller that
    /// carries an [HttpGet] / [HttpPost] / ... attribute), so a NEW action cannot slip past the lists above.
    /// </summary>
    private static readonly MethodInfo[] ControllerActions = typeof(InvoiceController)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any())
        .ToArray();

    /// <summary>
    /// The completeness check: each action is classified EXACTLY once. A new action fails here until somebody
    /// decides whether a received invoice may reach it - the question that is easy to forget otherwise.
    /// </summary>
    [Fact]
    public void EveryControllerAction_IsClassified_AsIssuedOnlyOrEveryType()
    {
        ControllerActions.Length.ShouldBeGreaterThan(0);
        // Overloads would make the name-based lists ambiguous.
        ControllerActions.Select(m => m.Name).ShouldBeUnique();

        var unclassified = ControllerActions.Select(m => m.Name)
            .Where(name => !IssuedOnlyActions.Contains(name) && !EveryTypeActions.Contains(name));
        unclassified.ShouldBeEmpty(
            "Classify each new InvoiceController action: add [IssuedDocumentOnly] and list it in IssuedOnlyActions, " +
            "or list it in EveryTypeActions when it must work for a received invoice too (DEVGUIDE 5.6).");

        IssuedOnlyActions.Intersect(EveryTypeActions).ShouldBeEmpty("an action cannot be in both lists");

        // No stale names: a renamed or removed action must leave the lists too.
        IssuedOnlyActions.Concat(EveryTypeActions).Except(ControllerActions.Select(m => m.Name)).ShouldBeEmpty();
    }

    /// <summary>The lists and the attribute must agree: issued-only = guarded, every-type = not guarded.</summary>
    [Fact]
    public void TheGuard_IsOnExactlyTheIssuedOnlyActions()
    {
        foreach (var action in ControllerActions)
        {
            var guarded = action.GetCustomAttribute<IssuedDocumentOnlyAttribute>() is not null;

            guarded.ShouldBe(IssuedOnlyActions.Contains(action.Name),
                $"{action.Name}: [IssuedDocumentOnly] must be present exactly on the actions listed in IssuedOnlyActions");
        }
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
        result.ShouldBeOfType<BadRequestObjectResult>().Value!.ToString()!.ShouldContain("Document 3 ");
    }

    /// <summary>
    /// The guard used to look every id up separately (N round trips before a bulk export even started).
    /// It must ask ONCE for the whole set, whatever the number of ids.
    /// </summary>
    [Fact]
    public async Task BulkIds_AreCheckedWithASingleQuery()
    {
        var ids = Enumerable.Range(1, DownloadLinkController.MaxBulkIds).Select(i => (long)i).ToList();

        var (_, nextCalled, service) = await RunWithServiceAsync(routeId: null, ids: string.Join(',', ids),
            types: ids.ToDictionary(i => i, _ => EDocumentType.Invoice));

        nextCalled.ShouldBeTrue();
        await service.Received(1).FindReceivedDocumentIdAsync(
            Arg.Is<IReadOnlyCollection<long>>(asked => asked.SequenceEqual(ids)), Arg.Any<CancellationToken>());
        await service.DidNotReceive().GetDocumentTypeAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Same limit as the download links (100). Over it: 400 without touching the database.</summary>
    [Fact]
    public async Task BulkIds_OverTheLimit_Return400_WithoutAnyQuery()
    {
        var ids = string.Join(',', Enumerable.Range(1, DownloadLinkController.MaxBulkIds + 1));

        var (result, nextCalled, service) = await RunWithServiceAsync(routeId: null, ids: ids, types: new());

        nextCalled.ShouldBeFalse();
        result.ShouldBeOfType<BadRequestObjectResult>().Value!.ToString()!.ShouldContain("maximum is 100");
        service.ReceivedCalls().ShouldBeEmpty();
    }

    /// <summary>Repeated ids are one document: 101 copies of the same id are not "too many".</summary>
    [Fact]
    public async Task BulkIds_DuplicatesDoNotCountTowardsTheLimit()
    {
        var ids = string.Join(',', Enumerable.Repeat(7, DownloadLinkController.MaxBulkIds + 1));

        var (_, nextCalled) = await RunAsync(routeId: null, ids: ids, types: new() { [7] = EDocumentType.Invoice });

        nextCalled.ShouldBeTrue();
    }

    private static async Task<(IActionResult? Result, bool NextCalled)> RunAsync(
        long? routeId, Dictionary<long, EDocumentType> types, string? ids = null)
    {
        var (result, nextCalled, _) = await RunWithServiceAsync(routeId, types, ids);
        return (result, nextCalled);
    }

    private static async Task<(IActionResult? Result, bool NextCalled, IInvoiceService Service)> RunWithServiceAsync(
        long? routeId, Dictionary<long, EDocumentType> types, string? ids = null)
    {
        var service = Substitute.For<IInvoiceService>();
        // Same contract as the real query: the lowest received id of the asked set, null when there is none.
        service.FindReceivedDocumentIdAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0)
                .Where(id => types.TryGetValue(id, out var t) && t == EDocumentType.ReceivedInvoice)
                .OrderBy(id => id)
                .Select(id => (long?)id)
                .FirstOrDefault());

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

        return (executing.Result, nextCalled, service);
    }
}
