namespace Fakvio.Domain.Enums;

/// <summary>
/// User roles in the system
/// Defines access levels and permissions
/// </summary>
public enum EUserRole
{
    /// <summary>
    /// Regular user - can work with invoices and clients in their company
    /// </summary>
    User = 0,

    /// <summary>
    /// Company administrator - can manage users and settings in their company
    /// </summary>
    Admin = 1,

    /// <summary>
    /// System administrator - can manage all companies and users
    /// Has access to all data across all tenants
    /// </summary>
    SysAdmin = 2
}
