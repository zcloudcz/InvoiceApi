using System.IO.Compression;
using System.Text;
using Fakvio.Infrastructure.Service;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="IsdocZipReader"/> — shared ZIP-unwrap helper used by both
/// the email pipeline and the manual-upload pipeline for .isdocx (F1.10, see
/// docs/adr/0002-sk-einvoicing-peppol.md). Focus: the "ZIP bomb" defense (a tiny
/// compressed file that decompresses to gigabytes), since that's the actual security
/// property this class exists to provide.
/// </summary>
public class IsdocZipReaderTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Fact]
    public void ExtractXmlText_PlainXmlBytes_ReturnsAsIs()
    {
        var bytes = Encoding.UTF8.GetBytes("<Invoice/>");

        IsdocZipReader.ExtractXmlText(bytes, _logger, "plain.isdoc").ShouldBe("<Invoice/>");
    }

    [Fact]
    public void ExtractXmlText_ZipWithIsdocEntry_ReturnsEntryContent()
    {
        var zipBytes = BuildZip("invoice.isdoc", "<Invoice><ID>Z-1</ID></Invoice>");

        IsdocZipReader.ExtractXmlText(zipBytes, _logger, "test.isdocx")
            .ShouldBe("<Invoice><ID>Z-1</ID></Invoice>");
    }

    [Fact]
    public void ExtractXmlText_ZipWithoutIsdocEntry_ReturnsNull()
    {
        var zipBytes = BuildZip("readme.txt", "not an invoice");

        IsdocZipReader.ExtractXmlText(zipBytes, _logger, "test.isdocx").ShouldBeNull();
    }

    [Fact]
    public void ExtractXmlText_DecompressionBomb_RejectedInsteadOfExhaustingMemory()
    {
        // A small ZIP entry that decompresses to well beyond MaxDecompressedBytes.
        // Highly repetitive content compresses extremely well, so the ZIP stays tiny —
        // that gap (tiny on disk, huge decompressed) is exactly what a ZIP bomb exploits.
        var huge = new string('A', IsdocZipReader.MaxDecompressedBytes + 1024);
        var zipBytes = BuildZip("invoice.isdoc", huge);

        // Sanity check that this test actually exercises the bomb defense (and isn't just
        // passing because the ZIP itself happens to be small for an unrelated reason).
        zipBytes.Length.ShouldBeLessThan(IsdocZipReader.MaxDecompressedBytes / 10);

        IsdocZipReader.ExtractXmlText(zipBytes, _logger, "bomb.isdocx").ShouldBeNull();
    }

    private static byte[] BuildZip(string entryName, string content)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(content);
        }
        return ms.ToArray();
    }
}
