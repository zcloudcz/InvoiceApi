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
/// - Current tenant stats (clients, invoices, overdue amounts)
/// - Domain context (Czech invoicing, DPH/VAT)
///
/// This makes the AI responses more relevant and context-aware.
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

            // Build the system prompt with business context.
            var sb = new StringBuilder();
            sb.AppendLine("You are Fakvio AI Assistant — a helpful invoicing and business assistant.");
            sb.AppendLine("You help users with: creating invoices, understanding billing, managing clients,");
            sb.AppendLine("Czech tax regulations (DPH/VAT), payment reminders, and general business questions.");
            sb.AppendLine();
            sb.AppendLine("Important rules:");
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

            // Fallback: return a basic prompt without business data.
            return "You are Fakvio AI Assistant — a helpful invoicing and business assistant. " +
                   "You help users with creating invoices, managing clients, Czech tax regulations (DPH/VAT), " +
                   "and general business questions. Be concise and professional. " +
                   "Respond in the same language the user writes in (Czech or English).";
        }
    }
}
