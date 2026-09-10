# Fakvio — Release notes

Záznam dokončených změn. **Jeden záznam na každý task, který byl mergnut do
`develop`.** Zapisuje `agent-ops` v okamžiku mergu — viz `.claude/agents/agent-ops.md`
Step 2b. Ručně sem nepiš; když záznam chybí, chybí i merge.

Řazeno **nejnovější nahoře**. Sekce `## Nevydáno` drží to, co je v `develop`, ale
ještě nebylo promováno dál. Řez dělá `/release` (promotion `develop → TEST-ENV`):
sekce se přejmenuje na verzi s datem a nad ní vznikne nová prázdná `## Nevydáno`.
`/release-prod` (`TEST-ENV → master`) už tenhle soubor nemění.

Formát řádku:

    - **#<issue>** — co se změnilo a proč to zajímá čtenáře. (PR #<pr>, `<commit>`)

Píše se **dopad, ne diff**. „Opraveno `FindAsync` bez `Include`" nikomu nic neřekne;
„splatnost faktury ignorovala nastavení klienta a vždy použila 14 dní" ano.

---

## Nevydáno

## 2026.09.10.4 — 2026-09-10

### Změny
- **#423** — Tailscale tunel k databázi odstraněn: App Service má pevnou sadu outbound IP, PostgreSQL je dostupný přímo (TLS, allowlist 19 IP na firewallu a v `pg_hba.conf`). Pryč je i startup gate (`StartupGateMiddleware`, pole `startupDatabaseReady` v health) a `RetryAfterHandler` v UI; migrace masteru běží synchronně před prvním requestem. Produkce přepnuta 2026-09-10 večer. (PR #423)
- **#422** — delší retry ve verify krocích deploy workflow (první start na B1 trvá až 7 min); docs: `WEBSITES_CONTAINER_START_TIME_LIMIT=900`. (PR #422)

## 2026.09.10.3 — 2026-09-10

### Změny
- **#419** — hosting API a MCP HTTP hostu přesunut z Azure Functions (Flex Consumption) na Azure App Service (Linux B1, plan `asp-fakvio-b1`): jeden host `Fakvio.API`, projekty `Fakvio.Functions` a `Fakvio.Functions.Generator` smazány, Tailscale tunel a startup gate běží v API hostu, nový `ReminderWorker` (denní upomínky 06:00 UTC) nahrazuje timer trigger, `McpKeepAlive` odstraněn (Always On). Nové adresy: API `https://fakvio-api.azurewebsites.net`, test `https://fakvio-api-test.azurewebsites.net`; MCP web appky `fakvio-mcp-web(-test)`. (PR #419, `07ea98d`)

## 2026.09.10.2 — 2026-09-10

### Opravy
- **#416** — uvítání asistenta pro nedokončené nastavení (#214) se nově odesílá jako součást první odpovědi konverzace (`SeedAssistantMessage`), takže asistent má kontext a může se efektivně ptát na chybějící údaje; prompt nyní jmenuje konkrétní chybějící pole (např. `NUMBER_SEQUENCE_MISSING: Invoice, CreditNote`) místo generických kódů. (PR #416, `6898311`)
- **#364** (bezpečnostní oprava) — `UserDto` v odpovědi na listovací endpointy (`GET /api/user`, `/api/user/paged`, `/api/user/{id}`) nesl syrový `InvitationToken`, a protože `POST /api/user/set-password` ten token přijímá anonymně, šlo o přihlašovací údaj viditelný komukoli přihlášenému ve firmě. Navíc forgot-password recykluje stejné pole, takže obyčejný `User` mohl počkat, až si Admin vyžádá reset hesla, a účet mu převzít. Token je teď pryč z `UserDto` úplně; pozvánka ho vrací na vlastním `InvitedUserDto` (jen server-side, do e-mailu) a nová úzká cesta `GET /api/user/{id}/invitation-token` (jen `Admin`/`SysAdmin`) slouží pro obnovení pozvánkového odkazu. (PR #365, `18810d7`)

