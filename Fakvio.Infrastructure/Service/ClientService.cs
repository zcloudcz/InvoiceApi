using AresService;
using Fakvio.Application.Common.Extensions;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Validation;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of client service
/// Handles all client-related business logic
/// </summary>
public class ClientService : IClientService
{
    private readonly TenantDbContext _context;
    private readonly IAresService _aresService;
    private readonly ILogger<ClientService> _logger;

    public ClientService(
        TenantDbContext context,
        IAresService aresService,
        ILogger<ClientService> logger)
    {
        _context = context;
        _aresService = aresService;
        _logger = logger;
    }

    /// <summary>
    /// Maps a Client entity to ClientDto using ZMapper v1.1.0.
    /// ZMapper now handles BaseEntity (Id, CreatedAt, UpdatedAt) and nested collection Ids
    /// automatically, so no manual property assignments are needed for Client mapping.
    /// </summary>
    private static ClientDto MapToDto(Client entity)
    {
        var dto = entity.ToClientDto();

        // Manual mapping for enum → string properties (ZMapper can't auto-map enum? → string?).
        dto.TaxRegime = entity.TaxRegime?.ToString();
        dto.ActivityType = entity.ActivityType?.ToString();
        dto.IsMainActivity = entity.IsMainActivity;
        dto.FlatRateBand = entity.FlatRateBand?.ToString();

        return dto;
    }

    /// <summary>
    /// Gets all clients with related data
    /// </summary>
    public async Task<List<ClientDto>> GetAllClientsAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching all clients (includeInactive: {IncludeInactive})", includeInactive);

        // AsNoTracking: read-only list — results are mapped to DTOs
        // AsSplitQuery: Address and Contact are both collection navigations — without split query,
        // EF Core generates a single SQL JOIN that causes a cartesian explosion (N x M rows).
        // AsSplitQuery sends separate SELECT per collection, avoiding the row multiplication.
        var query = _context.Client
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .Include(c => c.BankAccount)
            .Include(c => c.BillingSettings)
            .AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        var clients = await query.ToListAsync(cancellationToken);

