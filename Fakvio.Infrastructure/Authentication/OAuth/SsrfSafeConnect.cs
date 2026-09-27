using System.Net;
using System.Net.Sockets;

namespace Fakvio.Infrastructure.Authentication.OAuth;

/// <summary>
/// The SSRF guard behind the CIMD fetch (ADR 0001, docs/adr/0001-mcp-oauth21.md §4.6, threat T7).
///
/// Deliberately stricter than the pre-existing loopback/IP-literal check in
/// <c>ReceivedInvoiceTools</c> (issue #438): that one only rejects a URL whose HOST is already an
/// IP literal or "localhost". It does nothing about a host NAME that resolves to a private
/// address (DNS rebinding), because it never resolves DNS at all. A CIMD fetch runs from the
/// server's own network on attacker-controlled input (the client_id an anonymous authorize
/// request supplies), so this guard resolves DNS itself and connects to the address it just
/// validated — never to whatever a second, later lookup might return.
/// </summary>
public static class SsrfSafeConnect
{
    /// <summary>
    /// True when <paramref name="address"/> must never be dialed from server-side code: loopback,
    /// private (RFC 1918), link-local (including IPv6 fe80::/10), CGNAT (100.64.0.0/10), unique
    /// local IPv6 (fc00::/7) and the deprecated site-local fec0::/10, multicast, unspecified
    /// (0.0.0.0 / ::), "this network" (0.0.0.0/8), IETF protocol assignments incl. NAT64/DNS64
    /// (192.0.0.0/24), benchmarking (198.18.0.0/15), reserved/"Class E" (240.0.0.0/4), the
    /// well-known NAT64 prefix (64:ff9b::/96), or the two well-known Azure metadata endpoints.
    /// IPv4-mapped IPv6 addresses (::ffff:10.0.0.1) are unwrapped first so they cannot smuggle a
    /// blocked IPv4 range past a check that only looks at the IPv6 shape.
    /// </summary>
    public static bool IsForbidden(IPAddress address)
    {
        var addr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(addr) || addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any))
            return true;

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = addr.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (link-local — includes the Azure IMDS address 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 100.64.0.0/10 (CGNAT)
            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return true;
            // Multicast 224.0.0.0/4
            if (bytes[0] is >= 224 and <= 239) return true;
            // Codex review finding: additional IANA special-purpose ranges (RFC 6890) that are
            // not globally routable and must not be treated as "public" either.
            // 0.0.0.0/8 ("this network" — only 0.0.0.0 itself was covered above)
            if (bytes[0] == 0) return true;
            // 192.0.0.0/24 (IETF protocol assignments, incl. NAT64/DNS64 well-known prefixes)
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) return true;
            // 198.18.0.0/15 (benchmarking)
            if (bytes[0] == 198 && bytes[1] is 18 or 19) return true;
            // 240.0.0.0/4 (reserved/"Class E") + 255.255.255.255 broadcast
            if (bytes[0] >= 240) return true;
        }
        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (addr.IsIPv6LinkLocal || addr.IsIPv6Multicast) return true;

            var bytes = addr.GetAddressBytes();
            // fc00::/7 — unique local addresses
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            // fec0::/10 — deprecated IPv6 site-local (RFC 3879), still special-use
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0) return true;
            // 64:ff9b::/96 — well-known NAT64 translation prefix (RFC 6052): a translated
            // address here is exactly the IPv4-mapped smuggling trick, one level removed.
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B
                && bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0
                && bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0)
                return true;
        }

        // Azure instance metadata service — reachable from every App Service/VM, must never be
        // proxyable through a CIMD fetch that an anonymous caller controls the target of.
        if (addr.Equals(IPAddress.Parse("169.254.169.254")) || addr.Equals(IPAddress.Parse("168.63.129.16")))
            return true;

        return false;
    }

    /// <summary>
    /// Resolves <paramref name="host"/>, rejects it if EVERY address is safe... actually rejects
    /// the whole lookup if ANY resolved address is forbidden (a hostname that round-robins
    /// between a public and a private IP is not something we can partially trust), and returns
    /// the first safe address to connect to.
    ///
    /// Returning a single address (not the whole list) is what closes the DNS-rebinding TOCTOU
    /// window: the caller opens the actual TCP connection to the IP this method already vetted,
    /// instead of resolving the hostname a second time and connecting to whatever answer comes
    /// back on that second lookup.
    /// </summary>
    public static async Task<IPAddress> ResolveSafeAddressAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct);

        if (addresses.Length == 0)
            throw new SsrfBlockedException($"Host '{host}' did not resolve to any address.");

        foreach (var address in addresses)
        {
            if (IsForbidden(address))
                throw new SsrfBlockedException($"Host '{host}' resolves to a blocked address ({address}).");
        }

        return addresses[0];
    }
}

/// <summary>Raised when a CIMD fetch target resolves to (or otherwise reaches for) a forbidden address.</summary>
public sealed class SsrfBlockedException(string message) : Exception(message);
