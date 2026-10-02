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

### Nové funkce

- **#491** — Kopie faktury umí posunout období v textech („Hosting 3/2026“ → „Hosting 4/2026“, „březen 2026“ → „duben 2026“, čtvrtletí, přelom roku) — volba v dialogu kopie. U opakovaných faktur se posun zapíná přepínačem „Posouvat období v textech“ (nové plány zapnuto, stávající beze změny). (PR #491, `6cd8ffc`)
- **#492** — Import bankovních výpisů ve formátu GPC/ABO (KB, ČSOB, Fio, Raiffeisen, MONETA…) na stránce Platby: platby se založí bez duplicit (i vůči platbám už načteným z bankovních e-mailů) a hned se spárují s vydanými i přijatými fakturami. (PR #492, `6e1dbfb`)
- **#488** — Přiznání k DPH (ř. 1/2/25), přehled DPH a kontrolní hlášení (A.1, A.4/A.5) nově započítávají dobropisy záporně v období jejich DUZP — dřív se ignorovaly a DPH vycházela vyšší. O zařazení do A.4/A.5 rozhoduje absolutní hodnota opravy (dle FAQ Finanční správy). (PR #488, `1f0121f`)
- **#489** — Ukládání časů do databáze sjednoceno na UTC jedním převodníkem pro obě databáze (dosavadní normalizace u hodnot bez časové zóny nedělala nic); v produkci beze změny, na vývojových strojích zmizí posun o časové pásmo. (PR #489, `d34d664`)

## 2026.10.02 — 2026-10-02

### Nové funkce

- **#478** — Faktury v EUR s IBAN nesou SEPA QR kód (EPC) místo české QR Platby, takže je zaplatí i zahraniční bankovní aplikace (nulové a záporné částky dál používají QR Platbu). U klienta lze DIČ ověřit ve VIES tlačítkem „Ověřit ve VIES“, přes chat i MCP (verify_vat_vies). (PR #478, `0638a1f`)
- **#480** — Zálohové faktury: po zaplacení zálohy (ručně i spárováním z banky) plátci DPH automaticky vznikne daňový doklad k přijaté platbě — i při částečné platbě, bez duplicit, s DUZP = den platby (lze vypnout v Moje firma). MCP umí vystavit zálohu, vyúčtovací fakturu (issue_final_invoice), DPP (issue_tax_receipt) a zjistit zbývající zálohu. (PR #480, `865e813`)
- **#483** — EPO: přiznání k DPH a kontrolní hlášení nově zahrnují přenesenou daňovou povinnost (DP3 ř. 10/11, 25, 43/44; KH A.1/B.1) včetně přijatých faktur v režimu PDP; přibylo souhrnné hlášení (DPHSHV) pro plnění do EU s náhledem a volbou zboží/služby, navazující řádky 20/21 přiznání a MCP nástroj export_vat_epo. (PR #483, `a2bee8e`)
- **#484** — Režim EU OSS: plátce registrovaný k OSS (nastavení v Moje firma) může u faktury spotřebiteli z jiného státu EU zaškrtnout „Režim OSS“ — položky pak nesou sazbu DPH země odběratele, PDF to uvádí a faktura se nezapočítá do českého přiznání ani KH. Nový čtvrtletní přehled „OSS hlášení“ (CSV, MCP get_oss_report) přepočítává cizí měny kurzem ECB podle pravidel OSS. Sazby EU jsou v číselníku, který spravuje SysAdmin. (PR #484, `dea3ffb`)
- **#479** — Export do účetnictví: vydané i přijaté faktury za zvolené období jdou stáhnout jako XML pro POHODA, Money S3 a ABRA Flexi (dialog „Export do účetnictví“ v seznamu faktur i přes MCP). Formáty jsou ověřené proti oficiálním schématům; doklady, které cílový systém neumí přijmout (např. cizí měna v Money/Flexi, nepodporovaná sazba), se přeskočí a export řekne kolik. (PR #479, `4fd5c4e`)
- **#482** — Webhooky: v Nastavení → Webhooky si správce firmy zaregistruje URL, na kterou Fakvio posílá podepsané (HMAC-SHA256) události — vytvoření, odeslání, zaplacení a zrušení faktury, nová přijatá faktura a přijatá platba; s opakováním při výpadku, testovacím pingem a logem doručení. Ochrana proti SSRF (jen veřejné HTTPS adresy). (PR #482, `eb639f1`)
- **#476** — Přenesená daňová povinnost (§92a) jde nastavit přímo v editoru položek — sloupec „Režim DPH“, „Kód PDP“ a přepínač pro celou fakturu; PDF nese povinnou poznámku „Daň odvede zákazník“ s kódy plnění, ISDOC posílá LocalReverseCharge a MCP/chat přijímají režim i kód (nový nástroj list_reverse_charge_codes). (PR #476, `aedec06`)
- **#481** — Nástěnka je modulární: přes „Upravit nástěnku“ si každý uživatel zapne, vypne a seřadí moduly; přibyly grafy tržeb po měsících, příjmů vs. výdajů a stáří neuhrazených pohledávek (jen faktury v CZK). Nový průvodce prvním nastavením (/setup) provede správce firmou, bankovním účtem, číselnými řadami a pozváním kolegů. (PR #481, `58c2ecc`)
- **#477** — Nová faktura (z aplikace, MCP, chatu, šablony i opakované faktury) bez zadaného účtu automaticky dostane výchozí bankovní účet firmy, přednostně v měně faktury; přes MCP lze účet zvolit (`bankAccountId`) i dodatečně změnit (`set_invoice_bank_account`). Opraveno míchání čísla účtu a IBAN ze dvou různých účtů při vytvoření ze šablony. (PR #477, `d83e2e5`)

