# Fakvio — Párování plateb: Analýza a dev zadání

> Dokument připraven 2026-04-23. Autor: návrh Claude (Opus 4.7) na žádost Martina Záhálky.
> Cílem je navrhnout modul **Automatické párování bankovních plateb** s odběrem faktur v Fakviu, inspirovaný zavedenou praxí českých konkurentů (Fakturoid, iDoklad, Fakturovač, SuperFaktura).

---

## 1. Proč to potřebujeme

Dnes v Fakviu existuje pouze ruční krok **"Mark as Paid"** (`InvoiceService.MarkAsPaidAsync`, endpoint `PUT /api/invoice/{id}/mark-paid`). Uživatel musí každou uhrazenou fakturu označit ručně — to znamená:

- otevřít internetové bankovnictví, projít výpis,
- najít příslušnou fakturu v Fakviu, kliknout "Mark as Paid",
- u částečných úhrad/přeplatků není žádná podpora (status je jen Paid/Not Paid).

U automatizovaných konkurentů je párování plateb **jedna z top-3 prodejních featur** — a uživatelé ji očekávají. Bez ní má Fakvio podstatnou konkurenční nevýhodu (zejména proti Fakturoidu a iDokladu).

---

## 2. Jak to dělá konkurence

### 2.1 Fakturoid — email-only, real-time

**Mechanismus:** Každý bankovní účet v Fakturoidu dostane unikátní e-mail ve tvaru `bank.X.Y@fakturoidmail.cz`. Uživatel si v internetovém bankovnictví nastaví **notifikace o pohybu na tento e-mail**. Jakmile banka odešle notifikaci (typicky do sekund po transakci), Fakturoid zprávu parsuje, vytáhne **variabilní symbol + částku** a páruje proti nespárovaným fakturám/nákladům.

**Párování podle:** VS + přesná shoda částky. Bez VS nebo při rozdílné částce se nepáruje — Fakturoid vytvoří úkol ("task") k ručnímu vyřešení.

**Podporované banky (CZ/SK):** Fio, KB, Air Bank, Česká spořitelna, ČSOB, Raiffeisenbank, MONETA, Creditas, Citfin, UniCredit, Tatra banka, SLSP. Chybí mBank, Partners (ty e-mailové notifikace neposkytují v použitelném formátu).

**Speciál — Fio API:** Pro Fio banku existuje i alternativa přes REST API (polling každou hodinu) — robustnější, nezávislé na mailových notifikacích.

**Směr plateb:** Stejný mechanismus pro příchozí (faktury) i odchozí (expenses/náklady) platby.

**Slabina:** Formát e-mailové notifikace se **liší banka od banky** — každá má samostatný parser (Fakturoid má per-banku "návod k nastavení"). Když banka změní formát notifikace, rozbije se to.

### 2.2 iDoklad — email přes doménu @itsmybill.eu

**Mechanismus:** Identický koncept — vygeneruje se "bankovní e-mail" na doméně `@itsmybill.eu`, banka posílá notifikace.

**Párování podle:** Primárně VS, sekundárně částka:
- VS + přesná shoda částky → **Uhrazeno**
- VS + nižší částka → **Částečně uhrazeno**
- VS + vyšší částka → **Přeplaceno**
- Více faktur se stejným VS → páruje se podle částky
- Bez shody → nespárovaný pohyb v sekci Finance → Banka (ruční dořešení)

**Rozdíl oproti Fakturoidu:** iDoklad **explicitně modeluje částečné úhrady a přeplatky** — to je pro účetnictví důležité.

**Podporované banky:** Air, ČS, ČSOB, Fio, KB, MONETA, RB, UniCredit (CZ); ČSOB, Fio, SLSP, Tatra, VÚB (SK).

### 2.3 Fakturovač — email-based, free tier

Jednoduché emailové napojení (Nastavení → Banka), park kmenových funkcí má zdarma, některá rozšíření jsou placená. Podrobnosti o parseru a rozsahu shodné s výše.

### 2.4 SuperFaktura — hybrid (email + API přes agregátor)

Dvě souběžné cesty:
1. **E-mailové notifikace** — stejný princip jako Fakturoid.
2. **Přímé propojení přes Tatra banka API** — Tatra banka funguje jako **agregátor/PSD2 bridge**, přes nějž SuperFaktura čte pohyby z Tatra, SLSP, ČSOB, VÚB, Fio, RB, UniCredit. Omezení: SK-centric.

