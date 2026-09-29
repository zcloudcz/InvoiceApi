using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Parses UBL 2.1 / Peppol BIS Billing 3.0 invoices and credit notes into
/// <see cref="InvoiceExtractedData"/>. Same role as <see cref="IsdocImportParser"/>
/// (pure, synchronous, no AI) but for the UBL/Peppol format used by the Slovak
/// mandatory e-invoicing regime from 2027 — see
/// docs/adr/0002-sk-einvoicing-peppol.md, task F1.10.
///
/// UBL namespaces used: the root element lives in the Invoice-2 or CreditNote-2
/// namespace; all its children use the shared "cac" (aggregate) and "cbc" (basic)
/// component namespaces. Prefixes in the source document don't matter — we always
/// compare by namespace + local name.
///
/// Junior note on the security here: this parser sits at a trust boundary — the XML
/// comes from an inbound email attachment or a file a user uploads, i.e. from outside
/// Fakvio. Two things matter:
/// 1. DTDs are rejected (<see cref="DtdProcessing.Prohibit"/>). A DOCTYPE with an
///    ENTITY declaration is exactly how the classic "XXE" attack (read a local file
///    via an external entity) and the "billion laughs" attack (exponential entity
///    expansion to exhaust memory) work — banning DOCTYPE outright blocks both,
///    which is simpler and safer than trying to allow "safe" DTDs.
/// 2. <see cref="XmlResolver"/> is null, so even a non-DTD external reference is
///    never fetched over the network or filesystem.
/// On top of that, a byte-size cap is checked before parsing starts (so a huge
/// file can't be used to exhaust memory even without any entity tricks), and any
/// failure — malformed XML, a rejected DOCTYPE, an unrecognized root — is caught
/// and turned into a null return, never an exception. Callers (the email pipeline
/// and the manual-upload import) already know how to show a friendly "could not
/// read this file" message when extraction returns null.
/// </summary>
public class UblImportParser : IUblImportParser
{
    private static readonly XNamespace InvoiceNs = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace CreditNoteNs = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    /// <summary>
    /// Hard cap on the XML payload, checked before parsing. Real Peppol BIS invoices
    /// are a few KB to a few hundred KB; 10 MB matches the per-file limit already used
    /// for PDF uploads (see ImportController) and is generous headroom, not a realistic
    /// document size.
    /// </summary>
    internal const int MaxXmlSizeBytes = 10 * 1024 * 1024;

    private readonly ILogger<UblImportParser> _logger;

