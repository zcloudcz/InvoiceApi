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

    // SOCKS5 reply fields (RFC 1928) the fake server below plays with.
    private const byte AddrTypeIPv4 = 0x01;
    private const byte AddrTypeIPv6 = 0x04;
    private const byte ReplySucceeded = 0x00;

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

    private static Socks5Forwarder StartForwarder(FakeSocks5Server socks) => Socks5Forwarder.Start(
        listenPort: 0, // OS-assigned: never collides with a real deployment or a parallel test.
        socksPort: socks.Port,
        target: TargetAddress,
        targetPort: TargetPort,
        logger: NullLogger.Instance);

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
        private int _connectCount;

        /// <param name="replyAddressType">Address type of the bound address in the CONNECT reply (ATYP).</param>
        /// <param name="replyCode">Reply code (REP); anything but 0 means the proxy refused.</param>
        public FakeSocks5Server(byte replyAddressType = AddrTypeIPv4, byte replyCode = ReplySucceeded)
        {
            _replyAddressType = replyAddressType;
            _replyCode = replyCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>How many CONNECT requests were accepted — one per forwarded connection.</summary>
        public int ConnectCount => Volatile.Read(ref _connectCount);

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
            var addressLength = _replyAddressType == AddrTypeIPv6 ? 16 : 4;
            var reply = new byte[4 + addressLength + 2];
            reply[0] = 0x05;
            reply[1] = _replyCode;
            reply[2] = 0x00;
            reply[3] = _replyAddressType;
            return reply;
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();

                // Greeting must be exactly "version 5, one method, no authentication".
                var greeting = new byte[3];
                await stream.ReadExactlyAsync(greeting);
                greeting.ShouldBe(new byte[] { 0x05, 0x01, 0x00 });
                await stream.WriteAsync(new byte[] { 0x05, 0x00 });

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
