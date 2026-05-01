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
///    (EpoSchemaProvider) are present in the build output. The XSD files are
///    expected at Resources/Epo/2026/dphdp3_epo2.xsd / dphkh1_epo2.xsd
///    (deployed by Fakvio.Infrastructure via None Include CopyToOutputDirectory
///    from issue #35). If the files are absent the test skips gracefully.
/// </summary>
public class EpoFixtureValidationTests
{
    // =========================================================================
    // Paths — resolved relative to the test assembly's output directory
    // =========================================================================

    /// <summary>
    /// Base directory for XSD files: bin/.../Resources/Epo/{year}/.
    /// Populated by EpoSchemaProvider (issue #35) via None Include CopyToOutputDirectory.
    /// </summary>
    private static string XsdBasePath(int year) =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "Epo", year.ToString());

    /// <summary>
    /// Full path to an XSD file: Resources/Epo/{year}/{xsdFileName}.
    /// File name must follow the _epo2.xsd convention (e.g. dphdp3_epo2.xsd).
    /// </summary>
    private static string XsdPath(int year, string xsdFileName) =>
        Path.Combine(XsdBasePath(year), xsdFileName);

    /// <summary>
    /// Full path to a sample fixture XML file.
    /// Copied to output via None Update CopyToOutputDirectory in Fakvio.Tests.Unit.csproj.
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
    public void DPHDP3_Sample2026_HasVetaDAndVeta1()
    {
        // DPHDP3 form must contain:
        //   VetaD — period header and taxpayer identification (minOccurs=1 in XSD)
        //   Veta1 — standard-rate supply tax amounts (present in this fixture)
        // Note: VetaR is a text-attachment element (not a tax-line element); tax amounts
        // belong in Veta1 through Veta6.
        var path = SamplePath(2026, "DPHDP3_sample_2026.xml");
        var doc = XDocument.Parse(File.ReadAllText(path));
        var dphdp3 = doc.Root!.Element("DPHDP3")!;

        dphdp3.Element("VetaD").ShouldNotBeNull("VetaD (period/taxpayer header) is required in DPHDP3");
        dphdp3.Element("Veta1").ShouldNotBeNull("Veta1 (standard-rate supply amounts) is required in this fixture");
    }

    [Fact]
    public void DPHKH1_Sample2026_ContainsExpectedSections()
    {
        // DPHKH1 fixture must contain all four populated sections:
        //   A.4 (VetaA4) — received supplies, at least one record (maxOccurs=unbounded)
        //   A.5 (VetaA5) — received supplies aggregate, exactly one (maxOccurs=1)
        //   B.2 (VetaB2) — reverse-charge supplies, at least one record (maxOccurs=unbounded)
        //   B.3 (VetaB3) — transfer-of-liability aggregate, exactly one (maxOccurs=1)
        // Sections A.1 and B.1 are deliberately empty in this fixture.
        var path = SamplePath(2026, "DPHKH1_sample_2026.xml");
        var doc = XDocument.Parse(File.ReadAllText(path));
        var dphkh1 = doc.Root!.Element("DPHKH1")!;

        dphkh1.Elements("VetaA4").ShouldNotBeEmpty("Section A.4 (VetaA4) should have at least one entry");
        dphkh1.Element("VetaA5").ShouldNotBeNull("Section A.5 aggregate (VetaA5) should be present (maxOccurs=1)");
        dphkh1.Elements("VetaB2").ShouldNotBeEmpty("Section B.2 (VetaB2) should have at least one entry");
        dphkh1.Element("VetaB3").ShouldNotBeNull("Section B.3 aggregate (VetaB3) should be present (maxOccurs=1)");

        // A.1 and B.1 intentionally empty in this fixture
        dphkh1.Elements("VetaA1").ShouldBeEmpty("Section A.1 (VetaA1) should be absent in this fixture");
        dphkh1.Elements("VetaB1").ShouldBeEmpty("Section B.1 (VetaB1) should be absent in this fixture");
    }

    [Fact]
    public void DPHDP3_Sample2026_PeriodAttributesAreSet()
    {
        // Period attributes zdobd_od and zdobd_do must be present on VetaD
        // and formatted as D.M.RRRR (EPO2 dateInMultiFormat), NOT ISO 8601.
        var path = SamplePath(2026, "DPHDP3_sample_2026.xml");
        var doc = XDocument.Parse(File.ReadAllText(path));
        var vetaD = doc.Root!.Element("DPHDP3")!.Element("VetaD")!;

        var from = (string?)vetaD.Attribute("zdobd_od");
        var to   = (string?)vetaD.Attribute("zdobd_do");

        from.ShouldNotBeNullOrEmpty("Attribute zdobd_od (period start) must be set on VetaD");
        to.ShouldNotBeNullOrEmpty("Attribute zdobd_do (period end) must be set on VetaD");

        // EPO2 uses Czech date format D.M.RRRR (e.g. "1.3.2026"), not ISO 8601
        bool IsCzechDate(string? s) =>
            s is not null && DateTime.TryParseExact(
                s,
                new[] { "d.M.yyyy", "dd.M.yyyy", "d.MM.yyyy", "dd.MM.yyyy" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _);

        IsCzechDate(from).ShouldBeTrue($"zdobd_od '{from}' is not a valid D.M.RRRR date (EPO2 format)");
        IsCzechDate(to).ShouldBeTrue($"zdobd_do '{to}' is not a valid D.M.RRRR date (EPO2 format)");
    }

    // =========================================================================
    // XSD schema validation — requires issue #35 (EpoSchemaProvider) to be merged
    // =========================================================================

    [Fact]
    public void DPHDP3_Sample2026_ValidatesAgainstXsd()
    {
        // Skips gracefully when XSD is not yet deployed (issue #35 not merged).
        // XSD filename follows the _epo2.xsd naming convention used by EpoSchemaProvider.
        var xsdPath = XsdPath(2026, "dphdp3_epo2.xsd");
        if (!File.Exists(xsdPath))
            return; // issue #35 not yet merged — skip

        var samplePath = SamplePath(2026, "DPHDP3_sample_2026.xml");
        var errors = ValidateXml(samplePath, xsdPath);
        errors.ShouldBeEmpty($"DPHDP3 fixture XSD errors:\n{string.Join("\n", errors)}");
    }

    [Fact]
    public void DPHKH1_Sample2026_ValidatesAgainstXsd()
    {
        // Skips gracefully when XSD is not yet deployed (issue #35 not merged).
        var xsdPath = XsdPath(2026, "dphkh1_epo2.xsd");
        if (!File.Exists(xsdPath))
            return; // issue #35 not yet merged — skip

        var samplePath = SamplePath(2026, "DPHKH1_sample_2026.xml");
        var errors = ValidateXml(samplePath, xsdPath);
        errors.ShouldBeEmpty($"DPHKH1 fixture XSD errors:\n{string.Join("\n", errors)}");
    }

    private static List<string> ValidateXml(string xmlPath, string xsdPath)
    {
        var schemas = new XmlSchemaSet();
        schemas.Add(null, xsdPath);
        var doc = XDocument.Parse(File.ReadAllText(xmlPath));
        var errors = new List<string>();
        doc.Validate(schemas, (_, e) => errors.Add(e.Message));
        return errors;
    }
}
