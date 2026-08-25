// ============================================================================
// Socks5ForwarderTests — the forwarder that lets Npgsql reach the tailnet.
//
// Nothing here spawns a process or leaves the loopback interface: a fake SOCKS5
// server stands in for tailscaled, so the whole SOCKS5 conversation (greeting,
// CONNECT, reply) can be asserted byte for byte. That conversation is the part
// that is impossible to debug in production — a wrong byte just looks like "the
// database is down" in an Azure log.
// ============================================================================

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Fakvio.Functions.Tailscale;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class Socks5ForwarderTests
{
    // The tailnet address of fakvio-db-server and its PostgreSQL port — the same values the
    // production default uses, so the CONNECT assertion below is a real end-to-end check.
    private static readonly IPAddress TargetAddress = IPAddress.Parse("100.69.241.17");
    private const int TargetPort = 5544;

    private const int ClientCount = 3;
    private const int PayloadSize = 64 * 1024; // Larger than one TCP segment, so partial reads happen.

    // SOCKS5 fields (RFC 1928) the fake server below plays with.
    private const byte AddrTypeIPv4 = 0x01;
    private const byte AddrTypeUnknown = 0x02; // Not defined by the RFC — a proxy answering this is broken.
    private const byte AddrTypeDomain = 0x03;
    private const byte AddrTypeIPv6 = 0x04;
    private const byte ReplySucceeded = 0x00;
    private const byte NoAuth = 0x00;
    private const byte NoAcceptableMethods = 0xFF; // What a proxy answers when it insists on authentication.

    // Domain name the fake server reports as its bound address when asked for an ATYP=3 reply.
    private const string BoundDomainName = "fakvio-db-server";

    // Upper bound for "the other side should notice this by now" waits. Never actually reached.
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ForwardsConcurrentConnections_ThroughSocks5Connect()
    {
        using var socks = new FakeSocks5Server();
        var forwarder = StartForwarder(socks);

        var roundTrips = Enumerable.Range(0, ClientCount)
            .Select(seed => RoundTripAsync(forwarder.Port, (byte)seed))
            .ToArray();

        var results = await Task.WhenAll(roundTrips);

        // Every client got its own payload back — proof that connections are not crossed.
        for (var i = 0; i < ClientCount; i++)
        {
            results[i].ShouldBe(BuildPayload((byte)i));
        }

        socks.ConnectCount.ShouldBe(ClientCount);
    }

    [Fact]
    public async Task ForwardsPayload_WhenProxyRepliesWithIPv6BoundAddress()
    {
        // A proxy may answer CONNECT with any address type, and the reply trailer is sized by it:
        // an IPv6 bound address is 12 bytes longer than the usual IPv4 one. If the code assumed the
        // 10-byte IPv4 form, those 12 bytes would be handed to Npgsql as if they were data.
        using var socks = new FakeSocks5Server(replyAddressType: AddrTypeIPv6);
        var forwarder = StartForwarder(socks);

        var received = await RoundTripAsync(forwarder.Port, seed: 0);

        received.ShouldBe(BuildPayload(0));
    }

    [Fact]
    public async Task ClosesClientConnection_WhenProxyRejectsConnect()
    {
        // Reply code 0x05 = "connection refused by destination host" — typically an ACL that does
        // not allow the database port. The client must be told (by a closed socket), not left
        // talking into a tunnel that goes nowhere.
        using var socks = new FakeSocks5Server(replyCode: 0x05);
        var forwarder = StartForwarder(socks);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.Port);

        (await ReadOneByteAsync(client)).ShouldBe(0, "the forwarder should have closed the connection");
    }

    [Fact]
    public void ListensOnLoopbackOnly()
    {
        // The forwarder is an unauthenticated door into the tailnet. Binding it to any other
        // address would expose the database to whatever else can reach this host.
        using var socks = new FakeSocks5Server();
        var forwarder = StartForwarder(socks);

        forwarder.LocalEndPoint.Address.ShouldBe(IPAddress.Loopback);
    }

    [Fact]
    public async Task ClosesClientConnection_WhenProxyAcceptsButNeverAnswers()
    {
        // The nastiest failure mode: the proxy takes the connection and then goes mute. Without a
        // deadline on the handshake both sockets would stay open forever, and a connection pool
        // retrying into that is how a sandbox runs out of file descriptors. Deadline shortened to
        // 500 ms here — the production value is 15 s, which is a deadline, not a test budget.
        using var socks = new FakeSocks5Server(silent: true);
        var forwarder = StartForwarder(socks, negotiationTimeout: TimeSpan.FromMilliseconds(500));

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.Port);

        (await ReadOneByteAsync(client)).ShouldBe(0, "the forwarder should have given up on the mute proxy");
    }

    [Fact]
    public async Task ForwardsPayload_WhenProxyRepliesWithDomainBoundAddress()
    {
        // The third reply shape: a length-prefixed domain name. Its trailer can only be sized by
        // reading that length byte first, so a forwarder that skipped it would hand the leftover
        // name bytes to Npgsql as the first bytes of the PostgreSQL handshake.
        using var socks = new FakeSocks5Server(replyAddressType: AddrTypeDomain);
        var forwarder = StartForwarder(socks);

        var received = await RoundTripAsync(forwarder.Port, seed: 0);

        received.ShouldBe(BuildPayload(0));
    }

    [Fact]
    public async Task ConnectViaSocks_Fails_WhenProxyDemandsAuthentication()
    {
        // tailscaled offers "no authentication" on its loopback proxy. If that ever changed, the
        // handshake must fail loudly instead of writing a CONNECT request the proxy is not reading.
        using var socks = new FakeSocks5Server(greetingMethod: NoAcceptableMethods);

        var exception = await Should.ThrowAsync<IOException>(() => ConnectThroughAsync(socks));

        exception.Message.ShouldContain("handshake rejected");
    }

    [Fact]
    public async Task ConnectViaSocks_Fails_WhenProxyRepliesWithUnknownAddressType()
    {
        // An unknown ATYP makes the trailer length unknowable. Guessing it would desynchronise the
        // stream, which surfaces much later as a corrupt PostgreSQL protocol message.
        using var socks = new FakeSocks5Server(replyAddressType: AddrTypeUnknown);

        var exception = await Should.ThrowAsync<IOException>(() => ConnectThroughAsync(socks));

        exception.Message.ShouldContain("unknown address type");
    }

    [Fact]
    public async Task ClosesUpstreamConnection_WhenClientDisconnects()
    {
        // Half of the teardown contract: Npgsql closing a pooled connection must release the
        // upstream socket too. Leaking one socket per pooled connection is how the sandbox runs
        // out of file descriptors after a few hours of normal traffic.
        using var socks = new FakeSocks5Server();
        var forwarder = StartForwarder(socks);

        await RoundTripAsync(forwarder.Port, seed: 0); // Disposes the client at the end of the call.

        await socks.ConnectionClosed.WaitAsync(TeardownTimeout);
    }

    [Fact]
    public async Task ClosesClientConnection_WhenProxyClosesAfterSuccessfulConnect()
    {
        // The other half: when the tunnel dies mid-connection (daemon restart, ACL change), the
        // client has to see the socket close. Npgsql then fails fast and retries instead of
        // blocking until its command timeout.
        using var socks = new FakeSocks5Server(closeAfterReply: true);
        var forwarder = StartForwarder(socks);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarder.Port);

        (await ReadOneByteAsync(client)).ShouldBe(0, "the forwarder should have closed the client side too");
    }

    [Fact]
    public void StartYieldsNoForwarderWhenAnotherWorkerAlreadyServesThePort()
    {
        // Flex Consumption runs several worker processes on one instance and they share the sandbox,
        // loopback included: a taken port means a sibling worker's forwarder is already serving it,
        // and every worker's connection string reaches that one. So this must be a log line, not an
        // exception that takes the whole tunnel bring-up down with it (issue #321).
        using var sibling = new TcpListener(IPAddress.Loopback, 0)
        {
            // Must be set before Start(): without it Windows lets the second bind succeed and the
            // collision this test is about would never happen.
            ExclusiveAddressUse = true
        };
        sibling.Start();
        var occupiedPort = ((IPEndPoint)sibling.LocalEndpoint).Port;

        var forwarder = Socks5Forwarder.Start(
            occupiedPort, socksPort: occupiedPort, TargetAddress, TargetPort, NullLogger.Instance);

        forwarder.ShouldBeNull();
    }

    /// <summary>Runs one SOCKS5 handshake straight against the fake proxy, with no forwarder in between.</summary>
    private static async Task ConnectThroughAsync(FakeSocks5Server socks)
    {
        using var timeout = new CancellationTokenSource(TeardownTimeout);
        using var connection = await Socks5Forwarder.ConnectViaSocksAsync(
            socks.Port, TargetAddress, TargetPort, timeout.Token);
    }

    // Null-forgiving: listenPort 0 always binds, so this overload never returns the null that the
    // "port already served" case produces.
    private static Socks5Forwarder StartForwarder(FakeSocks5Server socks, TimeSpan? negotiationTimeout = null) => Socks5Forwarder.Start(
        listenPort: 0, // OS-assigned: never collides with a real deployment or a parallel test.
        socksPort: socks.Port,
        target: TargetAddress,
        targetPort: TargetPort,
        logger: NullLogger.Instance,
        // Default keeps the other tests on the production deadline; they never reach it.
        negotiationTimeout: negotiationTimeout ?? TimeSpan.FromSeconds(15))!;

    /// <summary>Reads a single byte, returning 0 when the peer closed (gracefully or by reset) instead.</summary>
    private static async Task<int> ReadOneByteAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            return await client.GetStream().ReadAsync(new byte[1], timeout.Token);
        }
        catch (IOException)
        {
            return 0; // Connection reset — the same message as a graceful close for this assertion.
        }
    }

    /// <summary>Writes a payload into the forwarder and reads back what the fake server echoed.</summary>
    private static async Task<byte[]> RoundTripAsync(int forwarderPort, byte seed)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forwarderPort);
        var stream = client.GetStream();

        var payload = BuildPayload(seed);
        await stream.WriteAsync(payload);

        var received = new byte[payload.Length];
        await stream.ReadExactlyAsync(received);
        return received;
    }

    private static byte[] BuildPayload(byte seed)
    {
        var payload = new byte[PayloadSize];
        // Content that differs per client, so a crossed connection cannot pass the assertion.
        Array.Fill(payload, (byte)(seed + 1));
        return payload;
    }

    /// <summary>
    /// Minimal SOCKS5 server: validates the handshake the forwarder sends, then echoes everything
    /// back. Stands in for tailscaled's proxy, which a unit test must never need.
    /// </summary>
    private sealed class FakeSocks5Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte _replyAddressType;
        private readonly byte _replyCode;
        private readonly byte _greetingMethod;
        private readonly bool _silent;
        private readonly bool _closeAfterReply;
        private readonly TaskCompletionSource _connectionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectCount;

        /// <param name="replyAddressType">Address type of the bound address in the CONNECT reply (ATYP).</param>
        /// <param name="replyCode">Reply code (REP); anything but 0 means the proxy refused.</param>
        /// <param name="silent">When true the server accepts the connection and never answers a single byte.</param>
        /// <param name="greetingMethod">Authentication method the server offers back; anything but 0 means "authenticate first".</param>
        /// <param name="closeAfterReply">When true the server hangs up right after a successful CONNECT reply.</param>
        public FakeSocks5Server(
            byte replyAddressType = AddrTypeIPv4,
            byte replyCode = ReplySucceeded,
            bool silent = false,
            byte greetingMethod = NoAuth,
            bool closeAfterReply = false)
        {
            _replyAddressType = replyAddressType;
            _replyCode = replyCode;
            _silent = silent;
            _greetingMethod = greetingMethod;
            _closeAfterReply = closeAfterReply;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>How many CONNECT requests were accepted — one per forwarded connection.</summary>
        public int ConnectCount => Volatile.Read(ref _connectCount);

        /// <summary>Completes once a served connection ends — i.e. once the forwarder released the upstream socket.</summary>
        public Task ConnectionClosed => _connectionClosed.Task;

        public void Dispose() => _listener.Dispose();

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException)
                {
                    return; // Test finished.
                }

                _ = ServeAsync(client);
            }
        }

        /// <summary>
        /// VER REP RSV ATYP + bound address + port. The bound address is all zeros — a real proxy
        /// has nothing meaningful to report here — but its *length* follows ATYP, which is exactly
        /// what the forwarder has to size the read by.
        /// </summary>
        private byte[] BuildReply()
        {
            // A domain bound address is length-prefixed; the fixed-size forms are not.
            var domain = _replyAddressType == AddrTypeDomain ? Encoding.ASCII.GetBytes(BoundDomainName) : [];
            var addressLength = _replyAddressType switch
            {
                AddrTypeIPv6 => 16,
                AddrTypeDomain => 1 + domain.Length,
                _ => 4
            };

            var reply = new byte[4 + addressLength + 2];
            reply[0] = 0x05;
            reply[1] = _replyCode;
            reply[2] = 0x00;
            reply[3] = _replyAddressType;
            if (_replyAddressType == AddrTypeDomain)
            {
                reply[4] = (byte)domain.Length;
                domain.CopyTo(reply, 5);
            }

            return reply;
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                await ConverseAsync(client);
            }
            finally
            {
                _connectionClosed.TrySetResult();
            }
        }

        private async Task ConverseAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();

                if (_silent)
                {
                    // Swallow whatever arrives and answer nothing — never close either, otherwise
                    // the forwarder would give up on the EOF instead of on its own deadline.
                    var sink = new byte[256];
                    while (await stream.ReadAsync(sink) > 0)
                    {
                    }

                    return;
                }

                // Greeting must be exactly "version 5, one method, no authentication".
                var greeting = new byte[3];
                await stream.ReadExactlyAsync(greeting);
                greeting.ShouldBe(new byte[] { 0x05, 0x01, 0x00 });
                await stream.WriteAsync(new byte[] { 0x05, _greetingMethod });

                if (_greetingMethod != NoAuth)
                {
                    return; // A proxy with no acceptable method hangs up instead of reading a request.
                }

                // CONNECT: VER CMD RSV ATYP + IPv4 + port.
                var request = new byte[10];
                await stream.ReadExactlyAsync(request);
                request[0].ShouldBe((byte)0x05);
                request[1].ShouldBe((byte)0x01); // CONNECT
                request[3].ShouldBe((byte)0x01); // IPv4 — MagicDNS names must never be sent
                new IPAddress(request.AsSpan(4, 4).ToArray()).ShouldBe(TargetAddress);
                BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)).ShouldBe((ushort)TargetPort);

                Interlocked.Increment(ref _connectCount);

                await stream.WriteAsync(BuildReply());

                if (_closeAfterReply)
                {
                    return; // Tunnel dies right after the handshake — the client must notice.
                }

                try
                {
                    await stream.CopyToAsync(stream);
                }
                catch (Exception)
                {
                    // Client hung up — normal at the end of the test.
                }
            }
        }
    }
}
