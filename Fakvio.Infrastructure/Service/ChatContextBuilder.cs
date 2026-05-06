using System.Text;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Builds an AI system prompt enriched with the tenant's current business data.
/// The system prompt gives the AI assistant knowledge about:
/// - What the app does (invoicing)
/// - What tools/capabilities it has (ARES lookup, invoice creation, navigation, etc.)
/// - Current tenant stats (clients, invoices, overdue amounts)
/// - Domain context (Czech invoicing, DPH/VAT)
///
/// Prompt structure (from top to bottom):
/// 1. App identity line — always hardcoded.
/// 2. Company identity — auto from tenant DB (NOT editable).
/// 3. Style + rules sections — EDITABLE by SysAdmin via AiSystemPromptCustom in DB.
///    When AiSystemPromptCustom is non-empty it REPLACES the hardcoded sections.
///    When null/empty the hardcoded defaults are used.
/// 4. Appendix — EDITABLE by SysAdmin via AiSystemPromptAppendix.
///    When non-empty, appended after the main sections (custom or default).
/// 5. Business context stats — auto from tenant DB (NOT editable).
///
/// IMPORTANT: The system prompt must clearly state that the assistant IS connected
/// to the Fakvio system and CAN perform actions. Without this, models like Ollama
/// respond with "I'm not connected to any system" because they don't know they have tools.
/// </summary>
public class ChatContextBuilder : IChatContextBuilder
{
    private readonly TenantDbContext _context;
    private readonly IAiInstructionsService _aiInstructions;
    private readonly ILogger<ChatContextBuilder> _logger;

    public ChatContextBuilder(
        TenantDbContext context,
        IAiInstructionsService aiInstructions,
        ILogger<ChatContextBuilder> logger)
    {
        _context = context;
        _aiInstructions = aiInstructions;
        _logger = logger;
    }

