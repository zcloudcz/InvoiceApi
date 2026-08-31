namespace Fakvio.McpServer.Configuration;

/// <summary>
/// Which transport the MCP server speaks. Selected once at startup from the
/// <c>FAKVIO_MCP_TRANSPORT</c> environment variable; the tools themselves are
/// identical in both modes (same assembly, same <c>WithToolsFromAssembly()</c> scan).
///
/// Junior note: the two modes differ in exactly one thing that matters —
/// where the API credential comes from. See <see cref="Client.IApiTokenProvider"/>.
/// </summary>
public enum EMcpTransport
{
    /// <summary>
    /// One process serves one local AI client over stdin/stdout. The credential is the
    /// <c>FAKVIO_API_TOKEN</c> environment variable. This is the default, so an existing
    /// Claude Desktop / Claude Code configuration keeps working untouched.
    /// </summary>
    Stdio = 0,

    /// <summary>
    /// One process serves many remote clients over HTTP (MCP Streamable HTTP on <c>/mcp</c>).
    /// The credential is the API key the caller presents on every request — never a value
    /// captured at startup.
    /// </summary>
    Http = 1
}
