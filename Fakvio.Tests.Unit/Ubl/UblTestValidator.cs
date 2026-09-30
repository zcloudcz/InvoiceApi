using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using net.liberty_development.SaxonHE12s9apiExtensions;
using net.sf.saxon.s9api;

namespace Fakvio.Tests.Unit.Ubl;

/// <summary>
/// Test-only validation harness for the UBL 2.1 / Peppol BIS Billing 3.0 export (ADR 0002,
/// F1.2). Wraps the vendored artifacts in <c>Ubl/Vendored/</c> — see that folder's
/// <c>README.md</c> for where each file comes from.
///
/// Two independent checks, both against a fully-formed <see cref="XDocument"/> (no I/O other
/// than reading the artifacts themselves):
/// <list type="bullet">
/// <item><see cref="ValidateXsd"/> — structural correctness against the OASIS UBL 2.1 XSD.</item>
/// <item><see cref="ValidateSchematron"/> — business-rule correctness (EN 16931 + Peppol BIS)
/// via Saxon-HE 12 (XSLT 2.0/3.0 — <c>System.Xml.Xsl</c> only runs XSLT 1.0, which the official
/// precompiled stylesheets are not).</item>
/// </list>
///
/// Never referenced from a production project — this class, and the
/// <c>SaxonHE12s9apiExtensions</c>/<c>IKVM.Maven.Sdk</c> packages it needs, live only in
/// <c>Fakvio.Tests.Unit</c>.
/// </summary>
internal static class UblTestValidator
{
    // The artifacts are copied next to the test assembly (see the .csproj's
    // "Ubl\Vendored\**" None/CopyToOutputDirectory items) — resolve relative to
    // AppContext.BaseDirectory so the harness works from `dotnet test` or an IDE test runner
    // regardless of the current working directory.
    private static readonly string VendoredRoot =
        Path.Combine(AppContext.BaseDirectory, "Ubl", "Vendored");

    // --------------------------------------------------------------------------
    // XSD validation
    // --------------------------------------------------------------------------

    /// <summary>
    /// Validates <paramref name="document"/> against the OASIS UBL 2.1 XSD for its root element
    /// (<c>Invoice</c> or <c>CreditNote</c>). Returns every validation error message; an empty
    /// list means the document is schema-valid.
    /// </summary>
    internal static IReadOnlyList<string> ValidateXsd(XDocument document)
    {
        var rootName = document.Root?.Name.LocalName
            ?? throw new ArgumentException("Document has no root element.", nameof(document));

        var schemaSet = rootName switch
        {
            "Invoice" => InvoiceSchemaSet.Value,
            "CreditNote" => CreditNoteSchemaSet.Value,
            _ => throw new NotSupportedException(
                $"No UBL 2.1 XSD wired up for root element '{rootName}'. " +
                "Only Invoice and CreditNote are used by the UBL export.")
        };

        var errors = new List<string>();
        // A clone is validated in place, so the caller's document (and any later assertions on
        // it) are never mutated by XSD default/fixed value expansion.
        var copy = new XDocument(document);
        copy.Validate(schemaSet, (_, e) => errors.Add(e.Message));
        return errors;
    }

    private static readonly Lazy<XmlSchemaSet> InvoiceSchemaSet =
        new(() => LoadSchemaSet("UBL-Invoice-2.1.xsd"), isThreadSafe: true);

    private static readonly Lazy<XmlSchemaSet> CreditNoteSchemaSet =
        new(() => LoadSchemaSet("UBL-CreditNote-2.1.xsd"), isThreadSafe: true);

    private static XmlSchemaSet LoadSchemaSet(string maindocFileName)
    {
        var schemaSet = new XmlSchemaSet();

        // Relying on XmlSchemaSet to auto-follow <xsd:import schemaLocation="../common/…">
        // from the maindoc schema alone does not reliably pull in the whole transitive closure
        // (observed: the compiled set silently ends up missing declarations from
        // UBL-CommonExtensionComponents-2.1.xsd, breaking compilation with a confusing "element
        // is not declared" error). Adding every "common" schema explicitly first — each under
        // its own targetNamespace, read straight from the .xsd file — guarantees the closure is
        // complete before the maindoc schema (which only references these namespaces) is added.
        foreach (var commonSchemaPath in Directory.EnumerateFiles(Path.Combine(VendoredRoot, "common"), "*.xsd"))
        {
            using var commonReader = XmlReader.Create(commonSchemaPath, ReaderSettings);
            schemaSet.Add(null, commonReader);
        }

        var maindocPath = Path.Combine(VendoredRoot, "maindoc", maindocFileName);
        using var maindocReader = XmlReader.Create(maindocPath, ReaderSettings);
        schemaSet.Add(null, maindocReader);

        schemaSet.Compile();
        return schemaSet;
    }

