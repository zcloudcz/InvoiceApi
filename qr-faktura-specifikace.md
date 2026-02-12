# QR Faktura – Specifikace pro implementaci

## 1. Úvod

**QR Faktura** je český standard vytvořený Komorou daňových poradců ČR (představen 14. 6. 2016), jehož cílem je automatizovaný přenos hlavičkových účetních dat z faktury do účetních systémů prostřednictvím QR kódu. Standard je zdarma, bez registrace a licence.

QR Faktura používá formát **Short Invoice Descriptor (SIND)** – kompaktní textový řetězec, který se zakóduje do QR kódu. Standard je plně integrován s **QR Platbou** (SPD – Short Payment Descriptor), takže na faktuře je jen **jeden QR kód** obsahující jak platební, tak účetní údaje.

### Klíčové zdroje

| Zdroj | URL |
|-------|-----|
| Oficiální stránka KDP ČR | https://www.kdpcr.cz/informace/qr-faktura |
| Popis formátu | https://www.kdpcr.cz/informace/qr-faktura/popis-formatu |
| Integrace s QR Platbou | https://www.kdpcr.cz/informace/qr-faktura/integrace-s-qr-platbou |
| RESTful API (referenční implementace) | https://api.qrfgen.cz/generator/ |
| PDF specifikace ke stažení | https://www.kdpcr.cz/informace/qr-faktura?sp=down-file&did=20206 |
| C# parser (SpaydParserLib) | https://github.com/msimecek/SpaydParserLib |
| QR Platba specifikace | https://qr-platba.cz/pro-vyvojare/specifikace-formatu/ |

---

## 2. Formát řetězce SIND

### 2.1 Struktura

Řetězec vždy začíná fixní hlavičkou:

```
SID*1.0*{klíč}:{hodnota}*{klíč}:{hodnota}*...
```

- Hlavička: `SID*`
- Verze: `1.0*`
- Atributy: `{klíč}:{hodnota}*` (klíč oddělen od hodnoty dvojtečkou, hodnota zakončena hvězdičkou)
- Poslední hodnota nemusí být zakončena hvězdičkou

### 2.2 Znaková sada

Pro **maximální efektivitu** QR kódu (alfanumerický mód) se doporučuje používat pouze:

```
0-9, A-Z (pouze velká písmena), mezera, $ % * + - . / :
```

Pokud hodnota obsahuje znak mimo tuto množinu, QR kód přepne do **binárního formátu** (větší velikost).

### 2.3 Pravidla pro klíče a hodnoty

**Klíče:**
- Velká písmena z množiny `[A-Z-]`
- Proprietární klíče začínají prefixem `X-`
- Každý klíč musí mít hodnotu (klíč bez hodnoty není přípustný)

**Hodnoty:**
- Nesmí být obklopeny bílými znaky
- Nesmí obsahovat znak `*` (hvězdička) – kóduje se jako `%2A`
- Mohou obsahovat znak `:` (dvojtečka)
- Speciální znaky se kódují URL kódováním

### 2.4 Ukládání a MIME type

- **MIME type:** `application/x-shortinvoicedescriptor`
- **Přípona souboru:** `.sind`

---

## 3. Atributy – Tabulka 1 (Základní)

