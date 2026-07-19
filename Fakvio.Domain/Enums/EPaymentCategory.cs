namespace Fakvio.Domain.Enums;

/// <summary>
/// Category of a recognized recurring payment (RecognizedCounterparty.Category).
/// Used for future expense reporting — recognition itself works without it.
/// </summary>
public enum EPaymentCategory
{
    /// <summary>Social insurance payment to ČSSZ/OSSZ (sociální pojištění).</summary>
    SocialInsurance = 1,

    /// <summary>Health insurance payment (zdravotní pojištění — VZP, ZPMV, …).</summary>
    HealthInsurance = 2,

    /// <summary>Voluntary sickness insurance payment (nemocenské pojištění).</summary>
    SicknessInsurance = 3,

    /// <summary>VAT payment to / refund from the tax office (DPH).</summary>
    Vat = 4,

    /// <summary>Income tax payment / advance (daň z příjmu, zálohy).</summary>
    IncomeTax = 5,

    /// <summary>Anything else (rent, loan installment, …).</summary>
    Other = 99
}
