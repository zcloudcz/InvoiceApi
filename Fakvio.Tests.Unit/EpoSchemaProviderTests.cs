using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="EpoSchemaProvider"/> and <see cref="EEpoFormType"/>.
///
/// The provider is instantiated with a custom base directory pointing to the real
/// XSD assets copied to the Infrastructure output during build. This is preferable
/// to mocking the file system — the XSD files are cheap to read and the real
/// compilation exercise tests the whole loading path end-to-end.
///
/// Test categories:
///   1. Happy path: VatReturn (DPHDP3) and ControlStatement (DPHKH1) load without errors.
///   2. Caching: repeated calls for the same (formType, year) return the SAME instance.
///   3. Missing year: requesting a year with no file throws NotSupportedException.
///   4. Unknown form type: requesting an unknown EEpoFormType throws NotSupportedException.
///   5. XML roundtrip: a minimal conformant XML document validates against the compiled schema.
///   6. Concurrent access: ConcurrentDictionary handles parallel calls without data corruption.
///   7. Edge-case years: year 0 and negative years throw NotSupportedException (no file exists).
/// </summary>
public class EpoSchemaProviderTests
{
    // Resolve the real XSD assets from the Infrastructure project output.
    // The test project references Fakvio.Infrastructure, so MSBuild copies the
    // Resources/Epo/** files to the test output directory alongside the assembly.
    // AppContext.BaseDirectory points to the test output (e.g. bin/Debug/net10.0/).
    private static readonly string TestBaseDirectory = AppContext.BaseDirectory;

    private EpoSchemaProvider CreateProvider() => new(TestBaseDirectory);

    // =========================================================================
    // Happy path — real XSD files must load and compile without error
    // =========================================================================

    [Fact]
    public void GetSchemaSet_VatReturn_2026_ReturnsCompiledSchemaSet()
    {
        // DPHDP3 XSD for 2026 must load without throwing.
        // A compiled XmlSchemaSet with Count > 0 means the schema was parsed successfully.
        var provider = CreateProvider();

        var schemaSet = provider.GetSchemaSet(EEpoFormType.VatReturn, 2026);

        schemaSet.ShouldNotBeNull();
        schemaSet.Count.ShouldBeGreaterThan(0,
            "Expected at least one compiled schema in the XmlSchemaSet for DPHDP3 2026.");
    }

    [Fact]
    public void GetSchemaSet_ControlStatement_2026_ReturnsCompiledSchemaSet()
    {
        // DPHKH1 XSD for 2026 must load without throwing.
        var provider = CreateProvider();

        var schemaSet = provider.GetSchemaSet(EEpoFormType.ControlStatement, 2026);

        schemaSet.ShouldNotBeNull();
        schemaSet.Count.ShouldBeGreaterThan(0,
            "Expected at least one compiled schema in the XmlSchemaSet for DPHKH1 2026.");
    }

    // =========================================================================
    // Caching — repeated calls must return the SAME XmlSchemaSet instance
    // =========================================================================

    [Fact]
    public void GetSchemaSet_CalledTwiceForSameKey_ReturnsSameInstance()
    {
        // XmlSchemaSet.Compile() is expensive; the provider must cache the result.
        // Reference equality (ReferenceEquals) confirms the same object is reused.
        var provider = CreateProvider();

        var first  = provider.GetSchemaSet(EEpoFormType.VatReturn, 2026);
        var second = provider.GetSchemaSet(EEpoFormType.VatReturn, 2026);

        ReferenceEquals(first, second).ShouldBeTrue(
            "Expected the provider to return the cached XmlSchemaSet, not recompile it.");
    }

    [Fact]
    public void GetSchemaSet_DifferentFormTypes_ReturnDistinctInstances()
    {
        // VatReturn and ControlStatement use different XSD files and must produce
        // separate XmlSchemaSet instances.
        var provider = CreateProvider();

        var vatReturn       = provider.GetSchemaSet(EEpoFormType.VatReturn, 2026);
        var controlStatement = provider.GetSchemaSet(EEpoFormType.ControlStatement, 2026);

        ReferenceEquals(vatReturn, controlStatement).ShouldBeFalse(
            "DPHDP3 and DPHKH1 schemas must be separate XmlSchemaSet instances.");
    }

    // =========================================================================
    // Missing year — no XSD file → NotSupportedException
    // =========================================================================

