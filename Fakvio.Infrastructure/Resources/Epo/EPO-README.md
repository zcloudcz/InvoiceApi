# EPO XML — schémata a roční update

Tato složka obsahuje XSD schémata pro formuláře EPO Finanční správy ČR.
Jsou uložena jako **copy-to-output** (ne embedded resource), aby šlo přidat nový rok
bez recompile — jen přidej XSD soubory a restartuj aplikaci.

---

## Adresářová struktura

```
Fakvio.Infrastructure/Resources/Epo/
├── EPO-README.md                ← tento soubor
└── 2026/
    ├── dphdp3_epo2.xsd          ← schéma DPHDP3 (přiznání k DPH), rok 2026
    └── dphkh1_epo2.xsd          ← schéma DPHKH1 (kontrolní hlášení), rok 2026

Fakvio.Tests.Unit/Resources/Epo/
└── 2026/
    └── Samples/
        ├── DPHDP3_sample_2026.xml   ← syntetický vzorek přiznání k DPH
        └── DPHKH1_sample_2026.xml   ← syntetický vzorek kontrolního hlášení
```

Šablona pro nový rok: `{rok}/dphdp3_epo2.xsd` a `{rok}/dphkh1_epo2.xsd`.

---

## Roční update XSD — postup

Schémata Finanční správy se obvykle mění jednou ročně (konec roku / začátek nového zdaňovacího období).

### 1. Kde stáhnout

**Oficiální zdroj — Finanční správa ČR (adisspr.mfcr.cz):**

```
https://adisspr.mfcr.cz/adistc/adis/idpr_pub/epo2_info/popis_struktury.faces
```

Na stránce vyhledej sekci odpovídající novému roku a stáhni soubory:
- `epo2_dphdp3_*.xsd` → uložit jako `Fakvio.Infrastructure/Resources/Epo/{rok}/dphdp3_epo2.xsd`
- `epo2_dphkh1_*.xsd` → uložit jako `Fakvio.Infrastructure/Resources/Epo/{rok}/dphkh1_epo2.xsd`

Alternativní cesta (přímý FTP/HTTP ke zdrojům):

```
https://www.financnisprava.cz/assets/cs/prilohy/d-EPO/
```

Hledej soubory s názvem `epo2_dphdp3_YYYY*.xsd` (kde `YYYY` je nový rok).

### 2. Jak verifikovat hash / datum modifikace

Po stažení ověř, zda se soubor skutečně změnil:

```powershell
# Výpočet SHA-256 nového souboru:
Get-FileHash .\dphdp3_epo2.xsd -Algorithm SHA256

# Porovnej s předchozí verzí (pokud ji máš) nebo s verzí z webu Finanční správy
# (web někdy zobrazuje hash u ke stažení).
```

Zkontroluj datum `Last-Modified` v HTTP hlavičkách při stahování:

```powershell
Invoke-WebRequest -Uri "https://adisspr.mfcr.cz/.../epo2_dphdp3_2027.xsd" `
  -Method Head | Select-Object -ExpandProperty Headers
```

Pokud se hash ani datum nezměnily, schéma pro nový rok je stejné jako loňské
a starý soubor stačí zkopírovat.

### 3. Kam soubor uložit

```
Fakvio.Infrastructure/
└── Resources/
    └── Epo/
        └── {rok}/
            ├── dphdp3_epo2.xsd
            └── dphkh1_epo2.xsd
```

Soubory jsou deklarovány jako `CopyToOutputDirectory` v `Fakvio.Infrastructure.csproj`
(přidáno PR#75, issue #35) jako `<None Include="Resources\Epo\**\*.xsd">`.
Wildcard pokrývá všechny roky automaticky — žádná změna v csproj při přidání nového roku.

### 4. Jak rozšířit EpoSchemaProvider o nový rok

`EpoSchemaProvider` (implementace `IEpoSchemaProvider` z issue #35) načítá XSD
ze souborového systému relativně k `AppContext.BaseDirectory`. Rozpoznané roky
jsou určeny **přítomností souboru** — žádný hardcoded seznam let.

Pokud přidáš soubory pro rok `2027`, provider je automaticky najde bez změny kódu,
protože cesta je sestavena dynamicky:

```csharp
// EpoSchemaProvider.cs — zjednodušená ukázka:
var xsdPath = Path.Combine(
    AppContext.BaseDirectory,
    "Resources", "Epo", year.ToString(), fileName);
