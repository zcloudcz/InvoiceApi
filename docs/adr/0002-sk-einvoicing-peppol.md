# ADR 0002 — SK povinná e-fakturace 2027: UBL/Peppol BIS export a napojení na digitálního poštáře

- **Stav:** Proposed (čeká na rozhodnutí ownera, §8)
- **Datum:** 2026-09-29
- **Story:** N7 (`research/plan-2026-W39-extra.md`, backlog R-007)
- **Dotčené projekty:** `Fakvio.Infrastructure`, `Fakvio.Application`, `Fakvio.Contracts`, `Fakvio.API`,
  `Fakvio.UI.Shared`, `Fakvio.McpServer`, `Fakvio.Domain` (jen F1.8), `Fakvio.Tests.Unit`
- **Kód ověřen proti:** `origin/develop` `e9ee572` (2026-09-29)

---

## 1. Kontext

### 1.1 Právní rámec (ověřeno z primárních zdrojů 2026-09-29)

| Téma | Stav | Zdroj |
|---|---|---|
| Zákon | **č. 385/2025 Z. z.** (novela zákona 222/2004 o DPH), schválen NR SR 9. 12. 2025, vyhlášen 19. 12. 2025. E-fakturace je v § 85o (přechodné ustanovení), doručovací služba v § 76a (účinný už od 1. 1. 2026). | [FS-info-7], [slov-lex] |
| Účinnost | Dobrovolně 1. 1.–31. 12. 2026; **povinně od 1. 1. 2027** tuzemské B2B/B2G; přeshraniční + zrušení KV DPH od **1. 7. 2030** (ViDA). | [FAQ] I/15, I/17, I/62 |
| Kdo **vystavuje** | Jen **SK plátci DPH** (§ 4, 4b, 4c) za tuzemská plnění (SK→SK) pro tuzemskou zdanitelnou osobu nebo nezdanitelnou PO; vč. faktury k přijaté platbě (záloze). | [FAQ] I/38, I/62 |
| Kdo **přijímá** | **Každá** PO a zdanitelná FO (i neplátce, živnostník, pronajímatel) — musí mít smluvního digitálního poštáře a DIČ. | [FAQ] I/7, I/54, I/70 |
| Kdo ne | B2C; neplátci nemusí vystavovat (mohou dobrovolně, pak kategorie `O`); plnění osvobozená § 28–42 (nevzniká povinnost faktury); SIS/VS a utajované; zjednodušená faktura z eKasy ≤ 400 €. | [FAQ] I/4, I/37, I/54, I/55, I/66 |
| **Formát** | XML dle **EN 16931**, syntax UBL 2.1 nebo CII; v síti Peppol **Peppol BIS Billing 3.0** (+ schematron CEN a Peppol). Samostatný SK CIUS/schematron **neexistuje** — SK specifika jsou v PASR (identifikátor `0245:DIČ`, centrální SMP) a v transpoziční tabulce FS (v1.11, 2026-09-11), která značí, co je povinné podle SK práva. | [FAQ] I/1, II/37 (XSD), [PASR], [XLSX] |
| **Kanál** | „Doručovací služba“ = síť **Peppol** přes **certifikovaného poskytovatele (PDS, „digitálny poštár“)** akreditovaného Peppol autoritou SK (FR SR). SK subjekty musí být v **centrálním SK SMP** pod `0245:<10místné DIČ>`; na příjem má participant **právě jednoho** poštáře, na odesílání jich může mít víc. Poštáře si podnikatel volí na portálu FS (`vpds.financnasprava.sk`). | [PASR] §1, §4, §7; [FAQ] I/36, I/71, II/48 |
| E-mail/jiná cesta | E-fakturu lze poslat i jinak než doručovací službou jen **se souhlasem příjemce**; pak odpadá oznamovací povinnost. | [FAQ] I/66 |
| **Reporting** | **5-rohý model**: C2 (poštář odesílatele) a C3 (poštář příjemce) automaticky oznamují údaje FS (C5) ve formátu **TDD**; odesílatel při vyhotovení, příjemce do **5 dnů** od přijetí. Reportuje se jen, je-li na dokladu IČ DPH; i když doručení selže. Endpoint C5 plánován Q3/2026. **Fakvio ani uživatel nic neposílá sám.** | [FAQ] I/18, I/32, I/44, II/9; [PASR] §6; [TDD] |
| Lhůty | Vyhotovit a odeslat do **15 dnů** od dodání / přijetí platby (beze změny). | [FAQ] I/13 |
| Sankce | Neoznámení / chybné / pozdní údaje: pokuta **do 10 000 €**, opakovaně **do 100 000 €**; zjevný omyl opravený hned a prokázané selhání poštáře bez pokuty. | [FAQ] I/18 |
| E-faktura vs PDF | PDF ani sken **nejsou** e-faktura. PDF smí být přílohou (BT-125, od Peppol hotfixu 3.0.20). Pro čitelnost stačí XML. | [FAQ] I/3, II/7, II/8 |
| Zálohy | Proforma (výzva k platbě) **se Peppolem neposílá**; faktura k přijaté platbě ano. Odpočet zdaněné zálohy = záporný řádek (množství −1) + BT-25; nezdaněná záloha = BT-113. | [FAQ] I/34, I/38, II/38 |
| Opravy | Dobropis s odkazem BT-25 na původní fakturu (BT-25 je opakovatelný); fakturu nelze přes Peppol „odmítnout“. | [FAQ] I/11, I/50, II/49 |
| Kategorie DPH | Podle právního režimu, ne podle výše DPH: `S`, `E`(+VATEX), `K`, `G`, `AE` (VATEX-EU-AE), `O` (VATEX-EU-O, bez IČ DPH — BR-O-02), `Z` jen pro skutečnou nulovou sazbu. | [FAQ] III |
| Náklady pro uživatele | FS odhaduje 5–12 €/měs. za jednoduchou aplikaci poštáře. Konkurence: SuperFaktúra příjem zdarma, 50 odeslání/měs. v placeném tarifu; iDoklad.sk 50/měs. zdarma; Faktúrovo zdarma do konce 2026. | [FAQ] I/10, `research/competitors.md` |
| Stav dokumentace | Žádná samostatná „vyhláška“ — formát je daný odkazem na EN 16931 (vykonávací rozhodnutí EK 2017/1870), Peppol BIS a PASR. FS průběžně aktualizuje FAQ (poslední 2026-09-11), transpozici (v1.11) a seznam PDS (2026-09-29, 78 certifikovaných). | [ES-page] |

### 1.2 Koho se to v praxi týká (dopad na Fakvio)