## 2026.09.10 — 2026-09-10

### Opravy
- **#375** — za studena nastartovaná Function appka krátce po startu vracela na požadavky chybu vypadající jako rozbité DB připojení (tunel do databáze byl ve skutečnosti v pořádku, jen ještě nedoběhl); teď appka během startu odpoví `503` s automatickým opakováním na klientovi místo toho, aby request spadl do neexistujícího portu a skončil chybou. (Konfigurace PgBouncer na DB hostu, skutečná příčina zbylých výpadků, zůstává otevřená jako ruční krok správce.) (PR #407, `2b9a324a`)

## 2026.09.08 — 2026-09-08

Tyhle záznamy se do produkce dostaly už s release PR #406 (2026-09-08), ale sekce
`## Nevydáno` se tehdy nepřejmenovala — datum je doplněné zpětně podle mergu #406.

### Opravy
- **#363** — při kopírování úryvku „remote" pro připojení MCP HTTP klienta se uživateli zkopírovala adresa API (`{ApiBaseUrl}/mcp`), která neexistuje — MCP HTTP host je oddělený deploy od API (od #240/#241). URL nyní vychází z nového `McpSettings:BaseUrl` config klíče; dokud se host nevystaví, zůstává prázdný a snippet místo chybné adresy zobrazí jasný placeholder. Opravena i chybová zpráva v `Fakvio.McpServer/Program.cs`, která zmínila jen JWT místo doporučeného API klíče. (PR #394, `2e2849d`)
- **#306** — jazykové nastavení klienta se tichou chybou ignorovalo — MCP `UpdateClient` přijal parametr `language`, hlásil úspěch a nic neuhnul. Nově se nastavení aplikuje korektně v `CreateClientAsync` i `UpdateClientAsync` (nula = neměnit, jako u ostatních polí). (PR #383, `ecd7b0e6`)
- **#279** — MCP server nově sanitizuje těla chyb API předtím, než je pošle externím klientům, takže neúmyslně nezuniknou interní detaily jako stack trace nebo SQL chyby. Zrušené requesty nyní správně propagují místo toho, aby se konvertovaly na falešné doménové chyby. (PR #381, `28470c96`)

- **#283** — chat tooly `create_invoice` a `create_received_invoice` tichou chybou zaměňovaly chybějící výchozí sazbu DPH nulou a nekladné množství jedničkou. U neplátců DPH bylo to správné chování (0 % je legitimní sazba), ale `create_invoice` to špatně aplikoval i na plátce, kde by selhalo teprve v service s vágní hláškou o povinnosti sazby. Nově se guard podmíňuje statusem plátcovství: plátce bez výchozí sazby dostane čitelnou zprávu a možnost ji nastavit; neplátce fakturu vytvoří se správnou nulou. `create_received_invoice` zůstává bezpodmínečný (přijaté doklady jsou cizího původu). (PR #382, `e66cca8`)
- **#257** — MCP server měl default `FAKVIO_API_URL` na `7001`, ale lokální API dle launchSettings běží na `7047`; default proto nikdy nefungoval bez explicitního nastavení env proměnné. Default je nyní `7047` shodný s API profilu; detaily zkopírované do kódu a README se teď hlídají testem, aby se nedriftovaly znovu. (PR #378, `c86de66`)
- **#301** — chat asistent v přehledu a importu přijatých faktur odmítl data zadaná jedním číslem (`import_invoice` navíc tiše místo chybného data použil výchozí): např. „15.3.2026" se neakceptovalo, jen „15.03.2026". Nový jednolitý parser (`ChatToolDates.TryParseOptional`) podporuje obě podoby, jak je ostatní nástroje už dělají (`create_invoice`, `get_vat_report`). Import nečitelného data navíc nově vrací chybu místo tichého defaultního data — v účetnictví (ne ve filtru) je to lepší bezpečnostní chování. (PR #384, `cb1b9a6`)
- **#269** — součet faktury na stránce se nyní sčítá jednotlivě pro každou měnu — smíšená CZK/EUR stránka místo jednoho nesmyslného čísla (`12600`) vypíše správně `12 100,00 CZK; 500,00 EUR`. (PR #379, `68dda99`)

