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
        // Arrange — a payload the size a real PDF attachment would produce.
        var base64Payload = new string('A', 50_000);
        var tool = CreateTool("attach_file", ChatToolResult.Success("Attached."),
            new ChatToolParameter
            {
                Name = "file_content_base64",
                Type = ChatToolParameterType.String,
                Description = "Base64-encoded file content",
                IsRequired = true
            });
        var executor = CreateExecutor(tool);

        // Act
        await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "attach_file",
            Parameters = new Dictionary<string, string> { ["file_content_base64"] = base64Payload }
        });

        // Assert — the parameter NAME is still there (needed to debug which call ran), the
        // VALUE is not, on any Information-level line.
        var infoText = string.Join("\n", LoggedMessages(LogLevel.Information));
        infoText.ShouldContain("file_content_base64");
        infoText.ShouldNotContain(base64Payload);
    }

    /// <summary>
    /// The same payload IS allowed at Debug — just never whole. AC #354 asks for a hard cap
    /// (200 chars + "…") so even a Debug-enabled sink cannot receive a multi-megabyte line.
    /// </summary>
    [Fact]
    public async Task ExecuteToolAsync_WithLargeBase64Parameter_TruncatesTheValueAtDebug()
    {
        // Arrange
        var base64Payload = new string('A', 50_000);
        var tool = CreateTool("attach_file", ChatToolResult.Success("Attached."),
            new ChatToolParameter
            {
                Name = "file_content_base64",
                Type = ChatToolParameterType.String,
                Description = "Base64-encoded file content",
                IsRequired = true
            });
        var executor = CreateExecutor(tool);

        // Act
        await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "attach_file",
            Parameters = new Dictionary<string, string> { ["file_content_base64"] = base64Payload }
        });

        // Assert
        var debugText = string.Join("\n", LoggedMessages(LogLevel.Debug));
        debugText.ShouldNotContain(base64Payload);
        debugText.ShouldContain(new string('A', 200) + "…");
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
