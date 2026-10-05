using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Which generated attachments an invoice e-mail carries when the sender made no explicit choice.
/// <see cref="Ubl"/> null means "automatic": decided by whether the buyer is reachable via Peppol.
/// </summary>
public readonly record struct ResolvedEmailAttachments(bool Pdf, bool Isdoc, bool? Ubl);

/// <summary>
/// Resolves the default e-mail attachments: client's BillingSettings → issuer's BillingSettings
/// (company-wide default) → system default (PDF yes, ISDOC yes, UBL automatic).
/// Same chain as <c>DocumentFileName.ResolveFormat</c>. Pure function: no database, easy to test.
/// </summary>
public static class EmailAttachmentDefaults
{
    /// <summary>
    /// Walks the chain independently for each attachment: the first level whose mode is not
    /// <see cref="EEmailAttachMode.Inherit"/> wins. A missing BillingSettings row counts as "Inherit".
    /// </summary>
    public static ResolvedEmailAttachments Resolve(BillingSettings? client, BillingSettings? issuer) => new(
        Pick(client?.EmailAttachPdf, issuer?.EmailAttachPdf) ?? true,
        Pick(client?.EmailAttachIsdoc, issuer?.EmailAttachIsdoc) ?? true,
        Pick(client?.EmailAttachUbl, issuer?.EmailAttachUbl));

    private static bool? Pick(EEmailAttachMode? client, EEmailAttachMode? issuer) => ToBool(client) ?? ToBool(issuer);

    private static bool? ToBool(EEmailAttachMode? mode) => mode switch
    {
        EEmailAttachMode.Yes => true,
        EEmailAttachMode.No => false,
        _ => null
    };

    /// <summary>
    /// Merges the explicit per-send choices into the client's own settings ("save as default for this client").
    /// A field is written only when the sender sent a value AND (the client already had its own value OR the
    /// choice differs from what was effective before) - so merely confirming the inherited defaults does not
    /// freeze them and the client keeps following later changes of the company setting.
    /// </summary>
    /// <param name="target">The client's own settings, modified in place.</param>
    /// <param name="effectiveBefore">What <see cref="Resolve"/> returned for this client before the send.</param>
    /// <param name="ublEffectiveBefore">UBL as it was actually preselected (the automatic rule already applied).</param>
    public static void ApplyChoices(
        BillingSettings target, ResolvedEmailAttachments effectiveBefore, bool ublEffectiveBefore,
        bool? attachPdf, bool? attachIsdoc, bool? attachUbl)
    {
        target.EmailAttachPdf = Merge(target.EmailAttachPdf, attachPdf, effectiveBefore.Pdf);
        target.EmailAttachIsdoc = Merge(target.EmailAttachIsdoc, attachIsdoc, effectiveBefore.Isdoc);
        target.EmailAttachUbl = Merge(target.EmailAttachUbl, attachUbl, ublEffectiveBefore);
    }

    private static EEmailAttachMode Merge(EEmailAttachMode current, bool? chosen, bool effectiveBefore)
    {
        if (chosen is null)
            return current;

        if (current == EEmailAttachMode.Inherit && chosen.Value == effectiveBefore)
            return current;

        return chosen.Value ? EEmailAttachMode.Yes : EEmailAttachMode.No;
    }
}