| Segment uživatele Fakvia | Povinnost od 1. 1. 2027 | Co potřebuje od Fakvia |
|---|---|---|
| CZ firma (plátce i neplátce) fakturující SK firmě | **žádná** — povinnost je jen SK→SK | nic; volitelně UBL příloha jako komfort pro SK odběratele |
| SK plátce DPH (tuzemské B2B) | vystavovat EN 16931 XML přes poštáře do 15 dnů + přijímat | minimálně UBL export (FS výslovně připouští „vyexportuj XML a nahraj ho do aplikace poštáře“, [FAQ] I/14); plnohodnotně odeslání přes AP (fáze 2) + SK lokalizace (§1.4) |
| SK neplátce (živnostník) | jen **přijímat** přes poštáře | nic povinného; komfort = import UBL do přijatých faktur |
| CZ firma přijímající faktury od SK/EU dodavatelů v UBL | žádná | komfort = import UBL do přijatých faktur |

Dlouhodobě: ViDA zavede **přeshraniční B2B e-fakturaci v celé EU od 1. 7. 2030** (i pro CZ plátce). UBL/EN 16931
export je tedy investice použitelná i mimo SK.

### 1.3 Fakvio dnes (ověřeno v kódu)

**Podpora SK firem je jen částečná:**
- SK je jen v daňovém kalkulátoru OSVČ (`TaxYearConfig` seed `MasterDbContext.cs:1142-1175`, `TaxEstimationService.cs:520`).
- DPH sazby seed jen CZ 21/12/0 (`TenantDbContext.cs:1432-1435`); SK 23/19/5 si uživatel musí přidat ručně (`/vat-rates`).
- Země adresy je volný text s výchozí hodnotou „Česká republika“ (`ClientDetail.razor:479`, `CompanyDetail.razor:443`) — žádný ISO kód.
- `Client.TaxNumber` je jediné pole pro DIČ/IČ DPH (`Client.cs:27`); SK DIČ (10 číslic) vs IČ DPH (`SK`+DIČ) se nerozlišuje.
- ARES jen CZ, žádný SK registr (RPO/ORSR); UI jen CZ/EN; EPO/kontrolní hlášení jen CZ; kurzy ČNB.
- ISDOC natvrdo `LocalCurrencyCode = CZK` (`IsdocMapper.cs:96`).

**Co už máme a využijeme:**
- ISDOC 6.0.2 export: `IsdocMapper.cs` (čistý statický mapper, XDocument) + `IsdocExportService.cs` (načtení s `Include`, XSD
  validace s cache `Lazy<XmlSchemaSet>`, serializace UTF-8 bez BOM) — **vzor** pro UBL.
- Endpointy `GET /api/invoice/{id}/isdoc` (`InvoiceController.cs:910-940`), bulk ZIP (`:1268-1313`), UI tlačítko
  (`InvoiceDetail.razor:255-259`, `:1830`), bulk (`Invoices.razor:43`), MCP `ExportInvoiceIsdoc` (`InvoiceTools.cs:487-525`).
- E-mail vždy přikládá PDF + ISDOC (`EmailService.cs:108-142`).
- Přijaté faktury: e-mail pipeline ISDOC > PDF/QR > AI (`InvoiceEmailProcessor.cs:262-268`), ruční import jen PDF
  (`ImportController.cs:55-110`), výstup `InvoiceExtractedData`.
- Readiness vzor: `TenantNotReadyException` + `ReadinessIssueDto` + `ToBadRequestResult()` (400 `{code, missingFields, issues}`)
  a lokalizace `Readiness_Code_*`.

**Vedlejší nález:** `IsdocMapper.cs:217` zapisuje volný text země („Česká republika“) do `Country/IdentificationCode` — ISDOC
chce ISO kód. Opraví se stejným helperem jako UBL (F1.1).

### 1.4 Gap analýza: datový model vs EN 16931 / Peppol BIS 3.0 (+ SK)

| BT | Význam | Fakvio | Řešení |
|---|---|---|---|
| BT-24/23 | Customization/Profile ID | — | konstanty (F1.3) |
| BT-1, BT-2, BT-9 | číslo, datum vystavení, splatnost | `DocumentNumber`, `IssueDate`, `DueDate` | ok; Draft (bez čísla) odmítnout |
| BT-3 | typ dokladu | `EDocumentType` | 380 Invoice, 386 TaxReceiptForAdvance, 381 CreditNote (kořen `CreditNote`); **Proforma odmítnout** ([FAQ] I/34) |
| BT-7 | DUZP | `TaxableSupplyDate` | ok |
| BT-10 / BT-13 | reference kupujícího / objednávka (PEPPOL-EN16931-R003: aspoň jedno) | **chybí** | fallback BT-10 = `DocumentNumber`; pole „Číslo objednávky“ až na požádání (SK ho nevyžaduje, [XLSX] BT-13) |
| BT-25 | odkaz na předchozí fakturu | `OriginalInvoiceId` | dobropis → původní faktura; konečná faktura s odpočtem → daňové doklady k záloze |
| **BT-34 / BT-49** | elektronická adresa prodávajícího/kupujícího (**povinné**) | **chybí** | odvodit z `TaxNumber`+země (`0245:DIČ` SK, `9929:CZ…` CZ); volitelný override `Client.PeppolId` (F1.8) |
| BT-30/47 | IČO (+ schéma `0158` pro SK) | `RegistrationNumber` | ok; schemeID jen pro SK |
| BT-31/48 | IČ DPH | `TaxNumber` + `IsVatPayer` | jen když plátce a kategorie ≠ `O` (BR-O-02) |
| BT-33 | SK: právní forma + zápis v OR (Obch. zák. § 3a) | **chybí** | mimo N7 → SK lokalizace |
| BT-40/55 | kód země ISO 3166 | volný text | normalizace (F1.1) |
| BT-35–38 | adresa | `Street`, `AddressLine2`, `City`, `PostalCode` | ok (SK povinné, [XLSX]) |
| BT-81 | způsob platby UNCL4461 | `EPaymentMethod` | 58 (SEPA, IBAN+EUR) / 30 / 10 / 48 / ZZZ |
| BT-83 | VS | `VariableSymbol` | `PaymentID` (SK: VS max 10 číslic, [XLSX]) |
| BT-84/86 | IBAN, BIC | `IBAN`, `SWIFT`, `BankAccountNumber` | povinné pro 30/58 (BR-61) |
| BT-130 | jednotka UN/ECE Rec 20 | volný text (`"ks"`, `"pcs"`, `InvoiceItemEditor.razor:256`) | mapování (F1.1), fallback `C62` |
| BT-151/118 | kategorie DPH | `EVatRegime` + sazba + `IsVatPayer` | mapování §4.1.2 |
| BT-120/121 | důvod osvobození / VATEX | **chybí** | obecný text + VATEX dle kategorie; detailní VATEX (132-1x) později |
| BT-146 | cena (nesmí být záporná, BR-27) | odpočet zálohy má zápornou cenu (`InvoiceService.cs:1302`) | záporný řádek → množství −1, cena kladná ([FAQ] II/38) |
| BT-6/BT-111 | DPH v měně účtování, pokud měna ≠ EUR (SK) | kurz na faktuře **není** uložen | fáze 1: SK prodávající + měna ≠ EUR → blokující pre-flight |
| BT-155 | kód položky | `ProductCode` | ok |
| Textové řádky | — | `IsTextRow` | do BT-22 (poznámka), ne jako řádek |

