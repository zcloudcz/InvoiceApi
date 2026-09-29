using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.Ubl;

/// <summary>
/// Per-document pre-flight checks for the UBL/Peppol export (ADR 0002 §4.1.4, F1.5).
///
/// Unlike <c>ITenantReadinessService</c> (tenant-wide, checked once), these rules depend
/// on the specific invoice being exported — a client with an incomplete address only blocks
/// *that client's* invoices, not every export. <c>UblExportService</c> calls
/// <see cref="Check"/> before <see cref="UblMapper.Map(Invoice, IReadOnlyList{string}?)"/> and
/// throws <c>TenantNotReadyException</c> with the blocking issues found, reusing the exact same
/// 400 body shape the tenant readiness gate already uses.
///
/// Pure function, no I/O — takes the already-loaded <see cref="Invoice"/> graph (Issuer with
/// Address, Client with Address, InvoiceItem) and returns issues; it never queries anything
/// itself, same design as <see cref="UblMapper"/>.
/// </summary>
internal static class UblPreflight
{
    private const string MyCompanyRoute = "/my-company";
    private const string ClientRoutePrefix = "/clients/";

    /// <summary>
    /// Returns every blocking issue found. An empty list means the invoice is ready to export.
    /// </summary>
    internal static List<ReadinessIssueDto> Check(Invoice invoice)
    {
        var issues = new List<ReadinessIssueDto>();

        if (invoice.Status == EInvoiceStatus.Draft)
            issues.Add(Issue(ReadinessCodes.EinvoiceDraft));

        if (invoice.DocumentType == EDocumentType.Proforma)
            issues.Add(Issue(ReadinessCodes.EinvoiceProformaNotSupported));

        if (invoice.Client is null)
        {
            // Nothing else about the buyer can be checked without a Client — later checks would
            // just report the same problem under a different code.
            issues.Add(Issue(ReadinessCodes.EinvoiceBuyerMissing));
            return AddCommonChecks(invoice, issues, buyerLoaded: false);
        }

        return AddCommonChecks(invoice, issues, buyerLoaded: true);
    }

    private static List<ReadinessIssueDto> AddCommonChecks(Invoice invoice, List<ReadinessIssueDto> issues, bool buyerLoaded)
    {
        var issuer = invoice.Issuer;
        var items = (invoice.InvoiceItem ?? Enumerable.Empty<InvoiceItem>()).Where(i => !i.IsTextRow).ToList();

        if (issuer != null)
        {
            AddAddressIssue(issuer, MyCompanyRoute, issues);
            if (UblCodes.EndpointId(issuer) is null)
                issues.Add(Issue(ReadinessCodes.EinvoiceSellerEndpointMissing, fixRoute: MyCompanyRoute));
        }

        if (buyerLoaded && invoice.Client != null)
        {
            var clientRoute = ClientRoutePrefix + invoice.Client.Id;
            AddAddressIssue(invoice.Client, clientRoute, issues);
            if (UblCodes.EndpointId(invoice.Client) is null)
                issues.Add(Issue(ReadinessCodes.EinvoiceBuyerEndpointMissing, fixRoute: clientRoute));
        }

        if (invoice.PaymentMethod == EPaymentMethod.BankTransfer &&
            string.IsNullOrWhiteSpace(invoice.IBAN) && string.IsNullOrWhiteSpace(invoice.BankAccountNumber))
        {
            issues.Add(Issue(ReadinessCodes.EinvoicePaymentAccountMissing, fixRoute: MyCompanyRoute));
        }

        var issuerIsVatPayer = issuer?.IsVatPayer == true;
        if (issuerIsVatPayer && items.Count > 0)
        {
            var categories = items
                .Select(i => UblCodes.VatCategory(i.VatRegime, i.VatRatePercentage, issuerIsVatPayer).Code)
                .ToList();
            // BR-O-11..14: an out-of-scope line cannot share a document with a chargeable one —
            // the whole invoice must be either entirely out of scope or entirely not.
            if (categories.Contains("O") && categories.Any(c => c != "O"))
                issues.Add(Issue(ReadinessCodes.EinvoiceOutOfScopeMixed));

            var hasReverseCharge = items.Any(i => i.VatRegime == EVatRegime.ReverseCharge);
            if (hasReverseCharge)
            {
                var sellerVatIdMissing = string.IsNullOrWhiteSpace(issuer?.TaxNumber);
                var buyerVatIdMissing = !buyerLoaded || invoice.Client == null ||
                    !invoice.Client.IsVatPayer || string.IsNullOrWhiteSpace(invoice.Client.TaxNumber);
                if (sellerVatIdMissing || buyerVatIdMissing)
                    issues.Add(Issue(ReadinessCodes.EinvoiceReverseChargeVatIdMissing));
            }
        }

        // Phase 1 has no stored exchange rate (BT-6/BT-111) — a Slovak seller must invoice in
        // EUR until that lands (ADR 0002 §4.1.4, deliberately deferred).
        var issuerCountry = UblCodes.CountryToIso2(
            issuer?.Address?.FirstOrDefault(a => a.IsPrimary)?.Country ?? issuer?.Address?.FirstOrDefault()?.Country);
        var currencyCode = invoice.Currency?.Code;
        if (issuerCountry == "SK" && !string.Equals(currencyCode, "EUR", StringComparison.OrdinalIgnoreCase))
            issues.Add(Issue(ReadinessCodes.EinvoiceSkNonEurCurrency));

        if (items.Count == 0)
            issues.Add(Issue(ReadinessCodes.EinvoiceNoLines));

        return issues;
    }

    private static void AddAddressIssue(Client party, string fixRoute, List<ReadinessIssueDto> issues)
    {
        var address = party.Address?.FirstOrDefault(a => a.IsPrimary) ?? party.Address?.FirstOrDefault();

        var missing = new List<string>();
        if (address is null)
        {
            missing.Add(nameof(Client.Address));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(address.Street)) missing.Add(nameof(Address.Street));
            if (string.IsNullOrWhiteSpace(address.City)) missing.Add(nameof(Address.City));
            if (string.IsNullOrWhiteSpace(address.PostalCode)) missing.Add(nameof(Address.PostalCode));
            if (UblCodes.CountryToIso2(address.Country) is null) missing.Add(nameof(Address.Country));
        }

        if (missing.Count > 0)
            issues.Add(new ReadinessIssueDto
            {
                Code = ReadinessCodes.EinvoiceAddressIncomplete,
                Severity = EReadinessSeverity.Blocking,
                MissingFields = missing,
                FixRoute = fixRoute,
                IssuerId = party.Id,
                IssuerName = party.CompanyName
            });
    }

    private static ReadinessIssueDto Issue(string code, string fixRoute = "") => new()
    {
        Code = code,
        Severity = EReadinessSeverity.Blocking,
        MissingFields = [],
        FixRoute = fixRoute
    };
}
