using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace Fakvio.Infrastructure.Authentication.OAuth;

public static class OAuthClientHttpClientExtensions
{
    /// <summary>
    /// Dedicated named HttpClient for CIMD fetches, wired to the SSRF-safe connect callback
    /// (<see cref="SsrfSafeConnect"/>) instead of the framework default. A dedicated client, not
    /// a shared one, is deliberate: nothing else in the process may ever reuse this handler for
    /// a request whose target host is NOT re-validated per connection. Extracted from DI setup so
    /// tests can build the REAL handler.
    /// </summary>
    public static IHttpClientBuilder AddOAuthClientMetadataHttpClient(this IServiceCollection services)
        => services.AddHttpClient(OAuthClientResolver.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                // Without this, a system-wide proxy would make ConnectCallback see the PROXY's
                // address instead of the CIMD target's, so the SSRF guard would validate the
                // wrong host.
                UseProxy = false,
                Proxy = null,
                ConnectCallback = async (context, ct) =>
                {
                    var safeAddress = await SsrfSafeConnect.ResolveSafeAddressAsync(context.DnsEndPoint.Host, ct);
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        // Connect to the address just validated, not to a second DNS lookup:
                        // that closes the DNS-rebinding TOCTOU window (ADR 4.6).
                        await socket.ConnectAsync(safeAddress, context.DnsEndPoint.Port, ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            });
}
