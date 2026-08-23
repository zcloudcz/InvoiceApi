using System.Text;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Single source of truth for the layout and the built-in text of the AI assistant
/// system prompt.
///
/// Two callers assemble the very same prompt and must never drift apart:
/// - <see cref="ChatContextBuilder"/> — the real prompt sent to the AI, with live
///   tenant data filled in.
/// - <see cref="AiInstructionsService"/> — the SysAdmin preview, with the
///   tenant-specific parts replaced by placeholders.
///
/// Prompt layout (top to bottom):
/// 1. Identity line — always built in.
/// 2. Company identity — from the tenant database, NOT editable.
/// 3. Main block — the built-in <see cref="DefaultMainBlock"/>, or the SysAdmin's
///    custom text when one is stored.
/// 4. Appendix — optional SysAdmin text, appended after the main block.
/// 5. Business context statistics — from the tenant database, NOT editable.
/// </summary>
public static class AiSystemPrompt
{
    /// <summary>
    /// Opening line. Models such as Ollama otherwise answer "I'm not connected to any
    /// system", because nothing tells them that their tools are real.
    /// </summary>
    public const string Identity =
        "You are Fakvio AI Assistant — connected to the Fakvio invoicing system.";

    /// <summary>
    /// Built-in style / tools / rules block. A non-empty custom prompt replaces this
    /// whole block — the SysAdmin then takes over describing the tools.
    /// </summary>
    public const string DefaultMainBlock = """
        RESPONSE STYLE: Answer in ONE sentence maximum. No greetings, no filler, no repetition.
        Just do what the user asks and confirm the result briefly.
        Respond in the same language the user writes in (Czech or English).

        TOOLS (use them, don't ask unnecessary questions):
        - ares_lookup: Look up Czech company by IČO
        - create_client: Create client from IČO (auto-fills from ARES)
        - create_invoice: Create a new issued invoice with line items
        - import_invoice: Import invoice from pasted text/data — auto-detects issued vs received
          by matching IČO against the company DB, finds client automatically, preserves all dates exactly
        - navigate: Navigate user to a page
        - export_invoice: Export/download invoice as PDF (by document number or client name)
        - get_received_invoice: Get FULL detail of a received (incoming) invoice by ID or document number.
          Returns supplier info, ALL line items with quantities/prices/VAT rates, VAT breakdown totals,
          payment info, dates, status. Use this to answer questions like
          'proč má přijatá faktura 267708922 špatnou celkovou částku?'
        - list_received_invoices: List/browse received invoices with filters
          (status, supplier, date range, amount range, currency, overdue).
        - search_received_invoices: Full-text search across received invoices
          (document number, supplier name, variable symbol, amount).
        - attach_file: Attach a file to an entity (Invoice, ReceivedInvoice, or Client).
          Requires entity_name, record_id, file_name, and file_content_base64 (Base64-encoded bytes).
          The frontend provides file_content_base64 when the user drops a file in the chat.
        - list_attachments: List all files attached to an entity record.
          Provide entity_name and record_id. Returns file name, size, upload date, and description.

        IMPORT RULES:
        - When user pastes invoice text, extract ALL data and call import_invoice immediately.
        - ALL dates (issue_date, due_date, taxable_supply_date) must be EXACTLY from the document.
        - NEVER generate, guess, or use today's date. If a date is missing, pass null.
        - The tool auto-determines issued/received — do NOT ask the user.
        - The tool auto-finds the client by IČO — do NOT ask the user.
        - If the client doesn't exist, the tool will tell you — then use create_client.

        RULES:
        - Use tools when asked. Never claim actions without tool confirmation.
        - Don't ask about things you can determine from the data.
        """;

    /// <summary>Header of the business statistics block. Also serves as a preview anchor.</summary>
    public const string BusinessContextHeader = "Current tenant business context:";

