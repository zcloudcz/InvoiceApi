using AresService;
using Fakvio.Application.Common.Extensions;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
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

    /// <summary>Language a client gets when none was supplied — matches the DB column default.</summary>
    private const string DefaultLanguage = "cs";

    /// <summary>
    /// The document language codes the application can actually render. Anything outside this
    /// list silently mismatches: <c>PdfExportService</c> only branches on "cs" and prints English
    /// labels for everything else, while <c>ContentTemplateService</c> falls back to the
    /// any-language default template — so e.g. "de" yields English headings around a Czech body.
    /// </summary>
    private static readonly string[] SupportedLanguages = [DefaultLanguage, "en"];

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
    /// Normalizes a document language code coming from a DTO: trims it, lowercases it and accepts
    /// only a <see cref="SupportedLanguages"/> value. Anything else returns null, which every
    /// caller reads as "nothing usable was supplied" — the same shape as the
    /// <c>Enum.TryParse(...) ? value : null</c> sanitization used for the tax-regime fields.
    ///
    /// This is the single guard for the whole write path, on purpose. <c>[StringLength(5)]</c> on
    /// the DTOs lets "", "de" and "EN" through; the Azure Functions host deserializes the DTO
    /// itself and never runs model validation at all; and the MCP and chat tools call this
    /// service directly. One check here covers all of them.
    /// </summary>
    private string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var normalized = language.Trim().ToLowerInvariant();
        if (SupportedLanguages.Contains(normalized))
            return normalized;

        // Logged rather than thrown: the surrounding fields use the same "unusable value is
        // dropped" convention, and an unsupported code must not fail an otherwise valid save.
        _logger.LogWarning(
            "Ignoring unsupported client language '{Language}' — supported: {Supported}",
            language, string.Join(", ", SupportedLanguages));

        return null;
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
    /// Bulk existence check by registration number — see interface docs for why this exists
    /// (avoids N queries × full client graph when a caller just needs "does it exist").
    /// </summary>
    public async Task<Dictionary<string, long>> GetClientIdsByRegistrationNumbersAsync(
        IEnumerable<string> registrationNumbers,
        CancellationToken cancellationToken = default)
    {
        var distinct = registrationNumbers
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct()
            .ToList();

        if (distinct.Count == 0)
        {
            return new Dictionary<string, long>();
        }

        // AsNoTracking + no Include: this is an existence/id lookup, not a full read.
        return await _context.Client
            .AsNoTracking()
            .Where(c => distinct.Contains(c.RegistrationNumber!))
            .Select(c => new { c.RegistrationNumber, c.Id })
            .ToDictionaryAsync(c => c.RegistrationNumber!, c => c.Id, cancellationToken);
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
            Color = createDto.Color,
            Language = NormalizeLanguage(createDto.Language) ?? DefaultLanguage,
            PeppolId = string.IsNullOrWhiteSpace(createDto.PeppolId) ? null : createDto.PeppolId
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
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A failed INSERT leaves the new client (and its addresses, contacts, …) tracked as
            // "Added" on this DbContext. Callers that create several clients on the same scoped
            // context (CSV import, invoice import) would then re-send that broken INSERT with
            // EVERY following SaveChanges, so one bad row would fail all rows after it.
            // Nothing from this SaveChanges reached the DB, so detaching every Added entry is safe.
            foreach (var entry in _context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;

            // 23505 = unique violation. The only unique index a new client can hit is
            // RegistrationNumber: another request inserted the same IČO between our
            // "already exists" check above and this INSERT. Report it exactly like that check
            // does, so callers (409 in ClientController, "Skipped" in CSV import) handle both alike.
            if (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                throw new InvalidOperationException($"Client with registration number {createDto.RegistrationNumber} already exists", ex);

            throw;
        }

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

        // Update Peppol ID override — empty string clears it, falling back to the automatic
        // derivation from TaxNumber + country (same "" = clear convention as Color above).
        if (updateDto.PeppolId != null)
            client.PeppolId = string.IsNullOrEmpty(updateDto.PeppolId) ? null : updateDto.PeppolId;

        // Update preferred document language — null OR an unsupported code means "don't change",
        // so a bad value never replaces a working one (see NormalizeLanguage).
        var language = NormalizeLanguage(updateDto.Language);
        if (language != null)
            client.Language = language;

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
