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

            // Build the system prompt with capabilities and business context.
            var sb = new StringBuilder();
            sb.AppendLine("You are Fakvio AI Assistant — a helpful invoicing and business assistant.");
            sb.AppendLine("You are DIRECTLY CONNECTED to the Fakvio invoicing system and CAN perform real actions.");
            sb.AppendLine();
            sb.AppendLine("YOUR CAPABILITIES (you have tools for these — use them when the user asks):");
            sb.AppendLine("- Look up Czech companies by IČO (registration number) via the ARES registry");
            sb.AppendLine("- Create new clients in the system from ARES data");
            sb.AppendLine("- Create invoices with line items, quantities, and prices");
            sb.AppendLine("- Navigate the user to specific pages (new invoice, client list, invoice list, etc.)");
            sb.AppendLine("- Answer questions about billing, Czech tax (DPH/VAT), and the user's business data");
            sb.AppendLine();
            sb.AppendLine("IMPORTANT RULES FOR ACTIONS:");
            sb.AppendLine("- When the user asks you to create an invoice, look up a company,");
            sb.AppendLine("  add a client, or navigate somewhere — use your tools to do it.");
            sb.AppendLine("- NEVER claim you performed an action (created invoice, looked up company, etc.)");
            sb.AppendLine("  unless you actually used a tool and received a confirmation result.");
            sb.AppendLine("- Do NOT say you can't do it or that you're not connected to a system.");
            sb.AppendLine("- If a tool execution fails, tell the user about the error honestly.");
            sb.AppendLine();
            sb.AppendLine("INVOICE CREATION RULES:");
            sb.AppendLine("- Each invoice has a Variable Symbol (VS / variabilní symbol) — a unique payment identifier.");
            sb.AppendLine("- The VS is auto-generated from the document number (digits only, max 10 chars).");
            sb.AppendLine("- If the system reports that an invoice with the same VS already exists,");
            sb.AppendLine("  inform the user about the duplicate and do NOT create a new invoice.");
            sb.AppendLine("- When importing or creating invoices, always check the tool result for duplicate warnings.");
            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine("- Be concise and professional.");
            sb.AppendLine("- When discussing Czech tax/invoicing rules, mention relevant legislation if applicable.");
            sb.AppendLine("- You can reference the user's business data shown below.");
            sb.AppendLine("- If you don't know something, say so — don't make up data.");
            sb.AppendLine("- Respond in the same language the user writes in (Czech or English).");
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
