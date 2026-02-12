# Pagination, Filtering & Sorting - Implementační dokumentace

> Vytvořeno: 8. ledna 2026
> Status: API implementace částečně dokončena, zbývá dokončit InvoiceService, UserService a všechny UI komponenty

## ✅ Dokončeno

### API Infrastructure
- ✅ `PagedResult<T>` - Generic wrapper pro stránkovaná data
- ✅ `PaginationParams` - Base třída pro pagination parametry
- ✅ `QueryableExtensions` - Extension methods pro IQueryable (ApplySorting, ToPagedResultAsync, ApplySearch)
- ✅ `ClientFilterDto` - Filter parametry pro Clients
- ✅ `InvoiceFilterDto` - Filter parametry pro Invoices
- ✅ `UserFilterDto` - Filter parametry pro Users

### API Implementation
- ✅ `IClientService.GetClientsPagedAsync()` - Interface metoda
- ✅ `ClientService.GetClientsPagedAsync()` - Implementace
- ✅ `ClientController.GetClientsPaged()` - Endpoint GET /api/client/paged
- ✅ `CompanyController.GetCompaniesPaged()` - Endpoint GET /api/company/paged
- ✅ `IInvoiceService.GetInvoicesPagedAsync()` - Interface metoda

## 🚧 Zbývá implementovat

### API - Services

#### InvoiceService.GetInvoicesPagedAsync()

```csharp
// InvoiceApi.Infrastructure/Service/InvoiceService.cs

// Přidat using:
using InvoiceApi.Application.Common.Extensions;
using InvoiceApi.Application.Common.Pagination;

// Přidat metodu:
public async Task<PagedResult<InvoiceDto>> GetInvoicesPagedAsync(
    InvoiceFilterDto filter,
    CancellationToken cancellationToken = default)
{
    _logger.LogInformation("Fetching paged invoices (Page: {Page}, PageSize: {PageSize})",
        filter.Page, filter.PageSize);

    var query = _context.Invoice
        .Include(i => i.Client)
        .Include(i => i.Issuer)
        .Include(i => i.Items)
            .ThenInclude(item => item.VatRate)
        .AsQueryable();

    // Apply filters
    if (filter.DocumentType.HasValue)
        query = query.Where(i => i.DocumentType == filter.DocumentType.Value);

    if (filter.Status.HasValue)
        query = query.Where(i => i.Status == filter.Status.Value);

    if (filter.ClientId.HasValue)
        query = query.Where(i => i.ClientId == filter.ClientId.Value);

    if (filter.IssuerId.HasValue)
        query = query.Where(i => i.IssuerId == filter.IssuerId.Value);

    if (filter.IssueDateFrom.HasValue)
        query = query.Where(i => i.IssueDate >= filter.IssueDateFrom.Value);

    if (filter.IssueDateTo.HasValue)
        query = query.Where(i => i.IssueDate <= filter.IssueDateTo.Value);

    if (filter.DueDateFrom.HasValue)
        query = query.Where(i => i.DueDate >= filter.DueDateFrom.Value);

    if (filter.DueDateTo.HasValue)
        query = query.Where(i => i.DueDate <= filter.DueDateTo.Value);

    if (filter.IsOverdue == true)
        query = query.Where(i => i.Status == EInvoiceStatus.Completed && i.DueDate < DateTime.UtcNow);

    if (!string.IsNullOrWhiteSpace(filter.Currency))
        query = query.Where(i => i.Currency == filter.Currency);

    if (filter.MinAmount.HasValue)
        query = query.Where(i => i.TotalWithVat >= filter.MinAmount.Value);

    if (filter.MaxAmount.HasValue)
        query = query.Where(i => i.TotalWithVat <= filter.MaxAmount.Value);

    // Apply search
    if (!string.IsNullOrWhiteSpace(filter.Search))
    {
        var search = filter.Search.ToLower();
        query = query.Where(i =>
            (i.DocumentNumber != null && i.DocumentNumber.ToLower().Contains(search)) ||
            (i.Client.CompanyName != null && i.Client.CompanyName.ToLower().Contains(search)) ||
            (i.VariableSymbol != null && i.VariableSymbol.Contains(search)));
    }

    // Apply sorting
    var validSortFields = new[] { "DocumentNumber", "IssueDate", "DueDate", "TotalWithVat", "Status", "CreatedAt" };
    var sortBy = !string.IsNullOrWhiteSpace(filter.SortBy) && validSortFields.Contains(filter.SortBy, StringComparer.OrdinalIgnoreCase)
        ? filter.SortBy
        : "IssueDate";

    query = query.ApplySorting(sortBy, filter.IsDescending);

    // Get paged results
    var pagedResult = await query.ToPagedResultAsync(filter.Page, filter.PageSize, cancellationToken);

    // Map to DTOs
    return new PagedResult<InvoiceDto>(
        pagedResult.Items.Select(MapToDto).ToList(),
        pagedResult.TotalCount,
        pagedResult.PageNumber,
        pagedResult.PageSize);
}
```

