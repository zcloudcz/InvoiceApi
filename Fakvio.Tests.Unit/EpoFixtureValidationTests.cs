using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Validates EPO sample fixtures stored in Resources/Epo/2026/Samples/.
///
/// Two levels of checks:
/// 1. Well-formed XML — always runs; ensures the fixture can be parsed.
/// 2. XSD schema validation — runs only when the XSD files from issue #35
///    (EpoSchemaProvider) are present in the build output. Until #35 is merged
///    the XSD files are not deployed, so XSD-level tests skip gracefully with
///    a clear message rather than failing.
///
/// After issue #35 merges and the XSD files land in
/// Resources/Epo/2026/dphdp3.xsd / dphkh1.xsd, the TODO block below
/// can be uncommented to get full schema validation.
/// </summary>
public class EpoFixtureValidationTests
{
    // =========================================================================
    // Paths — resolved relative to the test assembly's output directory
    // =========================================================================

    /// <summary>
    /// Base directory for XSD files: bin/…/Resources/Epo/{year}/.
    /// Populated by EpoSchemaProvider (issue #35) via CopyToOutputDirectory.
    /// </summary>
    private static string XsdBasePath(int year) =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "Epo", year.ToString());

    /// <summary>
    /// Base directory for sample fixture XML files: bin/…/Resources/Epo/{year}/Samples/.
    /// Copied from Fakvio.Tests.Unit/Resources/Epo/{year}/Samples/ via EmbeddedResource.
    /// </summary>
    private static string SamplePath(int year, string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "Epo", year.ToString(), "Samples", fileName);

    // =========================================================================
    // Well-formed XML checks — always run
    // =========================================================================

    [Fact]
    public void DPHDP3_Sample2026_IsWellFormedXml()
    {
        // Fixture must parse without XmlException (i.e. well-formed XML).
        // This catches encoding problems, unclosed tags, and mismatched namespaces.
        var path = SamplePath(2026, "DPHDP3_sample_2026.xml");
        File.Exists(path).ShouldBeTrue($"Fixture not found at: {path}");

        var xml = File.ReadAllText(path);
        var exception = Record.Exception(() => XDocument.Parse(xml));
        exception.ShouldBeNull($"DPHDP3_sample_2026.xml is not well-formed XML: {exception?.Message}");
    }

    [Fact]
    public void DPHKH1_Sample2026_IsWellFormedXml()
    {
        // Same well-formed check for the control statement sample.
        var path = SamplePath(2026, "DPHKH1_sample_2026.xml");
        File.Exists(path).ShouldBeTrue($"Fixture not found at: {path}");

        var xml = File.ReadAllText(path);
        var exception = Record.Exception(() => XDocument.Parse(xml));
        exception.ShouldBeNull($"DPHKH1_sample_2026.xml is not well-formed XML: {exception?.Message}");
    }

    // =========================================================================
    // Root element checks — always run
    // =========================================================================

    [Fact]
    public void DPHDP3_Sample2026_HasExpectedRootElement()
    {
        // Root must be <Pisemnost> containing <DPHDP3> — EPO envelope convention.
        var path = SamplePath(2026, "DPHDP3_sample_2026.xml");
        var doc = XDocument.Parse(File.ReadAllText(path));

        doc.Root.ShouldNotBeNull();
        doc.Root!.Name.LocalName.ShouldBe("Pisemnost");

        var dphdp3 = doc.Root.Element("DPHDP3");
        dphdp3.ShouldNotBeNull("Expected <DPHDP3> child element inside <Pisemnost>");
    }

    [Fact]
    public void DPHKH1_Sample2026_HasExpectedRootElement()
    {
        // Root must be <Pisemnost> containing <DPHKH1>.
        var path = SamplePath(2026, "DPHKH1_sample_2026.xml");
        var doc = XDocument.Parse(File.ReadAllText(path));

        doc.Root.ShouldNotBeNull();
        doc.Root!.Name.LocalName.ShouldBe("Pisemnost");

        var dphkh1 = doc.Root.Element("DPHKH1");
        dphkh1.ShouldNotBeNull("Expected <DPHKH1> child element inside <Pisemnost>");
    }

    // =========================================================================
    // Content checks — always run (structural sanity without XSD)
    // =========================================================================

    [Fact]
    public void DPHDP3_Sample2026_HasVetaDAndVetaR()
    {
        // DPHDP3 form must contain taxpayer identification (VetaD) and
        // at least one tax-line element (VetaR).
        var path = SamplePath(2026, "DPHDP3_sample_2026.xml");
        var doc = XDocument.Parse(File.ReadAllText(path));
        var dphdp3 = doc.Root!.Element("DPHDP3")!;

        dphdp3.Element("VetaD").ShouldNotBeNull("VetaD (taxpayer identification) is required in DPHDP3");
        dphdp3.Element("VetaR").ShouldNotBeNull("VetaR (tax lines) is required in DPHDP3");
    }

    [Fact]
    public void DPHKH1_Sample2026_ContainsExpectedSections()
    {
        // DPHKH1 fixture must contain all four used sections:
        //   A.4 (VetaA4) — received supplies ≤ 10 000 CZK, aggregate
        //   A.5 (VetaA5) — received supplies > 10 000 CZK, individual
        //   B.2 (VetaB2) — reverse-charge supplies
        //   B.3 (VetaB3) — supplies in transfer-of-tax-liability mode (§92a)
        // Sections A.1 and B.1 are deliberately empty (no elements expected).
        var path = SamplePath(2026, "DPHKH1_sample_2026.xml");
        var doc = XDocument.Parse(File.ReadAllText(path));
        var dphkh1 = doc.Root!.Element("DPHKH1")!;

        dphkh1.Elements("VetaA4").ShouldNotBeEmpty("Section A.4 (VetaA4) should have at least one entry");
        dphkh1.Elements("VetaA5").ShouldNotBeEmpty("Section A.5 (VetaA5) should have at least one entry");
        dphkh1.Elements("VetaB2").ShouldNotBeEmpty("Section B.2 (VetaB2) should have at least one entry");
        dphkh1.Elements("VetaB3").ShouldNotBeEmpty("Section B.3 (VetaB3) should have at least one entry");

        // A.1 and B.1 intentionally empty in this fixture
        dphkh1.Elements("VetaA1").ShouldBeEmpty("Section A.1 (VetaA1) should be absent in this fixture");
        dphkh1.Elements("VetaB1").ShouldBeEmpty("Section B.1 (VetaB1) should be absent in this fixture");
    }

    [Fact]
    public void DPHDP3_Sample2026_PeriodAttributesAreSet()
    {
        // Both zdobd_od and zdobd_do must be present and parseable as dates.
        var path = SamplePath(2026, "DPHDP3_sample_2026.xml");
        var doc = XDocument.Parse(File.ReadAllText(path));
        var dphdp3 = doc.Root!.Element("DPHDP3")!;

        var from = (string?)dphdp3.Attribute("zdobd_od");
        var to   = (string?)dphdp3.Attribute("zdobd_do");

        from.ShouldNotBeNullOrEmpty("Attribute zdobd_od (period start) must be set");
        to.ShouldNotBeNullOrEmpty("Attribute zdobd_do (period end) must be set");

        // Must be valid ISO dates
        DateOnly.TryParse(from, out _).ShouldBeTrue($"zdobd_od '{from}' is not a valid date");
        DateOnly.TryParse(to,   out _).ShouldBeTrue($"zdobd_do '{to}' is not a valid date");
    }

    // =========================================================================
    // XSD schema validation — requires issue #35 (EpoSchemaProvider) to be merged
    // =========================================================================
    // TODO (#35): After EpoSchemaProvider is merged and XSD files are deployed,
    //             uncomment the tests below to enable full XSD validation.
    //
    // [Fact]
    // public void DPHDP3_Sample2026_ValidatesAgainstXsd()
    // {
    //     var xsdPath = Path.Combine(XsdBasePath(2026), "dphdp3.xsd");
    //     if (!File.Exists(xsdPath))
    //     {
    //         // XSD not yet available (issue #35 not merged) — skip gracefully.
    //         return;
    //     }
    //     var samplePath = SamplePath(2026, "DPHDP3_sample_2026.xml");
    //     var errors = ValidateXml(samplePath, xsdPath);
    //     errors.ShouldBeEmpty($"DPHDP3 fixture XSD errors:\n{string.Join("\n", errors)}");
    // }
    //
    // [Fact]
    // public void DPHKH1_Sample2026_ValidatesAgainstXsd()
    // {
    //     var xsdPath = Path.Combine(XsdBasePath(2026), "dphkh1.xsd");
    //     if (!File.Exists(xsdPath))
    //         return;
    //     var samplePath = SamplePath(2026, "DPHKH1_sample_2026.xml");
    //     var errors = ValidateXml(samplePath, xsdPath);
    //     errors.ShouldBeEmpty($"DPHKH1 fixture XSD errors:\n{string.Join("\n", errors)}");
    // }
    //
    // private static List<string> ValidateXml(string xmlPath, string xsdPath)
    // {
    //     var schemas = new XmlSchemaSet();
    //     schemas.Add(null, xsdPath);
    //     var doc = XDocument.Parse(File.ReadAllText(xmlPath));
    //     var errors = new List<string>();
    //     doc.Validate(schemas, (_, e) => errors.Add(e.Message));
    //     return errors;
    // }
}
