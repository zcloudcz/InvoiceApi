using System.Net;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Infrastructure.AiProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Native function calling for Gemini (issue #160).
///
/// Gemini used to fall back to the text protocol: the model was asked to print bare JSON
/// into its answer and a substring parser dug it out. These tests cover the two halves of
/// the native path — the request we build (functionDeclarations) and the response we parse
/// (functionCall parts) — plus the degradation rule that keeps a refusing model working.
///
/// Junior note: no network is touched. A stub HttpMessageHandler answers every call, which
/// also lets the test read the exact JSON that would have gone out on the wire.
/// </summary>
public class GeminiNativeToolCallingTests
{
    private static List<NativeToolDefinition> Tools() =>
    [
        new()
        {
            Name = "create_invoice",
            Description = "Creates an invoice.",
            Parameters =
            [
                new NativeToolParameter { Name = "client_name", Type = "string", Description = "Client name" },
                new NativeToolParameter
                {
                    Name = "items", Type = "array", Description = "Lines", ArrayItemType = "object"
                }
            ],
            Required = ["client_name"]
        }
    ];

    private static List<ChatMessageDto> Conversation() =>
    [
        new() { Role = "User", Content = "Vystav fakturu pro ACME." }
    ];

    // ─── Request building ─────────────────────────────────────────────────

    [Fact]
    public void BuildRequestBody_WithTools_DeclaresThemUnderToolsFunctionDeclarations()
    {
        var body = Serialize(GeminiApi.BuildRequestBody(Conversation(), "system prompt", Tools()));

        var declaration = body.GetProperty("tools")[0].GetProperty("functionDeclarations")[0];
        declaration.GetProperty("name").GetString().ShouldBe("create_invoice");
        declaration.GetProperty("description").GetString().ShouldBe("Creates an invoice.");
    }

    /// <summary>
    /// The declaration must carry the shared schema, spelled the way Gemini's OpenAPI
    /// subset wants it (upper-case types, arrays with an element schema).
    /// </summary>
    [Fact]
    public void BuildRequestBody_WithTools_SendsTheSharedSchemaInGeminiSpelling()
    {
        var body = Serialize(GeminiApi.BuildRequestBody(Conversation(), null, Tools()));

        var parameters = body.GetProperty("tools")[0]
            .GetProperty("functionDeclarations")[0]
            .GetProperty("parameters");

        parameters.GetProperty("type").GetString().ShouldBe("OBJECT");

        var properties = parameters.GetProperty("properties");
        properties.GetProperty("client_name").GetProperty("type").GetString().ShouldBe("STRING");
        properties.GetProperty("items").GetProperty("type").GetString().ShouldBe("ARRAY");
        properties.GetProperty("items").GetProperty("items").GetProperty("type").GetString().ShouldBe("OBJECT");

        parameters.GetProperty("required")[0].GetString().ShouldBe("client_name");
    }

    /// <summary>
    /// Regression guard for the plain completion and streaming paths, which share this
    /// builder: they must keep sending a request with no "tools" key at all.
    /// </summary>
    [Fact]
    public void BuildRequestBody_WithoutTools_SendsNoToolsKey()
    {
        var body = Serialize(GeminiApi.BuildRequestBody(Conversation(), "system prompt"));

        body.TryGetProperty("tools", out _).ShouldBeFalse();
        body.GetProperty("systemInstruction").GetProperty("parts")[0]
            .GetProperty("text").GetString().ShouldBe("system prompt");
        body.GetProperty("contents")[0].GetProperty("role").GetString().ShouldBe("user");
    }

    // ─── Response parsing ─────────────────────────────────────────────────

    [Fact]
    public void ParseToolResponse_ReadsFunctionCallNameAndArguments()
    {
        var result = GeminiApi.ParseToolResponse(
            FunctionCallResponse("""{"client_name":"ACME","total":1500.5}"""),
            NullLogger.Instance);

        result.HasToolCalls.ShouldBeTrue();
        result.ToolCalls[0].ToolName.ShouldBe("create_invoice");
        result.ToolCalls[0].Arguments["client_name"].ShouldBe("ACME");

        // Numbers keep their raw JSON text — the executor parses them back.
        result.ToolCalls[0].Arguments["total"].ShouldBe("1500.5");
    }

    /// <summary>
    /// Same contract as every other provider: a JSON null means "the parameter did not
    /// arrive", never the four-character text "null".
    /// </summary>
    [Fact]
    public void ParseToolResponse_DropsArgumentsSentAsJsonNull()
    {
        var result = GeminiApi.ParseToolResponse(
            FunctionCallResponse("""{"client_name":"ACME","note":null}"""),
            NullLogger.Instance);

        result.ToolCalls[0].Arguments.ShouldNotContainKey("note");
    }

    [Fact]
    public void ParseToolResponse_WithPlainText_ReturnsTextAndNoToolCalls()
    {
        var result = GeminiApi.ParseToolResponse(
            """{"candidates":[{"content":{"parts":[{"text":"Dobrý den"}]}}]}""",
            NullLogger.Instance);

        result.HasToolCalls.ShouldBeFalse();
        result.TextContent.ShouldBe("Dobrý den");
    }

    /// <summary>
    /// Gemini may answer with commentary and a call in the same parts array. Both are kept —
    /// ChatService prefers the tool call, exactly as it does for Claude.
    /// </summary>
    [Fact]
    public void ParseToolResponse_WithTextAndFunctionCall_KeepsBoth()
    {
        var result = GeminiApi.ParseToolResponse(
            """
            {"candidates":[{"content":{"parts":[
              {"text":"Zakládám fakturu."},
              {"functionCall":{"name":"create_invoice","args":{"client_name":"ACME"}}}
            ]}}]}
            """,
            NullLogger.Instance);

        result.HasToolCalls.ShouldBeTrue();
        result.TextContent.ShouldBe("Zakládám fakturu.");
    }

    // ─── Provider behaviour ───────────────────────────────────────────────

    [Fact]
    public void GeminiProvider_SupportsNativeToolsByDefault()
    {
        CreateProvider(Ok(FunctionCallResponse("""{"client_name":"ACME"}"""))).provider
            .SupportsNativeTools.ShouldBeTrue();
    }

    [Fact]
    public async Task GeminiProvider_SendsFunctionDeclarationsAndReturnsTheParsedCall()
    {
        var (provider, handler) = CreateProvider(Ok(FunctionCallResponse("""{"client_name":"ACME"}""")));

        var result = await provider.GetCompletionWithToolsAsync(Conversation(), "system", Tools());

        result.ShouldNotBeNull();
        result.ToolCalls[0].ToolName.ShouldBe("create_invoice");
        handler.LastBody.ShouldContain("functionDeclarations");
        handler.LastBody.ShouldContain("create_invoice");
    }

    /// <summary>
    /// Every failure degrades this message to the text flow, but only a definitive refusal of
    /// the tools themselves may latch native calling off — the latch lives until the process
    /// restarts and the singleton provider is shared by all tenants.
    ///
    /// Junior note: 400 is on both sides of the line. "function calling is not enabled" is
    /// permanent, an oversized request is just a long conversation (the history is sent whole)
    /// and says nothing about tools; an invalid key (401/403) breaks the text path just the
    /// same, so latching would only outlive the fix.
    /// </summary>
    [Theory]
    // Definitive refusals — latch off.
    [InlineData(HttpStatusCode.BadRequest,
        """{"error":{"message":"Function calling is not enabled for models/gemini-1.0-pro"}}""", false)]
    [InlineData(HttpStatusCode.NotFound,
        """{"error":{"message":"models/gemini-x is not found for API version v1beta"}}""", false)]
    // Everything else — fall back for this message only.
    [InlineData(HttpStatusCode.BadRequest,
        """{"error":{"message":"The input token count exceeds the maximum"}}""", true)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"API key not valid"}}""", true)]
    [InlineData(HttpStatusCode.Forbidden,
        """{"error":{"message":"Permission denied on resource project"}}""", true)]
    [InlineData(HttpStatusCode.RequestTimeout, "{}", true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "{}", true)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", true)]
    [InlineData(HttpStatusCode.InternalServerError, "{}", true)]
    public async Task GeminiProvider_LatchesNativeToolsOff_OnlyWhenTheToolsThemselvesAreRefused(
        HttpStatusCode status, string body, bool stillSupportsNativeTools)
    {
        var (provider, _) = CreateProvider(new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        });

        var result = await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        result.ShouldBeNull();
        provider.SupportsNativeTools.ShouldBe(stillSupportsNativeTools);
    }

    /// <summary>
    /// The latch is per provider instance. If the flag ever became static, one tenant's
    /// unsupported model would silently downgrade every other tenant — and nothing else in
    /// the suite would notice.
    /// </summary>
    [Fact]
    public async Task GeminiProvider_LatchIsPerInstance_AndDoesNotLeakToOtherProviders()
    {
        var (refused, _) = CreateProvider(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":{"message":"models/gemini-x is not found"}}""")
        });
        var (healthy, _) = CreateProvider(Ok(FunctionCallResponse("""{"client_name":"ACME"}""")));

        await refused.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        refused.SupportsNativeTools.ShouldBeFalse();
        healthy.SupportsNativeTools.ShouldBeTrue();
    }

    /// <summary>
    /// Gemini reports an invalid API key as <b>400</b>, not 401 — the 401 row of the theory
    /// above therefore covers a status Gemini never actually sends. This is the real payload,
    /// and it must not latch: the text-based tool path uses the very same key, so the latch
    /// would buy nothing and would outlive the fixed key.
    /// </summary>
    [Fact]
    public async Task GeminiProvider_WhenAnInvalidKeyIsReportedAs400_DoesNotLatchNativeToolsOff()
    {
        var (provider, _) = CreateProvider(Refusal(
            HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}"""));

        var result = await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        result.ShouldBeNull();
        provider.SupportsNativeTools.ShouldBeTrue();
    }

    [Fact]
    public async Task GeminiProvider_OnTransportFailure_FallsBackWithoutThrowing()
    {
        var httpClient = new HttpClient(new ThrowingHandler(new HttpRequestException("boom")));
        var provider = new GeminiProvider(httpClient, Settings(), NullLogger<GeminiProvider>.Instance);

        var result = await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        result.ShouldBeNull();
        provider.SupportsNativeTools.ShouldBeTrue();
    }

    /// <summary>
    /// The per-company provider must behave identically — it is the path a tenant with its
    /// own Gemini key takes, and it was the copy that historically lagged behind.
    /// </summary>
    [Fact]
    public async Task AdHocGeminiProvider_AlsoUsesNativeTools()
    {
        var handler = new CapturingHandler(Ok(FunctionCallResponse("""{"client_name":"ACME"}""")));
        var provider = new Fakvio.Infrastructure.Service.AdHocGeminiProvider(
            new HttpClient(handler), "test-key", "gemini-2.0-flash", NullLogger.Instance);

        provider.SupportsNativeTools.ShouldBeTrue();

        var result = await provider.GetCompletionWithToolsAsync(Conversation(), "system", Tools());

        result.ShouldNotBeNull();
        result.ToolCalls[0].ToolName.ShouldBe("create_invoice");
        handler.LastBody.ShouldContain("functionDeclarations");
    }

    // ─── What the operator sees in the log ────────────────────────────────

    /// <summary>
    /// ADMINGUIDE §5 tells the operator the fallback is recognisable in the log by the
    /// warning "rejected native tool calling". That promise is only worth anything if the
    /// wording, the model and the status are really there — pin all three.
    /// </summary>
    [Fact]
    public async Task GeminiProvider_OnLatch_LogsTheWarningTheAdminGuidePromises()
    {
        var logger = new RecordingLogger<GeminiProvider>();
        var provider = ProviderWith(logger, Refusal(HttpStatusCode.NotFound, ModelNotFoundBody));

        await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.Message.ShouldContain("rejected native tool calling");
        warning.Message.ShouldContain("gemini-2.0-flash");
        warning.Message.ShouldContain("404");
    }

    /// <summary>
    /// The other half of the same promise: a fallback that lasts one message must NOT carry
    /// the latch wording, or the operator would read a permanent downgrade into a rate limit.
    /// </summary>
    [Fact]
    public async Task GeminiProvider_OnPerMessageFallback_LogsAWarningWithoutTheLatchWording()
    {
        var logger = new RecordingLogger<GeminiProvider>();
        var provider = ProviderWith(logger, Refusal(HttpStatusCode.TooManyRequests, "{}"));

        await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.Message.ShouldNotContain("rejected native tool calling");
        warning.Message.ShouldContain("falling back for this message");
    }

    /// <summary>
    /// Characterisation, not endorsement: on a latch Gemini logs the status and drops the
    /// response body. The operator learns THAT the switch happened and on which status, never
    /// WHICH complaint triggered it — unlike the OpenAI path, which attaches the SDK exception
    /// (see <c>OpenAiNativeToolCallingTests</c>). Change this test only together with a
    /// deliberate decision about how much of a provider error body may reach the log.
    /// </summary>
    [Fact]
    public async Task GeminiProvider_OnLatch_DoesNotLogTheProviderErrorBody()
    {
        var logger = new RecordingLogger<GeminiProvider>();
        var provider = ProviderWith(logger, Refusal(HttpStatusCode.BadRequest, FunctionCallingDisabledBody));

        await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        logger.Warnings.ShouldHaveSingleItem()
            .FullText.ShouldNotContain("Function calling is not enabled");
    }

    // ─── Assumption behind latching on 404 ────────────────────────────────

    /// <summary>
    /// Latching on a bare 404 is only defensible because the Gemini endpoint is a literal in
    /// <see cref="GeminiApi"/>: a 404 can therefore mean "no such model", never "the operator
    /// mistyped a base URL". This test exists to fail the day the host becomes configurable —
    /// at that point <c>NativeToolRefusal</c>'s 404 rule has to be revisited, not this test.
    /// </summary>
    [Fact]
    public async Task GeminiProvider_AlwaysCallsTheHardcodedGoogleEndpoint()
    {
        var (provider, handler) = CreateProvider(Ok(FunctionCallResponse("""{"client_name":"ACME"}""")));

        await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        // The query string carries the API key, so only the path is asserted.
        handler.LastUri.ShouldNotBeNull();
        handler.LastUri.GetLeftPart(UriPartial.Path).ShouldBe(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static (GeminiProvider provider, CapturingHandler handler) CreateProvider(HttpResponseMessage response)
    {
        var handler = new CapturingHandler(response);
        var provider = new GeminiProvider(
            new HttpClient(handler), Settings(), NullLogger<GeminiProvider>.Instance);

        return (provider, handler);
    }

    /// <summary>Same provider, but with a logger the test can read back.</summary>
    private static GeminiProvider ProviderWith(RecordingLogger<GeminiProvider> logger, HttpResponseMessage response)
        => new(new HttpClient(new CapturingHandler(response)), Settings(), logger);

    private static HttpResponseMessage Refusal(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private const string ModelNotFoundBody =
        """{"error":{"message":"models/gemini-x is not found for API version v1beta"}}""";

    private const string FunctionCallingDisabledBody =
        """{"error":{"message":"Function calling is not enabled for models/gemini-1.0-pro"}}""";

    private static IOptions<AiSettings> Settings() => Options.Create(new AiSettings
    {
        Gemini = new ProviderSettings { ApiKey = "test-key", Model = "gemini-2.0-flash" }
    });

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static string FunctionCallResponse(string argumentsJson) =>
        """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"create_invoice","args":"""
        + argumentsJson
        + """}}]}}]}""";

    private static JsonElement Serialize(object body)
        => JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(body, GeminiApi.JsonOptions));

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return response;
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
