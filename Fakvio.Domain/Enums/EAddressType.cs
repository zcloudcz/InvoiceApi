namespace Fakvio.Domain.Enums;

/// <summary>
/// Type of address (billing, shipping, etc.)
/// Each client can have multiple addresses of different types
/// </summary>
public enum EAddressType
{
    /// <summary>
    /// Primary business address (registered office)
    /// This is the official address from business registry (ARES)
    /// </summary>
    Primary = 1,

    /// <summary>
    /// Billing address - where invoices should be sent
    /// If different from primary address
    /// </summary>
    Billing = 2,

    /// <summary>
    /// Shipping/Delivery address - where goods are delivered
    /// </summary>
    Shipping = 3,

    /// <summary>
    /// Correspondence address - for general mail
    /// </summary>
    Correspondence = 4
}
