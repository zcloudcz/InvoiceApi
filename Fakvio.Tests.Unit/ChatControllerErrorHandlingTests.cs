// ============================================================================
// ChatControllerErrorHandlingTests — coverage for issue #156.
//
// ChatController used to send ex.ToString() — the FULL .NET stack trace — to
// the browser, both on the non-streaming endpoint (HTTP 500 body) and on the
// SSE stream ({"error": "..."} payload). The Blazor chat panel rendered it
// verbatim as an assistant message, so internal type names, file paths and
// configuration details ended up on the user's screen.
//
// Expected behaviour after the fix (issue #156):
//   - the full exception goes to ILogger → DatabaseLogger → AppLog (server side),
//   - the client only receives a short, safe message carrying the CorrelationId
//     so the user can quote it when reporting the problem.
// ============================================================================

using System.Security.Claims;
using System.Text.Json;
using Fakvio.API.Controller;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Verifies that both chat error paths (non-streaming HTTP 500 and SSE error event)
/// return a sanitized message with a CorrelationId instead of a raw stack trace.
///
/// Junior note: <c>ex.ToString()</c> renders the exception type, its message, all
/// inner exceptions AND the stack trace (namespaces, method names, source file paths).
/// Sending that to a browser is an information-disclosure bug — the tests below assert
/// that none of those fragments appear in what the client receives.
/// </summary>
public class ChatControllerErrorHandlingTests
{
    private const string TestCorrelationId = "11111111-2222-3333-4444-555555555555";

