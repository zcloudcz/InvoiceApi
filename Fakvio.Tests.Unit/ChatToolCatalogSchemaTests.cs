using System.Globalization;
using System.Text.RegularExpressions;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.DependencyInjection;
using Fakvio.Infrastructure.Service;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Contract tests for the REAL chat tool catalog — the tools the production composition
/// root (<c>AddFakvioCore</c>) registers, resolved through a real DI container.
///
/// Why this exists next to <see cref="ChatToolExecutorTests"/>: those tests describe the
/// executor's behaviour using NSubstitute mocks, so they prove the rules are enforced but
/// never touch a single shipped tool. Nothing checked that the tools we actually
/// ship satisfy those rules — a tool with a broken schema would only blow up at startup in
/// production, and a tool that is never registered would be silently invisible to the model.
///
/// The JSON Schema facts pinned here are also the contract #160 (native tool calling for
/// OpenAI / Gemini) builds on: real types, "array" carrying an item type, and a required
/// list that matches the schema.
///
/// Junior note: nothing here connects to a database or calls an AI provider. The tools are
/// only RESOLVED (constructed) and asked for their schema; the one test that executes a call
/// runs it against schema-identical stubs — see <see cref="CreateInertExecutor"/>.
/// </summary>
public class ChatToolCatalogSchemaTests
{
    /// <summary>
    /// Naming convention every tool and parameter must follow: snake_case, starting with a
    /// letter. Stricter than what providers demand (OpenAI/Anthropic accept
    /// <c>^[a-zA-Z0-9_-]{1,64}$</c>), which is deliberate — DEVGUIDE §4.7 mandates snake_case
    /// so the same name works in the prompt text and in every provider's native schema.
    /// </summary>
    private const string SnakeCaseNamePattern = "^[a-z][a-z0-9_]*$";

    /// <summary>Hard limit on function / property names in the OpenAI and Anthropic tool APIs.</summary>
    private const int ProviderNameMaxLength = 64;

    /// <summary>
    /// The JSON Schema "type" keyword each parameter type must serialize to, written out
    /// literally instead of calling <c>ToJsonSchemaType()</c> — a test that asks production
    /// code what the right answer is cannot catch production changing its mind.
    /// </summary>
    private static readonly IReadOnlyDictionary<ChatToolParameterType, string> ExpectedJsonSchemaTypes =
        new Dictionary<ChatToolParameterType, string>
        {
            [ChatToolParameterType.String] = "string",
            [ChatToolParameterType.Number] = "number",
            [ChatToolParameterType.Integer] = "integer",
            [ChatToolParameterType.Boolean] = "boolean",
            [ChatToolParameterType.ObjectArray] = "array"
        };

    /// <summary>Loaded once for the whole class — building the container costs ~200 ms.</summary>
    private static readonly RealToolCatalog Catalog = RealToolCatalog.Load();

    /// <summary>
    /// Both DEVGUIDE §4.7 spellings of the shipped chat tool count: <c>`IChatTool`, 34 toolů</c>
    /// in the section intro and <c>37 MCP toolů, 34 chat toolů</c> in the totals line.
    ///
    /// Each alternative carries the words around the number, not just the number: the totals
    /// paragraph goes on to say "12 chat toolů nemá MCP protějšek", which a bare
    /// <c>(\d+) chat toolů</c> would also match and compare against the wrong quantity.
    /// Reusing one group name across alternatives is legal in .NET regex and keeps the read
    /// site to a single lookup.
    /// </summary>
    private static readonly Regex PublishedChatToolCount = new(
        @"`IChatTool`, (?<count>\d+) toolů|MCP toolů, (?<count>\d+) chat toolů",
        RegexOptions.Compiled);

