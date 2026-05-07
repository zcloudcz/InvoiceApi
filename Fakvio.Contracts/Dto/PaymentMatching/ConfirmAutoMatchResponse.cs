namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>
/// Response returned after a successful auto-match confirmation.
/// The UI uses PaidAmount and Remaining to refresh the invoice status display.
/// </summary>
public class ConfirmAutoMatchResponse
{
    /// <summary>Id of the newly created PaymentMatch row.</summary>
    public long PaymentMatchId { get; set; }

    /// <summary>Total amount paid on the invoice after this match.</summary>
    public decimal PaidAmount { get; set; }

    /// <summary>Remaining balance on the invoice after this match.</summary>
    public decimal Remaining { get; set; }
}
