using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Issue #269, AC #4 — list_invoices and list_received_invoices must format their "Page total"
/// line identically, since both tools are meant to share <see cref="ChatToolTotals"/>. The
/// per-tool test classes (ReportingChatToolTests, ReceivedInvoiceChatToolTests) each pin their
/// OWN tool's exact line, but neither one looks at the sibling tool — so a change that makes
/// one tool's call site drift from the other (different prefix, different lambda) would pass
/// both of those files while still breaking AC #4. This class runs both tools over equivalent
/// mixed-currency data and asserts the resulting lines are the exact same text.
/// </summary>
public class ChatToolPageTotalParityTests
{
    [Fact]
    public async Task MixedCurrencyPage_BothListTools_ProduceTheIdenticalPageTotalLine()
    {
        var invoicesResult = await BuildListInvoicesTool().ExecuteAsync([]);
        var receivedResult = await BuildListReceivedInvoicesTool().ExecuteAsync([]);

        var invoicesTotalLine = ExtractTotalLine(invoicesResult.OutputText);
        var receivedTotalLine = ExtractTotalLine(receivedResult.OutputText);

        invoicesTotalLine.ShouldBe(receivedTotalLine);
        invoicesTotalLine.ShouldBe($"  Page total (with VAT): {12100m:N2} CZK; {500m:N2} EUR");
    }

    private static string ExtractTotalLine(string outputText)
        => outputText.Split('\n').Single(line => line.Contains("Page total")).TrimEnd('\r');

    private static ListInvoicesTool BuildListInvoicesTool()
    {
        var page = new PagedResult<InvoiceDto>(
            [
                BuildInvoice(1, 12100m, "CZK"),
                BuildInvoice(2, 500m, "EUR")
            ],
            totalCount: 2, pageNumber: 1, pageSize: 10);

        var service = Substitute.For<IInvoiceService>();
        service.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>()).Returns(page);

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(42L);

        return new ListInvoicesTool(service, tenantResolver, Substitute.For<ILogger<ListInvoicesTool>>());
    }

    private static ListReceivedInvoicesTool BuildListReceivedInvoicesTool()
    {
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items =
            [
                BuildReceivedInvoice(1, 12100m, "CZK"),
                BuildReceivedInvoice(2, 500m, "EUR")
            ],
            TotalCount = 2, PageNumber = 1, PageSize = 10
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>()).Returns(paged);

        return new ListReceivedInvoicesTool(
            service, Substitute.For<IClientService>(), Substitute.For<ILogger<ListReceivedInvoicesTool>>());
    }

    private static InvoiceDto BuildInvoice(long id, decimal totalWithVat, string currencyCode)
        => new()
        {
            Id = id,
            DocumentNumber = $"FAK-{id}",
            ClientName = "Alza.cz",
            Status = EInvoiceStatus.Completed,
            DocumentType = EDocumentType.Invoice,
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            TotalBeforeVat = totalWithVat / 1.21m,
            TotalVat = totalWithVat - totalWithVat / 1.21m,
            TotalWithVat = totalWithVat,
            CurrencyCode = currencyCode
        };

    private static ReceivedInvoiceDto BuildReceivedInvoice(long id, decimal totalWithVat, string currencyCode)
        => new()
        {
            Id = id,
            DocumentNumber = $"INV-{id}",
            SupplierName = "Alza.cz",
            SupplierId = 42,
            Status = EReceivedInvoiceStatus.Received,
            IssueDate = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2024, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            TotalBeforeVat = totalWithVat / 1.21m,
            TotalVat = totalWithVat - totalWithVat / 1.21m,
            TotalWithVat = totalWithVat,
            CurrencyCode = currencyCode,
            CurrencySymbol = "Kč",
            Items = [],
            CreatedAt = new DateTime(2024, 4, 1, 12, 0, 0, DateTimeKind.Utc)
        };
}
