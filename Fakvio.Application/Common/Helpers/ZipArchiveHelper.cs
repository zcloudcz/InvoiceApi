using System.IO.Compression;

namespace Fakvio.Application.Common.Helpers;

/// <summary>
/// Helper for building ZIP archives in memory.
/// Used by attachment download endpoints (single record "download all"
/// and bulk download across records).
///
/// Why a helper? Entry-name collisions must be handled consistently —
/// a ZIP archive with two identical entry names is technically valid but
/// most extractors silently overwrite one file with the other, losing data.
/// </summary>
public static class ZipArchiveHelper
{
    /// <summary>
    /// Returns a file name that is unique within <paramref name="usedNames"/>
    /// and registers it there. Collisions get a " (2)", " (3)", … suffix before
    /// the extension: "scan.pdf" → "scan (2).pdf". Comparison is case-insensitive
    /// because Windows extractors treat "Scan.PDF" and "scan.pdf" as the same file.
    /// </summary>
    /// <param name="usedNames">Set of names already present in the archive (case-insensitive).</param>
    /// <param name="fileName">Desired file name (may repeat across attachments).</param>
    public static string UniqueEntryName(HashSet<string> usedNames, string fileName)
    {
        if (usedNames.Add(fileName))
            return fileName;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        // Try "name (2).ext", "name (3).ext", … until a free name is found.
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i}){extension}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Removes characters that are illegal or dangerous in ZIP entry path segments
    /// (path separators, colons, wildcards). Used to sanitize user-visible values
    /// like invoice document numbers before they become ZIP folder names.
    /// Returns "_" when the input is null/empty or sanitizes to nothing.
    /// </summary>
    public static string SanitizePathSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return "_";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        return string.IsNullOrEmpty(cleaned) ? "_" : cleaned;
    }

    /// <summary>
    /// Builds an in-memory ZIP archive from (entryName, content) pairs.
    /// Entry names must already be unique — use <see cref="UniqueEntryName"/> when
    /// assembling them. Returns the complete ZIP file bytes.
    /// </summary>
    public static byte[] CreateZip(IEnumerable<(string EntryName, byte[] Content)> entries)
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryName, content) in entries)
            {
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(content, 0, content.Length);
            }
        }

        return zipStream.ToArray();
    }
}
