using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
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
/// These two tests close that hole: they run the same registration path as
/// <c>Program.cs</c> and assert the resulting tool surface.
/// </summary>
public class ToolDiscoveryTests
{
    /// <summary>
    /// Builds the tool list exactly the way <c>Program.cs</c> does — scan the McpServer
    /// assembly for <c>[McpServerToolType]</c> classes and register their tools into DI.
    /// </summary>
    private static IReadOnlyList<McpServerTool> DiscoverTools()
    {
        var services = new ServiceCollection();
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

            // Schema generation runs over our parameter types (long?, enums, DTOs). If the SDK
            // cannot map one of them, it shows up here rather than at runtime on a live client.
            protocolTool.InputSchema.ValueKind.ShouldBe(JsonValueKind.Object,
                $"Tool '{protocolTool.Name}' has no usable input schema.");
        }
    }
}