### Změny pro vývojáře

- **#472** — Ve Fakviu je návod na připojení ChatGPT krok za krokem, včetně odkazu do nastavení aplikací a adresy MCP serveru; správce najde samostatný postup pro první nastavení. (PR #473, `4a9b4f1`)
- **#468** — Připojení webového ChatGPT k Fakviu přes OAuth přijímá jeho registrační metadata a vrací oprávnění ve správném formátu; dokončení přihlášení a obnovení přístupu tak může fungovat. (PR #467, `0abdc55`)

## 2026.10.01 — 2026-10-01

- **#456** — Faktury, dobropisy a daňové doklady k záloze jdou stáhnout jako e-faktura UBL / Peppol BIS 3.0 (vedle ISDOC) — jednotlivě, hromadně i přes MCP; slovenským odběratelům se UBL přikládá k e-mailu. Příprava na povinnou e-fakturaci na Slovensku od 2027 (odesílání přes Peppol přijde ve fázi 2). (PR #456, `499e029`)
- **#455** — Přijaté faktury umí importovat e-faktury UBL / Peppol BIS (XML) a ISDOC — z e-mailu i ručním nahráním; strukturovaná data se převezmou přesně, bez odhadu z PDF. (PR #455, `e04bacc`)
- **#454** — Opakované faktury jdou spravovat i z AI přes MCP (výpis, založení, úprava, pozastavení, obnovení, smazání plánu); MCP server 2.2.0. (PR #454, `3e7340b`)
- **#448** — MCP server umí přihlášení přes OAuth 2.1 (claude.ai / ChatGPT konektor jen adresou + souhlasem, volba čtení vs. zápis, správa „Připojených aplikací“, změna hesla odpojí aplikace). Za přepínačem `McpOAuth:Enabled`, výchozí vypnuto — bez nastavení se nic nemění. (PR #448, `8695c52`)

## 2026.09.29 — 2026-09-29

- **#452** — Neplátce DPH už nedostává DPH na faktury: formulář předvyplňoval 21 % a server sazbu převzal. Teď se u neplátce DPH vždy vynuluje (UI, MCP, šablony, opakované faktury), formulář i detail DPH nezobrazují a PDF nemá sloupec DPH, rekapitulaci, „Daňový doklad“ ani DUZP — místo toho „Dodavatel není plátcem DPH.“ Starší koncepty se opraví uložením, vystavené faktury vyžadují dobropis. (PR #452, `960b28d`)
- **#451** — Publikace MCP serveru na nuget.org znovu funguje (rozbitý řádek ve workflow), takže vyjde `Fakvio.McpServer` 2.1.0. (PR #451, `417d631`)