    /// <summary>
    /// Queries tenant database for business statistics and builds a system prompt.
    /// Also reads editable instructions from cache (fast) via IAiInstructionsService.
    /// Uses AsNoTracking for read-only queries (better performance).
    /// </summary>
    public async Task<string> BuildSystemPromptAsync(CancellationToken ct = default)
    {
        try
        {
            // Load editable instructions from cache (populated from master DB).
            // This call is very fast on cache hit — no DB round-trip needed.
            var (customPrompt, appendix) = await _aiInstructions.GetCachedInstructionsAsync(ct);

            // Gather business context from the tenant database.
            // Each query is simple and fast — counts and sums only.
            var totalClients = await _context.Client
                .AsNoTracking()
                .Where(c => !c.IsIssuer && c.IsActive)
                .CountAsync(ct);

            var openInvoices = await _context.Invoice
                .AsNoTracking()
                .Where(i => i.Status == EInvoiceStatus.Completed)
                .CountAsync(ct);

            var openInvoicesTotal = await _context.Invoice
                .AsNoTracking()
                .Where(i => i.Status == EInvoiceStatus.Completed)
                .SumAsync(i => i.TotalWithVat, ct);

            var overdueInvoices = await _context.Invoice
                .AsNoTracking()
                .Where(i => i.Status == EInvoiceStatus.Completed && i.DueDate < DateTime.UtcNow)
                .CountAsync(ct);

            var paidInvoicesCount = await _context.Invoice
                .AsNoTracking()
                .Where(i => i.Status == EInvoiceStatus.Paid)
                .CountAsync(ct);

            // Load the user's company (issuer) — critical for AI to know who "we" are.
            var issuer = await _context.Client
                .AsNoTracking()
                .Where(c => c.IsIssuer)
                .Select(c => new { c.CompanyName, c.RegistrationNumber, c.TaxNumber })
                .FirstOrDefaultAsync(ct);

            // Build the system prompt with capabilities and business context.
            var sb = new StringBuilder();
            sb.AppendLine("You are Fakvio AI Assistant — connected to the Fakvio invoicing system.");
            sb.AppendLine();

            // ── Section 1: Company identity (NOT editable) ────────────────────
            // Tell the AI exactly who the user's company is — critical for import_invoice tool.
            if (issuer != null)
            {
                sb.AppendLine("YOUR COMPANY (the user's company — you represent this entity):");
                sb.AppendLine($"- Name: {issuer.CompanyName}");
                sb.AppendLine($"- IČO: {issuer.RegistrationNumber}");
                if (!string.IsNullOrEmpty(issuer.TaxNumber))
                    sb.AppendLine($"- DIČ: {issuer.TaxNumber}");
                sb.AppendLine("When importing invoices: if YOUR IČO appears as the issuer (dodavatel), it's an ISSUED invoice.");
                sb.AppendLine("If YOUR IČO appears as the recipient (odběratel), it's a RECEIVED invoice.");
                sb.AppendLine();
            }

            // ── Section 2: Style + rules (EDITABLE — use custom if set, else hardcoded default) ──
            if (!string.IsNullOrEmpty(customPrompt))
            {
                // SysAdmin-defined instructions replace the hardcoded sections entirely.
                sb.AppendLine(customPrompt);
                sb.AppendLine();
            }
            else
            {
                // Hardcoded default style and rules.
                sb.AppendLine("RESPONSE STYLE: Answer in ONE sentence maximum. No greetings, no filler, no repetition.");
                sb.AppendLine("Just do what the user asks and confirm the result briefly.");
                sb.AppendLine("Respond in the same language the user writes in (Czech or English).");
                sb.AppendLine();
                sb.AppendLine("TOOLS (use them, don't ask unnecessary questions):");
                sb.AppendLine("- ares_lookup: Look up Czech company by IČO");
                sb.AppendLine("- create_client: Create client from IČO (auto-fills from ARES)");
                sb.AppendLine("- create_invoice: Create a new issued invoice with line items");
                sb.AppendLine("- import_invoice: Import invoice from pasted text/data — auto-detects issued vs received");
                sb.AppendLine("  by matching IČO against the company DB, finds client automatically, preserves all dates exactly");
                sb.AppendLine("- navigate: Navigate user to a page");
                sb.AppendLine("- export_invoice: Export/download invoice as PDF (by document number or client name)");
                sb.AppendLine("- get_received_invoice: Get FULL detail of a received (incoming) invoice by ID or document number.");
                sb.AppendLine("  Returns supplier info, ALL line items with quantities/prices/VAT rates, VAT breakdown totals,");
                sb.AppendLine("  payment info, dates, status. Use this to answer questions like");
                sb.AppendLine("  'proč má přijatá faktura 267708922 špatnou celkovou částku?'");
                sb.AppendLine("- list_received_invoices: List/browse received invoices with filters");
                sb.AppendLine("  (status, supplier, date range, amount range, currency, overdue).");
                sb.AppendLine("- search_received_invoices: Full-text search across received invoices");
                sb.AppendLine("  (document number, supplier name, variable symbol, amount).");
                sb.AppendLine();
                sb.AppendLine("IMPORT RULES:");
                sb.AppendLine("- When user pastes invoice text, extract ALL data and call import_invoice immediately.");
                sb.AppendLine("- ALL dates (issue_date, due_date, taxable_supply_date) must be EXACTLY from the document.");
                sb.AppendLine("- NEVER generate, guess, or use today's date. If a date is missing, pass null.");
                sb.AppendLine("- The tool auto-determines issued/received — do NOT ask the user.");
                sb.AppendLine("- The tool auto-finds the client by IČO — do NOT ask the user.");
                sb.AppendLine("- If the client doesn't exist, the tool will tell you — then use create_client.");
                sb.AppendLine();
                sb.AppendLine("RULES:");
                sb.AppendLine("- Use tools when asked. Never claim actions without tool confirmation.");
                sb.AppendLine("- Don't ask about things you can determine from the data.");
                sb.AppendLine();
            }

            // ── Section 3: Appendix (EDITABLE — appended when set) ───────────
            if (!string.IsNullOrEmpty(appendix))
            {
                sb.AppendLine(appendix);
                sb.AppendLine();
            }

            // ── Section 4: Business context stats (NOT editable) ─────────────
            sb.AppendLine("Current tenant business context:");
            sb.AppendLine($"- Total active clients: {totalClients}");
            sb.AppendLine($"- Open (unpaid) invoices: {openInvoices} (total: {openInvoicesTotal:N2} CZK)");
            sb.AppendLine($"- Overdue invoices: {overdueInvoices}");
            sb.AppendLine($"- Paid invoices: {paidInvoicesCount}");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build chat context from database, using default prompt");

            // Fallback: return a basic prompt without business data but WITH capabilities.
            return "You are Fakvio AI Assistant — a helpful invoicing and business assistant. " +
                   "You are DIRECTLY CONNECTED to the Fakvio invoicing system and CAN perform real actions. " +
                   "You can: look up companies by IČO (ARES), create clients, create invoices, " +
                   "look up / list / search received (incoming) invoices by ID, document number, supplier, date, or amount, " +
                   "and navigate users to pages. Use your tools when the user asks for these actions. " +
                   "Be concise and professional. " +
                   "Respond in the same language the user writes in (Czech or English).";
        }
    }
}