        return clients.Select(c => MapToDto(c)).ToList();
    }

    /// <summary>
    /// Gets paginated, filtered and sorted clients
    /// </summary>
    public async Task<PagedResult<ClientDto>> GetClientsPagedAsync(
        ClientFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching paged clients (Page: {Page}, PageSize: {PageSize}, Search: {Search})",
            filter.Page, filter.PageSize, filter.Search);

        // AsNoTracking: read-only paged query — results are mapped to DTOs
        // AsSplitQuery: Address and Contact are both collection navigations — prevents cartesian explosion.
        var query = _context.Client
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .Include(c => c.BankAccount)
            .Include(c => c.BillingSettings)
            .AsQueryable();

        // Apply filters
        if (!filter.IncludeInactive)
        {
            query = query.Where(c => c.IsActive);
        }

        if (filter.IsVatPayer.HasValue)
        {
            query = query.Where(c => c.IsVatPayer == filter.IsVatPayer.Value);
        }

        if (filter.IsIssuer.HasValue)
        {
            query = query.Where(c => c.IsIssuer == filter.IsIssuer.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.City))
        {
            query = query.Where(c => c.Address.Any(a => a.City != null && a.City.ToLower().Contains(filter.City.ToLower())));
        }

        if (!string.IsNullOrWhiteSpace(filter.Country))
        {
            query = query.Where(c => c.Address.Any(a => a.Country != null && a.Country.ToLower().Contains(filter.Country.ToLower())));
        }

        // Apply search
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.ToLower();
            query = query.Where(c =>
                (c.CompanyName != null && c.CompanyName.ToLower().Contains(search)) ||
                (c.TradingName != null && c.TradingName.ToLower().Contains(search)) ||
                (c.RegistrationNumber != null && c.RegistrationNumber.Contains(search)) ||
                c.Address.Any(a => a.City != null && a.City.ToLower().Contains(search)));
        }

        // Apply sorting
        var validSortFields = new[] { "CompanyName", "RegistrationNumber", "TaxNumber", "CreatedAt", "UpdatedAt" };
        var sortBy = !string.IsNullOrWhiteSpace(filter.SortBy) && validSortFields.Contains(filter.SortBy, StringComparer.OrdinalIgnoreCase)
            ? filter.SortBy
            : "CompanyName";

        query = query.ApplySorting(sortBy, filter.IsDescending);

        // Get paged results
        var pagedResult = await query.ToPagedResultAsync(filter.Page, filter.PageSize, cancellationToken);

        // Map to DTOs
        return new PagedResult<ClientDto>(
            pagedResult.Items.Select(c => MapToDto(c)).ToList(),
            pagedResult.TotalCount,
            pagedResult.PageNumber,
            pagedResult.PageSize);
    }

    /// <summary>
    /// Gets client by ID with all related data
    /// </summary>
    public async Task<ClientDto?> GetClientByIdAsync(
        long clientId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching client by ID: {ClientId}", clientId);

        // AsNoTracking: read-only lookup — result is mapped to DTO
        // AsSplitQuery: Address and Contact are both collection navigations — prevents cartesian explosion.
        var client = await _context.Client
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .Include(c => c.BankAccount)
            .Include(c => c.BillingSettings)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        return client != null ? MapToDto(client) : null;
    }

    /// <summary>
    /// Gets client by registration number
    /// </summary>
    public async Task<ClientDto?> GetClientByRegistrationNumberAsync(
        string registrationNumber,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching client by registration number: {RegistrationNumber}", registrationNumber);

        // AsNoTracking: read-only lookup — result is mapped to DTO
        // AsSplitQuery: Address and Contact are both collection navigations — prevents cartesian explosion.
        var client = await _context.Client
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .Include(c => c.BankAccount)
            .Include(c => c.BillingSettings)
            .FirstOrDefaultAsync(c => c.RegistrationNumber == registrationNumber, cancellationToken);

        return client != null ? MapToDto(client) : null;
    }

    /// <summary>
    /// Gets the issuer (your company)
    /// </summary>
    public async Task<ClientDto?> GetIssuerAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching issuer");

        // AsNoTracking: read-only lookup — result is mapped to DTO
        // AsSplitQuery: Address and Contact are both collection navigations — prevents cartesian explosion.
        // OrderBy(Id): deterministic ordering when multiple issuers exist (should be only one per tenant).
        var issuer = await _context.Client
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .Include(c => c.BankAccount)
            .Include(c => c.BillingSettings)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(c => c.IsIssuer, cancellationToken);

        return issuer != null ? MapToDto(issuer) : null;
    }

    /// <summary>
    /// Creates a new client
    /// Optionally fetches data from ARES
    /// </summary>
    public async Task<ClientDto> CreateClientAsync(
        CreateClientDto createDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating new client with registration number: {RegistrationNumber}",
            createDto.RegistrationNumber);

        // Check if client already exists (only when registration number is provided)
        if (!string.IsNullOrWhiteSpace(createDto.RegistrationNumber))
        {
            var existingClient = await _context.Client
                .FirstOrDefaultAsync(c => c.RegistrationNumber == createDto.RegistrationNumber, cancellationToken);

            if (existingClient != null)
            {
                throw new InvalidOperationException($"Client with registration number {createDto.RegistrationNumber} already exists");
            }
        }

        // Fetch from ARES if requested (requires registration number)
        if (createDto.FetchFromAres && !string.IsNullOrWhiteSpace(createDto.RegistrationNumber))
        {
            _logger.LogInformation("Fetching client data from ARES");
            var aresInfo = await _aresService.GetCompanyInfoAsync(createDto.RegistrationNumber, cancellationToken);

            if (aresInfo.IsSuccessful)
            {
                // Merge ARES data with provided data
                createDto.CompanyName = aresInfo.CompanyName;
                createDto.TaxNumber = aresInfo.TaxNumber ?? createDto.TaxNumber;
                createDto.IsVatPayer = aresInfo.IsVatPayer;

                // Add address from ARES if no addresses provided
                if (aresInfo.Address != null && createDto.Address.Count == 0)
                {
                    createDto.Address.Add(new CreateAddressDto
                    {
                        AddressType = Domain.Enums.EAddressType.Primary,
                        Street = aresInfo.Address.Street,
                        City = aresInfo.Address.City,
                        PostalCode = aresInfo.Address.PostalCode,
                        Country = aresInfo.Address.Country,
                        IsPrimary = true
                    });
                }
            }
            else
            {
                _logger.LogWarning("Failed to fetch data from ARES: {ErrorMessage}", aresInfo.ErrorMessage);
            }
        }

        // Create client entity
        var client = new Client
        {
            RegistrationNumber = createDto.RegistrationNumber,
            TaxNumber = createDto.TaxNumber,
            CompanyName = createDto.CompanyName,
            TradingName = createDto.TradingName,
            IsVatPayer = createDto.IsVatPayer,
            IsIssuer = createDto.IsIssuer,
            IsActive = true,
            LastAresFetchDate = createDto.FetchFromAres ? DateTime.UtcNow : null,
            // Tax regime fields — parse enum strings to strongly-typed values.
            TaxRegime = Enum.TryParse<Domain.Enums.ETaxRegime>(createDto.TaxRegime, out var regime) ? regime : null,
            ActivityType = Enum.TryParse<Domain.Enums.EActivityType>(createDto.ActivityType, out var activity) ? activity : null,
            IsMainActivity = createDto.IsMainActivity,
            FlatRateBand = Enum.TryParse<Domain.Enums.EFlatRateBand>(createDto.FlatRateBand, out var band) ? band : null,
            Color = createDto.Color
        };

        // Add addresses — ZMapper handles property mapping (AddressType, Street, City, etc.)
        foreach (var addressDto in createDto.Address)
        {
            client.Address.Add(addressDto.ToAddress());
        }

        // Add contacts — ZMapper handles property mapping (ContactType, ContactValue, Label, IsPrimary)
        foreach (var contactDto in createDto.Contact)
        {
            client.Contact.Add(contactDto.ToContact());
        }

        // Add bank accounts — first account automatically becomes default if none is explicitly set.
        // ZMapper maps all properties; we only override IsDefault for auto-default logic.
        if (createDto.BankAccount.Count > 0)
        {
            var hasExplicitDefault = createDto.BankAccount.Any(b => b.IsDefault);
            foreach (var (bankDto, index) in createDto.BankAccount.Select((b, i) => (b, i)))
            {
                ValidateBankAccountOrThrow(bankDto.AccountNumber, bankDto.IBAN);

                var bankAccount = bankDto.ToBankAccount();
                // Auto-default: first account is default if no explicit default is set
                if (!hasExplicitDefault)
                    bankAccount.IsDefault = index == 0;
                client.BankAccount.Add(bankAccount);
            }
        }

        // Add billing settings if provided — ZMapper maps all properties
        if (createDto.BillingSettings != null)
        {
            client.BillingSettings = createDto.BillingSettings.ToBillingSettings();
        }

        _context.Client.Add(client);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created client with ID: {ClientId}", client.Id);

        // Reload with includes
        var createdClient = await GetClientByIdAsync(client.Id, cancellationToken);
        return createdClient!;
    }

    /// <summary>
    /// Updates an existing client
    /// </summary>
    public async Task<ClientDto?> UpdateClientAsync(
        long clientId,
        UpdateClientDto updateDto,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating client: {ClientId}", clientId);

        var client = await _context.Client
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client == null)
        {
            _logger.LogWarning("Client not found: {ClientId}", clientId);
            return null;
        }

        // Refresh from ARES if requested (requires registration number)
        if (updateDto.RefreshFromAres && !string.IsNullOrWhiteSpace(client.RegistrationNumber))
        {
            _logger.LogInformation("Refreshing client data from ARES");
            var aresInfo = await _aresService.RefreshCompanyInfoAsync(client.RegistrationNumber, cancellationToken);

            if (aresInfo.IsSuccessful)
            {
                client.CompanyName = aresInfo.CompanyName;
                client.TaxNumber = aresInfo.TaxNumber ?? client.TaxNumber;
                client.IsVatPayer = aresInfo.IsVatPayer;
                client.LastAresFetchDate = DateTime.UtcNow;
            }
        }

        // Update fields if provided
        if (updateDto.CompanyName != null)
            client.CompanyName = updateDto.CompanyName;

        if (updateDto.TaxNumber != null)
            client.TaxNumber = updateDto.TaxNumber;

        if (updateDto.TradingName != null)
            client.TradingName = updateDto.TradingName;

        if (updateDto.IsVatPayer.HasValue)
            client.IsVatPayer = updateDto.IsVatPayer.Value;

        if (updateDto.IsActive.HasValue)
            client.IsActive = updateDto.IsActive.Value;

        // Update display color — empty string clears the color
        if (updateDto.Color != null)
            client.Color = string.IsNullOrEmpty(updateDto.Color) ? null : updateDto.Color;

        // Update tax regime fields if provided.
        if (updateDto.TaxRegime != null)
            client.TaxRegime = Enum.TryParse<Domain.Enums.ETaxRegime>(updateDto.TaxRegime, out var regime) ? regime : null;

        if (updateDto.ActivityType != null)
            client.ActivityType = Enum.TryParse<Domain.Enums.EActivityType>(updateDto.ActivityType, out var activity) ? activity : null;

        if (updateDto.IsMainActivity.HasValue)
            client.IsMainActivity = updateDto.IsMainActivity.Value;

        if (updateDto.FlatRateBand != null)
            client.FlatRateBand = Enum.TryParse<Domain.Enums.EFlatRateBand>(updateDto.FlatRateBand, out var band) ? band : null;

        // Update addresses if provided — replaces all existing addresses
        if (updateDto.Address != null)
        {
            // Load existing addresses so EF can track removal
            await _context.Entry(client).Collection(c => c.Address).LoadAsync(cancellationToken);
            client.Address.Clear();

            foreach (var addrDto in updateDto.Address)
            {
                client.Address.Add(new Address
                {
                    AddressType = addrDto.AddressType ?? Domain.Enums.EAddressType.Primary,
                    Street = addrDto.Street ?? string.Empty,
                    City = addrDto.City ?? string.Empty,
                    PostalCode = addrDto.PostalCode ?? string.Empty,
                    Country = addrDto.Country ?? string.Empty,
                    AddressLine2 = addrDto.AddressLine2,
                    IsPrimary = addrDto.IsPrimary ?? false
                });
            }
        }

        // Update contacts if provided — replaces all existing contacts
        if (updateDto.Contact != null)
        {
            await _context.Entry(client).Collection(c => c.Contact).LoadAsync(cancellationToken);
            client.Contact.Clear();

            foreach (var contactDto in updateDto.Contact)
            {
                client.Contact.Add(new Contact
                {
                    ContactType = contactDto.ContactType ?? Domain.Enums.EContactType.Email,
                    ContactValue = contactDto.ContactValue ?? string.Empty,
                    Label = contactDto.Label,
                    IsPrimary = contactDto.IsPrimary ?? false
                });
            }
        }

        // Update bank accounts if provided — replaces all existing bank accounts (same pattern as Address)
        if (updateDto.BankAccount != null)
        {
            await _context.Entry(client).Collection(c => c.BankAccount).LoadAsync(cancellationToken);
            client.BankAccount.Clear();

            var hasExplicitDefault = updateDto.BankAccount.Any(b => b.IsDefault == true);
            foreach (var (bankDto, index) in updateDto.BankAccount.Select((b, i) => (b, i)))
            {
                ValidateBankAccountOrThrow(bankDto.AccountNumber, bankDto.IBAN);

                client.BankAccount.Add(new BankAccount
                {
                    ClientId = clientId,
                    Label = bankDto.Label,
                    BankName = bankDto.BankName,
                    AccountNumber = bankDto.AccountNumber ?? string.Empty,
                    IBAN = bankDto.IBAN,
                    SWIFT = bankDto.SWIFT,
                    CurrencyCode = bankDto.CurrencyCode,
                    // Auto-default: first account is default if no explicit default is set
                    IsDefault = hasExplicitDefault ? (bankDto.IsDefault ?? false) : (index == 0)
                });
            }
        }

        // Update billing settings if provided
        if (updateDto.BillingSettings != null)
        {
            // Load existing billing settings
            await _context.Entry(client).Reference(c => c.BillingSettings).LoadAsync(cancellationToken);

            if (client.BillingSettings == null)
            {
                // Create new billing settings if none exist
                client.BillingSettings = new BillingSettings();
            }

            var bs = updateDto.BillingSettings;
            if (bs.DueDateCalculationType.HasValue)
                client.BillingSettings.DueDateCalculationType = bs.DueDateCalculationType.Value;
            if (bs.DueDays.HasValue)
                client.BillingSettings.DueDays = bs.DueDays.Value;
            if (bs.InvoiceNumberPrefix != null)
                client.BillingSettings.InvoiceNumberPrefix = bs.InvoiceNumberPrefix;
            if (bs.InvoiceNumberSuffix != null)
                client.BillingSettings.InvoiceNumberSuffix = bs.InvoiceNumberSuffix;
            if (bs.CreditNoteNumberPrefix != null)
                client.BillingSettings.CreditNoteNumberPrefix = bs.CreditNoteNumberPrefix;
            if (bs.CreditNoteNumberSuffix != null)
                client.BillingSettings.CreditNoteNumberSuffix = bs.CreditNoteNumberSuffix;
            if (bs.DefaultPaymentMethod != null)
                client.BillingSettings.DefaultPaymentMethod = bs.DefaultPaymentMethod;
            if (bs.BankAccountNumber != null)
                client.BillingSettings.BankAccountNumber = bs.BankAccountNumber;
            if (bs.Notes != null)
                client.BillingSettings.Notes = bs.Notes;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated client: {ClientId}", clientId);

        return await GetClientByIdAsync(clientId, cancellationToken);
    }

    /// <summary>
    /// Soft deletes a client
    /// </summary>
    public async Task<bool> DeleteClientAsync(
        long clientId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting client: {ClientId}", clientId);

        var client = await _context.Client
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client == null)
        {
            _logger.LogWarning("Client not found: {ClientId}", clientId);
            return false;
        }

        // Check if client has invoices
        var hasInvoices = await _context.Invoice
            .AnyAsync(i => i.ClientId == clientId || i.IssuerId == clientId, cancellationToken);

        if (hasInvoices)
        {
            _logger.LogWarning("Cannot delete client {ClientId} - has invoices", clientId);
            throw new InvalidOperationException("Cannot delete client with existing invoices");
        }

        // Soft delete
        client.IsActive = false;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Deleted (soft) client: {ClientId}", clientId);

        return true;
    }

    /// <summary>
    /// Fetches client data from ARES
    /// </summary>
    public async Task<ClientDto> FetchFromAresAsync(
        string registrationNumber,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching from ARES: {RegistrationNumber}", registrationNumber);

        // Use RefreshCompanyInfoAsync (bypasses cache) because the user explicitly
        // requested a fresh fetch — cached failures should not block manual retries.
        var aresInfo = await _aresService.RefreshCompanyInfoAsync(registrationNumber, cancellationToken);

        if (!aresInfo.IsSuccessful)
        {
            throw new InvalidOperationException($"Failed to fetch from ARES: {aresInfo.ErrorMessage}");
        }

        // Map ARES data to ClientDto
        var clientDto = new ClientDto
        {
            RegistrationNumber = aresInfo.RegistrationNumber,
            CompanyName = aresInfo.CompanyName,
            TaxNumber = aresInfo.TaxNumber,
            IsVatPayer = aresInfo.IsVatPayer,
            IsActive = true,
            IsIssuer = false,
            LastAresFetchDate = aresInfo.FetchedAt,
            CreatedAt = DateTime.UtcNow
        };

        // Add address if available
        if (aresInfo.Address != null)
        {
            clientDto.Address.Add(new AddressDto
            {
                AddressType = Domain.Enums.EAddressType.Primary,
                Street = aresInfo.Address.Street,
                City = aresInfo.Address.City,
                PostalCode = aresInfo.Address.PostalCode,
                Country = aresInfo.Address.Country,
                IsPrimary = true
            });
        }

        return clientDto;
    }

    /// <summary>
    /// Adds address to client
    /// </summary>
    public async Task<ClientDto?> AddAddressAsync(
        long clientId,
        CreateAddressDto addressDto,
        CancellationToken cancellationToken = default)
    {
        var client = await _context.Client
            .Include(c => c.Address)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client == null)
            return null;

        // ZMapper maps all value properties; ClientId is set by EF Core
        // from the parent navigation (client.Address.Add sets it automatically)
        client.Address.Add(addressDto.ToAddress());

        await _context.SaveChangesAsync(cancellationToken);

        return await GetClientByIdAsync(clientId, cancellationToken);
    }

    /// <summary>
    /// Adds contact to client
    /// </summary>
    public async Task<ClientDto?> AddContactAsync(
        long clientId,
        CreateContactDto contactDto,
        CancellationToken cancellationToken = default)
    {
        var client = await _context.Client
            .Include(c => c.Contact)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client == null)
            return null;

        // ZMapper maps all value properties; ClientId is set by EF Core
        // from the parent navigation (client.Contact.Add sets it automatically)
        client.Contact.Add(contactDto.ToContact());

        await _context.SaveChangesAsync(cancellationToken);

        return await GetClientByIdAsync(clientId, cancellationToken);
    }

    /// <summary>
    /// Rejects a bank account that could never be paid to.
    ///
    /// WHY the write path validates at all (issue #154):
    /// an account number with a typo used to be stored happily and only turned into a problem
    /// much later — as an unscannable QR code on an invoice the customer already received.
    /// The moment the user types the value is the only moment they still have the correct
    /// number in front of them, so that is where the failure belongs.
    ///
    /// What is and is not rejected is decided by BankAccountValidator (see its class comment):
    /// a Czech-shaped account number must pass the modulo 11 checksum, an IBAN must pass its
    /// own checksum, and a free-form foreign account number is accepted as-is because there
    /// is no checksum to verify it against.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The account number or the IBAN is invalid. The message names the offending value and
    /// the reason; ClientController turns it into an HTTP 400 the UI shows to the user.
    /// </exception>
    private static void ValidateBankAccountOrThrow(string? accountNumber, string? iban)
    {
        if (!BankAccountValidator.TryValidateAccountNumber(accountNumber, out var accountError))
        {
            throw new ArgumentException(accountError, nameof(accountNumber));
        }

        // The IBAN is optional — only a filled-in value has to be correct.
        if (!string.IsNullOrWhiteSpace(iban) &&
            !BankAccountValidator.TryValidateIban(iban, out var ibanError))
        {
            throw new ArgumentException(ibanError, nameof(iban));
        }
    }

    /// <summary>
    /// Adds a bank account to an existing client.
    /// If this is the first bank account, it is automatically set as default.
    /// If IsDefault is true, clears default from all other accounts (only one default allowed).
    /// </summary>
    public async Task<ClientDto?> AddBankAccountAsync(
        long clientId,
        CreateBankAccountDto bankAccountDto,
        CancellationToken cancellationToken = default)
    {
        var client = await _context.Client
            .Include(c => c.BankAccount)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client == null)
            return null;

        ValidateBankAccountOrThrow(bankAccountDto.AccountNumber, bankAccountDto.IBAN);

        // If this is the first account or explicitly marked as default, manage the default flag
        var isFirstAccount = client.BankAccount.Count == 0;
        var shouldBeDefault = isFirstAccount || bankAccountDto.IsDefault;

        // Clear existing default if the new account becomes the default
        if (shouldBeDefault)
        {
            foreach (var existing in client.BankAccount)
            {
                existing.IsDefault = false;
            }
        }

        // ZMapper maps all value properties; override IsDefault with the computed value.
        // ClientId is set by EF Core from the parent navigation.
        var newAccount = bankAccountDto.ToBankAccount();
        newAccount.IsDefault = shouldBeDefault;
        client.BankAccount.Add(newAccount);

        await _context.SaveChangesAsync(cancellationToken);

        return await GetClientByIdAsync(clientId, cancellationToken);
    }

    /// <summary>
    /// Updates billing settings for client
    /// </summary>
    public async Task<ClientDto?> UpdateBillingSettingsAsync(
        long clientId,
        CreateBillingSettingsDto settingsDto,
        CancellationToken cancellationToken = default)
    {
        var client = await _context.Client
            .Include(c => c.BillingSettings)
            .FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);

        if (client == null)
            return null;

        if (client.BillingSettings == null)
        {
            // Create new
            client.BillingSettings = new BillingSettings
            {
                ClientId = clientId
            };
        }

        // Update
        client.BillingSettings.DueDateCalculationType = settingsDto.DueDateCalculationType;
        client.BillingSettings.DueDays = settingsDto.DueDays;
        client.BillingSettings.CustomInvoiceNumberSequenceId = settingsDto.CustomInvoiceNumberSequenceId;
        client.BillingSettings.CustomCreditNoteNumberSequenceId = settingsDto.CustomCreditNoteNumberSequenceId;
        client.BillingSettings.InvoiceNumberPrefix = settingsDto.InvoiceNumberPrefix;
        client.BillingSettings.InvoiceNumberSuffix = settingsDto.InvoiceNumberSuffix;
        client.BillingSettings.CreditNoteNumberPrefix = settingsDto.CreditNoteNumberPrefix;
        client.BillingSettings.CreditNoteNumberSuffix = settingsDto.CreditNoteNumberSuffix;
        client.BillingSettings.DefaultPaymentMethod = settingsDto.DefaultPaymentMethod;
        client.BillingSettings.BankAccountNumber = settingsDto.BankAccountNumber;
        client.BillingSettings.Notes = settingsDto.Notes;

        await _context.SaveChangesAsync(cancellationToken);

        return await GetClientByIdAsync(clientId, cancellationToken);
    }

}
