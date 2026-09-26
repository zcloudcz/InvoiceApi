# Fakvio MCP Server

Aplikace, která zpřístupňuje fakturaci Fakvio AI klientům přes
**Model Context Protocol (MCP)**. Umí dva režimy — sada nástrojů je v obou
totožná, liší se jen tím, odkud se bere credential:

```
stdio  AI klient ←stdio→ Fakvio.McpServer ←HTTP + API klíč→ Fakvio.API ←EF Core→ PostgreSQL
http   AI klienti ←HTTP/MCP→ Fakvio.McpServer ←HTTP + API klíč→ Fakvio.API ←EF Core→ PostgreSQL
```

Ve **stdio** režimu si server spustí klient (Claude Code, Claude Desktop, …) jako
podproces; jeden proces obsluhuje jednoho uživatele, takže credential procesu
(`FAKVIO_API_TOKEN`) je zároveň credential toho uživatele. Ve **http** režimu jeden proces obsluhuje mnoho
volajících, takže credential nosí každý request zvlášť — je jím API klíč
volajícího, který server jen přeposílá na API.

Server sám nemá přístup k databázi — všechno jde přes REST API, takže platí
úplně stejná autorizace a tenant izolace jako pro webové UI. Rozsah oprávnění
určuje credential, se kterým se volá: role jeho vlastníka, u API klíče navíc
protnutá se scope klíče (`read` vs `read,write` — viz DEVGUIDE §2.10).

- Projekt: `Fakvio.McpServer` (net10.0, `PackAsTool`)
- Příkaz nainstalovaného nástroje: **`fakvio-mcp`** (`ToolCommandName` v csproj)
- Jméno serveru hlášené v MCP handshake: `fakvio` (`Program.cs`, `ServerInfo`)
- SDK: `ModelContextProtocol` 2.2.0, transporty: **stdio** (výchozí) a **Streamable HTTP** na `/mcp`

## Požadavky

- .NET 10 SDK
- **ASP.NET Core shared framework (`Microsoft.AspNetCore.App`)** — a to i pro stdio režim.
  Balíček `ModelContextProtocol.AspNetCore`, který přináší Streamable HTTP transport, nese
  `FrameworkReference`, takže ho potřebuje celý nástroj, ne jen http režim. Na stroji s plným
  .NET 10 SDK je součástí instalace; na cílovém stroji jen s .NET runtime se musí doinstalovat
  ASP.NET Core Runtime.
- Běžící `Fakvio.API` (lokálně nebo v cloudu), dosažitelné z počítače, kde běží AI klient
- Credential podle režimu: **API klíč `fak_live_…`** vydaný v UI na `/settings/integrations`
  (viz USERGUIDE §20) — ve stdio režimu se vloží do `FAKVIO_API_TOKEN`, v http režimu ho nese
  každý request volajícího (server žádný vlastní credential nemá). Ve stdio režimu projde
  i JWT token uživatele, ale platí jen 24 h, takže na trvalé napojení se nehodí.

## Build a spuštění

Vývojově (přímo z repa):

```bash
dotnet build Fakvio.McpServer/Fakvio.McpServer.csproj
dotnet run   --project Fakvio.McpServer
```

Jako globální .NET nástroj (příkaz `fakvio-mcp`) — balíček je na nuget.org,
takže uživatel k instalaci nepotřebuje repozitář:

```bash
dotnet tool install --global Fakvio.McpServer
```

Aktualizace, resp. odinstalace:

```bash
dotnet tool update    --global Fakvio.McpServer
dotnet tool uninstall --global Fakvio.McpServer
```

Verzi na nuget.org publikuje workflow `mcp-server.yml` při pushi do `master`
(job `publish-nuget`). **Číslo verze se zvedá ručně** — `<Version>` v
`Fakvio.McpServer.csproj`, ve stejném PR jako změna nástroje. Push jde
s `--skip-duplicate`, takže merge bez bumpu nic nepublikuje a nic neshodí;
cena za to je, že zapomenutý bump se projeví jen tím, že se oprava k uživatelům
nedostane.

Z rozpracované větve (nepublikovaná verze) se instaluje z lokálního balíčku:

```bash
dotnet pack Fakvio.McpServer/Fakvio.McpServer.csproj -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg Fakvio.McpServer
```

