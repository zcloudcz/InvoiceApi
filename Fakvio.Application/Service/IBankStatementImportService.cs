using Fakvio.Contracts.Dto.PaymentMatching;

namespace Fakvio.Application.Service;

/// <summary>
/// Imports a GPC/ABO bank statement file into BankTransaction rows of the current tenant
/// (idempotent: re-importing the same file creates nothing) and runs the payment matcher
/// on the new incoming credits.
/// </summary>
public interface IBankStatementImportService
{
    /// <param name="content">Raw file bytes.</param>
    /// <param name="bankAccountId">Target account; when null it is resolved per statement from the 074 account number.</param>
    Task<BankStatementImportResultDto> ImportAsync(byte[] content, long? bankAccountId, CancellationToken ct = default);
}
