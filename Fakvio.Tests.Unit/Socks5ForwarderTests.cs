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

    [Fact]
    public async Task ForwardsConcurrentConnections_ThroughSocks5Connect()
    {
        using var socks = new FakeSocks5Server();
        var forwarder = Socks5Forwarder.Start(
            listenPort: 0, // OS-assigned: never collides with a real deployment or a parallel test.
            socksPort: socks.Port,
            target: TargetAddress,
            targetPort: TargetPort,
            logger: NullLogger.Instance);

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
        private int _connectCount;

        public FakeSocks5Server()
        {
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

                // Success reply with a bound address of 0.0.0.0:0 (what a real proxy answers when
                // it has nothing meaningful to report).
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });

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
