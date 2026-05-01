using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Schema;
using Fakvio.Application.Service;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Loads and caches EPO XSD schema files from the file system.
///
/// Schema files are stored under <c>{AppContext.BaseDirectory}/Resources/Epo/{year}/</c>
/// as plain files with <c>CopyToOutputDirectory=PreserveNewest</c>. This means a new
/// tax year can be added by dropping the XSD file into the folder — no recompile needed.
///
/// File name convention (matches what the Czech Financial Administration publishes):
/// - DPHDP3 (VAT return):       dphdp3_epo2.xsd
/// - DPHKH1 (control statement): dphkh1_epo2.xsd
///
/// Caching: each (formType, year) key is compiled exactly once per process lifetime.
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> guarantees thread-safe reads without locking.
/// </summary>
public sealed class EpoSchemaProvider : IEpoSchemaProvider
{
    // ConcurrentDictionary is safe for concurrent read + lazy-init via GetOrAdd.
    // The factory delegate may run more than once under high contention but
    // XmlSchemaSet.Compile() is idempotent and the dictionary keeps only one result.
    private readonly ConcurrentDictionary<(EEpoFormType, int), XmlSchemaSet> _cache = new();

    /// <summary>
    /// Base directory where the <c>Resources/Epo/</c> folder lives.
    /// Injected so tests can point to a controlled directory without relying on
    /// <c>AppContext.BaseDirectory</c> (which can vary between test runners).
    /// When <c>null</c>, defaults to <c>AppContext.BaseDirectory</c>.
    /// </summary>
    private readonly string _baseDirectory;

    /// <summary>
    /// Constructs an <see cref="EpoSchemaProvider"/> using <c>AppContext.BaseDirectory</c>
    /// as the root for XSD asset lookup. Used by the DI container.
    /// </summary>
    public EpoSchemaProvider()
        : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>
    /// Constructs an <see cref="EpoSchemaProvider"/> using a custom base directory.
    /// Use this constructor in unit tests to point to a known folder.
    /// </summary>
    /// <param name="baseDirectory">Absolute path that contains the <c>Resources/Epo/</c> tree.</param>
    internal EpoSchemaProvider(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    /// <inheritdoc />
    public XmlSchemaSet GetSchemaSet(EEpoFormType formType, int year)
    {
        // GetOrAdd is thread-safe — only one XmlSchemaSet is stored per key.
        return _cache.GetOrAdd(
            (formType, year),
            key => LoadAndCompile(key.Item1, key.Item2));
    }

    // --------------------------------------------------------------------------
    // Private helpers
    // --------------------------------------------------------------------------

    /// <summary>
    /// Resolves the file-system path for a given (formType, year) pair and
    /// loads + compiles the XSD into an <see cref="XmlSchemaSet"/>.
    /// </summary>
    private XmlSchemaSet LoadAndCompile(EEpoFormType formType, int year)
    {
        var fileName = ResolveFileName(formType);
        var path = Path.Combine(
            _baseDirectory,
            "Resources", "Epo",
            year.ToString(),
            fileName);

        if (!File.Exists(path))
        {
            throw new NotSupportedException(
                $"EPO XSD schema not found for form type '{formType}' and year {year}. " +
                $"Expected file: '{path}'. " +
                $"Download the current XSD from adisspr.mfcr.cz and place it in the " +
                $"Fakvio.Infrastructure/Resources/Epo/{year}/ folder.");
        }

        using var reader = XmlReader.Create(path);
        var schemaSet = new XmlSchemaSet();
        schemaSet.Add(null, reader); // no target namespace — EPO schemas use no namespace
        schemaSet.Compile();
        return schemaSet;
    }

    /// <summary>
    /// Maps an <see cref="EEpoFormType"/> to the canonical XSD file name used
    /// by the Czech Financial Administration (Finanční správa).
    /// </summary>
    private static string ResolveFileName(EEpoFormType formType)
        => formType switch
        {
            // VAT return — "Přiznání k dani z přidané hodnoty" (DPHDP3)
            EEpoFormType.VatReturn      => "dphdp3_epo2.xsd",
            // VAT control statement — "Kontrolní hlášení DPH" (DPHKH1)
            EEpoFormType.ControlStatement => "dphkh1_epo2.xsd",

            // Guard against future enum values added without a corresponding XSD.
            _ => throw new NotSupportedException(
                $"Unknown EPO form type: {formType}. " +
                $"Add a file-name mapping in {nameof(EpoSchemaProvider)}.{nameof(ResolveFileName)}.")
        };
}