**Zajímavé:** Tento hybridní model je robustnější (API je strukturovanější než e-maily), ale drahý v provozu a závislý na Tatra bance.

### 2.5 Přehled — srovnání

| Nástroj      | Email notif. | Přímé API banky          | PSD2 AIS | CSV/GPC import | Částečné úhrady |
|--------------|:------------:|:------------------------:|:--------:|:--------------:|:---------------:|
| Fakturoid    | ✅ (main)    | ✅ Fio (polling)          | ❌       | ❌             | ❌ (jen celé)   |
| iDoklad      | ✅ (main)    | ❌                        | ❌       | ❌             | ✅ (+ přeplatky)|
| Fakturovač   | ✅           | ❌                        | ❌       | (neznámé)      | ❌              |
| SuperFaktura | ✅           | ✅ (přes Tatra agregátor) | ❌       | ✅             | ✅              |

---

## 3. Technické cesty — rozbor

### 3.1 E-mail parsing (hlavní dnešní cesta)

**Princip:** Aplikace provozuje MX endpoint (SMTP příjem) nebo IMAP mailbox s vyhrazenou subdoménou (`parovani.fakvio.cz`). Každému bankovnímu účtu v Fakviu se vygeneruje unikátní e-mail (např. `acct-{guid}@parovani.fakvio.cz`). Uživatel to vloží do notifikací ve svém internetovém bankovnictví.

**Plusy:**
- Nevyžaduje žádnou dohodu s bankami, žádnou regulaci.
- Funguje se všemi bankami, které posílají e-mailové notifikace (většina CZ/SK).
- Uživatel si to nastaví sám za 5 minut.
- Near real-time (v řádu sekund po transakci).

**Mínusy:**
- **Per-banku parser.** Každá banka má jiný formát e-mailu. Fakturoid to řeší desítkami parserů — vyžaduje průběžnou údržbu (když banka změní template, rozbije se).
- Nelze zpětně načíst historii (e-mail přijde jen o novém pohybu).
- E-maily nejsou strukturovaná data — plaintext/HTML, často lokalizované, některé obsahují jen "přišla platba, podívejte se do banky" bez částky.
- Možnost podvržení (e-mailový spoofing) — musíme ověřovat SPF/DKIM odesílatele banky, jinak riziko falešného spárování.

### 3.2 Přímé API banky (Fio, ČSOB, KB…)

**Fio:** Nejjednodušší v ČR — REST API, autentizace tokenem generovaným v internetovém bankovnictví, JSON výstup transakcí. Limity: 1 požadavek / 30 sec na token. Ideální pro polling a i pro importy historie.

**ČSOB, KB, ČS atd.:** Většinou **PSD2 AIS** (viz 3.3) — není to Fio-style "pojď, přihlas se tokenem".

**Plusy:**
- Strukturovaná data, spolehlivé.
- Historie dostupná (lze stáhnout třeba rok pozpátku).
- Lze i zahájit platbu (PISP) — budoucí feature.

**Mínusy:**
- Per-banku implementace — Fio je snadné, ostatní přes PSD2 jsou složité.

### 3.3 PSD2 AIS — standardizované open banking

Regulovaná cesta skrze Czech Standard for Open Banking (COBS). Vyžaduje:
- **Registraci v EBA registru** + notifikaci PSD2 u ČNB (JERRS).
- **Kvalifikovaný certifikát QWAC + QSEAL** (I.CA / Eviden) — náklady jednotky až desítky tisíc Kč ročně.
- Samostatnou integraci na každou banku (mírné odchylky od standardu).

**Plusy:**
- Jedna regulovaná cesta pro všechny banky v EU.
- Pokrytí prakticky všech CZ bank.
- PISP bonus — iniciace platby z Fakvio UI.

**Mínusy:**
- **Právní/regulatorní overhead** — Fakvio by se muselo registrovat jako AISP, což je netriviální projekt.
- SCA (Strong Customer Authentication) — klient musí souhlas každých 180 dní obnovovat.
- Vyplatí se až ve velkém objemu uživatelů.

