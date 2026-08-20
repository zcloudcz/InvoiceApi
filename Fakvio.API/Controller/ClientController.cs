using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for managing clients (customers and issuer)
/// Provides CRUD operations and ARES integration
/// Users can only manage clients from their own company
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize] // All endpoints require authentication
public class ClientController : ControllerBase
{
    private readonly IClientService _clientService;
    private readonly ILogger<ClientController> _logger;

    public ClientController(
        IClientService clientService,
        ILogger<ClientController> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all clients (customers only, not issuers)
    /// SysAdmin can see all clients, others only non-issuer clients
    /// </summary>
    /// <param name="includeInactive">Include inactive clients in results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of all clients</returns>
    /// <response code="200">Returns list of clients</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<ClientDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ClientDto>>> GetAllClients(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/client - includeInactive: {IncludeInactive}", includeInactive);

        var allClients = await _clientService.GetAllClientsAsync(includeInactive, cancellationToken);

        // Filter out issuers (companies) - those are managed via /api/company
        var clients = allClients.Where(c => !c.IsIssuer).ToList();

        return Ok(clients);
    }

    /// <summary>
    /// Gets paginated, filtered and sorted clients (customers only, not issuers)
    /// Supports pagination, filtering by multiple criteria, and sorting
    /// </summary>
    /// <param name="filter">Filter parameters including pagination, search, and sorting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paged result of clients</returns>
    /// <response code="200">Returns paged list of clients</response>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<ClientDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ClientDto>>> GetClientsPaged(
        [FromQuery] ClientFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/client/paged - Page: {Page}, PageSize: {PageSize}, Search: {Search}",
            filter.Page, filter.PageSize, filter.Search);

        // Force IsIssuer = false to only show customers (not companies)
        filter.IsIssuer = false;

        var result = await _clientService.GetClientsPagedAsync(filter, cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Gets a specific client by ID
    /// </summary>
    /// <param name="id">Client ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Client data</returns>
    /// <response code="200">Returns the client</response>
    /// <response code="404">Client not found</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> GetClientById(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/client/{Id}", id);

        var client = await _clientService.GetClientByIdAsync(id, cancellationToken);

        if (client == null)
        {
            _logger.LogWarning("Client {Id} not found", id);
            return NotFound(new { message = $"Client with ID {id} not found" });
        }

        return Ok(client);
    }

    /// <summary>
    /// Gets client by registration number (IČO)
    /// </summary>
    /// <param name="registrationNumber">Registration number (IČO)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Client data</returns>
    /// <response code="200">Returns the client</response>
    /// <response code="404">Client not found</response>
    [HttpGet("by-registration/{registrationNumber}")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> GetClientByRegistrationNumber(
        string registrationNumber,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/client/by-registration/{RegistrationNumber}", registrationNumber);

        var client = await _clientService.GetClientByRegistrationNumberAsync(registrationNumber, cancellationToken);

        if (client == null)
        {
            _logger.LogWarning("Client with registration number {RegistrationNumber} not found", registrationNumber);
            return NotFound(new { message = $"Client with registration number {registrationNumber} not found" });
        }

        return Ok(client);
    }

    /// <summary>
    /// Gets the issuer (your company)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Issuer data</returns>
    /// <response code="200">Returns the issuer</response>
    /// <response code="404">Issuer not configured</response>
    [HttpGet("issuer")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> GetIssuer(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/client/issuer");

        var issuer = await _clientService.GetIssuerAsync(cancellationToken);

        if (issuer == null)
        {
            _logger.LogWarning("Issuer not configured");
            return NotFound(new { message = "Issuer not configured. Please create a client with IsIssuer = true" });
        }

        return Ok(issuer);
    }

    /// <summary>
    /// Creates a new client
    /// Optionally fetches data from ARES registry
    /// </summary>
    /// <param name="createDto">Client data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created client</returns>
    /// <response code="201">Client created successfully</response>
    /// <response code="400">Invalid request data</response>
    /// <response code="409">Client with this registration number already exists</response>
    [HttpPost]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ClientDto>> CreateClient(
        [FromBody] CreateClientDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/client - Creating client with registration number {RegistrationNumber}",
            createDto.RegistrationNumber);

        // Prevent non-SysAdmin from creating issuers (companies)
        // Issuers should be created via /api/company by SysAdmin only
        if (createDto.IsIssuer && GetCurrentUserRole() != EUserRole.SysAdmin)
        {
            _logger.LogWarning("Non-SysAdmin attempted to create an issuer");
            return BadRequest(new { message = "Only SysAdmin can create companies (issuers). Use /api/company endpoint." });
        }

        try
        {
            var client = await _clientService.CreateClientAsync(createDto, cancellationToken);

            _logger.LogInformation("Client created with ID {Id}", client.Id);

            return CreatedAtAction(
                nameof(GetClientById),
                new { id = client.Id },
                client);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            _logger.LogWarning("Client with registration number {RegistrationNumber} already exists",
                createDto.RegistrationNumber);
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates an existing client
    /// </summary>
    /// <param name="id">Client ID</param>
    /// <param name="updateDto">Updated data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client</returns>
    /// <response code="200">Client updated successfully</response>
    /// <response code="404">Client not found</response>
    /// <response code="400">Invalid request data</response>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ClientDto>> UpdateClient(
        long id,
        [FromBody] UpdateClientDto updateDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PUT /api/client/{Id}", id);

        var client = await _clientService.UpdateClientAsync(id, updateDto, cancellationToken);

        if (client == null)
        {
            _logger.LogWarning("Client {Id} not found for update", id);
            return NotFound(new { message = $"Client with ID {id} not found" });
        }

        _logger.LogInformation("Client {Id} updated successfully", id);
        return Ok(client);
    }

    /// <summary>
    /// Deletes a client (soft delete)
    /// </summary>
    /// <param name="id">Client ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content</returns>
    /// <response code="204">Client deleted successfully</response>
    /// <response code="404">Client not found</response>
    /// <response code="409">Client cannot be deleted (has invoices)</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteClient(
        long id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DELETE /api/client/{Id}", id);

        try
        {
            var deleted = await _clientService.DeleteClientAsync(id, cancellationToken);

            if (!deleted)
            {
                _logger.LogWarning("Client {Id} not found for deletion", id);
                return NotFound(new { message = $"Client with ID {id} not found" });
            }

            _logger.LogInformation("Client {Id} deleted successfully", id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Cannot delete client {Id}: {Message}", id, ex.Message);
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Fetches client data from ARES registry by registration number
    /// Returns data without saving to database (preview)
    /// </summary>
    /// <param name="registrationNumber">Registration number (IČO)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Client data from ARES</returns>
    /// <response code="200">Returns client data from ARES</response>
    /// <response code="400">Failed to fetch from ARES</response>
    [HttpGet("ares/{registrationNumber}")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ClientDto>> FetchFromAres(
        string registrationNumber,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/client/ares/{RegistrationNumber}", registrationNumber);

        try
        {
            var client = await _clientService.FetchFromAresAsync(registrationNumber, cancellationToken);
            return Ok(client);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to fetch from ARES: {Message}", ex.Message);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Adds an address to existing client
    /// </summary>
    /// <param name="id">Client ID</param>
    /// <param name="addressDto">Address data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client</returns>
    /// <response code="200">Address added successfully</response>
    /// <response code="404">Client not found</response>
    [HttpPost("{id}/address")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> AddAddress(
        long id,
        [FromBody] CreateAddressDto addressDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/client/{Id}/address", id);

        var client = await _clientService.AddAddressAsync(id, addressDto, cancellationToken);

        if (client == null)
        {
            _logger.LogWarning("Client {Id} not found", id);
            return NotFound(new { message = $"Client with ID {id} not found" });
        }

        return Ok(client);
    }

    /// <summary>
    /// Adds a contact to existing client
    /// </summary>
    /// <param name="id">Client ID</param>
    /// <param name="contactDto">Contact data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client</returns>
    /// <response code="200">Contact added successfully</response>
    /// <response code="404">Client not found</response>
    [HttpPost("{id}/contact")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> AddContact(
        long id,
        [FromBody] CreateContactDto contactDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/client/{Id}/contact", id);

        var client = await _clientService.AddContactAsync(id, contactDto, cancellationToken);

        if (client == null)
        {
            _logger.LogWarning("Client {Id} not found", id);
            return NotFound(new { message = $"Client with ID {id} not found" });
        }

        return Ok(client);
    }

    /// <summary>
    /// Adds a bank account to an existing client.
    /// If this is the first bank account, it is automatically set as default.
    /// If IsDefault is true, clears default from all other accounts.
    /// </summary>
    /// <param name="id">Client ID</param>
    /// <param name="bankAccountDto">Bank account data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client with all bank accounts</returns>
    /// <response code="200">Bank account added successfully</response>
    /// <response code="404">Client not found</response>
    [HttpPost("{id}/bank-account")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> AddBankAccount(
        long id,
        [FromBody] CreateBankAccountDto bankAccountDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("POST /api/client/{Id}/bank-account", id);

        var client = await _clientService.AddBankAccountAsync(id, bankAccountDto, cancellationToken);

        if (client == null)
        {
            _logger.LogWarning("Client {Id} not found", id);
            return NotFound(new { message = $"Client with ID {id} not found" });
        }

        return Ok(client);
    }

    /// <summary>
    /// Updates or creates billing settings for client
    /// </summary>
    /// <param name="id">Client ID</param>
    /// <param name="settingsDto">Billing settings data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client</returns>
    /// <response code="200">Billing settings updated successfully</response>
    /// <response code="404">Client not found</response>
    [HttpPut("{id}/billing-settings")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> UpdateBillingSettings(
        long id,
        [FromBody] CreateBillingSettingsDto settingsDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PUT /api/client/{Id}/billing-settings", id);

        var client = await _clientService.UpdateBillingSettingsAsync(id, settingsDto, cancellationToken);

        if (client == null)
        {
            _logger.LogWarning("Client {Id} not found", id);
            return NotFound(new { message = $"Client with ID {id} not found" });
        }

        return Ok(client);
    }

    // ─── Advance tax receipt mode (issuer-only company setting) ───────────────

    /// <summary>
    /// Gets the advance-tax-receipt auto-conversion mode of this tenant's issuer.
    /// Readable by every authenticated user — the invoice screens show the resulting
    /// behaviour, so hiding the value would only be confusing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current mode</returns>
    /// <response code="200">Returns the current mode</response>
    /// <response code="404">Tenant has no issuer</response>
    [HttpGet("issuer/advance-tax-receipt-mode")]
    [ProducesResponseType(typeof(EAdvanceTaxReceiptMode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EAdvanceTaxReceiptMode>> GetAdvanceTaxReceiptMode(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GET /api/client/issuer/advance-tax-receipt-mode");

        var mode = await _clientService.GetAdvanceTaxReceiptModeAsync(cancellationToken);

        if (mode == null)
            return NotFound(new { message = "Issuer not configured." });

        return Ok(mode.Value);
    }

    /// <summary>
    /// Sets the advance-tax-receipt auto-conversion mode of this tenant's issuer.
    /// Restricted to Admin/SysAdmin — it is a company-wide accounting decision,
    /// not a per-user preference.
    /// </summary>
    /// <param name="dto">New mode</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The stored mode</returns>
    /// <response code="200">Mode updated successfully</response>
    /// <response code="404">Tenant has no issuer</response>
    [HttpPut("issuer/advance-tax-receipt-mode")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(typeof(EAdvanceTaxReceiptMode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EAdvanceTaxReceiptMode>> SetAdvanceTaxReceiptMode(
        [FromBody] SetAdvanceTaxReceiptModeDto dto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("PUT /api/client/issuer/advance-tax-receipt-mode -> {Mode}", dto.Mode);

        var updated = await _clientService.SetAdvanceTaxReceiptModeAsync(dto.Mode, cancellationToken);

        if (!updated)
            return NotFound(new { message = "Issuer not configured." });

        return Ok(dto.Mode);
    }

    #region Helper Methods

    /// <summary>
    /// Gets current user's role from JWT claims
    /// </summary>
    private EUserRole GetCurrentUserRole()
    {
        var roleClaim = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        return Enum.TryParse<EUserRole>(roleClaim, out var role) ? role : EUserRole.User;
    }

    /// <summary>
    /// Gets current user's company ID from JWT claims
    /// Returns null for SysAdmin
    /// </summary>
    private long? GetCurrentUserCompanyId()
    {
        var companyIdClaim = User.FindFirst("CompanyId")?.Value;
        return long.TryParse(companyIdClaim, out var companyId) ? companyId : null;
    }

    #endregion
}
