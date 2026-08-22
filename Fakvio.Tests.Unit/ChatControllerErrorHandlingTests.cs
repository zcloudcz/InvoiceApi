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

using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fakvio.API.Controller;
using Fakvio.API.Middleware;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// <param name="correlationId">
    /// Value the middleware would have stored. Pass null to simulate a request that never
    /// went through CorrelationIdMiddleware — the controller then falls back to "unknown".
    /// </param>
    private ChatController BuildController(string? correlationId = TestCorrelationId)
    {
        var controller = new ChatController(_chatService, _pdfTextExtractor, _logger);

        // ChatController.GetCurrentUserId() parses the NameIdentifier claim as long.
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "42") }, "TestAuth");

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };
        if (correlationId != null)
        {
            httpContext.Items["CorrelationId"] = correlationId;
        }

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

    // ═══════════════════════════════════════════════════════════════════════════
    // The error surface as a whole — added in the test round for issue #156.
    //
    // Why a second block of tests: all three review rounds on this fix turned on the
    // same question, "which path does the client actually take?". Each round closed
    // the branch that was being discussed (first the non-streaming catch, then the
    // very same conversation lookup on the SSE path) and left an equivalent one open.
    // The tests below stop treating those branches as separate stories:
    //
    //   1. ChatEndpoint is the COMPLETE inventory of the controller's endpoints, and a
    //      reflection guard fails when the controller gains or loses one.
    //   2. Every endpoint must be classified as either "handles its own failures" or
    //      "lets them propagate to GlobalExceptionMiddleware" — a second guard fails
    //      when one is left unclassified.
    //   3. The theories below run the error contract against those classified sets.
    //
    // A fourth variant of "that branch was forgotten" therefore cannot be added
    // silently: the guards go red before anyone has to spot the pattern by hand.
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Conversation ID used for every "this conversation is gone" scenario.</summary>
    private const long MissingConversationId = 999;

    /// <summary>
    /// The only sentence a client may receive when a conversation cannot be resolved.
    /// Mirrors the private constant in ChatController — if the two ever diverge the theories
    /// below fail, which is the intent: USERGUIDE §13 quotes this text word for word.
    /// </summary>
    private const string ConversationNotFoundText = "Conversation not found.";

    /// <summary>
    /// Every endpoint ChatController exposes. Kept honest by
    /// <see cref="ChatController_ExposesExactlyTheEndpointsInTheInventory"/>.
    /// </summary>
    public enum ChatEndpoint
    {
        GetConversations,
        GetConversation,
        SendMessage,
        StreamMessage,
        DeleteConversation,
        GetProviders,
        ExtractPdfText
    }

    /// <summary>
    /// Endpoints that catch failures themselves and write the response body. Their contract:
    /// nothing from the exception may appear in what the client receives.
    /// </summary>
    private static readonly ChatEndpoint[] SelfHandlingEndpoints =
    {
        ChatEndpoint.SendMessage,
        ChatEndpoint.StreamMessage,
        ChatEndpoint.ExtractPdfText
    };

    /// <summary>
    /// Endpoints that deliberately have no catch-all: an infrastructure failure has to reach
    /// GlobalExceptionMiddleware, which owns the sanitized 500 body for the whole API
    /// (DEVGUIDE §10.5). Writing their own body would mean a second, unreviewed error shape.
    /// </summary>
    private static readonly ChatEndpoint[] PropagatingEndpoints =
    {
        ChatEndpoint.GetConversations,
        ChatEndpoint.GetConversation,
        ChatEndpoint.DeleteConversation,
        ChatEndpoint.GetProviders
    };

    /// <summary>Endpoints that resolve a conversation and can therefore report it missing.</summary>
    private static readonly ChatEndpoint[] ConversationLookupEndpoints =
    {
        ChatEndpoint.SendMessage,
        ChatEndpoint.StreamMessage,
        ChatEndpoint.GetConversation,
        ChatEndpoint.DeleteConversation
    };

    public static IEnumerable<object[]> SelfHandlingCases => ToTheoryCases(SelfHandlingEndpoints);

    public static IEnumerable<object[]> PropagatingCases => ToTheoryCases(PropagatingEndpoints);

    public static IEnumerable<object[]> ConversationLookupCases => ToTheoryCases(ConversationLookupEndpoints);

    private static IEnumerable<object[]> ToTheoryCases(IEnumerable<ChatEndpoint> endpoints)
        => endpoints.Select(endpoint => new object[] { endpoint });

    // ─── Guards: the inventory cannot go stale ────────────────────────────────

    /// <summary>
    /// Reflection guard: <see cref="ChatEndpoint"/> must list exactly the action methods the
    /// controller declares. Adding an endpoint without adding it here fails this test, which is
    /// the point — the author is then forced to say how the new endpoint reports failures.
    /// </summary>
    [Fact]
    public void ChatController_ExposesExactlyTheEndpointsInTheInventory()
    {
        // Action methods are the public instance methods carrying [HttpGet] / [HttpPost] / …,
        // all of which derive from HttpMethodAttribute. DeclaredOnly skips ControllerBase members.
        var actionMethods = typeof(ChatController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any())
            .Select(method => method.Name)
            .ToList();

        actionMethods.ShouldBe(Enum.GetNames<ChatEndpoint>(), ignoreOrder: true,
            customMessage: "ChatController gained or lost an endpoint. Add it to the ChatEndpoint " +
                           "inventory and classify it, so its error contract gets covered too.");
    }

    /// <summary>
    /// Every endpoint in the inventory must be classified exactly once — either it answers its own
    /// failures (and is checked for leaks) or it propagates them (and is checked for that).
    /// </summary>
    [Fact]
    public void EveryEndpointInTheInventory_IsClassifiedExactlyOnce()
    {
        var classified = SelfHandlingEndpoints.Concat(PropagatingEndpoints).ToList();

        classified.ShouldBeUnique();
        classified.ShouldBe(Enum.GetValues<ChatEndpoint>(), ignoreOrder: true,
            customMessage: "Classify the endpoint as self-handling or propagating — see DEVGUIDE §10.5.");
    }

    // ─── The contract, endpoint by endpoint ───────────────────────────────────

    /// <summary>
    /// The invariant behind issue #156, stated once for every endpoint that builds its own error
    /// response: what the client receives may not contain the exception type, its message, the
    /// message of an inner exception, or a stack frame.
    /// </summary>
    [Theory]
    [MemberData(nameof(SelfHandlingCases))]
    public async Task SelfHandlingEndpoint_WhenDependencyFails_SendsNothingFromTheException(
        ChatEndpoint endpoint)
    {
        // Arrange & Act — a wrapped exception with a real stack trace, the shape production produces.
        var thrown = CreateRealisticException("Company 7 has no API key for provider 'Claude'.");

        var response = await InvokeWithFailingDependencyAsync(endpoint, thrown);

        // Assert — sanitized, and still saying something the user can act on.
        ShouldNotLeakExceptionDetails(response.RawBody, thrown);
        response.Message.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The counterpart: endpoints without a catch-all must let the failure travel to
    /// GlobalExceptionMiddleware untouched. If one of them ever starts writing its own body, this
    /// test fails and the new error shape gets reviewed instead of appearing unnoticed.
    /// </summary>
    [Theory]
    [MemberData(nameof(PropagatingCases))]
    public async Task PropagatingEndpoint_WhenDependencyFails_LeavesTheResponseToTheGlobalMiddleware(
        ChatEndpoint endpoint)
    {
        // Arrange & Act
        var thrown = CreateRealisticException("Database connection failed for tenant 7.");

        var caught = await Should.ThrowAsync<HttpRequestException>(
            () => InvokeWithFailingDependencyAsync(endpoint, thrown));

        // Assert — the very same exception object, neither swallowed nor re-wrapped.
        caught.ShouldBeSameAs(thrown);
    }

    /// <summary>
    /// One condition, one answer — on all four paths that resolve a conversation, including the
    /// SSE stream the chat panel really uses. This is the guarantee the third review round asked
    /// for: REST and SSE, GET and DELETE, all say the same controller-authored sentence.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConversationLookupCases))]
    public async Task ConversationLookupEndpoint_WhenConversationIsMissing_UsesTheOneAuthoredText(
        ChatEndpoint endpoint)
    {
        // Arrange & Act
        var thrown = new ChatConversationNotFoundException(MissingConversationId);

        var response = await InvokeWithFailingDependencyAsync(endpoint, thrown);

        // Assert — the exact constant, not ex.Message and not a per-endpoint variation.
        response.Message.ShouldBe(ConversationNotFoundText);

        // The ID is not echoed back (that would confirm which foreign IDs exist — the lookup
        // filters by conversation ID AND user ID at once), and there is no reference ID: nothing
        // was recorded as an incident. USERGUIDE §13 documents this as the one chat message
        // that comes without one.
        response.RawBody.ShouldNotContain(MissingConversationId.ToString());
        response.RawBody.ShouldNotContain(TestCorrelationId);
        response.RawBody.ShouldNotContain(thrown.Message);

        // SSE cannot set a status code — the headers are already on the wire — so it is the one
        // path where "the same answer" means the same text and log level instead of the same code.
        if (response.StatusCode is not null)
        {
            response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        }
    }

    /// <summary>
    /// A stale conversation ID is a routine client mistake on every path. Logging it at Error
    /// level would page the on-call engineer for a browser tab left open, and would let anyone
    /// probing foreign IDs manufacture incidents at will.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConversationLookupCases))]
    public async Task ConversationLookupEndpoint_WhenConversationIsMissing_IsNotLoggedAsAnIncident(
        ChatEndpoint endpoint)
    {
        // Arrange & Act
        await InvokeWithFailingDependencyAsync(
            endpoint, new ChatConversationNotFoundException(MissingConversationId));

        // Assert
        _logger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ─── SSE edge cases the endpoint has to tell apart ────────────────────────

    /// <summary>
    /// A client that closes the tab mid-answer cancels the stream. That is not a failure: no error
    /// frame may be written (nobody is listening) and nothing may be logged at Error level.
    ///
    /// This pins the ORDER of the catch blocks. Moving the catch-all in front of
    /// <see cref="OperationCanceledException"/> would turn every disconnect into a logged
    /// incident — the same class of mistake issue #156 is about, just in the other direction.
    /// </summary>
    [Fact]
    public async Task StreamMessage_WhenClientDisconnects_SendsNoErrorFrameAndDoesNotAlert()
    {
        // Arrange — one chunk goes out, then the client is gone.
        _chatService
            .StreamMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(FailingStream(new OperationCanceledException(), emitChunkFirst: true));

        var controller = BuildController();

        // Act
        await controller.StreamMessage(new SendMessageRequest { Message = "Hi" });

        // Assert
        ExtractSseError(await ReadBodyAsync(controller.HttpContext)).ShouldBeNull();

        _logger.DidNotReceive().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ─── The reference ID itself ──────────────────────────────────────────────

    /// <summary>
    /// CorrelationIdMiddleware normally fills HttpContext.Items before the controller runs. If it
    /// ever does not — a misordered pipeline, a direct call — the endpoint must still answer with
    /// a sanitized message instead of throwing a NullReferenceException on top of the first error.
    /// </summary>
    [Fact]
    public async Task SendMessage_WhenCorrelationIdIsMissing_StillAnswersWithASanitizedMessage()
    {
        // Arrange
        var thrown = CreateRealisticException("Company 7 has no API key for provider 'Claude'.");
        _chatService
            .SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        var controller = BuildController(correlationId: null);

        // Act
        var result = await controller.SendMessage(new SendMessageRequest { Message = "Hi" });

        // Assert — the documented "unknown" fallback, and still no leak.
        var body = JsonSerializer.Serialize(result.Result.ShouldBeOfType<ObjectResult>().Value);
        ShouldNotLeakExceptionDetails(body, thrown);
        body.ShouldContain("unknown");
    }

    /// <summary>
    /// Where the reference ID comes from, end to end: CorrelationIdMiddleware takes the inbound
    /// <c>X-Correlation-Id</c> header as-is — no length limit, no character set, no format check —
    /// and the controller quotes that value in the message the user sees.
    ///
    /// So the reference ID is CALLER-CONTROLLED TEXT. Nothing is broken today (the chat bubble
    /// renders it as plain text), but anything that starts rendering chat errors as rich content
    /// has to treat it as untrusted input — see PR #185, which adds a markdown renderer for chat
    /// messages. If validation is ever added to the middleware, this is the test that has to be
    /// updated, deliberately.
    /// </summary>
    [Fact]
    public async Task SendMessage_ReferenceId_IsTheInboundHeaderVerbatim_AndIsNotValidated()
    {
        // Arrange — a header value no legitimate client would ever send.
        const string callerSuppliedId = "[click here](javascript:alert(1))";

        var thrown = CreateRealisticException("Company 7 has no API key for provider 'Claude'.");
        _chatService
            .SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(thrown);

        // No CorrelationId pre-seeded — the middleware under test is what fills it in.
        var controller = BuildController(correlationId: null);
        controller.HttpContext.Request.Headers[CorrelationIdMiddleware.HeaderName] = callerSuppliedId;

        ActionResult<SendMessageResponse>? result = null;
        var middleware = new CorrelationIdMiddleware(async _ =>
            result = await controller.SendMessage(new SendMessageRequest { Message = "Hi" }));

        // Act — run the real middleware over the controller's own HttpContext.
        await middleware.InvokeAsync(controller.HttpContext);

        // Assert — the caller's string is handed back to the user untouched.
        var body = JsonSerializer.Serialize(result!.Result.ShouldBeOfType<ObjectResult>().Value);
        body.ShouldContain(callerSuppliedId);
    }

    // ─── Server frame → real client parser ────────────────────────────────────

    /// <summary>
    /// The bytes the controller writes are replayed through the real client parser
    /// (<see cref="ChatApiService.StreamMessageAsync"/>), the one ChatPanel consumes. Reading both
    /// sides and concluding that they match is what the earlier rounds did; this executes it.
    ///
    /// Completing the <c>await foreach</c> is itself an assertion: the not-found frame is not
    /// followed by <c>[DONE]</c>, and the client still has to terminate rather than hang.
    /// </summary>
    [Fact]
    public async Task StreamMessage_NotFoundFrame_ReachesTheRealClientAsAnErrorEvent()
    {
        // Arrange & Act
        var sseBody = await ProduceSseBodyAsync(new ChatConversationNotFoundException(MissingConversationId));

        var events = await CollectClientEventsAsync(sseBody);

        // Assert — one error event carrying exactly the controller's text (ChatPanel renders
        // evt.Error verbatim), with no stray text chunk before it.
        var single = events.ShouldHaveSingleItem();
        single.IsError.ShouldBeTrue();
        single.Error.ShouldBe(ConversationNotFoundText);
    }

    /// <summary>
    /// The same round trip for an infrastructure failure: the client sees the sanitized sentence
    /// with the reference ID, and none of the exception detail issue #156 was about.
    /// </summary>
    [Fact]
    public async Task StreamMessage_SanitizedErrorFrame_ReachesTheRealClientAsAnErrorEvent()
    {
        // Arrange & Act
        var thrown = CreateRealisticException("Company 7 has no API key for provider 'Claude'.");

        var sseBody = await ProduceSseBodyAsync(thrown);
        var events = await CollectClientEventsAsync(sseBody);

        // Assert
        var single = events.ShouldHaveSingleItem();
        single.IsError.ShouldBeTrue();
        single.Error!.ShouldContain(TestCorrelationId);
        ShouldNotLeakExceptionDetails(single.Error, thrown);
    }

    // ─── Helpers for the endpoint-wide theories ───────────────────────────────

    /// <summary>What the caller ends up with, normalized across the REST endpoints and the SSE stream.</summary>
    /// <param name="StatusCode">HTTP status code; null for SSE, which cannot set one.</param>
    /// <param name="RawBody">Everything the client receives — the whole JSON body or the whole stream.</param>
    /// <param name="Message">The single user-facing sentence pulled out of that body.</param>
    private sealed record ClientVisibleResponse(int? StatusCode, string RawBody, string? Message);

    /// <summary>
    /// Makes the endpoint's dependency fail with <paramref name="thrown"/>, calls the endpoint and
    /// returns what the client would receive.
    /// </summary>
    private async Task<ClientVisibleResponse> InvokeWithFailingDependencyAsync(
        ChatEndpoint endpoint, Exception thrown)
    {
        ArrangeFailure(endpoint, thrown);
        return await InvokeAsync(BuildController(), endpoint);
    }

    /// <summary>
    /// Dispatch table: which substituted dependency has to fail for each endpoint. One line per
    /// endpoint by nature, and deliberately exhaustive — an unhandled enum value throws instead of
    /// quietly producing a passing test.
    /// </summary>
    private void ArrangeFailure(ChatEndpoint endpoint, Exception thrown)
    {
        switch (endpoint)
        {
            case ChatEndpoint.GetConversations:
                _chatService.GetConversationsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(thrown);
                break;
            case ChatEndpoint.GetConversation:
                _chatService.GetConversationAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(thrown);
                break;
            case ChatEndpoint.SendMessage:
                _chatService.SendMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(thrown);
                break;
            case ChatEndpoint.StreamMessage:
                _chatService.StreamMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
                    .Returns(FailingStream(thrown, emitChunkFirst: false));
                break;
            case ChatEndpoint.DeleteConversation:
                _chatService.DeleteConversationAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(thrown);
                break;
            case ChatEndpoint.GetProviders:
                _chatService.GetAvailableProvidersAsync(Arg.Any<CancellationToken>())
                    .ThrowsAsync(thrown);
                break;
            case ChatEndpoint.ExtractPdfText:
                _pdfTextExtractor.ExtractTextAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(thrown);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unclassified endpoint.");
        }
    }

    /// <summary>Calls the endpoint and normalizes its answer into a <see cref="ClientVisibleResponse"/>.</summary>
    private static async Task<ClientVisibleResponse> InvokeAsync(ChatController controller, ChatEndpoint endpoint)
    {
        var request = new SendMessageRequest { Message = "Hi", ConversationId = MissingConversationId };

        return endpoint switch
        {
            ChatEndpoint.GetConversations => FromRest((await controller.GetConversations()).Result),
            ChatEndpoint.GetConversation => FromRest((await controller.GetConversation(MissingConversationId)).Result),
            ChatEndpoint.SendMessage => FromRest((await controller.SendMessage(request)).Result),
            ChatEndpoint.StreamMessage => await FromSseAsync(controller, request),
            ChatEndpoint.DeleteConversation => FromRest(await controller.DeleteConversation(MissingConversationId)),
            ChatEndpoint.GetProviders => FromRest((await controller.GetProviders()).Result),
            ChatEndpoint.ExtractPdfText => FromRest(await controller.ExtractPdfText(CreatePdfUpload())),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unclassified endpoint.")
        };
    }

    private static ClientVisibleResponse FromRest(IActionResult? result)
    {
        // NotFoundObjectResult and the plain 500 ObjectResult share this base type.
        var objectResult = result.ShouldBeAssignableTo<ObjectResult>()!;
        var rawBody = JsonSerializer.Serialize(objectResult.Value);

        return new ClientVisibleResponse(objectResult.StatusCode, rawBody, ReadJsonProperty(rawBody, "message"));
    }

    private static async Task<ClientVisibleResponse> FromSseAsync(
        ChatController controller, SendMessageRequest request)
    {
        await controller.StreamMessage(request);
        var body = await ReadBodyAsync(controller.HttpContext);

        return new ClientVisibleResponse(StatusCode: null, body, ExtractSseError(body));
    }

    private static string? ReadJsonProperty(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }

    /// <summary>A minimal upload that gets past the controller's own extension and size checks.</summary>
    private static IFormFile CreatePdfUpload()
    {
        var content = new MemoryStream("%PDF-1.7"u8.ToArray());
        return new FormFile(content, baseStreamOffset: 0, length: content.Length,
            name: "file", fileName: "invoice.pdf");
    }

    /// <summary>Runs the SSE endpoint against a failing stream and returns the raw bytes written.</summary>
    private async Task<string> ProduceSseBodyAsync(Exception thrown)
    {
        _chatService
            .StreamMessageAsync(Arg.Any<long>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(FailingStream(thrown, emitChunkFirst: false));

        var controller = BuildController();
        await controller.StreamMessage(
            new SendMessageRequest { Message = "Hi", ConversationId = MissingConversationId });

        return await ReadBodyAsync(controller.HttpContext);
    }

    /// <summary>
    /// Replays a server-produced SSE body through the real Blazor client service, wired the way DI
    /// wires it (named "InvoiceAPI" HttpClient), and collects the events ChatPanel would see.
    /// </summary>
    private static async Task<List<ChatStreamEvent>> CollectClientEventsAsync(string sseBody)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream")
        };

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("InvoiceAPI").Returns(
            new HttpClient(new SseHttpMessageHandler(response)) { BaseAddress = new Uri("https://test.local") });

        // A plain substitute is not CustomAuthenticationStateProvider, so the auth-header step is
        // a no-op — this test is about parsing the stream, not about authentication.
        var service = new ChatApiService(
            httpClientFactory,
            NullLogger<ChatApiService>.Instance,
            Substitute.For<AuthenticationStateProvider>());

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.StreamMessageAsync(new SendMessageRequest { Message = "Hi" }))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    /// <summary>Returns the prepared SSE response for any request — no network involved.</summary>
    private sealed class SseHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