    private readonly IChatService _chatService = Substitute.For<IChatService>();
    private readonly IPdfTextExtractorService _pdfTextExtractor = Substitute.For<IPdfTextExtractorService>();
    private readonly ILogger<ChatController> _logger = Substitute.For<ILogger<ChatController>>();

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the controller with an authenticated user and a CorrelationId already
    /// present in HttpContext.Items — exactly what CorrelationIdMiddleware does for
    /// every real request before the controller runs.
    /// </summary>
    private ChatController BuildController()
    {
        var controller = new ChatController(_chatService, _pdfTextExtractor, _logger);

        // ChatController.GetCurrentUserId() parses the NameIdentifier claim as long.
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "42") }, "TestAuth");

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        httpContext.Items["CorrelationId"] = TestCorrelationId;

        // A writable body is required so the SSE endpoint can call Response.WriteAsync.
        httpContext.Response.Body = new MemoryStream();

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    /// Creates an exception that already carries a stack trace (throwing and catching it
    /// is the only way to populate <see cref="Exception.StackTrace"/>), wrapped in an
    /// outer exception — the shape the chat pipeline produces in production.
    /// </summary>
    private static Exception CreateRealisticException(string innerMessage)
    {
        try
        {
            try
            {
                throw new InvalidOperationException(innerMessage);
            }
            catch (InvalidOperationException inner)
            {
                throw new HttpRequestException("AI provider call failed.", inner);
            }
        }
        catch (HttpRequestException ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// An async stream that optionally emits one chunk and then fails — the two shapes of
    /// SSE failure: before anything was written, and in the middle of an answer.
    /// </summary>
    private static async IAsyncEnumerable<string> FailingStream(Exception ex, bool emitChunkFirst)
    {
        if (emitChunkFirst)
        {
            yield return "Partial answer";
        }

        await Task.CompletedTask;
        throw ex;
    }

    /// <summary>
    /// Reads back everything the controller wrote into the response body.
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    /// <summary>
    /// Asserts that a client-visible text contains none of the internal details that
    /// <c>ex.ToString()</c> would have leaked.
    /// </summary>
    private static void ShouldNotLeakExceptionDetails(string clientText, Exception ex)
    {
        clientText.ShouldNotContain(ex.GetType().FullName!);
        clientText.ShouldNotContain(ex.Message);
        clientText.ShouldNotContain(ex.InnerException!.Message);
        clientText.ShouldNotContain("   at ");
        clientText.ShouldNotContain(nameof(ChatControllerErrorHandlingTests));
    }

    // ─── Non-streaming endpoint (POST /api/chat/send) ─────────────────────────

    /// <summary>
    /// Regression test for issue #156: an unexpected failure must produce HTTP 500 with a
    /// short message that quotes the CorrelationId — never the exception or its stack trace.
    /// </summary>
    [Fact]
    public async Task SendMessage_WhenServiceThrows_ReturnsSafeMessageWithCorrelationId()
    {
        // Arrange — the AI provider blows up with a detail-rich exception.
        var thrown = CreateRealisticException("Company 7 has no API key for provider 'Claude'.");
        _chatService
            .SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        var controller = BuildController();

        // Act
        var result = await controller.SendMessage(new SendMessageRequest { Message = "Hi" });

        // Assert — HTTP 500 with a sanitized, correlatable message.
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);

        var body = JsonSerializer.Serialize(objectResult.Value);
        ShouldNotLeakExceptionDetails(body, thrown);
        body.ShouldContain(TestCorrelationId);
    }

    /// <summary>
    /// The sanitized response must not silently swallow the failure — the full exception
    /// (stack trace included) has to reach ILogger, which DatabaseLogger persists into AppLog.
    /// </summary>
    [Fact]
    public async Task SendMessage_WhenServiceThrows_LogsFullExceptionServerSide()
    {
        // Arrange
        var thrown = CreateRealisticException("Company 7 has no API key for provider 'Claude'.");
        _chatService
            .SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        var controller = BuildController();

        // Act
        await controller.SendMessage(new SendMessageRequest { Message = "Hi" });

        // Assert — ILogger.Log received the exception object itself (not just its message).
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            thrown,
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// The concrete scenario named in issue #156, on the NON-streaming path.
    ///
    /// <c>CompanyAiSettingsResolver</c> throws a plain
    /// <see cref="InvalidOperationException"/> whose message spells out the CompanyId, the
    /// requested provider and the whole three-tier configuration fallback. The controller used
    /// to echo that message back verbatim (<c>BadRequest(new { message = ex.Message })</c>) and
    /// <c>InvoiceImport.razor</c> renders it as "AI review failed: {message}" — so the whole
    /// configuration dump landed on the user's screen.
    /// </summary>
    [Fact]
    public async Task SendMessage_WhenConfigurationResolverThrows_DoesNotLeakConfigurationDetails()
    {
        // Arrange — the exact message shape thrown by CompanyAiSettingsResolver.
        // Note the type: InvalidOperationException, NOT a generic exception. That is the whole
        // point of this test — the old code had a dedicated catch for this type.
        var thrown = new InvalidOperationException(
            "No AI provider resolved. CompanyId=7, RequestedProvider=Claude. " +
            "Tier 1 (company) → Tier 2 (system DB) → Tier 3 (appsettings.json) all failed. " +
            "Configure AI settings in SysAdmin System Settings or company settings.");

        _chatService
            .SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        var controller = BuildController();

        // Act
        var result = await controller.SendMessage(new SendMessageRequest { Message = "Hi" });

        // Assert — no configuration internals, but a usable reference for the user.
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        var body = JsonSerializer.Serialize(objectResult.Value);

        body.ShouldNotContain("CompanyId");
        body.ShouldNotContain("appsettings");
        body.ShouldNotContain("SysAdmin");
        body.ShouldNotContain(thrown.Message);
        body.ShouldContain(TestCorrelationId);
    }

    /// <summary>
    /// The old <c>catch (InvalidOperationException)</c> branch returned a 400 and logged
    /// nothing at all — the user got the leak and the operator got no record. The exception
    /// must reach ILogger so DatabaseLogger can persist it into AppLog under the CorrelationId
    /// that the user is asked to report.
    /// </summary>
    [Fact]
    public async Task SendMessage_WhenConfigurationResolverThrows_LogsFullExceptionServerSide()
    {
        // Arrange
        var thrown = new InvalidOperationException(
            "No AI provider resolved. CompanyId=7, RequestedProvider=Claude.");

        _chatService
            .SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        var controller = BuildController();

        // Act
        await controller.SendMessage(new SendMessageRequest { Message = "Hi" });

        // Assert — the exception object itself (with its message) reached the log.
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            thrown,
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ─── Unknown / foreign ConversationId (issue #156, round 3) ───────────────

    /// <summary>
    /// A ConversationId that does not resolve is a CLIENT mistake, not a server failure,
    /// so it must answer 404 — the same as GetConversation and DeleteConversation do for
    /// the very same condition. Answering 500 would page the on-call for a stale browser
    /// tab and would turn an IDOR probe into a stream of fake incidents.
    ///
    /// Junior note: the endpoint is a plain JWT-authenticated REST endpoint (also exposed
    /// through Fakvio.Functions), so "our UI only sends IDs it received from the server"
    /// is not a guarantee — any authenticated caller can send any number.
    /// </summary>
    [Fact]
    public async Task SendMessage_WhenConversationNotFound_ReturnsNotFound()
    {
        // Arrange — the service reports the conversation is missing (or belongs to someone else).
        var thrown = new ChatConversationNotFoundException(999);
        _chatService
            .SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        var controller = BuildController();

        // Act
        var result = await controller.SendMessage(
            new SendMessageRequest { Message = "Hi", ConversationId = 999 });

        // Assert — 404, not 500.
        var notFound = result.Result.ShouldBeOfType<NotFoundObjectResult>();
        notFound.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The 404 body must be written by the controller. Echoing <c>ex.Message</c> is the exact
    /// habit that caused issue #156 — and here it would additionally confirm which foreign IDs
    /// exist, because the lookup filters by conversation ID AND user ID at once.
    /// </summary>
    [Fact]
    public async Task SendMessage_WhenConversationNotFound_DoesNotEchoExceptionMessage()
    {
        // Arrange
        var thrown = new ChatConversationNotFoundException(999);
        _chatService
            .SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        var controller = BuildController();

        // Act
        var result = await controller.SendMessage(
            new SendMessageRequest { Message = "Hi", ConversationId = 999 });

        // Assert — controller-authored text only; no exception message, no ID echoed back.
        var notFound = result.Result.ShouldBeOfType<NotFoundObjectResult>();
        var body = JsonSerializer.Serialize(notFound.Value);

        body.ShouldNotContain(thrown.Message);
        body.ShouldNotContain("999");
        body.ShouldContain("Conversation not found.");
    }

    /// <summary>
    /// A missing conversation is a routine client error, so it must not be logged as an Error —
    /// Error level is what alerting and the AppLog error view react to. It also must not fall
    /// through to the catch-all, which would produce the 500 this round of work removes.
    /// </summary>
    [Fact]
    public async Task SendMessage_WhenConversationNotFound_DoesNotLogAsError()
    {
        // Arrange
        _chatService
            .SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ChatConversationNotFoundException(999));

        var controller = BuildController();

        // Act
        await controller.SendMessage(new SendMessageRequest { Message = "Hi", ConversationId = 999 });

        // Assert
        _logger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// GetConversation already answered 404 before this change; the point of the assertion is
    /// that it keeps doing so now that the service throws a dedicated type, and that its body
    /// no longer repeats <c>ex.Message</c>.
    /// </summary>
    [Fact]
    public async Task GetConversation_WhenConversationNotFound_ReturnsNotFoundWithoutExceptionMessage()
    {
        // Arrange
        var thrown = new ChatConversationNotFoundException(999);
        _chatService
            .GetConversationAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        var controller = BuildController();

        // Act
        var result = await controller.GetConversation(999);

        // Assert
        var notFound = result.Result.ShouldBeOfType<NotFoundObjectResult>();
        var body = JsonSerializer.Serialize(notFound.Value);

        body.ShouldNotContain(thrown.Message);
        body.ShouldContain("Conversation not found.");
    }

    /// <summary>
    /// Same guarantee for DeleteConversation — all three endpoints give one answer to
    /// "this conversation does not exist".
    /// </summary>
    [Fact]
    public async Task DeleteConversation_WhenConversationNotFound_ReturnsNotFoundWithoutExceptionMessage()
    {
        // Arrange
        var thrown = new ChatConversationNotFoundException(999);
        _chatService
            .DeleteConversationAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        var controller = BuildController();

        // Act
        var result = await controller.DeleteConversation(999);

        // Assert
        var notFound = result.ShouldBeOfType<NotFoundObjectResult>();
        var body = JsonSerializer.Serialize(notFound.Value);

        body.ShouldNotContain(thrown.Message);
        body.ShouldContain("Conversation not found.");
    }

    // ─── SSE endpoint (POST /api/chat/stream) ─────────────────────────────────

    /// <summary>
    /// Regression test for issue #156: when the stream fails after some text was already
    /// sent, the SSE error event must carry the safe message, not the stack trace.
    /// </summary>
    [Fact]
    public async Task StreamMessage_WhenStreamFailsMidResponse_SendsSafeErrorEvent()
    {
        // Arrange
        var thrown = CreateRealisticException("Company 7 has no API key for provider 'Claude'.");
        _chatService
            .StreamMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(FailingStream(thrown, emitChunkFirst: true));

        var controller = BuildController();

        // Act
        await controller.StreamMessage(new SendMessageRequest { Message = "Hi" });

        // Assert — the error payload is sanitized and correlatable.
        // The whole response body is checked first: asserting only on the "error" property
        // would pass even if the detail leaked through some other property of the same frame.
        var body = await ReadBodyAsync(controller.HttpContext);
        ShouldNotLeakExceptionDetails(body, thrown);

        var error = ExtractSseError(body);
        error.ShouldNotBeNull();
        error!.ShouldContain(TestCorrelationId);
    }

    /// <summary>
    /// The concrete scenario named in issue #156: CompanyAiSettingsResolver throws an
    /// InvalidOperationException whose message spells out the CompanyId, the requested
    /// provider and the whole configuration fallback chain. None of it may reach the client.
    /// </summary>
    [Fact]
    public async Task StreamMessage_WhenConfigurationResolverThrows_DoesNotLeakConfigurationDetails()
    {
        // Arrange — the message shape thrown by CompanyAiSettingsResolver.
        var thrown = new InvalidOperationException(
            "No AI provider configured for CompanyId 7. Requested provider 'Claude'. " +
            "Tier 1 (company settings) empty, tier 2 (system settings) empty, tier 3 (appsettings) empty.");

        _chatService
            .StreamMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(FailingStream(thrown, emitChunkFirst: false));

        var controller = BuildController();

        // Act
        await controller.StreamMessage(new SendMessageRequest { Message = "Hi" });

        // Assert — no configuration internals anywhere in the stream, but a usable
        // reference for the user in the error frame.
        var body = await ReadBodyAsync(controller.HttpContext);
        body.ShouldNotContain("CompanyId");
        body.ShouldNotContain("appsettings");
        body.ShouldNotContain(thrown.Message);

        var error = ExtractSseError(body);
        error.ShouldNotBeNull();
        error!.ShouldContain(TestCorrelationId);
    }

    /// <summary>
    /// Same server-side logging guarantee as for the non-streaming path.
    /// </summary>
    [Fact]
    public async Task StreamMessage_WhenStreamFails_LogsFullExceptionServerSide()
    {
        // Arrange
        var thrown = CreateRealisticException("Company 7 has no API key for provider 'Claude'.");
        _chatService
            .StreamMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(FailingStream(thrown, emitChunkFirst: false));

        var controller = BuildController();

        // Act
        await controller.StreamMessage(new SendMessageRequest { Message = "Hi" });

        // Assert
        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            thrown,
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// The "provider answered with nothing" branch is not an exception, but it is still an
    /// error the user sees. USERGUIDE §13 promises a reference ID on every chat failure,
    /// so this payload has to carry the CorrelationId as well.
    /// </summary>
    [Fact]
    public async Task StreamMessage_WhenProviderReturnsNoContent_IncludesReferenceId()
    {
        // Arrange — a stream that completes normally but never yields any text.
        _chatService
            .StreamMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyStream());

        var controller = BuildController();

        // Act
        await controller.StreamMessage(new SendMessageRequest { Message = "Hi" });

        // Assert
        var error = ExtractSseError(await ReadBodyAsync(controller.HttpContext));

        error.ShouldNotBeNull();
        error!.ShouldContain(TestCorrelationId);
    }

    /// <summary>
    /// The SSE endpoint resolves the conversation through the same lookup as SendMessage
    /// (<c>ChatService.GetOrCreateConversationAsync</c>), and it is the endpoint the chat panel
    /// really uses — so a conversation deleted in another browser tab surfaces here first.
    ///
    /// The client must get the controller's own "not found" text, not the sanitized
    /// "something went wrong, here is a reference ID" message: there is no server-side incident
    /// to look up. This is also what USERGUIDE §13 documents for this one message.
    /// </summary>
    [Fact]
    public async Task StreamMessage_WhenConversationNotFound_SendsNotFoundEventWithoutReferenceId()
    {
        // Arrange — a stale ConversationId, the shape ChatService throws for it.
        var thrown = new ChatConversationNotFoundException(999);
        _chatService
            .StreamMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(FailingStream(thrown, emitChunkFirst: false));

        var controller = BuildController();

        // Act
        await controller.StreamMessage(
            new SendMessageRequest { Message = "Hi", ConversationId = 999 });

        // Assert — check the whole frame first, so a leak through another property cannot hide.
        var body = await ReadBodyAsync(controller.HttpContext);
        body.ShouldNotContain(thrown.Message);
        body.ShouldNotContain("999");
        body.ShouldNotContain(TestCorrelationId);

        var error = ExtractSseError(body);
        error.ShouldNotBeNull();
        error!.ShouldBe("Conversation not found.");
    }

    /// <summary>
    /// The same operational point as on the non-streaming path: a stale conversation ID is a
    /// routine client mistake. Logging it as an Error would page the on-call engineer and would
    /// let anyone probing foreign IDs manufacture incidents.
    /// </summary>
    [Fact]
    public async Task StreamMessage_WhenConversationNotFound_DoesNotLogAsError()
    {
        // Arrange
        _chatService
            .StreamMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(FailingStream(new ChatConversationNotFoundException(999), emitChunkFirst: false));

        var controller = BuildController();

        // Act
        await controller.StreamMessage(
            new SendMessageRequest { Message = "Hi", ConversationId = 999 });

        // Assert
        _logger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// A stream that completes without yielding a single chunk — what a silently failing
    /// AI provider looks like from the controller's point of view.
    /// </summary>
    private static async IAsyncEnumerable<string> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// Pulls the "error" property out of the first SSE event that carries one.
    /// SSE frames look like: <c>data: {"error":"...","correlationId":"..."}\n\n</c>
    /// </summary>
    private static string? ExtractSseError(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data: {"))
                continue;

            using var doc = JsonDocument.Parse(line["data: ".Length..]);
            if (doc.RootElement.TryGetProperty("error", out var errorProp))
                return errorProp.GetString();
        }

        return null;
    }
}
