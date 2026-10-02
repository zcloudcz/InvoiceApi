# Vendored accounting-system schemas (test-only)

Used by `AccountingExporterTests` to validate exporter output against the vendors' own definitions.
Not shipped with the application.

- `Pohoda/` - Stormware POHODA XML schemas, version 2 (`data.xsd`, `invoice.xsd`, `type.xsd` and their imports),
  downloaded 2026-10-02 from https://www.stormware.cz/schema/version_2/ (publicly published for integrators).
- `MoneyS3/` - Money S3 XML transfer schemas (`_Document.xsd`, `__Faktura.xsd`, ...), downloaded 2026-10-02 from
  https://money.cz/wp-content/uploads/2024/10/schemas.zip (linked from https://money.cz/?p=1155, "XML prenosy").
- `AbraFlexi/catalog.txt` - snapshot of the writable fields of the ABRA Flexi evidences used by the exporter,
  taken from the public demo catalogue https://demo.flexibee.eu/c/demo/<evidence>/properties (Flexi publishes no XSD).
