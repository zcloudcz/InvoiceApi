using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that creates a new client (customer) from an IČO (registration number).
/// Automatically fetches company data from ARES by setting FetchFromAres = true
/// on CreateClientDto, so the client is created with full official data.
///
/// When a user says "Založ klienta s IČO 12345678", this tool:
/// 1. Checks if a client with that IČO already exists (prevents duplicates)
/// 2. Creates the client via IClientService (which internally calls ARES)
/// 3. Returns a confirmation with the created client details
///
/// Junior note: This is a write operation — it creates a new entity in the database.
/// Always checks for duplicates first to avoid InvalidOperationException.
/// </summary>
public class CreateClientTool : IChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<CreateClientTool> _logger;

    public CreateClientTool(IClientService clientService, ILogger<CreateClientTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "create_client";

    public string Description =>
        "Creates a new client (customer) in the system using their IČO. " +
        "Automatically fetches company data from ARES (name, address, DIČ). " +
        "Use this when the user explicitly asks to create, add, or register a client.";

    public string ParameterDescription =>
        "registration_number (string, required): Czech company IČO, exactly 8 digits.";

    /// <summary>
    /// Creates a new client by IČO.
    /// The CreateClientDto.FetchFromAres = true triggers automatic ARES data fetch
    /// inside ClientService, so we don't need to call ARES separately.
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Extract and validate the registration number parameter.
        if (!parameters.TryGetValue("registration_number", out var ico) || string.IsNullOrWhiteSpace(ico))
        {
            return ChatToolResult.Failure("Missing required parameter: registration_number (IČO).");
        }

        ico = ico.Trim().Replace(" ", "");

        _logger.LogInformation("Create client tool executing for IČO {Ico}", ico);

        // Check for existing client first — friendlier than letting CreateClientAsync throw.
        var existingClient = await _clientService.GetClientByRegistrationNumberAsync(ico, ct);
        if (existingClient != null)
        {
            _logger.LogInformation("Client with IČO {Ico} already exists (ID: {ClientId})", ico, existingClient.Id);
            return ChatToolResult.Success(
                $"Client already exists:\n" +
                $"- Name: {existingClient.CompanyName}\n" +
                $"- IČO: {existingClient.RegistrationNumber}\n" +
                $"- ID: {existingClient.Id}\n" +
                $"No new client was created because this company is already in the system.");
        }

        try
        {
            // Create the client with ARES auto-fetch enabled.
            // ClientService internally calls IAresService.GetCompanyInfoAsync
            // when FetchFromAres = true, populating CompanyName, TaxNumber, Address, etc.
            var createDto = new CreateClientDto
            {
                RegistrationNumber = ico,
                CompanyName = ico, // Temporary placeholder — overwritten by ARES data.
                FetchFromAres = true,
                IsIssuer = false // This is a customer, not the issuer (our company).
            };

            var createdClient = await _clientService.CreateClientAsync(createDto, ct);

            _logger.LogInformation("Client created successfully: {CompanyName} (ID: {ClientId})",
                createdClient.CompanyName, createdClient.Id);

            // Format the response with key client details.
            var addressText = createdClient.Address.Count > 0
                ? $"{createdClient.Address[0].Street}, {createdClient.Address[0].City}"
                : "No address";

            return ChatToolResult.Success(
                $"Client created successfully:\n" +
                $"- Name: {createdClient.CompanyName}\n" +
                $"- IČO: {createdClient.RegistrationNumber}\n" +
                $"- DIČ: {createdClient.TaxNumber ?? "N/A"}\n" +
                $"- VAT payer: {(createdClient.IsVatPayer ? "Yes" : "No")}\n" +
                $"- Address: {addressText}\n" +
                $"- Client ID: {createdClient.Id}");
        }
        catch (InvalidOperationException ex)
        {
            // ClientService throws InvalidOperationException for business rule violations
            // (e.g., duplicate registration number race condition, ARES fetch failure).
            _logger.LogWarning(ex, "Failed to create client for IČO {Ico}", ico);
            return ChatToolResult.Failure($"Could not create client: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Catch unexpected errors to prevent the whole chat from crashing.
            _logger.LogError(ex, "Unexpected error creating client for IČO {Ico}", ico);
            return ChatToolResult.Failure("An unexpected error occurred while creating the client.");
        }
    }
}
