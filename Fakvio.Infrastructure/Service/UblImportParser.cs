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
    /// are a few KB to a few hundred KB — 2 MB is already generous headroom.
    /// Kept well below ImportController's general 10 MB per-file limit on purpose:
    /// banning DOCTYPE blocks entity-expansion attacks, but a well-formed document with
    /// a huge number of small elements can still make <see cref="XDocument"/>'s DOM
    /// allocate several times its raw byte size — the smaller this cap, the smaller
    /// that worst case, without meaningfully constraining any real Peppol invoice.
    /// </summary>
    internal const int MaxXmlSizeBytes = 2 * 1024 * 1024;

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

        // DueDate (BT-9) only exists as a root element on Invoice — CreditNote's UBL
        // schema has no root cbc:DueDate at all; its (optional, rarely populated)
        // equivalent lives one level down, under PaymentMeans.
        var dueDate = isCreditNote
            ? Date(root.Element(Cac + "PaymentMeans"), Cbc, "PaymentDueDate")
            : Date(root, Cbc, "DueDate");

        var result = new InvoiceExtractedData
        {
            Source = EExtractionSource.Merged,
            DocumentNumber = Str(root, Cbc, "ID"),
            IssueDate = Date(root, Cbc, "IssueDate"),
            DueDate = dueDate,
            // BT-7 (VAT point date, "DUZP" in Czech/Slovak terms) is the direct match for
            // TaxableSupplyDate and lives at cbc:TaxPointDate on the root. It's rarely
            // populated (most invoices treat IssueDate as the tax point), so fall back to
            // the actual delivery date (BT-72) — a reasonable approximation — only if
            // TaxPointDate itself is absent.
            TaxableSupplyDate = Date(root, Cbc, "TaxPointDate")
                                 ?? Date(root.Element(Cac + "Delivery"), Cbc, "ActualDeliveryDate"),
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
            result.IssuerTaxNumber = Str(VatTaxScheme(supplier), Cbc, "CompanyID");
        }

        // ── Buyer (recipient) ───────────────────────────────────────────
        var buyer = root.Element(Cac + "AccountingCustomerParty")?.Element(Cac + "Party");
        if (buyer != null)
        {
            result.RecipientRegistrationNumber = Str(buyer, Cac, "PartyLegalEntity", Cbc, "CompanyID");
            result.RecipientName = Str(buyer, Cac, "PartyLegalEntity", Cbc, "RegistrationName")
                                    ?? Str(buyer, Cac, "PartyName", Cbc, "Name");
            result.RecipientTaxNumber = Str(VatTaxScheme(buyer), Cbc, "CompanyID");
        }

        // ── Totals ───────────────────────────────────────────────────────
        var totals = root.Element(Cac + "LegalMonetaryTotal");
        if (totals != null)
        {
            result.TotalBeforeVat = Dec(totals, Cbc, "TaxExclusiveAmount");
            result.TotalAmount = Dec(totals, Cbc, "PayableAmount") ?? Dec(totals, Cbc, "TaxInclusiveAmount");
        }

        // A Peppol invoice with a non-EUR seller can legally carry two <cac:TaxTotal>
        // elements: one in the document currency (BT-110/BT-111 area) and one in the
        // seller's accounting currency (BT-6). Picking the first unconditionally would
        // silently grab the wrong one for those documents, so prefer the TaxTotal whose
        // TaxAmount currencyID matches DocumentCurrencyCode; fall back to the first
        // TaxTotal for the (overwhelming majority of) documents that only have one.
        var taxTotals = root.Elements(Cac + "TaxTotal").ToList();
        var taxTotal = taxTotals.FirstOrDefault(t =>
                string.Equals(t.Element(Cbc + "TaxAmount")?.Attribute("currencyID")?.Value,
                    result.Currency, StringComparison.OrdinalIgnoreCase))
            ?? taxTotals.FirstOrDefault();
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
                var rawQuantity = ParseDecimal(quantityElement?.Value);

                // Unit price: prefer LineExtensionAmount / quantity over cac:Price/PriceAmount
                // directly. PriceAmount alone is only the true unit price when BaseQuantity is
                // 1 (its default) AND the line has no allowance/charge — both are optional UBL
                // features that would otherwise silently produce a wrong price (e.g. a price
                // quoted per 100 units would import 100x too high). LineExtensionAmount (the
                // line's actual net total) already reflects all of that, so dividing it by the
                // (signed) quantity recovers the correct effective unit price directly — and,
                // as a side effect, is naturally positive for both an ordinary line and a
                // negative-quantity correction line (equal signs cancel out).
                var lineExtension = Dec(line, Cbc, "LineExtensionAmount");
                var unitPrice = lineExtension.HasValue && rawQuantity is { } q && q != 0
                    ? lineExtension.Value / q
                    : Dec(line.Element(Cac + "Price"), Cbc, "PriceAmount");

                // Fakvio's ReceivedInvoice has no separate "this is a credit note" flag
                // (see docs/adr/0002-sk-einvoicing-peppol.md F1.10) — a credit is instead
                // represented as a negative line, mirroring how the (separate) UBL export
                // mapper normalizes credit notes to positive amounts in the other direction.
                // Peppol BIS itself always carries positive quantities on a CreditNote, so
                // we flip the sign here on the way in — after computing unitPrice above,
                // which must use the original (unflipped) quantity to stay correctly signed.
                var quantity = isCreditNote && rawQuantity.HasValue ? -rawQuantity : rawQuantity;

                return new ExtractedInvoiceItem
                {
                    Description = Str(item, Cbc, "Description") ?? Str(item, Cbc, "Name"),
                    Quantity = quantity,
                    UnitPrice = unitPrice,
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

    /// <summary>
    /// Picks the VAT <c>cac:PartyTaxScheme</c> out of a Party. A Party can legally carry
    /// more than one (e.g. a domestic VAT scheme plus a fiscal-representative scheme) —
    /// unconditionally taking the first one would silently pick the wrong CompanyID for
    /// those (rare) documents, so prefer the one whose TaxScheme/ID is "VAT"; fall back
    /// to the first for the overwhelming majority of documents that only have one.
    /// </summary>
    private static XElement? VatTaxScheme(XElement party)
    {
        var schemes = party.Elements(Cac + "PartyTaxScheme").ToList();
        return schemes.FirstOrDefault(s =>
                string.Equals(Str(s, Cac, "TaxScheme", Cbc, "ID"), "VAT", StringComparison.OrdinalIgnoreCase))
            ?? schemes.FirstOrDefault();
    }

    private static decimal? Dec(XElement? parent, XNamespace ns, string element)
        => ParseDecimal(Str(parent, ns, element));

    private static decimal? ParseDecimal(string? str)
    {
        if (str == null) return null;

        // Restricted styles on purpose: UBL/Peppol amounts are always plain
        // InvariantCulture decimals ("1656.25", "-3"), never grouped ("1,656.25").
        // NumberStyles.Any would also accept AllowThousands, silently misreading a
        // (technically invalid, but not impossible to encounter from a buggy sender)
        // comma-decimal value like "1,23" as 123 instead of failing to parse it.
        const NumberStyles styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        return decimal.TryParse(str, styles, CultureInfo.InvariantCulture, out var val) ? val : null;
    }

    private static DateTime? Date(XElement? parent, XNamespace ns, string element)
    {
        var str = Str(parent, ns, element);
        if (str == null) return null;

        // UBL dates are xsd:date: "YYYY-MM-DD", optionally followed by a timezone offset
        // ("2026-06-15+02:00"). Take just the date part and parse it with ParseExact — a
        // general DateTime.TryParse would also accept ambiguous, non-ISO forms (e.g.
        // "01/02/2026", which UBL never actually produces) depending on the host culture.
        var datePart = str.Length >= 10 ? str[..10] : str;
        return DateTime.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var val)
            // DateTimeStyles.None parses the clock value as-is (Kind=Unspecified); SpecifyKind
            // just labels it Utc without shifting it, unlike DateTimeStyles.AssumeUniversal
            // (which would convert into the local time zone before we re-label it — silently
            // shifting the date by the host's UTC offset).
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