Spuštění z terminálu jen ověří konfiguraci — server pak čeká na JSON-RPC zprávy
na stdin. Bez MCP klienta se nic „nezobrazí“, to je v pořádku. Ukončíte ho
Ctrl+C.

## Konfigurace

Server se konfiguruje **jen proměnnými prostředí** (žádný `appsettings.json`):

| Proměnná | Povinná | Výchozí | Popis |
|----------|---------|---------|-------|
| `FAKVIO_MCP_TRANSPORT` | ne | `stdio` | `stdio` nebo `http`. Cokoli jiného = chyba na stderr a exit code 1. |
| `FAKVIO_API_TOKEN` | jen pro `stdio` | – | Bearer credential — API klíč `fak_live_…` (doporučeno) nebo JWT token. Posílá se beze změny v hlavičce `Authorization`; API rozliší obojí podle prefixu (`fak_` vs `eyJ`), takže server nemusí vědět, co drží. Chybí-li ve stdio režimu, vypíše chybu na stderr a skončí s exit code 1. V HTTP režimu se nepoužívá. |
| `FAKVIO_API_URL` | ne | `https://localhost:7047` | Base URL API, např. `https://localhost:7047` (lokální `Fakvio.API`, viz `Fakvio.API/Properties/launchSettings.json`) nebo `https://api.fakvio.cz`. |
| `ASPNETCORE_URLS` | ne | Kestrel default | Jen `http` režim — na čem server poslouchá, standardní ASP.NET Core proměnná. |

### HTTP režim

```bash
FAKVIO_MCP_TRANSPORT=http FAKVIO_API_URL=https://localhost:7047 ASPNETCORE_URLS=http://localhost:5290 dotnet run
```

Server je obyčejná ASP.NET Core aplikace, takže ho hostuje cokoli, co umí spustit .NET
proces — v tomhle repu běží na Azure App Service (web app `fakvio-mcp-web` / `fakvio-mcp-web-test`).
Nasazení popisuje DEVGUIDE §9.1.

Klient posílá na `POST /mcp` a **musí** přiložit `Authorization: Bearer <API klíč>`
(klíč se zakládá v UI, viz ADMINGUIDE / USERGUIDE). Server klíč ověří na
`GET /api/api-key/me` u **každého** requestu — nic se necachuje, takže revokovaný
klíč přestane fungovat okamžitě. Neplatný nebo chybějící klíč = `401` s hlavičkou
`WWW-Authenticate: Bearer`.

Běží **stateless** (bez `Mcp-Session-Id`), takže `GET /mcp` a `/sse` nejsou k dispozici
a host jde škálovat bez sticky routingu.

> **Výchozí hodnota sedí jen na lokální vývoj.** `FAKVIO_API_URL` bez explicitního
> nastavení míří na `https://localhost:7047` (lokální `Fakvio.API`, viz
> `Fakvio.API/Properties/launchSettings.json`). Pro cloud nebo jiný port ho
> nastavte vždy explicitně.

Při HTTPS na localhost musí být vývojový certifikát důvěryhodný
(`dotnet dev-certs https --trust`), jinak HTTP volání selžou na validaci certifikátu.

### Získání credentialu

