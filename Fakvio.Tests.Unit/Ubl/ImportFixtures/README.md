# UBL / Peppol BIS Billing 3.0 sample fixtures (F1.10 — import)

Used by `UblImportParserTests` to verify `UblImportParser` against real-world
Peppol documents rather than only hand-crafted XML.

## Files

| File | Root | Source |
|---|---|---|
| `base-example.xml` | `Invoice` (380) | OpenPEPPOL official example, `rules/examples/base-example.xml` |
| `base-creditnote-correction.xml` | `CreditNote` (381) | OpenPEPPOL official example, `rules/examples/base-creditnote-correction.xml` |

## Origin

Fetched unmodified from the public GitHub repository
<https://github.com/OpenPEPPOL/peppol-bis-invoice-3>, path `rules/examples/`,
commit `261c458474e27d58a25be629cccac28883171c92` (`master` branch,
fetched 2026-09-29). This is the same repository referenced as the example
source in `docs/adr/0002-sk-einvoicing-peppol.md` §9 ("Peppol příklady").

## License

The `peppol-bis-invoice-3` repository does not carry an OSI license file or a
`LICENSE`/`LICENSE.md` at its root, and GitHub reports no detected license.
These example XML files are published by OpenPeppol AISBL as part of the
public Peppol BIS Billing 3.0 specification, explicitly for implementers to
test their software against — that is exactly how they are used here (test
fixtures only, not shipped in the product, not modified). If OpenPeppol's
licensing terms change or a redistribution concern is raised, replace these
two files with equivalent invoices synthesized by Fakvio itself (the shape is
simple enough to hand-write) — the tests don't depend on OpenPeppol-authored
values beyond what's asserted inline in `UblImportParserTests`.

## Note for F1.1/F1.2 unification (post-merge)

The parallel UBL **export** work (F1.1/F1.2, different worktree) vendors its
own XSD/schematron validation artifacts under
`Fakvio.Tests.Unit/Ubl/Artifacts/`. These import fixtures are a separate,
smaller set (sample *documents*, not *validators*) kept under
`Fakvio.Tests.Unit/Ubl/ImportFixtures/` to avoid any merge collision. After
both branches land on `develop`, consider whether the two Peppol example sets
should be consolidated into one `Ubl/` fixtures folder — not done here to
keep this task's diff isolated per the task brief.
