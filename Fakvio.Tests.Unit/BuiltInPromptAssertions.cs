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
/// The copy is complete — every line of the block, not a sample of them. A partial copy
/// only protects the lines it happens to list; the rest can be reworded, weakened or
/// deleted with the whole suite staying green.
///
/// Line endings are normalized before comparing, because the constant is a raw string
/// literal and therefore carries the line endings of the source file, which differ
/// between a Windows checkout and a Linux build agent.
/// </summary>
internal static class BuiltInPromptAssertions
{
    /// <summary>
    /// The built-in block verbatim, line by line, including blank lines and the two-space
    /// indentation of continuation lines. When <c>AiSystemPrompt.DefaultMainBlock</c> changes
    /// (a new tool, a reworded rule — see DEVGUIDE §4.7), this copy has to change with it.
    /// That is the point: the duplication turns every silent edit of the prompt into a red test.
    /// </summary>
    private static readonly string[] ExpectedMainBlockLines =
    [
        "RESPONSE STYLE: Answer in ONE sentence maximum. No greetings, no filler, no repetition.",
        "Just do what the user asks and confirm the result briefly.",
        "Respond in the same language the user writes in (Czech or English).",
        "",
        "TOOLS (use them, don't ask unnecessary questions):",
        "- ares_lookup: Look up Czech company by IČO",
        "- create_client: Create client from IČO (auto-fills from ARES)",
        "- create_invoice: Create a new issued invoice with line items",
        "- import_invoice: Import invoice from pasted text/data — auto-detects issued vs received",
        "  by matching IČO against the company DB, finds client automatically, preserves all dates exactly",
        "- navigate: Navigate user to a page",
        "- export_invoice: Export/download invoice as PDF (by document number or client name)",
        "- get_received_invoice: Get FULL detail of a received (incoming) invoice by ID or document number.",
        "  Returns supplier info, ALL line items with quantities/prices/VAT rates, VAT breakdown totals,",
        "  payment info, dates, status. Use this to answer questions like",
        "  'proč má přijatá faktura 267708922 špatnou celkovou částku?'",
        "- list_received_invoices: List/browse received invoices with filters",
        "  (status, supplier, date range, amount range, currency, overdue).",
        "- search_received_invoices: Full-text search across received invoices",
        "  (document number, supplier name, variable symbol, amount).",
        "- attach_file: Attach a file to an entity (Invoice, ReceivedInvoice, or Client).",
        "  Requires entity_name, record_id, file_name, and file_content_base64 (Base64-encoded bytes).",
        "  The frontend provides file_content_base64 when the user drops a file in the chat.",
        "- list_attachments: List all files attached to an entity record.",
        "  Provide entity_name and record_id. Returns file name, size, upload date, and description.",
        "",
        "IMPORT RULES:",
        "- When user pastes invoice text, extract ALL data and call import_invoice immediately.",
        "- ALL dates (issue_date, due_date, taxable_supply_date) must be EXACTLY from the document.",
        "- NEVER generate, guess, or use today's date. If a date is missing, pass null.",
        "- The tool auto-determines issued/received — do NOT ask the user.",
        "- The tool auto-finds the client by IČO — do NOT ask the user.",
        "- If the client doesn't exist, the tool will tell you — then use create_client.",
        "",
        "RULES:",
        "- Use tools when asked. Never claim actions without tool confirmation.",
        "- Don't ask about things you can determine from the data."
    ];

    /// <summary>
    /// Asserts that the composed prompt really carries the built-in style / tools / rules
    /// block, checked against the literals above rather than against the constant.
    /// </summary>
    public static void ShouldContainBuiltInMainBlock(this string prompt)
    {
        var normalizedPrompt = NormalizeLineEndings(prompt);

        // Line by line first: a single reworded or dropped line then names itself in the
        // failure message instead of leaving the reader to diff a 37-line chunk.
        foreach (var expectedLine in ExpectedMainBlockLines.Where(line => line.Length > 0))
            normalizedPrompt.ShouldContain(
                expectedLine,
                customMessage: $"The built-in main block is missing this line: {expectedLine}");

        // Then the block as a whole: the loop above cannot see a line that was *inserted*,
        // moved or re-indented, and "no silent change" has to cover those too. The block is
        // surrounded by blank lines in every composition, so anchoring on them also catches
        // a line appended to either end — which a plain substring match would let through.
        normalizedPrompt.ShouldContain(
            $"\n\n{string.Join("\n", ExpectedMainBlockLines)}\n\n",
            customMessage: "The built-in main block differs from the expected text — "
                + "an extra line, a different order, or different indentation.");
    }

    /// <summary>
    /// Makes CRLF and LF comparable. The prompt inherits its line endings from the source
    /// file the raw string literal lives in, which is a property of the checkout, not of
    /// the behaviour under test.
    /// </summary>
    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");
}
