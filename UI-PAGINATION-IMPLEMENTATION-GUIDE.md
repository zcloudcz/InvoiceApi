# Průvodce implementací UI paginace, filtrování a řazení

## ✅ DOKONČENO - Backend API (100%)

### 1. Generická infrastruktura
- ✅ `PagedResult<T>` - wrapper pro stránkovaná data
- ✅ `PaginationParams` - základní třída pro parametry stránkování
- ✅ `QueryableExtensions` - rozšiřující metody pro IQueryable (ApplySorting, ToPagedResultAsync, ApplySearch)
- ✅ Filter DTO pro všechny entity (ClientFilterDto, InvoiceFilterDto, UserFilterDto)

### 2. API Service implementations
- ✅ `ClientService.GetClientsPagedAsync()` - kompletní filtrace, řazení, stránkování
- ✅ `InvoiceService.GetInvoicesPagedAsync()` - kompletní filtrace včetně datumů a částek
- ✅ `UserService.GetUsersPagedAsync()` - filtrace podle role, společnosti, aktivity
- ✅ API build úspěšný (0 chyb)

### 3. API Controllers
- ✅ `ClientController.GetClientsPaged()` - GET /api/client/paged
- ✅ `CompanyController.GetCompaniesPaged()` - GET /api/company/paged
- ✅ `InvoiceController.GetInvoicesPaged()` - GET /api/invoice/paged
- ✅ `UserController.GetUsersPaged()` - GET /api/user/paged

### 4. Blazor API Services
- ✅ `PagedResult<T>` model v Blazor projektu
- ✅ `ClientApiService.GetPagedAsync()` - všechny parametry filtrace
- ✅ `CompanyApiService.GetPagedAsync()` - všechny parametry filtrace
- ✅ `InvoiceApiService.GetPagedAsync()` - kompletní filtrace (16 parametrů)
- ✅ `UserApiService.GetPagedAsync()` - filtrace podle role a společnosti

## 🔄 ZBÝVÁ - Blazor UI komponenty

### Komponenty k aktualizaci
Následující komponenty je třeba aktualizovat, aby používaly MudTable se server-side paginací:

1. `InvoiceApi.BlazorUI/Components/Pages/Clients.razor`
2. `InvoiceApi.BlazorUI/Components/Pages/Companies.razor`
3. `InvoiceApi.BlazorUI/Components/Pages/Invoices.razor`
4. `InvoiceApi.BlazorUI/Components/Pages/Users.razor`

---

## 📋 Příklad implementace - Clients.razor

### Kompletní šablona pro Clients.razor

