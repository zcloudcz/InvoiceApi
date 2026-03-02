namespace Fakvio.Application.Service;

/// <summary>
/// Builds the AI system prompt with tenant-specific business context.
/// The system prompt tells the AI assistant what it knows about the user's business:
/// client count, invoice count, overdue invoices, etc.
///
/// This makes the assistant context-aware — it can reference real business data
/// when answering questions about invoicing, clients, and billing.
/// </summary>
public interface IChatContextBuilder
{
    /// <summary>
    /// Builds a system prompt string containing the tenant's current business context.
    /// Queries the database for aggregate statistics and formats them into the prompt.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A formatted system prompt string for the AI provider.</returns>
    Task<string> BuildSystemPromptAsync(CancellationToken ct = default);
}