### 3.4 Agregátor třetí strany (Salt Edge, Yapily, Tink, Nordigen/GoCardless)

Kupujeme PSD2 přístup jako službu — oni drží AISP licenci, my platíme per request / per user.

**Plusy:**
- Žádný regulatorní overhead.
- Jedno API pro všechny banky.

**Mínusy:**
- **Cena:** ~€0.10–€1 / uživatel / měsíc + setup fee. Pro Fakvio s malou marží nevýhodné na start.
- Závislost na třetí straně.

### 3.5 Import výpisu (CSV / GPC / ABO / MT940 / CAMT.053)

**Princip:** Uživatel si v internetovém bankovnictví stáhne výpis v některém formátu a nahraje do Fakvio, my naparseujeme a spárujeme.

**Plusy:**
- Zero-dependency, funguje pro všechny banky, žádné limity, zero-cost.
- Skvělý fallback pro banky, kde e-mail/API nefunguje.
- Umí i dávkové zpětné párování (historie).

**Mínusy:**
- Ruční krok — není automatizace. Uživatel to musí chtít dělat.
- Mnoho formátů (každá banka jiné). Nejvšeobecnější: **GPC** (ABO-K, 1000 bytes/řádek, CZ standard), **CAMT.053** (mezinárodní ISO 20022), **MT940** (legacy SWIFT).

---

## 4. Návrh řešení pro Fakvio

### 4.1 Hlavní princip — fázovaný hybridní přístup

Nezkoušíme "vše najednou". Začínáme jednoduchým a škálujeme:

| Fáze | Co                                    | Kdy             | Komu to slouží                       |
|:----:|---------------------------------------|-----------------|--------------------------------------|
| 1    | **Ruční import GPC/CAMT výpisu**      | MVP (2–3 týdny) | Všem uživatelům, všem bankám         |
| 2    | **Fio API polling**                   | +2 týdny        | Fio klientům (velká část OSVČ)       |
| 3    | **E-mail parser (unikátní e-mail)**   | +4 týdny        | Všem ostatním bankám                 |
| 4    | **PSD2 přes agregátor** (Salt Edge)   | až cca 500+ tenantů | Velkým klientům, multi-banka        |

**Proč v tomto pořadí?**
- Import výpisu dává **okamžitě hodnotu** a validuje parsing/párování logiku bez závislosti na MX infra/bank API.
- Fio API je nejjednodušší API, velké % CZ OSVČ má Fio → rychlý win.
- E-mail parser je infrastruktura (MX, parsery, antispam, bezpečnost) — nejnáročnější kus, ale dlouhodobá bitva.
- PSD2 má smysl až když je uživatelská základna dost velká, aby pokryla náklady agregátoru.

### 4.2 Datový model — entity k přidání

```
┌─────────────────────────┐    ┌─────────────────────────┐
│    BankTransaction      │    │   BankAccountMailbox    │  (jen pro fázi 3)
│─────────────────────────│    │─────────────────────────│
│ Id                      │    │ Id                      │
│ BankAccountId (FK)      │◄───│ BankAccountId (FK)      │
│ ExternalId  (bank tx id)│    │ InboundEmail (unique)   │
│ TransactionDate         │    │ Provider (enum: Fio,    │
│ Amount                  │    │   KB, CSOB, CS…)        │
│ CurrencyCode            │    │ IsActive                │
│ VariableSymbol          │    └─────────────────────────┘
│ ConstantSymbol          │
│ SpecificSymbol          │    ┌─────────────────────────┐
│ CounterpartyAccount     │    │    PaymentMatch         │
│ CounterpartyName        │    │─────────────────────────│
│ Message (poznámka)      │    │ Id                      │
│ Direction (In/Out)      │◄───│ BankTransactionId (FK)  │
│ ImportSource (enum:     │    │ InvoiceId (FK, nullable)│
│   GPC, CAMT, Email, Api)│    │ ReceivedInvoiceId (FK,  │
│ RawPayload (JSON/text)  │    │   nullable)             │
│ MatchStatus (enum:      │    │ MatchedAmount           │
│   Unmatched, Matched,   │    │ MatchedBy (enum: Auto,  │
│   Manual, Ignored)      │    │   Manual)               │
│ CreatedAt / RowVersion  │    │ MatchedAt               │
└─────────────────────────┘    │ MatchedByUserId         │
                               │ Note                    │
                               └─────────────────────────┘
```