    [Fact]
    public void GetSchemaSet_NonExistentYear_ThrowsNotSupportedException()
    {
        // Year 1900 has no XSD asset — the provider must throw NotSupportedException
        // with a message that includes both the year and the form type so the developer
        // knows exactly what is missing.
        var provider = CreateProvider();

        var ex = Should.Throw<NotSupportedException>(
            () => provider.GetSchemaSet(EEpoFormType.VatReturn, 1900));

        // Both the year and the form type name must appear in the error message
        // so the developer knows exactly which asset is missing.
        ex.Message.ShouldContain("1900");      // year
        ex.Message.ShouldContain("VatReturn"); // form type name
    }

    [Fact]
    public void GetSchemaSet_NonExistentYear_ControlStatement_ThrowsNotSupportedException()
    {
        // Same check for the other form type.
        var provider = CreateProvider();

        var ex = Should.Throw<NotSupportedException>(
            () => provider.GetSchemaSet(EEpoFormType.ControlStatement, 1900));

        ex.Message.ShouldContain("1900");
        ex.Message.ShouldContain("ControlStatement");
    }

    // =========================================================================
    // Unknown form type — future-proofing guard
    // =========================================================================

    [Fact]
    public void GetSchemaSet_UnknownFormType_ThrowsNotSupportedException()
    {
        // If a new EEpoFormType value is added without adding a file-name mapping,
        // the provider must throw NotSupportedException rather than returning null
        // or loading the wrong file.
        var provider = CreateProvider();

        var ex = Should.Throw<NotSupportedException>(
            () => provider.GetSchemaSet((EEpoFormType)999, 2026));

        ex.Message.ShouldNotBeNullOrEmpty();
    }

    // =========================================================================
    // XML roundtrip — a minimal conformant document must validate against the
    // compiled schema without errors. This proves the XSD is usable, not just
    // loadable, and that the schema set is compiled with the correct elements.
    // =========================================================================

    [Fact]
    public void GetSchemaSet_VatReturn_MinimalConformantXml_ValidatesWithoutErrors()
    {
        // Minimal DPHDP3 document with all required attributes.
        // VetaD required: dapdph_forma, dokument (fixed "DP3"), k_uladis (fixed "DPH"), rok, typ_platce.
        // VetaP required: c_ufo (FÚ code), dic (taxpayer ID digits only).
        const string xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Pisemnost>
              <DPHDP3>
                <VetaD dapdph_forma="B" dokument="DP3" k_uladis="DPH" rok="2026" typ_platce="P" />
                <VetaP c_ufo="451" dic="1234567890" typ_ds="P" />
              </DPHDP3>
            </Pisemnost>
            """;

        var provider = CreateProvider();
        var schemaSet = provider.GetSchemaSet(EEpoFormType.VatReturn, 2026);

        var errors = new List<string>();
        var doc = XDocument.Parse(xml);
        doc.Validate(schemaSet, (_, e) => errors.Add(e.Message));

        errors.ShouldBeEmpty(
            "A minimal conformant DPHDP3 document should validate without any XSD errors.");
    }

    [Fact]
    public void GetSchemaSet_ControlStatement_MinimalConformantXml_ValidatesWithoutErrors()
    {
        // Minimal DPHKH1 document with all required attributes.
        // VetaD required: dokument (fixed "KH1"), k_uladis (fixed "DPH"), rok, khdph_forma.
        // VetaP required: c_ufo (FÚ code), dic (taxpayer ID digits only), typ_ds (F/P).
        const string xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Pisemnost>
              <DPHKH1>
                <VetaD dokument="KH1" k_uladis="DPH" rok="2026" khdph_forma="B" />
                <VetaP c_ufo="451" dic="1234567890" typ_ds="P" />
              </DPHKH1>
            </Pisemnost>
            """;

        var provider = CreateProvider();
        var schemaSet = provider.GetSchemaSet(EEpoFormType.ControlStatement, 2026);

        var errors = new List<string>();
        var doc = XDocument.Parse(xml);
        doc.Validate(schemaSet, (_, e) => errors.Add(e.Message));

        errors.ShouldBeEmpty(
            "A minimal conformant DPHKH1 document should validate without any XSD errors.");
    }

