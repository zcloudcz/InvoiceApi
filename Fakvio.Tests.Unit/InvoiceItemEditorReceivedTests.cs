// ============================================================================
// InvoiceItemEditorReceivedTests - the item editor on a RECEIVED invoice (issue #542, 2/2).
//
// A supplier's invoice can carry a VAT percentage that is not in the tenant's rate list (VatRateId
// is null, e.g. 15 %). The editor must show it, must not turn it into another rate or 0 on its own,
// must not offer text rows (the API refuses them) and must not let OUR VAT payer status rewrite
// what the supplier billed.
// ============================================================================

using Bunit;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Components.Shared;
using MudBlazor;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class InvoiceItemEditorReceivedTests : DocumentDetailTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public InvoiceItemEditorReceivedTests()
    {
        Api.ReferenceDelay = TimeSpan.FromMilliseconds(30);
    }

    private static CreateInvoiceItemDto OddRateItem() => new()
    {
        OrderIndex = 1, Description = "Odd rate", Quantity = 1, Unit = "ks", UnitPrice = 1000m,
        VatRateId = null, VatRatePercentage = 15m, VatRegime = EVatRegime.Standard
    };

    private IRenderedComponent<InvoiceItemEditor> RenderEditor(CreateInvoiceItemDto item, bool received)
    {
        var cut = Render<InvoiceItemEditor>(p => p
            .Add(c => c.Items, [item])
            .Add(c => c.IsReceived, received));
        // The rate list arrives after the first render; the odd percentage must be visible both before and after.
        // The editor loads the rate list, then the issuer, then the reverse charge codes (a VAT payer only): once the
        // last one was requested the rates are in. A non-payer issuer never requests the codes.
        cut.WaitForAssertion(() => Api.Called(HttpMethod.Get, "/api/vatrate/active").ShouldBeTrue(), Patience);
        if (received || Api.Issuer.IsVatPayer)
            cut.WaitForAssertion(() => Api.Called(HttpMethod.Get, "/api/reversechargecode").ShouldBeTrue(), Patience);
        return cut;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_percentage_outside_the_rate_list_is_shown_and_the_row_is_left_alone(bool received)
    {
        var item = OddRateItem();
        var cut = RenderEditor(item, received);

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("15%"), Patience);
        item.VatRateId.ShouldBeNull();
        item.VatRatePercentage.ShouldBe(15m);
    }

    [Fact]
    public async Task Choosing_the_shown_custom_percentage_again_changes_nothing_but_a_real_rate_does()
    {
        var item = OddRateItem();
        var cut = RenderEditor(item, received: true);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("15%"), Patience);
        var select = cut.FindComponents<MudSelect<long?>>().First();

        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(-1)); // the "custom percentage" entry
        item.VatRateId.ShouldBeNull();
        item.VatRatePercentage.ShouldBe(15m);

        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(2)); // 12 % from the list
        item.VatRateId.ShouldBe(2);
        item.VatRatePercentage.ShouldBe(12m);
    }

    [Fact]
    public void Received_editor_offers_no_text_row_and_ignores_our_own_vat_payer_status()
    {
        Api.Issuer.IsVatPayer = false; // we are NOT a VAT payer, the supplier still billed 15 %
        var item = OddRateItem();
        var cut = RenderEditor(item, received: true);

        cut.Markup.ShouldNotContain(L["Btn_AddTextRow"].Value);
        item.VatRatePercentage.ShouldBe(15m, "the supplier's VAT is not ours to rewrite");
        Api.Requests.ShouldNotContain(r => r.Path.Contains("/client/issuer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Issued_editor_keeps_text_rows_and_still_zeroes_vat_for_a_non_payer_issuer()
    {
        Api.Issuer.IsVatPayer = false;
        var item = OddRateItem();
        var cut = RenderEditor(item, received: false);

        cut.Markup.ShouldContain(L["Btn_AddTextRow"].Value);
        cut.WaitForAssertion(() => item.VatRatePercentage.ShouldBe(0m), Patience);
    }
}