**Poznámky k návrhu:**
- `BankTransaction` je **idempotentní** — `(BankAccountId, ExternalId)` je unique key. Při opakovaném importu se duplicity ignorují.
- `PaymentMatch` je **1:1 vůči faktuře, N:1 vůči transakci** (jedna transakce může pokrýt více faktur — dávková platba).
- Faktura může mít **více `PaymentMatch`** (částečné úhrady → částky se sčítají).
- `BankTransaction.MatchStatus` odráží celkový stav; `Invoice.Status` se mění až při plné úhradě (`sum(matchedAmount) >= Invoice.TotalWithVat`).

### 4.3 Domain logika — párovací algoritmus

Pseudokód:

```csharp
// Pořadí pravidel (first match wins).
// 1. Hledej nespárované faktury ISSUER = vlastník bankovního účtu, Status = Completed,
//    Direction = Incoming, BankAccount.Currency matches Transaction.Currency.
// 2. Primární pravidlo: VS match.
//    a) Jedna faktura s daným VS:
//       - částka shodná         → Paid
//       - částka menší          → PartiallyPaid (nový status), odečte se od Remaining
//       - částka větší          → Paid + přeplatek se uloží do BankTransaction.OverpaymentAmount
//    b) Více faktur se stejným VS:
//       - zkus najít přesnou shodu částky → match té jediné
//       - jinak ponech jako nespárované, nabídni k ručnímu rozhodnutí
// 3. Sekundární: shoda podle BankAccountNumber příjemce + částka + okno ±7 dní okolo DueDate.
// 4. Terciární: fuzzy match podle counterparty name + částka. Jen jako návrh, ne auto-match.
// 5. Vše ostatní → Unmatched, viditelné v "Platby k vyřešení".

// Po každé změně BankTransaction.MatchStatus přepočítat:
//   invoice.PaidAmount = SUM(PaymentMatch.MatchedAmount WHERE InvoiceId = invoice.Id)
//   invoice.Status = invoice.PaidAmount >= invoice.TotalWithVat ? Paid : PartiallyPaid
//   invoice.PaidAt = MAX(PaymentMatch.MatchedAt) jakmile se fakturuje ekvivalent celku
```

**Status Invoice — rozšíření:**
- Přidat `EInvoiceStatus.PartiallyPaid = 6` (mezi Completed a Paid).
- Přidat `Invoice.PaidAmount decimal` (computed/persisted — rozhodnout).
- `MarkAsPaidAsync` zůstává pro manuální ovládání; **páruje interně přes PaymentMatch** (i manuální úhrada vytvoří "virtuální" `BankTransaction` s `ImportSource = Manual`, aby byl auditní záznam konzistentní).

### 4.4 Fáze 1 — MVP: Import bankovního výpisu (GPC + CAMT.053)

**Scope:**
- Nová stránka `Payments.razor` (MudBlazor grid + horní karty "Unmatched / Matched / This month").
- Upload buttonem GPC nebo CAMT.053 soubor → server-side parsing → `BankTransaction` zápis → auto-match algoritmus.
- Řádková UI akce: "Match with invoice…" (MudDialog se seznamem kandidátů), "Ignore" (např. pro osobní výběry), "Unmatch".
- Faktura page ukazuje vedle status chipu i informaci "Uhrazeno: 15 000 / 20 000 Kč" + link na `BankTransaction`.

**Parsování:**
- GPC: Czech standard, 1000-byte pevné řádky; existuje OSS parser (`Altairis.FiscalIntegration` či vlastní — jednoduchý). Interface `IBankStatementParser` → `GpcParser`, `Camt053Parser`, budoucí `FioJsonParser`.
- CAMT.053: ISO 20022 XML — řešitelné přes `XmlSerializer` + XSD z ISO. OSS knihovny existují (`CAMT-Parser`).

**DoD:**
- Unit testy parserů (snapshot fixtures pro reálné výpisy od Fio, KB, ČSOB — sanitizované).
- Integrační test: upload fixture → očekávané `BankTransaction` řádky → očekávané auto-matches.
- Playwright E2E: upload → spárování → faktura = Paid.
- Výkon: soubor 10k transakcí parse + match do 5 sekund.
- Bezpečnost: max file size 10 MB, MIME validation, anti-virus scan (už máme blob storage, projít existujícím pipeline).

