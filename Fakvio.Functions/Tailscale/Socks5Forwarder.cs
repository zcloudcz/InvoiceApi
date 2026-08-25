// ============================================================================
// Socks5Forwarder — plain TCP listener that relays every accepted connection
// through a local SOCKS5 proxy to one fixed target.
//
// WHY this exists: tailscaled runs here in userspace networking mode, so the
// tailnet is reachable only through the SOCKS5 proxy it exposes on localhost.
// Npgsql has no SOCKS5 support and no pluggable socket factory, so the app
// cannot speak to the proxy itself. This forwarder gives Npgsql what it does
// understand — an ordinary TCP endpoint on 127.0.0.1 — and does the SOCKS5
// handshake on its behalf. See Tailscale/README.md for the whole chain.
//
// Everything here is one target, one hop: no routing table, no authentication
// method beyond "no auth" (the proxy is bound to loopback inside our own
// sandbox), no UDP associate.
// ============================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions.Tailscale;

public sealed class Socks5Forwarder
{
    // SOCKS5 wire constants (RFC 1928). Named so the byte soup below stays readable.
    private const byte Version = 0x05;
    private const byte NoAuth = 0x00;
    private const byte CmdConnect = 0x01;
    private const byte Reserved = 0x00;
    private const byte AddrTypeIPv4 = 0x01;
    private const byte AddrTypeDomain = 0x03;
    private const byte AddrTypeIPv6 = 0x04;
    private const byte ReplySucceeded = 0x00;

    // A stuck SOCKS5 endpoint must not park a socket pair forever — see HandleAsync.
    private static readonly TimeSpan DefaultNegotiationTimeout = TimeSpan.FromSeconds(15);

    // Breathing room after a failed accept(), so a broken listener cannot spin the CPU.
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromSeconds(1);

    // How many accepts in a row may fail before the loop gives up. Transient errors
    // (ECONNABORTED, EMFILE) recover long before this; ten in a row means it will not.
    private const int MaxConsecutiveAcceptFailures = 10;

    private readonly TcpListener _listener;
    private readonly IPEndPoint _localEndPoint;
    private readonly int _socksPort;
    private readonly IPAddress _target;
    private readonly int _targetPort;
    private readonly ILogger _logger;
    private readonly TimeSpan _negotiationTimeout;

    private Socks5Forwarder(TcpListener listener, int socksPort, IPAddress target, int targetPort, ILogger logger, TimeSpan negotiationTimeout)
    {
        _listener = listener;
        // Remembered once, at construction: reading LocalEndpoint later (typically from a catch
        // block) can throw ObjectDisposedException on a listener that has already been closed.
        _localEndPoint = (IPEndPoint)listener.LocalEndpoint;
        _socksPort = socksPort;
        _target = target;
        _targetPort = targetPort;
        _logger = logger;
        _negotiationTimeout = negotiationTimeout;
    }

    /// <summary>Address and port the forwarder actually bound to (the port differs from the requested one only when 0 was asked for).</summary>
    public IPEndPoint LocalEndPoint => _localEndPoint;

    /// <summary>Port the forwarder actually listens on. Differs from the requested one only when 0 was asked for.</summary>
    public int Port => _localEndPoint.Port;

    /// <summary>
    /// Binds 127.0.0.1:listenPort and starts accepting. Pass 0 to let the OS pick a free port
    /// (tests do this so they never collide with the port a real deployment uses).
    /// </summary>
    // ponytail: no stop API — the forwarder is started once at host startup and lives as long as
    // the process does. Add IDisposable / CancellationToken plumbing only if something ever needs
    // to restart the tunnel without restarting the worker.
    public static Socks5Forwarder Start(int listenPort, int socksPort, IPAddress target, int targetPort, ILogger logger)
        => Start(listenPort, socksPort, target, targetPort, logger, DefaultNegotiationTimeout);

    /// <summary>
    /// Same as <see cref="Start(int, int, IPAddress, int, ILogger)"/>, only with the handshake
    /// deadline spelled out. Internal because nothing but the test needs it: proving the deadline
    /// exists would otherwise cost 15 s of real waiting per run.
    /// </summary>
    internal static Socks5Forwarder Start(
        int listenPort, int socksPort, IPAddress target, int targetPort, ILogger logger, TimeSpan negotiationTimeout)
    {
        // Loopback only. This endpoint is an unauthenticated door into the tailnet — it must never
        // be reachable from outside the sandbox.
        var listener = new TcpListener(IPAddress.Loopback, listenPort);
        listener.Start();

        var forwarder = new Socks5Forwarder(listener, socksPort, target, targetPort, logger, negotiationTimeout);
        _ = forwarder.AcceptLoopAsync();

        logger.LogInformation(
            "Tailscale: forwarder 127.0.0.1:{ListenPort} -> {Target}:{TargetPort} (via SOCKS5 127.0.0.1:{SocksPort})",
            forwarder.Port, target, targetPort, socksPort);
        return forwarder;
    }

    /// <summary>
    /// Opens one connection to target:targetPort through the SOCKS5 proxy on 127.0.0.1:socksPort.
    /// The returned client is already past the handshake — whatever is written next goes straight
    /// to the target.
    /// </summary>
    public static async Task<TcpClient> ConnectViaSocksAsync(int socksPort, IPAddress target, int targetPort, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, socksPort, ct);
            var stream = client.GetStream();