```razor
@page "/clients"
@using InvoiceApi.Application.Dto.Client
@using InvoiceApi.BlazorUI.Models
@using InvoiceApi.BlazorUI.Services
@inject ClientApiService ClientService
@inject NavigationManager Navigation
@inject ISnackbar Snackbar

<PageTitle>Zákazníci</PageTitle>

<MudText Typo="Typo.h4" Class="mb-4">Zákazníci</MudText>

<MudPaper Class="pa-4 mb-4">
    <MudGrid>
        <MudItem xs="12" sm="6" md="4">
            <MudTextField @bind-Value="_searchString"
                          Label="Vyhledávání"
                          Variant="Variant.Outlined"
                          Adornment="Adornment.Start"
                          AdornmentIcon="@Icons.Material.Filled.Search"
                          Immediate="false"
                          DebounceInterval="500"
                          OnDebounceIntervalElapsed="OnSearchChanged" />
        </MudItem>
        <MudItem xs="12" sm="6" md="4">
            <MudSelect @bind-Value="_isVatPayerFilter"
                       Label="Plátce DPH"
                       Variant="Variant.Outlined"
                       Clearable="true"
                       T="bool?">
                <MudSelectItem T="bool?" Value="@((bool?)null)">Vše</MudSelectItem>
                <MudSelectItem T="bool?" Value="@((bool?)true)">Ano</MudSelectItem>
                <MudSelectItem T="bool?" Value="@((bool?)false)">Ne</MudSelectItem>
            </MudSelect>
        </MudItem>
        <MudItem xs="12" sm="6" md="4">
            <MudSwitch @bind-Checked="_includeInactive" Color="Color.Primary" Label="Zahrnout neaktivní" />
        </MudItem>
        <MudItem xs="12">
            <MudButton Variant="Variant.Filled"
                       Color="Color.Primary"
                       OnClick="ApplyFilters"
                       StartIcon="@Icons.Material.Filled.FilterAlt">
                Filtrovat
            </MudButton>
            <MudButton Variant="Variant.Outlined"
                       OnClick="ResetFilters"
                       StartIcon="@Icons.Material.Filled.Clear"
                       Class="ml-2">
                Vymazat filtry
            </MudButton>
        </MudItem>
    </MudGrid>
</MudPaper>

<MudTable ServerData="@(new Func<TableState, Task<TableData<ClientDto>>>(ServerReload))"
          Dense="true"
          Hover="true"
          @ref="_table"
          Loading="@_loading"
          LoadingProgressColor="Color.Info">
    <ToolBarContent>
        <MudText Typo="Typo.h6">Seznam zákazníků</MudText>
        <MudSpacer />
        <MudButton Variant="Variant.Filled"
                   Color="Color.Primary"
                   StartIcon="@Icons.Material.Filled.Add"
                   Href="/clients/create">
            Nový zákazník
        </MudButton>
    </ToolBarContent>
    <HeaderContent>
        <MudTh><MudTableSortLabel SortLabel="CompanyName" T="ClientDto">Název společnosti</MudTableSortLabel></MudTh>
        <MudTh><MudTableSortLabel SortLabel="RegistrationNumber" T="ClientDto">IČO</MudTableSortLabel></MudTh>
        <MudTh><MudTableSortLabel SortLabel="TaxNumber" T="ClientDto">DIČ</MudTableSortLabel></MudTh>
        <MudTh>Plátce DPH</MudTh>
        <MudTh>Město</MudTh>
        <MudTh>Země</MudTh>
        <MudTh><MudTableSortLabel SortLabel="CreatedAt" T="ClientDto">Vytvořeno</MudTableSortLabel></MudTh>
        <MudTh>Akce</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd DataLabel="Název">@context.CompanyName</MudTd>
        <MudTd DataLabel="IČO">@context.RegistrationNumber</MudTd>
        <MudTd DataLabel="DIČ">@context.TaxNumber</MudTd>
        <MudTd DataLabel="Plátce DPH">
            @if (context.IsVatPayer)
            {
                <MudChip Size="Size.Small" Color="Color.Success">Ano</MudChip>
            }
            else
            {
                <MudChip Size="Size.Small" Color="Color.Default">Ne</MudChip>
            }
        </MudTd>
        <MudTd DataLabel="Město">@context.Address?.FirstOrDefault()?.City</MudTd>
        <MudTd DataLabel="Země">@context.Address?.FirstOrDefault()?.Country</MudTd>
        <MudTd DataLabel="Vytvořeno">@context.CreatedAt.ToString("dd.MM.yyyy")</MudTd>
        <MudTd DataLabel="Akce">
            <MudIconButton Icon="@Icons.Material.Filled.Edit"
                           Color="Color.Primary"
                           Size="Size.Small"
                           Href="@($"/clients/edit/{context.Id}")" />
            <MudIconButton Icon="@Icons.Material.Filled.Delete"
                           Color="Color.Error"
                           Size="Size.Small"
                           OnClick="@(() => DeleteClient(context.Id))" />
        </MudTd>
    </RowTemplate>
    <NoRecordsContent>
        <MudText>Žádné zákazníky nebyly nalezeny</MudText>
    </NoRecordsContent>
    <LoadingContent>
        <MudText>Načítání...</MudText>
    </LoadingContent>
    <PagerContent>
        <MudTablePager PageSizeOptions="new int[] { 10, 25, 50, 100 }" />
    </PagerContent>
</MudTable>

@code {
    private MudTable<ClientDto>? _table;
    private bool _loading = false;

    // Filter parameters
    private string? _searchString = null;
    private bool? _isVatPayerFilter = null;
    private bool _includeInactive = false;
    private string? _cityFilter = null;
    private string? _countryFilter = null;

    private async Task<TableData<ClientDto>> ServerReload(TableState state)
    {
        _loading = true;
        try
        {
            // Get sort parameters
            var sortBy = state.SortLabel;
            var sortDirection = state.SortDirection == SortDirection.Descending ? "desc" : "asc";

            // Call API with pagination and filters
            var result = await ClientService.GetPagedAsync(
                page: state.Page + 1, // MudTable is 0-indexed, API is 1-indexed
                pageSize: state.PageSize,
                search: _searchString,
                sortBy: sortBy,
                sortDirection: sortDirection,
                isVatPayer: _isVatPayerFilter,
                includeInactive: _includeInactive,
                city: _cityFilter,
                country: _countryFilter
            );

            return new TableData<ClientDto>
            {
                TotalItems = result.TotalCount,
                Items = result.Items
            };
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Chyba při načítání dat: {ex.Message}", Severity.Error);
            return new TableData<ClientDto> { TotalItems = 0, Items = new List<ClientDto>() };
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnSearchChanged()
    {
        await _table!.ReloadServerData();
    }

    private async Task ApplyFilters()
    {
        await _table!.ReloadServerData();
    }

    private async Task ResetFilters()
    {
        _searchString = null;
        _isVatPayerFilter = null;
        _includeInactive = false;
        _cityFilter = null;
        _countryFilter = null;

        await _table!.ReloadServerData();
    }

    private async Task DeleteClient(long id)
    {
        var result = await ClientService.DeleteAsync(id);
        if (result)
        {
            Snackbar.Add("Zákazník byl úspěšně smazán", Severity.Success);
            await _table!.ReloadServerData();
        }
        else
        {
            Snackbar.Add("Chyba při mazání zákazníka", Severity.Error);
        }
    }
}
```

