# Fakvio — Uživatelská příručka (USERGUIDE)

> Praktický průvodce pro uživatele firmy (tenant). Vysvětluje **co a kde** v aplikaci najdete a **jak** to používat — ne technické detaily. Cílová skupina: účetní, fakturant nebo majitel firmy.
>
> **MAINTENANCE RULE (povinné):** Pokud přidáváš nebo měníš funkci viditelnou uživateli (nová stránka, nová akce, změna chování formuláře, nový stav faktury, nový export…), **musíš aktualizovat odpovídající sekci tohoto souboru**. Bez záznamu PR neprochází review.

---

## Obsah

0. [Registrace firmy](#0-registrace-firmy)
1. [Přehled (Dashboard)](#1-přehled-dashboard)
2. [Faktury](#2-faktury)
3. [Dobropisy](#3-dobropisy)
4. [Proforma faktury a daňový doklad o přijaté platbě](#4-proforma-faktury-a-daňový-doklad-o-přijaté-platbě)
5. [Klienti](#5-klienti)
6. [Šablony faktur (InvoiceTemplates)](#6-šablony-faktur-invoicetemplates)
7. [Šablony dokumentů (ContentTemplates — PDF + email)](#7-šablony-dokumentů-contenttemplates--pdf--email)
8. [Přijaté faktury](#8-přijaté-faktury)
9. [Číselné řady](#9-číselné-řady)
10. [Nastavení firmy](#10-nastavení-firmy)
11. [DPH sazby a režimy](#11-dph-sazby-a-režimy)
12. [Přehled DPH + EPO export](#12-přehled-dph--epo-export)
13. [AI asistent](#13-ai-asistent)
14. [Upomínky (Dunning)](#14-upomínky-dunning)
15. [Párování plateb](#15-párování-plateb)
16. [Lokalizace — přepínání jazyka CZ/EN](#16-lokalizace--přepínání-jazyka-czen)
17. [Notifikace](#17-notifikace)
18. [Příjem faktur emailem](#18-příjem-faktur-emailem)
19. [Nastavení hesla a první přihlášení](#19-nastavení-hesla-a-první-přihlášení)
20. [Napojení vlastního AI klienta (MCP server)](#20-napojení-vlastního-ai-klienta-mcp-server)

---

## 0. Registrace firmy

**Stránka:** `/register` (dostupná bez přihlášení)

1. Vyplňte e-mail, jméno, příjmení, název firmy a IČO.
2. Klikněte „Načíst z ARES" — systém doplní oficiální název firmy **a sídlo**
   (ulice, PSČ, město, země) z obchodního rejstříku ARES.
3. Sekce „Sídlo firmy" je editovatelná — pokud ARES nemá aktuální údaje,
   adresu jednoduše přepište. Uloží se to, co ve formuláři vidíte.
4. Klikněte „Zaregistrovat se" — na zadaný e-mail přijde odkaz pro nastavení hesla.

Sídlo se uloží k vaší firmě jako primární adresa a používá se jako blok vystavitele
na fakturách (PDF). Změnit ho lze později v „Nastavení firmy".

**Plátcovství DPH se odvodí z ARES:** má-li vaše firma v registru DIČ, založí se rovnou
jako plátce DPH (a DIČ se uloží). Bez DIČ v ARES — nebo když je registr nedostupný —
vznikne firma jako neplátce. DIČ v registru ale není právní záruka plátcovství, proto si
nastavení po přihlášení zkontrolujte v „Nastavení firmy"; tam ho lze kdykoli přepnout.

**Pozor na jednu výjimku:** když ulici, PSČ i město **úplně vymažete**, systém to bere
jako „nevyplněno" a doplní adresu z ARES. Firma bez adresy vznikne jen tehdy, když ji
nemá ani ARES (nebo je registr nedostupný) — pak ji doplňte v „Nastavení firmy"
před vystavením první faktury. Chcete-li adresu odstranit, udělejte to tam.

---

## 1. Přehled (Dashboard)

**Stránka:** `/` (úvodní stránka po přihlášení)

Dashboard zobrazuje aktuální přehled vaší firmy na jednom místě:

### Připravenost k fakturaci

Pokud vaší firmě chybí něco, co musí být na daňovém dokladu, uvidíte nahoře na dashboardu
banner se seznamem chybějících položek. U každé položky je odkaz **Doplnit**, který vás
zavede přímo na stránku, kde ji vyplníte.

Banner rozlišuje dvě závažnosti:

| Barva | Význam |
|-------|--------|
| Červená — „Než začnete fakturovat, doplňte tato nastavení" | Bez těchto údajů aplikace **odmítne vystavit fakturu** (viz [§2.3.1](#231-když-vystavení-skončí-chybou-nedokončené-nastavení-firmy)) |
| Oranžová — „Doporučená nastavení k doplnění" | Fakturovat můžete, ale narazíte později v konkrétní funkci (typicky EPO export přiznání k DPH) |

Máte-li v účtu víc vystavitelů, je u položky uvedeno, které firmy se týká.
Když je vše vyplněné, banner se nezobrazuje vůbec.

Stejný banner najdete i v detailu faktury ve stavu Draft — tam je omezený na vystavitele
té konkrétní faktury, takže vidíte přesně to, co by vystavení zablokovalo.

### KPI karty (klikatelné — navigují na příslušný seznam)

| Karta | Co zobrazuje | Navigace |
|-------|-------------|----------|
| Faktury splatné tento měsíc | Počet vydaných faktur se splatností v aktuálním měsíci + celkové součty bez/s DPH | `/invoices` |
| Aktivní klienti | Počet klientů se stavem Aktivní | `/clients` |
| Nesplaceno | Celková částka v CZK u vydaných (Completed) ale nezaplacených faktur | `/invoices` |
| Nespárované platby | Počet bankovních transakcí čekajících na párování (zobrazí se jen když > 0) | `/payments` |
| Upozornění | Počet otevřených alertů (zobrazí se jen když > 0) — kliknutím se rozbalí detail panel | expanduje panel |
| Notifikace (zvoneček) | Ikona v horní liště — badge s počtem nepřečtených notifikací, kliknutím dropdown | viz [§17](#17-notifikace) |

### Grafy

- **Faktury podle stavu** — koláčový (donut) graf: Draft / Issued / Paid / Credited
- **Top klienti podle obratu** — koláčový graf: top 10 klientů, seřazeni sestupně

### Pojistné zálohy

Pokud máte nastavenu daňovou konfiguraci, zobrazí se:
- Datum příští platby zálohy pojistného + kolik dnů zbývá (červeně pokud prošlo nebo zbývají ≤ 5 dní)
- Výše měsíčních záloh — sociální + zdravotní pojistné

### Přehled po splatnosti

Tabulka faktur, které překročily datum splatnosti. Každý řádek je klikatelný — otevře detail faktury.

### Poslední faktury

Tabulka posledních 5 faktur s číslem, klientem, datem a stavem. Kliknutím se otevře detail.

### Widget upomínek

Zobrazí se pokud máte aktivní upomínkový systém: počty Draft/Sent/Failed upomínek + poslední záznamy.

### Quick Start

Pro nové uživatele: průvodce v krocích (Klienti → Faktury).

---

## 2. Faktury

**Stránka:** `/invoices`

### 2.1 Přehled faktur

Grid zobrazuje vydané faktury s těmito sloupci:
- Číslo dokladu (chip s ikonou)
- Klient
- Datum vystavení
- Datum uskutečnění zdanitelného plnění (DUZP) — zobrazí se jen pokud jste plátce DPH
- Datum splatnosti
- Celková částka s DPH + měna
- Stav (chip barevný — viz tabulka stavů)
- Upozornění (ikonka varování pokud faktura má otevřený alert)
- Odesláno emailem (ikonka obálky s datem posledního odeslání)

**Filtrace:**
- Globální textové hledání (číslo dokladu, klient, poznámky) — debounce 300 ms
- Sloupcové filtry (kliknutím na záhlaví sloupce) — datum funguje s operátory =, >, <, between
- Sloupcové řazení (kliknutím na záhlaví)

**Hromadné operace** (bulkové — po zaškrtnutí checkboxů):
- Vystavit (pouze Draft faktury)
- Označit jako zaplacené (pouze Issued faktury)
- Stáhnout PDF jako ZIP
- Odeslat emailem
- Smazat (Draft nebo Issued)

### 2.2 Stavy faktury

| Stav | Barva | Význam |
|------|-------|--------|
| Draft (Koncept) | Šedá | Faktura je ve stavu konceptu, lze editovat a smazat |
| Issued (Vydaná) | Modrá | Faktura byla vystavena, čeká na zaplacení |
| Paid (Zaplacená) | Zelená | Faktura je uhrazena |
| Creditnoted (Dobropisována) | Oranžová | K faktuře byl vystaven dobropis |
| Deleted (Smazaná) | Tmavá | Faktura je soft-smazaná, lze ji obnovit |

### 2.3 Akce v řádku gridu

| Ikona | Akce | Podmínka |
|-------|------|----------|
| Oko | Zobrazit detail | vždy |
| Odeslat | Vystavit fakturu (Draft → Issued) | pouze Draft |
| Smazat | Smazat fakturu | pouze Draft |
| Zaplatit | Označit jako zaplacenu | pouze Issued |
| PDF | Stáhnout PDF (výběr šablony) | vždy |
| Email | Odeslat emailem | vždy |
| Kopírovat | Zkopírovat fakturu jako nový Draft | ne CreditNote, ne Deleted |
| Tři tečky | Sekundární akce (Vytvořit šablonu) | vždy |
| Obnovit | Obnovit smazanou fakturu | pouze Deleted |

### 2.3.1 Když vystavení skončí chybou „nedokončené nastavení firmy"

Fakturu lze vystavit (Draft → Issued) jen tehdy, když má **vystavitel dané faktury**
vyplněné vše, co na daňovém dokladu musí být. Dokud něco chybí, aplikace vystavení
**odmítne** a v chybové hlášce uvede, co chybí. Podle označení v hlášce najdete
v tabulce, kam pro nápravu jít:

| V hlášce uvidíte | Co chybí | Kde to doplníte |
|------------------|----------|-----------------|
| `ISSUER_ADDRESS_INCOMPLETE` | Adresa vystavitele (ulice, město, PSČ, země) | Nastavení firmy (`/my-company`) |
| `ISSUER_REGISTRATION_NUMBER_MISSING` | IČO | Nastavení firmy |
| `ISSUER_TAX_NUMBER_MISSING` | DIČ — jen pokud jste plátce DPH | Nastavení firmy |
| `ISSUER_BANK_ACCOUNT_MISSING` | Bankovní účet | Nastavení firmy |
| `NUMBER_SEQUENCE_MISSING` | Aktivní výchozí číselná řada pro daný typ dokladu | Číselné řady (`/number-sequences`) |
| `ISSUER_MISSING` | Vystavitel není v účtu vůbec založený | Nastavení firmy |

Co je dobré vědět:

- Kontroluje se **vystavitel té konkrétní faktury**. Máte-li v účtu víc vystavitelů,
  nedokončený vystavitel blokuje jen své vlastní doklady.
- Kontroluje se jen typ dokladu, který právě vystavujete — chybějící řada dobropisů
  nebrání vystavení běžné faktury.
- Nevyplněná hlavička EPO (podání DPH) je jen upozornění a **fakturaci nebrání**.
- Odmítnutá faktura zůstane beze změny ve stavu Draft a nespotřebuje číslo z číselné
  řady. Po doplnění údajů akci prostě zopakujte.
- Totéž platí pro hromadné vystavení (u odmítnutých faktur uvidíte důvod v souhrnu
  výsledku, zbytek dávky se vystaví) i pro vytvoření faktury ze šablony se zapnutým
  automatickým vystavením — tam se faktura vytvoří jako Draft a jen se nevystaví.

### 2.4 Vytvoření nové faktury

**Tlačítko:** „Nová faktura" na stránce `/invoices`

**Postup:**
1. Klikněte na „+ Nová faktura" → otevře se stránka `/invoices/create`
2. Vyberte klienta — po výběru se automaticky doplní fakturační nastavení (prefix/suffix, vlastní číselná řada, splatnost)
3. Vyberte typ dokladu (Faktura / Dobropis / Proforma / Daňový doklad)
4. Vyberte měnu
5. Číslo dokladu — nechte prázdné pro automatické číslování, nebo zadejte vlastní
6. Vyplňte data (datum vystavení, datum splatnosti, DUZP)
7. Přidejte položky faktury v editoru položek
8. Klikněte „Vytvořit"

**Vytvoření ze šablony:**
- Z gridu: rozbalovací menu vedle „+ Nová faktura" → „Nová z šablony" → vyberte šablonu + klienta
- Z řádku faktury: tři tečky → „Vytvořit šablonu" (uloží fakturu jako šablonu pro budoucí použití)

**Vytvoření importem:**
- Z gridu: rozbalovací menu → „Import faktur" → `/invoices/import?target=IssuedInvoice`

### 2.5 Editace faktury

- Detail faktury na `/invoices/{id}` — klikněte na řádek nebo ikonu oka
- Ve stavu Draft: přímá editace, tlačítko „Uložit"
- Ve stavu Issued/Paid: pouze zobrazení; pro editaci vraťte fakturu do Draft (pokud to systém umožňuje)
- Unsaved changes guard: při navigaci pryč bez uložení se zobrazí potvrzovací dialog

### 2.6 Kopírování faktury

- Ikonka kopírování v řádku gridu → vytvoří nový Draft se stejnými daty
- Nová faktura dostane nové číslo dokladu (z číselné řady)
- Po zkopírování se zobrazí toast se číslem nové faktury + grid se obnoví
- Kopírování není dostupné pro Dobropisy ani Smazané faktury

### 2.7 Export PDF

- Kliknutím na ikonu PDF v řádku gridu
- Výběr šablony: výchozí šablona nebo vlastní (pokud máte nakonfigurované ContentTemplates)
- Soubor se stáhne pojmenovaný `Faktura_{číslo}.pdf`

**Hromadný PDF export:** zaškrtněte faktury → toolbar → PDF → stáhne se ZIP archiv

### 2.8 Export ISDOC

Dostupný z detailu faktury — viz příslušné tlačítko. ISDOC je český standard pro elektronické faktury.

**Hromadný ISDOC export:** zaškrtněte faktury → toolbar → „Stáhnout ISDOC" → stáhne se ZIP archiv s `.isdoc` soubory. Koncepty (Draft) se přeskakují — nevydaná faktura nemá právně platný ISDOC.

### 2.9 Odeslání emailem

- Z gridu: ikona emailu → dialog „Odeslat fakturu"
- Zadejte emailovou adresu příjemce (předvyplněna z nastavení klienta)
- Klikněte „Odeslat"
- Po odeslání se v řádku zobrazí ikonka zelené obálky s datem

**Hromadné odeslání:** zaškrtněte faktury → toolbar → Email → odešle se na výchozí email každého klienta

---

## 3. Dobropisy

**Stránka:** `/invoices?type=CreditNote`

Dobropis (credit note) opravuje nebo ruší vydanou fakturu.

### Vytvoření dobropisu

**Ze stránky Faktury:**
1. Klikněte „+ Nový dobropis" (nebo z gridu faktur přes Nová faktura → typ Dobropis)
2. Vyberte **původní fakturu** (pole Původní faktura — povinné pro CreditNote)
3. Doplňte položky a uložte

**Alternativně:** Otevřete detail faktury → tlačítko „Vystavit dobropis" (pokud existuje, závisí na konfiguraci)

### Specifika dobropisu

- V gridu faktur jsou dostupné přes URL parametr `?type=CreditNote`
- Číslo dokladu začíná prefixem `CN-` (dle konfigurace číselné řady)
- Kopírování dobropisu není dostupné (ikonka kopírování se nezobrazuje)
- Po vystavení dobropisu se původní faktura označí stavem `Creditnoted`

---

## 4. Proforma faktury a daňový doklad o přijaté platbě

### Proforma faktura

**Stránka:** `/invoices?type=Proforma`

Proforma (záloha) je nezávazný doklad před přijetím platby. Číslo dokladu: prefix `PF-`.

**Vytvoření:** stejný postup jako standardní faktura, typ = Proforma.

### Daňový doklad o přijaté platbě (DPP)

**Stránka:** `/invoices?type=TaxReceiptForAdvance`

Vystavuje se po přijetí zálohy. Číslo dokladu: prefix `DPP-`.

**Automatické vystavení:** systém může automaticky vystavit DPP po označení proformy jako zaplacené (závisí na nastavení).

---

## 5. Klienti

**Stránka:** `/clients`

### 5.1 Seznam klientů

Grid s klienty: název firmy, IČ, DIČ, město, plátce DPH (ano/ne), stav (aktivní/neaktivní).

Kliknutím na řádek nebo ikonu oka otevřete detail klienta.

### 5.2 Nový klient

1. Klikněte „+ Nový klient" → `/clients/create`
2. Zadejte IČ a klikněte „ARES" — systém automaticky doplní název, adresu a DIČ z ARES (CZ obchodní rejstřík)
3. Upravte nebo ručně zadejte zbývající údaje:
   - Název firmy / obchodní název
   - DIČ, plátce DPH (přepínač)
   - Jazyk dokumentů (cs / en) — v tomto jazyce se generují PDF a emaily pro tohoto klienta
   - Barva (volitelná) — barevně označí řádky tohoto klienta v gridu faktur
4. Přidejte adresy (fakturační, doručovací…) tlačítkem „+ Adresa"
5. Přidejte kontakty (email, telefon)
6. Přidejte bankovní účty
7. Klikněte „Vytvořit"

### 5.3 Fakturační nastavení klienta

V detailu klienta sekce „Fakturační nastavení" (BillingSettings):

| Pole | Popis |
|------|-------|
| Prefix čísla dokladu | Přidá se před číslo faktury (např. `FIRMA-`) |
| Suffix čísla dokladu | Přidá se za číslo faktury |
| Vlastní číselná řada | Pokud nastavena, použije se místo výchozí řady |
| Splatnost (dny) | Výchozí počet dní od data vystavení do data splatnosti |

Tyto hodnoty se automaticky aplikují při vytváření nové faktury pro tohoto klienta.

### 5.4 Historie faktur klienta

V detailu klienta se zobrazuje tabulka faktur tohoto klienta (server-side stránkování).

---

## 6. Šablony faktur (InvoiceTemplates)

**Stránka:** `/invoice-templates`

Šablona faktury ukládá opakované položky (např. měsíční paušál) pro rychlé vytvoření nové faktury.

### Vytvoření šablony

**Ze šablon:** „+ Nová šablona" → `/invoice-templates/create`

**Z existující faktury:**
1. V gridu faktur: tři tečky (⋮) v řádku → „Vytvořit šablonu"
2. Zadejte název šablony a volitelně popis
3. Systém zkopíruje položky faktury do šablony

### Použití šablony pro novou fakturu

**Z gridu faktur:**
1. Rozbalovací menu vedle „+ Nová faktura" → „Nová z šablony"
2. Vyberte šablonu (šablona může mít nastaveného výchozího klienta — pak se předvyplní)
3. Vyberte klienta
4. Klikněte „Vytvořit" → otevře se stránka vytvoření faktury s předvyplněnými položkami

### Filtrace šablon

Grid šablon umožňuje filtrovat podle:
- Textového hledání (název)
- Typu dokladu (Faktura / Dobropis)
- Stavu (aktivní / neaktivní)

---

## 7. Šablony dokumentů (ContentTemplates — PDF + email)

**Stránka:** `/content-templates`

ContentTemplates jsou HTML šablony pro generování PDF a emailů. Lze je editovat v WYSIWYG editoru.

### Typy šablon

| Skupina | Typy |
|---------|------|
| PDF | InvoicePdf, CreditNotePdf, AdvanceInvoicePdf (Proforma), TaxReceiptForAdvancePdf |
| Email | InvoiceEmail, ReminderEmail, InvitationEmail, PasswordResetEmail |
| Systémové | Šablony upravované SysAdminem (globální) |

### Filtrování

Čipy v horní části: Vše / PDF / Email / Systémové

### Editace šablony

Detail šablony `/content-templates/{id}` → WYSIWYG editor (Quill) pro HTML obsah.

**Placeholdery** (dostupné v PDF/email šablonách):
- `{{IssuerName}}`, `{{IssuerAddress}}`, `{{ClientName}}`, `{{ClientAddress}}`
- `{{InvoiceNumber}}`, `{{IssueDate}}`, `{{DueDate}}`, `{{TaxableSupplyDate}}`
- `{{TotalWithoutVat}}`, `{{TotalVat}}`, `{{TotalWithVat}}`, `{{CurrencyCode}}`
- `{{Items}}` (tabulka položek), `{{Notes}}`, `{{PaymentInfo}}`

---

## 8. Přijaté faktury

**Stránka:** `/received-invoices`

Správa faktur, které vaše firma přijala od dodavatelů.

### Import přijaté faktury

Dvě možnosti:
1. **Ruční zadání:** „+ Nová přijatá faktura" → formulář
2. **Import:** „Import" → `/received-invoices/import?target=ReceivedInvoice` → nahrání souboru (PDF/XML)

### Stavy přijatých faktur

Filtrovat lze podle stavu pomocí výběrového pole nad gridem.

### Filtrování a řazení v gridu

Kromě fulltextového vyhledávání nad gridem lze filtrovat přímo v řádku filtrů
pod hlavičkou sloupců — číslo dokladu a dodavatel (obsahuje text), stav (výběr),
datum vystavení a splatnosti (operátory =, >, <, mezi). Kliknutím na hlavičku
sloupce se řadí; druhé kliknutí obrátí směr.

### Stažení příloh přijatých faktur

Sloupec **Přílohy** v gridu zobrazuje počet příloh faktury a tlačítko stažení:

- **1 příloha** → stáhne se přímo původní soubor.
- **Více příloh** → stáhne se ZIP `Prilohy_{číslo dokladu}.zip`.
- Faktury bez příloh tlačítko nemají.

**Hromadné stažení:** označte faktury zaškrtávátky → v panelu hromadných akcí
klikněte na „Stáhnout přílohy". Stáhne se jeden ZIP se složkou pro každou
fakturu (pojmenovanou podle čísla dokladu). Faktury bez příloh se přeskočí.

### Export ISDOC

Přijatou fakturu lze exportovat do formátu ISDOC (český standard elektronické
fakturace — import do Pohody, Money S3, Helios apod.):

- **Z detailu faktury:** tlačítko „Stáhnout ISDOC" v horní liště. Dodavatelem
  dokumentu je dodavatel faktury, odběratelem vaše firma.
- **Hromadně:** označte faktury zaškrtávátky → v panelu hromadných akcí
  „Stáhnout ISDOC" → stáhne se jeden ZIP s `.isdoc` soubory pojmenovanými
  podle čísel dokladů.

### Přehled DPH z přijatých faktur

Data z přijatých faktur se projevují v přehledu DPH (sekce Vstupní DPH) na stránce `/vat-report`.

---

## 9. Číselné řady

**Stránka:** `/number-sequences`

Konfigurace formátů číslovacích řad pro dokumenty.

### Formáty číselných řad

Grid Formátů zobrazuje dostupné formáty:

| Pole | Popis |
|------|-------|
| Název | Identifikátor formátu |
| Vzor (FormatPattern) | Šablona čísla — např. `INV-{YEAR}-{SEQ:4}` |
| Počet číslic | Délka pořadového čísla (s leading zeros) |
| Reset ročně | Pořadové číslo se každý rok resetuje na 1 |
| Reset měsíčně | Pořadové číslo se každý měsíc resetuje na 1 |

**Dostupné tokeny ve vzoru:**
- `{YEAR}` — aktuální rok (4 číslice)
- `{MONTH}` — aktuální měsíc (2 číslice)
- `{SEQ:N}` — pořadové číslo s N čísticemi

### Přiřazení číslovací řady

Sekce „Číselné řady" zobrazuje přiřazení konkrétní číselné řady ke každému typu dokladu (Faktura, Dobropis, Proforma, DPP).

Lze přiřadit:
- Výchozí systémový formát
- Vlastní formát (kliknutím na řádek → dialog editace)

### Když číselná řada chybí nebo je vypnutá

Číslo dokladu se generuje **výhradně** z přiřazené číselné řady. Pokud pro daný typ
dokladu žádná aktivní výchozí řada neexistuje, je přiřazená řada deaktivovaná, nebo se
číslo z jiného důvodu nepodaří vygenerovat, vytvoření dokladu **skončí chybou**.

Aplikace v takovém případě nikdy nepřidělí náhradní číslo mimo vaši řadu — číslování
dokladů musí zůstat souvislé a předvídatelné. Chybová hláška uvádí typ dokladu i stránku
`/number-sequences`, kde řadu nastavíte; po nastavení aktivní výchozí řady akci zopakujte.

Zvláštní případ je **souběh** — dva doklady si sáhnou pro číslo ze stejné řady ve stejný
okamžik. Aplikace se pokus několikrát zopakuje sama, a když ani pak neuspěje, vytvoření
dokladu skončí chybou. Tady není nic špatně nastaveného: hláška to výslovně říká a stačí
akci zopakovat.

---

## 10. Nastavení firmy

**Stránka:** `/my-company`

Správa informací o vaší firmě (vydavatele faktur).

### Co lze nastavit

**Základní informace:**
- IČ — tlačítko „ARES" automaticky doplní z registru
- Název firmy, obchodní název
- DIČ, plátce DPH

**Adresy:** sídlo, provozovna, fakturační adresa (přes dialog Adresa)

**Kontakty:** email, telefon

**Bankovní účty:**
- Přidávání přes dialog „Bankovní účet"
- Formát: číslo účtu, kód banky, IBAN, BIC/SWIFT
- QR kód platba — systém generuje QR kód pro faktury automaticky

**Email pro příjem faktur:**
- Aktivace unikátní emailové adresy pro automatický příjem faktur — viz [§18](#18-příjem-faktur-emailem)

**Cloud úložiště:**
- Napojení cloudového úložiště pro ukládání dokumentů

**Nastavení EPO (jen role Admin):**
- Hlavičkové údaje pro elektronické podání přiznání k DPH a kontrolního hlášení — viz [§12](#12-přehled-dph--epo-export)
- Kód finančního úřadu a kód územního pracoviště jsou povinné, bez nich EPO export nelze vygenerovat
- Kontaktní telefon, kontaktní e-mail a jméno oprávněné osoby jsou volitelné (uložený kontaktní
  e-mail zatím nejde vymazat, jen přepsat jiným)
- Sekce má vlastní tlačítko „Uložit" — ukládá se nezávisle na tlačítku „Upravit" nahoře
- **Známé omezení:** uložení dnes projde jen správci systému. U role Admin skončí chybou —
  než bude opraveno, požádejte o vyplnění správce systému.

### Jak editovat

1. Klikněte „Upravit" (tužka) v pravém horním rohu
2. Upravte pole
3. Klikněte „Uložit"

Při navigaci pryč bez uložení se zobrazí potvrzovací dialog.

---

## 11. DPH sazby a režimy

**Stránka:** `/vat-rates`

### DPH sazby

Grid zobrazuje dostupné DPH sazby:

| Pole | Popis |
|------|-------|
| Název | Popis sazby (např. „Základní sazba", „Snížená sazba") |
| Sazba | Procento (např. 21 %, 12 %) |
| Platí od / Platí do | Časové vymezení platnosti sazby |
| Typ | Standardní / Snížená |
| Výchozí | Hvězdička = výchozí sazba v položkách faktury |

Kliknutím na řádek nebo „+ Nová sazba" lze editovat nebo přidávat.

### DPH režimy (EVatRegime) — na položkách faktury

Každá položka faktury má DPH regime:

| Režim | Kdy použít |
|-------|-----------|
| **Standard** | Standardní tuzemská transakce s DPH |
| **ReverseCharge (PDP)** | Přenesená daňová povinnost — kupující odvádí DPH. Povinné uvedení kódu §92 (ReverseChargeCode). |
| **Exempt** (Osvobozeno) | Plnění osvobozené od DPH (§51–§62 ZDPH) |
| **OutOfScope** (Mimo scope) | Plnění, které není předmětem DPH |

**Přenesená daňová povinnost (PDP / Reverse Charge):**
- Nastavte regime = ReverseCharge
- Vyberte příslušný kód §92b/c/d/e z číselníku MFČR (pole „Kód přenesené DPH")
- Faktura se správně zobrazí v kontrolním hlášení (DPHKH1)

---

## 12. Přehled DPH + EPO export

**Stránka:** `/vat-report`

### Přehled DPH

1. Vyberte období (datum od–do) nebo použijte rychlé filtry (Aktuální měsíc, Předchozí měsíc, Aktuální čtvrtletí, Předchozí čtvrtletí)
2. Klikněte „Generovat"

**Zobrazené informace:**
- Výstupní DPH (z vydaných faktur): celková výše + počet faktur
- Vstupní DPH (z přijatých faktur): celková výše + počet faktur
- Daňová povinnost = Výstupní − Vstupní (kladná = odvod, záporná = nadměrný odpočet)
- Výnosy, náklady, zisk
- Detailní rozpad výstupního DPH po sazbách
- Detailní rozpad vstupního DPH po sazbách

### EPO export (Elektronické podání daňového přiznání)

**Dostupné pouze pro plátce DPH.**

Sekce „EPO Export" umožňuje stáhnout dva soubory pro portál EPO MFČR:

| Soubor | Typ | Obsah |
|--------|-----|-------|
| DPHDP3 | XML | Daňové přiznání k DPH |
| DPHKH1 | XML | Kontrolní hlášení DPH |

**Postup:**
1. Vyberte rok a typ období (Měsíční / Čtvrtletní)
2. Vyberte konkrétní měsíc nebo čtvrtletí
3. Klikněte „Stáhnout DPHDP3" nebo „Stáhnout DPHKH1"
4. Vygenerovaný XML soubor nahrajte na portál EPO (https://adisepo.mfcr.cz)

### Chybějící nastavení EPO

Export vyžaduje vyplněný kód finančního úřadu a kód územního pracoviště. Pokud chybí,
místo staženého souboru se zobrazí upozornění se seznamem chybějících polí:

- **Máte roli Admin** — upozornění nabídne tlačítko „Přejít do nastavení firmy", které vás
  přenese na `/my-company` do sekce „Nastavení EPO" (viz [§10](#10-nastavení-firmy)).
- **Nemáte roli Admin** — sekce nastavení je pro vás skrytá, takže upozornění místo odkazu
  napíše, že pole musí doplnit administrátor firmy.

---

## 13. AI asistent

**Přístup:** dvě cesty ke stejnému panelu:

- ikona robota (🤖) v horní liště — první ikona vpravo od názvu aplikace,
- položka **AI asistent** v hlavním menu vlevo (hned pod Přehledem).

Obojí otevře/zavře boční panel (Chat Drawer) na pravé straně.

**Panel si pamatuje, jestli byl otevřený.** Pokud ho necháte otevřený a stránku znovu
načtete, otevře se sám. Stav se pamatuje pro daný prohlížeč a zařízení — na mobilu tedy
můžete mít panel zavřený, i když ho na počítači necháváte otevřený.

**Na mobilu** panel zabírá celou šířku displeje. Zavřete ho křížkem vpravo nahoře v panelu
nebo klepnutím mimo panel. Plocha pro přetažení souboru se na telefonu nezobrazuje —
soubor připojíte tlačítkem se sponkou vedle textového pole.

### Co AI asistent umí

AI asistent zná kontext vaší firmy a umí odpovídat na otázky i provádět akce:

| Oblast | Co umí |
|--------|--------|
| Faktury | Vyhledat fakturu, zobrazit detail, vypsat seznam faktur podle kritérií, stáhnout PDF nebo ISDOC |
| Faktury — změny | Vystavit koncept, označit vydanou fakturu jako zaplacenou, odeslat ji e-mailem, smazat koncept. **Každou takovou změnu si nejdřív necháte odsouhlasit** — viz níže |
| Klienti | Vypsat seznam klientů, vyhledat klienta, zobrazit celý detail (adresy, kontakty, bankovní účty, fakturační nastavení), založit nového podle IČO, upravit údaje a smazat klienta. Úprava i smazání se vždy nejdřív ukážou k odsouhlasení — viz níže. |
| Přijaté faktury | Vyhledat, vypsat seznam |
| Přehledy a reporty | Shrnout dashboard, vypsat faktury po splatnosti, faktury za období nebo pro konkrétního klienta, spočítat DPH za období |
| Nastavení firmy | Přečíst nastavení vlastní firmy (název, IČO, DIČ, plátcovství DPH, jazyk dokladů, adresa, kontakty, bankovní účty) a změnit ho — včetně přidání, úpravy a zrušení bankovního účtu („Přidej nám účet 1234567890/0100", „Od ledna jsme plátci DPH"). IČO měnit nelze. |
| Číselné řady | Vypsat číselné řady (název, typ dokladu, prefix/sufix, stav čítače, formát, která je výchozí) i dostupné formáty číslování, založit novou řadu a upravit existující — název, prefix, sufix, čítač, nebo ji nastavit jako výchozí („Založ řadu Faktury 2026 s prefixem FV-", „Nastav u faktur čítač na 100"). Typ dokladu a formát u existující řady měnit nelze — na to založte řadu novou. |
| Sazby DPH | Vypsat sazby DPH (procento, název, základní/snížená, platnost, výchozí), přidat novou a upravit existující — název, procento, platnost, druh, nebo ji nastavit jako výchozí („Přidej sníženou sazbu 12 % od 1.1.2026", „Ať je 21 % výchozí"). Rušit sazby přes asistenta nelze. |
| Kontrola nastavení | Zkontrolovat, jestli máte vyplněné vše potřebné k vystavení faktury („Můžu už fakturovat?", „Co mi ještě chybí?"). Vypíše, co chybí, jestli to fakturaci brání, nebo jen omezuje jednu funkci, a na které stránce se to doplní. Nic sám nemění. |
| Obecné dotazy | Odpovídat na otázky o funkcích aplikace |
| Navigace | Otevřít libovolnou stránku aplikace — faktury, přijaté faktury, klienty, platby, šablony, DPH a daně, upomínky i nastavení („Otevři DPH přiznání", „Založ novou fakturu pro klienta XYZ"). Stránky správce systému a přihlašovací obrazovky asistent neotevírá. |

### Než asistent něco změní, zeptá se

Dotazy asistent zodpoví rovnou. Ale všechno, co **mění data** — vystavení konceptu, označení
faktury jako zaplacené, odeslání e-mailu, smazání konceptu, i změny u klientů, nastavení
firmy, bankovních účtů, číselných řad nebo sazeb DPH — proběhne na dvě fáze:

1. Napíšete, co chcete („Vystav koncept FAK-2026-001").
2. Asistent **nic neudělá** a napíše, co by se stalo („Vystavím fakturu FAK-2026-001 pro
   Alza.cz, 12 100,00 Kč: stav Koncept → Vystaveno…", u ostatních entit „Název se změní
   z X na Y").
3. Teprve když odpovíte, že souhlasíte, akci provede a potvrdí výsledek.

Když akce provést nejde, dozvíte se to už v tom druhém kroku — například „faktura je už
zaplacená, vystavit lze jen koncept". Potvrzovat tedy nemusíte nic, co by stejně selhalo.
Když náhled nesedí, řekněte, co má být jinak, a asistent nabídne nový.

Dvě omezení u faktur, která jsou v aplikaci schválně:

- **Smazat umí asistent jen koncept** (a jde o smazání do koše — koncept obnovíte na stránce
  Faktury). Vydaný doklad má číslo z číselné řady, ten smažete jen ručně v přehledu faktur.
- **U odeslání e-mailem musíte říct adresu příjemce.** Asistent ji sám neuhodne, a v náhledu
  ji uvidíte dřív, než e-mail odejde.

Smazání klienta je „měkké" — klient se skryje, ale nezmizí, takže starší faktury
na něj dál odkazují. Klienta, který už nějakou fakturu má, smazat nelze vůbec;
asistent vám to řekne. Skrytého klienta lze vrátit zpět („Obnov klienta X").

Když si vyžádáte načtení údajů z ARESu a rejstřík zrovna neodpoví nebo dané IČO
nezná, asistent to řekne rovnou — nikdy nepotvrdí načtení dat, ke kterému nedošlo.

Výchozí účet firmy nejde zrušit, jen přesunout: řekněte, který účet má být nově výchozí
(„Ať je výchozí ten eurový"), a asistent přeznačí oba. Požadavek „tenhle už ať není výchozí"
odmítne — nějaký účet výchozí být musí, jinak by ho aplikace vybrala sama a vy byste se to
nedozvěděli. **Stejné pravidlo platí u číselných řad a sazeb DPH:** výchozí se dá jen
přesunout na jinou řadu nebo sazbu, ne zrušit. Ze stejného důvodu asistent odmítne
překlopit výchozí sazbu ze základní na sníženou (nebo naopak) — druh, který by opustila,
by zůstal bez výchozí sazby. Nejdřív určete jako výchozí jinou sazbu toho druhu, pak
teprve tuhle překlopte.

Pozor na ruční posun čítače číselné řady dozadu — čísla, která už na dokladech jsou, by se
vydala podruhé. Asistent na to v náhledu upozorní, ale rozhodnutí nechává na vás.

Jedno omezení má úprava a rušení bankovních účtů: účet, ke kterému už jsou v aplikaci
navázané platby (načtené bankovní transakce nebo aktivní e-mailová schránka pro příjem
výpisů), přepsat nejde — ani přes asistenta, ani ručně na stránce **Moje firma**. Asistent
vám v takovém případě řekne, proč to neprošlo; upravit takový účet lze až po odpojení
navázaných dat.

Asistent navíc ví, **kde právě stojíte** — jakou stránku máte otevřenou a jestli je na ní
konkrétní doklad — a zná dnešní datum. Můžete se tedy ptát „kdy je tahle faktura splatná"
místo toho, abyste číslo dokladu opisovali. Pokud vám ještě něco chybí k vystavení faktury
(vyplněná vlastní firma, adresa, číselná řada), asistent to ví a nasměruje vás na stránku,
kde se to doplní.

### Jak používat

1. Klikněte na ikonu robota v horní liště nebo na **AI asistent** v menu
2. V panelu napište dotaz v přirozeném jazyce (česky nebo anglicky)
3. Odpověď se zobrazuje streamovaně (postupně, token po tokenu)
4. Konverzace se ukládají — historii konverzací zobrazíte tlačítkem hodiny (History)
5. Konverzaci smažete ikonou koše v historii. Aplikace se vždy zeptá na potvrzení —
   smazanou konverzaci nelze obnovit.

### Formátování odpovědí

Odpovědi asistenta se zobrazují naformátované: nadpisy, tučné písmo, odrážkové
a číslované seznamy, tabulky i bloky kódu. Text tedy neuvidíte jako „syrové" znaky
(`**tučně**`, `| sloupec |`), ale jako hotové formátování.

Odkazy v odpovědi vedou buď do aplikace, nebo na web. Z bezpečnostních důvodů jsou
odkazy s neobvyklým cílem zneškodněny (kliknutí na ně nic neudělá) a případné HTML
z odpovědi se vypíše jako text, nikdy se nespustí.

### Výběr AI poskytovatele

Pokud je nakonfigurováno více AI poskytovatelů, zobrazí se rozbalovací seznam (Claude / OpenAI / Gemini / Ollama) pro výběr.

### Chování asistenta

Styl odpovědí a pravidla asistenta nastavuje správce systému. Pokud vám asistent odpovídá
jinak, než jste zvyklí, je pravděpodobně upravené systémové nastavení — obraťte se na správce.

### Příklady dotazů

- „Ukaž mi faktury pro klienta XYZ za poslední měsíc"
- „Jaký je celkový obrat za Q1 2026?"
- „Najdi fakturu číslo FAK-2026-001"
- „Kolik mám nesplacených faktur?"
- „Vystav koncept faktury FAK-2026-014"
- „Označ fakturu FAK-2026-003 jako zaplacenou"
- „Pošli fakturu FAK-2026-003 na ucetni@alza.cz"
- „Stáhni fakturu FAK-2026-003 jako ISDOC"
- „Můžu už fakturovat? Co mi ještě chybí v nastavení?"

### Když se odpověď nepodaří vygenerovat

Místo odpovědi se objeví krátká hláška s **referenčním ID** (dlouhé číslo
ve tvaru `11111111-2222-…`). Technický detail chyby se neposílá do prohlížeče —
zapisuje se do serverového logu. Při hlášení problému administrátorovi vždy uveďte
toto referenční ID, podle něj chybu v logu dohledá.

Platí to pro všechna místa, kde aplikace volá AI: chatovací panel i AI kontrolu
importu dokladů (hláška „AI review failed…" na stránce importu).

Výjimkou je hláška „Conversation not found." — ta referenční ID nemá, protože
nejde o chybu serveru. Znamená, že konverzace už neexistuje (typicky jste ji
smazali v jiném okně prohlížeče). Stačí obnovit seznam konverzací.

---

## 14. Upomínky (Dunning)

**Stránka:** `/reminders`

Systém automaticky generuje upomínky pro faktury po splatnosti.

### Přehled upomínek

Grid zobrazuje upomínky se sloupci: datum, číslo faktury, klient, úroveň upomínky, celková částka, stav.

**Filtrace:** textové hledání + filtr podle stavu

### Stavy upomínek

| Stav | Barva | Popis |
|------|-------|-------|
| Draft | Šedá | Upomínka čeká na odeslání |
| Sent | Zelená | Upomínka odeslána emailem |
| Failed | Červená | Odeslání selhalo |
| Cancelled | Oranžová | Upomínka zrušena |

### Hromadné akce

Po zaškrtnutí upomínek v gridu se zobrazí toolbar:
- **Odeslat** — odešle Draft upomínky emailem
- **Zrušit** — přestaví Draft upomínky na Cancelled

### Nastavení upomínek

**Stránka:** `/reminder-settings`

Konfigurace pravidel automatického generování upomínek (časové rozstupy, počet upomínek, text).

### Dashboard widget

Na Dashboardu se zobrazuje souhrn: počty Draft/Sent/Failed + poslední záznamy + celková nesplacená částka.

---

## 15. Párování plateb

**Stránka:** `/payments`

Automatické párování bankovních plateb s fakturami pomocí IMAP (příjem emailových notifikací z banky).

### Jak funguje

1. Banka odesílá emailová oznámení o příchozích platbách na speciální adresu vaší firmy
2. Systém tyto emaily přijme a automaticky spáruje platbu s fakturou (dle variabilního symbolu nebo jiných kritérií)
3. Nespárované nebo pochybné platby čekají na ruční kontrolu

### Přehled plateb

Grid zobrazuje bankovní transakce:

| Pole | Popis |
|------|-------|
| Datum | Datum transakce |
| Částka + měna | Příchozí / odchozí |
| Stav párování | Nespárované / Vyžaduje kontrolu / Spárované / Částečně spárované / Rozpoznané / Ignorované |
| Spárovaná faktura / protistrana | Číslo faktury, nebo štítek rozpoznané protistrany |

**Filtrace:** stav párování, směr platby (příchozí / odchozí), textové hledání

### Ruční párování

Kliknutím na řádek s platbou se otevře detail → tlačítko „Spárovat s fakturou" → výběr faktury ze seznamu.

### Rozpoznané protistrany (platby bez faktury)

Pravidelné platby bez faktury — sociální a zdravotní pojištění, nemocenská, DPH
na finanční úřad — lze rozpoznávat podle evidence známých účtů:

1. Na stránce **Moje firma** → sekce „Rozpoznané protistrany" přidejte záznam:
   název (např. „OSSZ — sociální pojištění"), číslo účtu a volitelně VS/SS/KS
   (prázdný symbol = libovolná hodnota) + kategorii (pojištění, DPH, daň…).
   Pro **platby kartou** (nemají protiúčet ani symboly) vyplňte místo účtu
   **vzor jména obchodníka** — např. „ANTHROPIC" rozpozná platbu
   „Platba kartou v ANTHROPIC* CLAUDE SUB, SAN FRANCISCO, CA". Hledá se
   v názvu protistrany i ve zprávě, bez rozlišení velikosti písmen.
2. Po uložení se automaticky projdou existující nespárované platby — počet nově
   rozpoznaných ukáže notifikace.
3. Nové platby na známý účet se při příjmu rozpoznají samy (faktury mají vždy
   přednost). Rozpoznaná platba má stav **Rozpoznáno** a v gridu štítek se
   jménem protistrany.
4. Funguje obousměrně — např. vratka DPH přijde jako příchozí platba z účtu FÚ.

Ruční akce na `/payments`: „Přiřadit protistranu" (ikona záložky) u nespárované
platby, „Zrušit přiřazení" u rozpoznané platby (vrátí ji mezi nespárované).

V okně „Přiřadit protistranu" je i tlačítko **„Vytvořit protistranu z platby"** —
otevře formulář předvyplněný údaji z platby (číslo účtu, VS/SS/KS, název
protistrany), stačí doplnit název/kategorii a uložit. Nový záznam se rovnou
použije pro přiřazení, není třeba nic kopírovat ručně.

### Notifikace při spárování

Při úspěšném spárování platby (automatickém i ručním) dostanou všichni uživatelé firmy notifikaci — zobrazí se jako badge na zvonečku v horní liště. Kliknutím na notifikaci přejdete přímo na detail spárované faktury.

### Dashboard badge

V levém menu vedle „Platby" se zobrazuje číslo (badge) s počtem nespárovaných plateb. Na Dashboardu se zobrazí KPI karta.

---

## 15a. Moje preference

**Stránka:** `/preferences` (menu Nastavení → Moje preference)

Osobní nastavení uživatelského rozhraní — platí jen pro váš účet a přenáší se
mezi zařízeními (ukládá se k profilu, ne do prohlížeče).

| Volba | Popis |
|-------|-------|
| Počet záznamů v tabulkách | Výchozí počet řádků ve všech přehledech (10/25/50/100). Změna se projeví po uložení na nově otevřených přehledech. |

---

## 15b. Mobilní zobrazení

Aplikace se na telefonu (šířka pod 600px) automaticky přizpůsobí:

- Tlačítka akcí (Nová faktura, Import, …) se zmenší na ikonu — popis se zobrazí
  po podržení prstu / najetí.
- V přehledech se skryjí méně důležité sloupce (datumy, IČO/DIČ, …); zobrazit
  je lze přes menu sloupců v hlavičce tabulky.
- Odhlášení najdete v menu profilu (ikona avatara vpravo nahoře).
- Dialogy se otevírají přes celou obrazovku.

---

## 16. Lokalizace — přepínání jazyka CZ/EN

**Komponenta:** LanguageSwitcher v pravém horním rohu navigace

Aplikace podporuje dvě jazykové mutace:
- **Česky (cs)** — výchozí
- **English (en)** — přepnutím se změní veškerý popis UI

**Jazyk dokumentů** (PDF, emaily) závisí na nastavení každého klienta (pole „Jazyk" v detailu klienta), ne na jazyku UI. Pokud klient má `en`, dostane PDF a email v angličtině bez ohledu na váš jazyk UI.

---

## 17. Notifikace

**Přístup:** ikona zvonečku 🔔 v pravém horním rohu navigace (vedle tlačítka odhlášení)

Systém notifikací vás informuje o důležitých událostech, například o spárování plateb s fakturami.

### Zvoneček s badgem

V horní liště aplikace se zobrazuje ikona zvonečku. Pokud máte nepřečtené notifikace, zobrazí se červený badge s jejich počtem.

### Rozbalovací menu notifikací

Kliknutím na zvoneček se otevře rozbalovací panel:

- **Až 10 posledních notifikací** — seřazeny od nejnovější
- Nepřečtené notifikace jsou zvýrazněny tučně a mají modrý indikátor
- Každá notifikace obsahuje:
  - Ikona dle typu (např. ikona platby pro spárované platby)
  - Titulek
  - Popis události
  - Relativní čas (např. „před 5 min", „před 2 h")
- **„Označit vše jako přečtené"** — tlačítko v záhlaví menu
- **„Zobrazit vše"** — odkaz na stránku se všemi notifikacemi

### Prokliknutí na detail

Kliknutím na notifikaci:
1. Notifikace se automaticky označí jako přečtená
2. Aplikace vás přesměruje na detail příslušného dokladu (např. na detail faktury, přijaté faktury)

### Stránka notifikací

**Stránka:** `/notifications`

Kompletní seznam všech notifikací se stránkováním a filtry:

| Filtr | Popis |
|-------|-------|
| Jen nepřečtené | Přepínač — zobrazí pouze nepřečtené notifikace |

**Akce:**
- Kliknutím na řádek → přechod na detail dokladu + označení jako přečtené
- „Označit vše jako přečtené" — hromadné označení

### Typy notifikací

| Typ | Kdy se generuje |
|-----|----------------|
| Platba spárována | Systém automaticky nebo ručně spároval bankovní platbu s fakturou |
| Faktura importována emailem | Email s fakturou byl automaticky zpracován a doklad vytvořen |
| Faktura k přezkoumání | Email s fakturou importován, ale s nízkou spolehlivostí rozpoznání |

---

## 18. Příjem faktur emailem

**Stránka nastavení:** `/my-company` (sekce „Email pro příjem faktur")
**Stránka přehledu:** `/invoice-emails`

Systém umožňuje automatický příjem a zpracování faktur zaslaných emailem.

### Jak aktivovat

1. Přejděte na **Nastavení firmy** (`/my-company`)
2. V sekci **Email pro příjem faktur** klikněte **Aktivovat**
3. Systém vygeneruje unikátní emailovou adresu (např. `fak-a7b3x9k2mp@fakvio.cz`)
4. Zkopírujte adresu (ikona kopírování) a sdělte ji dodavatelům nebo ji nastavte jako přeposílací adresu

### Jak to funguje

1. Dodavatel (nebo vy) pošle email s fakturou (PDF nebo ISDOC příloha) na vygenerovanou adresu
2. Systém automaticky:
   - Rozpozná typ dokumentu z příloh (ISDOC má přednost před PDF)
   - Určí, zda jde o **přijatou fakturu** (od dodavatele) nebo **vydanou fakturu** (naši) — porovnáním IČO
   - Pokud dodavatel v systému neexistuje, automaticky ho založí (z ARES dle IČO)
   - Pokud faktura se stejným číslem již existuje, přidá přílohy k existujícímu dokladu
   - Vytvoří doklad a připojí originální PDF/ISDOC jako přílohu
3. Dostanete **notifikaci** (zvoneček) o výsledku importu

### Přehled příchozích emailů

**Stránka:** `/invoice-emails`

Zobrazuje všechny emaily přijaté na fakturační adresu:

| Sloupec | Popis |
|---------|-------|
| Datum | Datum přijetí emailu |
| Odesílatel | Email nebo jméno odesílatele |
| Předmět | Předmět emailu |
| Přílohy | Typ příloh — ISDOC (zelený chip), PDF (modrý chip) |
| Směr | Přijatá / Vydaná faktura |
| Stav | Aktuální stav zpracování (viz tabulka níže) |

**Rozbalitelný detail** — klikněte na šipku ▸ u každého řádku:
- Email metadata (od, předmět, datum, čas přijetí, počet příloh)
- Zpracování (stav, směr, spolehlivost rozpoznání v %, odkaz na vytvořený doklad)
- Chybová zpráva (pokud zpracování selhalo) — zobrazena v plném znění

### Stavy zpracování

| Stav | Barva | Popis |
|------|-------|-------|
| Čeká | Šedá | Email přijat, čeká na zpracování |
| Importováno | Zelená | Faktura úspěšně vytvořena |
| K přezkoumání | Oranžová | Faktura vytvořena, ale s nízkou spolehlivostí — zkontrolujte |
| Chyba | Červená | Zpracování selhalo — detail chyby v rozbalitelném panelu |
| Ignorováno | Tmavá | Uživatel označil email jako „ne-fakturu" |

### Akce

| Ikona | Akce | Kdy dostupná |
|-------|------|-------------|
| ↻ (retry) | Zkusit zpracovat znovu | Chyba, K přezkoumání |
| 👁‍🗨 (ignorovat) | Označit jako ne-fakturu | Chyba, K přezkoumání |
| ↗ (otevřít) | Přejít na vytvořenou fakturu | Importováno |

### Správa emailové adresy

V sekci **Nastavení firmy** máte k dispozici:

| Akce | Popis |
|------|-------|
| Aktivovat | Vygeneruje novou emailovou adresu |
| Deaktivovat | Přeruší příjem — emaily na starou adresu budou ignorovány |
| Nový alias | Vygeneruje novou adresu, stará přestane okamžitě fungovat |
| Kopírovat | Zkopíruje emailovou adresu do schránky |

### Podporované formáty příloh

| Formát | Spolehlivost | Popis |
|--------|-------------|-------|
| ISDOC (.isdoc) | Nejvyšší | Český standard pro elektronické faktury — strukturovaný XML |
| ISDOCX (.isdocx) | Nejvyšší | ZIP kontejner s ISDOC XML uvnitř |
| PDF | Vysoká | Rozpoznání přes QR kód, AI, nebo textovou analýzu |
| Email bez příloh | Nízká | Pokus o rozpoznání z těla emailu (pouze AI) |


---

## 19. Nastavení hesla a první přihlášení

Odkaz z registračního nebo pozvánkového emailu vede na stránku **Nastavení hesla**
(`/set-password?token=…`). Po zadání hesla se současně ověří vaše emailová adresa
a založí se váš pracovní prostor (databáze vaší firmy).

Výsledek uvidíte přímo na stránce:

| Hlášení | Co znamená | Co dělat |
|---------|-----------|----------|
| Zelené „Heslo bylo úspěšně nastaveno" | Heslo je nastavené a pracovní prostor je připravený | Přihlaste se |
| Oranžové „Heslo bylo nastaveno, ale váš pracovní prostor se nepodařilo připravit" | Heslo platí, ale založení prostoru selhalo — přihlášení zatím nebude fungovat | Zkuste to za chvíli znovu; pokud problém trvá, kontaktujte podporu (přípravu dokončí administrátor) |
| Červené „Pozvánka je neplatná nebo vypršela" | Odkaz vypršel (48 hodin) nebo už byl použit | Požádejte administrátora o novou pozvánku |

Oranžové hlášení nikdy neznamená, že musíte zakládat účet znovu — heslo zůstává
platné a po dokončení přípravy se přihlásíte stejnými údaji.

---

## 20. Napojení vlastního AI klienta (MCP server)

Kromě vestavěného [AI asistenta](#13-ai-asistent) umí Fakvio pracovat i s AI
aplikací, kterou už používáte na svém počítači (např. Claude Desktop nebo
Claude Code). Napojení zajišťuje **MCP server** — malý program, který běží
u vás lokálně a překládá požadavky AI na volání Fakvia.

**V čem se to liší od AI asistenta v aplikaci:**

| | AI asistent v aplikaci | MCP server |
|---|---|---|
| Kde se ovládá | Panel v pravém horním rohu Fakvia | Vaše AI aplikace na počítači |
| Instalace | Žádná | Nutná (program + konfigurační soubor) |
| Rozsah akcí | Vyhledávání a přehledy | 36 nástrojů — vystavení faktury, přijaté faktury, přehledy, DPH, daňové výpočty, šablony |

### Co je potřeba

1. **Nainstalovaný MCP server** na vašem počítači. Instalaci a nastavení
   provádí správce systému — technický postup je v souboru
   `Fakvio.McpServer/README.md`.
2. **Přístupový token** (JWT) vašeho účtu. Token zastupuje vaše přihlášení,
   proto má AI přesně stejná oprávnění, jaká máte vy — nic víc.
3. **Zápis do konfigurace AI aplikace** — vzor je v souboru
   `.mcp.json.sample`; do něj se doplní adresa Fakvia a token.

### Co s tím AI zvládne

| Oblast | Příklady |
|--------|---------|
| Vydané faktury | Vypsat, najít podle čísla, vystavit novou, dokončit, označit jako uhrazenou, odeslat emailem, stáhnout PDF nebo ISDOC, smazat koncept |
| Klienti | Vypsat, zobrazit detail, založit, upravit, dohledat firmu v ARES |
| Přijaté faktury | Vypsat, zobrazit, zadat novou, schválit, označit jako uhrazenou, smazat |
| Přehledy | Dashboard, faktury po splatnosti, faktury klienta, faktury za období, přehled DPH |
| Daně | Odhad daně, porovnání daňových režimů, roční příjmy, zálohy na pojistné |
| Šablony | Vypsat, zobrazit, vystavit fakturu ze šablony |

Příklady zadání: „Vystav fakturu pro klienta XYZ na 15 000 Kč za konzultace“,
„Stáhni mi PDF faktury FAK-2026-001“, „Kolik mám letos zaplatit na zálohách?“

### Bezpečnost — čtěte, než token někam vložíte

- Token **je uložen v konfiguračním souboru v čitelné podobě**. Kdo se dostane
  k souboru, dostane se k vašemu účtu. Nesdílejte ho a neposílejte emailem.
- Token **platí 24 hodin**. Po vypršení začne AI hlásit chyby — stačí vložit nový.
- AI může data i **měnit a mazat** (vystavit fakturu, smazat koncept). Než akci
  potvrdíte, přečtěte si, co se chystá udělat.
- Napojení nefunguje přes internetový prohlížeč — server běží na vašem
  počítači a připojuje se na stejné Fakvio, do kterého se hlásíte v UI.