#### IUserService & UserService

```csharp
// IUserService - přidat using a metodu:
using InvoiceApi.Application.Common.Pagination;

Task<PagedResult<UserDto>> GetUsersPagedAsync(
    UserFilterDto filter,
    CancellationToken cancellationToken = default);

// UserService - implementace:
public async Task<PagedResult<UserDto>> GetUsersPagedAsync(
    UserFilterDto filter,
    CancellationToken cancellationToken = default)
{
    var query = _context.User
        .Include(u => u.Company)
        .AsQueryable();

    // Apply filters
    if (!filter.IncludeInactive)
        query = query.Where(u => u.IsActive);

    if (filter.Role.HasValue)
        query = query.Where(u => u.Role == filter.Role.Value);

    if (filter.CompanyId.HasValue)
        query = query.Where(u => u.CompanyId == filter.CompanyId.Value);

    if (filter.NeverLoggedIn == true)
        query = query.Where(u => u.LastLoginAt == null);
    else if (filter.NeverLoggedIn == false)
        query = query.Where(u => u.LastLoginAt != null);

    // Apply search
    if (!string.IsNullOrWhiteSpace(filter.Search))
    {
        var search = filter.Search.ToLower();
        query = query.Where(u =>
            (u.Email != null && u.Email.ToLower().Contains(search)) ||
            (u.FirstName != null && u.FirstName.ToLower().Contains(search)) ||
            (u.LastName != null && u.LastName.ToLower().Contains(search)) ||
            (u.Company != null && u.Company.CompanyName != null && u.Company.CompanyName.ToLower().Contains(search)));
    }

    // Apply sorting
    var validSortFields = new[] { "Email", "FirstName", "LastName", "Role", "LastLoginAt", "CreatedAt" };
    var sortBy = !string.IsNullOrWhiteSpace(filter.SortBy) && validSortFields.Contains(filter.SortBy, StringComparer.OrdinalIgnoreCase)
        ? filter.SortBy
        : "Email";

    query = query.ApplySorting(sortBy, filter.IsDescending);

    // Get paged results
    var pagedResult = await query.ToPagedResultAsync(filter.Page, filter.PageSize, cancellationToken);

    // Map to DTOs
    return new PagedResult<UserDto>(
        pagedResult.Items.Select(MapToDto).ToList(),
        pagedResult.TotalCount,
        pagedResult.PageNumber,
        pagedResult.PageSize);
}
```

### API - Controllers

#### InvoiceController

```csharp
// Přidat using:
using InvoiceApi.Application.Common.Pagination;

// Přidat endpoint:
[HttpGet("paged")]
[ProducesResponseType(typeof(PagedResult<InvoiceDto>), StatusCodes.Status200OK)]
public async Task<ActionResult<PagedResult<InvoiceDto>>> GetInvoicesPaged(
    [FromQuery] InvoiceFilterDto filter,
    CancellationToken cancellationToken = default)
{
    _logger.LogInformation("GET /api/invoice/paged - Page: {Page}, PageSize: {PageSize}",
        filter.Page, filter.PageSize);

    var result = await _invoiceService.GetInvoicesPagedAsync(filter, cancellationToken);
    return Ok(result);
}
```

