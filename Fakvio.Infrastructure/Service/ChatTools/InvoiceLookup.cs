using System.Globalization;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// How every issued-invoice chat tool identifies the document it works with (issue #217).
///
/// All five lifecycle tools (get / complete / mark paid / send by e-mail / delete) accept the
/// same pair of parameters, so the schema and the resolution live here once. A second copy is
/// how one tool would start accepting an id the others reject — and the model would have to
/// learn per-tool exceptions.
///
/// Junior note: "at least one of id / document_number" is a rule JSON Schema cannot express
/// (it only knows required / optional per parameter), which is why both parameters are
/// optional in the schema and the rule is enforced in <see cref="ResolveAsync"/>. The same
/// approach is used by <c>GetReceivedInvoiceTool</c>.
/// </summary>
internal static class InvoiceLookup
{
    /// <summary>
    /// The identity parameters, shared by every lifecycle tool.
    /// Static readonly: the schema is constant, so there is no reason to rebuild it per tool.
    /// </summary>
    internal static readonly ChatToolParameter[] IdentitySchema =
    [
        new()
        {
            Name = "id",
            Type = ChatToolParameterType.Integer,
            Description = "Internal database ID of the issued invoice (shown as ID=… by list_invoices)"
        },
        new()
        {
            Name = "document_number",
            Type = ChatToolParameterType.String,
            Description = "Document number as printed on the invoice (e.g. 'FAK-2026-001')"
        }
    ];

    /// <summary>
    /// Resolves the invoice the parameters point at. Returns the DTO, or a ready-to-return
    /// error message for the model — never both, never neither.
    ///
    /// <paramref name="id"/> wins over <paramref name="document_number"/>: it is exact, while a
    /// document number is only unique per number sequence.
    /// </summary>
    internal static async Task<(InvoiceDto? Invoice, string? Error)> ResolveAsync(
        IInvoiceService invoiceService,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct)
    {
        parameters.TryGetValue("id", out var rawId);
        parameters.TryGetValue("document_number", out var rawDocumentNumber);

        var hasId = !string.IsNullOrWhiteSpace(rawId);
        var hasDocumentNumber = !string.IsNullOrWhiteSpace(rawDocumentNumber);

        if (!hasId && !hasDocumentNumber)
        {
            return (null, "Provide at least one of: id (database ID) or document_number (invoice number).");
        }

        if (hasId)
        {
            // The executor validated that the value is an integer, but it dispatches the RAW
            // string, so a model that sent " 42 " still arrives with the spaces (#268).
            // The false branch is not dead code for a tool called directly: without it a
            // malformed id would silently fall through to the document_number lookup.
            if (!long.TryParse(rawId!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                return (null, $"Invalid id: '{rawId}'. It must be a numeric database ID.");

            var byId = await invoiceService.GetInvoiceByIdAsync(id, ct);
            return byId is null
                ? (null, $"Issued invoice with ID {id} not found.")
                : (byId, null);
        }

        var documentNumber = rawDocumentNumber!.Trim();
        var byNumber = await invoiceService.GetInvoiceByDocumentNumberAsync(documentNumber, ct);

        return byNumber is null
            ? (null, $"Issued invoice with document number '{documentNumber}' not found.")
            : (byNumber, null);
    }

    /// <summary>
    /// One-line identification of a document, used by every preview and confirmation message
    /// so the user always recognises which invoice is about to change.
    /// </summary>
    internal static string Describe(InvoiceDto invoice)
        => $"{invoice.DocumentType} {invoice.DocumentNumber ?? $"(no number, ID {invoice.Id})"} " +
           $"for {invoice.ClientName}, {invoice.TotalWithVat:N2} {invoice.CurrencyCode}";
}
