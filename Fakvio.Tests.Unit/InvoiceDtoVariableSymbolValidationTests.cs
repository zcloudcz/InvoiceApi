using System.ComponentModel.DataAnnotations;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Variable symbol validation of the unified document DTOs (DEVGUIDE §5.6): an ISSUED document keeps
/// "digits only, max 10" (Czech banking), a RECEIVED invoice carries whatever the supplier printed, up to the
/// 50 characters the column holds. The update DTO has no document type, so it only limits the length; the issued
/// rule for an update is checked by <c>InvoiceController.UpdateInvoice</c> (see UnifiedDocumentEndpointTests).
/// </summary>
public class InvoiceDtoVariableSymbolValidationTests
{
    /// <summary>Runs the same validation ASP.NET runs on a request body (attributes, then IValidatableObject).</summary>
    private static List<ValidationResult> Validate(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    private static CreateInvoiceDto NewCreateDto(EDocumentType type, string? variableSymbol) => new()
    {
        DocumentType = type, ClientId = 1, IssuerId = 1, CurrencyId = 1, VariableSymbol = variableSymbol,
        InvoiceItem = [new CreateInvoiceItemDto { Description = "Hosting", Quantity = 1, UnitPrice = 100 }]
    };

    [Theory]
    [InlineData("INV-2026-0042")]
    [InlineData("12345678901")] // 11 digits
    [InlineData("1234567890\n")] // trailing newline must not slip past the end anchor
    public void Create_IssuedType_RefusesSymbolThatIsNotMax10Digits(string symbol)
    {
        foreach (var type in Enum.GetValues<EDocumentType>().Where(t => t.IsIssued()))
        {
            var error = Validate(NewCreateDto(type, symbol)).ShouldHaveSingleItem();

            error.ErrorMessage.ShouldBe("Variable symbol must contain only digits (max 10)");
            error.MemberNames.ShouldBe([nameof(CreateInvoiceDto.VariableSymbol)]);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567890")]
    public void Create_IssuedType_AcceptsEmptyOrUpTo10Digits(string? symbol)
        => Validate(NewCreateDto(EDocumentType.Invoice, symbol)).ShouldBeEmpty();

    [Theory]
    [InlineData("INV-2026-0042")]
    [InlineData("12345678901234567890")]
    public void Create_ReceivedInvoice_AcceptsTheSuppliersSymbolAsPrinted(string symbol)
        => Validate(NewCreateDto(EDocumentType.ReceivedInvoice, symbol)).ShouldBeEmpty();

    [Fact]
    public void CreateAndUpdate_RefuseASymbolLongerThanTheColumn()
    {
        var tooLong = new string('7', 51);

        Validate(NewCreateDto(EDocumentType.ReceivedInvoice, tooLong)).ShouldNotBeEmpty();
        Validate(new UpdateInvoiceDto { VariableSymbol = tooLong }).ShouldNotBeEmpty();
    }

    [Fact]
    public void Update_AcceptsTheSuppliersSymbol_BecauseTheTypeIsKnownOnlyFromTheStoredRow()
        => Validate(new UpdateInvoiceDto { VariableSymbol = "INV-2026-0042" }).ShouldBeEmpty();
}
