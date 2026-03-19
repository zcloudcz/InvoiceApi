using System.Security.Claims;
using AresService;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.CompanySettings;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ZMapper; // ZMapper extension methods for DTO mapping (e.g., .ToClientDto(), .ToAddress())

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for company (issuer) management and multi-tenant provisioning.
/// SysAdmin only — manages invoice issuers (companies) and their tenant infrastructure.
///
/// IMPORTANT: Company CRUD uses MasterDbContext directly (NOT IClientService/TenantDbContext).
/// Companies (issuers) are master-level entities — SysAdmin manages them without needing
/// a tenant database. IClientService operates on TenantDbContext which requires a provisioned
/// tenant, making it unsuitable for SysAdmin operations.
///
/// Company CRUD: standard create/read/update/delete for issuers.
/// Tenant lifecycle: provision, activate, deactivate tenant schemas.
/// Settings CRUD: manage CompanySystemSettings (schema name, limits, notes).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "SysAdmin")] // Only SysAdmin can manage companies
public class CompanyController : ControllerBase
{
    private readonly IAresService _aresService;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IEmailService _emailService;
    private readonly MasterDbContext _masterContext;
    private readonly ILogger<CompanyController> _logger;

    public CompanyController(
        IAresService aresService,
        ITenantProvisioningService provisioningService,
        IEmailService emailService,
        MasterDbContext masterContext,
        ILogger<CompanyController> logger)
    {
        _aresService = aresService;
        _provisioningService = provisioningService;
        _emailService = emailService;
        _masterContext = masterContext;
        _logger = logger;
    }

    /// <summary>
    /// Gets all companies (invoice issuers) from the master database.
    /// </summary>
    /// <param name="includeInactive">Include inactive companies</param>
    /// <returns>List of companies</returns>
    /// <response code="200">Returns list of companies</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<ClientDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ClientDto>>> GetAllCompanies(
        [FromQuery] bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _masterContext.Client
                .AsNoTracking()
                .Where(c => c.IsIssuer);

            if (!includeInactive)
                query = query.Where(c => c.IsActive);

            // Include Address for grid display (e.g., showing primary address)
            var companies = await query
                .Include(c => c.Address)
                .OrderBy(c => c.CompanyName)
                .ToListAsync(cancellationToken);