| Klíč | Povinný | Max. délka | Formát | Popis | Příklad |
|------|---------|------------|--------|-------|---------|
| **ID** | ANO | 40 | Alfanumerický (bez `*`) | Jednoznačné označení dokladu | `ID:ABCD123456789EF*` |
| **DD** | ANO | 8 | `YYYYMMDD` (ISO 8601) | Datum vystavení dokladu | `DD:20160615*` |
| **AM** | ANO | 18 | Desetinné číslo (max 2 des. místa, `.` oddělovač) | Celková částka k úhradě | `AM:123456789.45*` |
| TP | NE | 1 | Číslice | Typ daňového plnění: `0`=běžný, `1`=RPDP, `2`=smíšený. Výchozí: `0` | `TP:0*` |
| TD | NE | 1 | Číslice | Typ dokladu: `0`=nedaňový (záloha), `1`=opravný, `2`=k přijaté platbě, `3`=splátkový kalendář, `4`=platební kalendář, `5`=souhrnný, `9`=ostatní. Výchozí: `9` | `TD:9*` |
| SA | NE | 1 | Číslice | Faktura obsahuje zúčtování záloh: `0`=ne, `1`=ano. Výchozí: `0` | `SA:1*` |
| MSG | NE | 40 | Alfanumerický (bez `*`) | Textový popis předmětu fakturace | `MSG:KONZULTACE KVETEN 2016*` |
| ON | NE | 20 | Alfanumerický (bez `*`) | Číslo objednávky | `ON:OBJ20160614TK*` |
| VS | NE | 10 | Celé číslo | Variabilní symbol | `VS:1234567890*` |
| VII | NE | 14 | Alfanumerický | DIČ výstavce | `VII:CZ12345678*` |
| INI | NE | 8 | Celé číslo | IČO výstavce | `INI:12345678*` |
| VIR | NE | 14 | Alfanumerický | DIČ příjemce | `VIR:CZ09876543*` |
| INR | NE | 8 | Celé číslo | IČO příjemce | `INR:98765432*` |
| DUZP | NE | 8 | `YYYYMMDD` | Datum uskutečnění zdanitelného plnění | `DUZP:20160413*` |
| DPPD | NE | 8 | `YYYYMMDD` | Datum povinnosti přiznat daň | `DPPD:20161201*` |
| DT | NE | 8 | `YYYYMMDD` | Datum splatnosti | `DT:20160908*` |
| TB0 | NE | 18 | Desetinné číslo | Základ daně v **základní sazbě** (CZK) | `TB0:3000*` |
| T0 | NE | 18 | Desetinné číslo | Daň v **základní sazbě** (CZK) | `T0:630*` |
| TB1 | NE | 18 | Desetinné číslo | Základ daně v **1. snížené sazbě** (CZK) | `TB1:2000*` |
| T1 | NE | 18 | Desetinné číslo | Daň v **1. snížené sazbě** (CZK) | `T1:300*` |
| TB2 | NE | 18 | Desetinné číslo | Základ daně ve **2. snížené sazbě** (CZK) | `TB2:1000*` |
| T2 | NE | 18 | Desetinné číslo | Daň ve **2. snížené sazbě** (CZK) | `T2:100*` |
| NTB | NE | 18 | Desetinné číslo | Osvobozená plnění / mimo předmět DPH / neplátci (CZK) | `NTB:500*` |
| CC | NE | 3 | ISO 4217 (velká písmena) | Měna celkové částky. Výchozí: `CZK` | `CC:CZK*` |
| FX | NE | 18 | Desetinné číslo (max 3 des. místa) | Směnný kurz CZK ↔ měna | `FX:123456.789*` |
| FXA | NE | 5 | Celé číslo | Počet jednotek cizí měny pro přepočet. Výchozí: `1` | `FXA:100*` |
| ACC | NE | 46 | IBAN[+BIC] | Číslo účtu výstavce. Formát: `IBAN+SWIFT` (SWIFT volitelný) | `ACC:CZ5855000000001265098001+RZBCCZPP*` |
| CRC32 | NE | 8 | Hex `[A-F0-9]` | Kontrolní součet – viz sekce 4 | `CRC32:1234ABCD*` |

## 4. Atributy – Tabulka 2 (Proprietární)

| Klíč | Povinný | Max. délka | Formát | Popis | Příklad |
|------|---------|------------|--------|-------|---------|
| X-SW | NE | 30 | Alfanumerický (bez `*`) | Označení účetního SW, ve kterém byla faktura vytvořena | `X-SW:E-FAKTURANT V5.3*` |
| X-URL | NE | 70 | Alfanumerický (bez `*`) | URL pro získání faktury ve strukturovaném formátu | `X-URL:HTTP://E-FAKTURANT.CZ/INV/` |

---

## 5. Výpočet CRC32

Postup pro výpočet kontrolního součtu:

1. Použít všechny atributy **kromě** CRC32
2. Sestavit **kanonický řetězec**: hlavička `SID*1.0*`, poté atributy seřazené **abecedně dle klíče**, každý zakončený hvězdičkou
3. Spočítat CRC32 tohoto řetězce
4. Převést výsledek do **hexadecimálního** zápisu (8 znaků, velká písmena A-F)
5. Připojit `CRC32:{hex}*` k řetězci

---

## 6. Integrace s QR Platbou (SPD)

Klíčový princip: **jeden QR kód** na faktuře slouží pro platbu i pro účetnictví.

### 6.1 Postup integrace

1. **Hodnoty nesmí obsahovat `*`** – v řetězci QR Faktury se hvězdičky v hodnotách URL-kódují jako `%2A`
2. **Shodné klíče** v obou formátech (QR Faktura i QR Platba) se **vyjmou z QR Faktury** a vloží do řetězce QR Platby
   - Speciální případ: klíč `VS` z QR Faktury se změní na `X-VS` v QR Platbě
3. **Zbytek řetězce QR Faktury** se URL-kóduje (všechny `*` → `%2A`) a vloží jako hodnota klíče **`X-INV`** v řetězci QR Platby
4. Klíč `MSG` se může vyskytovat v obou řetězcích nezávisle

### 6.2 Validace výsledného řetězce

Výsledný řetězec QR Platby musí být validní:
- Musí obsahovat validní klíč `ACC` (číslo účtu) – povinný
- Celková částka (`AM`) musí být **kladná** a **max. 10 znaků**

### 6.3 Označení QR kódu

