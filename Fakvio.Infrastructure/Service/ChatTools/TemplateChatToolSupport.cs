using System.Globalization;
using System.Text;
using Fakvio.Contracts.Dto.ContentTemplate;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Shared plumbing for the five template chat tools (issue #225):
/// <see cref="ListInvoiceTemplatesTool"/>, <see cref="GetInvoiceTemplateTool"/>,
/// <see cref="ListContentTemplatesTool"/>, <see cref="GetContentTemplateTool"/> and
/// <see cref="SetDefaultContentTemplateTool"/>.
///
/// Two different things are called "template" in Fakvio and the model must never mix them up,
/// so the wording each tool uses is defined once, here:
/// <list type="bullet">
/// <item><b>Invoice template</b> (<c>InvoiceTemplate</c>) — a blueprint of invoice DATA
/// (line items, currency, payment details) used to create a new invoice quickly.</item>
/// <item><b>Content template</b> (<c>ContentTemplate</c>) — the HTML that renders a PDF or an
/// e-mail body. This is the one that has a "default" per type and language.</item>
/// </list>
///
/// Junior note: every number and date below is formatted with <see cref="CultureInfo.InvariantCulture"/>.
/// The text goes into the AI prompt, so it must read the same no matter which culture the
/// server thread happens to run under (under <c>th-TH</c> a plain <c>ToString("yyyy-MM-dd")</c>
/// would print a Buddhist year — the same trap <c>ChatToolDates</c> documents for parsing).
/// </summary>
internal static class TemplateChatToolSupport
{
    /// <summary>
    /// Where a user edits the HTML of a content template. Editing HTML through the chat is
    /// deliberately out of scope (story #149, question 3) — the WYSIWYG editor on this page is
    /// the right tool — so every content-template output points there instead.
    /// </summary>
    public const string ContentTemplateEditRoute = "/content-templates";

    // ─── Parameter parsing ────────────────────────────────────────────────
    // Presence, type and allowed values are validated centrally by ChatToolExecutor before a
    // tool runs, so these helpers only translate "not supplied" into a default — they are not
    // a second validation.

    /// <summary>
    /// Reads a required record id. The executor guarantees it is present and an integer,
    /// so parsing cannot fail here.
    /// </summary>
    public static long RequiredId(Dictionary<string, string> parameters, string name)
        => long.Parse(parameters[name].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads an optional text parameter, or null when it was not sent. Blank counts as
    /// "not sent" — models happily send an empty string for a filter they do not want.
    /// </summary>
    public static string? OptionalText(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;

    /// <summary>Reads an optional boolean parameter, or null when it was not sent.</summary>
    public static bool? OptionalFlag(Dictionary<string, string> parameters, string name)
        => parameters.TryGetValue(name, out var raw) && bool.TryParse(raw.Trim(), out var flag)
            ? flag
            : null;

    /// <summary>
    /// Reads an optional positive whole number, falling back to <paramref name="defaultValue"/>.
    /// Zero and negatives fall back too: a page 0 is not a filter, it is a broken call.
    /// </summary>
    public static int OptionalCount(Dictionary<string, string> parameters, string name, int defaultValue)
        => parameters.TryGetValue(name, out var raw)
           && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
           && value > 0
            ? value
            : defaultValue;

    /// <summary>
    /// Reads an optional enum parameter by name (case-insensitive), or null when it was not
    /// sent. An unknown value cannot reach this method — the schema's AllowedValues are
    /// enforced centrally — so null really does mean "no filter".
    /// </summary>
    public static TEnum? OptionalEnum<TEnum>(Dictionary<string, string> parameters, string name)
        where TEnum : struct, Enum
        => OptionalText(parameters, name) is { } raw && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var value)
            ? value
            : null;

    // ─── Invoice templates (invoice data blueprints) ──────────────────────

    /// <summary>
    /// One invoice template on one line, id first — the id is the handle
    /// <see cref="GetInvoiceTemplateTool"/> needs, and the model can only quote an id it
    /// has been shown.
    /// </summary>
    public static string DescribeInvoiceTemplate(InvoiceTemplateDto template)
    {
        var details = new List<string>
        {
            $"name: {template.Name}",
            $"document type: {template.DocumentType}"
        };

        if (!string.IsNullOrWhiteSpace(template.ClientName))
            details.Add($"client: {template.ClientName}");
        if (!string.IsNullOrWhiteSpace(template.CurrencyCode))
            details.Add($"currency: {template.CurrencyCode}");

        details.Add($"used {template.UsageCount}x");

        if (!template.IsActive)
            details.Add("INACTIVE");

        return $"ID={template.Id} | {string.Join(" | ", details)}";
    }

    /// <summary>
    /// Renders one invoice template as a text block — one fact per line, because this text is
    /// the model's only source for the answer and anything missing gets invented instead.
    /// </summary>
    public static string FormatInvoiceTemplate(InvoiceTemplateDto template)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Invoice template (a blueprint of invoice data, used to create invoices quickly):");
        sb.AppendLine($"  ID: {template.Id}");
        sb.AppendLine($"  Name: {template.Name}");

        if (!string.IsNullOrWhiteSpace(template.Description))
            sb.AppendLine($"  Description: {template.Description}");

        sb.AppendLine($"  Document type: {template.DocumentType}");
        sb.AppendLine($"  Issuer: {template.IssuerName} (ID {template.IssuerId})");
        sb.AppendLine($"  Default client: {(template.ClientName is null ? "(not set)" : $"{template.ClientName} (ID {template.ClientId})")}");
        sb.AppendLine($"  Due date offset: {template.DueDateOffsetDays} days after the issue date");
        sb.AppendLine($"  Currency: {template.CurrencyCode}");
        sb.AppendLine($"  Number sequence: {template.NumberSequenceName ?? "(the default sequence for this document type)"}");

        AppendPayment(sb, template);

        if (!string.IsNullOrWhiteSpace(template.Notes))
            sb.AppendLine($"  Notes: {template.Notes}");

        sb.AppendLine($"  Used: {template.UsageCount}x " +
                      $"{(template.LastUsedAt is { } lastUsed ? $"(last on {FormatDate(lastUsed)})" : "(never yet)")}");
        sb.AppendLine($"  Active: {YesNo(template.IsActive)}");

        AppendItems(sb, template);

        return sb.ToString();
    }

    // ─── Content templates (PDF / e-mail HTML) ────────────────────────────

    /// <summary>
    /// One content template on one line. The default flag is spelled out because it is the
    /// single thing <see cref="SetDefaultContentTemplateTool"/> changes.
    /// </summary>
    public static string DescribeContentTemplate(ContentTemplateDto template)
    {
        var details = new List<string>
        {
            $"name: {template.Name}",
            $"type: {template.TemplateType}",
            $"language: {template.Language}"
        };

        if (template.IsDefault)
            details.Add("DEFAULT");
        if (!template.IsActive)
            details.Add("INACTIVE");

        return $"ID={template.Id} | {string.Join(" | ", details)}";
    }

    /// <summary>
    /// Renders one content template as a text block.
    ///
    /// The HTML body itself is deliberately NOT included — only its size. Chat cannot edit it
    /// (see <see cref="ContentTemplateEditRoute"/>), and a full template is tens of kilobytes
    /// that would be paid for in every following message of the conversation.
    /// </summary>
    public static string FormatContentTemplate(ContentTemplateDto template)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Content template (the HTML that renders a PDF document or an e-mail body):");
        sb.AppendLine($"  ID: {template.Id}");
        sb.AppendLine($"  Name: {template.Name}");
        sb.AppendLine($"  Type: {template.TemplateType}");
        sb.AppendLine($"  Language: {template.Language}");
        sb.AppendLine($"  Default for this type and language: {YesNo(template.IsDefault)}");
        sb.AppendLine($"  Active: {YesNo(template.IsActive)}");

        if (!string.IsNullOrWhiteSpace(template.Description))
            sb.AppendLine($"  Description: {template.Description}");

        if (!string.IsNullOrWhiteSpace(template.Subject))
            sb.AppendLine($"  E-mail subject: {template.Subject}");

        sb.AppendLine(
            $"  HTML body: {template.HtmlBody.Length} characters — not shown here. " +
            $"The HTML is edited in the visual editor at {ContentTemplateEditRoute}, not through chat.");
        sb.AppendLine($"  Created: {FormatDate(template.CreatedAt)}" +
                      $"{(template.UpdatedAt is { } updated ? $", last changed: {FormatDate(updated)}" : string.Empty)}");

        return sb.ToString();
    }

    /// <summary>Date as ISO yyyy-MM-dd — see the culture note on the class.</summary>
    public static string FormatDate(DateTime value)
        => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>
    /// Appends the payment details the template pre-fills. Empty fields are left out — an empty
    /// "IBAN:" label only invites the model to fill it in from imagination.
    /// </summary>
    private static void AppendPayment(StringBuilder sb, InvoiceTemplateDto template)
    {
        var payment = new List<string>();

        if (template.PaymentMethod is { } method)
            payment.Add($"method: {method}");
        if (!string.IsNullOrWhiteSpace(template.BankAccountNumber))
            payment.Add($"account: {template.BankAccountNumber}");
        if (!string.IsNullOrWhiteSpace(template.IBAN))
            payment.Add($"IBAN: {template.IBAN}");
        if (!string.IsNullOrWhiteSpace(template.SWIFT))
            payment.Add($"SWIFT: {template.SWIFT}");
        if (!string.IsNullOrWhiteSpace(template.VariableSymbol))
            payment.Add($"VS: {template.VariableSymbol}");
        if (!string.IsNullOrWhiteSpace(template.ConstantSymbol))
            payment.Add($"KS: {template.ConstantSymbol}");
        if (!string.IsNullOrWhiteSpace(template.SpecificSymbol))
            payment.Add($"SS: {template.SpecificSymbol}");

        if (payment.Count > 0)
            sb.AppendLine($"  Payment: {string.Join(" | ", payment)}");
    }

    /// <summary>
    /// Appends the pre-filled line items — the reason an invoice template exists at all.
    /// A text row (<c>IsTextRow</c>) carries no amounts, so it is printed as plain text.
    /// </summary>
    private static void AppendItems(StringBuilder sb, InvoiceTemplateDto template)
    {
        if (template.InvoiceItem.Count == 0)
        {
            sb.AppendLine("  Items: none — the template pre-fills only the header data.");
            return;
        }

        sb.AppendLine("  Items:");
        foreach (var item in template.InvoiceItem.OrderBy(item => item.OrderIndex))
        {
            sb.AppendLine($"    {DescribeItem(item, template.CurrencyCode)}");
        }
    }

    private static string DescribeItem(InvoiceItemDto item, string currencyCode)
        => item.IsTextRow
            ? $"{item.Description} (text row, no amount)"
            : $"{item.Description} | {Amount(item.Quantity)} {item.Unit} x {Amount(item.UnitPrice)} {currencyCode} | " +
              $"VAT {Amount(item.VatRatePercentage)} % | total with VAT: {Amount(item.TotalWithVat)} {currencyCode}";

    private static string Amount(decimal value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}
