namespace Fakvio.Domain.Enums;

/// <summary>
/// Whether one generated attachment (PDF / ISDOC / UBL) is added to an invoice e-mail by default.
/// Stored per client and per issuer on <c>BillingSettings</c>.
///
/// Resolution: client value → issuer (company-wide) value → system default
/// (PDF yes, ISDOC yes, UBL automatic). <see cref="Inherit"/> means "no opinion here, ask the next level".
/// </summary>
public enum EEmailAttachMode
{
    /// <summary>
    /// Not configured at this level. Falls through to the next level; at the very end the system default
    /// applies (PDF and ISDOC on, UBL "automatic" = only for Slovak / Peppol-reachable buyers).
    /// </summary>
    Inherit = 0,

    /// <summary>Always attach.</summary>
    Yes = 1,

    /// <summary>Never attach.</summary>
    No = 2
}
