using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using iText.Html2pdf;
using iText.Kernel.Pdf;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// PDF export service that uses iText7 pdfhtml to convert HTML templates into PDF documents.
/// It loads invoice data from the database, resolves the HTML template from ContentTemplate,
/// populates it with Handlebars-style placeholders (e.g. {{CompanyName}}), and converts to PDF.
///
/// Template resolution order:
/// 1. ContentTemplate with type InvoicePdf/CreditNotePdf (default for the document type)
/// 2. Built-in fallback template (hardcoded HTML)
/// </summary>
public class PdfExportService : IPdfExportService
{
    private readonly TenantDbContext _context;
    private readonly IContentTemplateService _contentTemplateService;
    private readonly IQrPaymentService _qrPaymentService;
    private readonly ILogger<PdfExportService> _logger;

    public PdfExportService(
        TenantDbContext context,
        IContentTemplateService contentTemplateService,
        IQrPaymentService qrPaymentService,
        ILogger<PdfExportService> logger)
    {
        _context = context;
        _contentTemplateService = contentTemplateService;
        _qrPaymentService = qrPaymentService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<byte[]> GenerateInvoicePdfAsync(long invoiceId, CancellationToken ct = default)
    {
        // Delegate to the overload with null templateId — uses default template
        return GenerateInvoicePdfAsync(invoiceId, null, ct);
    }

    /// <inheritdoc />
    public async Task<byte[]> GenerateInvoicePdfAsync(long invoiceId, long? contentTemplateId, CancellationToken ct = default)
    {
        _logger.LogInformation("Generating PDF for invoice {InvoiceId}", invoiceId);

        // Load the invoice with all related data (client, issuer, items, currency).
        // AsSplitQuery: This query includes multiple collection navigations (Client.Address,
        // Client.Contact, Issuer.Address, Issuer.Contact, InvoiceItem) — without split query,
        // the single JOIN produces a massive cartesian product (rows = addresses x contacts x items).
        var invoice = await _context.Invoice
            .AsSplitQuery()
            .Include(i => i.Client).ThenInclude(c => c!.Address)
            .Include(i => i.Client).ThenInclude(c => c!.Contact)
            .Include(i => i.Issuer).ThenInclude(c => c.Address)
            .Include(i => i.Issuer).ThenInclude(c => c.Contact)
            .Include(i => i.InvoiceItem).ThenInclude(item => item.ReverseChargeCode)
            .Include(i => i.Currency)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException($"Invoice with ID {invoiceId} not found.");

        // Determine the PDF template type from the invoice's document type.
        // All four document types have their own distinct PDF template.
        // The mapping lives here in one place — no scattered switch statements.
        var templateType = ResolveTemplateType(invoice.DocumentType);

        // Read the client's preferred language for document generation.
        // Falls back to "cs" (Czech) when no client or no language is set.
        var clientLanguage = invoice.Client?.Language ?? "cs";

        // Resolve the PDF template from ContentTemplate system.
        // Priority: 1) explicit contentTemplateId (user selected),
        //           2) default for document type + client language (with any-language fallback),
        //           3) built-in fallback
        string htmlTemplate;

        if (contentTemplateId.HasValue)
        {
            // User explicitly selected a specific template — load it by ID
            var specificTemplate = await _contentTemplateService.GetByIdAsync(contentTemplateId.Value, ct);
            if (specificTemplate != null)
            {
                htmlTemplate = specificTemplate.HtmlBody;
                _logger.LogInformation("Using user-selected template '{TemplateName}' (ID {TemplateId}) for invoice {InvoiceId}",
                    specificTemplate.Name, contentTemplateId.Value, invoiceId);
            }
            else
            {
                // Invalid template ID — throw so the caller knows the requested template doesn't exist
                throw new KeyNotFoundException($"Content template with ID {contentTemplateId.Value} not found.");
            }
        }
        else
        {
            // No explicit template — use the default for the document type + client language.
            // The overload with language parameter has a built-in fallback to any-language default.
            var contentTemplate = await _contentTemplateService.GetDefaultByTypeAsync(templateType, clientLanguage, ct);
            if (contentTemplate != null)
            {
                htmlTemplate = contentTemplate.HtmlBody;
                _logger.LogInformation("Using default template '{TemplateName}' (type {Type}, lang {Lang}) for invoice {InvoiceId}",
                    contentTemplate.Name, templateType, clientLanguage, invoiceId);
            }
            else
            {
                // No template configured at all — use the built-in fallback HTML for the doc type
                htmlTemplate = GetDefaultHtmlTemplate(invoice.DocumentType);
                _logger.LogInformation("No content template found for type {Type}, using built-in default for invoice {InvoiceId}",
                    templateType, invoiceId);
            }
        }

        // Generate QR code for the invoice (QR Platba+F or QR Faktura).
        // Non-critical — if QR generation fails, the PDF is still generated without it.
        string? qrCodeBase64 = null;
        try
        {
            var qrBytes = await _qrPaymentService.GenerateQrCodeImageAsync(invoiceId, pixelsPerModule: 8, ct);
            qrCodeBase64 = Convert.ToBase64String(qrBytes);
            _logger.LogInformation("Generated QR code for invoice {InvoiceId} ({Size} bytes)", invoiceId, qrBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QR code generation failed for invoice {InvoiceId}, PDF will be generated without QR", invoiceId);
        }

        // Replace all Handlebars-style placeholders with actual invoice data
        var html = ReplacePlaceholders(htmlTemplate, invoice, qrCodeBase64);

        // Convert the populated HTML to a PDF byte array
        return await GeneratePdfFromHtmlAsync(html, ct);
    }

    /// <inheritdoc />
    public Task<byte[]> GeneratePdfFromHtmlAsync(string html, CancellationToken ct = default)
    {
        _logger.LogInformation("Converting HTML to PDF ({Length} chars)", html.Length);

        // Use a MemoryStream to collect the PDF bytes.
        // We must NOT dispose the stream before reading, because HtmlConverter.ConvertToPdf
        // closes the PdfDocument/PdfWriter which also closes the underlying stream.
        // Instead, we copy bytes out after conversion completes.
        var memoryStream = new MemoryStream();

        // Create iText7 PdfWriter that writes into our MemoryStream.
        // SetCloseStream(false) prevents the PdfWriter from closing our MemoryStream
        // when the PdfDocument is closed, so we can still read the bytes afterward.
        var pdfWriter = new PdfWriter(memoryStream);
        pdfWriter.SetCloseStream(false);

        // Create a PdfDocument backed by the PdfWriter
        using var pdfDocument = new PdfDocument(pdfWriter);

        // Set default page size to A4 (standard European invoice format)
        pdfDocument.SetDefaultPageSize(iText.Kernel.Geom.PageSize.A4);

        // ConverterProperties allows customization of the HTML-to-PDF conversion
        var converterProperties = new ConverterProperties();

        // Convert the HTML string into PDF pages using iText7 pdfhtml
        HtmlConverter.ConvertToPdf(html, pdfDocument, converterProperties);

        // Get the byte array from the stream (stream is still open thanks to SetCloseStream(false))
        var result = memoryStream.ToArray();

        _logger.LogInformation("PDF generated successfully ({Size} bytes)", result.Length);

        // Clean up the stream
        memoryStream.Dispose();

        // Return the PDF as a byte array
        return Task.FromResult(result);
    }

    /// <summary>
    /// Replaces Handlebars-style placeholders (e.g. {{CompanyName}}) in the HTML template
    /// with the actual values from the invoice entity.
    /// The {{QrCodeImage}} placeholder is replaced with an inline base64 PNG image tag,
    /// or removed entirely if QR code generation failed.
    /// </summary>
    internal static string ReplacePlaceholders(string html, Domain.Entities.Invoice invoice, string? qrCodeBase64 = null)
    {
        // A non-VAT payer's PDF must not mention VAT — strip the VAT parts of the template
        // first, so the item rows below know whether the VAT column is still there.
        // Document language (client's) for localized labels in the PDF.
        var docLang = invoice.Client?.Language ?? "cs";
        var showVatColumn = true;
        if (invoice.Issuer is { IsVatPayer: false })
        {
            html = StripVatFromTemplate(html, docLang, out var vatColumnRemoved);
            showVatColumn = !vatColumnRemoved;
        }

        // Get issuer (company that sends the invoice) and client (recipient) addresses
        var issuerAddress = invoice.Issuer?.Address?.FirstOrDefault();
        var clientAddress = invoice.Client?.Address?.FirstOrDefault();

        // Resolve issuer contacts by type — email and phone are shown separately in the template
        var issuerEmail = invoice.Issuer?.Contact?
            .FirstOrDefault(c => c.ContactType == Domain.Enums.EContactType.Email)?.ContactValue ?? "";
        var issuerPhone = invoice.Issuer?.Contact?
            .FirstOrDefault(c => c.ContactType == Domain.Enums.EContactType.Phone)?.ContactValue ?? "";

        var clientContact = invoice.Client?.Contact?.FirstOrDefault();


        // Localized document type label based on client language
        var documentTypeLabel = GetDocumentTypeLabel(invoice.DocumentType, docLang);

        // Dictionary maps each placeholder name to its actual value
        var replacements = new Dictionary<string, string>
        {
            // Issuer (sender) information
            ["IssuerName"] = invoice.Issuer?.CompanyName ?? "",
            ["IssuerRegistrationNumber"] = invoice.Issuer?.RegistrationNumber ?? "",
            ["IssuerTaxNumber"] = invoice.Issuer?.TaxNumber ?? "",
            ["IssuerStreet"] = issuerAddress?.Street ?? "",
            ["IssuerCity"] = issuerAddress?.City ?? "",
            ["IssuerPostalCode"] = issuerAddress?.PostalCode ?? "",
            ["IssuerCountry"] = issuerAddress?.Country ?? "",
            ["IssuerEmail"] = issuerEmail,
            ["IssuerPhone"] = issuerPhone,

            // Client (recipient) information
            ["ClientName"] = invoice.Client?.CompanyName ?? "",
            ["ClientRegistrationNumber"] = invoice.Client?.RegistrationNumber ?? "",
            ["ClientTaxNumber"] = invoice.Client?.TaxNumber ?? "",
            ["ClientStreet"] = clientAddress?.Street ?? "",
            ["ClientCity"] = clientAddress?.City ?? "",
            ["ClientPostalCode"] = clientAddress?.PostalCode ?? "",
            ["ClientCountry"] = clientAddress?.Country ?? "",
            ["ClientEmail"] = clientContact?.ContactValue ?? "",

            // Invoice metadata
            ["DocumentNumber"] = invoice.DocumentNumber ?? "",
            // Use DateOnly-style formatting to prevent any timezone shift.
            // Dates are stored as UTC midnight (e.g., 2026-03-31T00:00:00Z).
            // .Date strips the time component — ensures the calendar date is always
            // identical to what the user sees in the UI, regardless of server timezone.
            ["IssueDate"] = FormatDateForPdf(invoice.IssueDate),
            ["DueDate"] = FormatDateForPdf(invoice.DueDate),
            ["TaxableSupplyDate"] = FormatDateForPdf(invoice.TaxableSupplyDate),
            ["DocumentType"] = invoice.DocumentType.ToString(),
            ["DocumentTypeLabel"] = documentTypeLabel,
            ["Status"] = invoice.Status.ToString(),

            // Payment information
            ["VariableSymbol"] = invoice.VariableSymbol ?? "",
            ["ConstantSymbol"] = invoice.ConstantSymbol ?? "",
            ["SpecificSymbol"] = invoice.SpecificSymbol ?? "",
            ["BankAccountNumber"] = invoice.BankAccountNumber ?? "",
            ["IBAN"] = invoice.IBAN ?? "",
            ["SWIFT"] = invoice.SWIFT ?? "",
            // PaymentMethod is now an enum — convert to localized string for PDF based on client language
            ["PaymentMethod"] = GetPaymentMethodLabel(invoice.PaymentMethod, docLang),

            // Financial totals — use space as thousands separator (Czech format)
            ["TotalBeforeVat"] = invoice.TotalBeforeVat.ToString("N2"),
            ["TotalVat"] = invoice.TotalVat.ToString("N2"),
            ["TotalWithVat"] = invoice.TotalWithVat.ToString("N2"),
            ["CurrencyCode"] = invoice.Currency?.Code ?? "",
            ["CurrencySymbol"] = invoice.Currency?.Symbol ?? "",

            // Notes
            ["Notes"] = invoice.Notes ?? ""
        };

        // Replace each {{Placeholder}} in the HTML with the actual value
        foreach (var kvp in replacements)
        {
            html = html.Replace($"{{{{{kvp.Key}}}}}", kvp.Value);
        }

        // EU OSS invoice (DEVGUIDE §4.16): VAT is the destination country's, so the rates are labelled
        // "DPH <country> x %" and a "Režim OSS" note is added above the totals.
        var ossCountry = invoice.OssCountryCode;
        if (ossCountry != null)
            html = html.Replace(GrandTotalTable,
                $@"<p style=""margin:8px 0 4px 0; font-style:italic"">{(docLang == "cs" ? "Režim OSS (zvláštní režim jednoho správního místa)." : "OSS scheme (EU One-Stop-Shop special scheme).")}</p>" + GrandTotalTable);

        // Build the invoice items table rows dynamically.
        // Columns match the reference design: Description | Unit | VAT% | Quantity | UnitPrice | TotalBeforeVat
        var itemsHtml = "";
        if (invoice.InvoiceItem != null)
        {
            foreach (var item in invoice.InvoiceItem.OrderBy(i => i.OrderIndex))
            {
                if (item.IsTextRow)
                {
                    // Text row spans all columns — display-only note, no financial data
                    itemsHtml += $@"<tr>
                        <td colspan=""{(showVatColumn ? 6 : 5)}"" style=""font-style:italic;color:#555"">{item.Description}</td>
                    </tr>";
                }
                else
                {
                    // Reverse charge items bill 0 VAT — mark the rate cell with an asterisk so the
                    // reader is pointed at the mandatory note below (§92a ZDPH, "daň odvede zákazník").
                    var isReverseCharge = item.VatRegime == EVatRegime.ReverseCharge;
                    var vatCell = showVatColumn
                        ? isReverseCharge
                            ? $@"<td style=""text-align:center"">{item.VatRatePercentage:N0} % *</td>"
                            : $@"<td style=""text-align:center"">{(ossCountry != null ? $"{ossCountry} {item.VatRatePercentage:0.##}" : $"{item.VatRatePercentage:N0}")} %</td>"
                        : "";
                    itemsHtml += $@"<tr>
                        <td>{item.Description}</td>
                        <td style=""text-align:center"">{item.Unit}</td>
                        {vatCell}
                        <td style=""text-align:center"">{item.Quantity:N0}</td>
                        <td style=""text-align:right"">{item.UnitPrice:N2} {invoice.Currency?.Symbol ?? ""}</td>
                        <td style=""text-align:right"">{item.TotalBeforeVat:N2} {invoice.Currency?.Symbol ?? ""}</td>
                    </tr>";
                }
            }
        }

        // Replace the items placeholder with the generated rows
        html = html.Replace("{{InvoiceItems}}", itemsHtml);

        // Build the VAT breakdown (recapitulation) table rows.
        // Groups invoice items by VAT rate and shows the base + VAT amount for each rate.
        var vatBreakdownHtml = "";
        // Items under reverse charge bill 0 VAT and must NOT be lumped into a regular-rate group
        // (that would misleadingly read as a genuine 0% rate). They get their own recap line(s)
        // further down, grouped by nominal rate, labelled "daň odvede zákazník" instead of an amount.
        var hasReverseCharge = invoice.InvoiceItem?.Any(i => !i.IsTextRow && i.VatRegime == EVatRegime.ReverseCharge) ?? false;
        if (invoice.InvoiceItem != null)
        {
            // Exclude text rows — they have no financial data (VatRatePercentage = 0)
            // and would create a spurious "0%" line in the VAT recapitulation.
            var vatGroups = invoice.InvoiceItem
                .Where(i => !i.IsTextRow && i.VatRegime != EVatRegime.ReverseCharge)
                .GroupBy(i => i.VatRatePercentage)
                .OrderByDescending(g => g.Key);

            foreach (var group in vatGroups)
            {
                var baseAmount = group.Sum(i => i.TotalBeforeVat);
                var vatAmount = group.Sum(i => i.VatAmount);
                vatBreakdownHtml += $@"<tr>
                    <td style=""text-align:center"">{(ossCountry != null ? $"DPH {ossCountry} {group.Key:0.##}" : $"{group.Key:N0}")} %</td>
                    <td style=""text-align:right"">{baseAmount:N0}</td>
                    <td style=""text-align:right"">{vatAmount:N2} {invoice.Currency?.Symbol ?? ""}</td>
                </tr>";
            }

            if (hasReverseCharge)
            {
                var rcGroups = invoice.InvoiceItem
                    .Where(i => !i.IsTextRow && i.VatRegime == EVatRegime.ReverseCharge)
                    .GroupBy(i => i.VatRatePercentage)
                    .OrderByDescending(g => g.Key);

                foreach (var group in rcGroups)
                {
                    var baseAmount = group.Sum(i => i.TotalBeforeVat);
                    var rcLabel = docLang == "cs" ? "daň odvede zákazník *" : "customer self-assesses VAT *";
                    vatBreakdownHtml += $@"<tr>
                        <td style=""text-align:center"">{group.Key:N0} % (PDP)</td>
                        <td style=""text-align:right"">{baseAmount:N0}</td>
                        <td style=""text-align:right; font-style:italic"">{rcLabel}</td>
                    </tr>";
                }
            }
        }
        html = html.Replace("{{VatBreakdown}}", vatBreakdownHtml);

        // Mandatory reverse-charge note (§92a ZDPH): the document must state that the customer
        // self-assesses VAT, and list the reverse charge code(s) used so the buyer can match them
        // to the VAT control statement (kontrolní hlášení B.1). Rendered directly in code — same
        // "insert before the grand-total table" approach as StripVatFromTemplate's non-payer note —
        // so existing/custom ContentTemplate rows never need a {{Placeholder}} edit to pick it up.
        if (hasReverseCharge)
        {
            html = html.Replace(GrandTotalTable, BuildReverseChargeNote(invoice, docLang) + GrandTotalTable);
        }

        // Replace the QR code placeholder with an inline base64 image.
        // If QR code generation failed or no base64 data, remove the entire QR section.
        if (!string.IsNullOrEmpty(qrCodeBase64))
        {
            // EUR invoices with an IBAN get the SEPA EPC QR code (see QrPaymentService) —
            // label it accordingly instead of the generic Czech "QR Platba".
            var isSepa = QrPaymentService.UsesEpc(invoice);
            var altText = isSepa ? "QR platba SEPA" : "QR Platba";
            html = html.Replace("{{QrCodeImage}}", $@"<img src=""data:image/png;base64,{qrCodeBase64}"" alt=""{altText}"" style=""width:120px; height:120px;"" />");
        }
        else
        {
            // Remove the QR placeholder and hide the QR section entirely if present
            html = html.Replace("{{QrCodeImage}}", "");
        }

        return html;
    }

    // Markup of the built-in PDF templates (Fakvio.Infrastructure/Templates/*.html). Every tenant's
    // seeded ContentTemplate rows are copies of those files, so matching the exact snippets covers
    // them all without a data migration.
    private const string VatColumnHeader = @"<th style=""text-align:center; width:8%"">DPH</th>";
    private static readonly System.Text.RegularExpressions.Regex VatRecapTable = new(
        @"<table class=""vat-summary"">\s*<thead>.*?\{\{VatBreakdown\}\}.*?</table>",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);
    private const string GrandTotalTable = @"<table class=""vat-summary"" style=""margin-top:0"">";
    // Only a VAT payer issues a tax document with a taxable supply date (DUZP).
    private const string TaxDocumentSubtitle = @"<span class=""doc-subtitle"">DAŇOVÝ DOKLAD</span>";
    private const string TaxableSupplyDateRow =
        @"<tr><td class=""date-label"">DATUM ZDAN. PLNĚNÍ</td><td class=""date-value"">{{TaxableSupplyDate}}</td></tr>";

    /// <summary>
    /// Turns a PDF template into its non-VAT-payer form: removes the VAT rate column and the
    /// VAT recapitulation, the "DAŇOVÝ DOKLAD" subtitle and the taxable supply date row,
    /// renames "CELKEM BEZ DPH" to "CELKEM" and adds a line that the supplier is not a VAT
    /// payer (in the client's language).
    /// ponytail: exact-snippet matching — a template the user rewrote by hand keeps its VAT
    /// column (showing 0 %). If custom templates become common, add a {{#if VatPayer}} section
    /// syntax to the template engine instead.
    /// </summary>
    /// <param name="vatColumnRemoved">True when the VAT header cell was found and removed —
    /// item rows must then drop their VAT cell too, or the columns shift.</param>
    internal static string StripVatFromTemplate(string html, string language, out bool vatColumnRemoved)
    {
        vatColumnRemoved = html.Contains(VatColumnHeader);
        html = html.Replace(VatColumnHeader, "");
        html = html.Replace(">CELKEM BEZ DPH<", ">CELKEM<");
        html = VatRecapTable.Replace(html, "", 1);
        html = html.Replace(TaxDocumentSubtitle, "");
        html = html.Replace(TaxableSupplyDateRow, "");

        var note = language == "cs" ? "Dodavatel není plátcem DPH." : "The supplier is not a VAT payer.";
        html = html.Replace(GrandTotalTable,
            $@"<p style=""margin:8px 0 4px 0; font-style:italic"">{note}</p>" + GrandTotalTable);
        return html;
    }

    /// <summary>
    /// Builds the mandatory reverse-charge note (§92a zákona č. 235/2004 Sb.) shown on documents
    /// that contain at least one PDP item: states the customer self-assesses VAT, and lists every
    /// distinct reverse-charge code used (kód předmětu plnění) so the buyer can match it against
    /// the control statement (kontrolní hlášení B.1). Printing the code is good practice, not a
    /// strict legal requirement, but it is already selected on the item so there is no reason to omit it.
    /// </summary>
    private static string BuildReverseChargeNote(Domain.Entities.Invoice invoice, string language)
    {
        var codes = invoice.InvoiceItem!
            .Where(i => !i.IsTextRow && i.VatRegime == EVatRegime.ReverseCharge && i.ReverseChargeCode != null)
            .Select(i => i.ReverseChargeCode!)
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.Code)
            .ToList();

        if (language == "cs")
        {
            var codeLines = string.Join("", codes.Select(c =>
                $@"<li>kód předmětu plnění {c.Code} – {c.NameCs} ({c.ParagraphRef})</li>"));
            return $@"<p style=""margin:8px 0 4px 0; font-style:italic"">
                Přenesená daňová povinnost dle §92a zákona č. 235/2004 Sb., o dani z přidané hodnoty —
                daň odvede zákazník.
                {(codeLines.Length > 0 ? $"<ul style=\"margin:4px 0 0 18px; padding:0\">{codeLines}</ul>" : "")}
            </p>";
        }

        var codeLinesEn = string.Join("", codes.Select(c =>
            $@"<li>supply code {c.Code} – {c.NameEn ?? c.NameCs} ({c.ParagraphRef})</li>"));
        return $@"<p style=""margin:8px 0 4px 0; font-style:italic"">
            Reverse charge under §92a of Act No. 235/2004 Coll., on value added tax —
            VAT to be self-assessed by the customer.
            {(codeLinesEn.Length > 0 ? $"<ul style=\"margin:4px 0 0 18px; padding:0\">{codeLinesEn}</ul>" : "")}
        </p>";
    }

    /// <summary>
    /// Maps an invoice's EDocumentType to the corresponding EContentTemplateType for PDF rendering.
    /// All four document types have their own PDF template type — this is the single authoritative
    /// mapping; every caller uses this method instead of duplicating the switch.
    /// </summary>
    internal static EContentTemplateType ResolveTemplateType(EDocumentType documentType)
        => documentType switch
        {
            EDocumentType.Invoice => EContentTemplateType.InvoicePdf,
            EDocumentType.CreditNote => EContentTemplateType.CreditNotePdf,
            EDocumentType.Proforma => EContentTemplateType.AdvanceInvoicePdf,
            EDocumentType.TaxReceiptForAdvance => EContentTemplateType.TaxReceiptForAdvancePdf,
            _ => EContentTemplateType.InvoicePdf   // safe fallback for any future types
        };

    /// <summary>
    /// Maps an invoice's EDocumentType to the corresponding email EContentTemplateType.
    /// Used by EmailService to pick the right email template for each document type.
    /// </summary>
    internal static EContentTemplateType ResolveEmailTemplateType(EDocumentType documentType)
        => documentType switch
        {
            EDocumentType.Invoice => EContentTemplateType.InvoiceEmail,
            EDocumentType.CreditNote => EContentTemplateType.CreditNoteEmail,
            EDocumentType.Proforma => EContentTemplateType.AdvanceInvoiceEmail,
            EDocumentType.TaxReceiptForAdvance => EContentTemplateType.TaxReceiptForAdvanceEmail,
            _ => EContentTemplateType.InvoiceEmail  // safe fallback
        };

    /// <summary>
    /// Converts the EPaymentMethod enum to a localized label for the PDF template.
    /// Returns Czech labels for "cs", English labels for other languages.
    /// </summary>
    private static string GetPaymentMethodLabel(Domain.Enums.EPaymentMethod? method, string language)
    {
        if (language == "cs")
        {
            return method switch
            {
                Domain.Enums.EPaymentMethod.BankTransfer => "PŘEVODEM",
                Domain.Enums.EPaymentMethod.Cash => "HOTOVĚ",
                Domain.Enums.EPaymentMethod.CreditCard => "KARTOU",
                Domain.Enums.EPaymentMethod.PayPal => "PAYPAL",
                Domain.Enums.EPaymentMethod.Other => "JINÝ",
                _ => ""
            };
        }

        // English (and any other language) fallback
        return method switch
        {
            Domain.Enums.EPaymentMethod.BankTransfer => "BANK TRANSFER",
            Domain.Enums.EPaymentMethod.Cash => "CASH",
            Domain.Enums.EPaymentMethod.CreditCard => "CREDIT CARD",
            Domain.Enums.EPaymentMethod.PayPal => "PAYPAL",
            Domain.Enums.EPaymentMethod.Other => "OTHER",
            _ => ""
        };
    }

    /// <summary>
    /// Returns a localized document type label for the PDF header.
    /// Czech / English labels for all four document types.
    /// </summary>
    private static string GetDocumentTypeLabel(EDocumentType documentType, string language)
    {
        if (language == "cs")
        {
            return documentType switch
            {
                EDocumentType.CreditNote => "DOBROPIS",
                EDocumentType.Proforma => "ZÁLOHOVÁ FAKTURA",
                EDocumentType.TaxReceiptForAdvance => "DAŇOVÝ DOKLAD O PŘIJATÉ PLATBĚ",
                _ => "FAKTURA"
            };
        }

        return documentType switch
        {
            EDocumentType.CreditNote => "CREDIT NOTE",
            EDocumentType.Proforma => "ADVANCE INVOICE",
            EDocumentType.TaxReceiptForAdvance => "TAX RECEIPT FOR ADVANCE PAYMENT",
            _ => "INVOICE"
        };
    }

    /// <summary>
    /// Formats a nullable DateTime for PDF output using dd.MM.yyyy (Czech standard format).
    /// Handles UTC dates that were shifted by timezone conversion: if time is >= 22:00 UTC,
    /// the date was likely midnight in CET/CEST (UTC+1/+2) — rounds up to the next day.
    /// This ensures the PDF date matches exactly what the user entered in the UI.
    /// </summary>
    private static string FormatDateForPdf(DateTime? date)
    {
        if (!date.HasValue) return "";
        var dt = date.Value;
        // If time >= 22:00 UTC, this was midnight in UTC+1/+2 timezone — show the next day
        var calendarDate = dt.Hour >= 22 ? dt.Date.AddDays(1) : dt.Date;
        return calendarDate.ToString("dd.MM.yyyy");
    }

    /// <summary>
    /// Returns the built-in fallback HTML template that matches the given document type.
    /// Used when no ContentTemplate record is found in the database for the document type.
    /// Each document type has its own distinct default HTML (different colour accent, labels, etc.).
    /// </summary>
    private static string GetDefaultHtmlTemplate(EDocumentType documentType)
        => documentType switch
        {
            EDocumentType.CreditNote => DefaultSeedData.GetDefaultCreditNotePdfTemplate(),
            EDocumentType.Proforma => DefaultSeedData.GetDefaultAdvanceInvoicePdfTemplate(),
            EDocumentType.TaxReceiptForAdvance => DefaultSeedData.GetDefaultTaxReceiptForAdvancePdfTemplate(),
            _ => DefaultSeedData.GetDefaultInvoicePdfTemplate()
        };
}
