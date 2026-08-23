using System.Text.Json;
using Fakvio.Infrastructure.Service.ChatTools;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ToolArgumentReader — the single conversion from a model's JSON
/// arguments object to the Dictionary&lt;string, string&gt; that IChatTool.ExecuteAsync takes.
///
/// This is the shared code path behind all three flows (ChatToolExecutor.ParseToolCall
/// for text-based providers, ClaudeProvider and OllamaProvider for native tool calling),
/// so what is asserted here holds for every provider by construction.
/// </summary>
public class ToolArgumentReaderTests
{
    private static Dictionary<string, string> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ToolArgumentReader.ReadArguments(document.RootElement);
    }

    [Fact]
    public void ReadArguments_UnwrapsStringValues()
    {
        var arguments = Read("""{"client_name": "Alza a.s.", "note": "sleva 10 %"}""");

        arguments["client_name"].ShouldBe("Alza a.s.");
        arguments["note"].ShouldBe("sleva 10 %");
    }

    [Fact]
    public void ReadArguments_KeepsRawJson_ForNumbersBooleansAndArrays()
    {
        var arguments = Read("""
                             {"discount": 10.5,
                              "copies": 3,
                              "send_email": true,
                              "items": [{"description": "Mléko", "unit_price": 999}]}
                             """);

        arguments["discount"].ShouldBe("10.5");
        arguments["copies"].ShouldBe("3");
        arguments["send_email"].ShouldBe("true");
        arguments["items"].ShouldStartWith("[");
    }

    [Fact]
    public void ReadArguments_DropsJsonNullValues()
    {
        // The regression this guards: GetRawText() renders a JSON null as the text "null",
        // which is neither blank nor type-invalid, so it survives every validation layer
        // and gets written to the database (ImportInvoiceTool would store IBAN = "null").
        var arguments = Read("""{"iban": null, "variable_symbol": null, "swift": "AGBACZPP"}""");

        arguments.ShouldNotContainKey("iban");
        arguments.ShouldNotContainKey("variable_symbol");
        arguments["swift"].ShouldBe("AGBACZPP");
    }

    [Fact]
    public void ReadArguments_NeverProducesTheText_null()
    {
        var arguments = Read("""{"iban": null}""");

        arguments.Values.ShouldNotContain("null");
    }

    [Fact]
    public void ReadArguments_KeepsEmptyStrings_TheyAreARealValueTheCallerDecidesAbout()
    {
        // An empty string is not the same signal as a JSON null: the executor treats it as
        // blank (= not supplied), but that decision belongs there, not here.
        var arguments = Read("""{"note": ""}""");

        arguments["note"].ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void ReadArguments_ReturnsEmptyMap_WhenTheRootIsNotAnObject(string json)
    {
        // EnumerateObject() throws InvalidOperationException on a non-object element, and
        // the providers only catch JsonException — an empty map is the safe answer.
        Read(json).ShouldBeEmpty();
    }

    [Fact]
    public void ReadArguments_ReturnsEmptyMap_ForAnEmptyObject()
    {
        Read("{}").ShouldBeEmpty();
    }
}
