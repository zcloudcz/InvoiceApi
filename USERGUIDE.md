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

Pokud byl účet vytvořen, ale odeslání emailu selhalo, zůstane na stránce registrace upozornění. Na přihlášení použijte **Zapomenuté heslo** pro nové zaslání; pokud email nedorazí, kontaktujte podporu. Výpadek služby při dvoufázovém ověření se zobrazuje odděleně od nesprávného kódu. V nastavení ověřovací aplikace lze ruční klíč zkopírovat ikonou kopírování; při selhání schránky jej zkopírujte ručně.

## 1. Přehled (Dashboard)

**Stránka:** `/` (úvodní stránka po přihlášení)

Dashboard zobrazuje aktuální přehled vaší firmy na jednom místě:

### Dokončit nastavení (checklist připravenosti)

Pokud vaší firmě chybí něco, co musí být na daňovém dokladu, uvidíte nahoře na dashboardu
kartu **Dokončit nastavení** se seznamem toho, co zbývá. U každé položky je odkaz
**Doplnit**, který vás zavede přímo na stránku, kde ji vyplníte. Pod položkou jsou drobně
vypsaná konkrétní pole, která chybí.

Položky jsou rozdělené do dvou skupin, každá má vlastní nadpis (a barvu koleček):

| Nadpis skupiny | Význam |
|----------------|--------|
| **Než začnete fakturovat, doplňte tato nastavení** (červená kolečka) | Bez těchto údajů aplikace **odmítne vystavit fakturu** (viz [§2.3.1](#231-když-vystavení-skončí-chybou-nedokončené-nastavení-firmy)) |
| **Doporučená nastavení k doplnění** (oranžová kolečka) | Fakturovat můžete, ale narazíte později v konkrétní funkci (typicky EPO export přiznání k DPH) |

Skupina, ve které nic nechybí, se nezobrazí vůbec.

Máte-li v účtu víc vystavitelů, je u položky uvedeno, které firmy se týká.

**Odložení na později.** Tlačítko **Připomenout později** kartu sbalí na jediný řádek
„Dokončit nastavení (počet)“. Kliknutím na něj se průvodce kdykoli znovu rozbalí — není to
jednorázové zavření. Volba se pamatuje v prohlížeči, takže přežije i obnovení stránky
(na jiném počítači nebo v jiném prohlížeči se karta objeví opět rozbalená).

**Hotovo se nikde neukládá.** Seznam se počítá vždy znovu z aktuálního stavu nastavení, takže
jakmile poslední položku doplníte (třeba i kolega), zmizí karta i sbalený řádek úplně —
odložení na tom nic nemění a žádné zastaralé připomínky nezbývají.

Chybí-li něco červeného, doplníte to na příslušných stránkách — karta vás na ně odkáže.

Stejný seznam najdete i v detailu faktury ve stavu Draft — tam se zobrazí jako barevný
banner omezený na vystavitele té konkrétní faktury, takže vidíte přesně to, co by vystavení
zablokovalo. Tam se odložit nedá — jde o poslední varování před kliknutím na Vystavit.

Kontrolní seznam používá názvy polí odpovídající formulářům. Pokud oprava vyžaduje vyšší oprávnění, místo nefunkčního odkazu uvede, koho kontaktovat: nastavení EPO řeší SysAdmin, číselné řady Účetní nebo SysAdmin.

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
- Sloupcové řazení (kliknutím na záhlaví); bez zvoleného řazení jsou nahoře nejnovější faktury podle data vystavení

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

Pole zákazníka začíná prázdné; před vytvořením jej vyberte. Během ukládání je tlačítko Vytvořit/Uložit dočasně vypnuté, aby opakované kliknutí neposlalo další požadavek. Při neúspěšném načtení přehledu se zobrazí chyba s možností opakování, nikoli zpráva o prázdném seznamu. Filtr částky v hlavičce přehledu není nabízen.

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

### 2.8a Export e-faktury (UBL / Peppol BIS)

**Co to je:** od 1. 1. 2027 musí slovenští plátci DPH vystavovat tuzemské (SK→SK) faktury jako
strukturovanou e-fakturu ve formátu **UBL 2.1 / Peppol BIS Billing 3.0**, posílanou přes síť
Peppol. Fakvio zatím (fáze 1) e-fakturu neodesílá samo — vygeneruje XML soubor, který nahrajete
do aplikace svého „digitálního poštáře" (viz seznam certifikovaných poskytovatelů na webu
Finanční správy SR). I mimo SK je export užitečný jako komfort pro odběratele v Peppol síti a
příprava na celoevropskou e-fakturaci (ViDA) od 1. 7. 2030.

**Kdy použít:**
- Vystavujete fakturu slovenskému odběrateli (nebo jinému odběrateli v síti Peppol).
- Chcete e-fakturu nahrát do aplikace svého poštáře místo ručního přepisování údajů.

**Stažení:** v detailu faktury tlačítko „Stáhnout e-fakturu (UBL)" vedle tlačítka ISDOC. Tlačítko
je zakázané u konceptů (Draft) a u proforem — proforma není daňový doklad a e-fakturu z ní
vystavit nejde.

**Hromadný export:** zaškrtněte faktury → toolbar → „Stáhnout e-faktury (UBL)" → stáhne se ZIP
archiv s `.xml` soubory. Koncepty a proformy se přeskakují stejně jako u ISDOC.

**Co zkontrolovat, když export selže:** Fakvio před vygenerováním XML kontroluje, jestli má
faktura vše potřebné pro platnou e-fakturu. Když ne, zobrazí se seznam konkrétních problémů
místo souboru — nejčastěji:
- **DIČ a země** vaší firmy nebo odběratele — z nich se odvozuje Peppol adresa; když odvození
  nejde (DPH skupina, neobvyklý formát DIČ), nastavte přepis v poli „Peppol ID" u klienta / na
  stránce Moje firma (§5, §10).
- **Adresa** (ulice, město, PSČ, země) musí být kompletní.
- **Platba bankovním převodem** musí mít IBAN nebo číslo účtu.
- **Slovenský vystavitel** může zatím e-fakturu vystavit jen v měně **EUR**.

**Automatická příloha v e-mailu:** když faktuře pošlete přes „Odeslat emailem" (§2.9) a
odběratel je slovenský (nebo má nastavený přepis Peppol ID), Fakvio k PDF a ISDOC automaticky
přidá i UBL XML. Když se e-faktura nedá vygenerovat (viz problémy výše), příloha se prostě
vynechá — email se vždy odešle. **Pozor:** poslat e-fakturu jinak než přes Peppol síť
(např. e-mailem) je u slovenského plátce DPH legální jen se souhlasem příjemce.

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

Při změně typu nového dokladu na **Dobropis** se zobrazí vyhledávání původní faktury. Hledejte podle čísla nebo zákazníka; hledání probíhá i mezi staršími vystavenými a zaplacenými fakturami. Zpřesněte hledaný text, pokud je výsledků mnoho. Bez výběru původní faktury nelze dobropis vytvořit.

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
   - **Peppol ID (přepis)** — volitelné, nechte prázdné. Fakvio adresu pro e-fakturu (Peppol)
     odvodí samo z DIČ a země klienta; pole vyplňte jen když odvození nestačí (DPH skupina,
     zahraniční klient bez SK/CZ DIČ apod.). Formát `schéma:hodnota`, např. `0245:2020123456`
     (SK) — viz §2.8a Export e-faktury.
4. Přidejte adresy (fakturační, doručovací…) tlačítkem „+ Adresa"
5. Přidejte kontakty (email, telefon)
6. Přidejte bankovní účty
7. Klikněte „Vytvořit"

Při editaci klienta mění pole Email a Telefon první kontakt příslušného typu. Další uložené kontakty, jejich popisky a označení hlavního kontaktu zůstávají zachované. Smazání klienta z přehledu i detailu vyžaduje potvrzení; zrušení potvrzení nic nemění. Během ukládání je opakované odeslání zablokované.

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

V detailu klienta se zobrazuje tabulka faktur tohoto klienta (server-side stránkování). Číslo faktury otevře její detail. Stav filtrujte výběrem nad tabulkou; hlavička tabulky nenabízí druhý filtr stavu ani nepodporovaný filtr částky.

### 5.5 Import klientů z Fakturoidu / iDokladu

**Stránka:** `/clients` → tlačítko „Importovat z CSV"

Když přecházíte z Fakturoidu nebo iDokladu, nemusíte klienty přepisovat ručně:

1. Ve Fakturoidu: Kontakty → Export; v iDokladu: Adresář → Export. Uložte jako CSV.
2. Na stránce Klienti klikněte „Importovat z CSV" a vyberte soubor.
3. Systém zobrazí náhled — u každého řádku vidíte, zda je klient **nový**, **duplicitní**
   (podle IČO — už v systému existuje, nebo se opakuje v souboru) nebo **chybný** (chybí název
   nebo IČO, případně je některá hodnota delší, než systém dovoluje — např. PSČ nad 20 znaků,
   typicky posunutý sloupec v exportu).
   Neznámé sloupce z exportu se ignorují.
4. Klikněte „Importovat" — založí se jen nové řádky. Opakovaným nahráním stejného souboru se
   nic nezaloží znovu.

Import zatím nenačítá historii vydaných faktur (jen kontakty) — přenos faktur je plánovaná
druhá fáze.

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

### Opakované faktury

Na detailu uložené šablony (`/invoice-templates/{id}`) je sekce **„Opakování"**, kde lze naplánovat
automatické generování faktur z této šablony:

1. Klikněte „Přidat plán".
2. Vyberte klienta, frekvenci (týdně / měsíčně / čtvrtletně / ročně), interval (např. „každé 2
   měsíce"), den v měsíci (1–28) nebo den v týdnu (u týdenní frekvence), první datum vystavení
   a volitelně konec — buď konkrétním datem, nebo počtem opakování.
3. Uložte.

**Kdy se faktura vygeneruje:** jednou za app hodinovou kontrolu (`RecurringInvoiceWorker`), jakmile
nastane naplánovaný termín. Faktura se **rovnou vystaví** (ne koncept) — číslo dokladu se přidělí
ihned, takže po smazání vygenerované faktury nezůstane v číselné řadě díra.

**Co když aplikace byla dočasně nedostupná:** zmeškaná perioda se dožene v příštím běhu, ale vždy
jen jedna faktura za cyklus — datum vystavení odpovídá původně plánovanému datu, ne datu, kdy se
generování skutečně stihlo.

**Chyba generování:** pokud se fakturu nepodaří vytvořit (např. chybí nastavení firmy), plán
zůstane ve stejném stavu a příští cyklus to zkusí znovu. Chybu vidíte v gridu plánů jako červený
štítek u sloupce „Poslední chyba" (najetí myší zobrazí detail) a přijde vám i in-app notifikace —
jen jednou pro stejnou chybu, ne při každém dalším neúspěšném pokusu. Po úspěšném vygenerování
se chyba vymaže a případná další chyba vás upozorní znovu.

**Pozastavení / obnovení / smazání:** tlačítka v řádku plánu. Smazání plánu, který ještě nikdy
nevygeneroval fakturu, ho úplně odstraní; jinak se jen pozastaví (historie zůstává zachována).

**Automatické odeslání e-mailem („Rovnou vystavit a odeslat e-mailem"):** faktura se vystaví
vždy — tento přepínač navíc pošle vystavenou fakturu e-mailem na kontaktní adresu klienta
(kontakt typu „Email"). Pokud klient nemá e-mail vyplněný nebo odeslání selže (SMTP chyba),
faktura zůstane vystavená, uvidíte to jako chybu u plánu a další perioda se generuje normálně —
neodeslaný e-mail se negeneruje znovu.

---

Sazby DPH, číselné řady a šablony dokumentů může běžný **Uživatel** prohlížet. Zápisové akce vyžadují roli **Účetní** nebo **SysAdmin**; rozhraní je běžnému uživateli nenabízí ani po otevření detailu šablony přímým odkazem.

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
2. **Import:** „Import" → `/received-invoices/import?target=ReceivedInvoice` → nahrání souboru
   (PDF, ISDOC, nebo UBL/Peppol BIS XML — viz kap. 18 „Podporované formáty příloh" pro detaily
   o rozpoznání UBL faktur a dobropisů)

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
Chybějící řadu jde opravit i přes AI (kapitola 20) — `list_number_sequences` vypíše dostupné
formáty a `create_number_sequence` založí novou výchozí řadu, aniž byste museli otevírat UI.

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
- **Peppol ID (přepis)** — volitelné, nechte prázdné. Adresu pro e-fakturu (Peppol) Fakvio
  odvodí samo z DIČ a země vaší firmy; vyplňte jen když odvození nestačí. Formát
  `schéma:hodnota`, např. `0245:2020123456` (SK) — viz §2.8a Export e-faktury.

**Adresy:** sídlo, provozovna, fakturační adresa (přes dialog Adresa)

**Kontakty:** email, telefon

**Bankovní účty:**
- Přidávání přes dialog „Bankovní účet"
- Formát: číslo účtu, kód banky, IBAN, BIC/SWIFT
- QR kód platba — systém generuje QR kód pro faktury automaticky

Základní informace, primární adresu i bankovní účet jde nastavit i přes AI (kapitola 20) —
`update_my_company` a `add_bank_account`.

Nová faktura (včetně té vytvořené přes AI nebo z opakování) dostane bankovní účet automaticky: nejdřív výchozí účet v měně faktury, potom jakýkoli účet v této měně, jinak výchozí účet. V editoru faktury ho můžete změnit; po změně měny se účet přepne jen pokud jste žádný ručně nevybrali.

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
- V editoru položek (platí pro fakturu, dobropis i zálohovou fakturu; jen pro plátce DPH) nastavte ve sloupci „Režim DPH" hodnotu Přenesená daňová povinnost
- Ve sloupci „Kód PDP" vyberte příslušný kód předmětu plnění z číselníku MFČR (§92a–92e)
- Chcete-li převést celou fakturu najednou, zapněte přepínač „Přenesená daňová povinnost" nad tabulkou položek a vyberte jeden kód; zapíše se do všech položek (přepínač se sám vypne, pokud pak u některé položky režim změníte)
- Součty ukazují DPH 0 a u položky text „daň odvede zákazník"; PDF obsahuje povinnou poznámku dle §92a a použité kódy, ISDOC blok LocalReverseCharge
- Faktura se správně zobrazí v kontrolním hlášení (DPHKH1)

### Neplátce DPH

Pokud vaše firma **není plátcem DPH** (Moje firma → „Plátce DPH“ vypnuto), Fakvio DPH
nikdy neúčtuje:

- ve formuláři faktury i šablony chybí sloupec se sazbou DPH a místo tří součtů je jen **Celkem**,
- i kdyby položka sazbu nesla (starší šablona, MCP, import), uloží se s 0 % a bez DPH,
- PDF nemá sloupec DPH, rekapitulaci DPH, nadpis „Daňový doklad“ ani datum zdanitelného plnění
  a obsahuje větu „Dodavatel není plátcem DPH.“

Faktury vystavené **před** touto opravou (2026-09-29) mohly DPH nést. Koncept stačí otevřít,
upravit a uložit — DPH se vynuluje. U vystavené faktury vystavte dobropis a fakturu znovu.

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

**AI asistent byl z aplikace skryt** (ikona v horní liště, položka v menu i uvítání po přihlášení).
Práci s fakturami, klienty a přehledy zajišťují běžné stránky aplikace; pro práci s AI
použijte MCP — viz kapitola 20 (API klíč) a připojení klienta (např. Claude Desktop).


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

### Stránkování plateb a výběr pro párování

Historie plateb načítá zvolenou stránku ze serveru a umožňuje změnit počet řádků. Změna filtru vrátí historii na první stránku. Také nespárované příchozí platby mají vlastní stránky. Hromadné párování zpracuje označené řádky, nebo aktuální stránku, pokud nic neoznačíte. Při přechodu na jinou stránku či obnovení nespárovaných plateb se předchozí výběr zruší. Chyba načtení se zobrazí samostatně; použijte tlačítko obnovení.

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

Při výpadku notifikací zůstane poslední načtený seznam a objeví se chyba s možností opakování. Neúspěšné označení za přečtené nemaže upozornění ani nesnižuje počet nepřečtených položek. Odkaz na bankovní transakce vede do přehledu Plateb. Ovládání zvonečku, seznamu i tabulek respektuje zvolený jazyk.

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

1. Dodavatel (nebo vy) pošle email s fakturou (PDF, ISDOC nebo UBL/Peppol XML příloha) na vygenerovanou adresu
2. Systém automaticky:
   - Rozpozná typ dokumentu z příloh (ISDOC a UBL mají přednost před PDF — jsou to strukturovaná
     data bez potřeby AI, takže rozpoznání je spolehlivější)
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
| UBL / Peppol BIS (.xml) | Nejvyšší | Evropský standard elektronické fakturace (EN 16931) — používá se v síti Peppol, povinný pro SK plátce DPH od 1. 1. 2027. Podporuje faktury i dobropisy. |
| PDF | Vysoká | Rozpoznání přes QR kód, AI, nebo textovou analýzu |
| Email bez příloh | Nízká | Pokus o rozpoznání z těla emailu (pouze AI) |

**Poznámka k UBL:** faktura se rozpozná podle IČO/DIČ dodavatele (dohledá se existující
klient, jinak se založí nový), načtou se položky, DPH, splatnost, variabilní symbol a
IBAN přímo z XML — bez použití AI. Nevalidní nebo poškozený UBL soubor se bezpečně
odmítne (čitelná chybová hláška) a nezpůsobí pád zpracování; pokud email obsahuje i jinou
přílohu (např. PDF), ta se zpracuje samostatně beze změny.


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

## 19a. První požadavek po delší nečinnosti trvá chvíli déle

Když aplikaci nikdo delší dobu nepoužívá, server se uspí a další požadavek ho musí probudit.
Než je připravený, aplikace **sama počká a požadavek zopakuje** — poznáte to jen podle toho,
že se stránka načítá o vteřinu nebo dvě déle. Nemusíte nic dělat ani nic obnovovat.

Dřív se v tomhle okamžiku místo čekání zobrazila chyba, kterou bylo potřeba „proklikat"
opakovaným obnovením stránky. To už neplatí.

Pokud čekání trvá **déle než ~20 vteřin** a skončí chybou, nejde o probouzení serveru —
kontaktujte podporu.

---

## 19b. Chyby při přihlášení, registraci a resetu hesla

Přihlášení, registrace, načtení firmy z ARES i zapomenuté heslo jsou chráněné neviditelnou
kontrolou proti robotům (reCAPTCHA). Pokud se zobrazí hláška **„Ověření proti robotům se
nezdařilo"**:

1. Zkuste stránku obnovit (F5) a akci zopakovat.
2. Pokud používáte blokování reklam (AdBlock, uBlock Origin apod.), vypněte ho pro
   `app.fakvio.cz` — právě tyto nástroje nejčastěji blokují skript, na kterém kontrola
   proti robotům závisí.
3. Přetrvává-li chyba, kontaktujte podporu.

Tato hláška neznamená chybu ve vašich přihlašovacích údajích ani v e-mailu — je to
samostatná kontrola, která proběhne dřív, než se cokoli z formuláře vůbec odešle.

**Hláška „Příliš mnoho pokusů. Zkuste to prosím znovu za chvíli."** se zobrazí, když
z vaší sítě přišlo za krátkou dobu příliš mnoho pokusů o přihlášení/registraci/reset
hesla (ochrana proti zneužití, ne chyba na vaší straně). Počkejte přibližně minutu a
zkuste to znovu.

---

## 20. Napojení vlastního AI klienta (MCP server)

Fakvio umí pracovat s AI aplikací, kterou už používáte (např. Claude Desktop, Claude Code
nebo ChatGPT). Napojení zajišťuje **MCP server** — program, který překládá požadavky AI na
volání Fakvia. Nabízí 71 nástrojů — vystavení faktury, přijaté faktury, přehledy, DPH, daňové
výpočty, šablony, měny, nastavení, platby a upomínky, opakované faktury.

Postup je vždy stejný: **vytvořit klíč → vložit konfiguraci do AI aplikace → ověřit**.

---

### 20.1 Krok 1 — vytvořte si API klíč

API klíč je vaše osobní „heslo pro AI“. Zastupuje vaše přihlášení, takže AI má
přesně stejná oprávnění jako vy — nikdy víc.

1. V levém menu otevřete **Nastavení → Integrace** (adresa `/settings/integrations`).
   Stránku má k dispozici každý přihlášený uživatel a vidí na ní **jen své vlastní klíče**.
2. Klikněte na **Nový API klíč**.
3. Vyplňte:
   - **Název** — podle čeho klíč poznáte, například „Claude Desktop — notebook“. Povinné,
     nejvýš 100 znaků.
   - **Oprávnění** — `Jen čtení` (AI smí jen číst a počítat) nebo `Čtení i zápis`
     (AI smí i vystavovat faktury, zakládat klienty, mazat koncepty).
   - **Platnost do** — nepovinné. Prázdné pole znamená, že klíč nikdy nevyprší.
     Nejdřívější volitelný den je zítřek.
4. Potvrďte **Vytvořit**.

> ### ⚠️ Klíč uvidíte právě jednou
> Hned po vytvoření se nahoře objeví panel s vygenerovaným klíčem (začíná `fak_live_`).
> **Jakmile panel zavřete tlačítkem „Klíč mám uložený“, klíč už nikdo nezobrazí** —
> Fakvio si z něj ukládá jen otisk, ne klíč samotný. Ztracený klíč nejde obnovit,
> jen revokovat a vytvořit nový.

V tom panelu jsou kromě samotného klíče i **dva hotové konfigurační bloky** — jeden pro
lokální a jeden pro vzdálené připojení — a u každého tlačítko pro zkopírování do schránky.
Klíč už v nich je vyplněný, takže nejrychlejší cesta je zkopírovat rovnou ten blok, který
odpovídá vašemu způsobu připojení podle kroku 2.

---

### 20.2 Krok 2 — vyberte způsob připojení

| | Lokální (stdio) | Vzdálený (HTTP) |
|---|---|---|
| Kde MCP server běží | Na vašem počítači, spouští ho vaše AI aplikace | Na serveru, který provozuje váš správce |
| Co musíte nainstalovat | Nástroj `fakvio-mcp` (jeden příkaz, viz krok 3a) | Nic |
| Co potřebujete znát | Adresu **API** Fakvia (najdete ji v připraveném bloku, viz krok 3a) | Adresu MCP serveru — `https://mcp.fakvio.cz/mcp` (viz krok 3b) |
| Kdy zvolit | Pracujete na jednom počítači a máte tam práva instalovat | Chcete se připojit odkudkoli nebo nemůžete nic instalovat |

Nevíte-li, který způsob zvolit, zeptejte se správce systému — technický popis provozu MCP
serveru má v ADMINGUIDE, kapitola „Bezpečnost“.

---

### 20.3 Krok 3a — lokální připojení (stdio)

Do konfiguračního souboru své AI aplikace vložte blok níž (v Claude Code je to
`.mcp.json` v kořeni projektu, v Claude Desktop `claude_desktop_config.json` — kde přesně
soubor leží, říká dokumentace té aplikace). Máte-li otevřený panel s klíčem, použijte radši
připravený blok **Lokální MCP server (stdio)**, ať se nepřepíšete.

```json
{
  "mcpServers": {
    "fakvio": {
      "command": "fakvio-mcp",
      "env": {
        "FAKVIO_API_URL": "https://fakvio-api.azurewebsites.net",
        "FAKVIO_API_TOKEN": "fak_live_vas-klic"
      }
    }
  }
}
```

- `FAKVIO_API_URL` je adresa **API** Fakvia — tedy serveru, se kterým aplikace mluví.
  **Není to adresa, na kterou se hlásíte v prohlížeči** (`app.fakvio.cz`) — v produkci je to
  `https://fakvio-api.azurewebsites.net`. Nejjistější je ale vzít hodnotu z připraveného
  bloku **Lokální MCP server (stdio)** na stránce Integrace — je v něm vyplněná automaticky.
- `FAKVIO_API_TOKEN` je váš API klíč z kroku 1.
- Předpokladem je nainstalovaný nástroj `fakvio-mcp`. Vyžaduje .NET 10 SDK (nebo
  .NET 10 runtime **spolu s ASP.NET Core runtime**) a instaluje se jedním příkazem:

  ```bash
  dotnet tool install --global Fakvio.McpServer
  ```

  Aktualizace je `dotnet tool update --global Fakvio.McpServer`. Nemáte-li na počítači
  práva instalovat, požádejte správce.
  Verze **2.0.0** je nekompatibilní s předchozími — vlastní skript, který volá nástroje s pevně
  zapsanými argumenty, po aktualizaci upravte podle `Fakvio.McpServer/README.md` § „Kompatibilita
  2.0". Váš AI klient (Claude, ChatGPT) si schéma nástrojů načte sám, nic dělat nemusíte.

Po uložení souboru AI aplikaci restartujte.

---

### 20.4 Krok 3b — vzdálené připojení (HTTP)

Tady se nic neinstaluje — AI aplikace se rovnou připojí na adresu MCP serveru a klíč
posílá v hlavičce každého požadavku.

```json
{
  "mcpServers": {
    "fakvio-remote": {
      "type": "http",
      "url": "https://mcp.fakvio.cz/mcp",
      "headers": {
        "Authorization": "Bearer fak_live_vas-klic"
      }
    }
  }
}
```

- `url` je produkční adresa MCP serveru, končí vždy `/mcp`. Připravený blok na stránce
  Integrace ji vyplní automaticky.
- Podporu vzdálených MCP serverů musí umět i vaše AI aplikace; ne všechny to zatím zvládají.

---

### 20.5 Krok 4 — ověřte, že to funguje

Zeptejte se AI například „Kolik mám faktur po splatnosti?“. Když odpoví čísly z Fakvia,
je hotovo. Když ne, obvyklé příčiny jsou tyhle:

| Co AI hlásí | Co se stalo | Co s tím |
|-------------|-------------|----------|
| Chybu s číslem **401** | Klíč je špatně zkopírovaný, vypršel, nebo byl revokovaný | Vytvořte na stránce Integrace nový klíč a vložte ho znovu |
| Chybu s číslem **403** | Klíč má oprávnění `Jen čtení` a AI se pokusila něco změnit | Vytvořte klíč s `Čtení i zápis` (a ten původní revokujte) |
| Že nástroje Fakvia vůbec nevidí | Konfigurace se nenačetla | Zkontrolujte, že soubor je uložený na správném místě, a AI aplikaci restartujte |
| Že se nemůže připojit — **lokální** režim | Chybí nástroj `fakvio-mcp`, nebo v `FAKVIO_API_URL` není adresa API (častá chyba: je tam adresa, na které máte Fakvio otevřené v prohlížeči) | Porovnejte `FAKVIO_API_URL` s blokem **Lokální MCP server (stdio)** na stránce Integrace; když blok už nemáte otevřený, řekněte si o adresu API správci |
| Že se nemůže připojit — **vzdálený** režim | Nesedí `url` MCP serveru | Zkontrolujte, že `url` je přesně `https://mcp.fakvio.cz/mcp` (viz krok 3b) |

---

### 20.6 Co s tím AI zvládne

| Oblast | Příklady |
|--------|---------|
| Vydané faktury | Vypsat, najít podle čísla, vystavit novou, dokončit, označit jako uhrazenou, odeslat emailem, stáhnout PDF nebo ISDOC, smazat koncept |
| Klienti | Vypsat, zobrazit detail, založit, upravit, dohledat firmu v ARES, zjistit vystavitele |
| Přijaté faktury | Vypsat, zobrazit, zadat novou, schválit, označit jako uhrazenou, smazat |
| Přehledy | Dashboard, faktury po splatnosti, faktury klienta, faktury za období, přehled DPH, přijaté faktury po splatnosti |
| Daně | Odhad daně, porovnání daňových režimů, roční příjmy, zálohy na pojistné, daňové nastavení |
| Šablony | Vypsat, zobrazit, vystavit fakturu ze šablony |
| Nastavení | Zkontrolovat, co firmě chybí k vystavení faktury, vypsat platné měny (pro vystavení faktury v cizí měně), vypsat i založit/upravit číselné řady, vypsat platné sazby DPH, upravit údaje o firmě a adresu, přidat bankovní účet |
| Platby a upomínky | Vypsat bankovní platby a jejich stav spárování, zobrazit detail platby, vypsat odeslané upomínky (i k jedné faktuře), zobrazit nastavení upomínek |
| Opakované faktury | Vypsat a zobrazit plány (i pro konkrétní šablonu), založit nový plán, upravit frekvenci/datum/limity, pozastavit a obnovit, smazat |

Příklady zadání: „Vystav fakturu pro klienta XYZ na 15 000 Kč za konzultace“,
„Stáhni mi PDF faktury FAK-2026-001“, „Kolik mám letos zaplatit na zálohách?“

---

### 20.7 Správa klíčů a bezpečnost

Na stránce **Nastavení → Integrace** máte seznam svých klíčů:

| Sloupec | Co znamená |
|---------|------------|
| Název | Co jste zadali při vytvoření |
| Prefix | Prvních 12 znaků klíče — jen na rozlišení, přihlásit se jimi nedá |
| Oprávnění | `Jen čtení` / `Čtení i zápis` |
| Vytvořen, Platnost do | `Bez expirace`, pokud jste datum nevyplnili |
| Naposledy použit | Orientační — zapisuje se nejvýš jednou za pět minut, takže úplně poslední volání v seznamu ještě být nemusí |
| Stav | `Aktivní`, `Vypršel`, `Revokován` |

**Revokace.** U každého klíče, který ještě nebyl revokovaný — tedy i u vypršelého — je v řádku
ikona zákazu. Po potvrzení klíč **okamžitě** přestane fungovat — nic se necachuje, takže další
požadavek AI už neprojde. Vzít zpět to nejde; místo revokovaného klíče se vydává nový.

**Na co si dát pozor:**

- Klíč je v konfiguračním souboru **čitelně**. Kdo se dostane k souboru, dostane se k vašemu
  účtu — nesdílejte ho, neposílejte emailem a nedávejte ho do gitu. Když se přesto někam dostane,
  klíč revokujte; je to rychlejší i bezpečnější než ho hledat.
- Klíč **nikdy neumí víc než váš účet**. Když má navíc `Jen čtení`, umí ještě míň — na zápis
  vrátí chybu, i kdyby vaše role zápis dovolovala. AI dostane srozumitelnou zprávu
  (`"error": "forbidden"` s návodem vytvořit klíč `Čtení i zápis`), ne obecnou chybu serveru —
  pozná tak hned, že problém je v oprávnění klíče, ne v pádu aplikace.
- Klíčem **nejde spravovat klíče**. Vytvořit nebo revokovat klíč jde jen po přihlášení do
  aplikace, takže ani zneužitý klíč si nevyrobí náhradu.
- Když se váš účet deaktivuje, přestanou fungovat **všechny** vaše klíče najednou.
- AI může data i **měnit a mazat** (vystavit fakturu, smazat koncept). Než akci potvrdíte,
  přečtěte si, co se chystá udělat.
- Dává smysl mít **víc klíčů** — jeden na aplikaci či počítač. Když jeden ztratíte, revokujete
  ho bez dopadu na ostatní.

### 20.8 Připojení přes claude.ai / ChatGPT (OAuth) — třetí cesta

Kromě API klíče (§20.1–20.7) jde Fakvio připojit k **claude.ai** a **ChatGPT** i bez klíče —
stačí v daném klientovi zadat adresu Fakvio MCP serveru a přihlásit se běžným Fakvio účtem
(heslo, SSO, 2FA — cokoli používáte dnes). Klient se nejdřív zeptá na souhlas: uvidíte, o
kterou aplikaci jde (podle adresy, ne podle jména, které si aplikace sama zvolila), kam vás po
povolení pošle, a zvolíte **Jen čtení** (výchozí) nebo **Čtení i zápis**.

**ChatGPT na webu — běžný uživatel:**

1. Otevřete [ChatGPT](https://chatgpt.com/) v prohlížeči, přihlaste se a přejděte do
   **Nastavení → Aplikace**.
2. Najděte **Fakvio** a zvolte **Připojit**. Pokud ho v seznamu nevidíte, požádejte správce
   pracovního prostoru ChatGPT o jeho přidání; sami nemusíte vytvářet API klíč.
3. Na stránce Fakvia se přihlaste svým běžným účtem. Zkontrolujte název aplikace, firmu a
   požadovaná oprávnění; pro první připojení zvolte **Jen čtení** a potvrďte **Povolit**.
4. V ChatGPT otevřete nový chat, vyberte Fakvio v nabídce nástrojů a zkuste
   „Zobraz moje poslední faktury.“ Když v další zprávě chcete nová data z Fakvia, vyberte
   aplikaci znovu nebo ji v textu označte.

**Jednorázové přidání správcem ChatGPT nebo oprávněným vývojářem:** zapněte
**Developer mode** v **Nastavení → Aplikace → Pokročilá nastavení** nebo při vytváření
aplikace v **Nastavení pracovního prostoru → Aplikace → Vytvořit**. Pak otevřete
**Aplikace → Vytvořit**, zadejte název **Fakvio**, adresu `https://mcp.fakvio.cz/mcp`
a přihlášení **OAuth**. Spusťte **Scan Tools**, dokončete přihlášení do Fakvia a aplikaci
vytvořte. Správce pracovního prostoru ji následně publikuje pro ostatní členy.
Nabídky i dostupnost se liší podle plánu a nastavení ChatGPT;
viz [aktuální návod OpenAI](https://help.openai.com/en/articles/12584461-developer-mode-and-mcp-apps-in-chatgpt).

> API klíč z §20.1 v ChatGPT na webu nepoužívejte. K přihlášení slouží OAuth; heslo ani klíč
> nikdy nevkládejte do textu chatu. Pokud připojení nenabídne přihlášení, ověřte u správce
> ChatGPT dostupnost vlastní MCP aplikace.

**Správa připojených aplikací.** Na stránce **Nastavení → Integrace** přibyla pod klíči sekce
**Připojené aplikace** — jedna aplikace = jeden řádek (název, oprávnění, kdy vzniklo, kdy
naposledy použito). Tlačítko **Odebrat** okamžitě ukončí přístup té aplikace — stejně
neodvolatelně jako revokace API klíče. Když se znovu přihlásíte přes stejnou aplikaci, vznikne
nové připojení (staré tím zmizí ze seznamu, žádné duplicity).

**Souvislost s heslem a 2FA.** Změna nebo reset hesla a vypnutí dvoufázového ověření odpojí
**všechny** vaše připojené aplikace najednou (API klíče zůstávají beze změny — jsou to jiný,
samostatně spravovaný typ přístupu). Po takové změně je tedy potřeba se v claude.ai/ChatGPT
znovu přihlásit.

## Zpětná vazba: chyby, nápady a postřehy

Po přihlášení otevřete formulář ikonou brouka vpravo nahoře. Vyberte **Chyba**, **Nápad** nebo **Připomínka**, napište předmět (nejvýše 200 znaků) a popis (nejvýše 10 000 znaků). Připojí se aktuální stránka bez parametrů a verze aplikace. Přílohy ani odesílání emailu tato funkce nepodporuje.

Po odeslání přejdete na detail hlášení. V nabídce **Moje hlášení** (`/feedback`) najdete pouze vlastní hlášení pro aktuální firmu; lze je filtrovat podle typu a stavu. Správce může nastavit stav **Nové**, **Řeší se**, **Vyřešeno** nebo **Zamítnuto** a přidat veřejnou odpověď, kterou uvidíte v detailu. Při chybě odeslání zůstane rozepsaný text ve formuláři. Do pole Stránka můžete vložit místní cestu nebo celou URL této aplikace; parametry a fragment se neukládají. Cizí či neplatná URL zobrazí chybu přímo u pole.

Stejné odeslání a sledování nabízí MCP nástroje `submit_feedback`, `list_feedback`, `get_feedback`; odeslání vyžaduje oprávnění k zápisu.

Role dříve označená **Admin** se zobrazuje jako **Účetní** (anglicky **Accountant**). Zachovává dosavadní pracovní oprávnění kromě správy uživatelů; ta je nyní dostupná jen systémovému správci. Úprava vlastního profilu, hesla a osobních nastavení zůstává dostupná. Štítek role vedle jména v záhlaví byl odstraněn.

## Více firem pod jedním účtem

Jeden email zůstává jednou přihlašovací identitou. Nabídka firmy v záhlaví ukazuje dostupná členství a role. Vyberte firmu a potvrďte přepnutí; aplikace se znovu načte s daty vybrané firmy. Neuložené změny předem uložte. Účetní je zobrazovaný název role Admin; správu uživatelů má pouze SysAdmin.

Volbou **Přidat firmu** založíte další firmu bez nové registrace nebo změny hesla. Pokud příprava firmy selže, použijte **Opakovat nastavení**; opakuje se příprava stejné firmy. Firmu lze vybrat po dokončení přípravy.

SysAdmin může pozvat existující účet do další firmy. Otevřete odkaz pod pozvaným emailem a zvolte přijetí pozvánky. Pokud nejste přihlášení, přihlaste se tlačítkem na stránce pozvánky; stejná karta se poté vrátí k přijetí. Pozvánka nemění heslo ani externí přihlášení. SysAdmin může změnit roli nebo odebrat přístup jen k jedné firmě; ostatní členství zůstávají zachována.

V **Nastavení → Integrace** vyberte firmy, ke kterým smí nový API klíč přistupovat. Aktuální firma je předvybraná a výběr zobrazuje názvy firem. Po vytvoření nejprve bezpečně uložte zobrazený klíč a potvrďte to tlačítkem; do té doby nelze vytvořit další klíč. Existující klíče automaticky nezískávají přístup k nově přidaným firmám. Připojené OAuth aplikace zůstávají vázané na firmu schválenou při připojení.

Neplatné nebo vypršené schválení OAuth zobrazí vysvětlení a pokyn zahájit připojení znovu. Při dočasné chybě načtení lze požadavek zopakovat.

MCP nástroje přijímají volitelný `companyId` pro každé volání osobním API klíčem s odpovídajícím oprávněním. `list_companies` zobrazí dostupná aktivní členství a `select_company` ověří výběr; `companyId` opakujte při dalších voláních, protože se neukládá. S OAuth parametr vynechte a použijte firmu schválenou při připojení. `add_company` vyžaduje zápis a samotným vytvořením nerozšíří oprávnění použitého klíče. Správa členství jiných uživatelů je dostupná pouze SysAdminovi; podrobnosti jsou v ADMINGUIDE.
