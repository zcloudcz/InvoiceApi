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
/// This class only gathers the tenant data; the prompt layout and the built-in text
/// live in <see cref="AiSystemPrompt"/>, shared with the SysAdmin preview so the two
/// can never drift apart.
///
/// The style/tools/rules block is editable by a SysAdmin — see
/// <see cref="IAiInstructionsService"/>. Company identity and business statistics are
/// always generated here and cannot be overridden.
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
    /// Uses AsNoTracking for read-only queries (better performance).
    /// </summary>
    public async Task<string> BuildSystemPromptAsync(CancellationToken ct = default)
    {
        try
        {
            // SysAdmin-editable instructions. Served from IMemoryCache on the hot path,
            // so this normally costs no database round-trip.
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

            // Tenants without a configured issuer simply get no company block.
            var companyBlock = issuer == null
                ? null
                : AiSystemPrompt.BuildCompanyBlock(
                    issuer.CompanyName,
                    issuer.RegistrationNumber,
                    issuer.TaxNumber);

            var businessContext = AiSystemPrompt.BuildBusinessContextBlock(
                totalClients: totalClients.ToString(),
                openInvoices: $"{openInvoices} (total: {openInvoicesTotal:N2} CZK)",
                overdueInvoices: overdueInvoices.ToString(),
                paidInvoices: paidInvoicesCount.ToString());

            return AiSystemPrompt.Compose(companyBlock, customPrompt, appendix, businessContext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build chat context from database, using default prompt");

            // Fallback: return a basic prompt without business data but WITH capabilities.
            return "You are Fakvio AI Assistant — a helpful invoicing and business assistant. " +
                   "You are DIRECTLY CONNECTED to the Fakvio invoicing system and CAN perform real actions. " +
                   "You can: look up companies by IČO (ARES), create clients, create invoices, " +
                   "look up / list / search received (incoming) invoices by ID, document number, supplier, date, or amount, " +
                   "attach files to entities and list existing attachments, " +
                   "and navigate users to pages. Use your tools when the user asks for these actions. " +
                   "Be concise and professional. " +
                   "Respond in the same language the user writes in (Czech or English).";
        }
    }
}