### Změny pro vývojáře

- **#242** — dokumentace pro API klíče a vzdálený MCP transport je nyní kompletní: USERGUIDE §20 pokrývá vytvoření klíče, volbu režimu (stdio/HTTP) a připojení AI klienta; ADMINGUIDE §9 vysvětluje SysAdminovi bezpečnostní model a správu klíčů; DEVGUIDE §4.9 dokumentuje všech 37 MCP nástrojů a autentizační architekturu. Obě režimy jsou nyní popsány a testy v `ChatToolCatalogSchemaTests` kontrolují, že změny v kódu jsou reflektovány i v dokumentaci. (PR #362, `62c55d0`)

## 2026.08.31 — 2026-08-31

### Změny pro uživatele

- **#214** — přihlásíte-li se s nedokončeným nastavením firmy (chybí sídlo, IČO/DIČ, bankovní
  účet nebo číselná řada), AI asistent v chatu se teď sám ozve jako první — otevře se panel
  s uvítáním a nabídkou pomoct to doplnit. Ptá se po jednom údaji a rovnou ho zapisuje, není
  potřeba přepínat do formulářů. Ozve se jen jednou za přihlášení (ne po každém obnovení
  stránky) a jakmile je nastavení kompletní, mlčí. (PR #358, `14e4a77`)
- **#210** — dashboard místo statického „Rychlý start" teď ukazuje živý přehled, co firmě ještě chybí k vystavení faktury — položky jsou rozdělené na blokující a doporučené (ne jen barvou, i nadpisem), každá vede přímo tam, kde se dá doplnit. Kartu jde tlačítkem „Připomenout později" sbalit na jeden řádek, ale nezmizí natrvalo — jakmile něco chybí, po dalším přihlášení se zase ukáže sama. (PR #348, `31bf00d`)

### Opravy
- **#279** — MCP server nově sanitizuje těla chyb API předtím, než je pošle externím klientům, takže neúmyslně nezuniknou interní detaily jako stack trace nebo SQL chyby. Zrušené requesty nyní správně propagují místo toho, aby se konvertovaly na falešné doménové chyby. (PR #381, `28470c96`)

- **#371** — Function App na Tailscale tunelu se v tailnetu hlásil vždy jako `fakvio-func`, takže prod a test uzel nešly v Tailscale konzoli ani v ACL rozlišit. Jméno teď dává App Setting `TAILSCALE_HOSTNAME` (výchozí `fakvio-func-prod`); TEST-ENV musí mít `fakvio-func-test`. (PR #371, `87699a2`)
- **#239** (bezpečnostní oprava) — MCP server (`Fakvio.McpServer`) posílal na každé volání API startupem zachycený token procesu místo tokenu volajícího uživatele; pod HTTP hostingem by to znamenalo, že tool cally jednoho uživatele nesou přihlašovací údaje jiného (cross-tenant leak). Autorizace teď jde per request přes `AuthHeaderHandler`. Ve stdio režimu (aktuální provoz) se chování nemění. (PR #343, `1f98b22`)

### Změny pro vývojáře

- **#241** — nový CI workflow `mcp-server.yml`: balí a publikuje `Fakvio.McpServer` (stdio distribuce) a nasazuje HTTP host. Deploy krok je zatím neaktivní — čeká na ruční založení Azure Web App (vlastní App Service plán, Flex Consumption plán Functions ho hostovat nemůže) a nastavení repo proměnné `MCP_HTTP_APP_NAME`; do té doby merge nic nenasazuje. (PR #360, `e67a5e3`)
