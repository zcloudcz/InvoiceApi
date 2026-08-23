# Fakvio MCP Server

Konzolová aplikace, která zpřístupňuje fakturaci Fakvio AI klientům přes
**Model Context Protocol (MCP)**. Klient (Claude Code, Claude Desktop, …) si
server spustí jako podproces a komunikuje s ním po **stdio**.

```
AI klient ←stdio→ Fakvio.McpServer ←HTTP + JWT→ Fakvio.API ←EF Core→ PostgreSQL
```

Server sám nemá přístup k databázi — všechno jde přes REST API, takže platí
úplně stejná autorizace a tenant izolace jako pro webové UI. Rozsah oprávnění
určuje JWT token, kterým server pracuje.

- Projekt: `Fakvio.McpServer` (net10.0, `PackAsTool`)
- Příkaz nainstalovaného nástroje: **`fakvio-mcp`** (`ToolCommandName` v csproj)
- Jméno serveru hlášené v MCP handshake: `fakvio` (`Program.cs`, `ServerInfo`)
- SDK: `ModelContextProtocol` 1.0.0, transport **pouze stdio**

## Požadavky

- .NET 10 SDK
- Běžící `Fakvio.API` (lokálně nebo v cloudu), dosažitelné z počítače, kde běží AI klient
- Platný JWT token uživatele Fakvio

## Build a spuštění

Vývojově (přímo z repa):

```bash
dotnet build Fakvio.McpServer/Fakvio.McpServer.csproj
dotnet run   --project Fakvio.McpServer
```

Jako globální .NET nástroj (příkaz `fakvio-mcp`):

```bash
dotnet pack Fakvio.McpServer/Fakvio.McpServer.csproj -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg Fakvio.McpServer
```

Aktualizace, resp. odinstalace:

```bash
dotnet tool update    --global --add-source ./nupkg Fakvio.McpServer
dotnet tool uninstall --global Fakvio.McpServer
```

Spuštění z terminálu jen ověří konfiguraci — server pak čeká na JSON-RPC zprávy
na stdin. Bez MCP klienta se nic „nezobrazí“, to je v pořádku. Ukončíte ho
Ctrl+C.

## Konfigurace

Server se konfiguruje **jen proměnnými prostředí** (žádný `appsettings.json`):

| Proměnná | Povinná | Výchozí | Popis |
|----------|---------|---------|-------|
| `FAKVIO_API_TOKEN` | ano | – | JWT bearer token. Chybí-li, server vypíše chybu na stderr a skončí s exit code 1. |
| `FAKVIO_API_URL` | ne | `https://localhost:7001` | Base URL API, např. `https://localhost:7047` nebo `https://api.fakvio.cz`. |

> **Pozor na výchozí hodnotu.** `Fakvio.API` běží lokálně na `https://localhost:7047`
> (viz `Fakvio.API/Properties/launchSettings.json`), takže výchozí `7001` na
> lokální vývoj nesedí — `FAKVIO_API_URL` nastavte vždy explicitně.

Při HTTPS na localhost musí být vývojový certifikát důvěryhodný
(`dotnet dev-certs https --trust`), jinak HTTP volání selžou na validaci certifikátu.

### Získání JWT tokenu

Nejrychleji přes login endpoint:

```bash
curl -X POST https://localhost:7047/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"user@example.com","password":"..."}'
```

Odpověď obsahuje `token` a `expiresAt`. Dvě věci, které je dobré vědět předem:

- **Produkce má na loginu reCAPTCHA v3** (`X-Captcha-Token`). Když je
  `Recaptcha:SecretKey` nakonfigurován, curl bez captcha tokenu dostane HTTP 400.
  V takovém případě si token vezměte z prohlížeče po přihlášení do UI:
  localStorage klíč `UserSession`, pole `Token`.
- **Účet se zapnutým 2FA** vrátí z `/api/auth/login` odpověď s
  `requiresTwoFactor: true` a prázdným tokenem. Přihlášení je pak potřeba
  dokončit přes `POST /api/twofactor/verify`.

Token platí **24 hodin** (`JwtSettings:ExpirationHours` v `Fakvio.API/appsettings.json`).
Po vypršení začnou nástroje vracet chyby — stačí do konfigurace klienta vložit nový token.

## Napojení AI klienta