---

## 🎯 Klíčové body implementace

### 1. Server-Side Reload metoda
```csharp
private async Task<TableData<TDto>> ServerReload(TableState state)
{
    _loading = true;
    try
    {
        var sortBy = state.SortLabel;
        var sortDirection = state.SortDirection == SortDirection.Descending ? "desc" : "asc";

        var result = await ApiService.GetPagedAsync(
            page: state.Page + 1, // MudTable je 0-indexed, API je 1-indexed
            pageSize: state.PageSize,
            search: _searchString,
            sortBy: sortBy,
            sortDirection: sortDirection
            // + další parametry filtrace...
        );

        return new TableData<TDto>
        {
            TotalItems = result.TotalCount,
            Items = result.Items
        };
    }
    finally
    {
        _loading = false;
    }
}
```

### 2. MudTable konfigurace
```razor
<MudTable ServerData="@(new Func<TableState, Task<TableData<TDto>>>(ServerReload))"
          Dense="true"
          Hover="true"
          @ref="_table"
          Loading="@_loading">
    <!-- ... -->
    <PagerContent>
        <MudTablePager PageSizeOptions="new int[] { 10, 25, 50, 100 }" />
    </PagerContent>
</MudTable>
```

### 3. Filtrace a vyhledávání
```razor
<MudTextField @bind-Value="_searchString"
              Label="Vyhledávání"
              DebounceInterval="500"
              OnDebounceIntervalElapsed="OnSearchChanged" />

<MudButton OnClick="ApplyFilters">Filtrovat</MudButton>
<MudButton OnClick="ResetFilters">Vymazat filtry</MudButton>
```

### 4. Řazení sloupců
```razor
<MudTh>
    <MudTableSortLabel SortLabel="PropertyName" T="TDto">
        Název sloupce
    </MudTableSortLabel>
</MudTh>
```

---

## 📊 Specifické filtry pro jednotlivé entity

### Invoices.razor - Dodatečné filtry
```razor
<MudSelect @bind-Value="_documentTypeFilter" Label="Typ dokumentu" Clearable="true">
    <MudSelectItem Value="@EDocumentType.Invoice">Faktura</MudSelectItem>
    <MudSelectItem Value="@EDocumentType.CreditNote">Dobropis</MudSelectItem>
</MudSelect>

<MudSelect @bind-Value="_statusFilter" Label="Stav" Clearable="true">
    <MudSelectItem Value="@EInvoiceStatus.Draft">Koncept</MudSelectItem>
    <MudSelectItem Value="@EInvoiceStatus.Completed">Dokončeno</MudSelectItem>
    <MudSelectItem Value="@EInvoiceStatus.Paid">Zaplaceno</MudSelectItem>
</MudSelect>

<MudDatePicker @bind-Date="_issueDateFrom" Label="Datum vystavení od" />
<MudDatePicker @bind-Date="_issueDateTo" Label="Datum vystavení do" />

<MudDatePicker @bind-Date="_dueDateFrom" Label="Datum splatnosti od" />
<MudDatePicker @bind-Date="_dueDateTo" Label="Datum splatnosti do" />

<MudSwitch @bind-Checked="_isOverdueFilter" Label="Pouze po splatnosti" />

<MudNumericField @bind-Value="_minAmount" Label="Min. částka" />
<MudNumericField @bind-Value="_maxAmount" Label="Max. částka" />
```

