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

| Poskytovatel | Model (výchozí) | Konfigurace |
|-------------|----------------|-------------|
| **Claude (Anthropic)** | `claude-sonnet-4-6` | API key (šifrovaný) + model ID |
| **OpenAI** | `gpt-4o` | API key (šifrovaný) + model ID |
| **Gemini (Google)** | `gemini-2.0-flash` | API key (šifrovaný) + model ID |
| **Ollama (Local)** | dle instalace | Base URL (lokální nebo sítě) + model ID |

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

Systémový prompt má pět bloků; editovatelné jsou dva prostřední:

| # | Blok | Editovatelné |
|---|------|--------------|
| 1 | Úvodní věta („You are Fakvio AI Assistant…") | ne |
| 2 | Identita firmy (název, IČO, DIČ z databáze tenanta) | ne |
| 3 | Hlavní instrukce — styl odpovědi, seznam nástrojů, pravidla importu | **ano** |
| 4 | Dodatek | **ano** |
| 5 | Business kontext (počty klientů a faktur z databáze tenanta) | ne |

| Pole | Chování |
|------|---------|
| **Vlastní instrukce** | Pokud není prázdné, **nahradí celý blok 3** — tedy i popis nástrojů. Bez popisu nástrojů je AI nemusí použít. |
| **Dodatek** | Připojí se za blok 3 (vlastní i výchozí). Použijte, pokud chcete jen přidat pravidlo a zachovat výchozí chování. |

#### Jak nastavit

1. Otevřete `/ai-instructions`
2. Chip nahoře ukazuje, jestli běží výchozí, nebo vlastní instrukce
3. Vyplňte pole a klikněte „Uložit"
4. „Náhled celého promptu" zobrazí složený prompt tak, jak ho AI dostane. Identita firmy
   a statistiky jsou v náhledu zástupné (`[N/A — preview mode]`), protože náhled běží
   v SysAdmin kontextu bez databáze tenanta. Náhled zobrazuje **uložený** stav, ne
   rozepsané změny.
5. „Obnovit výchozí" (s potvrzením) vymaže obě pole — AI se vrátí k vestavěným instrukcím

**Kdy se změna projeví:** nejpozději do 5 minut, restart aplikace není potřeba. Prompt je
cachovaný v paměti procesu s pevnou platností 5 minut. Uložení cache okamžitě zahodí v té
instanci aplikace, která požadavek odbavila — tam se změna projeví hned. Běží-li aplikace
ve více instancích (škálovaný Azure Function App), ostatní instance dojedou starý prompt
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

### Duální provoz (API vs Functions)

- **Fakvio.API:** IMAP polling běží jako `ImapPollWorker` (BackgroundService) — pro lokální vývoj
- **Fakvio.Functions:** IMAP polling běží jako `[TimerTrigger]` Function — pro Azure deploy

Logika jednoho cyklu žije v `IImapPollService.RunCycleAsync()` — sdílená oběma.

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

Logy starší než 30 dní jsou automaticky mazány (LogCleanupService / CleanupLogs Function). Výchozí hodnota je hardcoded — pokud je třeba změnit, kontaktujte vývojový tým.

### Flush

Logy jsou buffered in-memory a periodicky flushované do DB (LogFlushService / FlushLogs Function). Při neočekávaném crashu může přijít o poslední buffer. Přímý zápis do DB per request je záměrně vypnutý (výkon).

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
`SetApplicationName("Fakvio")` musí být totožné na API i Functions hostu — jinak navzájem nedešifrují.

**Přesun databáze na jiný server:** key ring je nejrizikovější část celé operace —
podle platformy, na které aplikace běžela, je sloupec `Xml` buď plaintext (přenositelný),
nebo zašifrovaný přes DPAPI (nepřenositelný, vyžaduje ruční obnovu **všech** secretů
včetně TOTP). Kompletní runbook včetně blokující pre-flight kontroly viz
[`SELFHOST-DB.md`](SELFHOST-DB.md) §4.

**Pozor:** `credential-health` **nekontroluje** `User.TotpSecretEncrypted`. Po ztrátě
klíčů projde zeleně i ve chvíli, kdy se žádný uživatel s 2FA nepřihlásí — stav 2FA se
musí ověřit zvlášť.

### reCAPTCHA v3 (ochrana anonymních endpointů)

Chrání přihlášení, registraci a anonymní ARES lookup na registračním formuláři. Jiná ochrana proti robotům v aplikaci **není**.

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
| **A — brána vypnutá** | `Recaptcha__Enabled=false` v App Settings API **i** Function Appu | Přihlášení, registrace i ARES fungují, ochrana proti robotům žádná. |
| **B — brána zapnutá** | (1) `Recaptcha__SecretKey` v App Settings API **i** Function Appu **a zároveň** (2) veřejný `SiteKey` zapsaný do `Fakvio.BlazorUI/wwwroot/appsettings.json` + nový deploy GitHub Pages | Ochrana je aktivní. |

Krok (2) nejde nahradit App Settings: WASM klient se konfiguruje ze statického souboru ve `wwwroot`, který workflow `blazorui-deploy.yml` publikuje beze změny — žádná substituce hodnot při deployi neexistuje. Když je `SiteKey` prázdný, stránka si od Googlu token vůbec nevyžádá, hlavička `X-Captcha-Token` nedorazí na server a fail-closed brána odpoví **400 na přihlášení, registraci i ARES lookup — všem uživatelům**.

`SiteKey` je veřejný klíč (přečte ho kdokoli ze zdroje stránky), takže jeho uložení do repozitáře není únik. Tajný je pouze `SecretKey` a ten do `wwwroot` **nikdy** nepatří.

Pozn.: `Recaptcha__SiteKey` v Azure App Settings nedělá nic — server SiteKey nečte, potřebuje ho jen klient.

**Diagnostika:** v logu (`/logs`, úroveň Error) hledejte zprávu `reCAPTCHA is enabled but Recaptcha:SecretKey is not configured`. Úroveň Warning zaznamená i odmítnutí kvůli nízkému skóre, neshodě akce nebo neznámému hostname.

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

### Diagnostické endpointy (Azure Functions)

| Endpoint | Přístup | K čemu |
|----------|---------|--------|
| `GET /api/diagnostic/health` | anonymní | Stav připojení k DB a čekající migrace. Anonymní zůstává kvůli health probe Azure. |
| `POST /api/diagnostic/migrate` | **jen SysAdmin** | Ruční spuštění EF Core migrací (master DB + všechny tenanty). |
| `GET /api/diagnostic/auth` | **jen SysAdmin** | Výpis stavu JWT tak, jak ho vidí worker — hlavička, claims, issuer/audience, ruční validace tokenu. |

Od issue #263 vyžadují `migrate` a `auth` platný Bearer token s rolí SysAdmin — dřív byly
anonymní, takže kdokoli mohl spustit migrace nebo si nechat vypsat konfiguraci JWT.
Bez tokenu vrací **401**, s tokenem bez role SysAdmin **403**.

Praktický důsledek pro ladění: `/api/diagnostic/auth` už nepomůže u volajícího, jehož token
se vůbec nevaliduje (dostane 401 dřív, než se cokoli vypíše). Pro takové případy použijte
logy Function Appu — `JwtAuthenticationMiddleware` důvod zamítnutí loguje.

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