## 2026.09.28.2 — 2026-09-28

- **#449** — Produkční UI má reCAPTCHA v3 site key, takže přihlášení, registrace, ARES lookup a zapomenuté heslo posílají token. Ověřování na serveru se zapíná až po nasazení (`Recaptcha__Enabled`). Řádek doplněn ručně při release — PR #449 byl mergnut mimo agent-ops. (PR #449, `c9580e0`)

## 2026.09.28 — 2026-09-28

- **#445** — Balíček `Fakvio.McpServer` se na nuget.org publikuje přes Trusted Publishing (OIDC, bez uloženého klíče); dřív publikace kvůli chybějícímu klíči tiše přeskakovala a na nuget.org zůstala verze 1.0.2. (PR #445, `9d7c9da`)
- **#444** — Opakované faktury: plán automaticky vystaví (a volitelně odešle) fakturu podle šablony; trvale selhávající plán notifikuje jen při změně chyby. (PR #444, `2d89b73`)
- **#444** — Hromadný import klientů z CSV s náhledem; příliš dlouhé hodnoty jsou označené jako neplatné, jeden vadný řádek už neshodí zbytek importu, duplicitní IČO se přeskočí. (PR #444, `2d89b73`)
- **#444** — Přihlášení, 2FA, reset hesla a registrace mají rate limit podle IP; reCAPTCHA zpevněna na přihlášení a zapomenutém hesle. (PR #444, `2d89b73`)
- **#444** — MCP server 2.1.0 (breaking): typované vstupy, anotace nástrojů, nové nástroje (měny, číselné řady, sazby DPH, firma, bankovní účet, platby, upomínky); `update_number_sequence` a `update_my_company` vyžadují potvrzení. (PR #444, `2d89b73`)

## 2026.09.20 — 2026-09-20

### Změny
- **PR #441** — AI asistent zmizel z UI: pryč ikona v horní liště, položka v menu, boční chat panel, uvítání po přihlášení i odkaz na AI instrukce. AI se používá přes MCP; backend zůstal. (PR #441, `9df3444`)
- **PR #442** — po importu faktur se už nespouští AI kontrola s hláškou „AI review failed". (PR #442, `a6c7675`)

## 2026.09.14.2 — 2026-09-14

### Změny
- **MCP `upload_received_invoice_attachment`** — soubor se předává jako `fileUrl` (https odkaz, server si ho stáhne; limit 50 MB) nebo `filePath` (jen lokální stdio server); inline `base64Content` zůstává pro malé soubory. Base64 přes model nešlo použít pro reálná PDF (475 KB ≈ 650 k znaků).

## 2026.09.14 — 2026-09-14

### Změny
- **PR #434** — MCP: nový nástroj `upload_received_invoice_attachment` přiloží soubor (typicky PDF dodavatele) k přijaté faktuře; `create_received_invoice` u neplatného vstupu (např. `paymentMethod: "Apple Pay"`) vrátí konkrétní `Invalid JSON format` místo anonymního `internal_error` a popis nástroje vyjmenovává platné způsoby platby. (PR #434, `5d60aad`)

### Opravy
- **PR #412** — `GET /api/company/{id}` při chybě už nevrací klientovi celý `ex.ToString()` (stack trace, interní detaily); chyba se loguje, odpověď je neutrální. (PR #412)

## 2026.09.11.2 — 2026-09-11

### Opravy
- **PR #429** — přehled faktur (UI i MCP `list_invoices`) bez zvoleného řazení ukazuje nejnovější faktury podle data vystavení; dřív řadil číslo dokladu jako text, takže u dvou číselných řad celá jedna řada propadla na konec a „poslední faktury" chyběly. MCP `list_invoices` nově přijímá `sortBy` / `sortDirection`. (PR #429, `bce347f`)

## 2026.09.11 — 2026-09-11

### Změny
- **#426** — testovací prostředí zrušeno (workflow `testenv_fakvio-api.yml`, `blazorui-test-deploy.yml` a job `deploy-http-test` odstraněny; Azure testovací appky smazány); certifikát PostgreSQL serveru je součástí balíčku API (`certs/fakvio-db-server.crt`) → connection string produkce běží s `Ssl Mode=VerifyCA`. (PR #426)

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