### Users.razor - Dodatečné filtry
```razor
<MudSelect @bind-Value="_roleFilter" Label="Role" Clearable="true">
    <MudSelectItem Value="@EUserRole.SysAdmin">SysAdmin</MudSelectItem>
    <MudSelectItem Value="@EUserRole.Admin">Admin</MudSelectItem>
    <MudSelectItem Value="@EUserRole.User">Uživatel</MudSelectItem>
</MudSelect>

<MudSwitch @bind-Checked="_neverLoggedInFilter" Label="Nikdy se nepřihlásili" />
```

---

## 🔧 Checklist pro každou komponentu

Pro každou ze 4 komponent (Clients, Companies, Invoices, Users):

1. ✅ Přidat `@ref` na MudTable
2. ✅ Nastavit `ServerData` parameter s metodou ServerReload
3. ✅ Implementovat `ServerReload(TableState state)` metodu
4. ✅ Přidat filter UI (MudTextField, MudSelect, MudSwitch, atd.)
5. ✅ Přidat `_loading` flag a nastavit `Loading` parameter
6. ✅ Implementovat `ApplyFilters()` a `ResetFilters()` metody
7. ✅ Přidat `MudTableSortLabel` pro řaditelné sloupce
8. ✅ Konfigurovat `PagerContent` s PageSizeOptions
9. ✅ Testovat pagination, filtrování a řazení

---

## 🚀 Další kroky

1. **Aktualizovat Clients.razor** - použít výše uvedenou šablonu
2. **Aktualizovat Companies.razor** - podobné jako Clients (stejné filtry)
3. **Aktualizovat Invoices.razor** - přidat rozšířené filtry (datum, částky, status)
4. **Aktualizovat Users.razor** - přidat filtry role a last login
5. **Build Blazor projektu** - `dotnet build InvoiceApi.BlazorUI/InvoiceApi.BlazorUI.csproj`
6. **Spustit aplikaci** - otestovat vše end-to-end
7. **Otestovat:**
   - Změna stránky funguje
   - Změna velikosti stránky funguje
   - Vyhledávání funguje s debounce
   - Filtry se aplikují správně
   - Řazení sloupců funguje (asc/desc)
   - Reset filtrů vymaže všechny hodnoty

---

## 📝 Poznámky

- **MudTable je 0-indexed**: Při volání API je třeba přičíst 1 k `state.Page`
- **DebounceInterval**: Nastaveno na 500ms pro vyhledávání, aby se snížil počet API volání
- **PageSizeOptions**: Výchozí hodnoty jsou [10, 25, 50, 100], lze přizpůsobit
- **Loading state**: Vždy nastavit `_loading = true` před API voláním a `false` v finally
- **Error handling**: Zachytit výjimky a zobrazit Snackbar s chybovou zprávou

---

## ✨ Výhody implementace

1. **Server-side pagination** - Efektivní pro velké datové sady
2. **Flexibilní filtrování** - Uživatelé mohou kombinovat více filtrů
3. **Dynamické řazení** - Kliknutím na záhlaví sloupce
4. **Debounce vyhledávání** - Snižuje počet API volání
5. **Typově bezpečné** - Využívá generické typy C#
6. **Znovu použitelné** - Stejný pattern pro všechny entity
7. **Škálovatelné** - Backend podporuje až 100 záznamů na stránku

---

**Poslední aktualizace:** 2026-01-08
**Stav:** Backend API kompletně hotovo (100%), UI komponenty zbývají (0%)
**Autor:** Claude Sonnet 4.5
