using System.Text.Json;
using System.Text.RegularExpressions;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Orchestrates chat tool detection, AI interaction, and tool execution.
///
/// How the "two-pass" flow works (called by ChatService):
/// 1. DetectToolIntent() — fast regex: does the message contain an IČO + relevant keyword?
/// 2. If yes → ChatService makes a non-streaming AI call with BuildToolInstructions() appended
/// 3. ParseToolCall() — extracts JSON tool call from the AI response
/// 4. ExecuteToolAsync() — dispatches to the matching IChatTool
/// 5. ChatService makes a streaming AI call with the tool result injected
///
/// Why regex-based intent detection (not sending every message to the AI)?
/// Performance: regex is ~0ms, AI call is ~500-2000ms. For 95% of messages
/// that don't need tools, we avoid the overhead entirely.
///
/// All registered IChatTool implementations are injected via IEnumerable{IChatTool} from DI.
/// To add a new tool: implement IChatTool, register it in DI, and it's automatically available.
///
/// Junior note: This uses the "partial class" feature with [GeneratedRegex] attributes
/// to get compiled regex at build time (faster than runtime-compiled regex).
/// </summary>
public partial class ChatToolExecutor : IChatToolExecutor
{
    private readonly Dictionary<string, IChatTool> _tools;
    private readonly ILogger<ChatToolExecutor> _logger;

