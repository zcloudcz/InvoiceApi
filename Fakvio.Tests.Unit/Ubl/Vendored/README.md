# UBL / Peppol BIS validation artifacts (ADR 0002, F1.2)

Vendored, read-only reference artifacts used only by `Fakvio.Tests.Unit` to validate the UBL
2.1 / Peppol BIS Billing 3.0 XML the UBL export (F1.3+) produces. **Not shipped in any
production project** — `dotnet list package` for `Fakvio.Infrastructure`/`Fakvio.API` is
unaffected; only `Fakvio.Tests.Unit.csproj` references them (as content, copied to the test
output directory) and the `SaxonHE12s9apiExtensions` NuGet package that runs the XSLT 2.0
schematron stylesheets.

All files retrieved 2026-09-29 (or the versioned date behind each link below) and must not be
edited by hand — replace wholesale when a new Peppol/EN16931 release requires re-verifying
fixtures against `UblMapperTests`.

## `maindoc/` + `common/` — UBL 2.1 XSD (OASIS)

Minimal import closure needed to validate `Invoice-2` and `CreditNote-2` documents: the two
maindoc schemas plus every `common/` schema they transitively import (signature/XAdES schemas
included — `UBL-CommonExtensionComponents` imports them even though Fakvio never populates a
digital signature extension).

- Source: OASIS UBL 2.1 (Committee Specification, `os-UBL-2.1`), full package
  <https://docs.oasis-open.org/ubl/os-UBL-2.1/UBL-2.1.zip> (only `xsd/maindoc/UBL-Invoice-2.1.xsd`,
  `xsd/maindoc/UBL-CreditNote-2.1.xsd` and all of `xsd/common/` except the unused
  `UBL-CoreComponentParameters-2.1.xsd` are kept here).
- License: OASIS specification — schemas may be copied and used in implementations under the
  OASIS IPR policy (royalty-free, unmodified use permitted); see
  <https://www.oasis-open.org/policies-guidelines/ipr/> and the notice inside each `.xsd` file.
- Retrieved: 2026-09-29.

## `EN16931-UBL-validation.xslt` — CEN EN 16931 schematron (precompiled)

- Source: ConnectingEurope `eInvoicing-EN16931`, release `validation-1.3.16` (2026-04-13),
  asset `en16931-ubl-1.3.16.zip`, file `xslt/EN16931-UBL-validation.xslt`:
  <https://github.com/ConnectingEurope/eInvoicing-EN16931/releases/tag/validation-1.3.16>
- License: EUPL-1.2 (European Union Public Licence) — repository license.
- Retrieved: 2026-09-29.

## `PEPPOL-EN16931-UBL.xslt` — Peppol BIS Billing 3.0 schematron (precompiled)

- Source: `phax/phive-rules`, `phive-rules-peppol/src/main/resources/external/schematron/openpeppol/2026.5/xslt/PEPPOL-EN16931-UBL.xslt`
  (Peppol BIS May 2026 release, `openpeppol/2026.5`):
  <https://github.com/phax/phive-rules/tree/master/phive-rules-peppol/src/main/resources/external/schematron/openpeppol/2026.5>
- License: MPL-2.0 (Mozilla Public License) — repository license.
- Retrieved: 2026-09-29.

## Not vendored here

`../Fixtures/` (sibling folder) holds the OpenPEPPOL example XML files
(`base-example.xml`, `base-creditnote-correction.xml`, `vat-category-O.xml`) used as
known-good fixtures by `UblValidatorSmokeTests` and `UblMapperTests` — see that folder's own
provenance note in `UblValidatorSmokeTests.cs`.

## Fallback if Saxon/IKVM does not build

Per ADR 0002 §4.1.3: if `SaxonHE12s9apiExtensions` (IKVM — downloads the Saxon JAR from Maven
Central on first restore) cannot be built in an environment, mark the schematron tests
`[Trait("Category","Schematron")]`, exclude them from the default run
(`dotnet test --filter "Category!=Schematron"`), validate the fixtures manually on
<https://www.testbed.peppol.org/validation> instead, and record the result in the PR. XSD
validation (`System.Xml.Schema`, no IKVM) is unaffected either way.
