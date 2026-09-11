// ============================================================================
// ChatToolExecutorLoggingTests — coverage for issue #354.
//
// ChatToolExecutor used to log every tool-call parameter VALUE at Information level —
// the level that lands in the DB log store SysAdmin reads on /logs. attach_file sends
// file_content_base64 (the whole file, Base64-encoded); create_client/update_client/
// send_invoice_email carry PII (name, address, IČO/DIČ, e-mail) in plain text.
//
// Expected behaviour after the fix (issue #354):
//   - Information logs only parameter NAMES (string.Join(", ", parameters.Keys)),
//   - full values go to Debug only, and even there each value is capped at 200 chars
//     so a large payload can never appear whole in any log sink.
// ============================================================================

using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class ChatToolExecutorLoggingTests
{
    private readonly ILogger<ChatToolExecutor> _logger = CreateLogger();

    /// <summary>
    /// IsEnabled(Debug) must return true here — the production code guards its Debug log
    /// behind it, and NSubstitute's default (false) would silently skip that call and make
    /// the truncation test below pass for the wrong reason (no log at all).
    /// </summary>
    private static ILogger<ChatToolExecutor> CreateLogger()
    {
        var logger = Substitute.For<ILogger<ChatToolExecutor>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        return logger;
    }

    // ─── Test helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// "%PDF-1.7" Base64-encoded, then filler — the size a real PDF attachment would produce.
    /// The distinctive prefix lets Information assertions prove that not even the FIRST few
    /// characters leak; a bare run of 'A's could only prove the whole value is absent.
    /// </summary>
    private const string PayloadPrefix = "JVBERi0xLjcK";
    private static readonly string LargeBase64Payload = PayloadPrefix + new string('A', 50_000);

    private static IChatTool CreateAttachFileTool() =>
        CreateTool("attach_file", ChatToolResult.Success("Attached."),
            new ChatToolParameter
            {
                Name = "file_content_base64",
                Type = ChatToolParameterType.String,
                Description = "Base64-encoded file content",
                IsRequired = true
            });

    private static IChatTool CreateTool(string name, ChatToolResult result, params ChatToolParameter[] parameters)
    {
        var tool = Substitute.For<IChatTool>();
        tool.ToolName.Returns(name);
        tool.Description.Returns($"{name} tool");
        tool.Parameters.Returns(parameters);
        tool.ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>()).Returns(result);
        return tool;
    }

    private ChatToolExecutor CreateExecutor(params IChatTool[] tools)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChatToolExecutor(tools, new TenantDbContext(options), _logger);
    }

    /// <summary>
    /// The formatted text of every Log call at the given level — read back from the received
    /// call rather than matched with Arg.Is, so a failing assertion shows the actual wording.
    /// </summary>
    private IReadOnlyList<string> LoggedMessages(LogLevel level) =>
        _logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                            && (LogLevel)call.GetArguments()[0]! == level)
            .Select(call => call.GetArguments()[2]!.ToString()!)
            .ToList();

    // ─── The AC #354 scenario itself: attach_file's Base64 payload ────────

    /// <summary>
    /// Regression test for issue #354, the exact scenario named in the issue: a large
    /// file_content_base64 value must never reach the Information-level log line.
    /// </summary>
    [Fact]
    public async Task ExecuteToolAsync_WithLargeBase64Parameter_DoesNotLogTheValueAtInformation()
    {
        // Arrange
        var executor = CreateExecutor(CreateAttachFileTool());

        // Act
        await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "attach_file",
            Parameters = new Dictionary<string, string> { ["file_content_base64"] = LargeBase64Payload }
        });

        // Assert — the parameter NAME is still there (needed to debug which call ran), no part
        // of the VALUE is, on any Information-level line.
        var infoText = string.Join("\n", LoggedMessages(LogLevel.Information));
        infoText.ShouldContain("file_content_base64");
        infoText.ShouldNotContain(PayloadPrefix);
    }

    /// <summary>
    /// The text-based flow (models without native tool calling) logs from ParseToolCall, a
    /// separate line from ExecuteToolAsync's — it needs its own guard against the same leak.
    /// </summary>
    [Fact]
    public void ParseToolCall_WithLargeBase64Parameter_LogsNamesAtInformationAndTruncatesAtDebug()
    {
        // Arrange
        var executor = CreateExecutor(CreateAttachFileTool());
        var modelResponse =
            $$$"""{"action": "attach_file", "parameters": {"file_content_base64": "{{{LargeBase64Payload}}}"}}""";

        // Act
        var toolCall = executor.ParseToolCall(modelResponse);

        // Assert — parsing itself is untouched, only the logging changed.
        toolCall.ShouldNotBeNull();
        toolCall.Parameters["file_content_base64"].ShouldBe(LargeBase64Payload);

        var infoText = string.Join("\n", LoggedMessages(LogLevel.Information));
        infoText.ShouldContain("file_content_base64");
        infoText.ShouldNotContain(PayloadPrefix);

        var debugText = string.Join("\n", LoggedMessages(LogLevel.Debug));
        debugText.ShouldContain(LargeBase64Payload[..200] + "…");
        debugText.ShouldNotContain(LargeBase64Payload[..201]);
    }

    /// <summary>
    /// The same payload IS allowed at Debug — just never whole. AC #354 asks for a hard cap
    /// (200 chars + "…") so even a Debug-enabled sink cannot receive a multi-megabyte line.
    /// </summary>
    [Fact]
    public async Task ExecuteToolAsync_WithLargeBase64Parameter_TruncatesTheValueAtDebug()
    {
        // Arrange
        var executor = CreateExecutor(CreateAttachFileTool());

        // Act
        await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "attach_file",
            Parameters = new Dictionary<string, string> { ["file_content_base64"] = LargeBase64Payload }
        });

        // Assert — exactly the first 200 chars, then the ellipsis; not one char more.
        var debugText = string.Join("\n", LoggedMessages(LogLevel.Debug));
        debugText.ShouldContain(LargeBase64Payload[..200] + "…");
        debugText.ShouldNotContain(LargeBase64Payload[..201]);
    }

    /// <summary>
    /// An emoji is a surrogate pair (two C# chars). A value whose 200th char is the first half
    /// of one must be cut BEFORE the pair, never through it — a lone half is invalid UTF-16.
    /// </summary>
    [Fact]
    public async Task ExecuteToolAsync_TruncationPoint_InsideSurrogatePair_KeepsThePairWhole()
    {
        // Arrange — chars 0..198 are 'A', chars 199..200 are the emoji's two halves.
        var value = new string('A', 199) + "\U0001F600" + new string('B', 50);
        var executor = CreateExecutor(CreateAttachFileTool());

        // Act
        await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "attach_file",
            Parameters = new Dictionary<string, string> { ["file_content_base64"] = value }
        });

        // Assert — the cut steps back to 199 chars, so the ellipsis directly follows the 'A's.
        var debugText = string.Join("\n", LoggedMessages(LogLevel.Debug));
        debugText.ShouldContain(new string('A', 199) + "…");
    }

    // ─── Invalid parameters (the Warning line) ─────────────────────────────

    /// <summary>
    /// The validation error quotes the rejected value ("got '...'"), and Warning lands in the
    /// DB log store just like Information. So the Warning line may carry parameter NAMES only;
    /// the full error goes to Debug (capped) and, unchanged, back to the model.
    /// </summary>
    [Fact]
    public async Task ExecuteToolAsync_WithInvalidParameterValue_DoesNotLogTheValueAtWarningOrInformation()
    {
        // Arrange — an Integer parameter handed a long, non-numeric, PII-bearing text.
        var invalidValue = "Jan Novák, Dlouhá 12, Praha " + new string('x', 1_000);
        var tool = CreateTool("list_invoices", ChatToolResult.Success("Listed."),
            new ChatToolParameter
            {
                Name = "limit", Type = ChatToolParameterType.Integer,
                Description = "Maximum number of invoices"
            });
        var executor = CreateExecutor(tool);

        // Act
        var result = await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "list_invoices",
            Parameters = new Dictionary<string, string> { ["limit"] = invalidValue }
        });

        // Assert — the model still gets the full explanation, so it can correct the call.
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain(invalidValue);

        var warningText = string.Join("\n", LoggedMessages(LogLevel.Warning));
        warningText.ShouldContain("limit");
        warningText.ShouldNotContain("Jan Novák");
        string.Join("\n", LoggedMessages(LogLevel.Information)).ShouldNotContain("Jan Novák");

        // Debug carries the error for diagnosis, but capped — never the whole 1 000-char value.
        var debugText = string.Join("\n", LoggedMessages(LogLevel.Debug));
        debugText.ShouldContain("…");
        debugText.ShouldNotContain(invalidValue);
    }

    // ─── PII parameters (create_client style) ──────────────────────────────

    /// <summary>
    /// The other half of AC #354: ordinary tools carrying PII (name, address, IČO/DIČ, e-mail)
    /// must not have those values echoed at Information level either — Base64 is the extreme
    /// case, but the rule is about every parameter value, not just large ones.
    /// </summary>
    [Fact]
    public async Task ExecuteToolAsync_WithPiiParameters_LogsOnlyParameterNamesAtInformation()
    {
        // Arrange
        var tool = CreateTool("create_client", ChatToolResult.Success("Client created."),
            new ChatToolParameter
            {
                Name = "company_name", Type = ChatToolParameterType.String,
                Description = "Client company name", IsRequired = true
            },
            new ChatToolParameter
            {
                Name = "email", Type = ChatToolParameterType.String,
                Description = "Client e-mail"
            });
        var executor = CreateExecutor(tool);

        // Act
        await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "create_client",
            Parameters = new Dictionary<string, string>
            {
                ["company_name"] = "Jan Novák s.r.o.",
                ["email"] = "jan.novak@example.cz"
            }
        });

        // Assert
        var infoText = string.Join("\n", LoggedMessages(LogLevel.Information));
        infoText.ShouldContain("company_name");
        infoText.ShouldContain("email");
        infoText.ShouldNotContain("Jan Novák s.r.o.");
        infoText.ShouldNotContain("jan.novak@example.cz");
    }
}
