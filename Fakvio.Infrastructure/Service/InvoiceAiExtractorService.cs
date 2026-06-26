using System.Text.Json;
using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Extracts structured invoice data from PDF text using a configured AI provider.
/// Second level in the 3-tier extraction pipeline: QR Code → AI → Regex.
///
/// How it works:
/// 1. Checks if any AI provider is available (via IAiProviderFactory)
/// 2. Sends the PDF text with a detailed system prompt to the default AI provider
/// 3. The AI returns a JSON object matching our expected schema
/// 4. We parse the JSON into InvoiceExtractedData
/// 5. On ANY failure, we return null (graceful fallback to regex)
///
/// The system prompt is carefully crafted to:
/// - Request ONLY a JSON response (no markdown, no explanation)
/// - Define the exact schema with all field types and formats
/// - Include Czech-specific context (IČO, DIČ, DUZP, variable symbol)
/// - Handle both Czech and English invoices
/// </summary>
public class InvoiceAiExtractorService : IInvoiceAiExtractor
{
    private readonly ICompanyAiSettingsResolver _companyAiResolver;
    private readonly ILogger<InvoiceAiExtractorService> _logger;

    /// <summary>
    /// Maximum time to wait for AI response before giving up.
    /// Invoice text extraction is a one-shot request, so 30 seconds is generous.
    /// If the AI takes longer, we fall back to regex (near-instant).
    /// </summary>
    private static readonly TimeSpan AiTimeout = TimeSpan.FromSeconds(30);

    public InvoiceAiExtractorService(
        ICompanyAiSettingsResolver companyAiResolver,
        ILogger<InvoiceAiExtractorService> logger)
    {
        _companyAiResolver = companyAiResolver;
        _logger = logger;
    }