            // Greeting: version, one supported method, "no authentication required".
            await stream.WriteAsync(new byte[] { Version, 0x01, NoAuth }, ct);

            var greetingReply = new byte[2];
            await stream.ReadExactlyAsync(greetingReply, ct);
            if (greetingReply[0] != Version || greetingReply[1] != NoAuth)
            {
                throw new IOException(
                    $"SOCKS5 handshake rejected: version 0x{greetingReply[0]:X2}, method 0x{greetingReply[1]:X2} (expected 0x05 / 0x00).");
            }

            // CONNECT request: VER CMD RSV ATYP + 4 address bytes + 2 port bytes (big endian).
            // Only IPv4 targets are supported — MagicDNS names are deliberately not used, see README.
            var request = new byte[10];
            request[0] = Version;
            request[1] = CmdConnect;
            request[2] = Reserved;
            request[3] = AddrTypeIPv4;
            target.GetAddressBytes().CopyTo(request, 4);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), (ushort)targetPort);
            await stream.WriteAsync(request, ct);

            await ReadConnectReplyAsync(stream, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the CONNECT reply. Its length depends on the bound-address type the proxy answers
    /// with, so the trailer is sized from ATYP instead of assuming the common 10-byte IPv4 form —
    /// guessing wrong would leave stray bytes in the stream and corrupt the first payload read.
    /// </summary>
    private static async Task ReadConnectReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4]; // VER REP RSV ATYP
        await stream.ReadExactlyAsync(header, ct);

        if (header[0] != Version)
        {
            throw new IOException($"SOCKS5 reply has version 0x{header[0]:X2}, expected 0x05.");
        }

        if (header[1] != ReplySucceeded)
        {
            throw new IOException($"SOCKS5 CONNECT failed with reply code 0x{header[1]:X2}.");
        }

        var trailerLength = header[3] switch
        {
            AddrTypeIPv4 => 4 + 2,
            AddrTypeIPv6 => 16 + 2,
            AddrTypeDomain => await ReadDomainLengthAsync(stream, ct) + 2,
            _ => throw new IOException($"SOCKS5 reply has unknown address type 0x{header[3]:X2}.")
        };

        await stream.ReadExactlyAsync(new byte[trailerLength], ct);
    }

    private static async Task<int> ReadDomainLengthAsync(NetworkStream stream, CancellationToken ct)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, ct);
        return length[0];
    }

    private async Task AcceptLoopAsync()
    {
        var consecutiveFailures = 0;

        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
                consecutiveFailures = 0;
            }
            catch (ObjectDisposedException)
            {
                return; // Listener closed — the process is shutting down.
            }
            catch (Exception ex)
            {
                // accept() fails transiently on Linux (ECONNABORTED when the peer vanishes during
                // the handshake, EMFILE when descriptors run out). Returning here would leave the
                // database unreachable for the rest of the process lifetime, and nothing restarts
                // this loop — so a single failure only costs a pause and a log line.
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveAcceptFailures)
                {
                    _logger.LogError(ex, "Tailscale: forwarder accept loop gave up on port {Port} after {Failures} consecutive failures",
                        _localEndPoint.Port, consecutiveFailures);
                    return;
                }

                _logger.LogWarning(ex, "Tailscale: forwarder accept failed on port {Port} (attempt {Failures}), retrying",
                    _localEndPoint.Port, consecutiveFailures);
                await Task.Delay(AcceptRetryDelay);
                continue;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var upstream = await ConnectUpstreamAsync())
            {
                var downstreamStream = client.GetStream();
                var upstreamStream = upstream.GetStream();

                // Pump both directions until either side stops. Disposing the two clients
                // afterwards tears the other copy down, so no explicit cancellation is needed.
                var toUpstream = PumpAsync(downstreamStream, upstreamStream);
                var toDownstream = PumpAsync(upstreamStream, downstreamStream);
                await Task.WhenAny(toUpstream, toDownstream);
            }
        }
        catch (Exception ex)
        {
            // Debug, not Warning: Npgsql resets pooled connections all the time and every reset
            // ends up here as a broken pipe. At Warning this would drown the real log.
            _logger.LogDebug(ex, "Tailscale: forwarded connection ended with an error");
        }
    }

    /// <summary>
    /// Opens the upstream connection under a bounded deadline. A SOCKS5 endpoint that accepts the
    /// connection and then stops answering would otherwise hang this task forever, holding both
    /// sockets open — repeated over a connection pool that is exactly how a sandbox runs out of
    /// file descriptors. The deadline covers only the handshake; the data pump is unlimited.
    /// </summary>
    private async Task<TcpClient> ConnectUpstreamAsync()
    {
        using var negotiation = new CancellationTokenSource(_negotiationTimeout);
        return await ConnectViaSocksAsync(_socksPort, _target, _targetPort, negotiation.Token);
    }

    /// <summary>
    /// Copies one direction and swallows the teardown exception. Without this the losing side of
    /// Task.WhenAny would fault after nobody is awaiting it any more.
    /// </summary>
    private static async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        try
        {
            await from.CopyToAsync(to);
        }
        catch (Exception)
        {
            // Expected on close: IOException / ObjectDisposedException from the disposed peer.
        }
    }
}
