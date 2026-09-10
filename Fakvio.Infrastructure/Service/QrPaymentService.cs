using Fakvio.Application.Exceptions;
using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Domain.Validation;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Service for generating QR codes for Czech invoices.
///
/// GenerateQrCodeImageAsync's strategy (in priority order), each candidate validated via
/// <see cref="CzechBankAccountValidator"/> (issue #154) before it is used:
/// 1. Valid IBAN → local SPD generation via QRCoder (fast, no external dependency)
/// 2. Valid Czech bank account (e.g., "1342333010/3030") → paylibo.com API
///    (converts Czech account format to valid QR Platba — same approach as Monarc.Core)
/// 3. Neither → <see cref="Fakvio.Application.Exceptions.NoUsableBankConnectionException"/>.
///    No QR code is generated — a SIND-only "QR Faktura" would look payable and is not.
///
/// The paylibo API is the proven solution for Czech domestic bank accounts
/// that don't have an IBAN. It generates a valid SPD QR code that all Czech
/// banking apps (George, mBank, Fio, etc.) can reliably scan.
///
/// GenerateSindStringAsync and GenerateSpdWithInvoiceAsync are unaffected — they are debugging /
/// inspection endpoints that deliberately return invoice-only SIND data, documented as such.
/// </summary>
public class QrPaymentService : IQrPaymentService
{
    private readonly TenantDbContext _context;
    private readonly IPayliboClient _payliboClient;
    private readonly ILogger<QrPaymentService> _logger;

    /// <summary>
    /// Application name embedded in the X-SW proprietary SIND attribute.
    /// </summary>
    private const string SoftwareName = "INVOICEAPI";

    public QrPaymentService(
        TenantDbContext context,
        IPayliboClient payliboClient,
        ILogger<QrPaymentService> logger)
    {
        _context = context;
        _payliboClient = payliboClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> GenerateSindStringAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await LoadInvoiceWithDetailsAsync(invoiceId, cancellationToken);
        var builder = BuildSindFromInvoice(invoice);
        return builder.Build();
    }

    /// <inheritdoc />
    public async Task<string> GenerateSpdWithInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await LoadInvoiceWithDetailsAsync(invoiceId, cancellationToken);

        // If IBAN is available, generate a simple SPD (QR Platba) with payment data only
        if (!string.IsNullOrWhiteSpace(invoice.IBAN))
        {
            return SpdIntegrator.BuildSimpleSpdString(
                invoice.IBAN,
                invoice.SWIFT,
                invoice.TotalWithVat,
                invoice.Currency?.Code,
                invoice.DueDate,
                invoice.VariableSymbol,
                invoice.DocumentNumber);
        }

