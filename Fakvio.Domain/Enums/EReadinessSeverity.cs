namespace Fakvio.Domain.Enums;

/// <summary>
/// How serious a tenant readiness problem is.
/// Existing values must NEVER be renumbered — the integer travels over the API.
/// </summary>
public enum EReadinessSeverity
{
    /// <summary>
    /// The setting is missing and business operations that depend on it must be refused
    /// (e.g. completing an invoice without an issuer address).
    /// </summary>
    Blocking = 1,

    /// <summary>
    /// The setting is missing but nothing is blocked right now — the user will only hit
    /// a wall later, in a specific feature (e.g. EPO export without tax office code).
    /// </summary>
    Warning = 2
}