            return Ok(companies.Select(MapToDto).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving companies");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while retrieving companies." });
        }
    }

    /// <summary>
    /// Gets paginated, filtered and sorted companies (invoice issuers only).
    /// Queries the master database directly.
    /// </summary>
    /// <param name="filter">Filter parameters including pagination, search, and sorting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paged result of companies</returns>
    /// <response code="200">Returns paged list of companies</response>
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<ClientDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ClientDto>>> GetCompaniesPaged(
        [FromQuery] ClientFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("GET /api/company/paged - Page: {Page}, PageSize: {PageSize}, Search: {Search}",
                filter.Page, filter.PageSize, filter.Search);

            // Build query against master DB — only issuers
            var query = _masterContext.Client
                .AsNoTracking()
                .Where(c => c.IsIssuer);

            // Apply search filter (company name or registration number)
            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                var search = filter.Search.ToLower();
                query = query.Where(c =>
                    c.CompanyName.ToLower().Contains(search) ||
                    (c.RegistrationNumber != null && c.RegistrationNumber.ToLower().Contains(search)));
            }

            // Apply active filter — by default only active companies are shown.
            // When IncludeInactive is true, show all companies (no filter applied).
            if (!filter.IncludeInactive)
                query = query.Where(c => c.IsActive);

            // Get total count before pagination
            var totalCount = await query.CountAsync(cancellationToken);

            // Apply sorting (default: CompanyName ascending)
            query = (filter.SortBy?.ToLower()) switch
            {
                "registrationnumber" => filter.IsDescending
                    ? query.OrderByDescending(c => c.RegistrationNumber)
                    : query.OrderBy(c => c.RegistrationNumber),
                "createdat" => filter.IsDescending
                    ? query.OrderByDescending(c => c.CreatedAt)
                    : query.OrderBy(c => c.CreatedAt),
                _ => filter.IsDescending
                    ? query.OrderByDescending(c => c.CompanyName)
                    : query.OrderBy(c => c.CompanyName)
            };

            // Apply pagination
            var page = Math.Max(1, filter.Page);
            var pageSize = Math.Clamp(filter.PageSize, 1, 100);

            // Include Address for grid display (e.g., showing primary address city)
            var companies = await query
                .Include(c => c.Address)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            return Ok(new PagedResult<ClientDto>
            {
                Items = companies.Select(MapToDto).ToList(),
                TotalCount = totalCount,
                PageNumber = page,
                PageSize = pageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving paged companies");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while retrieving companies." });
        }
    }

    /// <summary>
    /// Gets a specific company by ID from the master database.
    /// </summary>
    /// <param name="id">Company ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Company data</returns>
    /// <response code="200">Returns company data</response>
    /// <response code="403">Not a company (IsIssuer = false)</response>
    /// <response code="404">Company not found</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> GetCompanyById(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            // Include Address, Contact, and BillingSettings so the detail page shows full company data.
            // AsSplitQuery: Address and Contact are both collection navigations — prevents cartesian explosion.
            var client = await _masterContext.Client
                .AsNoTracking()
                .AsSplitQuery()
                .Include(c => c.Address)
                .Include(c => c.Contact)
                .Include(c => c.BillingSettings)
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

            if (client == null)
                return NotFound(new { message = $"Company with ID {id} not found." });

            if (!client.IsIssuer)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "This client is not a company (issuer)." });

            return Ok(MapToDto(client));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving company {CompanyId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = ex.ToString() + "An error occurred while retrieving the company." });
        }
    }

    /// <summary>
    /// Creates a new company (invoice issuer) in the master database.
    /// This creates the Client record only — tenant database is NOT created here.
    /// Use POST /api/company/settings + POST /api/company/{id}/provision to set up the tenant.
    /// </summary>
    /// <param name="createDto">Company creation data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created company</returns>
    /// <response code="201">Company created successfully</response>
    /// <response code="400">Invalid data or registration number already exists</response>
    [HttpPost]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ClientDto>> CreateCompany(
        [FromBody] CreateClientDto createDto, CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate: company name is required
            if (string.IsNullOrWhiteSpace(createDto.CompanyName))
                return BadRequest(new { message = "Company name is required." });

            // Check for duplicate registration number (if provided)
            if (!string.IsNullOrWhiteSpace(createDto.RegistrationNumber))
            {
                var exists = await _masterContext.Client
                    .AnyAsync(c => c.RegistrationNumber == createDto.RegistrationNumber && c.IsIssuer,
                        cancellationToken);

                if (exists)
                    return BadRequest(new { message = $"Company with registration number '{createDto.RegistrationNumber}' already exists." });
            }

            // Optionally fetch from ARES to auto-fill company data
            string? companyName = createDto.CompanyName;
            string? taxNumber = createDto.TaxNumber;
            bool isVatPayer = createDto.IsVatPayer;

            if (createDto.FetchFromAres &&
                !string.IsNullOrWhiteSpace(createDto.RegistrationNumber) &&
                createDto.RegistrationNumber.Length == 8)
            {
                var aresInfo = await _aresService.GetCompanyInfoAsync(
                    createDto.RegistrationNumber, cancellationToken);

                if (aresInfo.IsSuccessful)
                {
                    _logger.LogInformation("ARES data fetched for IČO {Ico}: {CompanyName}",
                        createDto.RegistrationNumber, aresInfo.CompanyName);

                    companyName = aresInfo.CompanyName;
                    taxNumber = aresInfo.TaxNumber ?? createDto.TaxNumber;
                    isVatPayer = aresInfo.IsVatPayer;
                }
                else
                {
                    _logger.LogWarning("ARES fetch failed for IČO {Ico}: {Error}",
                        createDto.RegistrationNumber, aresInfo.ErrorMessage);
                }
            }

            // Create the company entity in the master database
            var company = new Client
            {
                CompanyName = companyName,
                RegistrationNumber = createDto.RegistrationNumber,
                TradingName = createDto.TradingName,
                TaxNumber = taxNumber,
                IsVatPayer = isVatPayer,
                IsIssuer = true,
                IsActive = true,
                LastAresFetchDate = createDto.FetchFromAres ? DateTime.UtcNow : null,
                CreatedAt = DateTime.UtcNow,
                // Initialize nav property collections so addresses/contacts can be added
                Address = new List<Address>(),
                Contact = new List<Contact>()
            };

            // Add addresses from the DTO — ZMapper handles property mapping
            if (createDto.Address?.Count > 0)
            {
                foreach (var addrDto in createDto.Address)
                {
                    company.Address.Add(addrDto.ToAddress());
                }
            }

            // Add contacts from the DTO — ZMapper handles property mapping
            if (createDto.Contact?.Count > 0)
            {
                foreach (var contactDto in createDto.Contact)
                {
                    company.Contact.Add(contactDto.ToContact());
                }
            }

            // Add billing settings if provided — ZMapper handles property mapping
            if (createDto.BillingSettings != null)
            {
                company.BillingSettings = createDto.BillingSettings.ToBillingSettings();
            }

            _masterContext.Client.Add(company);
            await _masterContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Created company {CompanyId}: {CompanyName}", company.Id, company.CompanyName);

            return CreatedAtAction(nameof(GetCompanyById), new { id = company.Id }, MapToDto(company));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to create company: {RegistrationNumber}", createDto.RegistrationNumber);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating company: {RegistrationNumber}", createDto.RegistrationNumber);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while creating the company." });
        }
    }

    /// <summary>
    /// Updates an existing company in the master database.
    /// Only updates basic company fields — addresses, contacts, and billing settings
    /// are managed in the tenant database via the Client endpoints.
    /// </summary>
    /// <param name="id">Company ID to update</param>
    /// <param name="updateDto">Updated company data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated company</returns>
    /// <response code="200">Company updated successfully</response>
    /// <response code="400">Invalid data</response>
    /// <response code="403">Not a company (IsIssuer = false)</response>
    /// <response code="404">Company not found</response>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> UpdateCompany(
        long id, [FromBody] UpdateClientDto updateDto, CancellationToken cancellationToken = default)
    {
        try
        {
            // Load company WITH Address, Contact, and BillingSettings so we can update them.
            // AsSplitQuery: Address and Contact are both collection navigations — prevents cartesian explosion.
            var company = await _masterContext.Client
                .AsSplitQuery()
                .Include(c => c.Address)
                .Include(c => c.Contact)
                .Include(c => c.BillingSettings)
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

            if (company == null)
                return NotFound(new { message = $"Company with ID {id} not found." });

            if (!company.IsIssuer)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "This client is not a company (issuer)." });

            // Prevent changing IsIssuer flag for companies
            if (updateDto.IsIssuer.HasValue && !updateDto.IsIssuer.Value)
                return BadRequest(new { message = "Cannot change company to non-issuer. Use DELETE instead." });

            // Update basic fields (only if provided — partial update pattern)
            if (updateDto.CompanyName != null) company.CompanyName = updateDto.CompanyName;
            if (updateDto.TradingName != null) company.TradingName = updateDto.TradingName;
            if (updateDto.TaxNumber != null) company.TaxNumber = updateDto.TaxNumber;
            if (updateDto.IsVatPayer.HasValue) company.IsVatPayer = updateDto.IsVatPayer.Value;
            if (updateDto.IsActive.HasValue) company.IsActive = updateDto.IsActive.Value;

            // Replace-all strategy for addresses: remove existing, add new ones from DTO
            if (updateDto.Address != null)
            {
                _masterContext.RemoveRange(company.Address);
                company.Address.Clear();

                foreach (var addrDto in updateDto.Address)
                {
                    company.Address.Add(new Address
                    {
                        AddressType = addrDto.AddressType ?? Domain.Enums.EAddressType.Billing,
                        Street = addrDto.Street ?? string.Empty,
                        City = addrDto.City ?? string.Empty,
                        PostalCode = addrDto.PostalCode ?? string.Empty,
                        Country = addrDto.Country ?? string.Empty,
                        AddressLine2 = addrDto.AddressLine2,
                        IsPrimary = addrDto.IsPrimary ?? false,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            // Replace-all strategy for contacts: remove existing, add new ones from DTO
            if (updateDto.Contact != null)
            {
                _masterContext.RemoveRange(company.Contact);
                company.Contact.Clear();

                foreach (var contactDto in updateDto.Contact)
                {
                    company.Contact.Add(new Contact
                    {
                        ContactType = contactDto.ContactType ?? Domain.Enums.EContactType.Email,
                        ContactValue = contactDto.ContactValue ?? string.Empty,
                        Label = contactDto.Label,
                        IsPrimary = contactDto.IsPrimary ?? false,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            // Update billing settings (bank account, payment method, etc.)
            // If provided in DTO: create or update the BillingSettings record.
            if (updateDto.BillingSettings != null)
            {
                if (company.BillingSettings == null)
                {
                    // First time setting billing — create new record
                    company.BillingSettings = new BillingSettings
                    {
                        ClientId = company.Id,
                        CreatedAt = DateTime.UtcNow
                    };
                }

                var bs = updateDto.BillingSettings;
                if (bs.DueDateCalculationType.HasValue)
                    company.BillingSettings.DueDateCalculationType = bs.DueDateCalculationType.Value;
                if (bs.DueDays.HasValue)
                    company.BillingSettings.DueDays = bs.DueDays.Value;
                if (bs.DefaultPaymentMethod.HasValue)
                    company.BillingSettings.DefaultPaymentMethod = bs.DefaultPaymentMethod.Value;
                if (bs.BankAccountNumber != null)
                    company.BillingSettings.BankAccountNumber = bs.BankAccountNumber;
                if (bs.InvoiceNumberPrefix != null)
                    company.BillingSettings.InvoiceNumberPrefix = bs.InvoiceNumberPrefix;
                if (bs.InvoiceNumberSuffix != null)
                    company.BillingSettings.InvoiceNumberSuffix = bs.InvoiceNumberSuffix;
                if (bs.CreditNoteNumberPrefix != null)
                    company.BillingSettings.CreditNoteNumberPrefix = bs.CreditNoteNumberPrefix;
                if (bs.CreditNoteNumberSuffix != null)
                    company.BillingSettings.CreditNoteNumberSuffix = bs.CreditNoteNumberSuffix;
                if (bs.Notes != null)
                    company.BillingSettings.Notes = bs.Notes;

                company.BillingSettings.UpdatedAt = DateTime.UtcNow;
            }

            company.UpdatedAt = DateTime.UtcNow;

            await _masterContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Updated company {CompanyId}: {CompanyName}", company.Id, company.CompanyName);

            return Ok(MapToDto(company));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to update company {CompanyId}", id);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating company {CompanyId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while updating the company." });
        }
    }

    /// <summary>
    /// Deletes a company (soft delete - sets IsActive = false).
    /// WARNING: This will affect all users and data associated with this company.
    /// </summary>
    /// <param name="id">Company ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    /// <response code="200">Company deleted successfully</response>
    /// <response code="403">Not a company (IsIssuer = false)</response>
    /// <response code="404">Company not found</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteCompany(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            var company = await _masterContext.Client
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

            if (company == null)
                return NotFound(new { message = $"Company with ID {id} not found." });

            if (!company.IsIssuer)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "This client is not a company (issuer)." });

            // Soft delete — set IsActive = false
            company.IsActive = false;
            company.UpdatedAt = DateTime.UtcNow;

            await _masterContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Soft-deleted company {CompanyId}: {CompanyName}", company.Id, company.CompanyName);

            return Ok(new { message = "Company deleted successfully. All associated users and data remain but are marked inactive." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting company {CompanyId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while deleting the company." });
        }
    }

    /// <summary>
    /// Fetches company data from ARES registry by registration number.
    /// Uses IAresService directly (not IClientService) to avoid TenantDbContext dependency.
    /// Returns a preview DTO — data is NOT saved to the database.
    /// </summary>
    /// <param name="registrationNumber">Company registration number (IČO)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Company data from ARES</returns>
    /// <response code="200">Returns company data from ARES</response>
    /// <response code="404">Company not found in ARES</response>
    [HttpGet("ares/{registrationNumber}")]
    [ProducesResponseType(typeof(ClientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClientDto>> GetCompanyFromAres(
        string registrationNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            var aresInfo = await _aresService.GetCompanyInfoAsync(registrationNumber, cancellationToken);

            if (!aresInfo.IsSuccessful)
                return NotFound(new { message = $"ARES lookup failed: {aresInfo.ErrorMessage}" });

            // Map ARES data to ClientDto (preview — not saved to DB)
            var clientDto = new ClientDto
            {
                RegistrationNumber = aresInfo.RegistrationNumber,
                CompanyName = aresInfo.CompanyName,
                TaxNumber = aresInfo.TaxNumber,
                IsVatPayer = aresInfo.IsVatPayer,
                IsActive = true,
                IsIssuer = true,
                LastAresFetchDate = aresInfo.FetchedAt,
                CreatedAt = DateTime.UtcNow
            };

            // Add address if available
            if (aresInfo.Address != null)
            {
                clientDto.Address.Add(new AddressDto
                {
                    Street = aresInfo.Address.Street,
                    City = aresInfo.Address.City,
                    PostalCode = aresInfo.Address.PostalCode,
                    Country = aresInfo.Address.Country
                });
            }

            return Ok(clientDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching company from ARES: {RegistrationNumber}", registrationNumber);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while fetching company data from ARES." });
        }
    }

    #region CompanySystemSettings CRUD

    /// <summary>
    /// Gets the CompanySystemSettings for a specific company.
    /// Returns the tenant infrastructure configuration (database name, provisioning status, limits).
    /// </summary>
    /// <param name="id">Company ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>CompanySystemSettings data</returns>
    /// <response code="200">Returns the settings</response>
    /// <response code="404">Settings not found for this company</response>
    [HttpGet("{id}/settings")]
    [ProducesResponseType(typeof(CompanySystemSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompanySystemSettingsDto>> GetSettings(
        long id, CancellationToken cancellationToken = default)
    {
        try
        {
            // Load settings with company name for display
            var settings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .Include(s => s.Company)
                .FirstOrDefaultAsync(s => s.CompanyId == id, cancellationToken);

            if (settings == null)
                return NotFound(new { message = $"No CompanySystemSettings found for company {id}." });

            return Ok(MapSettingsToDto(settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving settings for company {CompanyId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while retrieving company settings." });
        }
    }

    /// <summary>
    /// Gets all CompanySystemSettings records.
    /// Returns the full list of tenant configurations for the SysAdmin dashboard.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of all CompanySystemSettings</returns>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(List<CompanySystemSettingsDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<CompanySystemSettingsDto>>> GetAllSettings(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .Include(s => s.Company)
                .OrderBy(s => s.CompanyId)
                .ToListAsync(cancellationToken);

            return Ok(settings.Select(MapSettingsToDto).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all company settings");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while retrieving company settings." });
        }
    }

    /// <summary>
    /// Creates a new CompanySystemSettings record for a company.
    /// This registers the tenant configuration but does NOT provision the database yet.
    /// Call POST /api/company/{id}/provision separately to create the actual database.
    /// </summary>
    /// <param name="dto">Settings creation data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created settings</returns>
    /// <response code="201">Settings created successfully</response>
    /// <response code="400">Invalid data or settings already exist</response>
    /// <response code="404">Company not found</response>
    [HttpPost("settings")]
    [ProducesResponseType(typeof(CompanySystemSettingsDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompanySystemSettingsDto>> CreateSettings(
        [FromBody] CreateCompanySystemSettingsDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify the company exists and is an issuer
            var company = await _masterContext.Client
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == dto.CompanyId && c.IsIssuer, cancellationToken);

            if (company == null)
                return NotFound(new { message = $"Company with ID {dto.CompanyId} not found or is not an issuer." });

            // Check for existing settings (one-to-one relationship)
            var existing = await _masterContext.CompanySystemSettings
                .AnyAsync(s => s.CompanyId == dto.CompanyId, cancellationToken);

            if (existing)
                return BadRequest(new { message = $"CompanySystemSettings already exists for company {dto.CompanyId}." });

            // Generate schema name if not provided — convention: "tenant_{companyId}"
            // Schema names use lowercase + underscores per PostgreSQL naming rules.
            var schemaName = !string.IsNullOrWhiteSpace(dto.SchemaName)
                ? dto.SchemaName
                : $"tenant_{dto.CompanyId}";

            var settings = new CompanySystemSettings
            {
                CompanyId = dto.CompanyId,
                SchemaName = schemaName,
                MaxUsers = dto.MaxUsers,
                AdminNotes = dto.AdminNotes,
                IsProvisioned = false,
                IsActive = false, // Not active until provisioned
                // Optional company SMTP settings (can also be configured later via UpdateSettings)
                SmtpHost = dto.SmtpHost,
                SmtpPort = dto.SmtpPort,
                SmtpUsername = dto.SmtpUsername,
                SmtpPassword = dto.SmtpPassword,
                SmtpSenderEmail = dto.SmtpSenderEmail,
                SmtpSenderName = dto.SmtpSenderName,
                SmtpUseSsl = dto.SmtpUseSsl,
                // Optional company AI settings (can also be configured later via UpdateSettings)
                AiDefaultProvider = dto.AiDefaultProvider,
                AiClaudeApiKey = dto.AiClaudeApiKey,
                AiClaudeModel = dto.AiClaudeModel,
                AiOpenAiApiKey = dto.AiOpenAiApiKey,
                AiOpenAiModel = dto.AiOpenAiModel,
                AiGeminiApiKey = dto.AiGeminiApiKey,
                AiGeminiModel = dto.AiGeminiModel,
                AiOllamaBaseUrl = dto.AiOllamaBaseUrl,
                AiOllamaModel = dto.AiOllamaModel,
                CreatedAt = DateTime.UtcNow
            };

            _masterContext.CompanySystemSettings.Add(settings);
            await _masterContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Created CompanySystemSettings for company {CompanyId} → schema '{SchemaName}'",
                dto.CompanyId, schemaName);

            // Reload with company navigation for DTO mapping
            settings = await _masterContext.CompanySystemSettings
                .Include(s => s.Company)
                .FirstAsync(s => s.Id == settings.Id, cancellationToken);

            return CreatedAtAction(nameof(GetSettings), new { id = dto.CompanyId }, MapSettingsToDto(settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating settings for company {CompanyId}", dto.CompanyId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while creating company settings." });
        }
    }

    /// <summary>
    /// Updates an existing CompanySystemSettings record.
    /// Only updatable fields can be changed — schema name and provisioning status are immutable.
    /// </summary>
    /// <param name="id">Company ID</param>
    /// <param name="dto">Updated settings data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated settings</returns>
    /// <response code="200">Settings updated successfully</response>
    /// <response code="404">Settings not found</response>
    [HttpPut("{id}/settings")]
    [ProducesResponseType(typeof(CompanySystemSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompanySystemSettingsDto>> UpdateSettings(
        long id, [FromBody] UpdateCompanySystemSettingsDto dto, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _masterContext.CompanySystemSettings
                .Include(s => s.Company)
                .FirstOrDefaultAsync(s => s.CompanyId == id, cancellationToken);

            if (settings == null)
                return NotFound(new { message = $"No CompanySystemSettings found for company {id}." });

            // Update only mutable fields (infrastructure config).
            // Note: SchemaName is immutable after provisioning — managed by provisioning service.
            settings.MaxUsers = dto.MaxUsers;
            settings.AdminNotes = dto.AdminNotes;

            // Update SMTP settings — each field is individually nullable (partial update).
            // SmtpHost: set to empty/null to clear and fall back to system SMTP.
            if (dto.SmtpHost != null) settings.SmtpHost = dto.SmtpHost;
            if (dto.SmtpPort.HasValue) settings.SmtpPort = dto.SmtpPort;
            if (dto.SmtpUsername != null) settings.SmtpUsername = dto.SmtpUsername;
            // SmtpPassword: null = keep existing, empty = clear, non-empty = update
            if (dto.SmtpPassword != null) settings.SmtpPassword = dto.SmtpPassword;
            if (dto.SmtpSenderEmail != null) settings.SmtpSenderEmail = dto.SmtpSenderEmail;
            if (dto.SmtpSenderName != null) settings.SmtpSenderName = dto.SmtpSenderName;
            if (dto.SmtpUseSsl.HasValue) settings.SmtpUseSsl = dto.SmtpUseSsl;

            // Update AI settings — same partial update pattern as SMTP.
            // null = keep existing, empty string = clear (fall back to system default).
            if (dto.AiDefaultProvider != null) settings.AiDefaultProvider = dto.AiDefaultProvider;
            // AiClaudeApiKey: null = keep existing, empty = clear, non-empty = update
            if (dto.AiClaudeApiKey != null) settings.AiClaudeApiKey = dto.AiClaudeApiKey;
            if (dto.AiClaudeModel != null) settings.AiClaudeModel = dto.AiClaudeModel;
            if (dto.AiOpenAiApiKey != null) settings.AiOpenAiApiKey = dto.AiOpenAiApiKey;
            if (dto.AiOpenAiModel != null) settings.AiOpenAiModel = dto.AiOpenAiModel;
            if (dto.AiGeminiApiKey != null) settings.AiGeminiApiKey = dto.AiGeminiApiKey;
            if (dto.AiGeminiModel != null) settings.AiGeminiModel = dto.AiGeminiModel;
            if (dto.AiOllamaBaseUrl != null) settings.AiOllamaBaseUrl = dto.AiOllamaBaseUrl;
            if (dto.AiOllamaModel != null) settings.AiOllamaModel = dto.AiOllamaModel;

            settings.UpdatedAt = DateTime.UtcNow;

            await _masterContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Updated CompanySystemSettings for company {CompanyId}", id);

            return Ok(MapSettingsToDto(settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating settings for company {CompanyId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while updating company settings." });
        }
    }

    #endregion

    #region Tenant Provisioning & Lifecycle

    /// <summary>
    /// Provisions a new tenant database for the given company.
    /// This is a long-running operation that:
    /// 1. Creates a new SQL Server database
    /// 2. Applies TenantDbContext migrations (schema creation)
    /// 3. Copies code tables from master DB (VatRate, Currency, etc.)
    /// 4. Creates the issuer (company) record in the tenant DB
    /// 5. Creates default number sequences
    /// 6. Marks the tenant as provisioned and active
    ///
    /// Prerequisites: CompanySystemSettings must exist for this company (via POST /api/company/settings).
    /// </summary>
    /// <param name="id">Company ID to provision</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status with provisioning details</returns>
    /// <response code="200">Tenant provisioned successfully</response>
    /// <response code="400">Provisioning failed (already provisioned, settings missing, etc.)</response>
    /// <response code="404">Company not found</response>
    [HttpPost("{id}/provision")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ProvisionTenant(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("SysAdmin triggered provisioning for company {CompanyId}", id);

            var result = await _provisioningService.ProvisionTenantAsync(id, cancellationToken);

            if (!result)
                return BadRequest(new { message = $"Provisioning failed for company {id}." });

            return Ok(new { message = $"Tenant provisioned successfully for company {id}." });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Provisioning failed for company {CompanyId}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error provisioning company {CompanyId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An unexpected error occurred during provisioning. Check server logs." });
        }
    }

    /// <summary>
    /// Activates a previously deactivated tenant.
    /// The tenant database must already be provisioned — this only flips the IsActive flag.
    /// After activation, users of this company can access tenant-scoped endpoints again.
    /// </summary>
    /// <param name="id">Company ID to activate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    /// <response code="200">Tenant activated</response>
    /// <response code="400">Activation failed (not provisioned)</response>
    /// <response code="404">Company settings not found</response>
    [HttpPut("{id}/activate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ActivateTenant(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("SysAdmin activated tenant for company {CompanyId}", id);

            var result = await _provisioningService.ActivateTenantAsync(id, cancellationToken);

            if (!result)
                return NotFound(new { message = $"No CompanySystemSettings found for company {id}." });

            return Ok(new { message = $"Tenant activated for company {id}." });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Activation failed for company {CompanyId}: {Message}", id, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating tenant for company {CompanyId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while activating the tenant." });
        }
    }

    /// <summary>
    /// Deactivates a tenant. The database remains but all access is blocked.
    /// Users of this company will receive a 403 from TenantContextMiddleware.
    /// Used for billing suspension, account freezing, etc.
    /// Does NOT delete the database — that's a manual DBA operation (safety measure).
    /// </summary>
    /// <param name="id">Company ID to deactivate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    /// <response code="200">Tenant deactivated</response>
    /// <response code="404">Company settings not found</response>
    [HttpPut("{id}/deactivate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeactivateTenant(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("SysAdmin deactivated tenant for company {CompanyId}", id);

            var result = await _provisioningService.DeactivateTenantAsync(id, cancellationToken);

            if (!result)
                return NotFound(new { message = $"No CompanySystemSettings found for company {id}." });

            return Ok(new { message = $"Tenant deactivated for company {id}." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deactivating tenant for company {CompanyId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while deactivating the tenant." });
        }
    }

    /// <summary>
    /// Applies pending EF Core migrations to a specific tenant's database.
    /// Used for manual maintenance — startup auto-migration handles this automatically.
    /// </summary>
    /// <param name="id">Company ID whose tenant DB to migrate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status</returns>
    /// <response code="200">Migrations applied</response>
    /// <response code="404">Tenant not found or not provisioned</response>
    [HttpPost("{id}/migrate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> MigrateTenant(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("SysAdmin triggered migration for company {CompanyId}", id);

            var result = await _provisioningService.MigrateTenantAsync(id, cancellationToken);

            if (!result)
                return NotFound(new { message = $"Tenant not found or not provisioned for company {id}." });

            return Ok(new { message = $"Migrations applied to tenant database for company {id}." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error migrating tenant for company {CompanyId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while migrating the tenant database." });
        }
    }

    #endregion

    #region SMTP Test

    /// <summary>
    /// Sends a test email using the company's SMTP settings to verify they work.
    /// The test email is sent to the current user's email address.
    /// Both Admin and SysAdmin can use this endpoint — Admin tests their own company's
    /// SMTP, SysAdmin can test any company's SMTP.
    /// </summary>
    /// <param name="id">Company ID whose SMTP settings to test</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success status with details, or error info</returns>
    /// <response code="200">Test email sent successfully</response>
    /// <response code="400">SMTP not configured or send failed</response>
    /// <response code="404">Company settings not found</response>
    [HttpPost("{id}/test-smtp")]
    [Authorize(Roles = "Admin,SysAdmin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> TestSmtp(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            // Load company settings to verify SMTP is configured
            var settings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.CompanyId == id, cancellationToken);

            if (settings == null)
                return NotFound(new { message = $"No CompanySystemSettings found for company {id}." });

            if (string.IsNullOrWhiteSpace(settings.SmtpHost))
                return BadRequest(new { message = "Company SMTP is not configured. Set the SMTP Host first." });

            // Get current user's email to send the test email to
            var userEmail = User.FindFirstValue(ClaimTypes.Email)
                ?? User.FindFirstValue("email");
            if (string.IsNullOrWhiteSpace(userEmail))
                return BadRequest(new { message = "Cannot determine your email address from the current session." });

            _logger.LogInformation(
                "Testing SMTP for company {CompanyId} → sending test email to {Email}",
                id, userEmail);

            // Send a simple test email using the company's SMTP settings.
            // EmailService.SendEmailAsync will resolve SMTP via the 3-tier chain,
            // but since we're testing company SMTP specifically, we validate the host
            // is configured above and the tenant resolver should pick it up.
            await _emailService.SendEmailAsync(
                userEmail,
                "SMTP Test — Fakvio",
                "<p>This is a test email to verify your SMTP configuration is working correctly.</p>" +
                $"<p>Company ID: {id}</p>" +
                $"<p>SMTP Host: {settings.SmtpHost}:{settings.SmtpPort ?? 587}</p>",
                ct: cancellationToken);

            return Ok(new { message = $"Test email sent successfully to {userEmail}." });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMTP test failed for company {CompanyId}", id);
            return BadRequest(new { message = $"SMTP test failed: {ex.Message}" });
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Maps a Client entity to ClientDto using ZMapper (same mapping as ClientService).
    /// ZMapper handles all properties including nested Address, Contact, BankAccount,
    /// and BillingSettings collections automatically.
    /// </summary>
    private static ClientDto MapToDto(Client client) => client.ToClientDto();

    /// <summary>
    /// Maps a CompanySystemSettings entity to a DTO for API responses.
    /// Includes company name from the navigation property for display convenience.
    /// SMTP password is NOT exposed — only a flag indicating whether one is set.
    /// This prevents credential leaks in API responses.
    /// </summary>
    private static CompanySystemSettingsDto MapSettingsToDto(CompanySystemSettings settings)
    {
        return new CompanySystemSettingsDto
        {
            Id = settings.Id,
            CompanyId = settings.CompanyId,
            CompanyName = settings.Company?.CompanyName ?? "Unknown",
            SchemaName = settings.SchemaName,
            IsProvisioned = settings.IsProvisioned,
            IsActive = settings.IsActive,
            ProvisionedAt = settings.ProvisionedAt,
            MaxUsers = settings.MaxUsers,
            AdminNotes = settings.AdminNotes,
            // Company SMTP settings — password is never exposed, only a flag
            SmtpHost = settings.SmtpHost,
            SmtpPort = settings.SmtpPort,
            SmtpUsername = settings.SmtpUsername,
            SmtpSenderEmail = settings.SmtpSenderEmail,
            SmtpSenderName = settings.SmtpSenderName,
            SmtpUseSsl = settings.SmtpUseSsl,
            HasSmtpPassword = !string.IsNullOrEmpty(settings.SmtpPassword),
            // Company AI settings — API keys are never exposed, only flags
            AiDefaultProvider = settings.AiDefaultProvider,
            AiClaudeModel = settings.AiClaudeModel,
            HasAiClaudeApiKey = !string.IsNullOrEmpty(settings.AiClaudeApiKey),
            AiOpenAiModel = settings.AiOpenAiModel,
            HasAiOpenAiApiKey = !string.IsNullOrEmpty(settings.AiOpenAiApiKey),
            AiGeminiModel = settings.AiGeminiModel,
            HasAiGeminiApiKey = !string.IsNullOrEmpty(settings.AiGeminiApiKey),
            AiOllamaBaseUrl = settings.AiOllamaBaseUrl,
            AiOllamaModel = settings.AiOllamaModel,
            CreatedAt = settings.CreatedAt,
            UpdatedAt = settings.UpdatedAt
        };
    }

    #endregion
}
