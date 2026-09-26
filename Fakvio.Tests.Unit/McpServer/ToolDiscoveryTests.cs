using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fakvio.McpServer;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for the MCP tool discovery pipeline of <c>Fakvio.McpServer</c> — what an AI client
/// gets back when it calls <c>tools/list</c>.
///
/// Junior note: every other test in this folder calls a tool method directly, so it never
/// touches the ModelContextProtocol SDK at all. That means a broken SDK upgrade (renamed
/// attributes, changed discovery rules, a schema generator that chokes on one of our
/// parameter types) would leave all of them green while the server exposes nothing.
/// These tests close that hole: they mirror the registration path of <c>Program.cs</c>
/// (API client first, then <c>AddMcpServer().WithToolsFromAssembly()</c>) and assert the
/// resulting tool surface.
/// </summary>
public class ToolDiscoveryTests
{
    /// <summary>
    /// Protocol tool names the SDK generates today: lowercase words joined by single underscores
    /// (<c>get_readiness</c>). Measured against both SDK 1.0.0 and 2.2.0 during the #238 upgrade.
    /// </summary>
    private const string SnakeCaseToolName = "^[a-z][a-z0-9]*(_[a-z0-9]+)*$";

    /// <summary>
    /// Guides that publish how many MCP tools ship, each with the wording it uses today.
    /// Every alternative is anchored on its surrounding phrase on purpose: a bare
    /// <c>(?&lt;count&gt;\d+) toolů</c> would also match "49 chat toolů" two words away in §4.7
    /// and compare the MCP surface against the chat one.
    /// </summary>
    private static readonly (string File, Regex Published)[] PublishedToolCounts =
    [
        ("DEVGUIDE.md", new Regex(
            @"\[McpServerTool\]`, (?<count>\d+) toolů"          // §4.7 intro
            + @"|Součty:\*\* (?<count>\d+) MCP toolů"           // §4.7 totals line
            + @"|\*\*(?<count>\d+) tools\*\*",                 // §4.9 breakdown
            RegexOptions.Compiled)),
        ("USERGUIDE.md", new Regex(@"(?<count>\d+) nástrojů", RegexOptions.Compiled)),
        (Path.Combine("Fakvio.McpServer", "README.md"), new Regex(
            @"Dostupné nástroje \((?<count>\d+)\)", RegexOptions.Compiled))
    ];

    /// <summary>
    /// Builds the tool list from the production registration itself —
    /// <see cref="McpServerRegistration.AddFakvioMcpServer"/>, the one method both the stdio host
    /// and the HTTP host call.
    ///
    /// Junior note: the order inside that method matters, and so does having
    /// <see cref="IFakvioApiClient"/> registered at all. The SDK asks
    /// <c>IServiceProviderIsService</c> whether it can resolve a tool parameter from DI; if it can,
    /// the parameter is injected and hidden from the tool's input schema, otherwise it becomes an
    /// input the AI client has to supply. Calling the real registration instead of re-creating it
    /// here is deliberate: a hand-copied mirror would keep passing after the hosts changed, and
    /// these tests would go quietly false-green.
    /// </summary>
    private static IReadOnlyList<McpServerTool> DiscoverTools() =>
        // WithToolsFromAssembly registers one McpServerTool singleton per discovered method.
        BuildServerContainer().GetServices<McpServerTool>().ToList();

    /// <summary>
    /// The container behind <see cref="DiscoverTools"/>, handed out whole so a test can also ask it
    /// <c>IServiceProviderIsService</c> — the very question the SDK asks when deciding whether a tool
    /// parameter is injected or has to be supplied by the AI client.
    /// </summary>
    private static ServiceProvider BuildServerContainer()
    {
        var services = new ServiceCollection();

        // The mode-specific piece the shared registration deliberately leaves to its caller.
        // Nothing here sends a request, so a substitute is enough.
        services.AddSingleton(Substitute.For<IApiTokenProvider>());
        services.AddFakvioMcpServer(new McpServerSettings { ApiBaseUrl = "https://api.invalid" });

        return services.BuildServiceProvider();
    }

    private static Assembly McpServerAssembly => typeof(Fakvio.McpServer.Tools.ReadinessTools).Assembly;