### 4.5 Fáze 2 — Fio API polling

**Scope:**
- Nová entita `BankConnection`: `BankAccountId`, `Provider (Fio)`, `EncryptedToken` (Data Protection API, stejný mechanismus jako 2FA secret), `LastSyncedAt`, `LastTransactionId`.
- `FioBankApiClient` (typed HttpClient) → `GET /ib_api/rest/periods/{token}/{from}/{to}/transactions.json` + `GET /ib_api/rest/last/{token}/transactions.json` (inkrement od posledního ID).
- **Azure Function timer trigger** (15 min interval) → projde všechny aktivní `BankConnection` → zavolá Fio API → deduplikace přes `ExternalId` → auto-match.
- UI: Nastavení → Bank Accounts → "Propojit s Fio bankou" → dialog pro token + test → uložit.

**Bezpečnost:**
- Token šifrován DPAPI.
- Fio tokeny mají read-only variantu (bez PISP) — validujeme že `X-API-Permissions` neobsahuje write.
- Rate limit 1 req/30s per token → naše Function musí držet per-token lock (Redis / SQL lock table).

**DoD:**
- Unit testy FioBankApiClient (mockovaný HttpMessageHandler).
- Integration test s reálným sandbox účtem (lze požádat Fio o test token).
- Funkce nespadne, když token expiruje → označí `BankConnection.Status = Invalid`, pošle e-mail uživateli.

### 4.6 Fáze 3 — E-mailové párování

**Scope:**
- Subdoména `parovani.fakvio.cz` s MX záznamem do Azure Communication Services **Email** nebo vlastní SMTP (např. na App Service + MailKit listener).
- Doporučení: **SendGrid Inbound Parse** nebo **AWS SES → Lambda → SQS → naše Function**. Nejsme závislí na tom, zda zpráva přišla v okamžiku pádu serveru — e-mail je ve frontě.
- Každý `BankAccount` dostane unique `InboundEmail = acct-{guid8}@parovani.fakvio.cz`.
- Worker (Azure Function queue trigger) pro každý příchozí e-mail:
  1. Ověří SPF/DKIM odesílatele (whitelist domén banky).
  2. Detekuje providera podle `From:` domény (`@fio.cz`, `@csob.cz`, `@airbank.cz`…).
  3. Spustí odpovídající `IBankEmailParser` → extrahuje částku, VS, counterparty.
  4. Zapíše `BankTransaction` + spustí auto-match.
- Per-banku parsery jako pluginy (interface `IBankEmailParser` + `[BankProviderKey]` atribut; DI resolve přes `IKeyedServiceProvider`).

**Rozsah banko-parserů na start:**
- Fio (šablona známá + jako fallback k API).
- KB, ČSOB, Česká spořitelna, Air Bank, Raiffeisenbank — cílíme na pokrytí ~85 % českého SME trhu.
- Moneta, UniCredit, Creditas, Tatra, SLSP, VÚB — ve druhé vlně.

**Robustnost:**
- Pokud parser selže (nový formát), e-mail jde do `ParserFailedQueue` + Slack/e-mail alert pro tým. Uživatel vidí transakci s `RawPayload` a může spárovat ručně.
- Parsery mají verze — "KbParser v2 (od 2026-06-01)" — fallback chain.

**DoD:**
- Golden-file testy parserů (fixtures = anonymizované reálné e-maily).
- Retry policy pro worker, dead-letter queue.
- Admin UI v SysAdmin sekci pro monitoring selhavších parsů.

### 4.7 Fáze 4 — PSD2 agregátor (budoucnost, >500 tenantů)

Mimo scope tohoto zadání; připravit tak, aby `BankConnection` + `IBankApiClient` byly rozšiřitelné o nového providera `SaltEdge` bez refactoringu modelu.

### 4.8 UX — klíčové pohledy

1. **Payments list (nová page)** — `/payments`
   - Filtry: Unmatched | Matched | All; Date range; Bank account.
   - Karty vpředu: "K vyřešení: 12 plateb / 45 300 Kč" — pozornost hned.
   - Řádek: Date | Counterparty | Amount | VS | Status chip | Actions.
   - Hromadné akce: Match selected → wizard.
