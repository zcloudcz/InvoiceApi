using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Assertions about the built-in main block of the AI system prompt.
///
/// The expected text is spelled out here as literals **on purpose**. Asserting against the
/// production constants would be a tautology: the prompt is composed from them, so such a
/// test stays green even when the built-in text silently changes (e.g. a rule gets dropped).
/// These literals are an independent copy, so they can actually fail.
///
/// The copy is complete — every static line of the block, not a sample of them. A partial
/// copy only protects the lines it happens to list; the rest can be reworded, weakened or
/// deleted with the whole suite staying green.
///
/// **The tool catalog is generated now** (issue #159): <c>AiSystemPrompt.BuildDefaultMainBlock</c>
/// renders one <c>- name: description</c> line per registered <c>IChatTool</c> instead of the
/// hand-maintained list that used to sit in the prompt constant. The catalog is still text the
/// model reads, so it is still copied here — as
/// <see cref="ExpectedRealToolCatalogLines"/>, in DI registration order. A renamed tool, a
/// reworded description, a tool added without registration or one dropped from the container
/// therefore still turns a test red.
///
/// Tests that drive the prompt with fake tools pass their own tool lines instead; the real
/// catalog above is the default, used by the test that composes the prompt from the real
/// composition root.
///
/// Line endings are normalized before comparing, because the constants are raw string
/// literals and therefore carry the line endings of the source file, which differ
/// between a Windows checkout and a Linux build agent.
/// </summary>
internal static class BuiltInPromptAssertions
{
    /// <summary>
    /// The static lines above the generated tool catalog, verbatim. When the built-in text
    /// changes (a reworded rule — see DEVGUIDE §4.7), this copy has to change with it.
    /// That is the point: the duplication turns every silent edit of the prompt into a red test.
    /// </summary>
    private static readonly string[] LinesBeforeToolCatalog =
    [
        "RESPONSE STYLE: Answer in ONE sentence maximum. No greetings, no filler, no repetition.",
        "Just do what the user asks and confirm the result briefly.",
        "Respond in the same language the user writes in (Czech or English).",
        "",
        "TOOLS (use them, don't ask unnecessary questions):"
    ];

    /// <summary>
    /// The catalog the eleven shipped tools are expected to render, in DI registration order
    /// (<c>ServiceCollectionExtensions</c>). Each line is <c>- {ToolName}: {Description}</c>
    /// with the description written out as one line. This is the independent copy: when a tool
    /// description changes, it has to be changed here too, deliberately. That is the point.
    /// </summary>
    private static readonly string[] ExpectedRealToolCatalogLines =
    [
        "- ares_lookup: Looks up a Czech company in the ARES business registry by IČO (registration number). Returns company name, tax number (DIČ), VAT status, and registered address. Read-only — use it when unsure whether the user wants a lookup or a new client.",
        "- create_client: Creates a new client (customer) in the system using their IČO. Automatically fetches company data from ARES (name, address, DIČ). Use this when the user explicitly asks to create, add, or register a client.",
        "- navigate: Navigates the user to a page in the application. Can open new invoice, new credit note, show client detail, open client list, invoice list, or new client form. If a client name is mentioned, it finds the client first. Opens forms only — it never creates a document.",
        "- create_invoice: Creates a new invoice for a client with specified items. Automatically resolves the client by name, sets default currency (CZK), applies default VAT rate, and generates a document number. After creation, navigates to the invoice detail page. Use this when the user provides item details (description, price) — when they only want to open the form, use 'navigate' with target new_invoice instead.",
        "- import_invoice: Imports an invoice from extracted data. Automatically determines if it's an issued (vydaná) or received (přijatá) invoice by matching IČO against the company database. Finds the client/supplier automatically. Preserves all dates exactly as extracted.",
        "- export_invoice: Export/download an invoice or credit note as a PDF file. Finds the document by number, by client name (most recent), or exports the most recent invoice if no parameters given.",
        "- get_received_invoice: Get detail of a received (incoming/expense) invoice by ID or document number. Returns all fields: supplier info, line items, VAT breakdown, totals, dates, status, payment info.",
        "- list_received_invoices: List received (incoming/expense) invoices with optional filtering by status, supplier name, date range, amount range, or currency. Returns paged results with totals.",
        "- search_received_invoices: Full-text search across received (incoming/expense) invoices. Searches by document number, supplier name, variable symbol, or amount. Use this when the user provides a number or name without specifying which field.",
        "- attach_file: Attach a file to an entity (Invoice, ReceivedInvoice, or Client). The file content must be provided as a Base64-encoded string — the frontend supplies it when the user drops a file into the chat. Returns the attachment ID, file name, and size upon success.",
        "- list_attachments: List all file attachments for an entity record (Invoice, ReceivedInvoice, or Client). Returns file name, size, upload date, and optional description for each attachment."
    ];

    /// <summary>The static lines below the generated tool catalog, verbatim.</summary>
    private static readonly string[] LinesAfterToolCatalog =
    [
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
    /// block, checked against the literals above rather than against the production constants.
    /// </summary>
    /// <param name="prompt">The composed system prompt.</param>
    /// <param name="expectedToolLines">
    /// The <c>- name: description</c> lines the registered tools are expected to render, in
    /// registration order. Pass the test's own literals, not values read from the tools.
    /// Omit this to expect the real shipped catalog (<see cref="ExpectedRealToolCatalogLines"/>).
    /// </param>
    public static void ShouldContainBuiltInMainBlock(this string prompt, params string[] expectedToolLines)
    {
        var normalizedPrompt = NormalizeLineEndings(prompt);

        var toolLines = expectedToolLines.Length > 0 ? expectedToolLines : ExpectedRealToolCatalogLines;

        string[] expectedLines =
        [
            .. LinesBeforeToolCatalog,
            .. toolLines,
            .. LinesAfterToolCatalog
        ];

        // Line by line first: a single reworded or dropped line then names itself in the
        // failure message instead of leaving the reader to diff the whole chunk.
        foreach (var expectedLine in expectedLines.Where(line => line.Length > 0))
            normalizedPrompt.ShouldContain(
                expectedLine,
                customMessage: $"The built-in main block is missing this line: {expectedLine}");

        // Then the block as a whole: the loop above cannot see a line that was *inserted*,
        // moved or re-indented, and "no silent change" has to cover those too. The block is
        // surrounded by blank lines in every composition, so anchoring on them also catches
        // a line appended to either end — which a plain substring match would let through.
        // This is also what pins the generated catalog: an extra, missing or misplaced tool
        // line breaks the anchored match even though no static line changed.
        normalizedPrompt.ShouldContain(
            $"\n\n{string.Join("\n", expectedLines)}\n\n",
            customMessage: "The built-in main block differs from the expected text — "
                + "an extra line, a different order, or different indentation.");
    }

    /// <summary>
    /// Makes CRLF and LF comparable. The prompt inherits its line endings from the source
    /// file the raw string literals live in, which is a property of the checkout, not of
    /// the behaviour under test.
    /// </summary>
    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n");
}