---

## 2. Rozhodnutí (souhrn)

1. **Fáze 1 hned (bez smlouvy s AP):** export **UBL 2.1 / Peppol BIS Billing 3.0** vedle ISDOC (Invoice, CreditNote,
   daňový doklad k záloze), odvození Peppol ID, runtime **pre-flight kontroly** (readiness vzor), **plná validace v testech**
   (UBL XSD + schematron CEN + Peppol přes Saxon-HE/IKVM jen v testovacím projektu), UI + MCP + e-mail příloha pro SK
   odběratele + **import UBL** do přijatých faktur. Pro SK plátce to znamená legální ruční cestu (export → nahrát do
   aplikace poštáře); pro všechny přípravu na ViDA 2030.
2. **Fáze 2 jen po rozhodnutí ownera cílit SK plátce DPH:** odeslání/příjem přes certifikovaného SK poštáře přes
   **SAPI-SK** (standardizované API SK Peppol komunity — jedna integrace, poskytovatel vyměnitelný). **Doporučený AP:
   ePošťák (Kaja Solutions s.r.o.)**, náhradní **Verteco** (white-label) nebo **Peppoš (Dativery, CZ)**.
3. **Fáze 3 (reporting FS) nestavíme** — reportuje poštář (C2/C3) ve formátu TDD. Znovu otevřít jen při vlastním AP
   nebo s ViDA 2030.
4. **Vlastní certifikace Peppol AP: zamítnuto** (§3).

---

## 3. Varianty

| Varianta | Pro | Proti | Verdikt |
|---|---|---|---|
| A. Nic nedělat, SK plátce vyloučit (web/registrace) | nulová práce | SK uživatelé od 1/2027 odejdou; žádná příprava na ViDA; konkurence (SuperFaktúra, iDoklad.sk, Faktúrovo) už Peppol má | jen pokud owner řekne „SK není trh“ — i pak doporučujeme fázi 1 kvůli ViDA/importu |
| **B. UBL export (fáze 1) + SK poštář přes SAPI-SK (fáze 2)** | malý SaaS bez infrastruktury; pay-per-use; výměna poskytovatele bez přepisu | závislost na třetí straně; onboarding přes portál FS dělá uživatel | **doporučeno** |
| C. Mezinárodní AP s proprietárním API (Pagero, ecosio, Basware, Unimaze, Tickstar, Qvalia, Billit) | pokryjí i EU/ViDA | enterprise/individuální ceny, u Qvalia 0,50–1 €/zprávu (dle přehledu třetí strany, neověřeno); proprietární API | záloha pro EU fázi 2030 |
| C'. Storecove (populární AP API pro SaaS) | dobré API | **není na seznamu certifikovaných PDS FS SR** (2026-09-29) → SK koncové uživatele obsloužit nesmí ([PASR] §7) | vyloučeno pro SK |
| D. Vlastní Peppol AP | žádný poplatek za doklad | členství OpenPeppol + Service Provider Agreement, AS4 infrastruktura s PKI certifikáty, Peppol testbed, **SK akreditace** (PO v EU, výpisy z RT statutárů, SK onboarding test), zápis do centrálního SK SMP, **TDD reporting na C5**, MLS, účast na provozních schůzkách, deregistrace do 3 pracovních dnů, 24/7 provoz, audit — neúměrné produktu zdarma bez týmu | **zamítnuto** |

---

## 4. Návrh

### 4.1 Fáze 1 — UBL export, validace, model

#### 4.1.1 Struktura (zrcadlí ISDOC, žádná nová abstrakce)
- `Fakvio.Infrastructure/Service/Ubl/UblCodes.cs` — `internal static`: kódy a odvození (země, jednotky, kategorie DPH,
  způsob platby, Peppol ID).
- `Fakvio.Infrastructure/Service/Ubl/UblMapper.cs` — `internal static Map(Invoice) → XDocument`, bez I/O (vzor `IsdocMapper`).
- `Fakvio.Application/Service/IUblExportService.cs` + `Fakvio.Infrastructure/Service/UblExportService.cs` — načtení
  (vzor `IsdocExportService.cs:44-73`), pre-flight, serializace (zkopírovat `SerialiseToBytes`). Interface má smysl kvůli
  vrstvám a DI (stejně jako ISDOC), ne kvůli více implementacím.
- Namespaces: `urn:oasis:names:specification:ubl:schema:xsd:Invoice-2` / `CreditNote-2`, `cac`, `cbc`.
- Konstanty: `CustomizationID = urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0`,
  `ProfileID = urn:fdc:peppol.eu:2017:poacc:billing:01:1.0`.
- **Pořadí elementů striktně dle UBL 2.1 XSD** (jinak XSD selže) — viz Peppol syntax strom [BIS].

#### 4.1.2 Mapování (pravidla, která musí držet i testy)

**Kategorie DPH (BT-151/BT-118)** — `UblCodes.VatCategory(regime, rate, issuerIsVatPayer)`:

| Vstup | Kategorie | Sazba v XML | BT-121 / BT-120 |
|---|---|---|---|
| vystavitel **není plátce** (všechny řádky) | `O` | — (bez `Percent`) | `VATEX-EU-O` / „Nepodléhá DPH“; **bez BT-31/BT-48** |
| `Standard`, sazba > 0 | `S` | sazba | — |
| `Standard`, sazba 0 (CZ seed „0 % osvobozeno“) | `E` | 0 | — / „Osvobozeno od DPH“ |
| `Exempt` | `E` | 0 | — / „Osvobozeno od DPH“ |
| `ReverseCharge` | `AE` | **0** (informativní DPH se do UBL nedává) | `VATEX-EU-AE` / „Přenesení daňové povinnosti“; oba VAT ID povinné |
| `OutOfScope` u plátce | `O` | — | `VATEX-EU-O`; **nesmí se mísit** s jinou kategorií (BR-O-11..14) → pre-flight |

`Z`, `K`, `G` fáze 1 negeneruje (Fakvio je nerozlišuje; SK nulovou sazbu nemá). Text BT-120 podle jazyka klienta
(`Client.Language`), SK text pro SK prodávajícího.

