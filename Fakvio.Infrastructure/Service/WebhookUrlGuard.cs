using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// SSRF protection for outbound webhook URLs (DEVGUIDE §4.15 Webhooks — security-critical).
///
/// A tenant-supplied URL is attacker-controlled input: without this guard a malicious or
/// compromised tenant could point a webhook at an internal service, the cloud metadata
/// endpoint (169.254.169.254), or localhost, and use our own server as an SSRF proxy with
/// our server's network position and (for metadata) credentials.
///
/// Two layers, both required:
///   1. <see cref="ValidateScheme"/> — cheap, at save/test time. Rejects non-https URLs
///      (http allowed only for localhost in Development, for local testing).
///   2. <see cref="ConnectCallback"/> — wired into the named HttpClient's SocketsHttpHandler.
///      Resolves the host to an IP address ITSELF (not trusting any earlier check) and
///      validates THAT address right before opening the socket. This is what actually closes
///      the DNS-rebinding hole: a hostname that resolved to a public IP at validation time
///      could resolve to 169.254.169.254 by the time the HTTP client connects — the only safe
///      place to check is at the point where the connection is actually made.
/// </summary>
public static class WebhookUrlGuard
{
    /// <summary>Set once at startup (Program.cs) to true in Development so http://localhost webhooks can be tested locally. Always false in production.</summary>
    public static bool AllowLoopback { get; set; }

    /// <summary>
    /// Validates the URL's scheme/host shape. Throws <see cref="InvalidOperationException"/>
    /// with a user-facing message when rejected.
    /// </summary>
    public static void ValidateScheme(Uri uri, IHostEnvironment env)
    {
        if (uri.Scheme == Uri.UriSchemeHttps)
            return;

        var isLocalhost = uri.Host is "localhost" or "127.0.0.1" or "::1";
        if (uri.Scheme == Uri.UriSchemeHttp && env.IsDevelopment() && isLocalhost)
            return; // Local dev convenience only.

        throw new InvalidOperationException(
            "Webhook URL must use https:// (http://localhost is only allowed in Development).");
    }

    /// <summary>
    /// ConnectCallback for SocketsHttpHandler: resolves the target host, rejects it if the
    /// resolved address is not a routable public address, and connects directly to that
    /// validated address (never re-resolving — that would reopen the DNS-rebinding window).
    /// </summary>
    public static async ValueTask<System.IO.Stream> ConnectCallback(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var address = addresses.FirstOrDefault(a => !IsBlockedAddress(a) || (AllowLoopback && IPAddress.IsLoopback(a)))
            ?? throw new InvalidOperationException(
                $"Webhook host '{context.DnsEndPoint.Host}' resolves only to a private/blocked address — refusing to connect.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when the address is loopback, private (RFC1918), link-local, multicast, CGNAT
    /// (100.64.0.0/10), IPv6 unique-local (fc00::/7), or the cloud metadata address
    /// (169.254.169.254 — already covered by link-local, called out here for clarity).
    /// </summary>
    internal static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 10) return true;                              // 10.0.0.0/8
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;   // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;               // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;               // 169.254.0.0/16 (link-local + cloud metadata)
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return true;  // 100.64.0.0/10 (CGNAT)
            if (b[0] == 0) return true;                                 // 0.0.0.0/8
            if (b[0] >= 224) return true;                               // 224.0.0.0/4 multicast + reserved
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 — unique local address (ULA)
            // IPv4-mapped IPv6 (::ffff:a.b.c.d) must be checked as its IPv4 form too.
            if (address.IsIPv4MappedToIPv6 && IsBlockedAddress(address.MapToIPv4())) return true;
        }

        return false;
    }
}