    /// <summary>
    /// Sends PDF text to the default AI provider and parses the structured JSON response.
    /// Returns null on any failure — the caller should fall back to regex extraction.
    /// </summary>
    public async Task<InvoiceExtractedData?> ExtractAsync(
        long? companyId, string pdfText,
        ImportCompanyContext? companyContext = null,
        CancellationToken ct = default)
    {
        // Guard: empty text — nothing to extract from
        if (string.IsNullOrWhiteSpace(pdfText))
        {
            _logger.LogDebug("Empty PDF text, skipping AI extraction");
            return null;
        }

        try
        {
            // Resolve AI provider using company-specific settings (Tier 1) or global fallback (Tier 2).
            // CompanyId is passed explicitly from the caller (InvoiceImportService) to avoid
            // IHttpContextAccessor issues in Azure Functions.
            var provider = await _companyAiResolver.ResolveProviderAsync(companyId, null, ct);

            _logger.LogInformation(
                "Starting AI invoice extraction using provider {Provider}, text length: {Length} chars",
                provider.ProviderName, pdfText.Length);

            // Create a linked cancellation token that also enforces our timeout.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(AiTimeout);

            // Build the system prompt — base prompt + company context if available.
            var effectivePrompt = BuildEffectivePrompt(companyContext);

            // Build the messages: system prompt + user message with the PDF text
            var messages = new List<ChatMessageDto>
            {
                new()
                {
                    Role = "User",
                    Content = pdfText
                }
            };

            // Get the AI response
            var jsonResponse = await provider.GetCompletionAsync(
                messages,
                systemPrompt: effectivePrompt,
                ct: timeoutCts.Token);

            if (string.IsNullOrWhiteSpace(jsonResponse))
            {
                _logger.LogWarning("AI returned empty response for invoice extraction");
                return null;
            }

            // Parse the JSON response into our data model
            var extracted = ParseAiResponse(jsonResponse);

            if (extracted != null)
            {
                extracted.Source = EExtractionSource.AiExtraction;
                _logger.LogInformation(
                    "AI extraction successful: DocNumber={DocNumber}, Amount={Amount}, Issuer={Issuer}",
                    extracted.DocumentNumber, extracted.TotalAmount, extracted.IssuerName);
            }

            return extracted;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AI extraction cancelled or timed out after {Timeout}s", AiTimeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            // Catch ALL exceptions — we never want AI extraction failure to break the import flow.
            // The regex fallback will handle it.
            _logger.LogWarning(ex, "AI extraction failed: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Parses the AI's JSON response into InvoiceExtractedData.
    /// The AI is instructed to return ONLY valid JSON (no markdown code blocks, no text).
    /// However, some models still wrap the response in ```json...``` — we handle that.
    /// </summary>
    internal static InvoiceExtractedData? ParseAiResponse(string jsonResponse)
    {
        // Strip markdown code block wrapper if present (some models add it despite instructions)
        var json = jsonResponse.Trim();
        if (json.StartsWith("```"))
        {
            // Remove first line (```json or ```) and last line (```)
            var firstNewline = json.IndexOf('\n');
            var lastBackticks = json.LastIndexOf("```");
            if (firstNewline > 0 && lastBackticks > firstNewline)
            {
                json = json[(firstNewline + 1)..lastBackticks].Trim();
            }
        }

        try
        {
            // Use case-insensitive property matching for robustness
            // (AI might use "documentNumber" or "DocumentNumber")
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                // Allow trailing commas and comments — AI output can be slightly imperfect
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            var aiResult = JsonSerializer.Deserialize<AiExtractionResponse>(json, options);

            if (aiResult == null)
                return null;

            return MapToExtractedData(aiResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps the AI's JSON response model to our unified InvoiceExtractedData.
    /// Handles type conversions (string dates → DateTime, string amounts → decimal).
    /// </summary>
    private static InvoiceExtractedData MapToExtractedData(AiExtractionResponse ai)
    {
        var data = new InvoiceExtractedData
        {
            DetectedDocumentType = NullIfEmpty(ai.DocumentType),
            DocumentNumber = NullIfEmpty(ai.DocumentNumber),
            IssueDate = ParseDate(ai.IssueDate),
            DueDate = ParseDate(ai.DueDate),
            TaxableSupplyDate = ParseDate(ai.TaxableSupplyDate),
            TotalAmount = ai.TotalAmount,
            TotalVat = ai.TotalVat,
            TotalBeforeVat = ai.TotalBeforeVat,
            Currency = NullIfEmpty(ai.Currency)?.ToUpperInvariant(),
            VariableSymbol = NullIfEmpty(ai.VariableSymbol),
            IBAN = NullIfEmpty(ai.IBAN),
            BankAccountNumber = NullIfEmpty(ai.BankAccountNumber),
            SWIFT = NullIfEmpty(ai.SWIFT),
            PaymentMethod = NullIfEmpty(ai.PaymentMethod),
            IssuerName = NullIfEmpty(ai.IssuerName),
            IssuerRegistrationNumber = NullIfEmpty(ai.IssuerRegistrationNumber),
            IssuerTaxNumber = NullIfEmpty(ai.IssuerTaxNumber),
            RecipientName = NullIfEmpty(ai.RecipientName),
            RecipientRegistrationNumber = NullIfEmpty(ai.RecipientRegistrationNumber),
            RecipientTaxNumber = NullIfEmpty(ai.RecipientTaxNumber),
            Source = EExtractionSource.AiExtraction
        };

        // Map line items if the AI extracted them
        if (ai.Items is { Count: > 0 })
        {
            data.Items = ai.Items.Select(item => new ExtractedInvoiceItem
            {
                Description = NullIfEmpty(item.Description),
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                VatRate = item.VatRate,
                Unit = NullIfEmpty(item.Unit),
                ProductCode = NullIfEmpty(item.ProductCode)
            }).ToList();
        }

        return data;
    }

    /// <summary>
    /// Parses a date string in multiple formats that AI models commonly produce.
    /// Supports: YYYY-MM-DD, DD.MM.YYYY, DD/MM/YYYY, YYYYMMDD.
    /// </summary>
    private static DateTime? ParseDate(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr))
            return null;

        // Try common formats — AI models are instructed to use ISO but may vary
        string[] formats = ["yyyy-MM-dd", "dd.MM.yyyy", "dd/MM/yyyy", "yyyyMMdd", "d.M.yyyy"];
        return DateTime.TryParseExact(dateStr.Trim(), formats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>Returns null for null, empty, or whitespace strings.</summary>
    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ─── System prompt ───────────────────────────────────────────────────

    /// <summary>
    /// Builds the effective system prompt by combining the base extraction prompt
    /// with company-specific context (if available).
    ///
    /// When company context is provided, the AI knows:
    /// - Which company is importing (name, IČO, DIČ)
    /// - Whether we're importing issued or received invoices
    /// - Special rules for issued imports (preserve document number exactly)
    /// </summary>
    private static string BuildEffectivePrompt(ImportCompanyContext? ctx)
    {
        if (ctx == null)
            return BaseSystemPrompt;

        var direction = ctx.IsIssuedImport
            ? $"""

              ISSUED INVOICE IMPORT — Our company is the ISSUER (dodavatel/vystavitel).
              - The other party on the invoice is the RECIPIENT (odběratel/příjemce) = our customer.
              - CRITICAL: The document number (číslo dokladu/faktury) must be extracted EXACTLY as printed
                on the invoice. Do NOT modify, reformat, or normalize it — it is part of our accounting
                sequence and must match our records precisely.
              - If the issuer IČO on the PDF does not match our IČO ({ctx.RegistrationNumber}),
                the document may not belong to this company.
              """
            : $"""

              RECEIVED INVOICE IMPORT — Our company is the RECIPIENT (odběratel/příjemce).
              - The other party on the invoice is the ISSUER (dodavatel/vystavitel) = our supplier.
              - The document number is the supplier's number — extract it as-is.
              - If the recipient IČO on the PDF does not match our IČO ({ctx.RegistrationNumber}),
                the document may not be addressed to this company.
              """;

        return BaseSystemPrompt + $"""

            IMPORTING COMPANY:
            - Company name: {ctx.CompanyName}
            - IČO: {ctx.RegistrationNumber}
            - DIČ: {ctx.TaxNumber ?? "N/A"}
            {direction}
            """;
    }

    /// <summary>
    /// Base system prompt for invoice data extraction.
    /// Extended at runtime by BuildEffectivePrompt with company-specific context.
    /// </summary>
    internal const string BaseSystemPrompt = """
        You are an invoice data extraction assistant. Your task is to extract structured data from the provided invoice text.

        IMPORTANT: Return ONLY a valid JSON object. No markdown, no explanation, no code blocks. Just pure JSON.

        Extract the following fields. Use null for any field you cannot find in the text.

        {
          "documentType": "Invoice|CreditNote|Proforma|TaxReceiptForAdvance — type of document. Invoice = standard tax invoice (faktura), CreditNote = dobropis, Proforma = zálohová faktura / proforma, TaxReceiptForAdvance = daňový doklad o přijaté platbě. Default to Invoice if unclear.",
          "documentNumber": "string — the invoice/document number EXACTLY as printed (e.g., FV2026001, 20260042). Do NOT modify or reformat.",
          "issueDate": "YYYY-MM-DD — date when the invoice was issued",
          "dueDate": "YYYY-MM-DD — payment due date (datum splatnosti)",
          "taxableSupplyDate": "YYYY-MM-DD — date of taxable supply (DUZP / datum uskutečnění zdanitelného plnění)",
          "totalAmount": 0.00,
          "totalVat": 0.00,
          "totalBeforeVat": 0.00,
          "currency": "ISO 4217 code (CZK, EUR, USD, etc.)",
          "variableSymbol": "string — Czech variable symbol (variabilní symbol), max 10 digits",
          "iban": "string — IBAN of the payee",
          "bankAccountNumber": "string — Czech bank account number format (e.g., 123456-1234567890/0100)",
          "swift": "string — SWIFT/BIC code",
          "paymentMethod": "BankTransfer|Cash|CreditCard|PayPal|Other — if detectable",
          "issuerName": "string — company name of the invoice issuer (dodavatel/vystavitel)",
          "issuerRegistrationNumber": "string — issuer's IČO (8 digits, Czech business ID)",
          "issuerTaxNumber": "string — issuer's DIČ (e.g., CZ12345678)",
          "recipientName": "string — company name of the invoice recipient (odběratel/příjemce)",
          "recipientRegistrationNumber": "string — recipient's IČO",
          "recipientTaxNumber": "string — recipient's DIČ",
          "items": [
            {
              "description": "string — line item description",
              "quantity": 0.00,
              "unitPrice": 0.00,
              "vatRate": 0.00,
              "unit": "string — unit of measure (ks, hod, m2, etc.)",
              "productCode": "string — product/service code if present"
            }
          ]
        }

        Rules:
        - ALL dates MUST be extracted EXACTLY as they appear on the invoice, converted to YYYY-MM-DD format.
          NEVER generate, guess, or substitute dates. If a date field is not found on the invoice, use null.
          Common Czech date labels: "Datum vystavení" = issueDate, "Datum splatnosti" = dueDate,
          "DUZP" or "Datum uskutečnění zdanitelného plnění" = taxableSupplyDate.
        - ALL amounts MUST be extracted EXACTLY as printed on the invoice. Do NOT recalculate or round.
          Amounts are decimal numbers (not strings).
        - Currency should be the 3-letter ISO 4217 code
        - If you find "Kč" in the text, the currency is "CZK"
        - IČO is always 8 digits (Czech registration number)
        - DIČ starts with country code (usually "CZ" for Czech) followed by digits
        - If items table is not found, set items to null (not empty array)
        - Distinguish between issuer (who created the invoice) and recipient (who receives it)
        - Document number must be extracted EXACTLY as it appears — no reformatting
        - documentType: look for keywords like "Dobropis" / "Credit Note" → CreditNote, "Zálohová faktura" / "Proforma" → Proforma,
          "Daňový doklad o přijaté platbě" → TaxReceiptForAdvance. If none found, default to "Invoice".
          Czech hints: prefix CN/CN- = CreditNote, PF/PF- = Proforma, DPP/DPP- = TaxReceiptForAdvance.
        - Extract ALL data faithfully from the document. Your job is OCR-like extraction, not generation.
        """;

    // ─── AI response model ───────────────────────────────────────────────

    /// <summary>
    /// JSON deserialization target matching the schema in the system prompt.
    /// All fields are nullable strings/decimals because the AI may not find every field.
    /// Dates are strings here (parsed to DateTime in MapToExtractedData).
    /// </summary>
    internal class AiExtractionResponse
    {
        public string? DocumentType { get; set; }
        public string? DocumentNumber { get; set; }
        public string? IssueDate { get; set; }
        public string? DueDate { get; set; }
        public string? TaxableSupplyDate { get; set; }
        public decimal? TotalAmount { get; set; }
        public decimal? TotalVat { get; set; }
        public decimal? TotalBeforeVat { get; set; }
        public string? Currency { get; set; }
        public string? VariableSymbol { get; set; }
        public string? IBAN { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? SWIFT { get; set; }
        public string? PaymentMethod { get; set; }
        public string? IssuerName { get; set; }
        public string? IssuerRegistrationNumber { get; set; }
        public string? IssuerTaxNumber { get; set; }
        public string? RecipientName { get; set; }
        public string? RecipientRegistrationNumber { get; set; }
        public string? RecipientTaxNumber { get; set; }
        public List<AiExtractionItemResponse>? Items { get; set; }
    }

    /// <summary>
    /// JSON deserialization target for a single invoice line item.
    /// </summary>
    internal class AiExtractionItemResponse
    {
        public string? Description { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? VatRate { get; set; }
        public string? Unit { get; set; }
        public string? ProductCode { get; set; }
    }
}
