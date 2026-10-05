using System.ComponentModel.DataAnnotations;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Pins the API validation of the manual <c>ExchangeRate</c> on the four invoice DTOs.
///
/// The attribute was switched from <c>[Range(typeof(decimal), "0.00000001", "1000000")]</c> to the
/// numeric <c>[Range(0.00000001, 1000000)]</c> overload so the MCP tool schema gets numeric
/// minimum/maximum (see ToolDiscoveryTests.EveryToolSchema_UsesNumbersForNumericConstraints).
/// These tests prove the swap did not change what the API accepts.
///
/// Junior note: the double overload validates a <c>decimal?</c> by converting it to double; the
/// bounds here are far from double's precision limit, so the conversion cannot flip a result.
/// </summary>
public class ExchangeRateRangeValidationTests
{
    public static TheoryData<decimal?, bool> Rates => new()
    {
        { null, true },             // omitted → ČNB rate is assigned later
        { 0.00000001m, true },      // lower bound inclusive
        { 25.125m, true },          // typical EUR rate
        { 1000000m, true },         // upper bound inclusive
        { 0m, false },              // a zero rate would zero every CZK amount
        { -1m, false },
        { 1000000.00000001m, false }
    };

    [Theory]
    [MemberData(nameof(Rates))]
    public void CreateReceivedInvoice_ExchangeRate(decimal? rate, bool valid) =>
        IsValid(nameof(CreateReceivedInvoiceDto.ExchangeRate), new CreateReceivedInvoiceDto { ExchangeRate = rate })
            .ShouldBe(valid);

    [Theory]
    [MemberData(nameof(Rates))]
    public void UpdateReceivedInvoice_ExchangeRate(decimal? rate, bool valid) =>
        IsValid(nameof(UpdateReceivedInvoiceDto.ExchangeRate), new UpdateReceivedInvoiceDto { ExchangeRate = rate })
            .ShouldBe(valid);

    [Theory]
    [MemberData(nameof(Rates))]
    public void CreateInvoice_ExchangeRate(decimal? rate, bool valid) =>
        IsValid(nameof(CreateInvoiceDto.ExchangeRate), new CreateInvoiceDto { ExchangeRate = rate })
            .ShouldBe(valid);

    [Theory]
    [MemberData(nameof(Rates))]
    public void UpdateInvoice_ExchangeRate(decimal? rate, bool valid) =>
        IsValid(nameof(UpdateInvoiceDto.ExchangeRate), new UpdateInvoiceDto { ExchangeRate = rate })
            .ShouldBe(valid);

    /// <summary>
    /// Validates only the one property, so unrelated [Required] fields left empty on the DTO
    /// cannot make the result fail for the wrong reason.
    /// </summary>
    private static bool IsValid(string propertyName, object dto)
    {
        var value = dto.GetType().GetProperty(propertyName)!.GetValue(dto);
        var context = new ValidationContext(dto) { MemberName = propertyName };
        return Validator.TryValidateProperty(value, context, new List<ValidationResult>());
    }
}