    public UblImportParser(ILogger<UblImportParser> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public InvoiceExtractedData? Parse(byte[] xmlBytes)
    {
        if (xmlBytes == null || xmlBytes.Length == 0)
            return null;

        if (xmlBytes.Length > MaxXmlSizeBytes)
        {
            _logger.LogWarning(
                "UBL document rejected: {Size} bytes exceeds the {Limit} byte limit",
                xmlBytes.Length, MaxXmlSizeBytes);
            return null;
        }

        XDocument doc;
        try
        {
            doc = LoadSecurely(xmlBytes);
        }
        catch (Exception ex)
        {
            // Covers malformed XML, a rejected DOCTYPE (DtdProcessing.Prohibit throws),
            // and any other XmlException — all treated the same: "not a valid UBL file".
            _logger.LogWarning(ex, "Failed to parse UBL XML — treating as not a valid UBL document");
            return null;
        }

        var root = doc.Root;
        if (root == null)
            return null;

        string documentTypeName;
        bool isCreditNote;
        if (root.Name == InvoiceNs + "Invoice")
        {
            // 380 = commercial invoice, 386 = prepayment/advance invoice (BT-3).
            documentTypeName = Str(root, Cbc, "InvoiceTypeCode") == "386" ? "TaxReceiptForAdvance" : "Invoice";
            isCreditNote = false;
        }
        else if (root.Name == CreditNoteNs + "CreditNote")
        {
            documentTypeName = "CreditNote";
            isCreditNote = true;
        }
        else
        {
            _logger.LogDebug("Unknown root element {Root} — not a UBL Invoice/CreditNote", root.Name);
            return null;
        }

        var result = new InvoiceExtractedData
        {
            Source = EExtractionSource.Merged,
            DocumentNumber = Str(root, Cbc, "ID"),
            IssueDate = Date(root, Cbc, "IssueDate"),
            DueDate = Date(root, Cbc, "DueDate"),
            // UBL/Peppol has no dedicated "date of taxable supply" field — the closest
            // equivalent is the actual delivery date (BT-72), when present.
            TaxableSupplyDate = Date(root.Element(Cac + "Delivery"), Cbc, "ActualDeliveryDate"),
            Currency = Str(root, Cbc, "DocumentCurrencyCode"),
            DetectedDocumentType = documentTypeName,
        };

        // ── Supplier (issuer) ───────────────────────────────────────────
        var supplier = root.Element(Cac + "AccountingSupplierParty")?.Element(Cac + "Party");
        if (supplier != null)
        {
            result.IssuerRegistrationNumber = Str(supplier, Cac, "PartyLegalEntity", Cbc, "CompanyID");
            result.IssuerName = Str(supplier, Cac, "PartyLegalEntity", Cbc, "RegistrationName")
                                 ?? Str(supplier, Cac, "PartyName", Cbc, "Name");
            result.IssuerTaxNumber = Str(supplier, Cac, "PartyTaxScheme", Cbc, "CompanyID");
        }

        // ── Buyer (recipient) ───────────────────────────────────────────
        var buyer = root.Element(Cac + "AccountingCustomerParty")?.Element(Cac + "Party");
        if (buyer != null)
        {
            result.RecipientRegistrationNumber = Str(buyer, Cac, "PartyLegalEntity", Cbc, "CompanyID");
            result.RecipientName = Str(buyer, Cac, "PartyLegalEntity", Cbc, "RegistrationName")
                                    ?? Str(buyer, Cac, "PartyName", Cbc, "Name");
            result.RecipientTaxNumber = Str(buyer, Cac, "PartyTaxScheme", Cbc, "CompanyID");
        }

        // ── Totals ───────────────────────────────────────────────────────
        var totals = root.Element(Cac + "LegalMonetaryTotal");
        if (totals != null)
        {
            result.TotalBeforeVat = Dec(totals, Cbc, "TaxExclusiveAmount");
            result.TotalAmount = Dec(totals, Cbc, "PayableAmount") ?? Dec(totals, Cbc, "TaxInclusiveAmount");
        }

        var taxTotal = root.Element(Cac + "TaxTotal");
        if (taxTotal != null)
            result.TotalVat = Dec(taxTotal, Cbc, "TaxAmount");

        // ── Payment means (first occurrence — Peppol BIS invoices carry at most one
        //    in the overwhelming majority of real documents) ───────────────
        var payment = root.Element(Cac + "PaymentMeans");
        if (payment != null)
        {
            result.VariableSymbol = Str(payment, Cbc, "PaymentID");
            result.PaymentMethod = MapPaymentMeansCode(Str(payment, Cbc, "PaymentMeansCode"));

            var account = payment.Element(Cac + "PayeeFinancialAccount");
            if (account != null)
            {
                result.IBAN = Str(account, Cbc, "ID");
                result.SWIFT = Str(account, Cac, "FinancialInstitutionBranch", Cbc, "ID");
            }
        }

        // ── Line items — <cac:InvoiceLine>/<cac:CreditNoteLine> are direct children
        //    of the root (unlike ISDOC, there's no wrapping "InvoiceLines" container).
        var lineElementName = isCreditNote ? "CreditNoteLine" : "InvoiceLine";
        var quantityElementName = isCreditNote ? "CreditedQuantity" : "InvoicedQuantity";
        var lines = root.Elements(Cac + lineElementName).ToList();
        if (lines.Count > 0)
        {
            result.Items = lines.Select(line =>
            {
                var item = line.Element(Cac + "Item");
                var quantityElement = line.Element(Cbc + quantityElementName);
                var quantity = ParseDecimal(quantityElement?.Value);

                // Fakvio's ReceivedInvoice has no separate "this is a credit note" flag
                // (see docs/adr/0002-sk-einvoicing-peppol.md F1.10) — a credit is instead
                // represented as a negative line, mirroring how the (separate) UBL export
                // mapper normalizes credit notes to positive amounts in the other direction.
                // Peppol BIS itself always carries positive quantities on a CreditNote, so
                // we flip the sign here on the way in.
                if (isCreditNote && quantity.HasValue)
                    quantity = -quantity;

                return new ExtractedInvoiceItem
                {
                    Description = Str(item, Cbc, "Description") ?? Str(item, Cbc, "Name"),
                    Quantity = quantity,
                    UnitPrice = Dec(line.Element(Cac + "Price"), Cbc, "PriceAmount"),
                    VatRate = Dec(item?.Element(Cac + "ClassifiedTaxCategory"), Cbc, "Percent"),
                    Unit = quantityElement?.Attribute("unitCode")?.Value,
                    ProductCode = Str(item, Cac, "SellersItemIdentification", Cbc, "ID"),
                };
            }).ToList();
        }

        _logger.LogDebug(
            "UBL parsed: Type={Type} DocNum={DocNum} Issuer={Issuer} Recipient={Recipient} Total={Total}",
            documentTypeName, result.DocumentNumber, result.IssuerRegistrationNumber,
            result.RecipientRegistrationNumber, result.TotalAmount);

        return result;
    }

    // ─── Secure XML loading ─────────────────────────────────────────────

    /// <summary>
    /// Loads XML with DTD processing and external entity resolution disabled.
    /// This is the trust boundary — see the class summary for the threats this blocks.
    /// </summary>
    private static XDocument LoadSecurely(byte[] xmlBytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        using var stream = new MemoryStream(xmlBytes);
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    // ─── XML helpers (same shape as IsdocImportParser) ──────────────────

    private static string? Str(XElement? parent, XNamespace ns, string element)
    {
        var value = parent?.Element(ns + element)?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? Str(XElement? parent, XNamespace childNs, string child, XNamespace grandchildNs, string grandchild)
        => Str(parent?.Element(childNs + child), grandchildNs, grandchild);

    private static decimal? Dec(XElement? parent, XNamespace ns, string element)
        => ParseDecimal(Str(parent, ns, element));

    private static decimal? ParseDecimal(string? str)
    {
        if (str == null) return null;
        return decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    private static DateTime? Date(XElement? parent, XNamespace ns, string element)
    {
        var str = Str(parent, ns, element);
        if (str == null) return null;

        // UBL date fields (BT-1/BT-2/BT-9/BT-72...) are plain calendar dates with no
        // time-zone offset ("2026-06-15"), so DateTimeStyles.None parses the clock value
        // as-is (Kind=Unspecified) and SpecifyKind just labels it Utc without shifting it.
        // (DateTimeStyles.AssumeUniversal would instead convert that value into the local
        // time zone before we re-label it — silently shifting the date by the host's UTC
        // offset, which is exactly the bug this comment exists to prevent reintroducing.)
        return DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.None, out var val)
            ? DateTime.SpecifyKind(val, DateTimeKind.Utc)
            : null;
    }

    /// <summary>
    /// Maps a UBL/UNCL4461 payment means code to Fakvio's own payment-method vocabulary.
    /// 30/58 = credit transfer (SEPA), 10 = cash, 48 = card; everything else → "Other".
    /// Intentionally coarse — this only feeds the import preview, not a source of truth.
    /// </summary>
    private static string? MapPaymentMeansCode(string? code) => code switch
    {
        null => null,
        "30" or "58" => "BankTransfer",
        "10" => "Cash",
        "48" => "CreditCard",
        _ => "Other",
    };
}