**Peppol ID (BT-34/BT-49)** — `UblCodes.EndpointId(Client)`, pořadí: (1) `Client.PeppolId` (po F1.8);
(2) země `SK` a `TaxNumber` odpovídá `^(SK)?(\d{10})$` → `0245` + 10 číslic; (3) země `CZ` a `TaxNumber` `^CZ\d{8,10}$` →
`9929` + celé DIČ; (4) jinak `null` → blokující pre-flight. (`9950` pro SK nepoužívat — PASR vyžaduje `0245`.)

**Země** — `UblCodes.CountryToIso2(string?)`: prázdné → `CZ`; dvoupísmenný kód → upper; známé názvy CZ/SK/EN
(„Česká republika“, „Česko“, „Czech Republic“, „Slovensko“, „Slovenská republika“, „Slovakia“, „Německo“/„Deutschland“,
„Rakousko“/„Österreich“, „Polsko“/„Polska“) → kód; neznámé → `null` (pre-flight). Bez nové knihovny.

**Jednotky** — `UblCodes.UnitToRec20(string?)` (case-insensitive, bez tečky): `ks, kus, pcs, pc` → `H87`; `hod, h, hr` → `HUR`;
`min` → `MIN`; `den, d, day` → `DAY`; `měs, mes, month` → `MON`; `rok, year` → `ANN`; `km` → `KMT`; `m` → `MTR`;
`m2, m²` → `MTK`; `m3, m³` → `MTQ`; `kg` → `KGM`; `g` → `GRM`; `t` → `TNE`; `l` → `LTR`; `bal` → `XPK`; `kpl, sada, set` → `SET`;
už platný kód (3 znaky velkými písmeny) nechat; jinak `C62`.

**Způsob platby (BT-81)** — `BankTransfer`: IBAN a měna EUR → `58`, jinak `30`; `BT-84` = IBAN, jinak `BankAccountNumber`;
bez obojího → pre-flight. `Cash` → `10`, `CreditCard` → `48`, `PayPal`/`Other` → `ZZZ`. VS → `cbc:PaymentID`.
Konstantní/specifický symbol se nepřenáší.

**Řádky** — text řádky do `cbc:Note`; řádek se zápornou cenou (odpočet zálohy) → `InvoicedQuantity = -Quantity`,
`PriceAmount = |UnitPrice|`, `LineExtensionAmount` záporná; `ProductCode` → `SellersItemIdentification`;
`Description` → `cbc:Name` (a `cbc:Description`, je-li delší než 100 znaků, zkrátit `Name`).

**Součty** — mapper je počítá z řádků, aby platily BR-CO-10..16 přesně: `LineExtensionAmount = Σ řádků`,
per (kategorie, sazba) `TaxAmount = round(base × rate / 100, 2, AwayFromZero)` (BR-CO-17, [FAQ] II/40), `TaxExclusive`,
`TaxInclusive = TaxExclusive + TaxTotal`, `PayableAmount = TaxInclusive`. Když se výsledné DPH liší od uloženého
`Invoice.TotalVat` o víc než 0,05, zalogovat warning (PDF a UBL by se lišily — k prošetření, ne k tichému přepsání).
Všechny částky 2 desetinná místa (`F2`, InvariantCulture) s `currencyID`.

**Dobropis** — kořen `CreditNote`, `CreditNoteTypeCode = 381`, `CreditNoteLine`/`CreditedQuantity`; Fakvio neukládá
znaménko jednotně (uživatel zadává), proto se každý řádek bere v absolutní hodnotě nezávisle na ostatních (ne podle
znaménka `TotalWithVat` za celý doklad — to selže na dokladu s kombinovanými znaménky řádků, viz F1.4 changelog).
Řádky s kombinovanými znaménky (`+` i `−` zároveň) blokuje pre-flight (`EINVOICE_CREDIT_NOTE_MIXED_SIGN_LINES`) —
export by jinak tiše spočítal jinou částku, než dobropis ve skutečnosti představuje.
`BillingReference/InvoiceDocumentReference/ID` = číslo původní faktury. **Implementační odchylka:** bez `IssueDate`
— oficiální Peppol fixture (`base-creditnote-correction.xml`) má v `InvoiceDocumentReference` jen `ID`, `IssueDate`
tam není a UBL/Peppol schematron ho nevyžaduje.

**Konečná faktura s odpočtem zálohy** — `BillingReference` na každý `TaxReceiptForAdvance`, jehož `OriginalInvoiceId`
= `Invoice.OriginalInvoiceId` (proforma). Načíst ve službě, mapperu předat jako seznam čísel.

#### 4.1.3 Validace — pragmatika
- **Runtime:** žádný schematron ani UBL XSD v produkci (mapper je deterministický; chyby tvaru XML jsou bugy mapperu →
  chytají testy). Za běhu jen **pre-flight datových podmínek** (§5, F1.5), které uživatel umí opravit, s odkazem kam.
  Poštář (fáze 2) validuje BIS znovu před odesláním.
- **Testy:** (a) UBL 2.1 XSD (OASIS `os-UBL-2.1`, jen `maindoc` Invoice/CreditNote + `common`) přes `System.Xml.Schema`;
  (b) schematron: `.NET` umí jen XSLT 1.0, artefakty EN 16931/Peppol jsou XSLT 2.0 → **`SaxonHE12s9apiExtensions` 12.10.1**
  (Saxon-HE 12 přes IKVM, MPL-2.0, net10.0) **jen v `Fakvio.Tests.Unit`**; předkompilované XSLT:
  `EN16931-UBL-validation.xslt` (ConnectingEurope `validation-1.3.16`, 2026-04-13) a `PEPPOL-EN16931-UBL.xslt`
  (phive-rules `openpeppol/2026.5` = Peppol BIS May 2026 release). Test: žádný `svrl:failed-assert[@flag='fatal']`.
  SaxonCS (komerční) netřeba. Pozor: `IKVM.Maven.Sdk` stahuje Saxon JAR z Maven Central při buildu (CI testy dnes
  nespouští, agenti běží lokálně na Windows).
