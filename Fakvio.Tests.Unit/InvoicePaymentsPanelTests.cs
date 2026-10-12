// ============================================================================
// InvoicePaymentsPanelTests - the ONE payments panel of the document detail page, for issued and
// received documents (issue #542, 2/2). It loads the rows of THIS document by id
// (GET /api/invoice/{id}/payments), unmatches with the REAL PaymentMatch id and shows the server's
// reason when an unmatch is refused.
// ============================================================================

using Bunit;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Components.Shared;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class InvoicePaymentsPanelTests : DocumentDetailTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public InvoicePaymentsPanelTests()
    {
        Api.ReferenceDelay = TimeSpan.FromMilliseconds(40);
    }

    private static PaymentMatchDto Match(long id, long bankTransactionId, decimal amount) => new()
    {
        Id = id,
        BankTransactionId = bankTransactionId,
        MatchedAmount = amount,
        MatchedAt = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc),
        MatchedBy = EMatchType.Manual,
        Note = $"note-{id}",
        CurrencyCode = "CZK"
    };

    private IRenderedComponent<InvoicePaymentsPanel> RenderPanel(InvoiceDto invoice, Action? onChanged = null)
    {
        return Render<InvoicePaymentsPanel>(p => p
            .Add(c => c.Invoice, invoice)
            .Add(c => c.OnChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => onChanged?.Invoke())));
    }

    private static InvoiceDto WithPaid(InvoiceDto dto, decimal paid)
    {
        dto.PaidAmount = paid;
        dto.TotalWithVat = 1000m;
        return dto;
    }

    [Theory]
    [InlineData(EDocumentType.Invoice, 10)]
    [InlineData(EDocumentType.ReceivedInvoice, 20)]
    public void Rows_are_loaded_by_the_document_id_for_both_sides(EDocumentType type, long id)
    {
        Api.Payments[id] = [Match(901, 7, 400m), Match(902, 8, 100m)];
        var invoice = WithPaid(type == EDocumentType.Invoice ? Issued(id, EInvoiceStatus.PartiallyPaid) : Received(id, EInvoiceStatus.PartiallyPaid), 500m);

        var cut = RenderPanel(invoice);

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("note-902"), Patience);
        Api.Called(HttpMethod.Get, $"/api/invoice/{id}/payments").ShouldBeTrue();
        Api.Requests.ShouldNotContain(r => r.Path.Contains("payment-matching/transactions", StringComparison.OrdinalIgnoreCase),
            "the old approach scanned the first 100 bank transactions by document number");
        cut.FindAll("tbody tr").Count.ShouldBe(2);
        // The header (paid X of Y) is there on both sides.
        cut.Markup.ShouldContain(L["InvoicePayments_OfTotal"].Value);
    }

    [Fact]
    public void Match_manually_is_offered_for_issued_documents_only()
    {
        var issued = RenderPanel(WithPaid(Issued(10, EInvoiceStatus.Completed), 0m));
        issued.WaitForAssertion(() => Api.Called(HttpMethod.Get, "/api/invoice/10/payments").ShouldBeTrue(), Patience);
        ButtonTexts(issued).ShouldContain(L["InvoicePayments_MatchManually"].Value);

        var received = RenderPanel(WithPaid(Received(20, EInvoiceStatus.Completed), 0m));
        received.WaitForAssertion(() => Api.Called(HttpMethod.Get, "/api/invoice/20/payments").ShouldBeTrue(), Patience);
        ButtonTexts(received).ShouldNotContain(L["InvoicePayments_MatchManually"].Value,
            "the server matches manual payments against issued documents only");
    }

    [Theory]
    [InlineData(EDocumentType.Invoice, 10)]
    [InlineData(EDocumentType.ReceivedInvoice, 20)]
    public void Unmatch_sends_the_real_payment_match_id_and_reports_the_change(EDocumentType type, long id)
    {
        Api.Payments[id] = [Match(901, 7, 400m)];
        var invoice = WithPaid(type == EDocumentType.Invoice ? Issued(id, EInvoiceStatus.PartiallyPaid) : Received(id, EInvoiceStatus.PartiallyPaid), 400m);
        var changed = 0;
        var cut = RenderPanel(invoice, () => changed++);
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(1), Patience);

        ClickFirst(cut, "tbody tr button");

        cut.WaitForAssertion(() => changed.ShouldBe(1), Patience);
        var request = Api.Writes.Single();
        request.Path.ShouldBe("/api/payment-matching/transactions/7/unmatch");
        request.Json.GetProperty("paymentMatchId").GetInt64().ShouldBe(901, "the row's own id, not the old placeholder 0");
    }

    [Fact]
    public void A_declined_confirmation_sends_nothing()
    {
        ConfirmAnswer = false;
        Api.Payments[10] = [Match(901, 7, 400m)];
        var cut = RenderPanel(WithPaid(Issued(10, EInvoiceStatus.PartiallyPaid), 400m));
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(1), Patience);

        ClickFirst(cut, "tbody tr button");

        Api.Writes.ShouldBeEmpty();
    }

    [Fact]
    public void A_refused_unmatch_shows_the_server_message_in_a_snackbar_and_does_not_report_a_change()
    {
        Api.UnmatchError = "This payment cannot be unmatched because part of the document was marked as paid manually.";
        Api.Payments[10] = [Match(901, 7, 400m)];
        var changed = 0;
        var cut = RenderPanel(WithPaid(Issued(10, EInvoiceStatus.PartiallyPaid), 400m), () => changed++);
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Count.ShouldBe(1), Patience);

        ClickFirst(cut, "tbody tr button");

        cut.WaitForAssertion(() => SnackbarTexts.ShouldContain(s => s.Contains("marked as paid manually")), Patience);
        changed.ShouldBe(0);
    }
}