    [Fact]
    public void ToolsList_ExposesEveryMethodAnnotatedAsMcpTool()
    {
        // Ground truth straight from the source: every [McpServerTool] method in the assembly,
        // regardless of whether its class carries [McpServerToolType]. Deriving it by reflection
        // instead of hard-coding a number keeps the test from going stale every time a tool is
        // added — while still failing loudly if the SDK stops seeing one.
        var annotated = AnnotatedToolMethods().Select(m => m.Name).ToList();

        annotated.ShouldNotBeEmpty("The assembly must contain [McpServerTool] methods at all.");

        var discovered = DiscoverTools();

        // Compare counts, not names: the SDK derives the protocol tool name from the method
        // name and may transform it (casing convention), which is not what this test guards.
        discovered.Count.ShouldBe(annotated.Count,
            $"Discovered [{string.Join(", ", discovered.Select(t => t.ProtocolTool.Name).Order())}] " +
            $"for annotated methods [{string.Join(", ", annotated.Order())}]. " +
            "A missing tool usually means its class lost the [McpServerToolType] attribute.");

        // Duplicate names would make one of the tools unreachable for the AI client.
        discovered.Select(t => t.ProtocolTool.Name).ShouldBeUnique();
    }

    [Fact]
    public void EveryTool_HasADescriptionAndAnObjectInputSchema()
    {
        foreach (var tool in DiscoverTools())
        {
            var protocolTool = tool.ProtocolTool;

            protocolTool.Name.ShouldNotBeNullOrWhiteSpace();

            // Without a description the AI client has no way to pick the right tool.
            protocolTool.Description.ShouldNotBeNullOrWhiteSpace(
                $"Tool '{protocolTool.Name}' is missing its [Description].");

            // Schema generation runs over the parameter types the AI client actually supplies
            // (long?, enums, DTOs). If the SDK cannot map one of them, it shows up here rather
            // than at runtime on a live client.
            protocolTool.InputSchema.ValueKind.ShouldBe(JsonValueKind.Object,
                $"Tool '{protocolTool.Name}' has no usable input schema.");
        }
    }

