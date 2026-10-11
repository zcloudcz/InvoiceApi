using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Models;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the Copy icon visibility logic used in the Invoices grid.
///
/// The rule lives in DocumentTypeUiPolicy (SupportsCopy: not for CreditNote, not for received
/// invoices); the grid additionally hides the icon for Deleted rows. These tests verify all
/// relevant Status x DocumentType combinations without spinning up a full Blazor component.
/// </summary>
public class InvoiceGridCopyIconTests
{
    // ── helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the Razor condition that guards the Copy icon in the grid row template, using the
    /// real policy instead of a copied rule. Must stay in sync with Invoices.razor:
    ///   @if (_policy.SupportsCopy &amp;&amp; context.Item.Status != EInvoiceStatus.Deleted)
    /// </summary>
    private static bool ShouldShowCopyIcon(InvoiceDto item)
        => DocumentTypeUiPolicy.For(item.DocumentType).SupportsCopy
           && item.Status != EInvoiceStatus.Deleted;

    private static InvoiceDto MakeDto(EDocumentType type, EInvoiceStatus status)
        => new() { DocumentType = type, Status = status };

    // ── visible combinations ──────────────────────────────────────────────────

    [Theory]
    [InlineData(EInvoiceStatus.Draft)]
    [InlineData(EInvoiceStatus.Completed)]
    [InlineData(EInvoiceStatus.Paid)]
    public void Invoice_NonDeleted_ShowsCopyIcon(EInvoiceStatus status)
    {
        // Invoice + any allowed status → icon is visible
        var dto = MakeDto(EDocumentType.Invoice, status);

        ShouldShowCopyIcon(dto).ShouldBeTrue(
            $"Copy icon should be visible for Invoice/{status}");
    }

    [Theory]
    [InlineData(EInvoiceStatus.Draft)]
    [InlineData(EInvoiceStatus.Completed)]
    [InlineData(EInvoiceStatus.Paid)]
    public void Proforma_NonDeleted_ShowsCopyIcon(EInvoiceStatus status)
    {
        // Proforma is allowed too — issue says only CreditNote and Deleted are excluded
        var dto = MakeDto(EDocumentType.Proforma, status);

        ShouldShowCopyIcon(dto).ShouldBeTrue(
            $"Copy icon should be visible for Proforma/{status}");
    }

    // ── hidden: Deleted status ────────────────────────────────────────────────

    [Theory]
    [InlineData(EDocumentType.Invoice)]
    [InlineData(EDocumentType.Proforma)]
    [InlineData(EDocumentType.TaxReceiptForAdvance)]
    public void AnyType_Deleted_HidesCopyIcon(EDocumentType type)
    {
        // Deleted invoices hide the copy icon regardless of document type
        var dto = MakeDto(type, EInvoiceStatus.Deleted);

        ShouldShowCopyIcon(dto).ShouldBeFalse(
            $"Copy icon must be hidden for {type}/Deleted");
    }

    // ── hidden: CreditNote document type ─────────────────────────────────────

    [Theory]
    [InlineData(EInvoiceStatus.Draft)]
    [InlineData(EInvoiceStatus.Completed)]
    [InlineData(EInvoiceStatus.Paid)]
    [InlineData(EInvoiceStatus.Deleted)]
    public void CreditNote_AnyStatus_HidesCopyIcon(EInvoiceStatus status)
    {
        // CreditNotes never show the copy icon — copying a credit note is out of scope
        var dto = MakeDto(EDocumentType.CreditNote, status);

        ShouldShowCopyIcon(dto).ShouldBeFalse(
            $"Copy icon must be hidden for CreditNote/{status}");
    }
}
