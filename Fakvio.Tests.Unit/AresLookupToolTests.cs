using AresService;
using AresService.Model;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for AresLookupTool.
/// Tests ARES company lookup via the chat tool interface.
/// Uses NSubstitute to mock the IAresService dependency.
/// </summary>
public class AresLookupToolTests
{
    private readonly IAresService _aresService;
    private readonly AresLookupTool _tool;

    public AresLookupToolTests()
    {
        _aresService = Substitute.For<IAresService>();
        var logger = Substitute.For<ILogger<AresLookupTool>>();
        _tool = new AresLookupTool(_aresService, logger);
    }

    [Fact]
    public void ToolName_IsAresLookup()
    {
        _tool.ToolName.ShouldBe("ares_lookup");
    }

    [Fact]
    public async Task Execute_ReturnsSuccess_WhenAresFindsCompany()
    {
        // Arrange — mock ARES to return a successful company lookup.
        _aresService.GetCompanyInfoAsync("12345678", Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                IsSuccessful = true,
                CompanyName = "Test s.r.o.",
                RegistrationNumber = "12345678",
                TaxNumber = "CZ12345678",
                IsVatPayer = true,
                LegalForm = "s.r.o.",
                Address = new AresAddress
                {
                    Street = "Hlavní 1",
                    City = "Praha",
                    PostalCode = "110 00"
                }
            });

        // Act
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, string> { ["registration_number"] = "12345678" });

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Test s.r.o.");
        result.OutputText.ShouldContain("12345678");
        result.OutputText.ShouldContain("CZ12345678");
        result.OutputText.ShouldContain("Hlavní 1");
        result.OutputText.ShouldContain("Praha");
        result.OutputText.ShouldContain("Yes"); // VAT payer
    }

    [Fact]
    public async Task Execute_ReturnsSuccess_WithoutAddress()
    {
        // Arrange — company found but no address available.
        _aresService.GetCompanyInfoAsync("12345678", Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                IsSuccessful = true,
                CompanyName = "Minimal s.r.o.",
                RegistrationNumber = "12345678",
                IsVatPayer = false,
                Address = null
            });

        // Act
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, string> { ["registration_number"] = "12345678" });

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Minimal s.r.o.");
        result.OutputText.ShouldContain("Address not available");
        result.OutputText.ShouldContain("No"); // Not VAT payer
    }

    [Fact]
    public async Task Execute_ReturnsFailure_WhenAresReturnsNotFound()
    {
        // Arrange — ARES lookup failed (company not found).
        _aresService.GetCompanyInfoAsync("99999999", Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                IsSuccessful = false,
                ErrorMessage = "Company not found in ARES registry."
            });

        // Act
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, string> { ["registration_number"] = "99999999" });

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Error:");
        result.OutputText.ShouldContain("not found");
    }

    [Fact]
    public async Task Execute_StripsWhitespace_FromIco()
    {
        // Arrange — IČO with extra whitespace.
        _aresService.GetCompanyInfoAsync("12345678", Arg.Any<CancellationToken>())
            .Returns(new AresCompanyInfo
            {
                IsSuccessful = true,
                CompanyName = "Whitespace s.r.o.",
                RegistrationNumber = "12345678"
            });

        // Act — pass IČO with spaces (AI might format it this way).
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, string> { ["registration_number"] = " 1234 5678 " });

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Whitespace s.r.o.");

        // Verify the cleaned IČO was passed to ARES.
        await _aresService.Received(1)
            .GetCompanyInfoAsync("12345678", Arg.Any<CancellationToken>());
    }
}