2. **Invoice detail — sekce "Payments"**
   - Nový panel nad Items: seznam spárovaných plateb + "Paid 15 000 / 20 000 Kč" progress bar.
   - Tlačítko "Match manually…" — dialog se seznamem nespárovaných plateb stejné měny.
3. **Client detail**
   - Historie plateb od klienta (agregováno přes `PaymentMatch.Invoice.ClientId`).
4. **Bank Account settings**
   - Připojení (Fio API / Email / Import-only).
   - Generovaný inbound e-mail (kopírovací tlačítko + návod pro nejčastější banky).
5. **Dashboard**
   - KPI "Unmatched payments" (dnes chybí).

### 4.9 Multi-tenant a bezpečnost

- Všechny nové entity (`BankTransaction`, `BankConnection`, `PaymentMatch`, `BankAccountMailbox`) jsou **tenant-scoped** (v `TenantDbContext`, per-schema).
- `InboundEmail` generovaný s **cryptographically random** 8-char suffixem → unguessable (nejde přes brute-force hádat tvary).
- E-maily z neznámých domén → auto-reject + log.
- Šifrování citlivých polí (`BankConnection.EncryptedToken`) přes Data Protection API s key ring v Blob storage (stejně jako 2FA).
- Audit log: každý match/unmatch → `AppLog` event s `UserId`, `CorrelationId`.

### 4.10 Lokalizace

Všechny nové UI stringy přes `IStringLocalizer` + `SharedResource.resx/cs` podle dnešního patternu. Seznam nových klíčů (nástřel ~40):
`Payments_Title`, `Payments_Unmatched`, `Payments_Matched`, `Payment_MatchButton`, `Payment_IgnoreButton`, `Payment_Status_Unmatched`, `Payment_Status_Matched`, `Payment_Status_Partial`, `Payment_ImportFile_Button`, `Payment_ConnectFio_Button`, `Payment_InboundEmail_Copy`, … (úplný seznam při implementaci).

### 4.11 Testovací strategie

- **Unit testy** (Tests.Unit): parsery (GPC/CAMT/e-mail per banku), matching algoritmus, status přechody.
- **Integration testy** (Tests.Integration): import výpisu E2E přes WebApplicationFactory + InMemoryDb; Fio API přes `HttpMessageHandler` sub.
- **Playwright** (Tests.Playwright): upload GPC → spárování → faktura Paid → emailnotifikace odběrateli. Scénář "částečná úhrada".
- **Load test**: import 100k transakcí v jednom souboru → do 60 s.

---

## 5. Odhad objemu práce

| Fáze                             | Backend | Frontend | Testy | Infra | Celkem (MD) |
|----------------------------------|:-------:|:--------:|:-----:|:-----:|:-----------:|
| 1 — Import výpisu GPC + CAMT     |   6     |    4     |   3   |  0.5  |    **13.5** |
| 2 — Fio API polling + Function   |   4     |    2     |   2   |  1    |     **9**   |
| 3 — E-mail parser + MX infra     |   8     |    3     |   4   |  3    |    **18**   |
| Datový model + status rozšíření  |   3     |    1     |   1   |  0    |     **5**   |
| UX: Payments page, dashboard     |   1     |    5     |   1   |  0    |     **7**   |
| **Celkem pro MVP (F1+F2)**       | **13**  |  **11**  | **7** | **1.5** | **~32 MD** |

> MD = člověko-dní vývojáře. Pro jedno developera cca **7 týdnů na MVP** (F1+F2+datový model+UX), fáze 3 extra ~4 týdny.

---

## 6. Rizika

| Riziko                                       | Dopad   | Mitigace                                                |
|----------------------------------------------|:-------:|---------------------------------------------------------|
| Banka změní formát e-mailové notifikace      | Vysoký  | Fallback chain parserů, golden-file testy, DLQ + alerty |
| Fio API token expirace / rate-limit 429      | Střední | Retry policy s exp backoff, per-token distrib lock      |
| False-positive match (špatná faktura Paid)   | Vysoký  | Auto-match **jen** při exact amount + VS; jinak suggest |
| MX infra down → ztráta notifikace            | Vysoký  | SendGrid/SES queue s 48h retention; idempotentní import |
| Regulatorní (PSD2) — zda jsme AISP?          | Nízký   | Dokud pouze čteme e-maily a klient nám dobrovolně dává  |
|                                              |         | Fio token, nejsme AISP. PSD2 až s agregátorem.          |
| Více měn — CZK faktura × EUR platba          | Střední | Match jen pokud shoda currency; jinak manual resolve    |

