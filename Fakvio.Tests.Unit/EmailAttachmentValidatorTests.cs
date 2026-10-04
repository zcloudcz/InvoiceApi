using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Email;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="EmailAttachmentValidator"/> - the trust-boundary check for extra invoice
/// email attachments supplied as base64 by API / MCP callers.
/// </summary>
public class EmailAttachmentValidatorTests
{
    private static EmailAttachmentDto File(string name, byte[]? content = null) => new()
    {
        FileName = name,
        ContentBase64 = Convert.ToBase64String(content ?? [1, 2, 3, 4])
    };

    [Fact]
    public void ValidateAndDecode_NullOrEmpty_ReturnsEmptyList()
    {
        EmailAttachmentValidator.ValidateAndDecode(null).ShouldBeEmpty();
        EmailAttachmentValidator.ValidateAndDecode([]).ShouldBeEmpty();
    }

    [Fact]
    public void ValidateAndDecode_HappyPath_DecodesNamesAndContent()
    {
        var result = EmailAttachmentValidator.ValidateAndDecode(
        [
            File("Contract.PDF", [10, 20, 30]),
            File("photo.jpeg", [40, 50])
        ]);

        result.Count.ShouldBe(2);
        result[0].FileName.ShouldBe("Contract.PDF"); // extension check is case-insensitive
        result[0].Content.ShouldBe(new byte[] { 10, 20, 30 });
        result[1].Content.ShouldBe(new byte[] { 40, 50 });
    }

    [Fact]
    public void ValidateAndDecode_MoreThanFiveFiles_Throws()
    {
        var files = Enumerable.Range(1, 6).Select(i => File($"f{i}.txt")).ToList();

        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode(files))
            .Message.ShouldContain("At most 5");
    }

    [Fact]
    public void ValidateAndDecode_ExactlyFiveFiles_IsAccepted()
    {
        var files = Enumerable.Range(1, 5).Select(i => File($"f{i}.txt")).ToList();

        EmailAttachmentValidator.ValidateAndDecode(files).Count.ShouldBe(5);
    }

    [Fact]
    public void ValidateAndDecode_TotalOverTenMegabytes_Throws()
    {
        // Two files of 6 MB each = 12 MB decoded > 10 MB limit.
        var big = new byte[6 * 1024 * 1024];

        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode(
                [File("a.zip", big), File("b.zip", big)]))
            .Message.ShouldContain("too large");
    }

    [Fact]
    public void ValidateAndDecode_HugeBase64_IsRejectedBeforeDecoding()
    {
        // 14 million characters of invalid base64: if the size check ran after decoding, we would
        // get "not valid base64" instead of "too large" - proving the length check goes first.
        var dto = new EmailAttachmentDto { FileName = "a.zip", ContentBase64 = new string('!', 14_000_000) };

        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode([dto]))
            .Message.ShouldContain("too large");
    }

    [Fact]
    public void ValidateAndDecode_InvalidBase64_Throws()
    {
        var dto = new EmailAttachmentDto { FileName = "a.pdf", ContentBase64 = "not base64 at all!!" };

        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode([dto]))
            .Message.ShouldContain("base64");
    }

    [Fact]
    public void ValidateAndDecode_EmptyContent_Throws()
    {
        var dto = new EmailAttachmentDto { FileName = "a.pdf", ContentBase64 = "" };

        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode([dto]));
    }

    [Theory]
    [InlineData("virus.exe")]
    [InlineData("page.html")]
    [InlineData("script.js")]
    [InlineData("noextension")]
    public void ValidateAndDecode_BlockedExtension_Throws(string name)
    {
        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode([File(name)]))
            .Message.ShouldContain("not allowed");
    }

    [Theory]
    [InlineData("../../evil.pdf")]
    [InlineData("..\\..\\evil.pdf")]
    [InlineData("/etc/passwd.txt")]
    [InlineData("sub/dir.pdf")]
    public void ValidateAndDecode_PathInFileName_Throws(string name)
    {
        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode([File(name)]))
            .Message.ShouldContain("path");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateAndDecode_EmptyFileName_Throws(string name)
    {
        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode([File(name)]));
    }

    [Fact]
    public void ValidateAndDecode_ControlCharactersInName_Throws()
    {
        // CR/LF in a file name could inject MIME headers.
        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode([File("a\r\nBcc: x@y.cz.pdf")]))
            .Message.ShouldContain("control");
    }

    [Fact]
    public void ValidateAndDecode_RightToLeftOverrideInName_Throws()
    {
        // U+202E would make "x‮fdp.zip" render as "xpiz.pdf" and disguise the real extension.
        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode([File("x‮fdp.zip")]))
            .Message.ShouldContain("control");
    }

    [Fact]
    public void ValidateAndDecode_FileNameLongerThan255_Throws()
    {
        var name = new string('a', 252) + ".pdf"; // 256 chars

        Should.Throw<ArgumentException>(() => EmailAttachmentValidator.ValidateAndDecode([File(name)]))
            .Message.ShouldContain("255");
    }
}