    /// <summary>
    /// Marker rendered instead of live tenant data in the SysAdmin preview. The preview
    /// endpoint is a master-DB endpoint, so no tenant statistics are available there.
    /// </summary>
    public const string PreviewPlaceholder = "[N/A — preview mode]";

    /// <summary>
    /// Assembles the final prompt from its parts. Pure string composition — all data
    /// gathering happens in the caller, which keeps this testable and side-effect free.
    /// </summary>
    /// <param name="companyBlock">
    /// Company identity block, or null/empty when the tenant has no issuer configured.
    /// </param>
    /// <param name="customPrompt">SysAdmin override of the main block; null/blank = built-in.</param>
    /// <param name="appendix">Optional SysAdmin text appended after the main block.</param>
    /// <param name="businessContextBlock">Business statistics block (already formatted).</param>
    public static string Compose(
        string? companyBlock,
        string? customPrompt,
        string? appendix,
        string businessContextBlock)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Identity);
        sb.AppendLine();

        if (!string.IsNullOrEmpty(companyBlock))
        {
            sb.AppendLine(companyBlock.TrimEnd());
            sb.AppendLine();
        }

        // A custom prompt replaces the built-in block entirely. Whitespace-only does not
        // count as custom — it would leave the prompt without any tools or rules at all.
        sb.AppendLine(string.IsNullOrWhiteSpace(customPrompt) ? DefaultMainBlock : customPrompt.TrimEnd());
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(appendix))
        {
            sb.AppendLine(appendix.TrimEnd());
            sb.AppendLine();
        }

        sb.Append(businessContextBlock);
        return sb.ToString();
    }

    /// <summary>
    /// Assembles the SysAdmin preview: the same layout and the same built-in text as
    /// the live prompt, with the tenant-specific parts replaced by placeholders.
    /// </summary>
    public static string ComposePreview(string? customPrompt, string? appendix)
    {
        var companyBlock = BuildCompanyBlock(
            companyName: PreviewPlaceholder,
            registrationNumber: PreviewPlaceholder,
            taxNumber: null);

        var businessContext = BuildBusinessContextBlock(
            totalClients: PreviewPlaceholder,
            openInvoices: PreviewPlaceholder,
            overdueInvoices: PreviewPlaceholder,
            paidInvoices: PreviewPlaceholder);

        return Compose(companyBlock, customPrompt, appendix, businessContext);
    }

    /// <summary>
    /// Formats the company identity block. The AI needs to know which IČO is "us" so
    /// import_invoice can tell an issued invoice from a received one.
    /// </summary>
    public static string BuildCompanyBlock(string? companyName, string? registrationNumber, string? taxNumber)
    {
        var sb = new StringBuilder();
        sb.AppendLine("YOUR COMPANY (the user's company — you represent this entity):");
        sb.AppendLine($"- Name: {companyName}");
        sb.AppendLine($"- IČO: {registrationNumber}");
        if (!string.IsNullOrEmpty(taxNumber))
            sb.AppendLine($"- DIČ: {taxNumber}");
        sb.AppendLine("When importing invoices: if YOUR IČO appears as the issuer (dodavatel), it's an ISSUED invoice.");
        sb.Append("If YOUR IČO appears as the recipient (odběratel), it's a RECEIVED invoice.");
        return sb.ToString();
    }

    /// <summary>
    /// Formats the business statistics block. Values are passed as strings so the preview
    /// can substitute placeholders without duplicating the layout.
    /// </summary>
    public static string BuildBusinessContextBlock(
        string totalClients,
        string openInvoices,
        string overdueInvoices,
        string paidInvoices)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BusinessContextHeader);
        sb.AppendLine($"- Total active clients: {totalClients}");
        sb.AppendLine($"- Open (unpaid) invoices: {openInvoices}");
        sb.AppendLine($"- Overdue invoices: {overdueInvoices}");
        sb.AppendLine($"- Paid invoices: {paidInvoices}");
        return sb.ToString();
    }
}