    // UBL-xmldsig-core-schema-2.1.xsd (the W3C xmldsig schema, vendored unmodified into the UBL
    // package) declares an internal DTD subset purely to define a couple of XML entities used
    // as namespace-URI shorthands inside the schema itself — no external DTD is ever fetched.
    // XmlReader refuses any DTD at all unless explicitly told it is safe to parse.
    private static readonly XmlReaderSettings ReaderSettings = new() { DtdProcessing = DtdProcessing.Parse };

    // --------------------------------------------------------------------------
    // Schematron validation (EN 16931 + Peppol BIS, via Saxon-HE 12)
    // --------------------------------------------------------------------------

    private const string SvrlNs = "http://purl.oclc.org/dsdl/svrl";

    /// <summary>
    /// Runs <paramref name="document"/> through both the CEN EN 16931 and the Peppol BIS Billing
    /// 3.0 schematron stylesheets (precompiled XSLT, see <c>Ubl/Vendored/README.md</c>) and
    /// returns the human-readable text of every <c>svrl:failed-assert</c> whose <c>flag</c>
    /// attribute is <c>"fatal"</c> — these are the assertions that PEPPOL-EN16931-UBL.xslt and
    /// EN16931-UBL-validation.xslt themselves mark as build-blocking; a "warning"-flagged
    /// assertion (a recommendation, not a EN 16931/Peppol rule violation) is not returned. An
    /// empty list means the document passes both rule sets.
    /// </summary>
    internal static IReadOnlyList<string> ValidateSchematron(XDocument document)
    {
        var errors = new List<string>();
        errors.AddRange(RunSchematron(document, "EN16931-UBL-validation.xslt"));
        errors.AddRange(RunSchematron(document, "PEPPOL-EN16931-UBL.xslt"));
        return errors;
    }

    private static IReadOnlyList<string> RunSchematron(XDocument document, string xsltFileName)
    {
        var processor = Processors.GetOrAdd(xsltFileName, _ => new Processor(licensedEdition: false));
        // XsltExecutable (the compiled stylesheet) is immutable/thread-safe and reused across
        // calls — compiling ~900 KB of generated XSLT is the expensive part. Xslt30Transformer
        // is NOT thread-safe (it holds per-run state), so a fresh one is loaded per call; xUnit
        // runs different test classes in parallel by default, and this validator is shared.
        var executable = Executables.GetOrAdd(xsltFileName, name =>
        {
            var xsltPath = Path.Combine(VendoredRoot, name);
            var compiler = processor.newXsltCompiler();
            return compiler.Compile(new FileInfo(xsltPath));
        });
        var transformer = executable.load30();

        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
        var source = inputStream.AsSource();
        var inputNode = processor.newDocumentBuilder().build(source);

        using var outputStream = new MemoryStream();
        var serializer = processor.NewSerializer(outputStream);
        // Both schematron stylesheets declare top-level xsl:param defaults (e.g. Peppol's
        // $profile, read via "(root/element())/ProfileID") that evaluate against the *global*
        // context item, which neither applyTemplates(XdmValue, …) nor applyTemplates(Source, …)
        // sets on their own — only setGlobalContextItem does. Without it Saxon fails with
        // "context item is absent" the first time such a parameter is evaluated.
        transformer.setGlobalContextItem(inputNode);
        transformer.applyTemplates(inputNode, serializer);

        outputStream.Position = 0;
        var svrl = XDocument.Load(outputStream);

        return svrl.Descendants(XName.Get("failed-assert", SvrlNs))
            .Where(e => (string?)e.Attribute("flag") == "fatal")
            .Select(DescribeFailedAssert)
            .ToList();
    }

    private static string DescribeFailedAssert(XElement failedAssert)
    {
        var text = failedAssert.Element(XName.Get("text", SvrlNs))?.Value.Trim() ?? "(no message)";
        var testId = (string?)failedAssert.Attribute("id");
        var location = (string?)failedAssert.Attribute("location");
        return testId is null ? text : $"[{testId}] {text} (at {location})";
    }

    // One Processor/compiled-executable pair per stylesheet, reused across all validations in
    // the test run — see the thread-safety note in RunSchematron above.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Processor> Processors = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, XsltExecutable> Executables = new();
}
