using System.Text.Json;
using Fakvio.Contracts.Dto.Tax;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for MCP TaxTools — verifies correct API delegation, JSON serialization,
/// and error handling for all 5 tax estimation tools.
///
/// Junior note: We mock IFakvioApiClient and verify that:
///   1. API methods are called with correct parameters
///   2. Results are properly JSON-serialized
///   3. Errors return JSON with "error" property instead of throwing
/// </summary>
public class TaxToolsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    // ── EstimateTax tests ────────────────────────────────────────────────

    [Fact]
    public async Task EstimateTax_ReturnsJsonWithEstimation()
    {
        // Arrange
        var expectedResult = new TaxEstimationResult
        {
            GrossIncome = 1_000_000m,
            TaxRegime = "LumpSumExpenses60",
            Country = "CZ",
            Year = 2026,
            IncomeTax = 30_000m,
            SocialInsurance = 80_000m,
            HealthInsurance = 40_000m,
            TotalObligations = 150_000m,
            NetIncome = 450_000m,
            EffectiveTaxRate = 15m
        };

        _api.EstimateTaxAsync(Arg.Any<TaxEstimationRequest>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        // Act
        var json = await TaxTools.EstimateTax(
            _api, 1_000_000m, "LumpSumExpenses60", "CZ", 2026);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("grossIncome").GetDecimal().ShouldBe(1_000_000m);
        doc.RootElement.GetProperty("taxRegime").GetString().ShouldBe("LumpSumExpenses60");
        doc.RootElement.GetProperty("totalObligations").GetDecimal().ShouldBe(150_000m);
    }

    [Fact]
    public async Task EstimateTax_PassesCorrectParameters()
    {
        _api.EstimateTaxAsync(Arg.Any<TaxEstimationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TaxEstimationResult());

        // Act
        await TaxTools.EstimateTax(
            _api, 800_000m, "FlatRateTax", "CZ", 2026,
            isMainActivity: false, actualExpenses: 200_000m);

        // Assert: verify API was called with correct request
        await _api.Received(1).EstimateTaxAsync(
            Arg.Is<TaxEstimationRequest>(r =>
                r.GrossIncome == 800_000m &&
                r.TaxRegime == "FlatRateTax" &&
                r.Country == "CZ" &&
                r.Year == 2026 &&
                r.IsMainActivity == false &&
                r.ActualExpenses == 200_000m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EstimateTax_OnError_ReturnsJsonError()
    {
        _api.EstimateTaxAsync(Arg.Any<TaxEstimationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Config not found"));

        // Act
        var json = await TaxTools.EstimateTax(_api, 1_000_000m, "Invalid");

        // Assert: error in JSON, no exception thrown
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("Config not found");
    }

    // ── CompareTaxRegimes tests ──────────────────────────────────────────

    [Fact]
    public async Task CompareTaxRegimes_ReturnsJsonArray()
    {
        var results = new List<TaxEstimationResult>
        {
            new() { TaxRegime = "FlatRateTax", TotalObligations = 100_000m },
            new() { TaxRegime = "LumpSumExpenses60", TotalObligations = 150_000m }
        };

        _api.CompareTaxRegimesAsync(
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(results);

        // Act
        var json = await TaxTools.CompareTaxRegimes(_api, 1_000_000m, "CZ", 2026);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().ShouldBe(2);
        doc.RootElement[0].GetProperty("taxRegime").GetString().ShouldBe("FlatRateTax");
    }

    [Fact]
    public async Task CompareTaxRegimes_OnError_ReturnsJsonError()
    {
        _api.CompareTaxRegimesAsync(
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Server error"));

        var json = await TaxTools.CompareTaxRegimes(_api, 1_000_000m);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("Server error");
    }

    // ── GetAnnualIncome tests ────────────────────────────────────────────

    [Fact]
    public async Task GetAnnualIncome_ReturnsIncomeData()
    {
        _api.GetAnnualIncomeAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new AnnualIncomeDto
            {
                Year = 2026,
                GrossIncome = 1_500_000m,
                InvoiceCount = 25,
                CurrencyCode = "CZK"
            });

        var json = await TaxTools.GetAnnualIncome(_api, 2026);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("year").GetInt32().ShouldBe(2026);
        doc.RootElement.GetProperty("grossIncome").GetDecimal().ShouldBe(1_500_000m);
        doc.RootElement.GetProperty("invoiceCount").GetInt32().ShouldBe(25);
    }

    [Fact]
    public async Task GetAnnualIncome_OnError_ReturnsJsonError()
    {
        _api.GetAnnualIncomeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Unauthorized"));

        var json = await TaxTools.GetAnnualIncome(_api, 2026);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("Unauthorized");
    }

    // ── GetInsuranceAdvance tests ────────────────────────────────────────

    [Fact]
    public async Task GetInsuranceAdvance_ReturnsAdvanceData()
    {
        _api.GetInsuranceAdvanceAsync(Arg.Any<CancellationToken>())
            .Returns(new InsuranceAdvanceDto
            {
                MonthlySocial = 4_096m,
                MonthlyHealth = 3_079m,
                MonthlyTotal = 7_175m,
                NextPaymentDate = new DateTime(2026, 4, 20),
                DaysUntilPayment = 13,
                CurrencyCode = "CZK",
                TaxRegime = "LumpSumExpenses60"
            });

        var json = await TaxTools.GetInsuranceAdvance(_api);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("monthlySocial").GetDecimal().ShouldBe(4_096m);
        doc.RootElement.GetProperty("monthlyHealth").GetDecimal().ShouldBe(3_079m);
        doc.RootElement.GetProperty("monthlyTotal").GetDecimal().ShouldBe(7_175m);
    }

    [Fact]
    public async Task GetInsuranceAdvance_WhenNull_ReturnsMessage()
    {
        _api.GetInsuranceAdvanceAsync(Arg.Any<CancellationToken>())
            .Returns((InsuranceAdvanceDto?)null);

        var json = await TaxTools.GetInsuranceAdvance(_api);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("message").GetString()
            .ShouldContain("No tax regime configured");
    }

    // ── GetTaxConfig tests ───────────────────────────────────────────────

    [Fact]
    public async Task GetTaxConfig_ReturnsConfigData()
    {
        _api.GetTaxConfigAsync("CZ", 2026, Arg.Any<CancellationToken>())
            .Returns(new TaxYearConfigDto
            {
                Year = 2026,
                Country = "CZ",
                IncomeTaxRate = 15m,
                SocialInsuranceRate = 29.2m,
                HealthInsuranceRate = 13.5m
            });

        var json = await TaxTools.GetTaxConfig(_api, "CZ", 2026);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("year").GetInt32().ShouldBe(2026);
        doc.RootElement.GetProperty("incomeTaxRate").GetDecimal().ShouldBe(15m);
    }

    [Fact]
    public async Task GetTaxConfig_WhenNull_ReturnsError()
    {
        _api.GetTaxConfigAsync("XX", 9999, Arg.Any<CancellationToken>())
            .Returns((TaxYearConfigDto?)null);

        var json = await TaxTools.GetTaxConfig(_api, "XX", 9999);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString()
            .ShouldContain("No tax config found");
    }
}
