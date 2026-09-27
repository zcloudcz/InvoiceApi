using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for managing clients (customers and issuer)
/// Handles business logic for client operations
/// </summary>
public interface IClientService
{
    /// <summary>
    /// Gets all clients (optionally filtered by active status)
    /// </summary>
    /// <param name="includeInactive">Include inactive clients in results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of clients</returns>
    Task<List<ClientDto>> GetAllClientsAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets paginated, filtered and sorted clients
    /// </summary>
    /// <param name="filter">Filter parameters including pagination and sorting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paged result of clients</returns>
    Task<PagedResult<ClientDto>> GetClientsPagedAsync(ClientFilterDto filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific client by ID
    /// </summary>
    /// <param name="clientId">Client ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Client data or null if not found</returns>
    Task<ClientDto?> GetClientByIdAsync(long clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets client by registration number (IČO)
    /// </summary>
    /// <param name="registrationNumber">Registration number</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Client data or null if not found</returns>
    Task<ClientDto?> GetClientByRegistrationNumberAsync(string registrationNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk existence check by registration number (IČO) — a single lean query (no navigation
    /// collections loaded), for callers that only need "does a client with this IČO exist, and
    /// what's its ID" for many IČOs at once (e.g. CSV import dedup). Prefer this over calling
    /// <see cref="GetClientByRegistrationNumberAsync"/> in a loop — that method loads the full
    /// client graph (addresses/contacts/bank accounts/billing settings) per call.
    /// </summary>
    /// <param name="registrationNumbers">Registration numbers to look up (duplicates/blank entries are ignored).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Map of registration number → existing client ID, for the ones that exist.</returns>
    Task<Dictionary<string, long>> GetClientIdsByRegistrationNumbersAsync(IEnumerable<string> registrationNumbers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the issuer (your company)
    /// There should be only one issuer per database
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Issuer data or null if not configured</returns>
    Task<ClientDto?> GetIssuerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new client
    /// Optionally fetches data from ARES registry
    /// </summary>
    /// <param name="createDto">Client creation data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created client</returns>
    Task<ClientDto> CreateClientAsync(CreateClientDto createDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing client
    /// </summary>
    /// <param name="clientId">Client ID to update</param>
    /// <param name="updateDto">Updated data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client or null if not found</returns>
    Task<ClientDto?> UpdateClientAsync(long clientId, UpdateClientDto updateDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a client (soft delete - sets IsActive = false)
    /// Cannot delete client if they have invoices
    /// </summary>
    /// <param name="clientId">Client ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found or has invoices</returns>
    Task<bool> DeleteClientAsync(long clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches client data from ARES by registration number
    /// Updates existing client or returns data for new client creation
    /// </summary>
    /// <param name="registrationNumber">Registration number (IČO)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Client data from ARES</returns>
    Task<ClientDto> FetchFromAresAsync(string registrationNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an address to existing client
    /// </summary>
    /// <param name="clientId">Client ID</param>
    /// <param name="addressDto">Address data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client or null if not found</returns>
    Task<ClientDto?> AddAddressAsync(long clientId, CreateAddressDto addressDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a contact to existing client
    /// </summary>
    /// <param name="clientId">Client ID</param>
    /// <param name="contactDto">Contact data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client or null if not found</returns>
    Task<ClientDto?> AddContactAsync(long clientId, CreateContactDto contactDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates or creates billing settings for client
    /// </summary>
    /// <param name="clientId">Client ID</param>
    /// <param name="settingsDto">Billing settings data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client or null if not found</returns>
    Task<ClientDto?> UpdateBillingSettingsAsync(long clientId, CreateBillingSettingsDto settingsDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a bank account to an existing client.
    /// If this is the first bank account, it is automatically set as default.
    /// If IsDefault is true, clears default from all other accounts.
    /// </summary>
    /// <param name="clientId">Client ID</param>
    /// <param name="bankAccountDto">Bank account data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated client or null if not found</returns>
    Task<ClientDto?> AddBankAccountAsync(long clientId, CreateBankAccountDto bankAccountDto, CancellationToken cancellationToken = default);
}