// fileName = "dphdp3_epo2.xsd" nebo "dphkh1_epo2.xsd"
```

Pokud by rok byl hardcoded (starší verze provideru), přidej ho do switch/mapy
v `EpoSchemaProvider.cs` — viz komentáře přímo v souboru.

---

## Troubleshooting — typické chyby validace

| Chyba | Příčina | Řešení |
|-------|---------|---------|
| `The element 'Veta1' has invalid child element 'r_XX'` | Nové schéma přidalo nebo odebralo atribut daňového řádku. | Porovnej atributy `Veta1` v novém XSD s generátorem v `EpoVatReturnMapper`. |
| `The required attribute 'dic' is missing` | DIČ chybí nebo je prázdné. | Ověř, zda je `Client.RegistrationNumber` vyplněno pro všechny strany dokladu. |
| `The required attribute 'dapdph_forma' is missing` | Chybí povinný atribut záhlaví přiznání na `VetaD`. | Ověř, že generátor nastavuje `dapdph_forma` na `VetaD`. |
| `The element 'DPHDP3' has invalid attribute 'typ_platce'` | Nové schéma omezilo nebo rozšířilo enum hodnot. | Zkontroluj hodnoty `typ_platce` v novém XSD (atribut SimpleType). |
| XSD soubor nenalezen (`FileNotFoundException`) | `CopyToOutputDirectory` nefunguje nebo cesta nesedí. | Spusť `dotnet build` a zkontroluj, zda jsou soubory v `bin/Debug/net10.0/Resources/Epo/{rok}/`. |
| Schema compilation error při startu | XSD má interní chybu (stažen poškozený soubor). | Znovu stáhni XSD a ověř jeho celistvost SHA-256 hashem (viz bod 2 výše). |

---

## Testing — jak ověřit nový rok

### Validace sample fixtures

Po přidání nového XSD spusť unit test `EpoFixtureValidationTests` v `Fakvio.Tests.Unit`:

```bash
dotnet test Fakvio.Tests.Unit --filter "FullyQualifiedName~EpoFixtureValidationTests"
```

Testy ověřují, že sample XML soubory v `Resources/Epo/{rok}/Samples/` projdou validací
proti příslušnému XSD schématu. Pokud test selže po update XSD, sample fixture musí být
aktualizován, aby odpovídal novému schématu.

### Vytvoření sample fixture pro nový rok

Pokud přidáváš rok `{YYYY}`, přidej také:
- `Fakvio.Tests.Unit/Resources/Epo/{YYYY}/Samples/DPHDP3_sample_{YYYY}.xml`
- `Fakvio.Tests.Unit/Resources/Epo/{YYYY}/Samples/DPHKH1_sample_{YYYY}.xml`

Vzor viz soubory pro rok 2026 ve stejné struktuře.

Názvy XSD souborů musí odpovídat konvenci `dphdp3_epo2.xsd` / `dphkh1_epo2.xsd`
(přípona `_epo2` odpovídá verzi EPO2 z portálu Finanční správy a je rozeznána
`EpoSchemaProvider` v `Fakvio.Infrastructure`).

### Smoke testy end-to-end (EpoSandboxSmokeTests — issue #41)

Po přidání nového roku spusť i smoke testy, které volají generátor XML end-to-end:

```bash
dotnet test Fakvio.Tests.Unit --filter "FullyQualifiedName~EpoSandboxSmokeTests"
```

Smoke testy generují XML z testovacích dat a validují výstup XSD schématem.
Jsou nezávislé na fixtures — slouží k ověření, že generátor produkuje validní XML
pro nový rok, nikoliv jen že sample fixture je správně sestavený.

---

## Reference

- [EPO portál Finanční správy](https://adisspr.mfcr.cz/adistc/adis/idpr_pub/epo2_info/popis_struktury.faces)
- [ZDPH — zákon o DPH č. 235/2004 Sb.](https://www.zakonyprolidi.cz/cs/2004-235) — definuje sekce KH (§92a apod.)
- `IEpoSchemaProvider` — `Fakvio.Application/Service/IEpoSchemaProvider.cs` (issue #35)
- `EpoSchemaProvider` — `Fakvio.Infrastructure/Service/Epo/EpoSchemaProvider.cs` (issue #35)
- `EpoVatReturnMapper` — generátor DPHDP3 XML (issue #36)
- `EpoControlStatementMapper` — generátor DPHKH1 XML (issue #37)
