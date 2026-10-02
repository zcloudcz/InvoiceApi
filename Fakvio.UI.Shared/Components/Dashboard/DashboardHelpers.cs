using System.Globalization;
using Fakvio.Domain.Enums;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Fakvio.UI.Shared.Components.Dashboard;

/// <summary>Colour and localized text of an invoice status, shared by dashboard widgets.</summary>
public static class InvoiceStatusDisplay
{
    public static Color Color(EInvoiceStatus status) => status switch
    {
        EInvoiceStatus.Completed => MudBlazor.Color.Info,
        EInvoiceStatus.Paid => MudBlazor.Color.Success,
        EInvoiceStatus.Creditnoted => MudBlazor.Color.Warning,
        EInvoiceStatus.Deleted => MudBlazor.Color.Dark,
        _ => MudBlazor.Color.Default
    };

    public static string Text(IStringLocalizer<SharedResource> l, EInvoiceStatus status) => status switch
    {
        EInvoiceStatus.Draft => l["Invoice_StatusDraft"].Value,
        EInvoiceStatus.Completed => l["Invoice_StatusIssued"].Value,
        EInvoiceStatus.Paid => l["Invoice_StatusPaid"].Value,
        EInvoiceStatus.Creditnoted => l["Invoice_StatusCancelled"].Value,
        EInvoiceStatus.Deleted => l["Invoice_StatusCancelled"].Value,
        _ => status.ToString()
    };
}

/// <summary>Chart options shared by all dashboard charts (one palette across widgets).</summary>
public static class DashboardCharts
{
    public static readonly ChartOptions Options = new()
    {
        ChartPalette = new[] { "#1976D2", "#388E3C", "#F57C00", "#D32F2F", "#7B1FA2", "#0097A7", "#FBC02D", "#5D4037", "#455A64", "#C2185B" }
    };
}

/// <summary>Formats the "yyyy-MM" month keys of the dashboard series as short axis labels.</summary>
public static class DashboardMonths
{
    public static string[] Labels(IEnumerable<string> months) => months
        .Select(m => DateTime.TryParseExact(m + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("MM/yy", CultureInfo.CurrentCulture)
            : m)
        .ToArray();
}
