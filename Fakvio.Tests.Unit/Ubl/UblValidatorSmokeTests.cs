using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Fakvio.Tests.Unit.Ubl;

/// <summary>
/// Smoke tests for <see cref="UblTestValidator"/> itself (ADR 0002, F1.2) — proves the harness
/// can tell a valid Peppol invoice from a broken one *before* <c>UblMapper</c> (F1.3+) exists,
/// so a later mapper bug is caught by a validator that is already known to work.
///
/// Fixtures in <c>Ubl/Fixtures/</c> are the official OpenPEPPOL examples
/// (<see href="https://github.com/OpenPEPPOL/peppol-bis-invoice-3/tree/v3.0.20/rules/examples"/>,
/// release v3.0.20, retrieved 2026-09-29) — not written by Fakvio, so a failure here points at
/// the harness, not at fixture data.
/// </summary>
public class UblValidatorSmokeTests
{
    private static readonly string FixturesRoot =
        Path.Combine(AppContext.BaseDirectory, "Ubl", "Fixtures");

    private static XDocument LoadFixture(string fileName)
        => XDocument.Load(Path.Combine(FixturesRoot, fileName));

    [Fact]
    public void BaseExample_PassesXsdValidation()
    {
        var document = LoadFixture("base-example.xml");

        var errors = UblTestValidator.ValidateXsd(document);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void BaseExample_PassesSchematronValidation()
    {
        var document = LoadFixture("base-example.xml");

        var errors = UblTestValidator.ValidateSchematron(document);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void CreditNoteExample_PassesBothValidations()
    {
        var document = LoadFixture("base-creditnote-correction.xml");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    [Fact]
    public void NonVatPayerExample_PassesBothValidations()
    {
        // vat-category-O.xml — the official fixture for the "no VAT charged" case (VATEX-EU-O),
        // the same category Fakvio emits for a non-VAT-payer issuer (F1.4).
        var document = LoadFixture("vat-category-O.xml");

        UblTestValidator.ValidateXsd(document).ShouldBeEmpty();
        UblTestValidator.ValidateSchematron(document).ShouldBeEmpty();
    }

    [Fact]
    public void MissingEndpointId_FailsSchematronWithPeppolRule020()
    {
        // Deliberately break a known-good document: PEPPOL-EN16931-R020 requires the seller's
        // EndpointID (BT-34) — remove it and confirm the harness actually reports a fatal error
        // instead of silently passing everything.
        var document = LoadFixture("base-example.xml");
        var cac = XNamespace.Get("urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2");
        var endpointId = document.Root!
            .Element(cac + "AccountingSupplierParty")!
            .Element(cac + "Party")!
            .Element(XNamespace.Get("urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2") + "EndpointID");
        endpointId!.Remove();

        var errors = UblTestValidator.ValidateSchematron(document);

        errors.ShouldContain(e => e.Contains("PEPPOL-EN16931-R020"));
    }

    [Fact]
    public void MalformedInvoice_FailsXsdValidation()
    {
        // An empty Invoice root is schema-invalid (ID, IssueDate, etc. are all required) —
        // confirms ValidateXsd actually enforces the schema instead of trivially passing.
        var ns = XNamespace.Get("urn:oasis:names:specification:ubl:schema:xsd:Invoice-2");
        var document = new XDocument(new XElement(ns + "Invoice"));

        var errors = UblTestValidator.ValidateXsd(document);

        errors.ShouldNotBeEmpty();
    }
}
