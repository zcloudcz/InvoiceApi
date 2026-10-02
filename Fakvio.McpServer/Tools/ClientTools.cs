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
    [McpServerTool(Title = "List clients", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "List clients (customers) with pagination and search. " +
        "Returns paginated results with company details, addresses, contacts, and billing settings.")]
    public static async Task<string> ListClients(
        IFakvioApiClient api,
        [Description("Page number (1-based, default 1)")] int page = 1,
        [Description("Items per page (default 20, max 100)")] int pageSize = 20,
        [Description("Search by company name, trading name, registration number (IČO), or city")] string? search = null,
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
    [McpServerTool(Title = "Get client", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
    [McpServerTool(Title = "Create client", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Create a new client (customer). Requires companyName at minimum. " +
        "JSON object with: companyName (required), registrationNumber (IČO), taxNumber (DIČ), " +
        "isVatPayer, language ('cs'/'en'), fetchFromAres (auto-fill from Czech registry), " +
        "address [{addressType, street, city, postalCode, country}], " +
        "contact [{contactType ('Email'/'Phone'), contactValue}], " +
        "bankAccount [{accountNumber, bankName, iban, swift}].")]
    public static async Task<string> CreateClient(
        IFakvioApiClient api,
        [Description("Client to create — companyName is required, everything else optional")] CreateClientDto client,
        CancellationToken ct = default)
    {
        if (client is null)
            return JsonSerializer.Serialize(new { error = "client is required." }, JsonOptions);

        try
        {
            var result = await api.CreateClientAsync(client, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Updates an existing client.
    /// Only provided fields are changed — null fields are left unchanged.
    /// </summary>
    [McpServerTool(Title = "Update client", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Update an existing client. Only provided fields are changed (partial update). " +
        "JSON object with optional: companyName, taxNumber, isVatPayer, isActive, language, " +
        "refreshFromAres (re-fetch from ARES registry).")]
    public static async Task<string> UpdateClient(
        IFakvioApiClient api,
        [Description("The client ID to update")] long clientId,
        [Description("Fields to change — only provided (non-null) fields are updated")] UpdateClientDto changes,
        CancellationToken ct = default)
    {
        if (changes is null)
            return JsonSerializer.Serialize(new { error = "changes is required." }, JsonOptions);

        try
        {
            var result = await api.UpdateClientAsync(clientId, changes, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Looks up a Czech company by IČO (registration number) in the ARES registry.
    /// Returns company data preview — does NOT save to database.
    /// </summary>
    [McpServerTool(Title = "Look up company in ARES", ReadOnly = true, Idempotent = true, OpenWorld = true), Description(
        "Look up a Czech company in the ARES registry by IČO (registration number). " +
        "Returns company name, address, VAT status, etc. Does NOT create a client — " +
        "use CreateClient with fetchFromAres=true for that.")]
    public static async Task<string> LookupAres(
        IFakvioApiClient api,
        [Description("Czech registration number (IČO), e.g., '12345678'")] string registrationNumber,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.FetchFromAresAsync(registrationNumber, ct);

            if (result is null)
                return JsonSerializer.Serialize(
                    new { error = $"ARES lookup failed for IČO '{registrationNumber}'. The number may be invalid." },
                    JsonOptions);

            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>
    /// Verifies an EU VAT identification number (DIČ) against VIES.
    /// Works for any EU member state, unlike LookupAres which is Czech-only (IČO).
    /// </summary>
    [McpServerTool(Title = "Verify VAT ID in VIES", ReadOnly = true, Idempotent = true, OpenWorld = true), Description(
        "Verify an EU VAT identification number (DIČ) against VIES (EU VAT registry). " +
        "Returns whether the number is currently registered, plus name/address when the " +
        "member state releases them. Use for any EU country — LookupAres is Czech IČO only.")]
    public static async Task<string> VerifyVatVies(
        IFakvioApiClient api,
        [Description("EU VAT ID including the 2-letter country prefix, e.g. 'CZ12345678'")] string vatId,
        CancellationToken ct = default)
    {
        try
        {
            var result = await api.VerifyVatViesAsync(vatId, ct);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
    [McpServerTool(Title = "Get my company", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(
        "Get the authenticated user's own company (issuer). " +
        "This is the entity that appears as the sender/creator on invoices. " +
        "Useful for getting issuerId when creating invoices. " +
        "The response's bankAccount list has each account's id, isDefault and currencyCode — " +
        "use one of those ids as bankAccountId in create_invoice / create_invoice_from_template / " +
        "set_invoice_bank_account. There is no separate list_bank_accounts tool, this is it.")]
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}
