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
    private static IReadOnlyList<McpServerTool> DiscoverTools()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IFakvioApiClient>());
        services.AddMcpServer().WithToolsFromAssembly(McpServerAssembly);

        // WithToolsFromAssembly registers one McpServerTool singleton per discovered method.
        return services.BuildServiceProvider().GetServices<McpServerTool>().ToList();
    }

    private static Assembly McpServerAssembly => typeof(Fakvio.McpServer.Tools.ReadinessTools).Assembly;

    [Fact]
    public void ToolsList_ExposesEveryMethodAnnotatedAsMcpTool()
    {
        // Ground truth straight from the source: every [McpServerTool] method in the assembly,
        // regardless of whether its class carries [McpServerToolType]. Deriving it by reflection
        // instead of hard-coding a number keeps the test from going stale every time a tool is
        // added — while still failing loudly if the SDK stops seeing one.
        var annotated = McpServerAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Static | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .Select(m => m.Name)
            .ToList();

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
}
