using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Infrastructure.AiProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Native function calling for OpenAI (issue #160).
///
/// OpenAI used to fall back to the text protocol: the model was asked to print bare JSON
/// into its answer and a substring parser dug it out. These tests cover the request we
/// build (tools with a JSON Schema), the response we parse (tool_calls) and the degradation
/// rule that keeps a refusing model working.
///
/// Junior note: the OpenAI SDK is driven through a stubbed HTTP transport, so the tests see
/// the real serialized request the SDK would have sent — no network, no API key needed.
/// </summary>
public class OpenAiNativeToolCallingTests
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
    public void BuildOptions_TurnsEachDefinitionIntoAFunctionTool()
    {
        var options = OpenAiToolCalling.BuildOptions(Tools());

        options.Tools.Count.ShouldBe(1);
        options.Tools[0].FunctionName.ShouldBe("create_invoice");
        options.Tools[0].FunctionDescription.ShouldBe("Creates an invoice.");

        // ChatService runs one tool call per turn, so parallel calls would be dropped.
        options.AllowParallelToolCalls.ShouldBe(false);
    }

    /// <summary>
    /// The parameters travel as raw JSON Schema. Arrays must carry an element schema —
    /// OpenAI answers 400 for an array property without "items".
    /// </summary>
    [Fact]
    public void BuildOptions_SendsTheSharedJsonSchemaIncludingArrayItems()
    {
        var options = OpenAiToolCalling.BuildOptions(Tools());
        var schema = JsonSerializer.Deserialize<JsonElement>(options.Tools[0].FunctionParameters);

        schema.GetProperty("type").GetString().ShouldBe("object");

        var properties = schema.GetProperty("properties");
        properties.GetProperty("client_name").GetProperty("type").GetString().ShouldBe("string");
        properties.GetProperty("items").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("items").GetProperty("items").GetProperty("type").GetString().ShouldBe("object");

        schema.GetProperty("required")[0].GetString().ShouldBe("client_name");
    }

    // ─── Response parsing ─────────────────────────────────────────────────

    [Fact]
    public void ParseCompletion_ReadsToolCallNameAndArguments()
    {
        var completion = CompletionWithToolCall("""{"client_name":"ACME","total":1500.5}""");

        var result = OpenAiToolCalling.ParseCompletion(completion, NullLogger.Instance);

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
    public void ParseCompletion_DropsArgumentsSentAsJsonNull()
    {
        var completion = CompletionWithToolCall("""{"client_name":"ACME","note":null}""");

        var result = OpenAiToolCalling.ParseCompletion(completion, NullLogger.Instance);

        result.ToolCalls[0].Arguments.ShouldNotContainKey("note");
    }

    /// <summary>
    /// A parameterless call arrives with an empty arguments payload. Parsing that as JSON
    /// throws, so it has to be read as "no arguments" instead of failing the whole turn.
    /// </summary>
    [Fact]
    public void ParseCompletion_WithEmptyArguments_YieldsAnEmptyArgumentMap()
    {
        var completion = CompletionWithToolCall("");

        var result = OpenAiToolCalling.ParseCompletion(completion, NullLogger.Instance);

        result.HasToolCalls.ShouldBeTrue();
        result.ToolCalls[0].Arguments.ShouldBeEmpty();
    }

    /// <summary>
    /// The arguments string is generated by the model, so it can arrive truncated. That must
    /// cost the tool its parameters, not the whole turn — the executor then reports the
    /// missing parameters back to the model, which retries.
    /// </summary>
    [Fact]
    public void ParseCompletion_WithMalformedArguments_KeepsTheCallAndDropsTheParameters()
    {
        var completion = CompletionWithToolCall("""{"client_name":""");

        var result = OpenAiToolCalling.ParseCompletion(completion, NullLogger.Instance);

        result.HasToolCalls.ShouldBeTrue();
        result.ToolCalls[0].Arguments.ShouldBeEmpty();
    }

    [Fact]
    public void ParseCompletion_WithPlainText_ReturnsTextAndNoToolCalls()
    {
        var completion = OpenAIChatModelFactory.ChatCompletion(
            content: new ChatMessageContent(ChatMessageContentPart.CreateTextPart("Dobrý den")));

        var result = OpenAiToolCalling.ParseCompletion(completion, NullLogger.Instance);

        result.HasToolCalls.ShouldBeFalse();
        result.TextContent.ShouldBe("Dobrý den");
    }

    // ─── Provider behaviour ───────────────────────────────────────────────

    [Fact]
    public void OpenAiProvider_SupportsNativeToolsByDefault()
    {
        CreateProvider(Ok(ToolCallResponseJson())).provider.SupportsNativeTools.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAiProvider_SendsToolsAndReturnsTheParsedCall()
    {
        var (provider, handler) = CreateProvider(Ok(ToolCallResponseJson()));

        var result = await provider.GetCompletionWithToolsAsync(Conversation(), "system", Tools());

        result.ShouldNotBeNull();
        result.ToolCalls[0].ToolName.ShouldBe("create_invoice");
        result.ToolCalls[0].Arguments["client_name"].ShouldBe("ACME");
        handler.LastBody.ShouldContain("\"tools\"");
        handler.LastBody.ShouldContain("create_invoice");
    }

    /// <summary>
    /// Every failure degrades this message to the text flow, but only a definitive refusal of
    /// the tools themselves may latch native calling off — the latch lives until the process
    /// restarts and the singleton provider is shared by all tenants.
    ///
    /// Junior note: 400 is on both sides of the line. "no function calling" is permanent,
    /// "context_length_exceeded" is just a long conversation (the history is sent whole) and
    /// says nothing about tools; an expired key (401/403) breaks the text path just the same,
    /// so latching would only outlive the fix.
    /// </summary>
    [Theory]
    // Definitive refusals — latch off.
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"tools not supported"}}""", false)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Function calling is not enabled"}}""", false)]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"message":"The model does not exist"}}""", false)]
    // Everything else — fall back for this message only.
    [InlineData(HttpStatusCode.BadRequest,
        """{"error":{"code":"context_length_exceeded","message":"maximum context length"}}""", true)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided"}}""", true)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"message":"Country not supported"}}""", true)]
    [InlineData(HttpStatusCode.RequestTimeout, "{}", true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "{}", true)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", true)]
    [InlineData(HttpStatusCode.InternalServerError, "{}", true)]
    public async Task OpenAiProvider_LatchesNativeToolsOff_OnlyWhenTheToolsThemselvesAreRefused(
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
    public async Task OpenAiProvider_LatchIsPerInstance_AndDoesNotLeakToOtherProviders()
    {
        var (refused, _) = CreateProvider(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":{"message":"The model does not exist"}}""")
        });
        var (healthy, _) = CreateProvider(Ok(ToolCallResponseJson()));

        await refused.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        refused.SupportsNativeTools.ShouldBeFalse();
        healthy.SupportsNativeTools.ShouldBeTrue();
    }

    /// <summary>
    /// New surface created by <c>AllowParallelToolCalls = false</c>: models that support tools
    /// but reject that one parameter (the o1 reasoning family) answer 400 with the parameter
    /// name in the message. "parallel_tool_calls" contains "tool", so the predicate reads it as
    /// a definitive refusal and latches.
    ///
    /// Characterisation, and the latch is arguably right: the flag is sent on every native
    /// call, so the 400 would repeat forever — retrying natively would just burn a round trip
    /// per message. The cost is honest and worth writing down: a model that CAN call functions
    /// is pushed onto the text protocol until the process restarts. Fixing that means not
    /// sending the flag to such models, not widening the predicate.
    /// </summary>
    [Fact]
    public async Task OpenAiProvider_WhenTheModelRejectsTheParallelToolCallsParameter_LatchesNativeToolsOff()
    {
        var (provider, _) = CreateProvider(Refusal(
            HttpStatusCode.BadRequest,
            """
            {"error":{"message":"Unsupported parameter: 'parallel_tool_calls' is not supported with this model.",
            "type":"invalid_request_error","param":"parallel_tool_calls","code":"unsupported_parameter"}}
            """));

        var result = await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        result.ShouldBeNull();
        provider.SupportsNativeTools.ShouldBeFalse();
    }

    // ─── What the operator sees in the log ────────────────────────────────

    /// <summary>
    /// ADMINGUIDE §5 promises the operator can spot the fallback in the log by the warning
    /// "rejected native tool calling". Pin the wording and the status.
    /// </summary>
    [Fact]
    public async Task OpenAiProvider_OnLatch_LogsTheWarningTheAdminGuidePromises()
    {
        var logger = new RecordingLogger<OpenAiProvider>();
        var provider = ProviderWith(logger, Refusal(HttpStatusCode.NotFound, ModelNotFoundBody));

        await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.Message.ShouldContain("rejected native tool calling");
        warning.Message.ShouldContain("404");
    }

    /// <summary>
    /// Unlike the Gemini path, OpenAI attaches the SDK exception — which repeats the service
    /// error body — so the operator sees WHICH complaint caused the latch, not only that one
    /// happened. Losing that attachment would quietly halve the diagnostic value of the line
    /// the guide points at, hence the test.
    /// </summary>
    [Fact]
    public async Task OpenAiProvider_OnLatch_KeepsTheProviderErrorBodyInTheLog()
    {
        var logger = new RecordingLogger<OpenAiProvider>();
        var provider = ProviderWith(logger, Refusal(HttpStatusCode.NotFound, ModelNotFoundBody));

        await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        logger.Warnings.ShouldHaveSingleItem().FullText.ShouldContain("The model `gpt-9` does not exist");
    }

    /// <summary>
    /// A fallback that lasts one message must not carry the latch wording, or the operator
    /// would read a permanent downgrade into a rate limit.
    /// </summary>
    [Fact]
    public async Task OpenAiProvider_OnPerMessageFallback_LogsAWarningWithoutTheLatchWording()
    {
        var logger = new RecordingLogger<OpenAiProvider>();
        var provider = ProviderWith(logger, Refusal(HttpStatusCode.TooManyRequests, "{}"));

        await provider.GetCompletionWithToolsAsync(Conversation(), null, Tools());

        var warning = logger.Warnings.ShouldHaveSingleItem();
        warning.Message.ShouldNotContain("rejected native tool calling");
        warning.Message.ShouldContain("falling back for this message");
    }

    /// <summary>
    /// The per-company provider must behave identically — it is the path a tenant with its
    /// own OpenAI key takes, and it was the copy that historically lagged behind.
    /// </summary>
    [Fact]
    public async Task AdHocOpenAiProvider_AlsoUsesNativeTools()
    {
        var handler = new CapturingHandler(Ok(ToolCallResponseJson()));
        var provider = new Fakvio.Infrastructure.Service.AdHocOpenAiProvider(
            ChatClientOver(handler), NullLogger.Instance);

        provider.SupportsNativeTools.ShouldBeTrue();

        var result = await provider.GetCompletionWithToolsAsync(Conversation(), "system", Tools());

        result.ShouldNotBeNull();
        result.ToolCalls[0].ToolName.ShouldBe("create_invoice");
        handler.LastBody.ShouldContain("create_invoice");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static (OpenAiProvider provider, CapturingHandler handler) CreateProvider(HttpResponseMessage response)
    {
        var handler = new CapturingHandler(response);
        return (new OpenAiProvider(ChatClientOver(handler), NullLogger<OpenAiProvider>.Instance), handler);
    }

    /// <summary>Same provider, but with a logger the test can read back.</summary>
    private static OpenAiProvider ProviderWith(RecordingLogger<OpenAiProvider> logger, HttpResponseMessage response)
        => new(ChatClientOver(new CapturingHandler(response)), logger);

    private static HttpResponseMessage Refusal(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private const string ModelNotFoundBody =
        """{"error":{"message":"The model `gpt-9` does not exist","type":"invalid_request_error"}}""";

    /// <summary>
    /// Builds a real SDK ChatClient whose transport is the given stub handler.
    /// Retries are disabled so an error-status test does not wait for SDK backoff.
    /// </summary>
    private static ChatClient ChatClientOver(HttpMessageHandler handler)
    {
        var options = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
        };

        return new OpenAIClient(new ApiKeyCredential("test-key"), options).GetChatClient("gpt-4o");
    }

    private static ChatCompletion CompletionWithToolCall(string argumentsJson)
        => OpenAIChatModelFactory.ChatCompletion(
            finishReason: ChatFinishReason.ToolCalls,
            toolCalls:
            [
                ChatToolCall.CreateFunctionToolCall(
                    id: "call_1",
                    functionName: "create_invoice",
                    functionArguments: BinaryData.FromString(argumentsJson))
            ]);

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    /// <summary>Chat Completions payload in which the model asked for one function call.</summary>
    private static string ToolCallResponseJson() =>
        """
        {
          "id": "chatcmpl-1",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "gpt-4o",
          "choices": [{
            "index": 0,
            "finish_reason": "tool_calls",
            "message": {
              "role": "assistant",
              "content": null,
              "tool_calls": [{
                "id": "call_1",
                "type": "function",
                "function": { "name": "create_invoice", "arguments": "{\"client_name\":\"ACME\"}" }
              }]
            }
          }]
        }
        """;

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return response;
        }
    }
}
