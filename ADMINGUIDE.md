# Fakvio — Administrátorská příručka (ADMINGUIDE)

> Průvodce pro SysAdmin roli. Vysvětluje **kde** a **jak** spravovat systém, tenanty, uživatele a infrastrukturu. Cílová skupina: systémový administrátor.
>
> **MAINTENANCE RULE (povinné):** Pokud přidáváš nebo měníš funkci viditelnou SysAdminovi (nová stránka, nové nastavení, nový log, nový provider…), **musíš aktualizovat odpovídající sekci tohoto souboru**. Bez záznamu PR neprochází review.

---

## Obsah

1. [SysAdmin Dashboard](#1-sysadmin-dashboard)
2. [Správa uživatelů](#2-správa-uživatelů)
3. [Správa firem (tenantů)](#3-správa-firem-tenantů)
4. [Systémové nastavení](#4-systémové-nastavení)
5. [AI poskytovatelé](#5-ai-poskytovatelé)
6. [IMAP + Párování plateb](#6-imap--párování-plateb)
7. [Logy (AppLog)](#7-logy-applog)
8. [Šablony dokumentů (systémové)](#8-šablony-dokumentů-systémové)
9. [Bezpečnost](#9-bezpečnost)
10. [Číselníky — systémové vs tenant kopie](#10-číselníky--systémové-vs-tenant-kopie)
11. [Impersonace tenant firmy](#11-impersonace-tenant-firmy)
12. [Odesílání testovacího emailu](#12-odesílání-testovacího-emailu)
13. [Diagnostika nasazení (health endpoint)](#13-diagnostika-nasazení-health-endpoint)
14. [Prostředí (test vs produkce)](#14-prostředí-test-vs-produkce)

---

## 1. SysAdmin Dashboard

**Stránka:** `/` (úvodní Dashboard bez impersonace)

Když se přihlásíte jako SysAdmin a **nejste** v impersonaci žádné firmy, zobrazí se systémový dashboard.

### KPI karty (všechny navigují na `/companies`)

| Karta | Co zobrazuje |
|-------|-------------|
| Celkem firem | Počet všech registrovaných firem |
| Provisionované | Firmy se zřízeným tenant databázovým schématem |
| Aktivní | Firmy s příznakem IsActive=true |
| Čekající | Firmy registrované ale dosud neprovisionované |

### Poslední logy (Warning + Error)

Tabulka posledních chybových a varování logů ze systému s odkazem „Zobrazit vše" → `/logs`.

### Souhrn logů podle úrovně

Chipy s počty logů pro každou úroveň: Critical / Error / Warning / Information.

---

## 2. Správa uživatelů

**Stránka:** `/users` (přístupná pro Admin i SysAdmin)

### Přehled uživatelů

Grid se sloupci: Email, Celé jméno, Role (chip), Firma, Poslední přihlášení, Stav.

Dostupné akce per uživatel:
- Editovat (tužka)
- Změnit heslo (klíč)
- Smazat (koš)

### Role uživatelů

| Role | Oprávnění |
|------|----------|
| **SysAdmin** | Přístup ke všem stránkám systému, správa firem, logy, systémové nastavení |
| **Admin** | Správa uživatelů v rámci své firmy, stejná fakturační práva jako User |
| **User** | Standardní uživatel — faktury, klienti, šablony, přijaté faktury, přehled DPH |

### Vytvoření nového uživatele

1. Klikněte „+ Nový uživatel"
2. Zadejte email, jméno, příjmení, roli
3. Možnost „Odeslat pozvánku emailem":
   - **Zapnuto:** systém odešle email s odkazem pro nastavení hesla (InvitationToken, platný 48 h). Heslo se nenastavuje ručně.
   - **Vypnuto:** zadejte heslo ručně (hash: BCrypt work factor 12)
4. Přiřaďte firmu (CompanyId)
5. Uložte

### Editace uživatele

Dialog editace umožňuje změnit: email, jméno, roli, přiřazenou firmu, stav (aktivní/neaktivní).

### Resetování hesla

1. V gridu: ikona klíče u uživatele → dialog „Změnit heslo"
2. Nebo: uživatel klikne „Zapomenuté heslo" na přihlašovací stránce → systém pošle email s odkazem (48 h platnost)

**Anti-enumeration:** systém vrátí úspěšnou odpověď i pro neexistující email (útočník nepozná, zda email existuje).

### 2FA (Two-Factor Authentication)

Uživatelé si sami konfigurují 2FA na `/profile/two-factor`.

**TOTP (Google Authenticator / Authy):**
- Systém vygeneruje 20-bytový secret, QR kód pro nastavení v autentikátoru
- Secret uložen zašifrovaný přes Data Protection API

**Email OTP:**
- 6místný kód, platnost 5 minut
- Hashován BCrypt (work factor 12) ve sloupci `User.TwoFactorEmailCode`
- Rate limit: 5 chybných pokusů → invalidace session tokenu

**Reset 2FA:** V případě ztráty přístupu — SysAdmin musí ručně vymazat `User.TotpSecretEncrypted` a `User.TwoFactorEnabled` v databázi (žádné UI pro force-reset zatím neexistuje).

---

## 3. Správa firem (tenantů)

**Stránka:** `/companies` (pouze SysAdmin)

### Přehled firem

Grid se sloupci: Název firmy, IČ, DIČ, Město, Plátce DPH, Stav, Provisioning stav.

Stavy provisionování:
- **Provisioned** (zelená) — firma má databázové schéma + je aktivní
- **Inactive** (šedá) — firma je provisioned ale deaktivovaná
- **Not provisioned** (oranžová) — firma existuje v master DB ale schéma nebylo zřízeno
- **No settings** (outlined) — firma nemá `CompanySystemSettings` záznam

### Vytvoření nové firmy

1. Klikněte „+ Nová firma" → `/companies/create`
2. Zadejte IČ + tlačítko „ARES" pro automatické doplnění z obchodního rejstříku
3. Vyplňte firmu, adresy, kontakty
4. Volitelně: nastavte SMTP pro firmu (v sekci Email Settings)
5. Volitelně: okamžitě vytvořte uživatele a odešlete pozvánku
6. Uložte

Po uložení firmy je potřeba **provisioning** (viz níže).

### Provisioning tenanta

**Kde:** detail firmy `/companies/{id}` → tlačítko „Provision"

Provisioning provede:
1. Vytvoří PostgreSQL schema `tenant_{companyId}`
2. Aplikuje EF Core migrace na nové schéma
3. Zkopíruje systémové číselníky (VatRate, Currency, NumberSequenceFormat, ContentTemplate) z master schématu do tenant schématu
4. Vytvoří záznam vystavitele (issuer) v tenant schématu — včetně adres, kontaktů, bankovních účtů a fakturačního nastavení z master záznamu firmy
5. Vytvoří výchozí číselné řady pro 4 typy dokladů (INV, CN-, PF-, DPP-)
6. Označí firmu jako IsProvisioned=true, IsActive=true

**Kdy provisioning proběhne a kdy ne**

- **Firma ve stavu Not provisioned** (`IsProvisioned=false`) — proběhne celý postup 1-6.
  Sem patří i opakování po chybě: příznak se nastavuje až v kroku 6, takže po neúspěšném
  běhu firma zůstává „nedoprovisionovaná" a další spuštění projde znovu všechny kroky.
- **Firma ve stavu Provisioned** (`IsProvisioned=true`) — **neproběhne nic**. Tlačítko
  „Provision" i automatické spuštění při nastavení hesla skončí bez zásahu do tenant
  schématu a nahlásí úspěch. Důvod: krok 3 číselníky nejdřív smaže a teprve pak nakopíruje
  znovu. U firmy, která už fakturuje, na mazané sazby DPH a měny vedou cizí klíče z faktur,
  takže opakovaný běh by buď spadl, nebo (hůř, protože tiše) přečísloval sazby a měny pod
  už vystavenými doklady.
- **Oprava schématu už provisionované firmy** se dělá migrací (`MigrateTenantAsync`), ne
  opakovaným provisioningem.

**Krok 5 je povinný.** Pokud v tenant schématu není žádný aktivní formát číselné řady
(`NumberSequenceFormat`), provisioning v kroku 5 selže s chybou a firma zůstane
`IsProvisioned=false`. Dřív se krok tiše přeskočil a tenant vznikl úplně bez číselných
řad — jeho faktury pak nešlo očíslovat. Náprava: zkontrolovat master číselník
„Formáty číselných řad" (musí mít alespoň jeden aktivní záznam), pak provisioning
spustit znovu.

**Selhaný provisioning po self-registraci:** provisioning se spouští automaticky i při
nastavení hesla nově registrovaným uživatelem. Když v tu chvíli selže, firma zůstane ve
stavu **Not provisioned** a uživatel dostane na stránce nastavení hesla oranžové upozornění,
že pracovní prostor není připravený (heslo mu ale platí). Takové firmy najdete v přehledu
`/company-settings` se stavem „Ne" ve sloupci Provisioned — provisioning z něj spustíte
znovu tlačítkem ▶. Důvod selhání najdete přímo v aplikaci na stránce `/logs` — vyhledejte
`Tenant provisioning FAILED`; záznam obsahuje krok, na kterém provisioning spadl.

### Aktivace / Deaktivace firmy

V detailu firmy: chip stavu (Aktivní/Neaktivní) + možnost editovat příznaky IsActive přímo.

Deaktivace: firma zůstane v DB, tenant schéma zůstane, ale uživatelé té firmy se nemohou přihlásit (JWT tenant kontrola).

### SMTP nastavení firmy (per-company)

V detailu firmy sekce „Email Settings" — umožňuje nastavit firemní SMTP odlišné od systémového SMTP:
- Host, port, username, heslo (šifrováno CredentialProtector)
- Sender email, sender name, použít SSL

**Priorita SMTP (3-tier):**
1. **Firma (CompanySystemSettings)** — pokud nastaveno, použije se pro emaily té firmy
2. **Systémové SMTP (SystemConfiguration)** — fallback, pokud firma nemá vlastní
3. **appsettings.json SmtpSettings** — poslední fallback

### AI nastavení firmy (per-company)

V detailu firmy sekce „AI Settings" — umožňuje nakonfigurovat AI poskytovatele specifické pro tuto firmu (přepíše systémové nastavení pro tuto firmu).

### EPO nastavení firmy (per-company)

Sekce „Nastavení EPO" na `/my-company` (viditelná pro role Admin a SysAdmin) — hlavičkové údaje
pro elektronické podání přiznání k DPH a kontrolního hlášení:
- Kód finančního úřadu (c_ufo) a kód územního pracoviště (c_pracufo) — **povinné**, bez nich
  API odmítne EPO export chybou `EPO_HEADER_INCOMPLETE`
- Kontaktní telefon, kontaktní e-mail, jméno oprávněné osoby — volitelné

Data leží na `CompanySystemSettings` v master DB, stejně jako SMTP a AI nastavení. SysAdmin
se k sekci dostane po zvolení firmy v přepínači impersonace.

---

## 4. Systémové nastavení

**Stránka:** `/system-settings` (pouze SysAdmin)

### Aplikační nastavení

| Pole | Popis |
|------|-------|
| AppName | Název aplikace (výchozí: „Fakvio") |
| BlazorBaseUrl | URL frontendu — používá se v emailových odkazech (reset hesla, pozvánka) |

### SMTP (systémové)

Záložní SMTP nastavení pro firmy bez vlastního SMTP. Viz §3 — SMTP priority.

| Pole | Popis |
|------|-------|
| Host | SMTP server (např. `smtp.gmail.com`) |
| Port | Typicky 587 (TLS) nebo 465 (SSL) |
| Username / Password | Přihlašovací údaje (heslo zašifrováno) |
| Sender email / name | Adresa a jméno odesílatele |
| Use SSL | Zapnout SSL/TLS |

### JWT nastavení

| Pole | Popis |
|------|-------|
| Expirace tokenu (h) | Počet hodin platnosti JWT tokenu (výchozí 24 h, rozsah 1–720) |

**Upozornění:** Klíče JWT (`JwtSettings:Secret`) jsou pouze v `appsettings.json` nebo env proměnných — nelze je nastavit přes UI z bezpečnostních důvodů.

---

## 5. AI poskytovatelé

**Stránka:** `/system-settings` sekce „AI nastavení" (pouze SysAdmin)

Systém podporuje 4 AI poskytovatele. Nastavení per systém jsou záloha; nastavení per firma (v detailu firmy) mají přednost.

### Dostupní poskytovatelé

| Poskytovatel | Model (výchozí) | Konfigurace | Nástroje (tool calling) |
|-------------|----------------|-------------|-------------------------|
| **Claude (Anthropic)** | `claude-sonnet-4-6` | API key (šifrovaný) + model ID | nativní |
| **OpenAI** | `gpt-4o` | API key (šifrovaný) + model ID | nativní |
| **Gemini (Google)** | `gemini-2.0-flash` | API key (šifrovaný) + model ID | nativní |
| **Ollama (Local)** | dle instalace | Base URL (lokální nebo sítě) + model ID | nativní, pokud to model umí (např. `gemma3` a `phi4` **ne**) |

**Nativní nástroje** znamenají, že seznam nástrojů posílá aplikace přímo do API poskytovatele
a model vrací strukturované volání. Je to spolehlivější než záložní textová cesta, kde model
píše volání jako JSON do běžné odpovědi.

Pokud model nástroje odmítne, aplikace se sama přepne na textovou cestu — nástroje fungují
dál, jen méně spolehlivě a s vyšší latencí. V logu to poznáte podle varování
„rejected native tool calling". Přepnutí platí až do restartu aplikace, proto na něj stačí
jen dvě odpovědi API: **neexistující model (404)** a **chyba 400, která přímo mluví
o nástrojích/funkcích**. Vypršelý API klíč, příliš dlouhá konverzace, rate limit ani výpadek
sítě přepnutí nezpůsobí — u dané zprávy se nástroje nepoužijí a další zpráva to zkusí znovu
nativně.

### Výchozí poskytovatel

Pole „Výchozí poskytovatel" určuje, který AI se použije když firma nemá explicitní nastavení.

### Jak nastavit

1. Otevřete `/system-settings` → sekce „AI"
2. Klikněte „Upravit"
3. Vyberte výchozího poskytovatele ze seznamu
4. Vyplňte API klíč pro každého poskytovatele, který chcete aktivovat
5. Volitelně upravte model ID
6. Uložte

**Hesla/klíče:** jsou šifrovány přes CredentialProtector (Data Protection API, AES-256). Po restartu bez persistovaných klíčů jsou nedešifrovatelné — viz §9.

### Instrukce AI asistenta (editovatelný system prompt)

**Stránka:** `/ai-instructions` (pouze SysAdmin, položka „Instrukce AI" v nav menu)

Umožňuje doladit chování AI asistenta bez nasazení nové verze. Nastavení je **systémové** —
platí pro všechny tenanty.

Systémový prompt má šest bloků; editovatelné jsou dva prostřední:

| # | Blok | Editovatelné |
|---|------|--------------|
| 1 | Úvodní věta („You are Fakvio AI Assistant…") | ne |
| 2 | Identita firmy (název, IČO, DIČ z databáze tenanta) | ne |
| 3 | Hlavní instrukce — styl odpovědi, seznam nástrojů, pravidla importu | **ano** |
| 4 | Dodatek | **ano** |
| 5 | Business kontext (počty klientů a faktur z databáze tenanta) | ne |
| 6 | Situační kontext (dnešní datum, otevřená stránka a doklad, chybějící nastavení tenanta) + onboarding instrukce | ne |

Blok 6 obsahuje navíc **onboarding instrukce** — jak asistent doprovodí firmu s nedokončeným
nastavením (ptát se po jednom údaji a rovnou ho zapsat). Přidají se jen tenantům, kterým
opravdu něco chybí; hotové firmě se do promptu nedostanou vůbec. V bloku 6 jsou schválně:
vlastní instrukce (blok 3) je nepřepíší, takže ani firma s vlastním promptem o onboarding
nepřijde. V náhledu je blok vidět (viz níže).

| Pole | Chování |
|------|---------|
| **Vlastní instrukce** | Pokud není prázdné, **nahradí celý blok 3** — tedy i popis nástrojů. Bez popisu nástrojů je AI nemusí použít. |
| **Dodatek** | Připojí se za blok 3 (vlastní i výchozí). Použijte, pokud chcete jen přidat pravidlo a zachovat výchozí chování. |

#### Jak nastavit

1. Otevřete `/ai-instructions`
2. Chip nahoře ukazuje, jestli běží výchozí, nebo vlastní instrukce
3. Vyplňte pole a klikněte „Uložit"
4. „Náhled celého promptu" zobrazí složený prompt tak, jak ho AI dostane. Identita firmy,
   statistiky i situační kontext jsou v náhledu zástupné (`[N/A — preview mode]`): první dvě
   proto, že náhled běží v SysAdmin kontextu bez databáze tenanta, situační kontext proto,
   že vzniká až u konkrétní zprávy uživatele. Náhled zobrazuje **uložený** stav, ne
   rozepsané změny.
5. „Obnovit výchozí" (s potvrzením) vymaže obě pole — AI se vrátí k vestavěným instrukcím

**Kdy se změna projeví:** nejpozději do 5 minut, restart aplikace není potřeba. Prompt je
cachovaný v paměti procesu s pevnou platností 5 minut. Uložení cache okamžitě zahodí v té
instanci aplikace, která požadavek odbavila — tam se změna projeví hned. Běží-li aplikace
ve více instancích (App Service s více instancemi), ostatní instance dojedou starý prompt
ještě nejvýše 5 minut. Náhled čte vždy přímo z databáze, takže ukazuje
aktuálně uložený stav bez ohledu na cache.

**Pozor:** pokud vývojáři přibude nový AI nástroj, do výchozích instrukcí se doplní
automaticky, ale do **vlastních instrukcí ne** — ty si musíte doplnit sami.

---

### Azure Blob Storage

Systémové úložiště souborů (přílohy faktur, exporty). Sdílený kontejner pro všechny tenanty — každý tenant má vlastní podadresář (`{CompanyId}/...`).

| Pole | Popis |
|------|-------|
| Connection String | Connection string Azure Storage účtu (šifrovaný). Formát: `DefaultEndpointsProtocol=https;AccountName=...` |
| Název kontejneru | Název blob kontejneru (výchozí: `fakvio-files`). Kontejner se vytvoří automaticky. |

**Priorita konfigurace (3-tier):**
1. `CompanySystemSettings.AzureBlobConnectionString` — per-firma override
2. `SystemConfiguration.AzureBlobConnectionString` — systémové (tato stránka)
3. `appsettings.json` sekce `AzureBlobStorage:ConnectionString` — fallback

**Test připojení:** Klikněte „Otestovat připojení" (tlačítko je dostupné vždy, bez přepnutí do editace). Systém zavolá Azure SDK `GetProperties` — žádná data nejsou čtena ani zapisována. Výsledek se zobrazí jako toast notifikace.

---

## 6. IMAP + Párování plateb

**Stránka:** `/sysadmin/payment-matching` (pouze SysAdmin)

Konfigurace systémové IMAP schránky pro automatické párování bankovních plateb.

### Jak funguje

1. Banka odesílá emailová oznámení o platbách na adresu ve formátu `{alias}@{InboundDomain}`
2. Systém se pravidelně přihlásí k IMAP schránce (poll interval) a přečte nové emaily
3. Email je routován k příslušnému tenantovi (firmy) pomocí aliasu v To/Delivered-To hlavičkách
4. AI nebo pattern matching spáruje platbu s fakturou

### Obecné nastavení

| Pole | Popis |
|------|-------|
| Aktivní (IsEnabled) | Zapnout/vypnout celé párování plateb |
| Inbound Domain | Doménová část příchozích emailů (např. `pay.fakvio.cz`) |
| Poll interval (min) | Jak často se kontroluje IMAP (min 5, max 1440) |
| Retenční lhůta (dny) | Jak dlouho uchovávat zpracované emaily (min 30) |

### IMAP konfigurace

| Pole | Popis |
|------|-------|
| IMAP Host | Adresa serveru (např. `imap.gmail.com`) |
| Port | Typicky 993 (SSL) nebo 143 (STARTTLS) |
| SSL | Zapnout SSL/TLS |
| Username / Password | Přihlašovací údaje (heslo prázdné = ponechat stávající) |
| Inbox složka | Složka pro příchozí emaily (výchozí `INBOX`) |
| Processed složka | Složka pro zpracované emaily |
| Unrouted složka | Složka pro nespárované emaily (nelze routovat k tenantovi) |

### Test připojení

Tlačítko „Test připojení" ověří IMAP login bez uložení nastavení.

### Worker status

Sekce pod formulářem zobrazuje stav background workeru (poslední spuštění, počet zpracovaných emailů).

### Run Now

Tlačítko „Spustit nyní" okamžitě spustí jeden cyklus IMAP pollingu (POST `/api/sysadmin/payment-matching/run-now`).

### Aliasy (BankAccountMailbox)

Každá firma si v detailu firmy konfiguruje vlastní alias (subfolder nebo speciální adresa). Systém routuje emaily pomocí multi-header fallback chain:
1. `Delivered-To` header
2. `X-Original-To` header
3. `Envelope-To` / `X-Envelope-To` header
4. `Received: for` clause
5. `To` header
6. `Cc` header

### Běh IMAP polling

Polling běží jako `ImapPollWorker` (BackgroundService) v jediném hostiteli `Fakvio.API`, který běží
na Azure App Service (produkce i test). Logika jednoho cyklu žije v `IImapPollService.RunCycleAsync()` —
pracovník ji volá v cyklu každých 5 minut. Vzájemné vyloučení mezi instancemi (App Service scale-out) řeší
PostgreSQL advisory lock.

---

## 7. Logy (AppLog)

**Stránka:** `/logs` (pouze SysAdmin)

### Přehled logů

Grid s logy: Timestamp, Level (chip), Source (zkrácená kategorie loggeru), Zpráva.

**Filtrace:**
- Výběr úrovně (Information / Warning / Error / Critical)
- Textové hledání (hledá v message, source, exception — case-insensitive)
- Sloupcový filtr na Timestamp (DateColumnFilter s operátory =, >, <, between)
- Sloupcový filtr na Source a Message (přes ColumnFilterRow)

**Detail logu:** Kliknutím na řádek se otevře dialog s plným textem záznamu včetně Exception stack trace a CorrelationId.

### Diagnostika

**CorrelationId:** Každý HTTP request má vlastní ID propagované přes `X-Correlation-Id` header. Pokud máte chybu, hledejte log záznamy se stejným CorrelationId.

**Referenční ID od uživatele:** Když AI asistent (chat) selže, uživatel místo technického detailu uvidí hlášku s referenčním ID — to je právě CorrelationId. Zadejte ho do textového hledání v `/logs` a najdete záznam s plnou výjimkou včetně stack trace.

**CompanyId:** Logy z tenant operací mají přiřazený CompanyId — lze filtrovat ve sloupci.

### Retence logů

Logy úrovně Debug/Info starší než 48 hodin jsou automaticky mazány (`LogCleanupService` BackgroundService, běží každou hodinu). Výchozí hodnota je hardcoded — pokud je třeba změnit, kontaktujte vývojový tým.

### Flush

Logy jsou buffered in-memory a periodicky flushované do DB (`LogFlushService` BackgroundService, běží každých 20 sekund). Při neočekávaném crashu může přijít o poslední buffer. Přímý zápis do DB per request je záměrně vypnutý (výkon).

### Opakované faktury (RecurringInvoiceWorker)

`RecurringInvoiceWorker` BackgroundService běží hodinově a generuje faktury z naplánovaných
šablon (viz DEVGUIDE §4.13). Průběh je vidět ve `/logs` — hledejte `Source` obsahující
`RecurringInvoiceWorker` nebo `RecurringInvoiceService`, případně text `RecurringInvoice:` v
message (log prefix jednotlivých kroků cyklu). Chyba jednoho plánu se zapíše i do `LastError`
na detailu šablony a vyvolá in-app notifikaci uživatelům dané firmy — SysAdmin ji v `/logs`
uvidí navíc jako `Error` záznam s plnou výjimkou.

---

## 8. Šablony dokumentů (systémové)

**Stránka:** `/content-templates` (přístupné i uživatelům; systémové šablony edituje SysAdmin)

Systémové šablony (typ = System) jsou sdílené a slouží jako výchozí pro všechny tenanty. Tenant-specifické šablony přepíší systémové pro danou firmu.

### Typy šablon (EContentTemplateType)

| Skupina | Typy |
|---------|------|
| PDF — vydané faktury | InvoicePdf, CreditNotePdf, AdvanceInvoicePdf, TaxReceiptForAdvancePdf |
| PDF — přijaté faktury | (interně) |
| Email | InvoiceEmail, ReminderEmail, InvitationEmail, PasswordResetEmail |
| Systémové | Šablony editovatelné SysAdminem |

### Editace šablony

1. Otevřete `/content-templates/{id}`
2. WYSIWYG editor (Quill) — vizuálně editujte HTML obsah
3. Dostupné placeholdery jsou popsány v USERGUIDE §7
4. Uložte

**Systémové šablony** (invitationEmail, passwordResetEmail atd.) edituje výhradně SysAdmin.

---

## 9. Bezpečnost

### Hesla (BCrypt)

- Algoritmus: BCrypt, work factor 12 (~100 ms verify → odolnost vůči brute force)
- Uložena jako hash v `User.PasswordHash`
- **Nikdy neukládat plaintext hesla**

### JWT tokeny

- Algoritmus: HS256 (HMAC-SHA256), klíč min. 32 znaků
- Expirace: konfigurovatelná (výchozí 24 h), ClockSkew=Zero
- Konfigurace: `JwtSettings:*` v appsettings / env

### API klíče (`fak_live_…`)

Vedle JWT přijímá aplikace i dlouhodobé API klíče — pro strojové klienty (MCP server, CI,
curl). Posílají se ve stejné hlavičce: `Authorization: Bearer fak_live_…`.

- Klíč je **osobní credential uživatele**, ne firemní. Tenant se odvozuje z firmy vlastníka.
- V DB je jen SHA-256 hash + prvních 12 znaků na zobrazení; **raw klíč se ukládá nikam** a do
  logu jde vždy jen prefix. Ztracený klíč nejde obnovit, jen zrušit a vydat nový.
- Funguje v jediném API hostiteli (Fakvio.API na App Service).

**Kde se klíče zakládají — self-service, ne SysAdmin agenda.** Klíče si vydává každý uživatel
sám na stránce **Nastavení → Integrace** (`/settings/integrations`); stačí libovolná přihlášená
role. SysAdmin bez impersonace ale tuhle položku v menu nemá (je ve fakturační skupině), takže
na ni musí přes URL přímo — stránka sama žádnou roli nevyžaduje. Zadává jméno klíče, rozsah
(`Jen čtení` / `Čtení i zápis`) a nepovinnou platnost do data;
raw klíč se ukáže **právě jednou** a stránka k němu rovnou vypíše hotové konfigurační bloky pro
MCP klienta. Revokace je tamtéž, s potvrzením, a platí okamžitě.

> **SysAdmin cizí klíče nevidí ani neruší.** Endpointy `/api/api-key` pracují vždy jen s klíči
> přihlášeného uživatele — není nad nimi žádná administrátorská nadstavba. Páka na kompromitovaný
> účet je proto **deaktivace uživatele** (řádek v tabulce níž). Impersonace firmy (§11) tu
> nepomůže — mění se jí tenant, ne identita, takže SysAdmin i pod ní vidí pořád jen své klíče.

**Co SysAdmina zajímá provozně:**

| Situace | Chování |
|---------|---------|
| Klíč revokovaný / expirovaný / neznámý | **401**, důvod se volajícímu neřekne (je v logu jako `API key authentication failed: …` s prefixem klíče) |
| **Deaktivace uživatele** (`IsActive = false`) | Okamžitě přestanou fungovat **i všechny jeho API klíče**. Toto je správný postup při odchodu člověka — samostatné rušení klíčů netřeba. |
| Smazání uživatele | Klíče mizí s ním (FK cascade). |
| Klíč s rozsahem `read` | Na jakýkoli zápis (POST/PUT/PATCH/DELETE) vrací **403**. Výjimka jsou výpočtové endpointy, dnes jen `POST /api/tax/estimate`. |
| Klíč s rozsahem `read,write` | Smí měnit data — ale **nikdy víc, než smí role vlastníka** (platí `role ∩ scope`). |
| Správa klíčů klíčem | Zakázáno. `GET/POST /api/api-key` a revokace jdou jen s přihlášením (JWT). Klíčem lze volat jen `GET /api/api-key/me`. |

**SysAdmin klíč a impersonace:** klíč nese roli vlastníka, takže klíč vydaný SysAdminem
umí `X-Company-Id` impersonaci úplně stejně jako jeho přihlášení (viz §11). Bez té hlavičky
takový klíč na tenant endpointy nedosáhne (403) — stejně jako SysAdmin bez impersonace.
Je to tedy **plnohodnotný SysAdmin credential s dlouhou platností**: vydávejte ho uvážlivě,
raději s vyplněnou expirací a rozsahem `read`.

### MCP server v HTTP režimu (vzdálené napojení AI klientů)

`Fakvio.McpServer` umí dva režimy, přepíná se proměnnou `FAKVIO_MCP_TRANSPORT`:

| Režim | Kdo ho spouští | Credential | Kdy dává smysl |
|-------|----------------|------------|----------------|
| `stdio` (výchozí) | AI klient na počítači uživatele, jako podproces | API klíč v `FAKVIO_API_TOKEN` (proměnná procesu) | Jeden uživatel, jeho vlastní stroj |
| `http` | Vy, jako trvale běžící službu | API klíč **v každém requestu** volajícího | Víc uživatelů, klienti, které nejde nic doinstalovat |

Neznámá hodnota proměnné = chyba na stderr a **exit code 1** (server, který měl poslouchat na
HTTP a místo toho čeká na stdin, vypadá zvenčí jako nastartovaný — proto fail fast).

**Co je potřeba k provozu HTTP hostu:**

- **ASP.NET Core shared framework** (`Microsoft.AspNetCore.App`) na cílovém stroji — a to i pro
  stdio režim; balíček se Streamable HTTP transportem ho táhne přes `FrameworkReference` do
  celého nástroje. Na stroji jen s .NET runtime se musí doinstalovat ASP.NET Core Runtime.
- `ASPNETCORE_URLS` — na čem Kestrel poslouchá. `FAKVIO_API_URL` — adresa API, kam server volá.
- **HTTPS.** Klienti posílají API klíč v hlavičce `Authorization`, takže po veřejné síti musí
  jít spojení šifrovaně. Host je holá ASP.NET Core aplikace bez vlastní TLS konfigurace —
  buď mu certifikát dodáte standardní cestou Kestrelu, nebo ho postavte za reverzní proxy.

**Bezpečnostní model — co je na něm důležité:**

- Host **nemá vlastní credential** a nemá přístup k databázi. Klíč volajícího jen přeposílá na
  `Fakvio.API`, takže autorizace i tenant izolace zůstávají tam, kde byly. Kompromitovaný MCP
  host tedy sám o sobě nedává přístup k datům, dokud mu někdo neposílá platné klíče.
- **Každý request se ověřuje znovu** proti `GET /api/api-key/me`, **bez jakékoli cache** — proto
  revokovaný klíč přestává fungovat okamžitě, ne „do vypršení cache". Chybějící hlavička se
  odmítne rovnou, bez round-tripu na API.
- Odmítnutí = **401** + `WWW-Authenticate: Bearer`, bez detailu v těle; důvod jde do logu hostu.
  **Nedostupné API se na 401 nepřevádí** — padá jako 500, aby se „API neběží" nepletlo s
  „tvůj klíč neplatí".
- Běží **stateless** (žádné `Mcp-Session-Id`), takže není potřeba sticky routing a host jde
  škálovat vodorovně. `GET /mcp` ani `/sse` k dispozici nejsou.
- Endpoint je jediný: `POST /mcp`.

> **MCP host už běží na App Service — jen v produkci.** #419 přesunul API a MCP host z Azure
> Functions na App Service; #426 pak zrušil test prostředí úplně (žádné `TEST-ENV`, žádné
> `fakvio-api-test`, žádné `mcp-test.fakvio.cz`) — od 2026-09-10 existuje jen produkce.
> Packaging i CI (`.github/workflows/mcp-server.yml`) mají proto jediný deploy job,
> `deploy-http-prod`, spouštěný z `master`. Přeskočí se, dokud není nastavená repo proměnná
> se jménem web appu, `MCP_HTTP_APP_NAME_PROD` (produkce, `fakvio-mcp-web`). Produkční MCP
> běží na planu `asp-fakvio-b1` vedle `fakvio-api`.
>
> **App settings na MCP web appu:**
> - `FAKVIO_MCP_TRANSPORT=http`
> - `FAKVIO_API_URL` (adresa API, `https://fakvio-api.azurewebsites.net`)
> - `ASPNETCORE_URLS=http://0.0.0.0:8080`
>
> **HTTPS Only** zapnuto.
>
> **Žádnou platformní autentizaci nezapínat** — ani Easy Auth, ani vyšší authorization.
> Autorizaci dělá API klíč uvnitř aplikace.
>
> **Custom doména:** `mcp.fakvio.cz` je vlastní doména s vlastním managed certifikátem.
> CNAME záznam u Forpsi míří na technického hostitele App Service. Adresu, kterou stránka
> Integrace nabízí, drží `McpSettings:BaseUrl` (`Fakvio.BlazorUI/wwwroot/appsettings.json`,
> dnes `https://mcp.fakvio.cz`) — samostatná hodnota, ne odhad z adresy API (#363).
>
> **Produkční hodnota se k uživateli dostane až releasem.** Tentýž push do `master` nasadí
> i samotný host, takže adresa a to, na co ukazuje, jdou živě spolu.

Podrobnosti pro vývojáře: DEVGUIDE §4.9, `Fakvio.McpServer/README.md`.

### Data Protection (CredentialProtector)

- Šifruje: SMTP hesla, IMAP hesla, AI API klíče uložené v DB
- Algoritmus: AES-256-CBC + HMACSHA256 (Data Protection API)
- **KRITICKÉ: klíče MUSÍ být persistované do DB** (`DataProtectionKeys` tabulka v master schématu)

**Co se stane při ztrátě klíčů** (restart bez persistovaných klíčů):
- CredentialProtector.Decrypt zachytí CryptographicException a vrátí raw ciphertext jako "plaintext"
- Služby (IMAP, SMTP, AI) dostanou neplatné heslo → `AuthenticationException`
- **Diagnostika:** Na stránce Systémová nastavení se zobrazí varovný banner s výpisem poškozených polí
- **Detekce via API:** `GET /api/system-configuration/credential-health` (SysAdmin only) — vrací `{ healthy: bool, issues: [...] }`
- **Řešení:** SysAdmin znovu uloží příslušná hesla/klíče v UI (SMTP, IMAP, AI API klíče, Azure Blob)

**Preventivní kontrola:** Ověřte, že `ServiceCollectionExtensions.cs` obsahuje:
```
AddDataProtection().PersistKeysToDbContext<MasterDbContext>().SetApplicationName("Fakvio")
```
`SetApplicationName("Fakvio")` je důležité pro konzistentní šifrování v celém systému.

**Přesun databáze na jiný server:** key ring je nejrizikovější část celé operace —
podle platformy, na které aplikace běžela, je sloupec `Xml` buď plaintext (přenositelný),
nebo zašifrovaný přes DPAPI (nepřenositelný, vyžaduje ruční obnovu **všech** secretů
včetně TOTP). Kompletní runbook včetně blokující pre-flight kontroly viz
[`SELFHOST-DB.md`](SELFHOST-DB.md) §4.

**Pozor:** `credential-health` **nekontroluje** `User.TotpSecretEncrypted`. Po ztrátě
klíčů projde zeleně i ve chvíli, kdy se žádný uživatel s 2FA nepřihlásí — stav 2FA se
musí ověřit zvlášť.

### reCAPTCHA v3 (ochrana anonymních endpointů)

Chrání přihlášení, registraci, anonymní ARES lookup na registračním formuláři a zapomenuté heslo (RC.3). Druhá vrstva ochrany je rate limiting (viz níže) — jiná ochrana proti robotům v aplikaci **není**.

Konfigurace v `appsettings.json` nebo env proměnných (Azure App Settings používá dvojité podtržítko, např. `Recaptcha__SecretKey`). Přes UI nastavit nelze — jde o secret.

| Klíč | Výchozí | Popis |
|------|---------|-------|
| `Recaptcha:Enabled` | `true` | `false` = ověřování se úplně přeskočí. Použijte jen tam, kde záměrně běžíte bez reCAPTCHA. |
| `Recaptcha:SiteKey` | prázdný | Veřejný klíč pro frontend (`Fakvio.BlazorUI/wwwroot/appsettings.json`). |
| `Recaptcha:SecretKey` | prázdný | Tajný klíč pro ověření na serveru. Získáte na https://www.google.com/recaptcha/admin (Score based v3). |
| `Recaptcha:AllowedHostnames` | prázdné pole | Seznam hostů, na kterých se site key používá (např. `fakvio.cz`). Prázdné = kontrola hostname se přeskočí. |

**DŮLEŽITÉ — brána je „fail closed"** (od issue #200): pokud je `Enabled=true` a `SecretKey` chybí, **přihlášení i registrace vracejí chybu 400**. Totéž při výpadku Googlu. Dřívější chování bylo opačné (při jakémkoli problému se požadavek propustil), což znamenalo, že zapomenutý klíč tiše vypnul ochranu.

**Nasazení — jsou jen dvě funkční varianty.** Samotné doplnění `Recaptcha__SecretKey` mezi ně nepatří, to přihlášení naopak rozbije:

| Varianta | Co nastavit | Výsledek |
|----------|-------------|----------|
| **A — brána vypnutá** | `Recaptcha__Enabled=false` v App Settings API (fakvio-api) | Přihlášení, registrace i ARES fungují, ochrana proti robotům žádná. |
| **B — brána zapnutá** | (1) `Recaptcha__SecretKey` v App Settings API (fakvio-api) **a zároveň** (2) veřejný `SiteKey` zapsaný do `Fakvio.BlazorUI/wwwroot/appsettings.json` + nový deploy GitHub Pages | Ochrana je aktivní. |

Krok (2) nejde nahradit App Settings: WASM klient se konfiguruje ze statického souboru ve `wwwroot`, který workflow `blazorui-deploy.yml` publikuje beze změny — žádná substituce hodnot při deployi neexistuje. Když je `SiteKey` prázdný, stránka si od Googlu token vůbec nevyžádá, hlavička `X-Captcha-Token` nedorazí na server a fail-closed brána odpoví **400 na přihlášení, registraci i ARES lookup — všem uživatelům**.

`SiteKey` je veřejný klíč (přečte ho kdokoli ze zdroje stránky), takže jeho uložení do repozitáře není únik. Tajný je pouze `SecretKey` a ten do `wwwroot` **nikdy** nepatří.

Pozn.: `Recaptcha__SiteKey` v Azure App Settings nedělá nic — server SiteKey nečte, potřebuje ho jen klient.

**Diagnostika:** v logu (`/logs`, úroveň Error) hledejte zprávu `reCAPTCHA is enabled but Recaptcha:SecretKey is not configured`. Úroveň Warning zaznamená i odmítnutí kvůli nízkému skóre, neshodě akce nebo neznámému hostname.

### Rate limiting anonymních endpointů (RC.4)

Druhá, nezávislá vrstva ochrany nad reCAPTCHA — omezuje **počet pokusů za jednotku času
na IP adresu**, bez ohledu na to, jestli je reCAPTCHA zapnutá. Chrání login, registraci,
ARES lookup, zapomenuté heslo, nastavení hesla, ověření pozvánky a ověření 2FA kódu
(politika `auth-anon` v `Fakvio.API/Program.cs`).

| Klíč | Výchozí | Popis |
|------|---------|-------|
| `RateLimiting:AuthAnon:PermitLimit` | `10` | Kolik požadavků smí jedna IP poslat za okno. |
| `RateLimiting:AuthAnon:WindowSeconds` | `60` | Délka okna ve vteřinách (fixed window). |

Po překročení limitu server odpoví **429 Too Many Requests** s hlavičkou `Retry-After`
(počet vteřin do konce okna); UI zobrazí lokalizovanou hlášku „Příliš mnoho pokusů".

**IP adresa přichází přes Azure App Service front-end** (`ForwardedHeadersMiddleware`,
`X-Forwarded-For`). Azure App Service front-end **není** na loopbacku (to platí pro IIS
in-process hosting na Windows, ne pro platformní edge App Service) — proto
`Program.cs` explicitně čistí `KnownNetworks`/`KnownProxies` (jinak by middleware
hlavičku nikdy nedůvěřoval a všichni volající by sdíleli jednu partition = jeden útok by
zablokoval přihlášení všem). Bezpečné je to jen proto, že do kontejneru na App Service
**nelze** navázat spojení jinudy než přes Azure vlastní edge — víc k tomu v DEVGUIDE §2.8a.

Limit je společný pro všechny anonymní endpointy dohromady (jedna IP, jedno okno) — pokud
je potřeba jemnější granularita (např. samostatný limit pro 2FA), rozdělit politiku
`auth-anon` na víc pojmenovaných politik v `Program.cs`.

### OAuth (Social login)

Dostupní poskytovatelé: Google, Microsoft, Facebook, Seznam.cz

Konfigurace v `appsettings.json` (nebo env):
```json
{
  "OAuth": {
    "Google":    { "ClientId": "...", "ClientSecret": "..." },
    "Microsoft": { "ClientId": "...", "ClientSecret": "..." },
    "Facebook":  { "AppId": "...", "AppSecret": "..." },
    "Seznam":    { "ClientId": "...", "ClientSecret": "..." }
  }
}
```

Provider se zaregistruje **jen pokud je nastaven ClientId** — chybějící config tiše skipuje provider.

**Callback URL:** `{BaseUrl}/api/auth/external-callback` (generická), nebo per-provider: `/api/auth/google-callback` atd.

### 2FA

Viz §2 — Správa uživatelů → 2FA. Uživatel si aktivuje sám. SysAdmin nemá UI pro force-reset (musí do DB).

### Middleware pipeline (pořadí)

```
1. CorrelationId
2. Authentication (JWT)
3. Impersonation (X-Company-Id header → claim přepis, pouze SysAdmin)
4. TenantContext (CompanyId → PostgreSQL schema nastavení)
```

**NIKDY neměnit pořadí** — Impersonation musí být po Authentication (Role musí být validní).

### Diagnostické endpointy

| Endpoint | Přístup | K čemu |
|----------|---------|--------|
| `GET /api/diagnostic/health` | **jen SysAdmin** | Režim autentizace k DB, stav připojení a čekající migrace. Podrobně §13. |
| `POST /api/diagnostic/migrate` | **jen SysAdmin** | Ruční spuštění EF Core migrací (master DB + všechny tenanty). |
| `GET /api/diagnostic/auth` | **jen SysAdmin** | Výpis stavu JWT tak, jak ho vidí worker — hlavička, claims, issuer/audience, ruční validace tokenu. |

Od issue #263 vyžadují `migrate` a `auth` platný Bearer token s rolí SysAdmin — dřív byly
anonymní, takže kdokoli mohl spustit migrace nebo si nechat vypsat konfiguraci JWT.
Od issue #138 platí totéž i pro `health`: vypisuje režim autentizace k databázi, cílový
server a jména migrací, což je pro útočníka stejně cenné. Bez tokenu vrací všechny tři
**401**, s tokenem bez role SysAdmin **403**.

**Dopad na health probe:** `health` už není použitelný jako anonymní liveness probe Azure —
probe bez tokenu dostane 401. Nastavte probe na jiný anonymní endpoint, nebo ji berte tak,
že 401 znamená „proces běží a odpovídá" (což pro liveness stačí; readiness ne).

Praktický důsledek pro ladění: `/api/diagnostic/auth` už nepomůže u volajícího, jehož token
se vůbec nevaliduje (dostane 401 dřív, než se cokoli vypíše). Pro takové případy použijte
logy App Service: `az webapp log tail -g invoiceapi -n fakvio-api` — `JwtAuthenticationMiddleware` důvod zamítnutí loguje.

---

## 10. Číselníky — systémové vs tenant kopie

### Master (systémové) číselníky

Upravují se přes stránky dostupné SysAdminovi bez impersonace:

| Číselník | Stránka | Popis |
|---------|---------|-------|
| Měny | `/currencies` | Dostupné měny + ČNB kurzy (automaticky aktualizovány) |
| DPH sazby | `/vat-rates` | Systémové DPH sazby (výchozí pro nové tenanty) |
| Formáty číselných řad | `/number-sequences` | Vzory pro číslovací řady |
| Šablony dokumentů | `/content-templates` | Systémové PDF/email šablony |
| Daňové konfigurace | `/tax-configs` | Konfigurace pojistného + daní pro OSVČ |

### Tenant kopie

Při provisioningu se **kopírují** do schématu tenanta:
- VatRate
- Currency
- NumberSequenceFormat
- ContentTemplate

Úpravy v tenant schématu jsou izolované (neovlivňují jiné tenanty ani master).

### Přidání nové systémové sazby DPH (příklad)

1. Přihlaste se jako SysAdmin (bez impersonace)
2. Otevřete `/vat-rates`
3. „+ Nová sazba" → vyplňte název, procento, platnost
4. Uložte
5. **Nová sazba se automaticky nezobrazí u existujících tenantů.** Opakovaný provisioning ji tam nedostane — u provisionované firmy se nespustí (viz „Kdy provisioning proběhne a kdy ne"). Do existujícího tenanta se sazba propaguje migrací (konzultujte vývojový tým).

---

## 11. Impersonace tenant firmy

SysAdmin může „vstoupit" do kontextu libovolné firmy bez přepínání účtu.

### Jak impersonovat

V detailu firmy `/companies/{id}` → tlačítko **„Impersonovat"** — aplikace nastaví `localStorage['ImpersonatedCompanyId']` a přidá hlavičku `X-Company-Id` ke každému API requestu.

Po aktivaci impersonace:
- Navigace zobrazuje fakturační menu (Faktury, Klienti, atd.) pro vybranou firmu
- Dashboard přepne z SysAdmin pohledu na pohled firmy
- Všechny operace probíhají v kontextu dané firmy

### Ukončení impersonace

Klikněte na chip s názvem firmy v horní liště (nebo ručně vymažte `ImpersonatedCompanyId` z localStorage).

### Bezpečnost impersonace

- Middleware `ImpersonationMiddleware` kontroluje Role — **pouze SysAdmin** může nastavit `X-Company-Id` header s efektem
- Header poslaný ne-SysAdmin uživatelem je tiše ignorován

---

## 12. Odesílání testovacího emailu

**Stránka:** `/send-email` (pouze SysAdmin bez impersonace)

Umožňuje odeslat testovací email na libovolnou adresu pro ověření SMTP nastavení.

Systém použije SMTP dle priority (viz §3 — SMTP priority).

---

## 13. Diagnostika nasazení (health endpoint)

`GET /api/diagnostic/health` — **jen SysAdmin** (Bearer token). Vrací stav master databáze a migrací
v běžícím API hostiteli (Fakvio.API na App Service).

K čemu to je: po změně konfigurace databáze (typicky Azure App Settings) potřebujete vidět,
**co běžící proces skutečně vyhodnotil** — ne co si myslíte, že je v konfiguraci.

```bash
curl -s -H "Authorization: Bearer <sysadmin-jwt>"   https://<host>/api/diagnostic/health | jq '{authMode, authModeSource, masterDbCanConnect}'
```

| Pole | Význam |
|------|--------|
| `authMode` | `Password` (heslo z connection stringu) nebo `AzureEntraId` (token z Entra ID). |
| `authModeSource` | Který klíč vyhrál: `Database:AuthMode` (nový), `UseAzureAdAuthentication (legacy)` nebo `default` (nikde nic nastaveno = `Password`). |
| `masterConnectionServer` | Host, databáze a uživatel — **nikdy heslo ani token**. |
| `masterDbCanConnect`, `masterDbPendingMigrations` | Dostupnost master DB a počet nenasazených migrací. |
| `databaseConnected` / `databaseReady` | Souhrnné vlajky pro monitoring. `databaseReady` = připojeno **a** žádné čekající migrace. |
| `startupMigration` | `pending` / `succeeded` / `failed` — výsledek startovní migrace master DB. Doplňují ho `startupMigrationCompletedAt` a při selhání `startupMigrationError`. |

HTTP **200** = databáze odpovídá, **503** = neodpovídá (monitoring může jet jen podle status
kódu). Při 503 se `authMode`/`authModeSource` hlásí dál — právě tehdy jsou nejužitečnější.

**`startupMigration: failed` neshazuje aplikaci** a health kvůli němu nevrací 503. Znamená to
„databáze odpovídá, ale je o pár migrací pozadu" — to je stav, přes který se dá provozovat,
zatímco výpadek by způsobil víc škody. Text výjimky je v `startupMigrationError`; dřív ho
nebylo kde přečíst, protože worker `ILogger` do App Insights nedoletí (issue #322).

### 13.1 Problémy s připojením k databázi

Master DB migrace se spouští synchronně při startu. Selhání je zalogováno a hlášeno v healthu
(`startupMigration: failed` + `startupMigrationError`), ale nešhodí aplikaci — zdraví (HTTP 200)
se vrací i při selhané migraci, protože to je stav provozovatelný (aplikace běží, jen je DB
o pár migrací pozadu).

Když je databáze **úplně nedostupná** (network timeout, firewall blok, špatné heslo):
- Health endpoint vrací HTTP 503 (`masterDbCanConnect: false`).
- Log stream / Application Insights hned po startu vypíší:

```text
info: Fakvio.Infrastructure.Database[0]
      Startup: database auth mode Password (source: Database:AuthMode)
```

a krátce poté:

```text
fail: Fakvio.Infrastructure.Data[0]
      Database connection failed: …
```

Běžné problémy:
- **Špatné IP v `pg_hba.conf`** — App Service má 19 outbound IP adres (`possibleOutboundIpAddresses`), všechny musí být v allowlistu.
- **Vypršený TLS certifikát** nebo `Ssl Mode=VerifyFull` se self-signed certifikátem bez `Root Certificate`.
- **Firewall na VPS** — Hostinger + ufw pravidla, oba musí povolit spojení z App Service IP.
- **Špatná hesla nebo databáze se nepřihlašuje** — ověř v `pg_hba.conf` a na serveru `SELECT * FROM pg_roles WHERE rolname = 'fakvio_prod'`.

**Přepnutí režimu autentizace k DB** (Azure App Settings):

1. Přidejte `Database__AuthMode` = `AzureEntraId` nebo `Password`. Starý klíč
   `UseAzureAdAuthentication` může chvíli zůstat, ale **musí souhlasit** — při rozporu
   aplikace při startu spadne s hláškou, která oba klíče jmenuje.
2. Restart → `curl` výše. `authModeSource` musí hlásit `Database:AuthMode`.
3. Teprve pak smažte `UseAzureAdAuthentication`.

Chyba při startu (např. heslo v connection stringu při `AzureEntraId`) vždy dopoví, **odkud**
se režim vzal — podle toho víte, který klíč opravit.

---

## 14. Prostředí (test vs produkce)

Aplikace běží ve **dvou oddělených prostředích**. Kód je stejný, Azure zdroje ne — testovací
prostředí má vlastní App Service (API + MCP), vlastní frontend hosting, vlastní JWT klíč i vlastní
deploy credentials. Nesdílí se **credentials, Azure zdroje ani data** — test tedy nemůže
sáhnout na produkční databázi ani na produkční Azure zdroje. Sdílený je naopak kód a ta část
konfigurace, která se nemá lišit (`JwtSettings__Issuer`/`Audience`, `AresSettings__BaseUrl`);
rozdíly vypisuje tabulka níž.

Vývojářský pohled (obsah workflow souborů, precedence konfigurace, jak se přepisuje URL API
v WASM bundlu) je v `DEVGUIDE.md` §9 — tady je jen to, co potřebuje SysAdmin.

### Co kde běží

| | **Test** (vypnuto 2026-09-10) | **Produkce** |
|---|---|---|
| Frontend hosting | *(vypnuto)* Azure Static Web App `fakvio-test-ui` | GitHub Pages (custom doména z `CNAME` v repu) |
| Frontend URL | *(vypnuto)* https://test.fakvio.cz | https://app.fakvio.cz |
| Backend API | *(vypnuto)* App Service `fakvio-api-test` | App Service `fakvio-api` |
| Backend API URL | *(vypnuto)* https://fakvio-api-test.azurewebsites.net | https://fakvio-api.azurewebsites.net |
| Backend MCP | *(vypnuto)* App Service `fakvio-mcp-web-test` | App Service `fakvio-mcp-web` |
| Backend MCP URL | *(vypnuto)* https://fakvio-mcp-web-test.azurewebsites.net | https://fakvio-mcp-web.azurewebsites.net |
| Zdrojová větev | *(vypnuto)* `TEST-ENV` | `master` |
| Deploy workflows | *(vypnuto)* — `testenv_fakvio-api.yml`, `mcp-server.yml`, `blazorui-test-deploy.yml` deaktivovány | `master_fakvio-api.yml` (API), `mcp-server.yml` (MCP), `blazorui-deploy.yml` (frontend) |
| Databáze | *(vypnuto)* vlastní PostgreSQL `fakvio_test` | produkční PostgreSQL na vlastním serveru (přístup přes veřejný internet, TLS + firewall) |

Větev **`TEST-ENV` na `origin` vzniká až prvním během `/release`** (odbočí z `master`).
Dokud tam není, testovací deploy workflows nemají co spustit — není to incident.

**Proč test jsou samostatné App Service:** test a produkce jsou zcela oddělené web appky na
**dvou** App Service planech — `asp-fakvio-b1` (produkce) a `asp-fakvio-b1-test` (test), oba Linux
Basic B1. Jeden sdílený B1 (1,75 GB) čtyři appky plus jejich Kudu kontejnery neunesl (swap, CPU
100 %, produkce odpovídala v sekundách), proto má test vlastní plan a deploy na test nikdy
nerestartuje nic vedle produkce. App Settings jsou na obou nezávislé; na všech appkách je
`WEBSITES_CONTAINER_START_TIME_LIMIT=900`, protože první start po deployi trvá na B1 až 7 minut.

**Proč frontend testu není na GitHub Pages:** Pages umí hostovat jen jeden web na repozitář
a ten patří produkci. Test proto jede na Azure Static Web Apps.

### Jak se liší konfigurace

Všechna nastavení jsou **App Settings v Azure** (App Service → Settings → Environment
variables), ne ve workflow souborech. Zápis používá dvojité podtržítko místo dvojtečky
(`JwtSettings__Secret`).

| Nastavení | Test (vypnuto) | Produkce |
|-----------|---|---|
| `JwtSettings__Secret` | vlastní, nesdílený | vlastní |
| `JwtSettings__Issuer`, `JwtSettings__Audience` | shodné | shodné |
| `CorsSettings__AllowedOrigins__0` / `__1` | *(vypnuto)*: `https://wonderful-meadow-0eb3ada03.7.azurestaticapps.net` a `https://test.fakvio.cz` | `https://app.fakvio.cz` |
| `ConnectionStrings__DefaultConnection` | *(vypnuto)*: vlastní PostgreSQL `fakvio_test` | `Host=<public-ip>;Port=5544;Database=fakvio_prod;Username=fakvio_prod;Password=***;Ssl Mode=VerifyCA;Root Certificate=/home/site/wwwroot/certs/fakvio-db-server.crt;Timezone=UTC;Maximum Pool Size=40` (viz `SELFHOST-DB.md`) |
| `Database__AuthMode` | *(vypnuto)*: `Password` | `AzureEntraId` |
| `UseAzureAdAuthentication` | *(vypnuto)*: `false` | `true` (Entra ID). Legacy klíč, musí souhlasit s `Database__AuthMode`. |
| `AresSettings__BaseUrl` | shodné s produkcí | |

### Jak je produkční databáze zapojená

Produkční databáze je **vlastní PostgreSQL na veřejné adrese** (databáze `fakvio_prod`,
uživatel `fakvio_prod`, port 5544). Bezpečnost je zajištěna:

1. **TLS s validací certifikátu** — connection string má `Ssl Mode=VerifyFull` a `Root Certificate`
   (pokud self-signed).
2. **VPS firewall + `pg_hba.conf`** — server přijímá spojení **jen** z App Service outbound IP adres.
   Přesný seznam z `az webapp show -n fakvio-api -g invoiceapi --query possibleOutboundIpAddresses`
   (19 adres, stabilní pro životnost app service).
3. **Per-role `pg_hba.conf` řádky** — `hostssl fakvio_prod fakvio_prod <ip>/32 scram-sha-256`.

Detaily: `SELFHOST-DB.md` — připojovací řetězec (§7), konfigurace PostgreSQL (§6.2–6.3),
ověření spojení (§3.4).

### Testovací databáze (v současnosti vypnuto)

Testovací App Service `fakvio-api-test` bylo vypnuto 2026-09-10. Workflow `testenv_fakvio-api.yml`
a `blazorui-test-deploy.yml` jsou v GitHub Actions zakázány. Není to incident — deploy test-env
prostě neběží.

Pokud bude test znovu spouštěn, měl by mít vlastní PostgreSQL (`fakvio_test`, uživatel `fakvio_test`)
dostupnou stejně jako produkce — přes veřejný internet s TLS, nikoliv přes Tailscale tunel.
Role per prostředí znamená, že uživatel `fakvio_test` se do produkční DB `fakvio_prod` nepřihlásí.
Zabezpečení: REVOKE `CONNECT … FROM PUBLIC; GRANT CONNECT … TO fakvio_test;` v `pg_hba.conf` —
přesné příkazy v `SELFHOST-DB.md` §7.

### Známá omezení testovacího prostředí

- SWA běží na **Free tier** — bez SLA. Pro testovací prostředí je to v pořádku, na produkční
  provoz to není.

### Promotion — kdo a kdy co spouští

    develop  ──/release──▶  TEST-ENV  ──/release-prod──▶  master

| Krok | Kdo | Co se stane |
|------|-----|-------------|
| `develop` | `agent-ops` (automaticky při mergi feature PR) | Nenasazuje se nic — `develop` nemá deploy workflow. |
| `/release` | **člověk** | Otevře promotion PR `develop → TEST-ENV` a skončí. **Testovací deploye se neběží** — test-env byl vypnut 2026-09-10. Karty na boardu se nehýbou. |
| `/release-prod` — 1. běh | **člověk** | Otevře promotion PR `TEST-ENV → master` a skončí. Merge dělá člověk v GitHubu; merge nasadí produkci. **Karty se zatím nehýbou.** |
| `/release-prod` — 2. běh | **člověk** po mergnutí release PR | Finalizace boardu: karty v `Implemented`, jejichž merge commit je ancestorem `master`, se přesunou do `Approved` (stejný test i pro story). Bez druhého běhu zůstane board viset v `Implemented`. |

Pravidla, která platí bez výjimky:

- Do `TEST-ENV` ani do `master` se **nikdy nekomituje přímo**. Oprava toho, co se najde na
  testu, jde jako běžný feature PR do `develop` a znovu přes `/release`.
- Žádná z větví nemá branch protection — pořadí stupňů drží konvence a agenti, ne GitHub.
  Ruční push mimo tento postup nikdo nezastaví, proto ho nedělejte.

Stavový automat obou příkazů je v `.claude/commands/release.md` a
`.claude/commands/release-prod.md`, vývojářský popis v `DEVGUIDE.md` §9.6.

### Secrets pro deploy a jejich rotace

**Hodnoty, které čtou workflows**, žijí v **GitHub → Settings → Secrets and variables →
Actions** daného repozitáře — v repu nikde jinde nejsou. Zdroje, ze kterých vznikají (deploy
token SWA, OIDC důvěra a RBAC role), jsou naopak v Azure; proto rotace níž začíná v portálu
a do GitHubu se výsledek jen zkopíruje.

| Secret | K čemu |
|--------|--------|
| `AZUREAPPSERVICE_CLIENTID_TEST`, `AZUREAPPSERVICE_TENANTID_TEST`, `AZUREAPPSERVICE_SUBSCRIPTIONID_TEST` | *(Nepoužíváno od 2026-09-10)* Přihlášení k Azure pro deaktivované workflows `testenv_fakvio-api.yml` a `mcp-server.yml` (app registration `zcloudcz-InvoiceApi-TEST`). |
| `AZURE_STATIC_WEB_APPS_API_TOKEN_TEST` | *(Nepoužíváno od 2026-09-10)* Deploy token pro deaktivovaný `blazorui-test-deploy.yml` → SWA `fakvio-test-ui`. |
| `AZUREAPPSERVICE_CLIENTID_71CB3DED09D246528906A346340AC1F8`, `AZUREAPPSERVICE_TENANTID_0F744DC7C56040999B540311235A4E45`, `AZUREAPPSERVICE_SUBSCRIPTIONID_0A2C19BC7D294FFA80F06B93F1D614E4` | Produkční OIDC pro `master_fakvio-api.yml` a `mcp-server.yml`. GUID příponu generuje Azure Portál — proto se nejmenují symetricky k `_TEST`. Produkční frontend token nepotřebuje: GitHub Pages se nasazují vestavěným `GITHUB_TOKEN`. |

**Rozsah oprávnění testovacího OIDC** (v současnosti nepoužíváno): federated credential
byla vázaná na subject `repo:zcloudcz/InvoiceApi:ref:refs/heads/TEST-ENV` a role **Contributor
scopovaná jen na web appky `fakvio-api-test` a `fakvio-mcp-web-test`**. Od 2026-09-10 jsou
tyto zdroje a workflows deaktivovány.

**Rotace deploy tokenu SWA** (v současnosti nepoužívá se): Secret `AZURE_STATIC_WEB_APPS_API_TOKEN_TEST`
byl určen pro deaktivovaný `blazorui-test-deploy.yml` → SWA `fakvio-test-ui`. Pokud by se test-env
znovu spustilo, rotace by probíhala takto: Azure Portal → Static Web App `fakvio-test-ui` →
**Manage deployment token** → *Reset*, pak nový token do `AZURE_STATIC_WEB_APPS_API_TOKEN_TEST`.

OIDC credentials rotaci nepotřebují: app registration nemá client secret, důvěra stojí na
federated credential. Mění se jen tehdy, když se mění samotná app registration nebo název větve.

---

## Rychlá reference — SysAdmin navigace

| Co chcete udělat | Kde |
|------------------|-----|
| Správa firem/tenantů | `/companies` |
| Detail + provisioning firmy | `/companies/{id}` |
| Správa uživatelů | `/users` |
| Systémové nastavení (SMTP, JWT, AI) | `/system-settings` |
| IMAP / Párování plateb | `/sysadmin/payment-matching` |
| Logy | `/logs` |
| Šablony dokumentů | `/content-templates` |
| Měny | `/currencies` |
| DPH sazby (systémové) | `/vat-rates` |
| Číselné řady (systémové) | `/number-sequences` |
| Daňové konfigurace (OSVČ) | `/tax-configs` |
| Test emailu | `/send-email` |
| Dashboard SysAdmin | `/` (bez impersonace) |
| Diagnostika DB / auth režimu | `GET /api/diagnostic/health` (jen API, bez UI) |
