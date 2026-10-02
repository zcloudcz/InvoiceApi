# EPO XML — schémata, roční update a sandbox testování

Tato složka obsahuje XSD schémata pro formuláře EPO Finanční správy ČR.
Jsou uložena jako **copy-to-output** (ne embedded resource), aby šlo přidat nový rok
bez recompile — jen přidej XSD soubory a restartuj aplikaci.

---

## Adresářová struktura

```
Fakvio.Infrastructure/Resources/Epo/
├── EPO-README.md          ← tento soubor
└── 2026/
    ├── dphdp3_epo2.xsd    ← schéma DPHDP3 (přiznání k DPH), rok 2026
    ├── dphkh1_epo2.xsd    ← schéma DPHKH1 (kontrolní hlášení), rok 2026
    └── dphshv_epo2.xsd    ← schéma DPHSHV (souhrnné hlášení), rok 2026
                             (stahuje se z https://adisspr.mfcr.cz/adis/jepo/schema/dphshv_epo2.xsd)

Fakvio.Tests.Unit/Resources/Epo/
└── 2026/
    └── Samples/
        ├── DPHDP3_sample_2026.xml   ← syntetický vzorek přiznání k DPH
        └── DPHKH1_sample_2026.xml   ← syntetický vzorek kontrolního hlášení
```

Šablona pro nový rok: `{rok}/dphdp3_epo2.xsd`, `{rok}/dphkh1_epo2.xsd` a `{rok}/dphshv_epo2.xsd`.

---

## Roční update XSD — postup

Schémata Finanční správy se obvykle mění jednou ročně (konec roku / začátek nového zdaňovacího období).

### 1. Kde stáhnout

**Oficiální zdroj — Finanční správa ČR:**

```
https://adisspr.mfcr.cz/adistc/adis/idpr_pub/epo2_info/popis_struktury.faces
```

Na stránce vyhledej sekci odpovídající novému roku a stáhni soubory:
- `epo2_dphdp3_*.xsd` → uložit jako `Fakvio.Infrastructure/Resources/Epo/{rok}/dphdp3_epo2.xsd`
- `epo2_dphkh1_*.xsd` → uložit jako `Fakvio.Infrastructure/Resources/Epo/{rok}/dphkh1_epo2.xsd`

### 2. Jak verifikovat hash / datum modifikace

```powershell
# Výpočet SHA-256 nového souboru:
Get-FileHash .\dphdp3_epo2.xsd -Algorithm SHA256
```

Pokud se hash ani datum nezměnily, schéma pro nový rok je stejné jako loňské.

### 3. Kam soubor uložit

```
Fakvio.Infrastructure/Resources/Epo/{rok}/dphdp3_epo2.xsd
Fakvio.Infrastructure/Resources/Epo/{rok}/dphkh1_epo2.xsd
```

Soubory jsou deklarovány jako `CopyToOutputDirectory` v `Fakvio.Infrastructure.csproj`:

```xml
<ItemGroup>
  <None Update="Resources\Epo\**\*.xsd">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

---

## Troubleshooting — typické chyby validace

| Chyba | Příčina | Řešení |
|-------|---------|---------|
| XSD soubor nenalezen (`FileNotFoundException`) | `CopyToOutputDirectory` nefunguje. | Spusť `dotnet build` a zkontroluj, zda jsou soubory v `bin/Debug/net10.0/Resources/Epo/{rok}/`. |
| `The element 'DPHDP3' has invalid attribute 'typ_platce'` | Nové schéma omezilo enum hodnot. | Zkontroluj hodnoty `typ_platce` v novém XSD. |

---

## EPO sandbox smoke test (issue #41)

### Co to je

Integrační test `EpoSandboxSmokeTests` v `Fakvio.Tests.Integration`, který:

1. Vygeneruje DPHDP3 a DPHKH1 XML pro syntetickou firmu CZ12345678.
2. Odešle je POST na EPO sandbox: `https://adisepo.mfcr.cz/adis/jepo/epo/ePodani/podani.faces`
3. Ověří HTTP 200 a absenci `<chyba>` / `<error>` elementů v odpovědi.

Test je označen `[SkippableFact]` — přeskočí se automaticky pokud:
- Env proměnná `RUN_EPO_SANDBOX_TESTS` není nastavena na `true`.
- Sandbox vrátí 4xx/5xx (nedostupný).
- Požadavek vyprší timeout (30 s).

### Jak spustit lokálně

```powershell
# Windows PowerShell:
$env:RUN_EPO_SANDBOX_TESTS = "true"
dotnet test Fakvio.Tests.Integration --filter "FullyQualifiedName~EpoSandboxSmokeTests" -v normal
```

```bash
# Linux / macOS / Git Bash:
RUN_EPO_SANDBOX_TESTS=true dotnet test Fakvio.Tests.Integration \
  --filter "FullyQualifiedName~EpoSandboxSmokeTests" -v normal
```

### Očekávaný výstup — přijato sandboxem

Sandbox by měl vrátit HTTP 200 s XML odpovědí bez `<chyba>` elementů.
Příklad přijaté odpovědi (formát se může lišit dle verze portálu):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Odpoved>
  <OK/>
</Odpoved>
```

nebo jednoduše prázdné tělo s HTTP 200.

### Očekávaný výstup — chyba validace

Pokud sandbox odmítne dokument, vrátí XML s `<chyba>` elementem:

```xml
<Odpoved>
  <chyba kod="100">Neplatný formát XML souboru</chyba>
</Odpoved>
```

V tomto případě test selže (nikoliv se přeskočí) s výpisem chybových elementů.

### CI konfigurace

V CI pipeline test poběží jen pokud je nastavena env proměnná:

```yaml
# GitHub Actions — přidej do step environment:
env:
  RUN_EPO_SANDBOX_TESTS: "true"
```

Doporučení: spouštět jen v dedicated integration workflow, ne v každém PR.

---

## Reference

- [EPO portál Finanční správy](https://adisspr.mfcr.cz/adistc/adis/idpr_pub/epo2_info/popis_struktury.faces)
- [EPO sandbox endpoint](https://adisepo.mfcr.cz/adis/jepo/epo/ePodani/podani.faces)
- `IEpoSchemaProvider` — `Fakvio.Application/Service/IEpoSchemaProvider.cs`
- `EpoSchemaProvider` — `Fakvio.Infrastructure/Service/Epo/EpoSchemaProvider.cs`
- `VatReportService` — generátor DPHDP3 a DPHKH1 XML
- Smoke testy — `Fakvio.Tests.Integration/EpoSandboxSmokeTests.cs`
