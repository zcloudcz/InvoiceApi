using Fakvio.Contracts.Dto.PaymentMatching;

namespace Fakvio.UI.Shared.Models;

/// <summary>
/// A single pair used by the bulk auto-match dialog on the Payments page.
/// Holds the unmatched bank transaction and the best-matching invoice proposal
/// found by the server.
/// </summary>
/// <param name="Transaction">The unmatched bank transaction.</param>
/// <param name="Proposal">The candidate invoice returned by FindAutoMatchForTransactionAsync.</param>
public record BulkAutoMatchItem(
    BankTransactionDto Transaction,
    TransactionAutoMatchProposalDto Proposal);
