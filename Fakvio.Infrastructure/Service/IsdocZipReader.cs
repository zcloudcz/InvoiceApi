using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Shared helper for extracting ISDOC XML text out of raw bytes — handles both a plain
/// .isdoc file (already XML) and .isdocx (a ZIP container with a .isdoc entry inside).
///
/// Used by both inbound paths that accept ISDOC (F1.10, see
/// docs/adr/0002-sk-einvoicing-peppol.md):
/// - <see cref="InvoiceEmailProcessor"/> (email attachments)
/// - <see cref="InvoiceImportService"/> (manual upload)
///
/// so they share exactly the same "ZIP bomb" protection instead of two copies that
/// could quietly drift apart.
/// </summary>
internal static class IsdocZipReader
{
    /// <summary>
    /// Cap on the *decompressed* size read out of a .isdocx entry. Real ISDOC documents
    /// are a few hundred KB at most — this only needs to be generous, not large.
    /// </summary>
    internal const int MaxDecompressedBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Reads the ISDOC XML text out of <paramref name="bytes"/>.
    /// </summary>
    /// <param name="bytes">Raw file/attachment bytes — either plain ISDOC XML or a ZIP.</param>
    /// <param name="logger">Logger for the caller's category (kept as the caller's own for clearer log grouping).</param>
    /// <param name="context">File name or other identifier, for log messages only.</param>
    /// <returns>
    /// The ISDOC XML text, or null if there's no .isdoc entry in the ZIP, the ZIP is
    /// malformed, or the entry decompresses to more than <see cref="MaxDecompressedBytes"/>.
    /// Never throws.
    /// </returns>
    public static string? ExtractXmlText(byte[] bytes, ILogger logger, string context)
    {
        // ZIP files start with the PK header (0x504B0304).
        var isZip = bytes.Length >= 4
            && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

        if (!isZip)
            return Encoding.UTF8.GetString(bytes);

        try
        {
            using var zipStream = new MemoryStream(bytes);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var entry = archive.Entries
                .FirstOrDefault(e => e.Name.EndsWith(".isdoc", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                logger.LogDebug("ISDOCX ZIP contains no .isdoc file ({Context})", context);
                return null;
            }

            // Bounded read: stop the moment more than MaxDecompressedBytes has come out of
            // the decompression stream. This is the actual ZIP-bomb defense — a ZIP entry's
            // own "uncompressed size" header is attacker-controlled and cannot be trusted,
            // so we count real bytes as they're decompressed instead of trusting entry.Length.
            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = entryStream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxDecompressedBytes)
                {
                    logger.LogWarning(
                        "ISDOCX entry exceeds {Limit} bytes decompressed — rejecting ({Context})",
                        MaxDecompressedBytes, context);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract ISDOC from ZIP container ({Context})", context);
            return null;
        }
    }
}
