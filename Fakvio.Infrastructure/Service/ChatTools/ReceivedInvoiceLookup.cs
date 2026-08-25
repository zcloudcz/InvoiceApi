using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ReceivedInvoice;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Identifies the ONE received invoice a write chat tool (approve / mark paid / delete)
/// is supposed to act on, from the parameters the model sent.
///
/// Why it is shared: the three write tools ask the same question ("which invoice?") and must
/// answer it identically. A per-tool copy is how "approve" and "delete" quietly end up picking
/// different invoices for the same words.
///
/// Why it is stricter than <see cref="GetReceivedInvoiceTool"/>, which resolves the same two
/// parameters on its own: a document number is matched as a substring, so "2024-1" can hit
/// several invoices. Showing the first match of an ambiguous read is harmless; approving or
/// deleting the first match is not — so here more than one candidate is an error that asks the
/// user to pick, never a silent choice.
///
/// Junior note: nothing here enforces business rules (which status may be approved, deleted, …).
/// Those live in <see cref="IReceivedInvoiceService"/> and must stay there — a copy in a chat
/// tool is a second source of truth that will drift.
/// </summary>
internal static class ReceivedInvoiceLookup
{
    /// <summary>
    /// The two ways a user can point at a received invoice, shared by every write tool.
    ///
    /// Both are optional in the schema because "at least one of them" is a rule JSON Schema
    /// cannot express; <see cref="ResolveAsync"/> enforces it and reports it to the model.
    /// </summary>
    public static readonly ChatToolParameter[] IdentityParameters =
    [
        new()
        {
            Name = "id",
            Type = ChatToolParameterType.Integer,
            Description = "Internal database ID of the received invoice (from a list or search result)"
        },
        new()
        {
            Name = "document_number",
            Type = ChatToolParameterType.String,
            Description = "Document number as printed on the invoice (e.g. '267708922'). " +
                          "Must match exactly one invoice."
        }
    ];

    /// <summary>
    /// Resolves the invoice from <c>id</c> (preferred) or <c>document_number</c>.
    /// Returns either the invoice or a ready-to-return failure explaining what to do next —
    /// never both, never neither.
    /// </summary>
    public static async Task<Resolution> ResolveAsync(
        IReceivedInvoiceService service,
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        parameters.TryGetValue("id", out var rawId);
        parameters.TryGetValue("document_number", out var rawDocumentNumber);

        var hasId = !string.IsNullOrWhiteSpace(rawId);
        var hasDocumentNumber = !string.IsNullOrWhiteSpace(rawDocumentNumber);

        if (!hasId && !hasDocumentNumber)
        {
            return Resolution.Failed(
                "Provide at least one of: id (database ID) or document_number (invoice number).");
        }

        // Path 1 — internal ID. Precise, so it wins whenever both are supplied.
        if (hasId)
        {
            // The schema declares 'id' as an integer and the executor validates that before the
            // tool runs, so this only guards direct calls (tests, future callers).
            if (!long.TryParse(rawId!.Trim(), out var id))
                return Resolution.Failed("Invalid id parameter — must be a numeric database ID.");

            var byId = await service.GetByIdAsync(id, ct);

            return byId is null
                ? Resolution.Failed($"Received invoice with ID {id} not found.")
                : Resolution.Found(byId);
        }

        // Path 2 — document number, matched case-insensitively as a substring, the way the
        // read tools already match it (a user rarely dictates the number character-perfect).
        var wanted = rawDocumentNumber!.Trim();
        var all = await service.GetAllAsync(ct: ct);
        var matches = all
            .Where(invoice => invoice.DocumentNumber != null &&
                              invoice.DocumentNumber.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => Resolution.Failed($"Received invoice with document number '{wanted}' not found."),
            1 => Resolution.Found(matches[0]),
            _ => Resolution.Failed(
                $"'{wanted}' matches {matches.Count} received invoices:\n{DescribeCandidates(matches)}\n" +
                "Say which one — the safest is to pass its id.")
        };
    }

    /// <summary>
    /// One-line description of an invoice, used both in the "which one did you mean?" list and
    /// in what the write tools report back, so the same invoice always reads the same way.
    /// </summary>
    public static string Describe(ReceivedInvoiceDto invoice)
        => $"ID={invoice.Id} | {invoice.DocumentNumber ?? "(no number)"} | {invoice.SupplierName} | " +
           $"{invoice.Status} | due: {ChatToolDates.Format(invoice.DueDate)} | " +
           $"{invoice.TotalWithVat:N2} {invoice.CurrencyCode}";

    /// <summary>Caps the candidate list — a model does not need fifty rows to ask one question.</summary>
    private const int MaxCandidatesShown = 10;

    private static string DescribeCandidates(IReadOnlyList<ReceivedInvoiceDto> matches)
    {
        var shown = matches.Take(MaxCandidatesShown).Select(invoice => $"  - {Describe(invoice)}");
        var suffix = matches.Count > MaxCandidatesShown
            ? $"\n  … and {matches.Count - MaxCandidatesShown} more"
            : string.Empty;

        return string.Join("\n", shown) + suffix;
    }

    /// <summary>
    /// Outcome of the lookup: exactly one of the two properties is set.
    /// A record instead of a tuple so call sites read as <c>resolution.Error</c>, not <c>.Item2</c>.
    /// </summary>
    internal sealed record Resolution
    {
        public ReceivedInvoiceDto? Invoice { get; private init; }

        public ChatToolResult? Error { get; private init; }

        public static Resolution Found(ReceivedInvoiceDto invoice) => new() { Invoice = invoice };

        public static Resolution Failed(string message) => new() { Error = ChatToolResult.Failure(message) };
    }
}
