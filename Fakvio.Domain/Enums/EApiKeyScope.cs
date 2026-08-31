namespace Fakvio.Domain.Enums;

/// <summary>
/// What an API key is allowed to do. Deliberately only two values —
/// per-area scopes (invoice:read, client:write, …) would produce a permission
/// matrix with no consumer, because the MCP server hands all its tools to a
/// single agent.
///
/// Effective permissions are always role ∩ scope: a key can never do more than
/// its owning user could. The only thing the role cannot express is
/// "let the AI look but not touch" — that is exactly what these scopes add.
///
/// Persisted as a lower-case, comma-joined string ("read", "read,write").
/// </summary>
public enum EApiKeyScope
{
    /// <summary>
    /// Read-only access (safe HTTP methods).
    /// </summary>
    Read = 0,

    /// <summary>
    /// Write access (create / update / delete). Always accompanied by <see cref="Read"/>.
    /// </summary>
    Write = 1
}