V kořeni repozitáře je vzor `.mcp.json.sample`. Pro Claude Code stačí:

```bash
cp .mcp.json.sample .mcp.json     # a doplnit token
```

```json
{
  "mcpServers": {
    "fakvio": {
      "command": "fakvio-mcp",
      "args": [],
      "env": {
        "FAKVIO_API_URL": "https://localhost:7047",
        "FAKVIO_API_TOKEN": "<váš JWT>"
      }
    }
  }
}
```

Stejný blok `mcpServers` patří i do konfigurace Claude Desktop
(`claude_desktop_config.json`).

Bez instalace nástroje lze server spouštět rovnou ze zdrojáků — místo
`command`/`args` použijte:

```json
"command": "dotnet",
"args": ["run", "--project", "C:/GIT/ZCLOUD/InvoiceApi/Fakvio.McpServer"]
```

`.mcp.json` obsahuje token v otevřené podobě, proto **patří do `.gitignore`**,
nikdy ne do commitu. Verzuje se jen `.mcp.json.sample`.

## Dostupné nástroje (36)

| Soubor | Počet | Nástroje |
|--------|-------|----------|
| `Tools/InvoiceTools.cs` | 10 | ListInvoices, GetInvoice, FindInvoiceByNumber, CreateInvoice, CompleteInvoice, MarkInvoicePaid, SendInvoiceEmail, ExportInvoicePdf, ExportInvoiceIsdoc, DeleteInvoice |
| `Tools/ClientTools.cs` | 6 | ListClients, GetClient, CreateClient, UpdateClient, LookupAres, GetIssuer |
| `Tools/ReceivedInvoiceTools.cs` | 6 | ListReceivedInvoices, GetReceivedInvoice, CreateReceivedInvoice, ApproveReceivedInvoice, MarkReceivedInvoicePaid, DeleteReceivedInvoice |
| `Tools/ReportingTools.cs` | 6 | GetDashboard, GetOverdueInvoices, GetClientInvoices, GetInvoicesByDateRange, GetVatReport, GetOverdueReceivedInvoices |
| `Tools/TaxTools.cs` | 5 | EstimateTax, CompareTaxRegimes, GetAnnualIncome, GetInsuranceAdvance, GetTaxConfig |
| `Tools/TemplateTools.cs` | 3 | ListTemplates, GetTemplate, CreateInvoiceFromTemplate |

Zdroj pravdy je vždy kód — atributy `[McpServerTool]` v `Tools/`:

```bash
grep -rcE '^\s*\[McpServerTool[,(]' Fakvio.McpServer/Tools/*.cs
```

### Chování nástrojů

- Každý nástroj vrací **JSON jako string**. Chyba se nevyhazuje jako výjimka,
  ale vrací se jako `{ "error": "..." }` — AI klient tak dostane čitelnou zprávu
  místo pádu spojení.
- `ExportInvoicePdf` a `ExportInvoiceIsdoc` vracejí soubor jako
  `base64Content` + `fileName`, `mimeType`, `sizeBytes`. Uložení souboru
  je na klientovi.
- Logy jdou **výhradně na stderr** (`LogToStandardErrorThreshold = Trace`).
  Stdout je vyhrazený pro JSON-RPC — jakýkoli zápis do stdout protokol rozbije.

## Přidání nového nástroje

1. Přidejte statickou metodu do existující třídy v `Tools/` (nebo novou třídu
   s atributem `[McpServerToolType]` — `WithToolsFromAssembly()` ji najde sama).
2. Metodu označte `[McpServerTool, Description("…")]` a každý parametr
   `[Description("…")]` — právě z těchto textů se AI rozhoduje, kdy nástroj zavolat.
3. Volejte API přes `IFakvioApiClient`; chybí-li endpoint, doplňte ho do
   `Client/IFakvioApiClient.cs` + `Client/FakvioApiClient.cs`.
4. Celé tělo obalte `try/catch` a vracejte serializovaný JSON (viz stávající nástroje).
5. Aktualizujte tabulku výše a DEVGUIDE §4.9.

## Testy

Unit testy jsou v `Fakvio.Tests.Unit/McpServer/` (mockovaný `IFakvioApiClient`):

```bash
dotnet test Fakvio.Tests.Unit/Fakvio.Tests.Unit.csproj --filter "FullyQualifiedName~McpServer"
```