---

## 7. Akceptační kritéria MVP (fáze 1 + 2)

1. Uživatel nahraje GPC soubor z Fio → v Payments page vidí všechny transakce → Invoice s shodným VS a částkou je **automaticky Paid**.
2. Uživatel nahraje CAMT.053 z KB → stejné chování.
3. Uživatel v Nastavení → Bank Account → "Připojit Fio" → vloží token → do 15 minut se nové pohyby automaticky stáhnou a spárují.
4. Částečná úhrada (VS sedí, částka menší) → Invoice má status **PartiallyPaid**, dashboard to ukazuje.
5. Platba, která se nespáruje, je viditelná v Payments → tab Unmatched + počítadlo v navigaci.
6. Uživatel může platbu spárovat ručně s fakturou přes MudDialog.
7. Všechny operace jsou tenant-scoped, audit-logované, pokryté ≥80 % unit + integration testy.
8. UI je lokalizované CZ/EN, plně funkční na mobile (MAUI app).

---

## 8. Open questions (k diskuzi před kick-offem)

1. **Částečné úhrady** — má Invoice mít `PartiallyPaid` status, nebo jen `PaidAmount` pole a status zůstane binární (Completed/Paid)? (Doporučení: explicitní status, kvůli filtrům a dashboardu.)
2. **Manuální "Mark as Paid"** — zrušit, nebo zachovat jako shortcut (který interně vytvoří virtuální `BankTransaction`)? (Doporučení: zachovat, s auditem.)
3. **Dobropisy / Credit notes** — párování na odchozí platby. V MVP ano, nebo odložit? (Doporučení: odložit — základ je inbound.)
4. **Multi-tenant inbound e-mail** — `acct-{guid}@parovani.fakvio.cz` nebo `{tenant}.{guid}@parovani.fakvio.cz`? (Doporučení: per-account GUID je jednodušší a stačí.)
5. **Import historie** — při připojení Fio API načíst historii 3 měsíce zpět, nebo jen budoucnost? (Doporučení: default 90 dní zpět, uživatel si může rozšířit.)
6. **iDoklad-style přeplatky** — evidovat pole `Overpayment` na `BankTransaction` a zobrazit klientovi jako kredit? (Doporučení: v MVP ne, jen varování; kredit je další epic.)

---

## 9. Zdroje (rešerše)

- [Fakturoid — Párování plateb s bankou](https://www.fakturoid.cz/podpora/automatizace/parovani-plateb-s-bankou)
- [Fakturoid — Fio API polling](https://www.fakturoid.cz/podpora/parovani/fio-api)
- [Fakturoid — per-banku návody (KB, ČSOB, UniCredit, Air, ČS, RB, Tatra)](https://www.fakturoid.cz/podpora/parovani)
- [iDoklad — Nastavení Banka](https://www.idoklad.cz/podpora/nastaveni-banka)
- [iDoklad — Spárujte si iDoklad s bankou](https://www.idoklad.cz/blog/sparujte-si-idoklad-s-bankou-hned-vite-o-zaplacenych-fakturach)
- [Fakturovač.cz — hlavní stránka](https://www.fakturovac.cz/)
- [SuperFaktura — automatické párování plateb](https://www.superfaktura.cz/blog/automaticke-parovani-plateb/)
- [Fio API Bankovnictví — oficiální stránka](https://www.fio.cz/bankovni-sluzby/api-bankovnictvi)
- [Fio API — PDF dokumentace](https://www.fio.cz/docs/cz/API_Bankovnictvi.pdf)
- [Czech Standard for Open Banking (COBS)](https://github.com/Czech-BA/COBS)
- [cz-banking-psd2 — seznam CZ developer portálů](https://github.com/JirkaChadima/cz-banking-psd2)
- [KB — PSD2 services](https://www.kb.cz/en/kbapi/psd2-services)
