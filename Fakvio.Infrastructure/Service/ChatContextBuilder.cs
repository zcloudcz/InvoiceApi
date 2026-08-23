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
    private readonly IReadOnlyList<IChatTool> _tools;
    private readonly IAiInstructionsService _aiInstructions;
    private readonly ILogger<ChatContextBuilder> _logger;

    public ChatContextBuilder(
        TenantDbContext context,
        IEnumerable<IChatTool> tools,
        IAiInstructionsService aiInstructions,
        ILogger<ChatContextBuilder> logger)
    {
        _context = context;

        // The capability list in the system prompt is generated from the registered tools,
        // so it can never drift from what the assistant can actually do.
        _tools = tools.ToList();
        _aiInstructions = aiInstructions;
        _logger = logger;
    }

    /// <summary>
    /// Queries tenant database for business statistics and builds a system prompt.
    /// Uses AsNoTracking for read-only queries (better performance).
    /// </summary>
    /// <param name="currentRoute">Route the client reported, or null when it sent none.</param>
    /// <param name="openEntity">Record open on that route, or null.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<string> BuildSystemPromptAsync(
        string? currentRoute = null,
        string? openEntity = null,
        CancellationToken ct = default)
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

            var situationalContext = AiSystemPrompt.BuildSituationalContextBlock(
                today: FormatToday(),
                currentPage: Sanitize(currentRoute, MaxRouteLength),
                openEntity: Sanitize(openEntity, MaxOpenEntityLength),
                setupGaps: await DescribeSetupGapsAsync(hasIssuer: issuer != null, ct));

            return AiSystemPrompt.Compose(
                companyBlock, customPrompt, appendix, businessContext, _tools, situationalContext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build chat context from database, using default prompt");

            // Fallback: basic prompt without business data, but still WITH the real capability list
            // (generated from the registered tools — no hand-maintained copy to go stale).
            return "You are Fakvio AI Assistant — a helpful invoicing and business assistant. " +
                   "You are DIRECTLY CONNECTED to the Fakvio invoicing system and CAN perform real actions. " +
                   "Use your tools when the user asks for these actions:\n" +
                   string.Join("\n", _tools.Select(t => $"- {t.ToolName}: {t.Description}")) +
                   "\nBe concise and professional. " +
                   "Respond in the same language the user writes in (Czech or English).";
        }
    }

    /// <summary>Caps mirroring the <c>SendMessageRequest</c> limits — see <see cref="Sanitize"/>.</summary>
    private const int MaxRouteLength = 200;
    private const int MaxOpenEntityLength = 100;

    /// <summary>
    /// Today's date as the model sees it. UTC, like every other timestamp in this app
    /// (overdue detection above included) — the app has no per-tenant time zone.
    /// The weekday is spelled out because "by Friday" questions are common and a model
    /// cannot reliably derive it from the date alone.
    /// </summary>
    private static string FormatToday()
    {
        var today = DateTime.UtcNow;
        return $"{today:yyyy-MM-dd} ({today.DayOfWeek})";
    }

    /// <summary>
    /// Trims client-supplied text before it is pasted into the system prompt: line breaks out
    /// (a newline would let a crafted route forge its own prompt section) and a hard length cap.
    /// The DTO carries the same limits, but the Functions host deserializes the request itself
    /// and runs no model validation — so the guard has to sit here too, where the value is used.
    /// </summary>
    private static string? Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var singleLine = value.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength];
    }

    /// <summary>
    /// Lists what still blocks the tenant from issuing an invoice, or null when nothing does.
    /// The assistant uses it to guide a fresh tenant instead of failing at the last step —
    /// and to stop offering "create an invoice" before that can possibly work.
    ///
    /// Two EXISTS queries; they only run for the prompt, so keep it at that.
    /// </summary>
    private async Task<string?> DescribeSetupGapsAsync(bool hasIssuer, CancellationToken ct)
    {
        var gaps = new List<string>();

        if (!hasIssuer)
            gaps.Add("the user's own company (issuer) is not set up");

        var hasInvoiceSequence = await _context.NumberSequence
            .AsNoTracking()
            .AnyAsync(s => s.DocumentType == EDocumentType.Invoice && s.IsActive, ct);
        if (!hasInvoiceSequence)
            gaps.Add("no invoice numbering series exists");

        var hasInvoiceTemplate = await _context.ContentTemplate
            .AsNoTracking()
            .AnyAsync(t => t.TemplateType == EContentTemplateType.InvoicePdf && t.IsActive, ct);
        if (!hasInvoiceTemplate)
            gaps.Add("no invoice PDF template exists");

        return gaps.Count == 0 ? null : string.Join("; ", gaps);
    }
}
