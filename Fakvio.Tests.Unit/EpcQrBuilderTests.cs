using System.Globalization;
using System.Text;
using Fakvio.Application.QrPayment;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Tests for the SEPA EPC QR payload (EPC069-12) and the EUR-vs-SPD routing rule.</summary>
public class EpcQrBuilderTests
{
    [Fact]
    public void Build_ProducesExactEpcPayload()
    {
        var payload = EpcQrBuilder.Build("Test s.r.o.", "DE89 3704 0044 0532 0130 00", "COBADEFFXXX", 1234.5m, "2026001", "2026001");

        payload.ShouldBe(string.Join("\n",
            "BCD", "002", "1", "SCT", "COBADEFFXXX", "Test s.r.o.", "DE89370400440532013000",
            "EUR1234.50", "", "", "Faktura 2026001 VS 2026001"));
    }

    [Fact]
    public void Build_WithoutBic_LeavesBicLineEmpty()
    {
        var lines = EpcQrBuilder.Build("A", "DE89370400440532013000", null, 1m, "1", null).Split('\n');
        lines[4].ShouldBe("");
        lines[10].ShouldBe("Faktura 1");
    }

    [Fact]
    public void Build_TruncatesNameAndRemittance_AndStaysWithin331Bytes()
    {
        var payload = EpcQrBuilder.Build(new string('N', 200), "DE89370400440532013000", "COBADEFFXXX", 1m, new string('D', 300), "1");
        var lines = payload.Split('\n');
        lines[5].Length.ShouldBe(70);
        lines[10].Length.ShouldBe(140);
        Encoding.UTF8.GetByteCount(payload).ShouldBeLessThanOrEqualTo(331);
    }

    [Theory]
    [InlineData("0.01", "EUR0.01")]
    [InlineData("10", "EUR10.00")]
    [InlineData("999999999.99", "EUR999999999.99")]
    public void Build_FormatsAmountInvariant(string amount, string expected)
    {
        var value = decimal.Parse(amount, CultureInfo.InvariantCulture);
        var lines = EpcQrBuilder.Build("A", "DE89370400440532013000", null, value, "1", null).Split('\n');
        lines[7].ShouldBe(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1000000000")]
    public void Build_RejectsAmountOutOfRange(string amount)
    {
        var value = decimal.Parse(amount, CultureInfo.InvariantCulture);
        Should.Throw<ArgumentOutOfRangeException>(() =>
            EpcQrBuilder.Build("A", "DE89370400440532013000", null, value, "1", null));
    }

    [Theory]
    [InlineData("EUR", true)]
    [InlineData("eur", true)]
    [InlineData("CZK", false)]
    [InlineData("USD", false)]
    public void Routing_EurWithIban_UsesEpc_OtherwiseSpd(string currency, bool epc)
    {
        var invoice = new Invoice
        {
            IBAN = "DE89370400440532013000",
            TotalWithVat = 100m,
            DocumentNumber = "1",
            VariableSymbol = "1",
            Currency = new Currency { Code = currency },
            Issuer = new Client { CompanyName = "Issuer" }
        };

        var content = QrPaymentService.BuildIbanPaymentQrContent(invoice);

        content.StartsWith("BCD\n").ShouldBe(epc);
        if (!epc) content.StartsWith("SPD*").ShouldBeTrue();
    }
}