#### UserController

```csharp
// Přidat using:
using InvoiceApi.Application.Common.Pagination;

// Přidat endpoint:
[HttpGet("paged")]
[ProducesResponseType(typeof(PagedResult<UserDto>), StatusCodes.Status200OK)]
public async Task<ActionResult<PagedResult<UserDto>>> GetUsersPaged(
    [FromQuery] UserFilterDto filter,
    CancellationToken cancellationToken = default)
{
    _logger.LogInformation("GET /api/user/paged - Page: {Page}, PageSize: {PageSize}",
        filter.Page, filter.PageSize);

    var result = await _userService.GetUsersPagedAsync(filter, cancellationToken);
    return Ok(result);
}
```

## 🎨 UI Implementation (Blazor)

### Blazor Services

Každý ApiService potřebuje metodu pro paged request:

```csharp
// ClientApiService.cs
public async Task<PagedResult<ClientDto>> GetPagedAsync(
    int page = 1,
    int pageSize = 50,
    string? search = null,
    string? sortBy = null,
    string? sortDirection = "asc",
    bool? isVatPayer = null,
    string? city = null,
    bool includeInactive = false)
{
    await AddAuthorizationHeaderAsync();

    var query = new List<string>();
    query.Add($"page={page}");
    query.Add($"pageSize={pageSize}");
    if (!string.IsNullOrEmpty(search)) query.Add($"search={Uri.EscapeDataString(search)}");
    if (!string.IsNullOrEmpty(sortBy)) query.Add($"sortBy={sortBy}");
    if (!string.IsNullOrEmpty(sortDirection)) query.Add($"sortDirection={sortDirection}");
    if (isVatPayer.HasValue) query.Add($"isVatPayer={isVatPayer.Value}");
    if (!string.IsNullOrEmpty(city)) query.Add($"city={Uri.EscapeDataString(city)}");
    if (includeInactive) query.Add("includeInactive=true");

    var queryString = string.Join("&", query);
    var response = await _httpClient.GetAsync($"/api/client/paged?{queryString}");
    response.EnsureSuccessStatusCode();

    return await response.Content.ReadFromJsonAsync<PagedResult<ClientDto>>()
        ?? new PagedResult<ClientDto>();
}
```

### UI Components (Clients.razor příklad)

