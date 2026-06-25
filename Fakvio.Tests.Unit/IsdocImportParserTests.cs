using Fakvio.Infrastructure.Service;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="IsdocImportParser"/>.
/// Validates ISDOC 6.0.2 XML parsing into InvoiceExtractedData.
/// </summary>
public class IsdocImportParserTests
{
    private readonly IsdocImportParser _sut = new(Substitute.For<ILogger<IsdocImportParser>>());

    [Fact]
    public void Parse_ValidIsdoc_ExtractsAllFields()
    {
        var xml = """
            <Invoice version="6.0.2" xmlns="http://isdoc.cz/namespace/2013">
              <DocumentType>1</DocumentType>
              <ID>FV-2026-001</ID>
              <UUID>12345678-1234-1234-1234-123456789012</UUID>
              <IssueDate>2026-06-15</IssueDate>
              <TaxPointDate>2026-06-15</TaxPointDate>
              <VATApplicable>true</VATApplicable>
              <ElectronicPossibilityAgreementReference>Electronic</ElectronicPossibilityAgreementReference>
              <LocalCurrencyCode>CZK</LocalCurrencyCode>
              <CurrRate>1</CurrRate>
              <RefCurrRate>1</RefCurrRate>
              <AccountingSupplierParty>
                <Party>
                  <PartyIdentification><ID>12345678</ID></PartyIdentification>
                  <PartyName><Name>Test Supplier s.r.o.</Name></PartyName>
                  <PostalAddress>
                    <StreetName>Ulice 1</StreetName>
                    <BuildingNumber>1</BuildingNumber>
                    <CityName>Praha</CityName>
                    <PostalZone>11000</PostalZone>
                    <Country><IdentificationCode>CZ</IdentificationCode><Name>CZ</Name></Country>
                  </PostalAddress>
                  <PartyTaxScheme><CompanyID>CZ12345678</CompanyID><TaxScheme>VAT</TaxScheme></PartyTaxScheme>
                </Party>
              </AccountingSupplierParty>
              <AccountingCustomerParty>
                <Party>
                  <PartyIdentification><ID>87654321</ID></PartyIdentification>
                  <PartyName><Name>Test Buyer a.s.</Name></PartyName>
                  <PostalAddress>
                    <StreetName>Jiná 2</StreetName>
                    <BuildingNumber>2</BuildingNumber>
                    <CityName>Brno</CityName>
                    <PostalZone>60200</PostalZone>
                    <Country><IdentificationCode>CZ</IdentificationCode><Name>CZ</Name></Country>
                  </PostalAddress>
                </Party>
              </AccountingCustomerParty>
              <InvoiceLines>
                <InvoiceLine>
                  <ID>1</ID>
                  <InvoicedQuantity unitCode="ks">2</InvoicedQuantity>
                  <LineExtensionAmount>1000.00</LineExtensionAmount>
                  <LineExtensionAmountTaxInclusive>1210.00</LineExtensionAmountTaxInclusive>
                  <LineExtensionTaxAmount>210.00</LineExtensionTaxAmount>
                  <UnitPrice>500.00</UnitPrice>
                  <UnitPriceTaxInclusive>605.00</UnitPriceTaxInclusive>
                  <ClassifiedTaxCategory><Percent>21</Percent><VATCalculationMethod>0</VATCalculationMethod></ClassifiedTaxCategory>
                  <Item><Description>Test položka</Description></Item>
                </InvoiceLine>
              </InvoiceLines>
              <TaxTotal>
                <TaxSubTotal>
                  <TaxableAmount>1000.00</TaxableAmount>
                  <TaxAmount>210.00</TaxAmount>
                  <TaxInclusiveAmount>1210.00</TaxInclusiveAmount>
                  <AlreadyClaimedTaxableAmount>0.00</AlreadyClaimedTaxableAmount>
                  <AlreadyClaimedTaxAmount>0.00</AlreadyClaimedTaxAmount>
                  <AlreadyClaimedTaxInclusiveAmount>0.00</AlreadyClaimedTaxInclusiveAmount>
                  <DifferenceTaxableAmount>1000.00</DifferenceTaxableAmount>
                  <DifferenceTaxAmount>210.00</DifferenceTaxAmount>
                  <DifferenceTaxInclusiveAmount>1210.00</DifferenceTaxInclusiveAmount>
                  <TaxCategory><Percent>21</Percent><TaxScheme>VAT</TaxScheme></TaxCategory>
                </TaxSubTotal>
                <TaxAmount>210.00</TaxAmount>
              </TaxTotal>
              <LegalMonetaryTotal>
                <TaxExclusiveAmount>1000.00</TaxExclusiveAmount>
                <TaxInclusiveAmount>1210.00</TaxInclusiveAmount>
                <AlreadyClaimedTaxExclusiveAmount>0.00</AlreadyClaimedTaxExclusiveAmount>
                <AlreadyClaimedTaxInclusiveAmount>0.00</AlreadyClaimedTaxInclusiveAmount>
                <DifferenceTaxExclusiveAmount>1000.00</DifferenceTaxExclusiveAmount>
                <DifferenceTaxInclusiveAmount>1210.00</DifferenceTaxInclusiveAmount>
                <PaidDepositsAmount>0.00</PaidDepositsAmount>
                <PayableAmount>1210.00</PayableAmount>
              </LegalMonetaryTotal>
              <PaymentMeans>
                <Payment>
                  <PaidAmount>1210.00</PaidAmount>
                  <PaymentMeansCode>42</PaymentMeansCode>
                  <Details>
                    <PaymentDueDate>2026-06-30</PaymentDueDate>
                    <ID>1234567890</ID>
                    <BankCode>0100</BankCode>
                    <Name></Name>
                    <IBAN>CZ6508000000001234567890</IBAN>
                    <BIC>GIBACZPX</BIC>
                    <VariableSymbol>2026001</VariableSymbol>
                  </Details>
                </Payment>
              </PaymentMeans>
            </Invoice>
            """;

        var result = _sut.Parse(xml);

        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("FV-2026-001");
        result.IssueDate.ShouldNotBeNull();
        result.TaxableSupplyDate.ShouldNotBeNull();
        result.Currency.ShouldBe("CZK");

        result.IssuerRegistrationNumber.ShouldBe("12345678");
        result.IssuerName.ShouldBe("Test Supplier s.r.o.");
        result.IssuerTaxNumber.ShouldBe("CZ12345678");

        result.RecipientRegistrationNumber.ShouldBe("87654321");
        result.RecipientName.ShouldBe("Test Buyer a.s.");

        result.TotalBeforeVat.ShouldBe(1000.00m);
        result.TotalVat.ShouldBe(210.00m);
        result.TotalAmount.ShouldBe(1210.00m);

        result.VariableSymbol.ShouldBe("2026001");
        result.IBAN.ShouldBe("CZ6508000000001234567890");
        result.SWIFT.ShouldBe("GIBACZPX");
        result.BankAccountNumber.ShouldBe("1234567890/0100");

        result.DueDate.ShouldNotBeNull();

        result.Items.ShouldNotBeNull();
        result.Items!.Count.ShouldBe(1);
        result.Items[0].Description.ShouldBe("Test položka");
        result.Items[0].Quantity.ShouldBe(2m);
        result.Items[0].UnitPrice.ShouldBe(500.00m);
        result.Items[0].VatRate.ShouldBe(21m);
        result.Items[0].Unit.ShouldBe("ks");
    }

