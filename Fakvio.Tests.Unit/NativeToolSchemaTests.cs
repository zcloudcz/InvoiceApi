using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Infrastructure.AiProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the single schema translator every provider uses (issue #160).
///
/// Before this existed, the four implementations that had native tools (Claude and Ollama ×
/// singleton + per-company ad-hoc) built the JSON Schema by hand, and two of those copies
/// had already lost the "items" keyword for array parameters. These tests pin the shape,
/// so a regression shows up here instead of as a 400 from a live API.
///
/// Junior note: expected values are written out as literals on purpose. A test that asks
/// production code what the right answer is cannot catch production changing its mind.
/// </summary>
public class NativeToolSchemaTests
{
    /// <summary>
    /// Tool definition covering everything the schema has to express: a required string,
    /// an enum, a number, and an array of objects.
    /// </summary>
    private static NativeToolDefinition SampleTool() => new()
    {
        Name = "create_invoice",
        Description = "Creates an invoice.",
        Parameters =
        [
            new NativeToolParameter
            {
                Name = "client_name",
                Type = "string",
                Description = "Client name"
            },
            new NativeToolParameter
            {
                Name = "currency",
                Type = "string",
                Description = "Currency code",
                EnumValues = ["CZK", "EUR"]
            },
            new NativeToolParameter
            {
                Name = "total",
                Type = "number",
                Description = "Total amount"
            },
            new NativeToolParameter
            {
                Name = "items",
                Type = "array",
                Description = "Invoice lines",
                ArrayItemType = "object"
            }
        ],
        Required = ["client_name"]
    };

    [Fact]
    public void BuildJsonSchema_DescribesAnObjectWithEveryParameterAsAProperty()
    {
        var schema = NativeToolSchema.BuildJsonSchema(SampleTool());

        schema["type"].ShouldBe("object");

        var properties = schema["properties"].ShouldBeOfType<Dictionary<string, object>>();
        properties.Keys.ShouldBe(["client_name", "currency", "total", "items"], ignoreOrder: true);
    }

    [Fact]
    public void BuildJsonSchema_KeepsTheDeclaredTypeAndDescriptionOfEachParameter()
    {
        var properties = Properties(NativeToolSchema.BuildJsonSchema(SampleTool()));

        properties["client_name"]["type"].ShouldBe("string");
        properties["client_name"]["description"].ShouldBe("Client name");
        properties["total"]["type"].ShouldBe("number");
    }

    [Fact]
    public void BuildJsonSchema_EmitsEnumOnlyForParametersThatRestrictTheirValues()
    {
        var properties = Properties(NativeToolSchema.BuildJsonSchema(SampleTool()));

        properties["currency"]["enum"].ShouldBe(new List<string> { "CZK", "EUR" });
        properties["client_name"].ShouldNotContainKey("enum");
    }

    /// <summary>
    /// The whole reason ArrayItemType exists: OpenAI and Gemini reject an array property
    /// that does not say what its elements look like.
    /// </summary>
    [Fact]
    public void BuildJsonSchema_EmitsItemsForArrayParameters()
    {
        var properties = Properties(NativeToolSchema.BuildJsonSchema(SampleTool()));

        var items = properties["items"]["items"].ShouldBeOfType<Dictionary<string, object>>();
        items["type"].ShouldBe("object");

        properties["client_name"].ShouldNotContainKey("items");
    }

    [Fact]
    public void BuildJsonSchema_ListsTheRequiredParameters()
    {
        var schema = NativeToolSchema.BuildJsonSchema(SampleTool());

        schema["required"].ShouldBe(new List<string> { "client_name" });
    }

    [Fact]
    public void BuildJsonSchema_OmitsRequiredWhenNoParameterIsMandatory()
    {
        var tool = SampleTool();
        tool.Required = [];

        NativeToolSchema.BuildJsonSchema(tool).ShouldNotContainKey("required");
    }

    /// <summary>
    /// Gemini's parameters field is an OpenAPI Schema whose "type" is a protobuf enum,
    /// so its JSON form is the upper-case member name.
    /// </summary>
    [Fact]
    public void BuildOpenApiSchema_UsesUpperCaseTypeKeywordsForGemini()
    {
        var schema = NativeToolSchema.BuildOpenApiSchema(SampleTool());
        var properties = Properties(schema);

        schema["type"].ShouldBe("OBJECT");
        properties["client_name"]["type"].ShouldBe("STRING");
        properties["total"]["type"].ShouldBe("NUMBER");
        properties["items"]["type"].ShouldBe("ARRAY");

        var items = properties["items"]["items"].ShouldBeOfType<Dictionary<string, object>>();
        items["type"].ShouldBe("OBJECT");
    }

    /// <summary>
    /// Only the type keyword is upper-cased — parameter names and enum values are data
    /// the model has to reproduce verbatim.
    /// </summary>
    [Fact]
    public void BuildOpenApiSchema_LeavesParameterNamesAndEnumValuesUntouched()
    {
        var properties = Properties(NativeToolSchema.BuildOpenApiSchema(SampleTool()));

        properties.ShouldContainKey("client_name");
        properties["currency"]["enum"].ShouldBe(new List<string> { "CZK", "EUR" });
    }

    // ─── Providers actually use it ────────────────────────────────────────

    /// <summary>
    /// Ollama already had native tools before #160; its hand-written schema loop was replaced
    /// by the shared translator. This pins that the wire format did not change with it —
    /// lower-case JSON Schema types and an element schema for the array parameter.
    /// </summary>
    [Fact]
    public async Task OllamaProvider_SendsTheSharedJsonSchemaOnTheWire()
    {
        var handler = new StubHandler("""{"message":{"role":"assistant","content":"ok"}}""");
        var provider = new OllamaProvider(
            new HttpClient(handler),
            Options.Create(new AiSettings
            {
                Ollama = new OllamaSettings { BaseUrl = "http://localhost:11434", Model = "llama3.1" }
            }),
            NullLogger<OllamaProvider>.Instance);

        await provider.GetCompletionWithToolsAsync([new ChatMessageDto { Role = "User", Content = "ahoj" }],
            null, [SampleTool()]);

        var function = JsonSerializer.Deserialize<JsonElement>(handler.LastBody)
            .GetProperty("tools")[0].GetProperty("function");

        function.GetProperty("name").GetString().ShouldBe("create_invoice");

        var properties = function.GetProperty("parameters").GetProperty("properties");
        properties.GetProperty("client_name").GetProperty("type").GetString().ShouldBe("string");
        properties.GetProperty("items").GetProperty("items").GetProperty("type").GetString().ShouldBe("object");
    }

    /// <summary>
    /// Claude takes the schema as <c>Tool.InputSchema</c>, which the SDK types as a plain
    /// object — so nothing but serialization proves our dictionary survives the trip. This
    /// serializes the very Tool the provider builds and checks the wire shape Anthropic
    /// documents: snake_case <c>input_schema</c> carrying the shared JSON Schema.
    /// </summary>
    [Fact]
    public void ClaudeTool_CarriesTheSharedJsonSchemaAsInputSchemaOnTheWire()
    {
        var tool = new Anthropic.Tool
        {
            Name = "create_invoice",
            Description = "Creates an invoice.",
            InputSchema = NativeToolSchema.BuildJsonSchema(SampleTool())
        };

        var wire = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(tool));
        var schema = wire.GetProperty("input_schema");

        wire.GetProperty("name").GetString().ShouldBe("create_invoice");
        schema.GetProperty("type").GetString().ShouldBe("object");

        var properties = schema.GetProperty("properties");
        properties.GetProperty("client_name").GetProperty("type").GetString().ShouldBe("string");
        properties.GetProperty("items").GetProperty("items").GetProperty("type").GetString().ShouldBe("object");
        schema.GetProperty("required")[0].GetString().ShouldBe("client_name");
    }

    private static Dictionary<string, Dictionary<string, object>> Properties(Dictionary<string, object> schema)
        => ((Dictionary<string, object>)schema["properties"])
            .ToDictionary(kv => kv.Key, kv => (Dictionary<string, object>)kv.Value);

    private sealed class StubHandler(string responseJson) : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };
        }
    }
}
