# Fakvio — Release notes

Záznam dokončených změn. **Jeden záznam na každý task, který byl mergnut do
`develop`.** Zapisuje `agent-ops` v okamžiku mergu — viz `.claude/agents/agent-ops.md`
Step 2b. Ručně sem nepiš; když záznam chybí, chybí i merge.

Řazeno **nejnovější nahoře**. Sekce `## Nevydáno` drží to, co je v `develop`, ale
ještě nebylo promováno na `master`. Při `/release` se přejmenuje na verzi s datem
a nad ní vznikne nová prázdná `## Nevydáno`.

Formát řádku:

    - **#<issue>** — co se změnilo a proč to zajímá čtenáře. (PR #<pr>, `<commit>`)

Píše se **dopad, ne diff**. „Opraveno `FindAsync` bez `Include`" nikomu nic neřekne;
„splatnost faktury ignorovala nastavení klienta a vždy použila 14 dní" ano.

---

## Nevydáno

### Opravy

- **#271** — AI chat asistent odmítl datum zadané jedním číslem (např. „15.3.2026")
  v přehledu faktur i v DPH reportu — bral jen dvouciferné tvary s nulou (`15.03.2026`).
  Nově akceptuje obě podoby, padded tvary i ISO datum se chovají stejně jako dřív.
  (PR #296, `46fad72`)
- **#263** — Diagnostické endpointy Azure Functions (`/api/diagnostic/migrate`,
  `/api/diagnostic/auth`) byly dostupné bez přihlášení: kdokoli mohl vzdáleně spustit
  DB migrace nebo si vypsat JWT konfiguraci (issuer, audience, délku secretu, claims).
  Oba teď vyžadují přihlášeného SysAdmina — bez tokenu 401, s tokenem bez role 403.
  (PR #270, `4f766df`)
- **#200** — Ověření reCAPTCHA bylo fail-open: výpadek Googlu nebo chybějící `SecretKey`
  bránu tiše propustily místo aby ji zablokovaly, a token se navíc nekontroloval proti
  akci ani doméně, takže se dal token z jednoho formuláře přehrát jinam. Anonymní ARES
  lookup navíc dával neomezeně rostoucí cache. Brána je nově fail-closed a ověřuje
  action i hostname, cache ARES se čistí při každém zápisu. **Vyžaduje zásah v produkci**
  (prázdný `SecretKey`/`SiteKey`) — viz ADMINGUIDE.md §9. (PR #246, `f968946`)
- **#233** — Swagger UI i `swagger.json` byly dostupné na rootu API i mimo Development,
  takže je při nasazení na klasický App Service host mohl vidět kdokoli. Nově se
  registrují jen v Development; v Production vrátí `/` i `/swagger/v1/swagger.json` 404.
  (PR #247, `329c588`)
- **#192** — Pozvání dalšího uživatele do už zavedené firmy znovu spouštělo provisioning
  tenanta, který u živého tenanta mazal a přečísloval sazby DPH, měny, číselné řady
  šablon i obsahové šablony — vystavené doklady tak mohly tiše ztratit vazbu na svou
  původní sazbu DPH nebo měnu. Nastavení hesla pozvaného uživatele už provisioning
  zavedené firmy nespouští. (PR #245, `074229d`)
- **#157** — Při registraci nové firmy se adresa dohledaná v ARES nikam neuložila —
  firma vznikla bez sídla a vystavitel v tenantu neměl adresu vůbec, takže PDF faktury
  měl prázdný blok vystavitele. Adresa z ARES se teď uloží a propíše do tenanta;
  registrační formulář ji navíc rovnou předvyplní, aby šla před odesláním zkontrolovat
  a opravit. (PR #173, `cc55ed9`)
- **#158** — Když k DPH exportu chyběly EPO údaje (kód finančního úřadu, kontaktní osoba…),
  `VatReport` poslal uživatele doplnit je na `/my-company` — stránku, kde tahle pole vůbec
  nebyla. Sekce je tam nyní pro Admina a SysAdmina; běžný uživatel dostane rovnou pokyn
  požádat administrátora, místo aby skončil na stránce bez řešení. (PR #176, `4219589`)
- **#156** — Chat posílal uživateli do prohlížeče celý stack trace serverové výjimky.
  Nyní dostane jen bezpečnou hlášku s referenčním ID, podle kterého se chyba dohledá
  v `/logs`. Neznámá konverzace vrací 404 místo 500. (PR #172, `f6a4f37`)
- **#155** — Když číselná řada chyběla nebo byla vypnutá, doklad tiše dostal náhradní
  číslo `INV2026001` mimo řadu. Nově se akce nedokončí a uživatel se dozví, co doplnit.
  Souběžná kolize čísel se hlásí jako přechodná s výzvou akci zopakovat. (PR #171, `d3c9b4c`)
- **#153** — Při zakládání firmy se vystaviteli nezkopíroval bankovní účet ani fakturační
  nastavení. (PR #170, `aa6a26c`)
- **#134** — Každé zřízení tenanta nechávalo otevřené spojení do databáze; při rušení
  schématu se navíc neuvolnil jeho datový zdroj. (PR #169, `4f75e39`)

### Změny pro vývojáře

- **#290** — Testovací prostředí `TEST-ENV` má teď vlastní deploy pro Azure Functions
  backend: push do větve `TEST-ENV` nasadí `Fakvio.Functions` do samostatné aplikace
  `zcloudinvoicingapi-test` (Flex Consumption, deployment slot tu není podporovaný),
  produkční deploy z `master` zůstal beze změny. Umožňuje ověřit release proti testovacímu
  backendu dřív, než jde na produkci. (PR #300, `dfab4a7`)
- **#138** — Health endpoint teď hlásí, jaký režim přihlášení k databázi (`authMode`,
  `authModeSource`) skutečně používá — v obou hostech, API i Azure Functions, poprvé
  stejně (API dosud žádný health endpoint nemělo). Umožňuje ověřit rollout přepínatelné
  DB autentizace bez čtení connection stringu; secret se do odpovědi nikdy nedostane.
  (PR #260, `17475ad`)
- **#236** — Integrace (MCP server, budoucí externí nástroje) se teď mohou přihlásit
  API klíčem místo běžného uživatelského přihlášení: klíč se pošle v hlavičce
  `Authorization: ApiKey <klíč>` a nese vlastní scope (které akce smí), takže
  nevyžaduje sdílené heslo ani plný přístup uživatele. Funguje shodně v API
  hostu i v Azure Functions. (PR #275, `bd4ffb3`)
- **#209** — Nový endpoint `GET /api/readiness` (i jako Azure Function) vrací, co ve
  vystaviteli ještě chybí k vystavení faktury (sídlo, číselné řady, šablony) — základ pro
  banner v UI (#215) a pro MCP/chat nástroje, které se teď mají o co opřít místo vlastní
  logiky. (PR #266, `824aaec`)
- **#228** — AI asistent v chatu teď umí nahlásit stav dashboardu (cashflow, neuhrazené a
  po splatnosti částky, top klienti), vypsat vydané faktury a dobropisy s filtry podle
  stavu, klienta, období nebo splatnosti a spočítat DPH report za zadané období — dřív
  musel uživatel tyhle přehledy hledat v UI ručně. (PR #259, `c3c591f`)
- **#212** — Zápisové nástroje AI chatu (zatím žádný neexistuje, ale #217/#218/#220/#222/
  #224/#225/#227 na tomhle základu staví) budou mít jednotný potvrzovací mechanismus: bez
  parametru `confirm: true` se zápis vůbec nespustí a model dostane jen náhled toho, co by
  se stalo. Gate je centrální (`ChatToolExecutor`), takže funguje stejně napříč všemi
  4 AI providery i textovým i nativním tool-calling flow — implementátor jednotlivého
  nástroje ho nemusí řešit sám. DEVGUIDE §4.7 dostal i paritní tabulku chat ↔ MCP nástrojů,
  aby bylo vidět, kolik nástrojů z MCP serveru chat ještě nepokrývá. (PR #258, `b91a94c`)
- **#230** — AI asistent v chatu dosud odpovídal bez ponětí, kde uživatel zrovna je:
  neznal dnešní datum, aktuální stránku ani otevřený doklad, a o nedokončeném nastavení
  firmy (chybějící sídlo, číselné řady, šablony) nevěděl vůbec. Prompt teď dostává
  poslední blok se situačním kontextem — datum, aktuální stránka, otevřený záznam a
  seznam blokujících mezer v nastavení firmy s odkazem, kde je doplnit — takže asistent
  může reagovat na to, co uživatel právě dělá, místo obecné odpovědi naslepo.
  (PR #265, `640f7c6`)
- **#206** — Vystavení faktury nebo dobropisu (ruční i hromadné, i automatické z
  šablony) teď nejdřív ověří, že má vystavitel vyplněné povinné údaje (adresa, IČO,
  bankovní účet, aktivní číselné řady). Když ne, vystavení se odmítne se srozumitelnou
  chybou a seznamem chybějících položek — doklad zůstane rozpracovaný, nespotřebuje
  číslo z řady a nic se neuloží napůl. Chybějící EPO nastavení vystavení neblokuje,
  jen upozorní. (PR #267, `9ac56b6`)
- **#235** — Základ pro strojový přístup do API bez lidského uživatelského účtu
  (SysAdmin nástroje, budoucí Remote MCP). Přibyl `ApiKey` (v master schématu — autentizace
  ho musí najít dřív, než zná tenanta): pojmenovaný klíč se scope read/read+write a
  volitelnou expirací, raw hodnota se vrátí jen jednou, DB drží pouze SHA-256 hash
  (vědomá odchylka od BCrypt — klíč je 32 B z CSPRNG, adaptivní hash by jen zbytečně
  zatížil CPU), výpis ukazuje jen prefix. Klíč lze soft-revokovat. CRUD dostupný v obou
  hostech (API controller i ručně psaný Functions wrapper). Zatím jen správa klíčů —
  přihlašování pomocí nich přidají navazující tasky. (PR #256, `b9b8066`)
- **#229** — AI chat asistent uměl v aplikaci navigovat jen na 6 natvrdo napsaných
  stránek. Nyní zná všech 28 stránek dostupných běžnému uživateli i firemnímu
  administrátorovi (např. přehled DPH, upomínky, číselné řady, uživatelé) včetně
  detailu konkrétního klienta — přihlašovací a čistě sysadminovské stránky zůstávají
  mimo dosah. Katalog cílů je odvozený přímo z routovací tabulky UI, takže nová
  stránka bez navigačního cíle spadne na testu, dokud ji někdo nedoplní.
  (PR #262, `c8edf27`)
- **#161** — AI asistent byl v aplikaci prakticky neviditelný: tlačítko v AppBaru
  splývalo s logem firmy, stav otevření se po refreshi nikdy nezapamatoval a nikde
  jinde na chat nevedl odkaz. Na mobilu zabíral drawer napevno celou obrazovku a
  chyběl mu CSS, takže vypadal rozbitě; markdown v odpovědích modelu (seznamy, tučné
  písmo, tabulky) se zobrazoval jako syrový text místo naformátovaný. Nově má ikonu
  robota na první pozici v AppBaru, položku v hlavním menu, pamatuje si otevření/zavření
  per zařízení, je responzivní na mobilu a odpovědi renderuje jako markdown (bezpečně
  sanitizovaný i proti odkazům typu `<javascript:…>`). Smazání konverzace teď vyžaduje
  potvrzení. (PR #185, `64d4508`)
- **#205** — Chybějící nastavení tenanta (adresa vystavitele, IČO, DIČ u plátce DPH,
  bankovní účet, číselná řada dokladu…) se dosud řešilo náhodně a nekonzistentně —
  jediný existující precedens byla EPO hlavička u DPH exportu. Nová
  `ITenantReadinessService` na jednom místě odpoví, jestli tenant má dost nastavení
  na fakturaci, s výčtem konkrétních chybějících položek a odkazem, kde je doplnit;
  je to základ, na kterém teď staví gate ve vystavování faktur (#206), REST endpoint
  (#209), chat/MCP nástroj (#211) a UI karta (#215). (PR #251, `6b978cf`)
- **#234** — MCP server (`fakvio-mcp`, 36 nástrojů pro AI klienty typu Claude Code/Desktop)
  neměl žádnou dokumentaci, takže napojení vlastního AI klienta vyžadovalo číst zdrojový
  kód. Nový `Fakvio.McpServer/README.md` popisuje build, spuštění, získání JWT tokenu
  a napojení klienta; `.mcp.json.sample` je copy-paste vzor konfigurace (reálný `.mcp.json`
  nese token v plaintextu, verzuje se jen vzor). DEVGUIDE §4.9 opraveno z 21 na
  aktuálních 36 nástrojů, USERGUIDE dostal novou kapitolu 20 pro koncového uživatele.
  (PR #250, `4dd6d6a`)
- **#232** — Šest AI/MCP nástrojů pro přijaté faktury (výpis, detail, založení, schválení,
  označení uhrazeno, smazání) nemělo jediný test, takže regrese v chování AI asistenta
  by prošla nepovšimnutá. Nově 21 mutačně ověřených testů; vedlejším zjištěním je
  nekrytý `PaginationParams` — základní třída všech stránkovaných filtrů v repu —
  který teď dostal vlastní 8 testů na clamping stránky/velikosti. (PR #248, `4f0d2a1`)
- **#208** — Registrace firmy z ARES ukládala jen název a IČO/DIČ, sídlo se zahazovalo
  a plátcovství DPH se nikdy nenastavilo, přestože ho ARES prozradí (DIČ přítomno).
  Adresa z ARES se teď uloží do klienta a `IsVatPayer` se odvodí z přítomnosti DIČ;
  registrační formulář se neměnil. (PR #249, `12b301c`)
- **#140** — Přesun produkční databáze z Azure PostgreSQL (Entra ID) na vlastní
  server dosud neměl žádný ověřený postup. Nový `SELFHOST-DB.md` runbook popisuje
  celý přesun krok za krokem: pre-flight kontroly, cutover přes `pg_dump`/`pg_restore`,
  ověření dat po schématech i migrace Data Protection key ringu, a rollback zpět na
  Azure, kdyby přesun nevyšel. (PR #244, `e68281a`)
- **#159** — Popis parametrů chat nástrojů pro AI asistenta byl natvrdo zadrátovaný `switch`
  podle názvu nástroje; nástroj, na který se ve switchi zapomnělo, tiše dostal jediný
  parametr `input` a nefungoval bez jakékoli chybové hlášky. Popis parametrů je teď
  strukturované schéma na každém nástroji (typ, povinnost, povolené hodnoty) a switch
  i tři ručně udržované kopie katalogu nástrojů (systémový prompt, definice pro model,
  kontext chatu) zmizely — generují se z jednoho zdroje. Nástroj s vadným schématem
  spadne hlasitě už při startu, ne tiše za běhu. Typy parametrů navíc přestaly být
  jen `string` (čísla, booleany a pole se posílají jako svůj typ, ne jako escapovaný
  text). Připravuje podklad pro nativní tool calling u OpenAI/Gemini (#160).
  (PR #188, `4b6c3be`)
- **#136** — `Fakvio.MigrationTool` a `DataIntegrityVerifier` mluvily s oběma databázemi přes
  syrové connection stringy, takže je nešlo přepnout na Entra ID auth zavedené v #132. Nástroj
  teď staví dva nezávislé data source factory (cíl i zdroj) a přebírá i přísnější sanitizaci
  a izolaci `search_path` tenant schématu. Nástroj se nedeployuje, takže bez dopadu na provoz.
  (PR #166, `1ce837d`)
- **#146** — Systémový prompt AI asistenta byl napevno zadrátovaný v kódu. SysAdmin ho teď může
  upravit (vlastní prompt i doplněk k výchozímu) přímo v UI se živým náhledem; změna se v chatu
  projeví do 5 minut (cache). (PR #187, `824775d`)
- **#46** — API pro položky faktury nově rozlišuje režim DPH (`EVatRegime`) a u přenesené daňové
  povinnosti nese kód předmětu plnění (`ReverseChargeCodeId`), včetně čtecího nested DTO. Nový
  endpoint `GET /api/reversechargecode` vrací číselník PDP kódů pro dropdown v editoru položek.
  Připravuje podklad pro sekce A.1/B.1 kontrolního hlášení. (PR #96, `5921db5`)
- **#178** — Výstupy source generátorů pod `Generated/` se přestaly verzovat. Každý build
  je přepisoval a vyráběl fantomové diffy. (PR #182, `eff1f5b`)
- **#137** — Čtyři testy připojení k databázi, které padaly v každém běhu, jsou nově za
  bránou `FAKVIO_DB_SMOKE=1`. Lokální běh je poprvé zelený. (PR #168, `eedfeb9`)
- **#135** — `dotnet ef` funguje proti Azure přes design-time factory. (PR #167, `b34e53f`)
- **#133** — Composition root staví databázová spojení přes `INpgsqlDataSourceFactory`,
  což je základ pro přepínání mezi Entra ID a heslem. (PR #143, `ad0cd1f`)
- **#132** — `DatabaseOptions` + `INpgsqlDataSourceFactory`. (PR #142, `a88e6e4`)
