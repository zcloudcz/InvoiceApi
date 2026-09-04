using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.Client;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for managing clients (customers and issuers).
/// Includes ARES integration for Czech company registry lookups.
///
/// Junior note: "Issuer" = the user's own company (who sends invoices).
/// "Client" = a customer (who receives invoices).
/// The API automatically filters by the authenticated user's company.
/// </summary>
[McpServerToolType]
public static class ClientTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Lists clients with pagination and optional search.
    /// By default returns only active customers (not issuers).
    /// </summary>
    [McpServerTool, Description(
        "List clients (customers) with pagination and search. " +
        "Returns paginated results with company details, addresses, contacts, and billing settings.")]
    public static async Task<string> ListClients(
        IFakvioApiClient api,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 20, max 100)")] int pageSize = 20,
        [Description("Search by company name, trading name, registration number (IÄO), or city")] string? search = null,
        [Description("Filter to VAT payers only (true/false)")] bool? isVatPayer = null,
        [Description("Include inactive (deleted) clients (default false)")] bool includeInactive = false,
        CancellationToken ct = default)
    {
        try
        {
            var filter = new ClientFilterDto
            {
                Page = page,
                PageSize = Math.Min(pageSize, 100),
                Search = search,
                IsVatPayer = isVatPayer,
                IncludeInactive = includeInactive
            };

            var result = await api.GetClientsPagedAsync(filter, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets a single client by ID with all details (addresses, contacts, bank accounts, billing).
    /// </summary>
    [McpServerTool, Description(
        "Get a single client by ID. Returns full details including addresses, " +
        "contacts, bank accounts, and billing settings.")]
    public static async Task<string> GetClient(
        IFakvioApiClient api,
        [Description("The client ID (database primary key)")] long clientId,
        CancellationToken ct = default)
    {
        try
        {
            var client = await api.GetClientByIdAsync(clientId, ct);

            if (client is null)
                return JsonSerializer.Serialize(new { error = $"Client with ID {clientId} not found." }, JsonOptions);

            return JsonSerializer.Serialize(client, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Creates a new client (customer).
    /// At minimum requires companyName. Set fetchFromAres=true to auto-fill from Czech registry.
    /// </summary>
    [McpServerTool, Description(
        "Create a new client (customer). Requires companyName at minimum. " +
        "JSON object with: companyName (required), registrationNumber (IÄO), taxNumber (DIÄ), " +
        "isVatPayer, language ('cs'/'en'), fetchFromAres (auto-fill from Czech registry), " +
        "address [{addressType, street, city, postalCode, country}], " +
        "contact [{contactType ('Email'/'Phone'), contactValue}], " +
        "bankAccount [{accountNumber, bankName, iban, swift}].")]
    public static async Task<string> CreateClient(
        IFakvioApiClient api,
        [Description(
            "JSON string of CreateClientDto. Example: " +
            "{\"companyName\":\"Acme s.r.o.\",\"registrationNumber\":\"12345678\"," +
            "\"isVatPayer\":true,\"fetchFromAres\":true}"
        )] string clientJson,
        CancellationToken ct = default)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<CreateClientDto>(clientJson, JsonOptions);

            if (dto is null)
                return JsonSerializer.Serialize(new { error = "Invalid JSON: could not deserialize CreateClientDto." }, JsonOptions);

            var result = await api.CreateClientAsync(dto, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid JSON format: {ex.Message}" }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Updates an existing client.
    /// Only provided fields are changed â null fields are left unchanged.
    /// </summary>
    [McpServerTool, Description(
        "Update an existing client. Only provided fields are changed (partial update). " +
        "JSON object with optional: companyName, taxNumber, isVatPayer, isActive, language, " +
        "refreshFromAres (re-fetch from ARES registry).")]
    public static async Task<string> UpdateClient(
        IFakvioApiClient api,
        [Description("The client ID to update")] long clientId,
        [Description(
            "JSON string of UpdateClientDto. Example: " +
            "{\"companyName\":\"New Name s.r.o.\",\"isVatPayer\":false}"
        )] string clientJson,
        CancellationToken ct = default)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<UpdateClientDto>(clientJson, JsonOptions);

            if (dto is null)
                return JsonSerializer.Serialize(new { error = "Invalid JSON: could not deserialize UpdateClientDto." }, JsonOptions);

            var result = await api.UpdateClientAsync(clientId, dto, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid JSON format: {ex.Message}" }, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Looks up a Czech company by IÄO (registration number) in the ARES registry.
    /// Returns company data preview â does NOT save to database.
    /// </summary>
    [McpServerTool, Description(
        "Look up a Czech company in the ARES registry by IÄO (registration number). " +
        "Returns company name, address, VAT status, etc. Does NOT create a client â " +
        "use CreateClient with fetchFromAres=true for that.")]
    public static async Task<string> LookupAres(
        IFakvioApiClient api,
        [Description("Czech registration number (IÄO), e.g., '12345678'")] string registrationNumber,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.FetchFromAresAsync(registrationNumber, ct);

            if (result is null)
                return JsonSerializer.Serialize(
                    new { error = $"ARES lookup failed for IÄO '{registrationNumber}'. The number may be invalid." },
                    JsonOptions);

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Gets the authenticated user's own company (issuer).
    /// This is the company that appears as the sender on invoices.
    /// </summary>
    [McpServerTool, Description(
        "Get the authenticated user's own company (issuer). " +
        "This is the entity that appears as the sender/creator on invoices. " +
        "Useful for getting issuerId when creating invoices.")]
    public static async Task<string> GetIssuer(
        IFakvioApiClient api,
        CancellationToken ct = default)
    {
        try
        {
            var issuer = await api.GetIssuerAsync(ct);

            if (issuer is null)
                return JsonSerializer.Serialize(new { error = "Issuer not found. The authenticated user may not have a company configured." }, JsonOptions);

            return JsonSerializer.Serialize(issuer, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a domain error — propagate it instead of
            // swallowing it into a fake "error" JSON result (issue #279).
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}