**Doporučená cesta — API klíč.** V UI Fakvia otevřete **Nastavení → Integrace**
(`/settings/integrations`), vytvořte klíč, zvolte rozsah (`Jen čtení` /
`Čtení i zápis`) a případnou platnost. Klíč se zobrazí **právě jednou** — server
si ukládá jen jeho SHA-256 otisk, takže ztracený klíč nejde obnovit, jen revokovat
a vydat nový. Stránka rovnou nabídne hotové konfigurační bloky pro oba režimy
(viz „Napojení AI klienta"). Klíč platí do vyplněné expirace nebo do revokace;
revokace je okamžitá (žádná cache, viz HTTP režim výše).

#### Alternativa — JWT token (jen stdio, platí 24 h)

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

V kořeni repozitáře je vzor `.mcp.json.sample`. Obsahuje **oba** režimy —
nechte si ten, který chcete, druhý blok smažte (dva zápisy najednou nejsou chyba,
jen zbytečně registrují server dvakrát). Pro Claude Code stačí:

```bash
cp .mcp.json.sample .mcp.json     # a doplnit klíč
```

**Lokální server (stdio)** — klienta spouští `fakvio-mcp` jako podproces:

```json
{
  "mcpServers": {
    "fakvio": {
      "command": "fakvio-mcp",
      "args": [],
      "env": {
        "FAKVIO_API_URL": "https://localhost:7047",
        "FAKVIO_API_TOKEN": "fak_live_<váš API klíč>"
      }
    }
  }
}
```

**Vzdálený server (Streamable HTTP)** — klient nic neinstaluje, jen volá běžící
HTTP host; `url` je adresa toho hostu (`ASPNETCORE_URLS`) plus cesta `/mcp`:

```json
{
  "mcpServers": {
    "fakvio-remote": {
      "type": "http",
      "url": "http://localhost:5290/mcp",
      "headers": {
        "Authorization": "Bearer fak_live_<váš API klíč>"
      }
    }
  }
}
```

Tytéž bloky `mcpServers` patří i do konfigurace Claude Desktop
(`claude_desktop_config.json`).

> Konfigurační bloky s už vyplněným klíčem vypíše stránka **Nastavení → Integrace**
> hned po vytvoření klíče — copy-paste je rychlejší a nehrozí překlep. Adresu
> vzdáleného serveru tam stránka odhaduje z adresy API; pokud HTTP host běží jinde,
> `url` po vložení opravte.

Bez instalace nástroje lze server spouštět rovnou ze zdrojáků — místo
`command`/`args` použijte:

```json
"command": "dotnet",
"args": ["run", "--project", "<cesta ke klonu repa>/Fakvio.McpServer"]
```

`.mcp.json` obsahuje credential v otevřené podobě, proto **patří do `.gitignore`**,
nikdy ne do commitu. Verzuje se jen `.mcp.json.sample`. Když se soubor přesto někam
dostane, klíč revokujte na `/settings/integrations` — přestane platit okamžitě.

## Dostupné nástroje (39)

| Soubor | Počet | Nástroje |
|--------|-------|----------|
| `Tools/InvoiceTools.cs` | 10 | ListInvoices, GetInvoice, FindInvoiceByNumber, CreateInvoice (typed params — clientId, items, currency code, optional issuerId — see below), CompleteInvoice, MarkInvoicePaid, SendInvoiceEmail, ExportInvoicePdf, ExportInvoiceIsdoc, DeleteInvoice |
| `Tools/ClientTools.cs` | 6 | ListClients, GetClient, CreateClient, UpdateClient, LookupAres, GetIssuer |
| `Tools/ReceivedInvoiceTools.cs` | 7 | ListReceivedInvoices, GetReceivedInvoice, CreateReceivedInvoice, ApproveReceivedInvoice, MarkReceivedInvoicePaid, DeleteReceivedInvoice, UploadReceivedInvoiceAttachment |
| `Tools/ReportingTools.cs` | 6 | GetDashboard, GetOverdueInvoices, GetClientInvoices, GetInvoicesByDateRange, GetVatReport, GetOverdueReceivedInvoices |
| `Tools/TaxTools.cs` | 5 | EstimateTax, CompareTaxRegimes, GetAnnualIncome, GetInsuranceAdvance, GetTaxConfig |
| `Tools/TemplateTools.cs` | 3 | ListTemplates, GetTemplate, CreateInvoiceFromTemplate |
| `Tools/ReadinessTools.cs` | 1 | GetReadiness |
| `Tools/CodeListTools.cs` | 1 | ListCurrencies |

Zdroj pravdy je vždy kód — atributy `[McpServerTool]` v `Tools/`:

```bash
grep -rcE '^\s*\[McpServerTool[,(]' Fakvio.McpServer/Tools/*.cs
```

### Chování nástrojů

- Každý nástroj vrací **JSON jako string**. Doménová chyba (404, validace vstupu) se
  nevyhazuje jako výjimka, ale vrací se jako `{ "error": "..." }` — AI klient tak dostane
  čitelnou zprávu místo pádu spojení.
- Neočekávaná výjimka jde přes `McpToolError.ToJson(ex)` — jedno místo pro všech 38
  nástrojů. Zaloguje celou výjimku server-side a vrátí stabilní JSON, **nikdy `ex.Message`**
  (to může nést syrové tělo API chyby z `FakvioApiClient.EnsureSuccessAsync`). Chybová
  odpověď API (`FakvioApiException`) se podle status kódu mapuje takto:

  | HTTP status | `error` | `message` |
  |---|---|---|
  | 401/403 | `forbidden` | Vždy pevný text — "the key is read-only or your role lacks the permission… /settings/integrations". |
  | 404 | `not_found` | `SafeMessage` z těla (`{"message":"…"}`), jinak "The requested record does not exist." |
  | 400/409/422 | `validation_error` | `SafeMessage` z těla, jinak "The API rejected the input." |
  | ostatní (5xx…) | `internal_error` | Obecná hláška, viz výše. |

  `SafeMessage` existuje jen když tělo API chyby je JSON objekt s řetězcovým `message`, nebo
  bare JSON string (`TaxController` styl) — cokoli jiného (ne-JSON tělo, HTML stránka) se
  nikdy nedostane k modelu, i pro 400/404.
- Zrušení od volajícího se **propaguje**: `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`.
  Filtr je nutný — `HttpClient` vyhodí `TaskCanceledException` (potomek `OperationCanceledException`)
  i při vlastním timeoutu, kdy token volajícího zrušený není; ten případ má skončit sanitizovaným JSONem,
  ne výjimkou přes MCP hranici.
- Deserializace vstupu od modelu má **vlastní menší `try`** před tím hlavním, aby `JsonException`
  z poškozené úspěšné odpovědi API spadla do sanitizované větve, a ne modelu zpátky jako „vstup
  je špatně" i s textem výjimky.
- `ExportInvoicePdf` a `ExportInvoiceIsdoc` vracejí soubor jako
  `base64Content` + `fileName`, `mimeType`, `sizeBytes`. Uložení souboru
  je na klientovi.
- Logy jdou **výhradně na stderr** (`LogToStandardErrorThreshold = Trace`).
  Stdout je vyhrazený pro JSON-RPC — jakýkoli zápis do stdout protokol rozbije.
- **`CreateInvoice` (N2.4) překládá modelem srozumitelný vstup na interní ID** — bez volání API:
  `currency` (ISO kód, výchozí `CZK`) se přeloží přes `list_currencies` na `currencyId`; `issuerId`
  vynechaný znamená vlastní firmu (`get_issuer`); položce bez `vatRateId` doplní ID podle
  `vatRatePercentage` (aktivní sazby k `issueDate`) — jen když je vystavitel plátce DPH, neplátce
  necháváme beze změny. Neznámá měna / procento vrátí konkrétní chybu se seznamem platných hodnot
  a API se vůbec nezavolá.

## Přidání nového nástroje

1. Přidejte statickou metodu do existující třídy v `Tools/` (nebo novou třídu
   s atributem `[McpServerToolType]` — `WithToolsFromAssembly()` ji najde sama).
2. Metodu označte `[McpServerTool(Title = "…", ReadOnly = …, Destructive = …, Idempotent = …, OpenWorld = …), Description("…")]`
   a každý parametr `[Description("…")]` — právě z těchto textů se AI rozhoduje, kdy nástroj zavolat.
   Hinty jsou **povinné** (hlídá `ToolDiscoveryTests.EveryTool_DeclaresItsSideEffects`):
   - `ReadOnly = true` — nástroj nic nemění (typicky `list_*`/`get_*`/`find_*`/`export_*`).
   - `Destructive = true` — nevratná akce (mazání, odeslání e-mailu, vystavení faktury).
     Každý `delete_*` nástroj musí mít `Destructive = true`.
   - `Idempotent = true` — opakované volání se stejnými argumenty nic dalšího nezmění
     (typicky zápisy, které jen nastaví stav — „mark as paid", „approve").
   - `OpenWorld = true` — nástroj mluví s něčím mimo Fakvio (ARES, e-mail, `fileUrl` stahování).
3. Volejte API přes `IFakvioApiClient`; chybí-li endpoint, doplňte ho do
   `Client/IFakvioApiClient.cs` + `Client/FakvioApiClient.cs`.
4. Celé tělo obalte `try` + `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`
   + `catch (Exception ex) { return McpToolError.ToJson(ex); }` (viz stávající nástroje)
   — nikdy vlastní `{ error = ex.Message }`. Případnou deserializaci vstupu dejte do
   samostatného `try` **před** tím hlavním.
5. Aktualizujte tabulku výše a DEVGUIDE §4.9.

## Testy

Unit testy jsou v `Fakvio.Tests.Unit/McpServer/` (mockovaný `IFakvioApiClient`):

```bash
dotnet test Fakvio.Tests.Unit/Fakvio.Tests.Unit.csproj --filter "FullyQualifiedName~McpServer"
```
