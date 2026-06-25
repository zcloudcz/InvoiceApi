namespace Fakvio.Application.Service;

/// <summary>
/// Generates random, URL-safe inbound email aliases for bank account mailboxes.
///
/// Aliases look like "pay-7f3k9p2aqr" — the "pay-" prefix is a visual hint
/// for end users that this address belongs to the payment matching feature.
///
/// Entropy target: ~50 bits (10 base32 chars * 5 bits/char), enough to make
/// brute-force guessing of aliases infeasible. For comparison, 40 bits could
/// be exhausted in a day with a determined attacker; 50 takes ~1000 days.
/// </summary>
public interface IAliasGenerator
{
    /// <summary>
    /// Produces a new random local-part for an inbound email address
    /// with the default "pay-" prefix for payment mailboxes.
    /// Caller must verify uniqueness against MasterMailboxIndex before committing.
    /// </summary>
    string Generate();

    /// <summary>
    /// Produces a new random local-part with a custom prefix
    /// (e.g., "fak-" for invoice mailboxes).
    /// </summary>
    string Generate(string prefix);
}