```razor
@page "/clients"
@attribute [Microsoft.AspNetCore.Authorization.Authorize]
@rendermode InteractiveServer
@using InvoiceApi.Application.Dto.Client
@using InvoiceApi.Application.Common.Pagination
@inject ClientApiService ClientService
@inject ISnackbar Snackbar

<PageTitle>Klienti</PageTitle>

<MudText Typo="Typo.h4" GutterBottom="true">Klienti</MudText>
<MudText Class="mb-4">Správa zákazníků</MudText>

<MudPaper Class="pa-4">
    <!-- Toolbar s filtry -->
    <MudGrid Class="mb-4">
        <MudItem xs="12" md="4">
            <MudTextField @bind-Value="_searchString"
                          Label="Hledat"
                          Placeholder="Název, IČO, město..."
                          Adornment="Adornment.Start"
                          AdornmentIcon="@Icons.Material.Filled.Search"
                          Immediate="false"
                          DebounceInterval="500"
                          OnDebounceIntervalElapsed="OnSearchChanged" />
        </MudItem>
        <MudItem xs="12" md="3">
            <MudSelect T="bool?" @bind-Value="_filterIsVatPayer"
                       Label="Plátce DPH"
                       Clearable="true"
                       OnClearButtonClick="() => { _filterIsVatPayer = null; _ = LoadDataAsync(); }">
                <MudSelectItem T="bool?" Value="true">Ano</MudSelectItem>
                <MudSelectItem T="bool?" Value="false">Ne</MudSelectItem>
            </MudSelect>
        </MudItem>
        <MudItem xs="12" md="3">
            <MudTextField @bind-Value="_filterCity"
                          Label="Město"
                          Clearable="true"
                          OnDebounceIntervalElapsed="() => _ = LoadDataAsync()"
                          DebounceInterval="500" />
        </MudItem>
        <MudItem xs="12" md="2">
            <MudSwitch @bind-Value="_includeInactive"
                       Label="Zobrazit neaktivní"
                       Color="Color.Primary"
                       OnChange="() => _ = LoadDataAsync()" />
        </MudItem>
    </MudGrid>

    <!-- Tlačítko přidat -->
    <MudButton Variant="Variant.Filled"
               Color="Color.Primary"
               StartIcon="@Icons.Material.Filled.Add"
               OnClick="OpenCreateDialog"
               Class="mb-4">
        Přidat klienta
    </MudButton>

    <!-- Table with server-side pagination -->
    <MudTable @ref="_table"
              Items="@_pagedClients.Items"
              Dense="true"
              Hover="true"
              Striped="true"
              Loading="@_loading"
              ServerData="LoadServerDataAsync"
              OnRowClick="RowClickEvent">
        <HeaderContent>
            <MudTh><MudTableSortLabel SortBy="new Func<ClientDto, object>(x => x.CompanyName)">Název společnosti</MudTableSortLabel></MudTh>
            <MudTh><MudTableSortLabel SortBy="new Func<ClientDto, object>(x => x.RegistrationNumber)">IČO</MudTableSortLabel></MudTh>
            <MudTh>DIČ</MudTh>
            <MudTh>Město</MudTh>
            <MudTh>Plátce DPH</MudTh>
            <MudTh><MudTableSortLabel SortBy="new Func<ClientDto, object>(x => x.IsActive)">Stav</MudTableSortLabel></MudTh>
            <MudTh>Akce</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd DataLabel="Název">@context.CompanyName</MudTd>
            <MudTd DataLabel="IČO">@context.RegistrationNumber</MudTd>
            <MudTd DataLabel="DIČ">@(context.TaxNumber ?? "—")</MudTd>
            <MudTd DataLabel="Město">@(context.Address.FirstOrDefault()?.City ?? "—")</MudTd>
            <MudTd DataLabel="Plátce DPH">
                @if (context.IsVatPayer)
                {
                    <MudIcon Icon="@Icons.Material.Filled.Check" Color="Color.Success" Size="Size.Small" />
                }
            </MudTd>
            <MudTd DataLabel="Stav">
                <MudChip T="string" Size="Size.Small" Color="@(context.IsActive ? Color.Success : Color.Default)">
                    @(context.IsActive ? "Aktivní" : "Neaktivní")
                </MudChip>
            </MudTd>
            <MudTd DataLabel="Akce">
                <MudIconButton Icon="@Icons.Material.Filled.Edit"
                               Size="Size.Small"
                               Color="Color.Primary"
                               OnClick="@(() => OpenEditDialog(context))" />
                <MudIconButton Icon="@Icons.Material.Filled.Delete"
                               Size="Size.Small"
                               Color="Color.Error"
                               OnClick="@(() => DeleteClient(context.Id))" />
            </MudTd>
        </RowTemplate>
        <PagerContent>
            <MudTablePager PageSizeOptions="new int[] { 10, 25, 50, 100 }"
                           InfoFormat="{first_item}-{last_item} z {all_items}" />
        </PagerContent>
    </MudTable>
</MudPaper>

@code {
    private MudTable<ClientDto>? _table;
    private PagedResult<ClientDto> _pagedClients = new();
    private bool _loading = true;

    // Filter parameters
    private string _searchString = "";
    private bool? _filterIsVatPayer = null;
    private string? _filterCity = null;
    private bool _includeInactive = false;

    private async Task<TableData<ClientDto>> LoadServerDataAsync(TableState state, CancellationToken cancellationToken)
    {
        _loading = true;

        try
        {
            // Map MudTable sort to API sort
            string? sortBy = null;
            string? sortDirection = null;

            if (!string.IsNullOrEmpty(state.SortLabel))
            {
                sortBy = state.SortLabel;
                sortDirection = state.SortDirection == SortDirection.Descending ? "desc" : "asc";
            }

            _pagedClients = await ClientService.GetPagedAsync(
                page: state.Page + 1, // MudTable is 0-based, API is 1-based
                pageSize: state.PageSize,
                search: _searchString,
                sortBy: sortBy,
                sortDirection: sortDirection,
                isVatPayer: _filterIsVatPayer,
                city: _filterCity,
                includeInactive: _includeInactive);

            return new TableData<ClientDto>
            {
                Items = _pagedClients.Items,
                TotalItems = _pagedClients.TotalCount
            };
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Chyba při načítání klientů: {ex.Message}", Severity.Error);
            return new TableData<ClientDto> { Items = new List<ClientDto>(), TotalItems = 0 };
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnSearchChanged()
    {
        if (_table != null)
        {
            await _table.ReloadServerData();
        }
    }

    private async Task LoadDataAsync()
    {
        if (_table != null)
        {
            await _table.ReloadServerData();
        }
    }

    private void RowClickEvent(TableRowClickEventArgs<ClientDto> args)
    {
        // Optional: Navigate to detail or open edit dialog
    }

    // ... rest of the code (dialogs, CRUD operations) ...
}
```

