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
}