    [Fact]
    public void NoTool_ExposesItsInjectedApiClientAsAnInputParameter()
    {
        // Every tool takes IFakvioApiClient as its first parameter and the SDK is expected to
        // inject it from DI, never to ask the AI client for it. If a future SDK version stops
        // hiding DI-resolvable parameters, 'api' would appear in the schema as a required object
        // the AI cannot construct — all tools would become uncallable while every other test in
        // this folder (they call the methods directly) stayed green. This test locks that down.
        foreach (var tool in DiscoverTools())
        {
            List<string> properties = tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var p)
                ? p.EnumerateObject().Select(prop => prop.Name).ToList()
                : [];

            properties.ShouldNotContain("api",
                $"Tool '{tool.ProtocolTool.Name}' exposes its injected IFakvioApiClient in the " +
                "input schema — the SDK is no longer resolving it from DI, so the tool is " +
                "uncallable for an AI client.");
        }
    }

    /// <summary>
    /// Every <c>[McpServerTool]</c> method in the McpServer assembly, whatever its class or visibility.
    /// Derived by reflection so the expectation cannot go stale as #241 / #242 add tools.
    /// </summary>
    private static IReadOnlyList<MethodInfo> AnnotatedToolMethods() =>
        McpServerAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToList();

    /// <summary>
    /// The letters of a name, with casing and underscores thrown away — <c>get_vat_report</c> and
    /// <c>GetVatReport</c> both become <c>getvatreport</c>.
    ///
    /// Junior note: this is how a protocol tool is paired with the method it came from without
    /// baking the SDK's casing rule into the test. A rule like "underscore before every capital"
    /// would start lying the day someone writes an acronym method name such as <c>GetVATReport</c>,
    /// and the test would fail on a naming style rather than on a real regression.
    /// </summary>
    private static string LettersOf(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

    /// <summary>
    /// Pairs each annotated method with the tool the SDK generated for it, failing loudly when a
    /// method has no counterpart (renamed, collapsed, or silently dropped by the SDK).
    /// </summary>
    private static IEnumerable<(MethodInfo Method, McpServerTool Tool)> PairToolsWithTheirMethods(
        IServiceProvider container)
    {
        var toolsByLetters = container.GetServices<McpServerTool>()
            .ToDictionary(tool => LettersOf(tool.ProtocolTool.Name));

        foreach (var method in AnnotatedToolMethods())
        {
            toolsByLetters.TryGetValue(LettersOf(method.Name), out var tool).ShouldBeTrue(
                $"No discovered tool corresponds to method '{method.Name}'. Discovered names: " +
                $"[{string.Join(", ", toolsByLetters.Values.Select(t => t.ProtocolTool.Name).Order())}].");

            yield return (method, tool!);
        }
    }

    /// <summary>
    /// The names an AI client sees as inputs of the tool. No fallback: a tool whose schema lost its
    /// <c>properties</c> object reports an empty surface, which is exactly the regression the callers
    /// of this helper compare against the parameters the method actually declares.
    /// </summary>
    private static IReadOnlyList<string> SchemaPropertyNames(McpServerTool tool) =>
        tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(property => property.Name).Order().ToList()
            : [];

    private static IReadOnlyList<string> SchemaRequiredNames(McpServerTool tool) =>
        tool.ProtocolTool.InputSchema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(name => name.GetString()!).Order().ToList()
            : [];

    /// <summary>
    /// Parameters the AI client has to fill in: everything the container cannot inject and that is
    /// not the framework-supplied cancellation token.
    ///
    /// Junior note: if a tool ever takes another parameter the SDK supplies by itself (an
    /// <c>IMcpServer</c>, a <c>RequestContext&lt;&gt;</c>, an <c>IProgress&lt;&gt;</c>), add its type
    /// to the exclusions here - otherwise the callers below expect it as an input the AI must send.
    /// </summary>
    private static IEnumerable<ParameterInfo> ClientSuppliedParameters(
        MethodInfo method, IServiceProviderIsService isService) =>
        method.GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .Where(p => !isService.IsService(p.ParameterType));

    [Fact]
    public void EveryToolName_IsTheProtocolSpellingOfItsMethodName()
    {
        // Counting tools (the test above) cannot tell a healthy surface from one the SDK renamed or
        // collapsed: 37 tools called anything at all still count as 37, and every AI client prompt,
        // DEVGUIDE §4.9 table and downstream task (#241 / #242) is written against the names.
        // The expectation is derived from the method names, so new tools need no edit here.
        var container = BuildServerContainer();

        foreach (var (method, tool) in PairToolsWithTheirMethods(container))
        {
            tool.ProtocolTool.Name.ShouldMatch(SnakeCaseToolName,
                $"Tool name '{tool.ProtocolTool.Name}' (method '{method.Name}') is not snake_case — " +
                "the SDK changed its naming convention and every client prompt now names a tool " +
                "that no longer exists.");
        }
    }

    [Fact]
    public void EveryToolSchema_ExposesExactlyTheParametersTheAiClientMustSupply()
    {
        // The shape half of the surface snapshot. Expected properties come from the method signature
        // minus whatever the container injects, which is the same rule the SDK applies — so this also
        // covers the injected API client without naming it, and fails on a schema that silently lost
        // its properties, gained an internal one, or renamed an existing input.
        var container = BuildServerContainer();
        var isService = container.GetRequiredService<IServiceProviderIsService>();

        foreach (var (method, tool) in PairToolsWithTheirMethods(container))
        {
            var expected = ClientSuppliedParameters(method, isService)
                .Select(p => p.Name!)
                .Order()
                .ToList();

            SchemaPropertyNames(tool).ShouldBe(expected,
                $"Input schema of tool '{tool.ProtocolTool.Name}' no longer matches the parameters " +
                $"of method '{method.Name}'.");
        }
    }

    [Fact]
    public void ToolParametersWithDefaults_StayOptionalInTheSchema()
    {
        // Optionality is part of the contract, not decoration: most tools take a page size, a filter
        // or an issuer ID with a C# default value. An SDK that marked those required would make every
        // such tool fail validation until the AI client guessed a value for each one.
        var container = BuildServerContainer();
        var isService = container.GetRequiredService<IServiceProviderIsService>();

        foreach (var (method, tool) in PairToolsWithTheirMethods(container))
        {
            var expectedRequired = ClientSuppliedParameters(method, isService)
                .Where(p => !p.HasDefaultValue)
                .Select(p => p.Name!)
                .Order()
                .ToList();

            SchemaRequiredNames(tool).ShouldBe(expectedRequired,
                $"Required inputs of tool '{tool.ProtocolTool.Name}' no longer match the parameters " +
                $"of method '{method.Name}' that have no default value.");
        }
    }

    /// <summary>
    /// Every tool must tell an AI client whether it is safe to call without confirmation
    /// (read-only) and whether it destroys data (destructive) — MCP clients such as Claude and
    /// ChatGPT use these annotations to decide whether to ask the user before invoking a tool.
    ///
    /// Junior note: the rule is derived from the tool's NAME, not from a hard-coded list of tool
    /// names (same principle as <see cref="EveryToolName_IsTheProtocolSpellingOfItsMethodName"/>
    /// above) — a hard-coded list would need a manual edit for every new tool and would silently
    /// stop catching regressions the day someone forgets that edit.
    /// </summary>
    [Fact]
    public void EveryTool_DeclaresItsSideEffects()
    {
        foreach (var tool in DiscoverTools())
        {
            var name = tool.ProtocolTool.Name;
            var annotations = tool.ProtocolTool.Annotations;

            annotations.ShouldNotBeNull(
                $"Tool '{name}' has no annotations — every [McpServerTool] must set " +
                "ReadOnly/Destructive/Idempotent/OpenWorld explicitly (DEVGUIDE §4.9).");

            annotations!.ReadOnlyHint.ShouldNotBeNull(
                $"Tool '{name}' does not declare ReadOnlyHint.");

            if (name.StartsWith("delete_", StringComparison.Ordinal))
            {
                annotations.DestructiveHint.ShouldBe(true,
                    $"Tool '{name}' starts with 'delete_' and must be DestructiveHint = true.");
            }

            if (name.StartsWith("get_", StringComparison.Ordinal) ||
                name.StartsWith("list_", StringComparison.Ordinal) ||
                name.StartsWith("find_", StringComparison.Ordinal) ||
                name.StartsWith("export_", StringComparison.Ordinal) ||
                name.StartsWith("lookup_", StringComparison.Ordinal) ||
                name.StartsWith("estimate_", StringComparison.Ordinal) ||
                name.StartsWith("compare_", StringComparison.Ordinal))
            {
                annotations.ReadOnlyHint.ShouldBe(true,
                    $"Tool '{name}' looks read-only from its name and must be ReadOnlyHint = true.");
            }
        }
    }

    /// <summary>
    /// Every guide that names a tool count must name the count the server actually exposes.
    ///
    /// Why this needs a test rather than a reviewer: story #144 published "36" and the number
    /// survived seven rounds of review because nothing fails when prose goes stale — the guide
    /// simply lies, and each new reader trusts it instead of counting the attributes again.
    /// It also merges silently: two branches that each add a tool rewrite the sentence to two
    /// different numbers, git takes one, and no conflict marker ever appears.
    ///
    /// The expectation comes from <see cref="DiscoverTools"/> — the live surface — never from a
    /// literal here, because a literal is the very thing that goes stale.
    /// </summary>
    [Fact]
    public void Guides_PublishTheLiveNumberOfMcpTools()
    {
        var expected = DiscoverTools().Count;
        var repositoryRoot = RepositoryRoot.Find();

        foreach (var (file, published) in PublishedToolCounts)
        {
            var text = File.ReadAllText(Path.Combine(repositoryRoot, file));

            var counts = published
                .Matches(text)
                .Select(match => int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture))
                .ToList();

            counts.ShouldNotBeEmpty(
                $"{file} publishes the MCP tool count; if the wording changed, update " +
                $"{nameof(PublishedToolCounts)} so the guard keeps biting");

            // Distinct() so the message reads "the guide says 36, we ship 37" instead of
            // repeating the same wrong number once per occurrence.
            counts
                .Distinct()
                .ShouldBe(
                    [expected],
                    $"every MCP tool count in {file} must match the {expected} tools the server exposes");
        }
    }
}