        // No IBAN — fall back to standalone SIND (QR Faktura only)
        var builder = BuildSindFromInvoice(invoice);
        _logger.LogWarning("Invoice {InvoiceId} has no IBAN — generating QR Faktura only (no payment data)",
            invoiceId);
        return builder.Build();
    }

    /// <inheritdoc />
    public async Task<byte[]> GenerateQrCodeImageAsync(long invoiceId, int pixelsPerModule = 10,
        CancellationToken cancellationToken = default)
    {
        var invoice = await LoadInvoiceWithDetailsAsync(invoiceId, cancellationToken);

        // Strategy 1: valid IBAN → local SPD generation (fastest, no external dependency).
        // Checksum-validated (issue #154) — an IBAN that merely "looks like" one used to reach
        // SpdIntegrator unvalidated and produce a QR code nobody's banking app could pay.
        var hasValidIban = CzechBankAccountValidator.IsValidIban(invoice.IBAN);
        if (hasValidIban)
        {
            var spdContent = SpdIntegrator.BuildSimpleSpdString(
                invoice.IBAN!,
                invoice.SWIFT,
                invoice.TotalWithVat,
                invoice.Currency?.Code,
                invoice.DueDate,
                invoice.VariableSymbol,
                invoice.DocumentNumber);

            _logger.LogInformation("Generating QR Platba for invoice {InvoiceId} via local SPD (IBAN available)",
                invoiceId);
            return GenerateQrPng(spdContent, pixelsPerModule);
        }
        if (!string.IsNullOrWhiteSpace(invoice.IBAN))
        {
            _logger.LogWarning(
                "Invoice {InvoiceId} has an IBAN that fails the checksum ({Iban}) — ignoring it",
                invoiceId, invoice.IBAN);
        }

        // Strategy 2: valid Czech bank account → paylibo.com API (same approach as Monarc.Core —
        // the API handles Czech account format natively). Also checksum-validated (issue #154).
        var hasValidCzechAccount = CzechBankAccountValidator.IsValidCzechAccountNumber(invoice.BankAccountNumber);
        if (hasValidCzechAccount)
        {
            _logger.LogInformation(
                "Generating QR Platba for invoice {InvoiceId} via paylibo API (Czech bank account: {Account})",
                invoiceId, invoice.BankAccountNumber);

            var payliboResult = await GenerateViaPayliboAsync(invoice);
            if (payliboResult.Length > 0)
            {
                return payliboResult;
            }

            _logger.LogWarning("Paylibo API failed for invoice {InvoiceId}", invoiceId);
        }
        else if (!string.IsNullOrWhiteSpace(invoice.BankAccountNumber))
        {
            _logger.LogWarning(
                "Invoice {InvoiceId} has a bank account number that fails the checksum ({Account}) — ignoring it",
                invoiceId, invoice.BankAccountNumber);
        }

        // No usable payment destination (issue #154): a SIND-only "QR Faktura" LOOKS like a
        // payment QR code but carries no payment instructions — nothing on the printed invoice
        // told the reader that. Rather than print a decorative code, generate none at all; the
        // caller (PdfExportService treats this as non-critical, InvoiceController returns 400)
        // decides what the user sees instead.
        throw new NoUsableBankConnectionException(invoiceId);
    }

    /// <summary>
    /// Generates a QR payment image via the paylibo.com API using Czech bank account format.
    /// Parses the bank account number from "prefix-account/bankCode" or "account/bankCode" format.
    ///
    /// Same approach as Monarc.Core InvoiceService.cs:
    ///   accountPrefix = part before dash (if present)
    ///   accountNumber = part after dash (or full number if no dash)
    ///   bankCode = part after slash
    /// </summary>
    private async Task<byte[]> GenerateViaPayliboAsync(Invoice invoice)
    {
        // Parse Czech bank account format: "prefix-account/bankCode" or "account/bankCode"
        var (prefix, accountNumber, bankCode) = ParseCzechBankAccount(invoice.BankAccountNumber!);

        var options = new PayliboQrOptions
        {
            accountPrefix = prefix,
            accountNumber = accountNumber,
            bankCode = bankCode,
            amount = invoice.TotalWithVat,
            currency = invoice.Currency?.Code ?? "CZK",
            vs = invoice.VariableSymbol,
            date = invoice.DueDate.HasValue ? DateOnly.FromDateTime(invoice.DueDate.Value) : null,
            message = invoice.DocumentNumber,
            size = 200,
            branding = false,
            compress = false
        };

        return await _payliboClient.CreateQrPaymentImageAsync(options);
    }

    /// <summary>
    /// Parses a Czech bank account string into prefix, account number, and bank code.
    ///
    /// Supported formats (same parsing logic as Monarc.Core):
    /// - "1342333010/3030" → prefix=null, account="1342333010", bankCode="3030"
    /// - "19-1234567890/0100" → prefix="19", account="1234567890", bankCode="0100"
    /// - "1234567890" → prefix=null, account="1234567890", bankCode=null (no slash)
    /// </summary>
    private static (string? prefix, string? accountNumber, string? bankCode) ParseCzechBankAccount(string bankAccount)
    {
        string? prefix = null;
        string? accountNumber = null;
        string? bankCode = null;

        // Split by "/" to extract bank code
        var slashParts = bankAccount.Split('/');
        var accountPart = slashParts[0];
        if (slashParts.Length > 1)
        {
            bankCode = slashParts[1].Trim();
        }

        // Split by "-" to extract prefix (same logic as Monarc.Core)
        var dashParts = accountPart.Split('-');
        if (dashParts.Length > 1)
        {
            // Has prefix: "19-1234567890"
            prefix = dashParts[0].Trim();
            accountNumber = dashParts[1].Trim();
        }
        else
        {
            // No prefix: "1342333010"
            accountNumber = dashParts[0].Trim();
        }

        return (prefix, accountNumber, bankCode);
    }

    /// <summary>
    /// Generates a QR code PNG image from any string content using QRCoder.
    /// Used for local SPD and SIND QR code generation.
    /// </summary>
    private static byte[] GenerateQrPng(string content, int pixelsPerModule)
    {
        using var qrGenerator = new QRCodeGenerator();
        var qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Loads an invoice with all related data needed for QR code generation:
    /// client, issuer, currency, and line items (with VAT rates).
    /// </summary>
    private async Task<Invoice> LoadInvoiceWithDetailsAsync(long invoiceId, CancellationToken ct)
    {
        return await _context.Invoice
            .AsNoTracking()
            .Include(i => i.Client)
            .Include(i => i.Issuer)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem).ThenInclude(item => item.VatRate)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException($"Invoice with ID {invoiceId} not found.");
    }

    /// <summary>
    /// Builds a SindBuilder populated with all available invoice data.
    /// Maps invoice fields to SIND attributes (Czech QR Faktura standard).
    /// </summary>
    private static SindBuilder BuildSindFromInvoice(Invoice invoice)
    {
        var builder = new SindBuilder();

        // === Required attributes ===
        builder.SetDocumentId(invoice.DocumentNumber ?? invoice.Id.ToString());
        builder.SetIssueDate(invoice.IssueDate ?? DateTime.UtcNow);
        builder.SetAmount(invoice.TotalWithVat);

        // === Payment attributes ===
        builder.SetVariableSymbol(invoice.VariableSymbol);
        builder.SetAccount(invoice.IBAN, invoice.SWIFT);
        builder.SetCurrency(invoice.Currency?.Code);
        builder.SetDueDate(invoice.DueDate);

        // === Issuer identification ===
        builder.SetIssuerTaxNumber(invoice.Issuer?.TaxNumber);
        builder.SetIssuerRegistrationNumber(invoice.Issuer?.RegistrationNumber);

        // === Recipient identification ===
        builder.SetRecipientTaxNumber(invoice.Client?.TaxNumber);
        builder.SetRecipientRegistrationNumber(invoice.Client?.RegistrationNumber);

        // === Tax dates ===
        builder.SetTaxableSupplyDate(invoice.TaxableSupplyDate);

        // === Document type: 1=corrective (credit note), 9=other (standard invoice) ===
        builder.SetDocumentType(invoice.DocumentType == EDocumentType.CreditNote ? 1 : 9);

        // === VAT breakdown by rate group ===
        SetVatBreakdown(builder, invoice);

        // === Software identifier (proprietary attribute) ===
        builder.SetSoftware(SoftwareName);

        return builder;
    }

    /// <summary>
    /// Groups invoice items by VAT rate and sets the appropriate SIND tax attributes.
    /// Czech VAT rates: Standard (21%) → TB0/T0, First reduced (12%) → TB1/T1, Zero → NTB.
    /// </summary>
    private static void SetVatBreakdown(SindBuilder builder, Invoice invoice)
    {
        if (invoice.InvoiceItem == null || !invoice.InvoiceItem.Any())
        {
            builder.SetNonTaxableAmount(invoice.TotalWithVat);
            return;
        }

        var groups = invoice.InvoiceItem
            .GroupBy(item => item.VatRatePercentage)
            .ToDictionary(g => g.Key, g => new
            {
                TaxBase = g.Sum(i => i.TotalBeforeVat),
                TaxAmount = g.Sum(i => i.VatAmount)
            });

        if (groups.TryGetValue(21m, out var standard))
            builder.SetStandardVat(standard.TaxBase, standard.TaxAmount);

        if (groups.TryGetValue(12m, out var reduced1))
            builder.SetReducedVat1(reduced1.TaxBase, reduced1.TaxAmount);

        if (groups.TryGetValue(0m, out var zeroRate))
            builder.SetNonTaxableAmount(zeroRate.TaxBase);
    }
}