## 📋 Checklist implementace

### API Backend
- [x] PagedResult<T> infrastructure
- [x] PaginationParams base class
- [x] QueryableExtensions
- [x] ClientFilterDto, InvoiceFilterDto, UserFilterDto
- [x] ClientService.GetClientsPagedAsync()
- [x] ClientController.GetClientsPaged()
- [x] CompanyController.GetCompaniesPaged()
- [ ] InvoiceService.GetInvoicesPagedAsync()
- [ ] InvoiceController.GetInvoicesPaged()
- [ ] IUserService.GetUsersPagedAsync()
- [ ] UserService.GetUsersPagedAsync()
- [ ] UserController.GetUsersPaged()

### Blazor UI
- [ ] PagedResult<T> model class
- [ ] ClientApiService.GetPagedAsync()
- [ ] InvoiceApiService.GetPagedAsync()
- [ ] UserApiService.GetPagedAsync()
- [ ] CompanyApiService.GetPagedAsync()
- [ ] Clients.razor - MudTable s server-side pagination
- [ ] Invoices.razor - MudTable s server-side pagination + filtry
- [ ] Users.razor - MudTable s server-side pagination + filtry
- [ ] Companies.razor - MudTable s server-side pagination + filtry
- [ ] VatRates.razor - MudTable s pagination (optional)

## 🔧 Build & Test

```bash
# Build API
cd InvoiceApi.API
dotnet build

# Build Blazor UI
cd ../InvoiceApi.BlazorUI
dotnet build

# Run tests
dotnet test

# Start servers
dotnet run --project InvoiceApi.API
dotnet run --project InvoiceApi.BlazorUI
```

## 📊 Test endpoints

```bash
# GET paginated clients
curl "http://localhost:5237/api/client/paged?page=1&pageSize=10&search=test&sortBy=CompanyName&sortDirection=asc"

# GET paginated invoices
curl "http://localhost:5237/api/invoice/paged?page=1&pageSize=25&status=1&issueDateFrom=2026-01-01"

# GET paginated users
curl "http://localhost:5237/api/user/paged?page=1&pageSize=50&role=1"

# GET paginated companies
curl "http://localhost:5237/api/company/paged?page=1&pageSize=10&isVatPayer=true"
```

## ✅ Dokončeno
Tato dokumentace pokrývá veškeré potřebné změny pro kompletní implementaci pagination, filtering a sorting v InvoiceApi projektu.