    /// <summary>Tool names drive the data-driven tests, so a failure names the guilty tool.</summary>
    public static TheoryData<string> RealToolNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var tool in Catalog.Tools)
            {
                names.Add(tool.ToolName);
            }

            return names;
        }
    }

    // ─── Registration ─────────────────────────────────────────────────────

    [Fact]
    public void CompositionRoot_RegistersAndResolves_EveryChatToolInTheInfrastructureAssembly()
    {
        // Anything implementing IChatTool is meant to be offered to the model. Discovering the
        // implementations by reflection means a newly written tool that nobody registered in
        // AddFakvioCore fails here instead of just never being offered.
        // Compared by type name rather than by Type so a failure reads as a list of class names.
        var implementedTools = typeof(ChatToolExecutor).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(IChatTool).IsAssignableFrom(type))
            .Select(type => type.Name)
            .Order()
            .ToList();

        implementedTools.ShouldNotBeEmpty();

        Catalog.RegisteredImplementationTypes
            .Select(type => type.Name)
            .Order()
            .ShouldBe(implementedTools, Case.Sensitive, "every IChatTool implementation must be registered in AddFakvioCore");

        Catalog.Tools
            .Select(tool => tool.GetType().Name)
            .Order()
            .ShouldBe(implementedTools, Case.Sensitive, "every registered tool must also be resolvable from the container");
    }

    [Fact]
    public void DevGuide_PublishesTheLiveNumberOfChatTools()
    {
        // DEVGUIDE §4.7 states how many chat tools ship, and the number is load-bearing:
        // it is what the next author trusts instead of counting registrations again.
        //
        // Why it needs a test and a reviewer is not enough: when two feature branches each
        // add tools, both rewrite that sentence to a DIFFERENT number, but the sentence that
        // was never touched stays byte-identical on both sides — git merges it silently, no
        // conflict marker appears, and the guide ships a stale count. #225 hit exactly that
        // after #217 merged. Nothing downstream fails, so it only surfaces when a human
        // notices the arithmetic does not add up.
        //
        // The expectation is derived from the live container (the same source the rest of
        // this class uses), never written out here — a hard-coded literal would be the very
        // thing that goes stale.
        var expected = Catalog.Tools.Count;

        var devGuide = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "DEVGUIDE.md"));

        var published = PublishedChatToolCount
            .Matches(devGuide)
            .Select(match => int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture))
            .ToList();

        published.Count.ShouldBeGreaterThanOrEqualTo(
            2,
            "DEVGUIDE §4.7 publishes the chat tool count twice (the intro line and the totals line); "
            + "if the wording changed, update PublishedChatToolCount so the guard keeps biting");

        // Distinct() so the failure message reads as "the guide says 29, we ship 34" rather
        // than repeating the same wrong number once per occurrence.
        published
            .Distinct()
            .ShouldBe(
                [expected],
                $"every chat tool count in DEVGUIDE.md must match the {expected} tools registered in AddFakvioCore");
    }

    [Fact]
    public void SystemPrompt_BuiltInBlock_CarriesTheRealGeneratedToolCatalog()
    {
        // #159 replaced the hand-written tool list in the prompt with a catalog generated from
        // the registered tools. Generated is not the same as verified: the tests that drive the
        // prompt elsewhere use fake tools, so nothing else checks what the model is actually
        // told about the tools we ship.
        //
        // ComposePreview is the real composition path (the SysAdmin preview and the live prompt
        // share it), so this also pins that the catalog lands inside the built-in block, in DI
        // registration order — compared against the independent literals in
        // BuiltInPromptAssertions, never against the tools themselves.
        var prompt = AiSystemPrompt.ComposePreview(customPrompt: null, appendix: null, Catalog.Tools);

        prompt.ShouldContainBuiltInMainBlock();
    }

    // ─── Startup schema validation over the real tools ────────────────────

    [Fact]
    public void ChatToolExecutor_ResolvedFromCompositionRoot_AcceptsEveryRealToolSchema()
    {
        using var provider = CreateCompositionRoot().BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Constructing the executor runs ValidateSchema over every registered tool, so a real
        // tool with an empty/duplicate parameter name, a missing description or an enum on a
        // non-string parameter throws right here — exactly as it would at application startup.
        var executor = scope.ServiceProvider.GetRequiredService<IChatToolExecutor>();

        executor.AvailableTools.ShouldBe(Catalog.Tools.Select(tool => tool.ToolName), ignoreOrder: true);
        executor.AvailableTools
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(executor.AvailableTools.Count, "tool names must be unique, they are the lookup key");
    }

    // ─── Naming contract ──────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(RealToolNames))]
    public void RealTool_UsesProviderCompatibleNames(string toolName)
    {
        var tool = ToolNamed(toolName);

        toolName.ShouldMatch(SnakeCaseNamePattern);
        toolName.Length.ShouldBeLessThanOrEqualTo(ProviderNameMaxLength);

        foreach (var parameter in tool.Parameters)
        {
            parameter.Name.ShouldMatch(SnakeCaseNamePattern);
            parameter.Name.Length.ShouldBeLessThanOrEqualTo(ProviderNameMaxLength);
        }
    }

    // ─── Native JSON Schema contract (#160) ───────────────────────────────

    [Theory]
    [MemberData(nameof(RealToolNames))]
    public void NativeToolDefinition_ForRealTool_MirrorsItsParameterSchema(string toolName)
    {
        var tool = ToolNamed(toolName);

        Catalog.Definitions
            .Count(definition => definition.Name == toolName)
            .ShouldBe(1, $"every registered tool must produce exactly one native definition ({toolName})");

        var toolDefinition = Catalog.Definitions.Single(definition => definition.Name == toolName);

        // A data-changing tool additionally gets the reserved 'confirm' flag appended by the
        // executor (issue #212). Spelled out literally, not taken from production code.
        var expectedParameterNames = tool.Parameters.Select(parameter => parameter.Name).ToList();
        if (tool is IConfirmableChatTool)
            expectedParameterNames.Add("confirm");

        toolDefinition.Description.ShouldBe(tool.Description);
        toolDefinition.Parameters
            .Select(parameter => parameter.Name)
            .ShouldBe(expectedParameterNames);
        toolDefinition.Required.ShouldBe(
            tool.Parameters.Where(parameter => parameter.IsRequired).Select(parameter => parameter.Name),
            ignoreOrder: true,
            "the required list must hold exactly the parameters the schema marks as required");

        foreach (var parameter in tool.Parameters)
        {
            var emitted = toolDefinition.Parameters.Single(candidate => candidate.Name == parameter.Name);

            emitted.Type.ShouldBe(ExpectedJsonSchemaTypes[parameter.Type]);
            emitted.Description.ShouldBe(parameter.Description);

            // "array" without an element schema is rejected by strict function-calling APIs,
            // and an item type on a scalar is just as wrong.
            emitted.ArrayItemType.ShouldBe(
                parameter.Type == ChatToolParameterType.ObjectArray ? "object" : null,
                $"parameter '{parameter.Name}' of type {parameter.Type}");

            if (parameter.AllowedValues is null)
            {
                emitted.EnumValues.ShouldBeNull();
            }
            else
            {
                emitted.EnumValues.ShouldBe(parameter.AllowedValues);
            }
        }
    }

    // ─── Prompt examples round trip ───────────────────────────────────────

    [Theory]
    [MemberData(nameof(RealToolNames))]
    public async Task GeneratedExampleCall_ForRealTool_ParsesAndPassesCentralValidation(string toolName)
    {
        // The system prompt tells the model to copy these examples, so an example that the
        // executor's own validation would reject teaches the model to make a rejected call.
        var exampleCall = ExampleCallFor(toolName);
        var executor = CreateInertExecutor();

        var parsedCall = executor.ParseToolCall(exampleCall);

        parsedCall.ShouldNotBeNull($"the generated example must be parsable as a tool call: {exampleCall}");
        parsedCall.Action.ShouldBe(toolName);

        var result = await executor.ExecuteToolAsync(parsedCall);

        result.IsSuccess.ShouldBeTrue(
            $"the example '{exampleCall}' must satisfy the tool's own schema, but validation said: {result.ErrorMessage}");
    }

    // ─── Test helpers ─────────────────────────────────────────────────────

    private static IChatTool ToolNamed(string toolName)
        => Catalog.Tools.Single(tool => tool.ToolName == toolName);

    /// <summary>
    /// Pulls the one example line the generated tool instructions offer for the given tool.
    /// </summary>
    private static string ExampleCallFor(string toolName)
    {
        var marker = $"\"action\": \"{toolName}\"";
        var exampleLines = Catalog.ToolInstructions
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(marker))
            .ToList();

        exampleLines.Count.ShouldBe(1, $"tool instructions must show exactly one example call for '{toolName}'");

        return exampleLines[0];
    }

    /// <summary>
    /// A real <see cref="ChatToolExecutor"/> over stubs that carry the REAL tools' schemas but
    /// do nothing when executed. Parsing and the central parameter validation are therefore the
    /// production ones, while no test touches a database or an AI provider.
    /// </summary>
    private static ChatToolExecutor CreateInertExecutor()
        => new(Catalog.Tools.Select(CreateSchemaMirror), Substitute.For<ILogger<ChatToolExecutor>>());

    private static IChatTool CreateSchemaMirror(IChatTool realTool)
    {
        // The mirror must keep the real tool's INTERFACE too: a confirmable tool mirrored as a
        // plain IChatTool would lose the confirm flag, and the executor under test would then
        // validate a schema production never uses.
        var mirror = realTool is IConfirmableChatTool
            ? Substitute.For<IConfirmableChatTool>()
            : Substitute.For<IChatTool>();
        mirror.ToolName.Returns(realTool.ToolName);
        mirror.Description.Returns(realTool.Description);
        mirror.Parameters.Returns(realTool.Parameters);
        mirror
            .ExecuteAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("stubbed execution"));

        if (mirror is IConfirmableChatTool confirmableMirror)
        {
            confirmableMirror
                .BuildPreviewAsync(Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
                .Returns(ChatToolResult.Success("stubbed preview"));
        }

        return mirror;
    }

    /// <summary>
    /// Walks up from the test binaries to the folder that holds <c>Fakvio.sln</c>, so a test
    /// can read a file that lives in the repository root. A relative "../../../.." would be
    /// shorter and would break the day the build layout or target framework changes.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fakvio.sln")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull($"Fakvio.sln was not found in any folder above {AppContext.BaseDirectory}");
        return directory.FullName;
    }

    /// <summary>
    /// The production registrations, wired exactly as API and Functions wire them.
    /// </summary>
    private static ServiceCollection CreateCompositionRoot()
    {
        // AddFakvioCore validates the database configuration eagerly (fail fast at startup),
        // so it needs a syntactically valid connection string — nothing ever connects to it.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=localhost;Database=fakvio_schema_test;Username=test;Password=test",
                ["Database:AuthMode"] = "Password",
                ["UseAzureAdAuthentication"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddFakvioCore(configuration);
        return services;
    }

    /// <summary>
    /// Snapshot of everything the tests need from one real container: the registered tool
    /// types, the resolved tool instances and the two artefacts generated from their schemas.
    /// Taken once, then the container is disposed — the snapshot only holds schema metadata,
    /// no service is called afterwards.
    /// </summary>
    private sealed record RealToolCatalog(
        IReadOnlyList<IChatTool> Tools,
        IReadOnlyList<Type> RegisteredImplementationTypes,
        IReadOnlyList<NativeToolDefinition> Definitions,
        string ToolInstructions)
    {
        public static RealToolCatalog Load()
        {
            var services = CreateCompositionRoot();

            var registeredImplementationTypes = services
                .Where(descriptor => descriptor.ServiceType == typeof(IChatTool))
                .Select(descriptor => descriptor.ImplementationType!)
                .ToList();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            var executor = scope.ServiceProvider.GetRequiredService<IChatToolExecutor>();

            return new RealToolCatalog(
                scope.ServiceProvider.GetServices<IChatTool>().ToList(),
                registeredImplementationTypes,
                executor.GetToolDefinitions(),
                executor.BuildToolInstructions());
        }
    }
}
