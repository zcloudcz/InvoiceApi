using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the Copy icon visibility logic used in the Invoices grid.
///
/// The rule (mirrored from Invoices.razor) is:
///   Show copy icon when DocumentType != CreditNote AND Status != Deleted
///
/// These tests verify all relevant Status × DocumentType combinations without
/// spinning up a full Blazor component (no bUnit available in this project).
/// </summary>
public class InvoiceGridCopyIconTests
{
    // ── helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the Razor condition that guards the Copy icon in the grid row template.
    /// Must stay in sync with Invoices.razor:
    ///   @if (context.Item.DocumentType != EDocumentType.CreditNote
    ///        &amp;&amp; context.Item.Status != EInvoiceStatus.Deleted)
    /// </summary>
    private static bool ShouldShowCopyIcon(InvoiceDto item)
        => item.DocumentType != EDocumentType.CreditNote
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
