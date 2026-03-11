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
    private readonly IAiProviderFactory _providerFactory;
    private readonly ILogger<InvoiceAiExtractorService> _logger;

    /// <summary>
    /// Maximum time to wait for AI response before giving up.
    /// Invoice text extraction is a one-shot request, so 30 seconds is generous.
    /// If the AI takes longer, we fall back to regex (near-instant).
    /// </summary>
    private static readonly TimeSpan AiTimeout = TimeSpan.FromSeconds(30);

    public InvoiceAiExtractorService(
        IAiProviderFactory providerFactory,
        ILogger<InvoiceAiExtractorService> logger)
    {
        _providerFactory = providerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if at least one AI provider is configured.
    /// Checks IAiProviderFactory.AvailableProviders — only providers
    /// with valid API keys (or reachable Ollama URLs) are listed.
    /// </summary>
    public bool IsAvailable => _providerFactory.AvailableProviders.Count > 0;

    /// <summary>
    /// Sends PDF text to the default AI provider and parses the structured JSON response.
    /// Returns null on any failure — the caller should fall back to regex extraction.
    /// </summary>
    public async Task<InvoiceExtractedData?> ExtractAsync(string pdfText, CancellationToken ct = default)
    {
        // Guard: no AI provider configured
        if (!IsAvailable)
        {
            _logger.LogDebug("No AI provider available, skipping AI extraction");
            return null;
        }

        // Guard: empty text — nothing to extract from
        if (string.IsNullOrWhiteSpace(pdfText))
        {
            _logger.LogDebug("Empty PDF text, skipping AI extraction");
            return null;
        }

        try
        {
            var provider = _providerFactory.GetDefaultProvider();

            _logger.LogInformation(
                "Starting AI invoice extraction using provider {Provider}, text length: {Length} chars",
                provider.ProviderName, pdfText.Length);

            // Create a linked cancellation token that also enforces our timeout.
            // This way, both user cancellation and timeout will stop the AI request.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(AiTimeout);

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
                systemPrompt: SystemPrompt,
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
    /// The system prompt sent to the AI provider for invoice data extraction.
    /// Carefully crafted to produce consistent, parseable JSON output.
    ///
    /// Key design decisions:
    /// - Explicit "return ONLY JSON" instruction (prevents chatty responses)
    /// - Full schema with types and examples (reduces ambiguity)
    /// - Czech-specific field descriptions (IČO, DIČ, VS, DUZP)
    /// - Instruction to use null for missing fields (not empty strings)
    /// - Date format: YYYY-MM-DD (ISO 8601 — unambiguous parsing)
    /// </summary>
    internal const string SystemPrompt = """
        You are an invoice data extraction assistant. Your task is to extract structured data from the provided invoice text.

        IMPORTANT: Return ONLY a valid JSON object. No markdown, no explanation, no code blocks. Just pure JSON.

        Extract the following fields. Use null for any field you cannot find in the text.

        {
          "documentNumber": "string — the invoice/document number (e.g., FV2026001, 20260042)",
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
        - Dates MUST be in YYYY-MM-DD format
        - Amounts are decimal numbers (not strings)
        - Currency should be the 3-letter ISO 4217 code
        - If you find "Kč" in the text, the currency is "CZK"
        - IČO is always 8 digits (Czech registration number)
        - DIČ starts with country code (usually "CZ" for Czech) followed by digits
        - If items table is not found, set items to null (not empty array)
        - Distinguish between issuer (who created the invoice) and recipient (who receives it)
        """;

    // ─── AI response model ───────────────────────────────────────────────

    /// <summary>
    /// JSON deserialization target matching the schema in the system prompt.
    /// All fields are nullable strings/decimals because the AI may not find every field.
    /// Dates are strings here (parsed to DateTime in MapToExtractedData).
    /// </summary>
    internal class AiExtractionResponse
    {
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