    public ChatToolExecutor(
        IEnumerable<IChatTool> tools,
        ILogger<ChatToolExecutor> logger)
    {
        _logger = logger;

        // Build a case-insensitive lookup dictionary from all registered tools.
        _tools = tools.ToDictionary(
            t => t.ToolName,
            t => t,
            StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("ChatToolExecutor initialized with {Count} tools: {Tools}",
            _tools.Count, string.Join(", ", _tools.Keys));
    }

    /// <summary>
    /// List of available tool names (for logging/debugging).
    /// </summary>
    public IReadOnlyList<string> AvailableTools => _tools.Keys.ToList().AsReadOnly();

    // ─── Compiled Regex Patterns ──────────────────────────────────────────

    /// <summary>
    /// Matches 8-digit sequences that look like Czech IČO numbers.
    /// Supports formats: "IČO 12345678", "ICO: 12345678", "ičo:12345678", standalone "12345678".
    /// Word boundaries (\b) prevent matching random 8-digit substrings of longer numbers.
    /// </summary>
    [GeneratedRegex(@"(?:IČO|ICO|ičo|ico)[\s:]*(\d{8})\b|\b(\d{8})\b", RegexOptions.Compiled)]
    private static partial Regex IcoPattern();

    /// <summary>
    /// Matches Czech and English keywords indicating the user wants to interact
    /// with company/client data. Case-insensitive.
    ///
    /// Czech: klient, firma, ARES, založ, najdi, vyhledej, společnost, přidej, etc.
    /// English: client, company, create, lookup, find, search, ARES, register, add, etc.
    /// </summary>
    [GeneratedRegex(
        @"\b(klient[aůuy]?|firm[auy]?|založ|zaloz|zaklad|založit|zalozit|najdi|najít|najit|vyhledej|hledej|" +
        @"ares|společnost|spolecnost|přidej|pridej|přidat|pridat|" +
        @"client|company|create|lookup|find|search|register|add)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ToolKeywordPattern();

    /// <summary>
    /// Matches Czech and English phrases that indicate the user is asking about
    /// received (incoming) invoices — přijaté faktury / expenses from suppliers.
    ///
    /// Czech: přijatá faktura, přijaté faktury, přijatou fakturu, přijatá, výdaj/ová faktura,
    ///        dodavatel, dodavatelská faktura, faktura od ...
    /// English: received invoice, incoming invoice, supplier invoice, expense invoice
    ///
    /// A standalone 8-to-15-digit number after "faktura" or "invoice" also triggers this path
    /// because the user likely means a document number (e.g. "faktura 267708922").
    /// </summary>
    [GeneratedRegex(
        @"\b(přijat[áaéeou]|prijat[áaéeou]|přijat[íi]|prijat[íi]|" +
        @"výdaj[oová]?|vydaj[oová]?|dodavatel[sš]k[áaé]?|dodavatel[eů]?|" +
        @"received invoice|incoming invoice|supplier invoice|expense invoice|" +
        @"přijatou fakturu|prijatou fakturu|přijaté faktury|prijaté faktury)\b|" +
        @"\b(faktura|fakturu|faktury|invoice)\b.{0,30}\b\d{5,15}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ReceivedInvoiceKeywordPattern();

    /// <summary>
    /// Matches Czech and English keywords indicating the user wants to navigate
    /// somewhere in the application or open a page/form.
    ///
    /// Czech: otevři, ukaž, přejdi, naviguj, zobraz, jdi na, nová faktura, nový klient, dobropis, etc.
    /// English: open, show, go to, navigate, display, new invoice, new client, new credit note, etc.
    ///
    /// This is a separate path from IČO + keyword detection — navigation doesn't require an IČO.
    /// </summary>
    [GeneratedRegex(
        @"\b(otevři|otevri|otevřít|otevrit|ukaž|ukaz|ukázat|ukazat|přejdi|prejdi|přejít|prejit|" +
        @"naviguj|navigovat|zobraz|zobrazit|" +
        @"jdi na|jdi do|chci|potřebuju|potrebuju|" +
        @"nová faktura|nova faktura|novou fakturu|nový klient|novy klient|nového klienta|noveho klienta|" +
        @"nový dobropis|novy dobropis|" +
        @"seznam faktur|seznam klientů|seznam klientu|přehled faktur|prehled faktur|" +
        @"open|show|go to|navigate|display|" +
        @"new invoice|new client|new credit note|" +
        @"invoice list|client list)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NavigationKeywordPattern();

    /// <summary>
    /// Matches Czech and English keywords indicating the user wants to CREATE an invoice
    /// (not just navigate to the create form). Must be combined with item/price context
    /// to distinguish "create invoice" (tool) from "open new invoice" (navigation).
    ///
    /// Czech: vytvoř/udělej/vystavit fakturu, faktura za/na (with amount context)
    /// English: create/make/generate invoice
    ///
    /// This pattern detects the intent to actually create a document, not just open a form.
    /// </summary>
    [GeneratedRegex(
        @"\b(vytvoř|vytvor|udělej|udelej|vystavit|vystav|vytvořit|vytvorit|" +
        @"create|make|generate|issue)\b.*\b(fakturu|faktura|faktur|invoice|dobropis|credit note)\b|" +
        @"\b(fakturu|faktura|invoice|dobropis|credit note)\b.*\b(za|na|for)\b.*\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InvoiceCreationPattern();

    /// <summary>
    /// Detects invoice import intent — user pastes invoice text or asks to import.
    /// Matches: "importuj fakturu", "import invoice", or pasted text containing
    /// both IČO (8 digits) and a monetary amount (common in pasted invoices).
    /// Also triggers on long messages (>300 chars) with IČO — likely pasted invoice content.
    /// </summary>
    [GeneratedRegex(
        @"\b(importuj|import|naimportuj|zaúčtuj|zauctuj|zaeviduj|přidej fakturu|pridej fakturu)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ImportKeywordPattern();

    /// <summary>
    /// Matches Czech and English keywords indicating the user wants to EXPORT/DOWNLOAD
    /// an invoice as PDF. Covers: stáhni, exportuj, download, export, pošli PDF, etc.
    /// </summary>
    [GeneratedRegex(
        @"\b(stáhni|stahni|stáhnout|stahnout|exportuj|exportovat|export|download|" +
        @"vygeneruj|generuj|generate|" +
        @"stáhnout pdf|stahnout pdf|pošli pdf|posli pdf|ukaž pdf|ukaz pdf)\b.*\b(fakturu?|faktur|invoice|dobropis|credit note|pdf)\b|" +
        @"\b(fakturu?|faktur|invoice|dobropis|credit note)\b.*\b(stáhni|stahni|exportuj|download|pdf|export)\b|" +
        @"\b(nejnovější|nejnovejsi|poslední|posledni|latest|last|recent)\b.*\b(fakturu?|faktur|invoice|dobropis)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ExportKeywordPattern();

    // ─── Intent Detection ─────────────────────────────────────────────────

    /// <summary>
    /// Fast check: does the user's message indicate a need for tool processing?
    ///
    /// Two paths can trigger tool intent:
    /// 1. IČO pattern + tool keyword (e.g., "Najdi firmu s IČO 12345678")
    /// 2. Navigation keyword (e.g., "Otevři novou fakturu", "Ukaž mi klienta ABC")
    ///
    /// Deliberately conservative — better to miss a tool call (the AI can still answer in text)
    /// than to add ~1-2s latency to every regular message with an unnecessary first-pass AI call.
    /// </summary>
    public bool DetectToolIntent(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        // Path 1: IČO pattern + tool keyword (for ARES lookup / client creation).
        if (IcoPattern().IsMatch(userMessage) && ToolKeywordPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: IČO pattern + keyword match");
            return true;
        }

        // Path 2: Navigation keyword (no IČO required).
        if (NavigationKeywordPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: navigation keyword match");
            return true;
        }

        // Path 3: Invoice creation keyword + item/price context.
        // Example: "Vytvoř fakturu pro Alza za mléko na 999,-"
        if (InvoiceCreationPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: invoice creation pattern match");
            return true;
        }

        // Path 4: Import keyword or pasted invoice content (long text with IČO).
        if (ImportKeywordPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: import keyword match");
            return true;
        }

        // Path 5: Long message with IČO — likely pasted invoice text for import.
        if (userMessage.Length > 300 && IcoPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: long text with IČO (likely pasted invoice)");
            return true;
        }

        // Path 6: Export/download keyword — user wants to download a PDF.
        if (ExportKeywordPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: export/download keyword match");
            return true;
        }

        // Path 7: Received invoice keyword — user asks about přijaté faktury / expenses.
        if (ReceivedInvoiceKeywordPattern().IsMatch(userMessage))
        {
            _logger.LogDebug("Tool intent detected in message: received invoice keyword match");
            return true;
        }

        return false;
    }

    // ─── Tool Instructions for System Prompt ──────────────────────────────

    /// <summary>
    /// Builds tool instructions to append to the system prompt.
    /// These tell the AI how to format tool calls.
    ///
    /// IMPORTANT: Instructions are kept very simple and explicit for Ollama llama3.1:8b.
    /// Small models need clear, unambiguous instructions with examples.
    /// </summary>
    public string BuildToolInstructions()
    {
        // Build the list of available tools with their descriptions.
        var toolDescriptions = string.Join("\n", _tools.Values.Select(t =>
            $"  - {t.ToolName}: {t.Description}\n    Parameters: {t.ParameterDescription}"));

        return
            "\n\nTOOLS:\n" +
            "You have access to these tools:\n" +
            $"{toolDescriptions}\n\n" +
            "WHEN TO USE TOOLS:\n" +
            "- If the user mentions an IČO and wants to look up a company, use \"ares_lookup\".\n" +
            "- If the user explicitly asks to create/add/register a client, use \"create_client\".\n" +
            "- If unsure whether to look up or create, use \"ares_lookup\" first (read-only, safer).\n" +
            "- If the user wants to open a page, navigate somewhere, or view something, use \"navigate\".\n" +
            "  Valid targets: new_invoice, new_credit_note, client_detail, client_list, invoice_list, new_client.\n" +
            "  If a client is mentioned, include \"client_name\" in parameters.\n" +
            "- If the user wants to CREATE an invoice (not just open the form), use \"create_invoice\".\n" +
            "  IMPORTANT: Use \"create_invoice\" when the user provides item details (description, price).\n" +
            "  Use \"navigate\" with target \"new_invoice\" when the user just wants to open the form.\n" +
            "  The items parameter must be a JSON array. Extract items from the user's message.\n" +
            "- If the user wants to EXPORT/DOWNLOAD/PRINT an invoice as PDF, use \"export_invoice\".\n" +
            "  Provide document_number or client_name. If neither is specified, exports the most recent invoice.\n" +
            "- If the user asks about a RECEIVED (incoming/expense) invoice by ID or document number, use \"get_received_invoice\".\n" +
            "  Provide id or document_number. Returns full detail including items and VAT breakdown.\n" +
            "- If the user wants to LIST or BROWSE received invoices (with filters), use \"list_received_invoices\".\n" +
            "  Supports status, supplier_name, date range, amount range, currency, and overdue filters.\n" +
            "- If the user SEARCHES for received invoices by text (number, supplier, amount), use \"search_received_invoices\".\n" +
            "  Provide a query string — matched against document number, supplier name, variable symbol, and amount.\n\n" +
            "HOW TO USE TOOLS:\n" +
            "Your ENTIRE response must be ONLY this JSON, nothing else:\n" +
            "{\"action\": \"tool_name\", \"parameters\": {\"key\": \"value\"}}\n\n" +
            "Examples:\n" +
            "- Look up IČO 12345678: {\"action\": \"ares_lookup\", \"parameters\": {\"registration_number\": \"12345678\"}}\n" +
            "- Open new invoice: {\"action\": \"navigate\", \"parameters\": {\"target\": \"new_invoice\"}}\n" +
            "- Open new invoice for client ABC: {\"action\": \"navigate\", \"parameters\": {\"target\": \"new_invoice\", \"client_name\": \"ABC\"}}\n" +
            "- Show client detail: {\"action\": \"navigate\", \"parameters\": {\"target\": \"client_detail\", \"client_name\": \"ABC\"}}\n" +
            "- Open client list: {\"action\": \"navigate\", \"parameters\": {\"target\": \"client_list\"}}\n" +
            "- Create invoice: {\"action\": \"create_invoice\", \"parameters\": {\"client_name\": \"Alza\", \"items\": \"[{\\\"description\\\": \\\"Mléko\\\", \\\"quantity\\\": 1, \\\"unit_price\\\": 999}]\"}}\n" +
            "- Create invoice with multiple items: {\"action\": \"create_invoice\", \"parameters\": {\"client_name\": \"ABC\", \"items\": \"[{\\\"description\\\": \\\"Item 1\\\", \\\"quantity\\\": 2, \\\"unit_price\\\": 500}, {\\\"description\\\": \\\"Item 2\\\", \\\"quantity\\\": 1, \\\"unit_price\\\": 300}]\"}}\n" +
            "- Export invoice by number: {\"action\": \"export_invoice\", \"parameters\": {\"document_number\": \"FV-2024-0001\"}}\n" +
            "- Export latest invoice for client: {\"action\": \"export_invoice\", \"parameters\": {\"client_name\": \"Alza\"}}\n" +
            "- Export the most recent invoice: {\"action\": \"export_invoice\", \"parameters\": {}}\n\n" +
            "If no tool is needed, respond normally with text.";
    }

    // ─── Parse Tool Call from AI Response ──────────────────────────────────

    /// <summary>
    /// Attempts to parse a tool call from the AI's response text.
    /// Expected format: {"action": "ares_lookup", "parameters": {"registration_number": "12345678"}}
    ///
    /// Returns null if the response is not a valid tool call.
    /// Handles edge cases:
    /// - AI wraps JSON in markdown code blocks (```json ... ```)
    /// - AI includes preamble text before the JSON
    /// - Malformed or missing "action" property
    /// </summary>
    public ParsedToolCall? ParseToolCall(string aiResponse)
    {
        if (string.IsNullOrWhiteSpace(aiResponse))
            return null;

        var cleanedResponse = aiResponse.Trim();

        // Strip markdown code block wrappers if present.
        // Ollama/LLMs sometimes wrap JSON in ```json ... ``` blocks.
        if (cleanedResponse.StartsWith("```"))
        {
            var lines = cleanedResponse.Split('\n');
            var jsonLines = lines
                .SkipWhile(l => l.TrimStart().StartsWith("```"))
                .TakeWhile(l => !l.TrimStart().StartsWith("```"))
                .ToArray();
            cleanedResponse = string.Join('\n', jsonLines).Trim();
        }

        // Find the first JSON object in the response.
        // The AI might include preamble text like "Sure, I'll look that up." before the JSON.
        var jsonStart = cleanedResponse.IndexOf('{');
        var jsonEnd = cleanedResponse.LastIndexOf('}');

        if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
            return null;

        var jsonString = cleanedResponse[jsonStart..(jsonEnd + 1)];

        try
        {
            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            // Must have an "action" property to be a valid tool call.
            if (!root.TryGetProperty("action", out var actionElement))
                return null;

            var action = actionElement.GetString();
            if (string.IsNullOrEmpty(action))
                return null;

            // Extract parameters (optional — some tools might not need params).
            var parameters = new Dictionary<string, string>();
            if (root.TryGetProperty("parameters", out var paramsElement) &&
                paramsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in paramsElement.EnumerateObject())
                {
                    parameters[prop.Name] = prop.Value.GetString() ?? prop.Value.ToString();
                }
            }

            _logger.LogInformation("Parsed tool call: action={Action}, parameters={Parameters}",
                action, string.Join(", ", parameters.Select(kv => $"{kv.Key}={kv.Value}")));

            return new ParsedToolCall
            {
                Action = action,
                Parameters = parameters
            };
        }
        catch (JsonException ex)
        {
            // Not valid JSON — this is a normal text response, not an error.
            _logger.LogDebug(ex, "AI response is not a JSON tool call (first 200 chars): {Response}",
                aiResponse[..Math.Min(200, aiResponse.Length)]);
            return null;
        }
    }

    // ─── Native Tool Definitions ─────────────────────────────────────────

    /// <summary>
    /// Builds NativeToolDefinition list from all registered IChatTool instances.
    /// Used by providers that support native tool calling (Ollama, etc.).
    ///
    /// Each tool's ParameterDescription is parsed into structured JSON Schema parameters.
    /// Since IChatTool doesn't define structured parameters, we use a hardcoded mapping
    /// for known tools. Unknown tools get a single "input" string parameter as fallback.
    ///
    /// Junior note: Native tool calling works much better than text-based instructions
    /// because models are fine-tuned to produce structured tool calls. The API enforces
    /// the parameter schema, so the model can't produce malformed output.
    /// </summary>
    public List<NativeToolDefinition> GetToolDefinitions()
    {
        var definitions = new List<NativeToolDefinition>();

        foreach (var tool in _tools.Values)
        {
            var def = new NativeToolDefinition
            {
                Name = tool.ToolName,
                Description = tool.Description
            };

            // Map known tools to structured parameters.
            // This is a hardcoded mapping because IChatTool uses free-text ParameterDescription.
            switch (tool.ToolName)
            {
                case "ares_lookup":
                    def.Parameters = new List<NativeToolParameter>
                    {
                        new() { Name = "registration_number", Type = "string",
                            Description = "Czech company registration number (IČO), exactly 8 digits" }
                    };
                    def.Required = new List<string> { "registration_number" };
                    break;

                case "create_client":
                    def.Parameters = new List<NativeToolParameter>
                    {
                        new() { Name = "registration_number", Type = "string",
                            Description = "Czech company registration number (IČO), exactly 8 digits. " +
                                          "Company data will be fetched from ARES automatically." }
                    };
                    def.Required = new List<string> { "registration_number" };
                    break;

                case "navigate":
                    def.Parameters = new List<NativeToolParameter>
                    {
                        new() { Name = "target", Type = "string",
                            Description = "Where to navigate in the application",
                            EnumValues = new List<string>
                            {
                                "new_invoice", "new_credit_note", "client_detail",
                                "client_list", "invoice_list", "new_client"
                            }
                        },
                        new() { Name = "client_name", Type = "string",
                            Description = "Client/company name (required for client_detail, optional for new_invoice to pre-select client)" }
                    };
                    def.Required = new List<string> { "target" };
                    break;

                case "create_invoice":
                    def.Parameters = new List<NativeToolParameter>
                    {
                        new() { Name = "client_name", Type = "string",
                            Description = "Name of the client/company to invoice" },
                        new() { Name = "items", Type = "string",
                            Description = "JSON array of invoice line items. Each item has: " +
                                          "\"description\" (string, required), \"quantity\" (number, default 1), " +
                                          "\"unit_price\" (number, required). " +
                                          "Example: [{\"description\": \"Web development\", \"quantity\": 10, \"unit_price\": 1500}]" },
                        new() { Name = "currency", Type = "string",
                            Description = "Currency code (e.g., \"CZK\", \"EUR\"). Default: CZK" },
                        new() { Name = "notes", Type = "string",
                            Description = "Optional notes to include on the invoice" }
                    };
                    def.Required = new List<string> { "client_name", "items" };
                    break;

                case "get_received_invoice":
                    def.Parameters = new List<NativeToolParameter>
                    {
                        new() { Name = "id", Type = "string",
                            Description = "Internal database ID of the received invoice" },
                        new() { Name = "document_number", Type = "string",
                            Description = "Document number as printed on the invoice (e.g. '267708922')" }
                    };
                    def.Required = new List<string>();  // at least one is required, validated inside ExecuteAsync
                    break;

                case "list_received_invoices":
                    def.Parameters = new List<NativeToolParameter>
                    {
                        new() { Name = "page", Type = "string",
                            Description = "Page number (default 1)" },
                        new() { Name = "page_size", Type = "string",
                            Description = "Items per page (default 10, max 50)" },
                        new() { Name = "status", Type = "string",
                            Description = "Filter by status",
                            EnumValues = new List<string> { "Received", "Approved", "Paid", "Rejected" } },
                        new() { Name = "supplier_name", Type = "string",
                            Description = "Supplier company name (case-insensitive substring match)" },
                        new() { Name = "issue_date_from", Type = "string",
                            Description = "Issue date range start (YYYY-MM-DD)" },
                        new() { Name = "issue_date_to", Type = "string",
                            Description = "Issue date range end (YYYY-MM-DD)" },
                        new() { Name = "min_amount", Type = "string",
                            Description = "Minimum total amount (with VAT)" },
                        new() { Name = "max_amount", Type = "string",
                            Description = "Maximum total amount (with VAT)" },
                        new() { Name = "currency", Type = "string",
                            Description = "Currency code filter (e.g. CZK, EUR)" },
                        new() { Name = "overdue", Type = "string",
                            Description = "Pass 'true' to show only overdue invoices" }
                    };
                    def.Required = new List<string>();
                    break;

                case "search_received_invoices":
                    def.Parameters = new List<NativeToolParameter>
                    {
                        new() { Name = "query", Type = "string",
                            Description = "Free-text search — matched against document number, supplier name, variable symbol, and amount" },
                        new() { Name = "limit", Type = "string",
                            Description = "Max results to return (default 10, max 50)" }
                    };
                    def.Required = new List<string> { "query" };
                    break;

                case "import_invoice":
                    def.Parameters = new List<NativeToolParameter>
                    {
                        new() { Name = "issuer_ico", Type = "string",
                            Description = "IČO of the invoice issuer (dodavatel/vystavitel)" },
                        new() { Name = "issuer_name", Type = "string",
                            Description = "Company name of the invoice issuer" },
                        new() { Name = "recipient_ico", Type = "string",
                            Description = "IČO of the invoice recipient (odběratel/příjemce)" },
                        new() { Name = "recipient_name", Type = "string",
                            Description = "Company name of the invoice recipient" },
                        new() { Name = "document_number", Type = "string",
                            Description = "Invoice number EXACTLY as printed on the document" },
                        new() { Name = "issue_date", Type = "string",
                            Description = "Date of issue in YYYY-MM-DD format, EXACTLY from the invoice" },
                        new() { Name = "due_date", Type = "string",
                            Description = "Payment due date in YYYY-MM-DD format, EXACTLY from the invoice" },
                        new() { Name = "taxable_supply_date", Type = "string",
                            Description = "DUZP in YYYY-MM-DD format, EXACTLY from the invoice" },
                        new() { Name = "variable_symbol", Type = "string",
                            Description = "Variable symbol (variabilní symbol) for payment" },
                        new() { Name = "bank_account", Type = "string",
                            Description = "Bank account number" },
                        new() { Name = "iban", Type = "string", Description = "IBAN" },
                        new() { Name = "swift", Type = "string", Description = "SWIFT/BIC code" },
                        new() { Name = "currency", Type = "string",
                            Description = "ISO 4217 currency code (CZK, EUR, etc.)" },
                        new() { Name = "items", Type = "string",
                            Description = "JSON array of line items: [{\"description\":\"...\",\"quantity\":1,\"unit_price\":100,\"vat_rate\":21}]" },
                        new() { Name = "notes", Type = "string", Description = "Optional notes" }
                    };
                    def.Required = new List<string> { "document_number", "items" };
                    break;

                default:
                    // Fallback for unknown tools: single "input" parameter.
                    def.Parameters = new List<NativeToolParameter>
                    {
                        new() { Name = "input", Type = "string",
                            Description = tool.ParameterDescription }
                    };
                    def.Required = new List<string> { "input" };
                    break;
            }

            definitions.Add(def);
        }

        return definitions;
    }

    // ─── Tool Execution ───────────────────────────────────────────────────

    /// <summary>
    /// Finds the matching tool by action name and executes it.
    /// Returns a failure result if the requested tool is not registered.
    /// </summary>
    public async Task<ChatToolResult> ExecuteToolAsync(
        ParsedToolCall toolCall,
        CancellationToken ct = default)
    {
        // Look up the tool in the dictionary (case-insensitive).
        if (!_tools.TryGetValue(toolCall.Action, out var tool))
        {
            _logger.LogWarning("Unknown tool requested: {Action}. Available: {Available}",
                toolCall.Action, string.Join(", ", _tools.Keys));
            return ChatToolResult.Failure(
                $"Unknown tool: {toolCall.Action}. Available tools: {string.Join(", ", _tools.Keys)}");
        }

        _logger.LogInformation("Executing tool {ToolName} with parameters: {Parameters}",
            tool.ToolName,
            string.Join(", ", toolCall.Parameters.Select(kv => $"{kv.Key}={kv.Value}")));

        try
        {
            var result = await tool.ExecuteAsync(toolCall.Parameters, ct);

            _logger.LogInformation("Tool {ToolName} completed: IsSuccess={IsSuccess}",
                tool.ToolName, result.IsSuccess);

            return result;
        }
        catch (Exception ex)
        {
            // Catch any unhandled exception from the tool to prevent the chat from crashing.
            _logger.LogError(ex, "Tool {ToolName} threw an unhandled exception", tool.ToolName);
            return ChatToolResult.Failure($"Tool execution failed: {ex.Message}");
        }
    }
}
