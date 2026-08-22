using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Assertions about the built-in main block of the AI system prompt.
///
/// The expected text is spelled out here as literals **on purpose**. Asserting
/// <c>prompt.ShouldContain(AiSystemPrompt.DefaultMainBlock)</c> would be a tautology:
/// the prompt is composed from that very constant, so such a test stays green even when
/// the built-in text silently changes (e.g. a tool description gets dropped). These
/// literals are an independent copy, so they can actually fail.
///
/// Only single lines are compared — never a multi-line chunk — because the constant is a
/// raw string literal and therefore carries the line endings of the source file, which
/// differ between a Windows checkout and the Linux build agent.
/// </summary>
internal static class BuiltInPromptAssertions
{
    /// <summary>
    /// One marker per section and per registered chat tool. When a new tool is added to
    /// <c>AiSystemPrompt.DefaultMainBlock</c> (see DEVGUIDE §4.7), add it here too — that is
    /// the point: the duplication makes a silent change to the prompt impossible.
    /// </summary>
    private static readonly string[] MainBlockMarkers =
    [
        // Sections.
        "RESPONSE STYLE: Answer in ONE sentence maximum. No greetings, no filler, no repetition.",
        "Respond in the same language the user writes in (Czech or English).",
        "TOOLS (use them, don't ask unnecessary questions):",
        "IMPORT RULES:",
        "RULES:",

        // Tools — first line of every entry.
        "- ares_lookup: Look up Czech company by IČO",
        "- create_client: Create client from IČO (auto-fills from ARES)",
        "- create_invoice: Create a new issued invoice with line items",
        "- import_invoice: Import invoice from pasted text/data",
        "- navigate: Navigate user to a page",
        "- export_invoice: Export/download invoice as PDF (by document number or client name)",
        "- get_received_invoice: Get FULL detail of a received (incoming) invoice by ID or document number.",
        "- list_received_invoices: List/browse received invoices with filters",
        "- search_received_invoices: Full-text search across received invoices",
        "- attach_file: Attach a file to an entity (Invoice, ReceivedInvoice, or Client).",
        "- list_attachments: List all files attached to an entity record.",

        // Rules the AI must not lose — wrong dates on imported invoices are a real defect.
        "- NEVER generate, guess, or use today's date. If a date is missing, pass null.",
        "- The tool auto-finds the client by IČO — do NOT ask the user.",
        "- Use tools when asked. Never claim actions without tool confirmation."
    ];

    /// <summary>
    /// Asserts that the composed prompt really carries the built-in style / tools / rules
    /// block, checked against the literals above rather than against the constant.
    /// </summary>
    public static void ShouldContainBuiltInMainBlock(this string prompt)
    {
        foreach (var marker in MainBlockMarkers)
            prompt.ShouldContain(marker, customMessage: $"The built-in main block is missing: {marker}");
    }
}