| Varianta | Označení na faktuře |
|----------|-------------------|
| QR Platba s integrovanou QR Fakturou | **QR Platba+F** |
| Pouze QR Faktura (bez platby) | **QR Faktura** |

### 6.4 Příklad integrace

**Původní řetězec QR Faktury:**
```
SID*1.0*ID:1963/160/2015*DD:20161201*TP:0*AM:9535.00*VS:1234567890*
VII:CZ60194383*VIR:CZ12345678*INI:60194383*DUZP:20161201*DT:20161217*
TB0:1000.00*T0:210.00*TB1:6500.00*T1:975.00*NTB:850.00*CC:CZK*
ACC:CZ3103000000270016060243*
```

**Shodné klíče s QR Platbou** (vyjmuty a přesunuty): `ACC`, `AM`, `CC`, `DT`  
**VS** se změní na `X-VS`

**Výsledný řetězec QR Platby:**
```
SPD*1.0*ACC:CZ3103000000270016060243*AM:9535.00*CC:CZK*DT:20161217*
X-VS:1234567890*X-INV:SID%2A1.0%2AID:1963/160/2015%2ADD:20161201%2A
TP:0%2AVII:CZ60194383%2AVIR:CZ12345678%2AINI:60194383%2ADUZP:20161201%2A
TB0:1000.00%2AT0:210.00%2ATB1:6500.00%2AT1:975.00%2ANTB:850.00%2A
```

---

## 7. RESTful API (referenční implementace)

Veřejné API pro generování QR kódů je dostupné na: `https://api.qrfgen.cz/generator/`

### Dostupné endpointy

- **Generování řetězce SIND** – vrátí textový řetězec
- **Generování souboru SIND** – vrátí soubor `.sind`
- **Generování QR kódu** – vrátí obrázek QR kódu

### Příklad volání (generování QR kódu pro fakturu)

```
https://api.qrfgen.cz/generator/image?
  ID=12345&
  CC=CZK&
  DD=20170203&
  DT=20170304&
  AM=121.00&
  TP=0&
  TD=9&
  SA=0&
  MSG=testovací+faktura&
  ON=obj1&
  VS=123456789&
  TB0=100.00&
  T0=21.00&
  DPPD=20170101&
  ACC=CZ5855000000001265098001+RZBCCZPP&
  VIR=CZ7708052000&
  INI=69274533&
  qrplatba=1
```

Parametr `qrplatba=1` aktivuje integraci s QR Platbou.

---

## 8. Implementační doporučení pro C# / .NET

### 8.1 Existující knihovna

Na GitHubu existuje C# knihovna **SpaydParserLib** pro parsování řetězců SPAYD (QR Platba) i SIND (QR Faktura):  
https://github.com/msimecek/SpaydParserLib

### 8.2 Doporučená architektura

```
InvoiceSystem/
├── Domain/
│   ├── QrFaktura/
│   │   ├── SindDocument.cs          // SIND řetězec – model
│   │   ├── SindAttribute.cs         // Klíč-hodnota pár
│   │   ├── InvoiceType.cs           // Enum pro TD (typ dokladu)
│   │   └── TaxationType.cs          // Enum pro TP (typ plnění)
│   └── QrPlatba/
│       └── SpdDocument.cs           // SPD řetězec – model
├── Application/
│   ├── QrFakturaGenerator.cs        // Generování SIND řetězce z faktury
│   ├── QrFakturaParser.cs           // Parsování SIND řetězce na objekt
│   ├── QrPlatbaIntegrator.cs        // Integrace SIND do SPD
│   └── Crc32Calculator.cs           // Výpočet CRC32
└── Infrastructure/
    └── QrCodeRenderer.cs            // Generování QR kódu (knihovna: QRCoder apod.)
```

### 8.3 Klíčové body pro implementaci

1. **Validace povinných polí**: `ID`, `DD`, `AM` musí být vždy přítomny
2. **Formát datumů**: Striktně `YYYYMMDD` (8 číslic, bez oddělovačů)
3. **Formát částek**: Tečka jako oddělovač desetinných míst, max. 2 desetinná místa (u FX max. 3)
4. **URL kódování**: Při integraci s QR Platbou nahradit `*` za `%2A`
5. **Kanonický řetězec pro CRC32**: Abecední řazení klíčů, každá hodnota zakončená `*`
6. **Znakové limity**: Dodržovat maximální délky dle specifikace
7. **QR kód**: Preferovat alfanumerický mód (pouze povolené znaky) pro menší velikost

---

## 9. Podporovaný software

Standard QR Faktura je podporován více než 30 účetními programy na českém trhu, mimo jiné: Pohoda, Money S3, iDoklad, Helios, KARAT, Fakturoid, SuperFaktura, Byznys ERP, Altus Vario, Premier systém, KOSYS a další.
