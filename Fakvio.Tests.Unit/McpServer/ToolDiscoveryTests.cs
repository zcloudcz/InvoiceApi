using System.Reflection;
using System.Text.Json;
using Fakvio.McpServer.Client;
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
    /// Builds the tool list the way <c>Program.cs</c> does — register <see cref="IFakvioApiClient"/>
    /// first, then scan the McpServer assembly for <c>[McpServerToolType]</c> classes and register
    /// their tools into DI.
    ///
    /// Junior note: the order matters, and so does having the client registered at all. The SDK asks
    /// <c>IServiceProviderIsService</c> whether it can resolve a tool parameter from DI; if it can,
    /// the parameter is injected and hidden from the tool's input schema, otherwise it becomes an
    /// input the AI client has to supply. <c>Program.cs</c> registers the client via
    /// <c>AddHttpClient</c> before <c>AddMcpServer()</c>, so every <c>IFakvioApiClient api</c>
    /// parameter is injected. A plain substitute is enough here — nothing calls it, only its
    /// presence in the container is observed.
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
        services.AddSingleton(Substitute.For<IFakvioApiClient>());
        services.AddMcpServer().WithToolsFromAssembly(McpServerAssembly);

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
}
