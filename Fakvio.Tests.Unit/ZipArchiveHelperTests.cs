using System.IO.Compression;
using Fakvio.Application.Common.Helpers;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ZipArchiveHelper — entry name deduplication,
/// path segment sanitization, and ZIP creation round-trip.
/// </summary>
public class ZipArchiveHelperTests
{
    // ── UniqueEntryName ──────────────────────────────────────────────

    [Fact]
    public void UniqueEntryName_FirstOccurrence_ReturnsOriginalName()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = ZipArchiveHelper.UniqueEntryName(used, "scan.pdf");

        result.ShouldBe("scan.pdf");
    }

    [Fact]
    public void UniqueEntryName_Duplicates_GetNumericSuffixBeforeExtension()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ZipArchiveHelper.UniqueEntryName(used, "scan.pdf").ShouldBe("scan.pdf");
        ZipArchiveHelper.UniqueEntryName(used, "scan.pdf").ShouldBe("scan (2).pdf");
        ZipArchiveHelper.UniqueEntryName(used, "scan.pdf").ShouldBe("scan (3).pdf");
    }

    [Fact]
    public void UniqueEntryName_IsCaseInsensitive()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ZipArchiveHelper.UniqueEntryName(used, "Scan.PDF").ShouldBe("Scan.PDF");
        // Windows extractors treat "scan.pdf" as the same file — must dedupe
        ZipArchiveHelper.UniqueEntryName(used, "scan.pdf").ShouldBe("scan (2).pdf");
    }

    [Fact]
    public void UniqueEntryName_FileWithoutExtension_SuffixAppended()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ZipArchiveHelper.UniqueEntryName(used, "readme").ShouldBe("readme");
        ZipArchiveHelper.UniqueEntryName(used, "readme").ShouldBe("readme (2)");
    }

    // ── SanitizePathSegment ──────────────────────────────────────────

    [Theory]
    [InlineData("FAK-2026-001", "FAK-2026-001")] // clean value passes through
    [InlineData("FA/2026:001", "FA_2026_001")]   // path separators and colon replaced
    [InlineData(@"a\b*c?", "a_b_c_")]            // backslash and wildcards replaced
    public void SanitizePathSegment_ReplacesIllegalCharacters(string input, string expected)
    {
        ZipArchiveHelper.SanitizePathSegment(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizePathSegment_EmptyInput_ReturnsUnderscore(string? input)
    {
        ZipArchiveHelper.SanitizePathSegment(input).ShouldBe("_");
    }

    // ── CreateZip ────────────────────────────────────────────────────

    [Fact]
    public void CreateZip_RoundTrip_PreservesEntriesAndContent()
    {
        var contentA = new byte[] { 1, 2, 3 };
        var contentB = new byte[] { 4, 5 };

        var zipBytes = ZipArchiveHelper.CreateZip(new[]
        {
            ("FAK-001/scan.pdf", contentA),
            ("FAK-002/photo.jpg", contentB)
        });

        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        archive.Entries.Count.ShouldBe(2);

        var entryA = archive.GetEntry("FAK-001/scan.pdf");
        entryA.ShouldNotBeNull();
        using var streamA = new MemoryStream();
        entryA!.Open().CopyTo(streamA);
        streamA.ToArray().ShouldBe(contentA);

        archive.GetEntry("FAK-002/photo.jpg").ShouldNotBeNull();
    }

    [Fact]
    public void CreateZip_EmptyInput_ReturnsValidEmptyArchive()
    {
        var zipBytes = ZipArchiveHelper.CreateZip(Array.Empty<(string, byte[])>());

        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        archive.Entries.ShouldBeEmpty();
    }
}
