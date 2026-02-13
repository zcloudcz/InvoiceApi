using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Enums;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using iText.Html2pdf;
using iText.Kernel.Pdf;

namespace InvoiceApi.Infrastructure.Service;

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
            .Include(i => i.InvoiceItem)
            .Include(i => i.Currency)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException($"Invoice with ID {invoiceId} not found.");

        // Determine the template type based on the invoice's document type
        var templateType = invoice.DocumentType == EDocumentType.CreditNote
            ? EContentTemplateType.CreditNotePdf
            : EContentTemplateType.InvoicePdf;

        // Resolve the PDF template from ContentTemplate system.
        // Priority: 1) explicit contentTemplateId (user selected), 2) default for document type, 3) built-in fallback
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
            // No explicit template — use the default for the document type
            var contentTemplate = await _contentTemplateService.GetDefaultByTypeAsync(templateType, ct);
            if (contentTemplate != null)
            {
                htmlTemplate = contentTemplate.HtmlBody;
                _logger.LogInformation("Using default template '{TemplateName}' (type {Type}) for invoice {InvoiceId}",
                    contentTemplate.Name, templateType, invoiceId);
            }
            else
            {
                // No template configured at all — use the built-in fallback HTML
                htmlTemplate = GetDefaultHtmlTemplate();
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
    private static string ReplacePlaceholders(string html, Domain.Entities.Invoice invoice, string? qrCodeBase64 = null)
    {
        // Get issuer (company that sends the invoice) and client (recipient) addresses
        var issuerAddress = invoice.Issuer?.Address?.FirstOrDefault();
        var clientAddress = invoice.Client?.Address?.FirstOrDefault();

        // Resolve issuer contacts by type — email and phone are shown separately in the template
        var issuerEmail = invoice.Issuer?.Contact?
            .FirstOrDefault(c => c.ContactType == Domain.Enums.EContactType.Email)?.ContactValue ?? "";
        var issuerPhone = invoice.Issuer?.Contact?
            .FirstOrDefault(c => c.ContactType == Domain.Enums.EContactType.Phone)?.ContactValue ?? "";

        var clientContact = invoice.Client?.Contact?.FirstOrDefault();

        // Localized document type label: "FAKTURA" or "DOBROPIS"
        var documentTypeLabel = invoice.DocumentType == EDocumentType.CreditNote
            ? "DOBROPIS"
            : "FAKTURA";

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
            ["IssueDate"] = invoice.IssueDate?.ToString("dd. M. yyyy") ?? "",
            ["DueDate"] = invoice.DueDate?.ToString("dd. M. yyyy") ?? "",
            ["TaxableSupplyDate"] = invoice.TaxableSupplyDate?.ToString("dd. M. yyyy") ?? "",
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
            // PaymentMethod is now an enum — convert to localized Czech string for PDF
            ["PaymentMethod"] = GetPaymentMethodLabel(invoice.PaymentMethod),

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

        // Build the invoice items table rows dynamically.
        // Columns match the reference design: Description | Unit | VAT% | Quantity | UnitPrice | TotalBeforeVat
        var itemsHtml = "";
        if (invoice.InvoiceItem != null)
        {
            foreach (var item in invoice.InvoiceItem.OrderBy(i => i.OrderIndex))
            {
                itemsHtml += $@"<tr>
                    <td>{item.Description}</td>
                    <td style=""text-align:center"">{item.Unit}</td>
                    <td style=""text-align:center"">{item.VatRatePercentage:N0} %</td>
                    <td style=""text-align:center"">{item.Quantity:N0}</td>
                    <td style=""text-align:right"">{item.UnitPrice:N2} {invoice.Currency?.Symbol ?? ""}</td>
                    <td style=""text-align:right"">{item.TotalBeforeVat:N2} {invoice.Currency?.Symbol ?? ""}</td>
                </tr>";
            }
        }

        // Replace the items placeholder with the generated rows
        html = html.Replace("{{InvoiceItems}}", itemsHtml);

        // Build the VAT breakdown (recapitulation) table rows.
        // Groups invoice items by VAT rate and shows the base + VAT amount for each rate.
        var vatBreakdownHtml = "";
        if (invoice.InvoiceItem != null)
        {
            var vatGroups = invoice.InvoiceItem
                .GroupBy(i => i.VatRatePercentage)
                .OrderByDescending(g => g.Key);

            foreach (var group in vatGroups)
            {
                var baseAmount = group.Sum(i => i.TotalBeforeVat);
                var vatAmount = group.Sum(i => i.VatAmount);
                vatBreakdownHtml += $@"<tr>
                    <td style=""text-align:center"">{group.Key:N0} %</td>
                    <td style=""text-align:right"">{baseAmount:N0}</td>
                    <td style=""text-align:right"">{vatAmount:N2} {invoice.Currency?.Symbol ?? ""}</td>
                </tr>";
            }
        }
        html = html.Replace("{{VatBreakdown}}", vatBreakdownHtml);

        // Replace the QR code placeholder with an inline base64 image.
        // If QR code generation failed or no base64 data, remove the entire QR section.
        if (!string.IsNullOrEmpty(qrCodeBase64))
        {
            html = html.Replace("{{QrCodeImage}}", $@"<img src=""data:image/png;base64,{qrCodeBase64}"" alt=""QR Platba"" style=""width:120px; height:120px;"" />");
        }
        else
        {
            // Remove the QR placeholder and hide the QR section entirely if present
            html = html.Replace("{{QrCodeImage}}", "");
        }

        return html;
    }

    /// <summary>
    /// Converts the EPaymentMethod enum to a Czech label for the PDF template.
    /// </summary>
    private static string GetPaymentMethodLabel(Domain.Enums.EPaymentMethod? method)
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

    /// <summary>
    /// Returns a default HTML template for invoices/credit notes matching the reference design.
    /// Layout: Issuer at top → blue header bar with doc type + number → Client + dates → items table
    /// → VAT breakdown → grand total. Uses table-based layout for iText7 pdfhtml compatibility.
    /// </summary>
    private static string GetDefaultHtmlTemplate()
    {
        return DefaultSeedData.GetDefaultInvoicePdfTemplate();
    }
}
