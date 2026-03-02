using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for CreateClientTool.
/// Tests client creation via the chat tool interface, including:
/// - Successful creation with ARES auto-fetch
/// - Duplicate detection (client already exists)
/// - Error handling (service exceptions)
///
/// Uses NSubstitute to mock IClientService dependency.
/// </summary>
public class CreateClientToolTests
{
    private readonly IClientService _clientService;
    private readonly CreateClientTool _tool;

    public CreateClientToolTests()
    {
        _clientService = Substitute.For<IClientService>();
        var logger = Substitute.For<ILogger<CreateClientTool>>();
        _tool = new CreateClientTool(_clientService, logger);
    }

    [Fact]
    public void ToolName_IsCreateClient()
    {
        _tool.ToolName.ShouldBe("create_client");
    }

    [Fact]
    public async Task Execute_CreatesClient_WhenNoExistingClient()
    {
        // Arrange — no existing client, create succeeds.
        _clientService.GetClientByRegistrationNumberAsync("12345678", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        _clientService.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto
            {
                Id = 1,
                CompanyName = "Test s.r.o.",
                RegistrationNumber = "12345678",
                TaxNumber = "CZ12345678",
                IsVatPayer = true,
                Address = new List<AddressDto>
                {
                    new() { Street = "Hlavní 1", City = "Praha" }
                }
            });

        // Act
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, string> { ["registration_number"] = "12345678" });

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("created successfully");
        result.OutputText.ShouldContain("Test s.r.o.");
        result.OutputText.ShouldContain("12345678");
        result.OutputText.ShouldContain("CZ12345678");

        // Verify the CreateClientDto was constructed correctly.
        await _clientService.Received(1).CreateClientAsync(
            Arg.Is<CreateClientDto>(d =>
                d.FetchFromAres == true &&
                d.IsIssuer == false &&
                d.RegistrationNumber == "12345678"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ReturnsExistingClient_WhenDuplicate()
    {
        // Arrange — client already exists.
        _clientService.GetClientByRegistrationNumberAsync("12345678", Arg.Any<CancellationToken>())
            .Returns(new ClientDto
            {
                Id = 99,
                CompanyName = "Existing Co",
                RegistrationNumber = "12345678"
            });

        // Act
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, string> { ["registration_number"] = "12345678" });

        // Assert — should report existing, not create.
        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("already exists");
        result.OutputText.ShouldContain("Existing Co");
        result.OutputText.ShouldContain("99"); // ID

        // Verify CreateClientAsync was NOT called.
        await _clientService.DidNotReceive().CreateClientAsync(
            Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ReturnsFailure_WhenMissingParameter()
    {
        // Act
        var result = await _tool.ExecuteAsync(new Dictionary<string, string>());

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Missing required parameter");

        // Neither lookup nor create should be called.
        await _clientService.DidNotReceive()
            .GetClientByRegistrationNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _clientService.DidNotReceive()
            .CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ReturnsFailure_WhenServiceThrowsInvalidOperation()
    {
        // Arrange — no existing client, but create throws (e.g., ARES fetch failure).
        _clientService.GetClientByRegistrationNumberAsync("12345678", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        _clientService.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns<ClientDto>(x => throw new InvalidOperationException("ARES lookup failed for IČO 12345678"));

        // Act
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, string> { ["registration_number"] = "12345678" });

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("Could not create client");
        result.OutputText.ShouldContain("ARES lookup failed");
    }

    [Fact]
    public async Task Execute_ReturnsFailure_WhenUnexpectedExceptionThrown()
    {
        // Arrange — unexpected database error.
        _clientService.GetClientByRegistrationNumberAsync("12345678", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        _clientService.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns<ClientDto>(x => throw new Exception("Connection refused"));

        // Act
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, string> { ["registration_number"] = "12345678" });

        // Assert — generic error message (don't expose internal details).
        result.IsSuccess.ShouldBeFalse();
        result.OutputText.ShouldContain("unexpected error");
    }

    [Fact]
    public async Task Execute_StripsWhitespace_FromIco()
    {
        // Arrange
        _clientService.GetClientByRegistrationNumberAsync("12345678", Arg.Any<CancellationToken>())
            .Returns((ClientDto?)null);

        _clientService.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto
            {
                Id = 2,
                CompanyName = "Clean s.r.o.",
                RegistrationNumber = "12345678"
            });

        // Act — IČO with spaces.
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, string> { ["registration_number"] = " 1234 5678 " });

        // Assert
        result.IsSuccess.ShouldBeTrue();

        // Verify cleaned IČO was used.
        await _clientService.Received(1)
            .GetClientByRegistrationNumberAsync("12345678", Arg.Any<CancellationToken>());
    }
}
