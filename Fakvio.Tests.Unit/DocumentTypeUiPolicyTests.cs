using System.Globalization;
using System.Resources;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Models;
using MudBlazor;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="DocumentTypeUiPolicy"/> - the one place where the UI decides
/// anything by document type. The tables below are the written-down contract: changing a rule in
/// the policy must change the matching row here on purpose.
/// </summary>
public class DocumentTypeUiPolicyTests
{
    private static readonly EDocumentType[] AllTypes = Enum.GetValues<EDocumentType>();
    private static readonly EInvoiceStatus[] AllStatuses = Enum.GetValues<EInvoiceStatus>();
    private static readonly EDocumentType[] IssuedTypes = AllTypes.Where(t => t != EDocumentType.ReceivedInvoice).ToArray();

    public static TheoryData<EDocumentType, EInvoiceStatus> TypeStatusMatrix()
    {
        var data = new TheoryData<EDocumentType, EInvoiceStatus>();
        foreach (var type in AllTypes)
            foreach (var status in AllStatuses)
                data.Add(type, status);
        return data;
    }

    // ── state machine: every type x every status ─────────────────────────────

    [Theory]
    [MemberData(nameof(TypeStatusMatrix))]
    public void CanEdit_FollowsTable(EDocumentType type, EInvoiceStatus status)
    {
        var expected = type == EDocumentType.ReceivedInvoice
            ? status is EInvoiceStatus.Draft or EInvoiceStatus.Completed or EInvoiceStatus.PartiallyPaid
            : status == EInvoiceStatus.Draft;

        DocumentTypeUiPolicy.For(type).CanEdit(status).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(TypeStatusMatrix))]
    public void CanComplete_IsDraftOnly(EDocumentType type, EInvoiceStatus status) =>
        DocumentTypeUiPolicy.For(type).CanComplete(status).ShouldBe(status == EInvoiceStatus.Draft);

    [Theory]
    [MemberData(nameof(TypeStatusMatrix))]
    public void CanMarkPaid_FollowsTable(EDocumentType type, EInvoiceStatus status)
    {
        var expected = type == EDocumentType.ReceivedInvoice
            ? status is EInvoiceStatus.Completed or EInvoiceStatus.PartiallyPaid
            : status == EInvoiceStatus.Completed;

        DocumentTypeUiPolicy.For(type).CanMarkPaid(status).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(TypeStatusMatrix))]
    public void CanMarkUnpaid_FollowsTable(EDocumentType type, EInvoiceStatus status)
    {
        var expected = type == EDocumentType.ReceivedInvoice
            ? status is EInvoiceStatus.Paid or EInvoiceStatus.PartiallyPaid
            : status == EInvoiceStatus.Paid;

        DocumentTypeUiPolicy.For(type).CanMarkUnpaid(status).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(TypeStatusMatrix))]
    public void CanRevertToDraft_IsCompletedOnly(EDocumentType type, EInvoiceStatus status) =>
        DocumentTypeUiPolicy.For(type).CanRevertToDraft(status).ShouldBe(status == EInvoiceStatus.Completed);

    [Theory]
    [MemberData(nameof(TypeStatusMatrix))]
    public void CanDelete_FollowsTable(EDocumentType type, EInvoiceStatus status)
    {
        var expected = type == EDocumentType.ReceivedInvoice
            ? status == EInvoiceStatus.Draft
            : status is EInvoiceStatus.Draft or EInvoiceStatus.Completed;

        DocumentTypeUiPolicy.For(type).CanDelete(status).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(TypeStatusMatrix))]
    public void CanRestore_IsDeletedOnly(EDocumentType type, EInvoiceStatus status) =>
        DocumentTypeUiPolicy.For(type).CanRestore(status).ShouldBe(status == EInvoiceStatus.Deleted);

    // ── status label and color: every type x every status ────────────────────

    [Theory]
    [MemberData(nameof(TypeStatusMatrix))]
    public void StatusLabelKey_FollowsTable(EDocumentType type, EInvoiceStatus status)
    {
        var received = type == EDocumentType.ReceivedInvoice;
        var expected = status switch
        {
            EInvoiceStatus.Draft => received ? "EReceivedInvoiceStatus_Received" : "Invoice_StatusDraft",
            EInvoiceStatus.Completed => received ? "EReceivedInvoiceStatus_Approved" : "Invoice_StatusIssued",
            EInvoiceStatus.Paid => "Invoice_StatusPaid",
            EInvoiceStatus.Creditnoted => "Invoice_StatusCredited",
            EInvoiceStatus.Deleted => "Invoice_StatusDeleted",
            EInvoiceStatus.PartiallyPaid => "Invoice_StatusPartiallyPaid",
            _ => throw new InvalidOperationException($"Add {status} to this test table.")
        };

        DocumentTypeUiPolicy.For(type).StatusLabelKey(status).ShouldBe(expected);
    }

    [Theory]
    [InlineData(EInvoiceStatus.Draft, Color.Default)]
    [InlineData(EInvoiceStatus.Completed, Color.Info)]
    [InlineData(EInvoiceStatus.Paid, Color.Success)]
    [InlineData(EInvoiceStatus.Creditnoted, Color.Warning)]
    [InlineData(EInvoiceStatus.Deleted, Color.Dark)]
    [InlineData(EInvoiceStatus.PartiallyPaid, Color.Info)]
    public void StatusColor_IsOneMappingForAllTypes(EInvoiceStatus status, Color expected) =>
        DocumentTypeUiPolicy.StatusColor(status).ShouldBe(expected);

    [Fact]
    public void StatusColor_CoversEveryDefinedStatus_NotJustTheDefault()
    {
        // Default is reserved for Draft and unknown values; every other status has its own mapping row above.
        foreach (var status in AllStatuses.Where(st => st != EInvoiceStatus.Draft))
            DocumentTypeUiPolicy.StatusColor(status).ShouldNotBe(Color.Default, status.ToString());
    }

    [Fact]
    public void UnknownStatus_DoesNotCrashTheGrid_ItFallsBackToTheRawName()
    {
        var policy = DocumentTypeUiPolicy.For(EDocumentType.Invoice);

        DocumentTypeUiPolicy.StatusColor((EInvoiceStatus)999).ShouldBe(Color.Default);
        policy.StatusLabelKey((EInvoiceStatus)999).ShouldBe("999");
    }

    [Fact]
    public void For_UnknownType_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => DocumentTypeUiPolicy.For((EDocumentType)999));

    // ── urls ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EDocumentType.Invoice, "/invoices", "/invoices/create")]
    [InlineData(EDocumentType.CreditNote, "/invoices?type=CreditNote", "/invoices/create?type=CreditNote")]
    [InlineData(EDocumentType.Proforma, "/invoices?type=Proforma", "/invoices/create?type=Proforma")]
    [InlineData(EDocumentType.TaxReceiptForAdvance, "/invoices?type=TaxReceiptForAdvance", "/invoices/create?type=TaxReceiptForAdvance")]
    [InlineData(EDocumentType.ReceivedInvoice, "/received-invoices", "/received-invoices/create")]
    public void ListAndCreateUrls_PerType(EDocumentType type, string listUrl, string createUrl)
    {
        var policy = DocumentTypeUiPolicy.For(type);

        policy.ListUrl.ShouldBe(listUrl);
        policy.CreateUrl.ShouldBe(createUrl);
    }

    [Theory]
    [InlineData(EDocumentType.Invoice, "/invoices/42")]
    [InlineData(EDocumentType.CreditNote, "/invoices/42")]
    [InlineData(EDocumentType.Proforma, "/invoices/42")]
    [InlineData(EDocumentType.TaxReceiptForAdvance, "/invoices/42")]
    [InlineData(EDocumentType.ReceivedInvoice, "/received-invoices/42")]
    public void DetailUrl_PerType(EDocumentType type, string expected) =>
        DocumentTypeUiPolicy.For(type).DetailUrl(42).ShouldBe(expected);

    [Fact]
    public void ImportUrl_PerSide()
    {
        foreach (var type in IssuedTypes)
            DocumentTypeUiPolicy.For(type).ImportUrl.ShouldBe("/invoices/import?target=IssuedInvoice");

        DocumentTypeUiPolicy.For(EDocumentType.ReceivedInvoice).ImportUrl
            .ShouldBe("/received-invoices/import?target=ReceivedInvoice");
    }

    [Theory]
    [InlineData("/invoices", null, EDocumentType.Invoice)]
    [InlineData("/invoices", "", EDocumentType.Invoice)]
    [InlineData("/invoices", "Bogus", EDocumentType.Invoice)]
    [InlineData("/invoices/", null, EDocumentType.Invoice)]
    [InlineData("/invoices", "CreditNote", EDocumentType.CreditNote)]
    [InlineData("/invoices", "Proforma", EDocumentType.Proforma)]
    [InlineData("/invoices", "TaxReceiptForAdvance", EDocumentType.TaxReceiptForAdvance)]
    [InlineData("/received-invoices", null, EDocumentType.ReceivedInvoice)]
    // The path fixes the type: a stray ?type= on the received route is ignored.
    [InlineData("/received-invoices", "CreditNote", EDocumentType.ReceivedInvoice)]
    public void ForListRoute_TypeIsFixedByThePath(string path, string? typeFilter, EDocumentType expected) =>
        DocumentTypeUiPolicy.ForListRoute(path, typeFilter)!.Type.ShouldBe(expected);

    [Theory]
    [InlineData("/invoices/5")]
    [InlineData("/invoices/create")]
    [InlineData("/received-invoices/5")]
    [InlineData("/dashboard")]
    public void ForListRoute_NonListPath_ReturnsNull(string path) =>
        DocumentTypeUiPolicy.ForListRoute(path, null).ShouldBeNull();

    [Fact]
    public void GridKey_IsSeparatePerSide()
    {
        foreach (var type in IssuedTypes)
            DocumentTypeUiPolicy.For(type).GridKey.ShouldBe("invoices");

        DocumentTypeUiPolicy.For(EDocumentType.ReceivedInvoice).GridKey.ShouldBe("received-invoices");
    }

    // ── texts ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EDocumentType.Invoice, "Invoice_Title", "Invoice_Subtitle", "Invoice_New", "Invoice_Detail")]
    [InlineData(EDocumentType.CreditNote, "Nav_CreditNotes", "CreditNote_Subtitle", "CreditNote_New", "CreditNote_Detail")]
    [InlineData(EDocumentType.Proforma, "Proforma_Title", "Proforma_Subtitle", "Proforma_New", "Proforma_Detail")]
    [InlineData(EDocumentType.TaxReceiptForAdvance, "TaxReceiptForAdvance_Title", "TaxReceiptForAdvance_Subtitle", "TaxReceiptForAdvance_New", "TaxReceiptForAdvance_Detail")]
    [InlineData(EDocumentType.ReceivedInvoice, "ReceivedInvoice_Title", null, "ReceivedInvoice_New", "ReceivedInvoice_Detail")]
    public void Titles_PerType(EDocumentType type, string title, string? subtitle, string newLabel, string detail)
    {
        var policy = DocumentTypeUiPolicy.For(type);

        policy.ListTitleKey.ShouldBe(title);
        policy.ListSubtitleKey.ShouldBe(subtitle);
        policy.NewLabelKey.ShouldBe(newLabel);
        policy.DetailTitleKey.ShouldBe(detail);
        policy.CreateTitleKey.ShouldBe(newLabel);
    }

    [Fact]
    public void CounterpartyLabel_ClientForIssued_SupplierForReceived()
    {
        foreach (var type in IssuedTypes)
            DocumentTypeUiPolicy.For(type).CounterpartyLabelKey.ShouldBe("Invoice_Client");

        DocumentTypeUiPolicy.For(EDocumentType.ReceivedInvoice).CounterpartyLabelKey.ShouldBe("ReceivedInvoice_Supplier");
    }

    [Fact]
    public void CompleteAction_IssueForIssued_ApproveForReceived()
    {
        var issued = DocumentTypeUiPolicy.For(EDocumentType.Invoice);
        issued.CompleteLabelKey.ShouldBe("Invoice_Issue");
        issued.BulkCompleteLabelKey.ShouldBe("Bulk_Issue");
        issued.CompleteSuccessKey.ShouldBe("Invoice_Issued");
        issued.BulkConfirmCompleteKey.ShouldBe("Bulk_ConfirmIssue");

        var received = DocumentTypeUiPolicy.For(EDocumentType.ReceivedInvoice);
        received.CompleteLabelKey.ShouldBe("ReceivedInvoice_Approve");
        received.BulkCompleteLabelKey.ShouldBe("ReceivedInvoice_Approve");
        received.CompleteSuccessKey.ShouldBe("ReceivedInvoice_Approved");
        received.BulkConfirmCompleteKey.ShouldBe("Bulk_ConfirmApprove");
    }

    // ── files ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EDocumentType.Invoice, EContentTemplateType.InvoicePdf, "FileName_Invoices")]
    [InlineData(EDocumentType.CreditNote, EContentTemplateType.CreditNotePdf, "FileName_CreditNotes")]
    [InlineData(EDocumentType.Proforma, EContentTemplateType.AdvanceInvoicePdf, "Proforma_Title")]
    [InlineData(EDocumentType.TaxReceiptForAdvance, EContentTemplateType.TaxReceiptForAdvancePdf, "TaxReceiptForAdvance_Title")]
    public void PdfSettings_PerIssuedType(EDocumentType type, EContentTemplateType templateType, string zipPrefixKey)
    {
        var policy = DocumentTypeUiPolicy.For(type);

        policy.PdfTemplateType.ShouldBe(templateType);
        policy.BulkPdfZipPrefixKey.ShouldBe(zipPrefixKey);
    }

    [Fact]
    public void PdfSettings_ReceivedHasNone()
    {
        var policy = DocumentTypeUiPolicy.For(EDocumentType.ReceivedInvoice);

        policy.PdfTemplateType.ShouldBeNull();
        policy.BulkPdfZipPrefixKey.ShouldBeNull();
    }

    [Fact]
    public void AttachmentEntityName_PerSide()
    {
        foreach (var type in IssuedTypes)
            DocumentTypeUiPolicy.For(type).AttachmentEntityName.ShouldBe("Invoice");

        DocumentTypeUiPolicy.For(EDocumentType.ReceivedInvoice).AttachmentEntityName.ShouldBe("ReceivedInvoice");
    }

    // ── capability flags ────────────────────────────────────────────────────

    [Fact]
    public void IsReceived_OnlyForReceivedInvoice()
    {
        foreach (var type in AllTypes)
            DocumentTypeUiPolicy.For(type).IsReceived.ShouldBe(type == EDocumentType.ReceivedInvoice);
    }

    [Theory]
    [MemberData(nameof(AllTypeData))]
    public void IssuedOnlyFeatures_AreOffForReceived(EDocumentType type)
    {
        var p = DocumentTypeUiPolicy.For(type);
        var issued = type != EDocumentType.ReceivedInvoice;

        p.SupportsPdf.ShouldBe(issued);
        p.SupportsEmail.ShouldBe(issued);
        p.SupportsQr.ShouldBe(issued);
        p.SupportsReadinessBanner.ShouldBe(issued);
        p.SupportsTemplates.ShouldBe(issued);
        p.ShowsAlertColumn.ShouldBe(issued);
        p.ShowsEmailSentColumn.ShouldBe(issued);
        p.UsesNumberSequence.ShouldBe(issued);
        p.UsesIssuerBankAccountSelect.ShouldBe(issued);
        p.ValidatesVariableSymbolAsDigits.ShouldBe(issued);
        p.IsdocSkipsDrafts.ShouldBe(issued);
        p.SupportsOss.ShouldBe(issued);
        p.UsesCounterpartyBillingSettings.ShouldBe(issued);
        p.HidesVatForNonVatPayerIssuer.ShouldBe(issued);
    }

    [Theory]
    [MemberData(nameof(TypeStatusMatrix))]
    public void CanExportUbl_NeedsAUblTypeAndAnIssuedDocument(EDocumentType type, EInvoiceStatus status)
    {
        // Same rule the UBL button used to carry inline: not for a draft, not for a proforma, never for received.
        var expected = status != EInvoiceStatus.Draft
                       && type != EDocumentType.Proforma
                       && type != EDocumentType.ReceivedInvoice;

        DocumentTypeUiPolicy.For(type).CanExportUbl(status).ShouldBe(expected);
    }

    [Theory]
    [InlineData("/received-invoices/create", true)]
    [InlineData("/received-invoices/42", true)]
    [InlineData("/received-invoices", true)]
    [InlineData("/Received-Invoices/42", true)]
    [InlineData("/invoices/create", false)]
    [InlineData("/invoices/42", false)]
    [InlineData("/", false)]
    // Not a segment match: a path that merely starts with the same letters is not the received route.
    [InlineData("/received-invoices-archive/1", false)]
    public void IsReceivedRoute_RecognisesTheReceivedSide(string path, bool expected) =>
        DocumentTypeUiPolicy.IsReceivedRoute(path).ShouldBe(expected);

    [Theory]
    [MemberData(nameof(AllTypeData))]
    public void ReceivedOnlyFeatures_AreOffForIssued(EDocumentType type)
    {
        var p = DocumentTypeUiPolicy.For(type);
        var received = type == EDocumentType.ReceivedInvoice;

        p.ShowsAttachmentsColumn.ShouldBe(received);
        p.HasReceivedDate.ShouldBe(received);
        p.HasFreeTextSupplierBankFields.ShouldBe(received);
        p.SupportsBulkAttachmentDownload.ShouldBe(received);
    }

    [Theory]
    [MemberData(nameof(AllTypeData))]
    public void UblAndCopy_ExceptionsPerType(EDocumentType type)
    {
        var p = DocumentTypeUiPolicy.For(type);
        var received = type == EDocumentType.ReceivedInvoice;

        // Proforma is not a tax document -> no UBL; a credit note cannot be copied.
        p.SupportsUbl.ShouldBe(!received && type != EDocumentType.Proforma);
        p.SupportsCopy.ShouldBe(!received && type != EDocumentType.CreditNote);
    }

    [Theory]
    [MemberData(nameof(AllTypeData))]
    public void AccountingExportDefaults_FollowTheSide(EDocumentType type)
    {
        var p = DocumentTypeUiPolicy.For(type);
        var received = type == EDocumentType.ReceivedInvoice;

        p.AccountingExportIncludeIssued.ShouldBe(!received);
        p.AccountingExportIncludeReceived.ShouldBe(received);
    }

    public static TheoryData<EDocumentType> AllTypeData()
    {
        var data = new TheoryData<EDocumentType>();
        foreach (var type in AllTypes)
            data.Add(type);
        return data;
    }

    // ── localization completeness ───────────────────────────────────────────

    /// <summary>
    /// Every resource key the policy can hand to the page must exist in BOTH the Czech neutral
    /// and the English resx. Looking only at the culture-specific set (no parent fallback) is what
    /// catches a key that is missing from en.resx but silently shown in Czech.
    /// </summary>
    [Fact]
    public void EveryKeyReturnedByThePolicy_ExistsInCzechAndEnglishResources()
    {
        var keys = new HashSet<string>();
        foreach (var type in AllTypes)
        {
            var p = DocumentTypeUiPolicy.For(type);
            keys.UnionWith(new[]
            {
                p.ListTitleKey, p.ListSubtitleKey, p.NewLabelKey, p.DetailTitleKey, p.CreateTitleKey,
                p.CounterpartyLabelKey, p.CompleteLabelKey, p.BulkCompleteLabelKey, p.CompleteSuccessKey,
                p.BulkConfirmCompleteKey, p.BulkPdfZipPrefixKey
            }.Where(k => k is not null)!);
            foreach (var status in AllStatuses)
                keys.Add(p.StatusLabelKey(status));
        }

        var manager = new ResourceManager("Fakvio.UI.Shared.Resources.SharedResource", typeof(SharedResource).Assembly);
        var czech = manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        var english = manager.GetResourceSet(new CultureInfo("en"), createIfNotExists: true, tryParents: false);
        czech.ShouldNotBeNull();
        english.ShouldNotBeNull();

        keys.Where(k => czech.GetString(k) is null).ShouldBeEmpty("missing in SharedResource.resx");
        keys.Where(k => english.GetString(k) is null).ShouldBeEmpty("missing in SharedResource.en.resx");
    }
}