    [Fact]
    public void GetSchemaSet_VatReturn_InvalidXml_ReportsValidationErrors()
    {
        // An otherwise-complete document with a wrong fixed attribute value must produce at
        // least one validation error — this confirms that validation is actually enforced.
        // VetaP is included so that the only violation is the bad "dokument" value on VetaD.
        const string xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Pisemnost>
              <DPHDP3>
                <VetaD dapdph_forma="B" dokument="WRONG" k_uladis="DPH" rok="2026" typ_platce="P" />
                <VetaP c_ufo="451" dic="1234567890" />
              </DPHDP3>
            </Pisemnost>
            """;

        var provider = CreateProvider();
        var schemaSet = provider.GetSchemaSet(EEpoFormType.VatReturn, 2026);

        var errors = new List<string>();
        var doc = XDocument.Parse(xml);
        doc.Validate(schemaSet, (_, e) => errors.Add(e.Message));

        errors.ShouldNotBeEmpty(
            "An invalid DPHDP3 document (wrong 'dokument' fixed value) must produce XSD validation errors.");
    }

    // =========================================================================
    // Concurrent access — ConcurrentDictionary must survive parallel load
    // without duplicate compilation or race-condition exceptions.
    // =========================================================================

    [Fact]
    public void GetSchemaSet_ConcurrentCalls_SameKey_AllReturnSameInstance()
    {
        // 20 threads all request the same (VatReturn, 2026) key simultaneously.
        // Every call must return the identical cached instance — no two calls
        // should produce different XmlSchemaSet objects for the same key.
        const int threadCount = 20;
        var provider = CreateProvider();

        var results = new ConcurrentBag<XmlSchemaSet>();
        var barrier = new Barrier(threadCount); // all threads start at the same moment

        var threads = Enumerable.Range(0, threadCount)
            .Select(_ => new Thread(() =>
            {
                barrier.SignalAndWait(); // maximize contention
                results.Add(provider.GetSchemaSet(EEpoFormType.VatReturn, 2026));
            }))
            .ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        results.Count.ShouldBe(threadCount);

        // All results must be the same cached instance.
        var first = results.First();
        results.ShouldAllBe(s => ReferenceEquals(s, first),
            "All concurrent calls for the same key must return the same cached XmlSchemaSet.");
    }

    [Fact]
    public void GetSchemaSet_ConcurrentCalls_DifferentKeys_ReturnCorrectInstances()
    {
        // Parallel calls for both form types must each get the correct (distinct) schema.
        const int threadCount = 10;
        var provider = CreateProvider();

        var vatResults  = new ConcurrentBag<XmlSchemaSet>();
        var ctrlResults = new ConcurrentBag<XmlSchemaSet>();
        var barrier = new Barrier(threadCount * 2);

        var vatThreads = Enumerable.Range(0, threadCount)
            .Select(_ => new Thread(() =>
            {
                barrier.SignalAndWait();
                vatResults.Add(provider.GetSchemaSet(EEpoFormType.VatReturn, 2026));
            }))
            .ToList();

        var ctrlThreads = Enumerable.Range(0, threadCount)
            .Select(_ => new Thread(() =>
            {
                barrier.SignalAndWait();
                ctrlResults.Add(provider.GetSchemaSet(EEpoFormType.ControlStatement, 2026));
            }))
            .ToList();

        var all = vatThreads.Concat(ctrlThreads).ToList();
        all.ForEach(t => t.Start());
        all.ForEach(t => t.Join());

        // Each group must be internally consistent (same cached instance).
        var firstVat = vatResults.First();
        vatResults.ShouldAllBe(s => ReferenceEquals(s, firstVat));

        var firstCtrl = ctrlResults.First();
        ctrlResults.ShouldAllBe(s => ReferenceEquals(s, firstCtrl));

        // The two groups must NOT share the same instance.
        ReferenceEquals(firstVat, firstCtrl).ShouldBeFalse(
            "VatReturn and ControlStatement schemas must be distinct instances even under concurrency.");
    }

    // =========================================================================
    // Edge-case years — year 0 and negative years have no XSD file and must
    // throw NotSupportedException rather than producing a silent wrong result.
    // =========================================================================

    [Fact]
    public void GetSchemaSet_YearZero_ThrowsNotSupportedException()
    {
        // Year 0 is not a valid tax year; no XSD asset should exist for it.
        var provider = CreateProvider();

        var ex = Should.Throw<NotSupportedException>(
            () => provider.GetSchemaSet(EEpoFormType.VatReturn, 0));

        // The message must mention year 0 so the caller knows what was requested.
        ex.Message.ShouldContain("0");
    }

    [Fact]
    public void GetSchemaSet_NegativeYear_ThrowsNotSupportedException()
    {
        // Negative years are nonsensical for EPO submissions.
        var provider = CreateProvider();

        var ex = Should.Throw<NotSupportedException>(
            () => provider.GetSchemaSet(EEpoFormType.VatReturn, -1));

        ex.Message.ShouldNotBeNullOrEmpty();
    }
}
