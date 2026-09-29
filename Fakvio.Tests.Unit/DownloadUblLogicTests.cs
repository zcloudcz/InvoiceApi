// =============================================================================
// DownloadUblLogicTests — pure-logic tests for InvoiceDetail.DownloadUbl() and the
// "Btn_ExportUbl" disabled condition (ADR 0002, F1.6). Same rationale as
// DownloadIsdocLogicTests: InvoiceDetail.razor is a Blazor component this project
// cannot render (no bunit host wired for it), so the decision rules are replicated
// here as plain C# and tested directly.
// =============================================================================

using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Models;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class DownloadUblLogicTests
{
    /// <summary>Replicates the Disabled="..." condition on the "Btn_ExportUbl" MudButton.</summary>
    private static bool IsDownloadDisabled(EInvoiceStatus status, EDocumentType documentType) =>
        status == EInvoiceStatus.Draft || documentType == EDocumentType.Proforma;

    /// <summary>Replicates the file-name formula: $"{_invoice.DocumentNumber ?? Id.ToString()}.xml".</summary>
    private static string BuildFileName(string? documentNumber, long invoiceId) =>
        $"{documentNumber ?? invoiceId.ToString()}.xml";

    [Theory]
    [InlineData(EInvoiceStatus.Draft, EDocumentType.Invoice, true)]
    [InlineData(EInvoiceStatus.Completed, EDocumentType.Proforma, true)]
    [InlineData(EInvoiceStatus.Draft, EDocumentType.Proforma, true)]
    [InlineData(EInvoiceStatus.Completed, EDocumentType.Invoice, false)]
    [InlineData(EInvoiceStatus.Completed, EDocumentType.CreditNote, false)]
    [InlineData(EInvoiceStatus.Completed, EDocumentType.TaxReceiptForAdvance, false)]
    [InlineData(EInvoiceStatus.Paid, EDocumentType.Invoice, false)]
    public void IsDownloadDisabled_MatchesDraftOrProformaRule(
        EInvoiceStatus status, EDocumentType documentType, bool expectedDisabled)
        => IsDownloadDisabled(status, documentType).ShouldBe(expectedDisabled);

    [Fact]
    public void BuildFileName_WithDocumentNumber_UsesDocumentNumberPlusXmlExtension()
        => BuildFileName("INV2026001", 42L).ShouldBe("INV2026001.xml");

    [Fact]
    public void BuildFileName_WithNullDocumentNumber_FallsBackToInvoiceId()
        => BuildFileName(null, 7L).ShouldBe("7.xml");

    // --------------------------------------------------------------------------
    // Result-branching: success / blocking issues / other failure
    // (mirrors the three-way if/else-if/else in DownloadUbl())
    // --------------------------------------------------------------------------

    private enum DownloadOutcome { Downloaded, ShowIssues, ShowGenericError }

    private static DownloadOutcome Classify(UblDownloadResult result) =>
        result.IsSuccess && result.FileBytes != null ? DownloadOutcome.Downloaded
        : result.Issues.Count > 0 ? DownloadOutcome.ShowIssues
        : DownloadOutcome.ShowGenericError;

    [Fact]
    public void Classify_SuccessResult_Downloads()
    {
        var result = UblDownloadResult.Success("<Invoice/>"u8.ToArray(), "INV2026001.xml");

        Classify(result).ShouldBe(DownloadOutcome.Downloaded);
    }

    [Fact]
    public void Classify_TenantNotReady_WithIssues_ShowsIssues()
    {
        var result = UblDownloadResult.Failure("TENANT_NOT_READY", "Tenant is not ready.",
        [
            new ReadinessIssueDto { Code = "EINVOICE_DRAFT", Severity = EReadinessSeverity.Blocking }
        ]);

        Classify(result).ShouldBe(DownloadOutcome.ShowIssues);
    }

    [Fact]
    public void Classify_NotFound_NoIssues_ShowsGenericError()
    {
        var result = UblDownloadResult.Failure("NOT_FOUND", "Invoice not found.");

        Classify(result).ShouldBe(DownloadOutcome.ShowGenericError);
    }
}