- **Fallback, pokud IKVM build zlobí:** schematron testy označit `[Trait("Category","Schematron")]` a vyřadit z výchozího
  běhu; ručně ověřit fixtures na Peppol testbedu (<https://www.testbed.peppol.org/validation>) a výsledek zapsat do PR.

#### 4.1.4 Změna modelu
Jediná DB změna fáze 1: volitelný `Client.PeppolId` (`varchar(64)`, nullable, formát `^\d{4}:[A-Za-z0-9.\-:]+$`) —
override pro případy, kde odvození z `TaxNumber` nestačí (DPH skupina, zahraniční odběratel). Client je v master i tenant
kontextu → dvě migrace. Bez override pole funguje odvození; F1.8 je proto samostatný, odložitelný task.

### 4.2 Fáze 2 — odeslání a příjem přes AP (až po rozhodnutí ownera, §8)

**Doporučený poskytovatel: ePošťák (Kaja Solutions s.r.o.)** — na seznamu certifikovaných PDS FS SR (2026-09-29);
veřejná dokumentace, sandbox `dev.epostak.sk`, rozhraní **SAPI** (hotové UBL XML) i Enterprise API (integrátor s centrálním
klíčem + `X-Firm-Id`, webhooky, stav AS4 doručení i Invoice Response, white-label registrace do SMP); pay-per-use
**0,10 € odeslání / 0,08 € příjem** (1–1 000 dokladů/měs.), bez setup poplatku a minima ([ePostak-API], 2026-09-29).
Riziko: malá firma (sama uvádí, že není plátce DPH) → zmírňuje SAPI-SK (přechod bez přepisu).

**Náhrada:** **Verteco digital services** — program „sprostredkovateľ“ 99 €/rok + 2 €/aktivní firma/měs. (nebo
0,01 €/doklad, min. 300 €/měs.), příjem zdarma, SAPI-SK, sandbox, white-label ([Verteco], 2026-09-29).
Alternativa s CZ podporou a výhledem na CZ/ViDA: **Peppoš (Dativery s.r.o.)**, REST API, free tarif 5 dokladů pak
0,20 €/doklad (dle [epostari] třetí strany, neověřeno).

**Architektura:**
- Typed `HttpClient` `SapiSkClient` (`Fakvio.Infrastructure/Service/EInvoice/`) podle SAPI-SK 1.0 ([SAPI]):
  `POST /auth/token` (client_credentials, access 15 min, refresh 30 dní), `POST /document/send` (hlavičky
  `Idempotency-Key` UUID + `X-Peppol-Participant-Id`, payload UBL ≤ 10 MB, odpověď 202 `ACCEPTED|REJECTED`),
  `GET /document/receive`, `GET /document/receive/{id}`, `POST /document/receive/{id}/acknowledge`. Interface až při druhém protokolu.
- SAPI 1.0 **nemá endpoint stavu doručení** → stav (AS4 delivered / failed) přes rozšíření poskytovatele (ePošťák
  `GET /documents/{id}/status`) nebo webhook; izolovat do jedné metody.
- Nová tenant entita `EInvoiceTransmission`: `InvoiceId?`, `ReceivedInvoiceId?`, `Direction`, `ProviderDocumentId`,
  `IdempotencyKey`, `Status` (`Queued, Accepted, Delivered, Failed, Rejected`), `LastError`, časy. `Idempotency-Key` =
  `Id` transmise → bezpečné opakování.
- Odeslání: tlačítko „Odeslat e-fakturu (Peppol)“ → pre-flight → fronta; `BackgroundService` + PostgreSQL advisory lock
  (pravidlo CLAUDE.md) odesílá a dotahuje stavy; notifikace při `Failed`.
- Příjem: `BackgroundService` periodicky `GET /document/receive` pro participanty tenantů → `UblImportParser` (F1.10) →
  `ReceivedInvoice` + příloha XML → `acknowledge`. Webhook později.
- Konfigurace: centrální přihlašovací údaje integrátora v Key Vault / `SystemConfiguration`; na tenantovi jen
  `PeppolParticipantId` vydavatele a příznak „Peppol aktivní“ (odvodit z issuer `Client`). Per-tenant tajemství jen pokud
  AP integrátorský model nenabídne (šifrovat `ICredentialProtector` jako SMTP).
- **Onboarding SK tenanta (dělá uživatel, Fakvio navede):** mít DIČ → na portálu FS vybrat poštáře
  (`vpds.financnasprava.sk`, pro PO nutná autorizace zastupování) → poštář zapíše `0245:DIČ` do SK SMP → ve Fakviu
  „Otestovat spojení“.
- **Omezení:** příjem má participant jen u **jednoho** poštáře ([FAQ] I/36). Když už tenant poštáře má (účetní, banka),
  Fakvio mu může přes svého AP **odesílat**, ale přijaté e-faktury si dovozí přes import UBL (F1.10, e-mail/upload).

### 4.3 Fáze 3 — reporting FS
Není potřeba: TDD na C5 posílá C2/C3 ([PASR] §6, [FAQ] I/32). Fakvio musí jen generovat validní BIS (a uvádět IČ DPH,
podle kterého se reporting spouští). Revize: vlastní AP (zamítnuto) nebo 1. 7. 2030 (přeshraniční + zrušení KV DPH).

### 4.4 Co fáze 1 **neřeší** (SK lokalizace — samostatný epic, jen pokud owner cílí SK plátce)
SK DPH sazby 23/19/5 v seedu, EUR jako lokální měna (ISDOC `IsdocMapper.cs:96`), kurzy ECB/NBS pro BT-6/BT-111, BT-33
(zápis v OR), SK jazyk UI, lookup SK registru (RPO), SK DPH přiznání/KV, rozlišení `K`/`G` a detailní VATEX.

---

## 5. Implementační plán fáze 1 (tasky pro implementaci)

Obecně: komentáře pro .NET juniora, testy xUnit + Shouldly (vzor `IsdocExportServiceTests.cs` — InMemory `TenantDbContext`,
`IsdocMapper` je `internal`, `InternalsVisibleTo` pro `Fakvio.Tests.Unit` už existuje), resources CZ+EN, DEVGUIDE/USERGUIDE
aktualizovat v tasku, kterého se týkají. Limit ~400 řádků diffu bez vendorovaných artefaktů (XSD/XSLT).

| Task | Závisí | Velikost |
|---|---|---|
| F1.1 `UblCodes` | — | S |
| F1.2 Validační harness v testech | — | S (+ artefakty) |
| F1.3 `UblMapper` — faktura a daňový doklad k záloze | F1.1, F1.2 | M |
| F1.4 `UblMapper` — dobropis, odpočet zálohy, neplátce, PDP | F1.3 | M |
| F1.5 `UblExportService` + pre-flight + API | F1.3 | M |
| F1.6 UI (detail + hromadně) + USERGUIDE | F1.5 | S |
| F1.7 MCP nástroj `export_invoice_ubl` | F1.5 | S |
| F1.8 `Client.PeppolId` override | F1.1 | M |
| F1.9 UBL příloha e-mailu pro SK odběratele | F1.5 | S |
| F1.10 Import UBL do přijatých faktur | F1.2 | M |

Paralelně: {F1.1, F1.2, F1.10} → F1.3 → {F1.4, F1.5, F1.8} → {F1.6, F1.7, F1.9}.

### F1.1 — `UblCodes` (kódy a odvození)
- **Soubory:** nový `Fakvio.Infrastructure/Service/Ubl/UblCodes.cs`; test `Fakvio.Tests.Unit/UblCodesTests.cs`
  (`[Theory]`); jednořádková oprava `IsdocMapper.cs:217` na `UblCodes.CountryToIso2(address?.Country) ?? "CZ"`.
- **Obsah:** `CountryToIso2`, `UnitToRec20`, `VatCategory(EVatRegime, decimal rate, bool issuerIsVatPayer)` → record
  `(string Code, decimal? Percent, string? ExemptionCode, string? ExemptionTextKey)`, `PaymentMeansCode(EPaymentMethod?,
  string? iban, string currency)`, `EndpointId(Client)` → `(string SchemeId, string Value)?` — pravidla §4.1.2.
- **AC:**
  - Tabulky §4.1.2 pokryté theory testy vč. `"Česká republika"`→`CZ`, `"SK2020123456"`→`0245:2020123456`,
    `"2020123456"` u SK neplátce→`0245:2020123456`, `"CZ12345678"`→`9929:CZ12345678`, neznámá jednotka→`C62`.
  - `Standard`+0 % → `E`; neplátce → `O` bez sazby; `ReverseCharge` → `AE` s `Percent = 0`.
  - Stávající ISDOC testy procházejí, ISDOC nově obsahuje ISO kód země.

### F1.2 — Validační harness (jen testovací projekt)
- **Soubory:** `Fakvio.Tests.Unit/Fakvio.Tests.Unit.csproj` + `PackageReference SaxonHE12s9apiExtensions 12.10.1`;
  `Fakvio.Tests.Unit/Ubl/Artifacts/` — UBL 2.1 XSD (`maindoc/UBL-Invoice-2.1.xsd`, `UBL-CreditNote-2.1.xsd` + `common/*`),
  `EN16931-UBL-validation.xslt`, `PEPPOL-EN16931-UBL.xslt`, `README.md` (zdroj, verze, datum, licence), csproj
  `None CopyToOutputDirectory`; `Fakvio.Tests.Unit/Ubl/UblTestValidator.cs` (`ValidateXsd(XDocument)` a
  `ValidateSchematron(XDocument)` → `IReadOnlyList<string>` chyb; XSLT kompilovat jednou, `static Lazy`);
  `Fakvio.Tests.Unit/Ubl/UblValidatorSmokeTests.cs`.
- **AC:**
  - Oficiální Peppol příklad `base-example.xml` (repo `OpenPEPPOL/peppol-bis-invoice-3`, `rules/examples`) projde XSD i oběma
    schematrony bez fatal chyb; uměle rozbitý (smazaný `cbc:EndpointID`) vrátí `PEPPOL-EN16931-R020`.
  - Produkční projekty nemají novou závislost (`dotnet list package` pro `Fakvio.Infrastructure`/`Fakvio.API` beze změny).
  - Pokud IKVM na stroji selže: fallback §4.1.3 (Trait + ruční testbed) popsán v README artefaktů.

### F1.3 — `UblMapper`: faktura (380) a daňový doklad k záloze (386)
- **Soubory:** nový `Fakvio.Infrastructure/Service/Ubl/UblMapper.cs` (`internal static XDocument Map(Invoice invoice,
  IReadOnlyList<string>? precedingDocumentNumbers = null)`); test `Fakvio.Tests.Unit/UblMapperTests.cs`.
- **Obsah:** hlavička (BT-24/23/1/2/3/9/7/22/5/10), `AccountingSupplierParty`/`AccountingCustomerParty` (EndpointID,
  PartyIdentification/LegalEntity s IČO a `schemeID="0158"` jen pro SK, PostalAddress, PartyTaxScheme jen pro plátce a ne-`O`,
  Contact z `Client.Contact`), `PaymentMeans`, `TaxTotal` + `TaxSubtotal` per (kategorie, sazba), `LegalMonetaryTotal`,
  `InvoiceLine` (§4.1.2). Vzor struktury a helperů: `IsdocMapper.cs` (`FormatDecimal`, `FormatDate`, `DeterministicUuid`
  lze zkopírovat/sdílet přesunem do `UblCodes`).
- **AC:**
  - Plátce CZ→SK faktura (2 sazby, IBAN, VS) i SK→SK (EUR, `0245`) projdou `UblTestValidator` (XSD + oba schematrony) bez fatal.
  - `TaxReceiptForAdvance` → `InvoiceTypeCode 386`; `Proforma` → `InvalidOperationException` (služba ji odfiltruje dřív).
  - Součty splňují BR-CO-10..17 (ověřeno schematronem) a test s „kulatinovým“ rozdílem zaloguje warning.
  - Textový řádek se objeví v `cbc:Note`, ne jako `InvoiceLine`.

### F1.4 — `UblMapper`: dobropis, odpočet zálohy, neplátce, přenesená DP
- **Soubory:** `UblMapper.cs` (větev `CreditNote`), `UblMapperTests.cs`.
- **AC:**
  - Dobropis → kořen `CreditNote`, `381`, `CreditNoteLine`, `BillingReference` na původní fakturu; kladné částky i když
    dobropis v DB má záporné řádky; validní dle harness.
  - Konečná faktura s řádkem odpočtu zálohy (vzor `InvoiceService.cs:1295-1305`) → množství −1, kladná cena,
    `BillingReference` na čísla daňových dokladů k záloze; validní.
  - Neplátce → všechny řádky `O`, žádné `PartyTaxScheme/CompanyID`, validní (BR-O-02).
  - `ReverseCharge` → `AE`, 0 %, VATEX-EU-AE, oba VAT ID; validní.

### F1.5 — `UblExportService` + pre-flight + API
- **Soubory:** `Fakvio.Application/Service/IUblExportService.cs`; `Fakvio.Infrastructure/Service/UblExportService.cs`
  (načtení jako `IsdocExportService.cs:44-73` + daňové doklady k záloze); DI vedle `ServiceCollectionExtensions.cs:151`;
  nové konstanty `EINVOICE_*` v `Fakvio.Contracts/Dto/Readiness/ReadinessCodes.cs` + `Readiness_Code_EINVOICE_*` v obou resx;
  `InvoiceController.cs` — `GET /api/invoice/{id}/ubl` (vzor `:910-940`, soubor `{prefix}_{číslo}.xml`, `application/xml`)
  a `GET /api/invoice/bulk/ubl?ids=` (vzor `:1268-1313`); testy `UblExportServiceTests.cs` + endpoint testy (vzor
  `IsdocEndpointTests.cs`); DEVGUIDE §4.2 bod 3 doplnit UBL.
- **Pre-flight** (blokující → `TenantNotReadyException(issues)` → existující `ToBadRequestResult()`; `FixRoute` na
  `/my-company` nebo `/clients/{id}`): `EINVOICE_DRAFT`, `EINVOICE_PROFORMA_NOT_SUPPORTED`, `EINVOICE_BUYER_MISSING`,
  `EINVOICE_SELLER_ENDPOINT_MISSING`, `EINVOICE_BUYER_ENDPOINT_MISSING`, `EINVOICE_ADDRESS_INCOMPLETE` (ulice, město, PSČ,
  země ISO — `MissingFields`), `EINVOICE_PAYMENT_ACCOUNT_MISSING`, `EINVOICE_OUT_OF_SCOPE_MIXED`,
  `EINVOICE_REVERSE_CHARGE_VAT_ID_MISSING`, `EINVOICE_SK_NON_EUR_CURRENCY`, `EINVOICE_NO_LINES`.
- **AC:**
  - Vydaná faktura → 200 a XML shodné s `UblMapper.Map`; neexistující → 404; Draft/Proforma/chybějící Peppol ID odběratele
    → 400 `{code:"TENANT_NOT_READY", issues:[{code:"EINVOICE_…"}]}`.
  - Bulk vrátí ZIP, doklady s pre-flight chybou přeskočí (jako ISDOC bulk) a zaloguje.
  - Každý kód má CZ i EN text (`Readiness_Code_Unknown` se pro ně nikdy neukáže — test přes resx).

### F1.6 — UI + USERGUIDE
- **Soubory:** `InvoiceDetail.razor` (tlačítko „Stáhnout e-fakturu (UBL)“ vedle ISDOC `:255-259`, disabled pro Draft
  a Proforma; při 400 zobrazit issues stejnou komponentou jako readiness), `Invoices.razor:43` (bulk tlačítko),
  `InvoiceApiService.cs:316-330` (`ExportUblAsync`, `BulkExportUblAsync` — vrací výsledek s `issues` z 400 těla, vzor
  `Fakvio.UI.Shared/Models/EpoDownloadResult.cs`; dnešní `ExportIsdocAsync` vrací jen `byte[]?` a chybu by ztratil), resx CZ/EN, `USERGUIDE.md` nová §2.8a
  „Export e-faktury (UBL / Peppol BIS)“ — co to je, kdy použít (SK odběratelé, nahrání do aplikace poštáře), co
  zkontrolovat (DIČ, země, IBAN).
- **AC:** tlačítko stáhne `.xml`; chyba pre-flight ukáže lokalizovaný seznam s odkazem na opravu; tlačítko je disabled pro Draft a Proforma
  (test vzor `DownloadIsdocLogicTests.cs`; text issue přes existující `Components/Shared/ReadinessIssueText.cs`).

### F1.7 — MCP `export_invoice_ubl`
- **Soubory:** `Fakvio.McpServer/Tools/InvoiceTools.cs` (vzor `ExportInvoiceIsdoc` `:487-525`, read-only, idempotent,
  vrací base64 + fileName), `Client/IFakvioApiClient.cs:61` + `FakvioApiClient.cs:131-140` (`ExportInvoiceUblAsync`),
  počet nástrojů 38→39 v DEVGUIDE/USERGUIDE/README (hlídá `McpServer/ToolDiscoveryTests.cs:41`), tool tabulka DEVGUIDE.
- **AC:** `ToolDiscoveryTests` zelené; popis nástroje říká, kdy použít UBL vs ISDOC; 400 z API vrací čitelné kódy
  `EINVOICE_*` (ne stack trace).

### F1.8 — `Client.PeppolId` (volitelný override)
- **Soubory:** `Fakvio.Domain/Entities/Client.cs` (+ XML komentář), konfigurace v `TenantDbContext`/`MasterDbContext`
  (max length 64), migrace `--context TenantDbContext --output-dir Migrations/Tenant` i Master (příkazy DEVGUIDE §4.3 bod 9),
  `ClientDto`/`CreateClientDto`/`UpdateClientDto` (+ ZMapper, pokud mapování není konvenční), `ClientDetail.razor:102`
  a `MyCompany.razor:106` — pole „Peppol ID“ s nápovědou zobrazující odvozenou hodnotu, validace formátu; `UblCodes.EndpointId`
  použije override jako první; resx; USERGUIDE §5 a §10.
- **AC:** prázdné pole = odvození beze změny; neplatný formát → validační chyba formuláře i API 400; migrace nad prázdnou
  i existující DB projdou; UBL používá override.

### F1.9 — UBL příloha e-mailu pro SK odběratele
- **Soubory:** `EmailService.cs:108-142` — když `UblCodes.CountryToIso2(odběratel) == "SK"` (nebo má `PeppolId`), přiložit
  `{prefix}_{číslo}.xml` z `IUblExportService`; pre-flight chyba = příloha se vynechá + warning log (e-mail nesmí selhat);
  test v `EmailService` testech; DEVGUIDE §4.2 bod 7.
- **AC:** SK odběratel dostane PDF + ISDOC + UBL; CZ odběratel beze změny; neúplná data → e-mail odejde bez UBL.
- **Pozn.:** e-faktura e-mailem je u SK plátce legální jen se souhlasem příjemce ([FAQ] I/66) — v USERGUIDE uvést.

### F1.10 — Import UBL do přijatých faktur
- **Soubory:** `Fakvio.Application/Service/IUblImportParser.cs` + `Fakvio.Infrastructure/Service/UblImportParser.cs`
  (vzor `IsdocImportParser.cs`, výstup `InvoiceExtractedData`; Invoice i CreditNote; neznámý root → `null`);
  `InvoiceEmailProcessor.cs:262` — větev pro `.xml` přílohu s kořenem UBL `Invoice-2`/`CreditNote-2` (priorita jako ISDOC, před PDF);
  `ImportController.cs:77` — preview přijme i `.xml` (UBL) a `.isdoc`; DI vedle `ServiceCollectionExtensions.cs:173`;
  testy `UblImportParserTests.cs` na fixtures z F1.2 a na výstupu `UblMapper`; USERGUIDE §8 a §18, DEVGUIDE (příjem faktur).
- **AC:** Peppol `base-example.xml` → správný dodavatel (název, IČO/DIČ), čísla, datumy, součty, položky; e-mail s UBL
  přílohou vytvoří přijatou fakturu bez AI; nevalidní XML → fallback na PDF/AI jako dnes.

---

## 6. Důsledky

**Pozitivní:** SK plátci mají od 1/2027 legální (ruční) cestu hned po fázi 1; CZ uživatelé dostanou import UBL od EU
dodavatelů; základ pro ViDA 2030; žádná nová produkční závislost ani infrastruktura; fáze 2 je vyměnitelná (SAPI-SK).

**Negativní / náklady:** druhý exportní formát k údržbě (Peppol vydává release 2× ročně — aktualizovat XSLT v testech);
IKVM/Saxon zpomalí build testů; odvozené Peppol ID může být u neobvyklých případů chybné (řeší F1.8).

**Odložené vědomě:** `K`/`G` a detailní VATEX, BT-13 číslo objednávky, BT-33, BT-6/BT-111 kurz, UBL pro přijaté faktury
(export), PDF jako BT-125 příloha, SK lokalizace (§4.4), vše z fáze 2.

---

## 7. Rizika
- **Validita vs skutečné AP:** schematron verze se liší od verze u poštáře → držet artefakty na aktuálním Peppol release,
  před fází 2 ověřit v sandboxu AP.
- **Kvalita dat tenantů:** volný text země/jednotek, chybějící DIČ → pre-flight a jasné hlášky místo tichých fallbacků.
- **Právní výklad:** FAQ FS se stále mění (v 2026 průběžně nové verze); před spuštěním fáze 2 znovu projít aktuální FAQ.

---

## 8. Otevřené otázky pro ownera
1. **Cílí Fakvio SK plátce DPH?** (a) Ne → jen fáze 1 (komfort, ViDA, import), na webu/registraci uvést, že odesílání přes
   Peppol neposkytujeme; (b) Ano → fáze 2 + epic SK lokalizace (§4.4). Termín rozhodnutí: do konce 10/2026 (backlog R-007).
2. **Kolik SK tenantů máme?** Ověřit v produkci jen čtením (issueři s adresou `SK`/„Slov…“ nebo `TaxNumber` začínajícím `SK`).
3. **AP a smluvní model:** souhlas s ePošťákem (záloha Verteco/Peppoš)? Integrátorský model (Fakvio platí a přeúčtuje /
   kvóta zdarma) nebo každý tenant vlastní smlouvu s poštářem?
4. **Cena pro uživatele:** Peppol odesílání jako první placená funkce, nebo kvóta zdarma (konkurence: 50/měs.)?
   Pay-per-use 0,10 € × 50 = 5 €/tenant/měs. nákladů při plném čerpání.
5. **E-mailová UBL příloha:** automaticky pro SK odběratele (návrh F1.9), nebo přepínač u klienta?
6. **Pole „Číslo objednávky“ (BT-13):** stačí fallback BT-10 = číslo dokladu, nebo přidat?

---

## 9. Zdroje (ověřeno 2026-09-29)
- [FAQ] FR SR 9/DPH/2025/IM „Najčastejšie otázky a odpovede k eFaktúre“, verze 2026-09-11 (57 s.; oddíly I daňové subjekty,
  II výrobci SW a poskytovatelé, III kategorie DPH/VATEX): <https://www.financnasprava.sk/_img/pfsedit/Dokumenty_PFS/Podnikatelia/Dan_z_pridanej_hodnoty/efaktura/2026/2026.09.11_FAQ_eFaktura.pdf>
- [ES-page] FS SR — eFaktúra (dokumenty, seznam PDS, harmonogram): <https://www.financnasprava.sk/sk/podnikatelia/dane/dan-z-pridanej-hodnoty/e-faktura>
- [FS-info-7] FR SR 7/DPH/2025/I — informace k zákonu 385/2025 (2025-12-29): <https://www.financnasprava.sk/_img/pfsedit/Dokumenty_PFS/Zverejnovanie_dok/Dane/Novinky_leg/Nepriame_dane/2025/2025.12.29_007_DPH_2025_I.pdf>
- [slov-lex] Zákon č. 385/2025 Z. z.: <https://www.slov-lex.sk/ezbierky/pravne-predpisy/SK/ZZ/2025/385/>
- [PASR] Slovakia — Peppol Authority Specific Requirements (2026-01-23): <https://www.financnasprava.sk/_img/pfsedit/Dokumenty_PFS/Podnikatelia/Dan_z_pridanej_hodnoty/efaktura/2026/2026.01.23_Peppol_Authority.pdf>
- [XLSX] Transpozície štandardu Peppol BIS v podmienkach SK legislatívy v1.11 (2026-09-11): <https://www.financnasprava.sk/_img/pfsedit/Dokumenty_PFS/Podnikatelia/Dan_z_pridanej_hodnoty/efaktura/2026/2026.09.11_Peppol_Bis3_v1_11.xlsx>
- Seznam certifikovaných PDS (2026-09-29): <https://www.financnasprava.sk/_img/pfsedit/Dokumenty_PFS/Podnikatelia/Dan_z_pridanej_hodnoty/efaktura/2026/2026.09.29_zoznam.pdf>;
  v procesu akreditace: <https://www.financnasprava.sk/_img/pfsedit/Dokumenty_PFS/Podnikatelia/Dan_z_pridanej_hodnoty/efaktura/2026/2026.09.29_akreditacie.pdf>
- Harmonogram (2026-03-18): <https://www.financnasprava.sk/_img/pfsedit/Dokumenty_PFS/Podnikatelia/Dan_z_pridanej_hodnoty/efaktura/2026/2026.03.18_eFak_harmonogram.pdf>
- [TDD] SK Tax Data Document: <https://docs.peppol.eu/tdd/sk/tdd-sk/>
- [BIS] Peppol BIS Billing 3.0 — May 2026 Release (syntax, pravidla, EAS/ICD číselníky): <https://docs.peppol.eu/poacc/billing/3.0/>,
  <https://docs.peppol.eu/poacc/billing/3.0/codelist/eas/>, <https://docs.peppol.eu/poacc/billing/3.0/codelist/ICD/>
- Peppol příklady: <https://github.com/OpenPEPPOL/peppol-bis-invoice-3/tree/master/rules/examples> (release v3.0.20, 2026-03-16)
- CEN EN 16931 validační artefakty (validation-1.3.16, 2026-04-13): <https://github.com/ConnectingEurope/eInvoicing-EN16931/releases>
- Předkompilované Peppol XSLT (phive-rules, openpeppol/2026.5): <https://github.com/phax/phive-rules/tree/master/phive-rules-peppol/src/main/resources/external/schematron/openpeppol>
- SaxonHE12s9apiExtensions 12.10.1 (2026-08-10, MPL-2.0): <https://www.nuget.org/packages/SaxonHE12s9apiExtensions/>
- [SAPI] SAPI-SK 1.0 (Slovak OpenPeppol Community): <https://www.sapi-sk.sk/>, OpenAPI <https://www.sapi-sk.sk/openapi.json>
- [ePostak-API] <https://epostak.sk/api-integracia>, <https://epostak.sk/api/docs/enterprise>
- [Verteco] <https://peppol.verteco.digital/sprostredkovatel>
- Peppoš (Dativery): <https://peppos.cz/sk/slovakia/>
- [epostari] přehled cen poštářů (třetí strana, neověřeno): <https://www.epostari.sk/digitalni-postari/ceny/>
- Peppol validátor: <https://www.testbed.peppol.org/validation>
- Konkurence: `C:\GIT\ZCLOUD\FAKVIO\research\competitors.md` (SuperFaktúra, iDoklad.sk, Faktúrovo — 2026-09-21/28)