    [Fact]
    public void Parse_NullInput_ReturnsNull()
    {
        _sut.Parse(null!).ShouldBeNull();
        _sut.Parse("").ShouldBeNull();
        _sut.Parse("   ").ShouldBeNull();
    }

    [Fact]
    public void Parse_MalformedXml_ReturnsNull()
    {
        _sut.Parse("<not valid xml").ShouldBeNull();
    }

    [Fact]
    public void Parse_MinimalIsdoc_ExtractsWhatIsAvailable()
    {
        var xml = """
            <Invoice version="6.0.2" xmlns="http://isdoc.cz/namespace/2013">
              <DocumentType>1</DocumentType>
              <ID>MIN-001</ID>
              <UUID>00000000-0000-0000-0000-000000000000</UUID>
              <IssueDate>2026-01-01</IssueDate>
              <VATApplicable>false</VATApplicable>
              <ElectronicPossibilityAgreementReference>E</ElectronicPossibilityAgreementReference>
              <LocalCurrencyCode>EUR</LocalCurrencyCode>
              <CurrRate>1</CurrRate>
              <RefCurrRate>1</RefCurrRate>
              <InvoiceLines>
                <InvoiceLine>
                  <ID>1</ID>
                  <InvoicedQuantity>1</InvoicedQuantity>
                  <LineExtensionAmount>100.00</LineExtensionAmount>
                  <LineExtensionAmountTaxInclusive>100.00</LineExtensionAmountTaxInclusive>
                  <LineExtensionTaxAmount>0.00</LineExtensionTaxAmount>
                  <UnitPrice>100.00</UnitPrice>
                  <UnitPriceTaxInclusive>100.00</UnitPriceTaxInclusive>
                  <ClassifiedTaxCategory><Percent>0</Percent><VATCalculationMethod>0</VATCalculationMethod></ClassifiedTaxCategory>
                  <Item><Description>Simple item</Description></Item>
                </InvoiceLine>
              </InvoiceLines>
              <TaxTotal><TaxAmount>0.00</TaxAmount></TaxTotal>
              <LegalMonetaryTotal>
                <TaxExclusiveAmount>100.00</TaxExclusiveAmount>
                <TaxInclusiveAmount>100.00</TaxInclusiveAmount>
                <AlreadyClaimedTaxExclusiveAmount>0.00</AlreadyClaimedTaxExclusiveAmount>
                <AlreadyClaimedTaxInclusiveAmount>0.00</AlreadyClaimedTaxInclusiveAmount>
                <DifferenceTaxExclusiveAmount>100.00</DifferenceTaxExclusiveAmount>
                <DifferenceTaxInclusiveAmount>100.00</DifferenceTaxInclusiveAmount>
                <PaidDepositsAmount>0.00</PaidDepositsAmount>
                <PayableAmount>100.00</PayableAmount>
              </LegalMonetaryTotal>
            </Invoice>
            """;

        var result = _sut.Parse(xml);

        result.ShouldNotBeNull();
        result.DocumentNumber.ShouldBe("MIN-001");
        result.Currency.ShouldBe("EUR");
        result.TotalAmount.ShouldBe(100.00m);
        result.IssuerRegistrationNumber.ShouldBeNull();
        result.RecipientRegistrationNumber.ShouldBeNull();
        result.VariableSymbol.ShouldBeNull();
    }
}
