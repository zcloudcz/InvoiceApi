namespace Fakvio.Contracts.Dto.Auth;

/// <summary>
/// Result of the anonymous ARES lookup used by the self-registration form
/// (<c>GET /api/auth/ares/{registrationNumber}</c>).
///
/// Deliberately narrow: it carries only what the registration form pre-fills,
/// not the full <c>ClientDto</c>. The lookup is reachable without a JWT, so the
/// less it exposes, the smaller the surface an abuser gets — the tenant-scoped
/// <c>ClientController.FetchFromAres</c> stays authenticated for everything else.
/// </summary>
public class AresLookupResponse
{
    /// <summary>Registration number (IČO) as confirmed by the registry.</summary>
    public string RegistrationNumber { get; set; } = string.Empty;

    /// <summary>Official company name from ARES.</summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>Registered office — street and building number. Empty when ARES has none.</summary>
    public string Street { get; set; } = string.Empty;

    /// <summary>Registered office — city.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Registered office — postal code.</summary>
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>Registered office — country (ARES defaults to "Česká republika").</summary>
    public string Country { get; set; } = string.Empty;
}
