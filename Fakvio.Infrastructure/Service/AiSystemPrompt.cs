using System.Text;
using Fakvio.Application.Service;

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
/// 3. Main block — the built-in one from <see cref="BuildDefaultMainBlock"/>, or the
///    SysAdmin's custom text when one is stored.
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

    /// <summary>Opening part of the built-in main block — how the assistant should answer.</summary>
    private const string StyleBlock = """
        RESPONSE STYLE: Answer in ONE sentence maximum. No greetings, no filler, no repetition.
        Just do what the user asks and confirm the result briefly.
        Respond in the same language the user writes in (Czech or English).
        """;

    /// <summary>Header of the tool catalog. The catalog itself is generated, never hand-written.</summary>
    private const string ToolsHeader = "TOOLS (use them, don't ask unnecessary questions):";

    /// <summary>Closing part of the built-in main block — the rules that follow the tool catalog.</summary>
    private const string RulesBlock = """
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

    /// <summary>
    /// Builds the built-in style / tools / rules block. A non-empty custom prompt replaces
    /// this whole block — the SysAdmin then takes over describing the tools.
    ///
    /// The tool catalog between the two static parts is generated from the registered
    /// <see cref="IChatTool"/> implementations, so it can never drift from what the
    /// assistant can actually do. Adding a tool means registering it in DI — nothing here
    /// and no hand-maintained list anywhere else (see DEVGUIDE §4.7).
    /// </summary>
    public static string BuildDefaultMainBlock(IEnumerable<IChatTool> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine(StyleBlock);
        sb.AppendLine();

        sb.AppendLine(ToolsHeader);
        foreach (var tool in tools)
            sb.AppendLine($"- {tool.ToolName}: {tool.Description}");
        sb.AppendLine();

        sb.Append(RulesBlock);
        return sb.ToString();
    }

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
    /// <param name="tools">Registered chat tools — the source of the built-in tool catalog.</param>
    /// <param name="situationalBlock">
    /// Situational context block (already formatted), or null when the caller has none.
    /// </param>
    public static string Compose(
        string? companyBlock,
        string? customPrompt,
        string? appendix,
        string businessContextBlock,
        IEnumerable<IChatTool> tools,
        string? situationalBlock = null)
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
        sb.AppendLine(string.IsNullOrWhiteSpace(customPrompt)
            ? BuildDefaultMainBlock(tools)
            : customPrompt.TrimEnd());
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(appendix))
        {
            sb.AppendLine(appendix.TrimEnd());
            sb.AppendLine();
        }

        sb.Append(businessContextBlock);

        // Situational data stays last: it is the most volatile part of the prompt and models
        // weigh the tail of the context most heavily.
        if (!string.IsNullOrWhiteSpace(situationalBlock))
        {
            sb.AppendLine();
            sb.Append(situationalBlock);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Assembles the SysAdmin preview: the same layout, the same built-in text and the
    /// same generated tool catalog as the live prompt, with the tenant-specific parts
    /// replaced by placeholders.
    /// </summary>
    public static string ComposePreview(string? customPrompt, string? appendix, IEnumerable<IChatTool> tools)
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

        // The situational block is runtime-only, but the SysAdmin still has to see that it
        // exists — a custom prompt is written against the whole layout, not half of it.
        var situationalContext = BuildSituationalContextBlock(
            today: PreviewPlaceholder,
            currentPage: PreviewPlaceholder,
            openEntity: PreviewPlaceholder,
            setupGaps: PreviewPlaceholder);

        return Compose(companyBlock, customPrompt, appendix, businessContext, tools, situationalContext);
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

    /// <summary>Header of the situational block. Also serves as a preview anchor.</summary>
    public const string SituationalContextHeader = "Current situation:";

    /// <summary>
    /// Conversational onboarding instructions (issue #214). Emitted only when the tenant
    /// still has blocking setup gaps — a configured tenant never pays for these tokens.
    ///
    /// It sits in the situational block on purpose: block 3 can be replaced wholesale by a
    /// SysAdmin custom prompt, and onboarding is the one thing a brand new tenant must not
    /// lose that way.
    ///
    /// The tools are referenced generically ("the matching tool above") instead of by name.
    /// The catalog is generated from the registered <see cref="IChatTool"/> implementations,
    /// so naming tools here would be a second, hand-maintained list free to go stale.
    ///
    /// The confirmation line is not politeness — write tools implementing
    /// <c>IConfirmableChatTool</c> answer the first call with a preview and write nothing
    /// until they are called again (issue #212). A model that does not know this reports
    /// the value as saved when it is not.
    ///
    /// "Never send the user to a settings page" holds for every gap the onboarding path can
    /// actually raise — the one exception, ISSUER_MISSING, has no tool because it cannot occur:
    /// <c>AuthService</c> creates the issuer during registration (AuthService.cs:223). If that
    /// ever changes, this line needs a carve-out before a create_issuer tool exists.
    /// </summary>
    public const string OnboardingInstructions = """
        ONBOARDING (the setup above is unfinished — finishing it is the user's first priority):
        - Ask for ONE missing value per message, in the order the gaps are listed. Never a list of questions.
        - Save each answer immediately with the matching tool above, confirm what was saved, then say what is still missing.
        - Never send the user to a settings page to type it in themselves — you have the tools for it.
        - When a tool answers that it needs confirmation, repeat what it would write and call it again only after the user agrees.
        """;

    /// <summary>
    /// Formats the situational block: what day it is, where the user is standing and what is
    /// still missing before they can invoice. Values are passed as strings so the preview can
    /// substitute placeholders without duplicating the layout.
    ///
    /// A line whose value is null or blank is left out entirely, the same way the DIČ line is:
    /// an empty "- Open entity:" would invite the model to invent one.
    /// </summary>
    /// <param name="today">Today's date, already formatted.</param>
    /// <param name="currentPage">Route the user is on, or null when the client did not send one.</param>
    /// <param name="openEntity">Record open on that page, or null when the page shows none.</param>
    /// <param name="setupGaps">Missing setup, or null when the tenant is fully configured.</param>
    public static string BuildSituationalContextBlock(
        string today,
        string? currentPage,
        string? openEntity,
        string? setupGaps)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SituationalContextHeader);
        sb.Append($"- Today's date: {today}");

        if (!string.IsNullOrWhiteSpace(currentPage))
            sb.Append($"{Environment.NewLine}- Current page: {currentPage}");

        if (!string.IsNullOrWhiteSpace(openEntity))
            sb.Append($"{Environment.NewLine}- Open record: {openEntity}");

        if (!string.IsNullOrWhiteSpace(setupGaps))
        {
            sb.Append($"{Environment.NewLine}- Setup not finished yet: {setupGaps}");

            // Only an unfinished tenant gets the onboarding instructions; for everyone else
            // they would be dead weight in every single request.
            sb.Append($"{Environment.NewLine}{Environment.NewLine}{OnboardingInstructions}");
        }

        return sb.ToString();
    }
}
