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
/// IMPORTANT: The system prompt must clearly state that the assistant IS connected
/// to the Fakvio system and CAN perform actions. Without this, models like Ollama
/// respond with "I'm not connected to any system" because they don't know they have tools.
/// </summary>
public class ChatContextBuilder : IChatContextBuilder
{
    private readonly TenantDbContext _context;
    private readonly ILogger<ChatContextBuilder> _logger;

    public ChatContextBuilder(TenantDbContext context, ILogger<ChatContextBuilder> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Queries tenant database for business statistics and builds a system prompt.
    /// Uses AsNoTracking for read-only queries (better performance).
    /// </summary>
    public async Task<string> BuildSystemPromptAsync(CancellationToken ct = default)
    {
        try
        {
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
                   "and navigate users to pages. Use your tools when the user asks for these actions. " +
                   "Be concise and professional. " +
                   "Respond in the same language the user writes in (Czech or English).";
        }
    }
}
