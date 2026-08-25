using Fakvio.Application.Exceptions;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Checks whether the current tenant has everything it needs to issue documents:
/// issuer identity (address, IČO, DIČ for VAT payers), a bank account, and active
/// default number sequences. EPO tax-office settings are reported as a warning only.
///
/// One place owns these rules so the invoice gate, the settings screen and the
/// dashboard all answer the question the same way.
/// </summary>
public interface ITenantReadinessService
{
    /// <summary>
    /// Runs all readiness rules and returns everything that is missing.
    /// Never throws for an unready tenant — that is what the report is for.
    /// </summary>
    /// <param name="issuerId">
    /// Optional — check only this issuer. When null, every issuer of the tenant
    /// (<c>Client.IsIssuer == true</c>) is checked.
    /// </param>
    /// <param name="documentType">
    /// Optional — require a number sequence only for this document type. When null, every
    /// document type the tenant must be able to issue is checked. Callers that are about to
    /// issue one concrete document pass its type, so an unrelated missing sequence
    /// (e.g. credit notes) does not report the tenant as unable to invoice.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ReadinessReportDto> GetReportAsync(
        long? issuerId = null,
        EDocumentType? documentType = null,
        CancellationToken ct = default);

    /// <summary>
    /// Guard version of <see cref="GetReportAsync"/> for callers that must refuse the
    /// operation — throws <see cref="TenantNotReadyException"/> when a blocking issue exists.
    /// Warnings never throw.
    /// </summary>
    /// <param name="issuerId">Optional — check only this issuer (see <see cref="GetReportAsync"/>).</param>
    /// <param name="documentType">Optional — check only this document type (see <see cref="GetReportAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnsureReadyAsync(
        long? issuerId = null,
        EDocumentType? documentType = null,
        CancellationToken ct = default);
}
