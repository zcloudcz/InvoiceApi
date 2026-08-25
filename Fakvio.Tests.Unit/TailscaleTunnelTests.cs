// ============================================================================
// TailscaleTunnelTests — what can be checked about the tunnel without a tailnet,
// a network or a child process.
//
// 1. No auth key = no tunnel. Local development, the production host and this
//    very test run all lack the key, so any accidental side effect on that path
//    would break every one of them.
// 2. The command-line arguments. They are the whole configuration of the tunnel
//    and a single wrong flag fails only in Azure, minutes into a deploy —
//    pinning them here turns that into a red test.
// 3. Redaction of the auth key from the CLI output, which is the one string that
//    travels from the child process straight into a log line.
// 4. Rejection of an unusable target: host and port come from App Settings, and
//    both are validated before any binary is touched, so these cases stay
//    process-free too.
// 5. The order of the bring-up and the tolerance of the binary copy — the two
//    halves of issue #321, where a second worker process on the same instance
//    overwrote a binary the first one was running and got 'Text file busy'.
//    Both tolerances are races, so both are driven through a seam: the copy that
//    fails only after a sibling worker slipped its own copy in, and the bring-up
//    that fails while the winner's daemon is still binding the SOCKS port.
// ============================================================================

using System.Net;
using System.Net.Sockets;
using Fakvio.Functions.Tailscale;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class TailscaleTunnelTests
{
    [Fact]
    public async Task SkipsWhenAuthKeyAbsent()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();

        var started = await TailscaleTunnel.StartIfConfiguredAsync(null, lifetime, NullLogger.Instance);

        started.ShouldBeFalse();
        // Nothing was registered for shutdown, i.e. no daemon was launched.
        _ = lifetime.DidNotReceive().ApplicationStopping;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SkipsWhenAuthKeyIsBlank(string authKey)
    {
        var started = await TailscaleTunnel.StartIfConfiguredAsync(
            authKey, Substitute.For<IHostApplicationLifetime>(), NullLogger.Instance);

        started.ShouldBeFalse();
    }

    [Theory]
    // IPv6 literal: its 16 address bytes do not fit the 4-byte address field of the CONNECT request.
    [InlineData("fd7a:115c:a1e0::1")]
    // MagicDNS name: userspace mode does not wire the daemon's resolver into this process, so a name
    // can never be resolved here — and it is the mistake an operator is most likely to make.
    [InlineData("fakvio-db-server")]
    // Host and port pasted into one setting instead of two.
    [InlineData("100.69.241.17:5544")]
    public async Task RejectsTargetHostThatIsNotAnIPv4Literal(string host)
    {
        // It has to be refused here, at the boundary: further in it would only show up once per
        // connection, as a debug log, behind a misleading "target not reachable" warning.
        using var _ = new EnvironmentVariableScope(TailscaleTunnel.TargetHostEnv, host);

        var exception = await StartTunnelAndCaptureFailureAsync();

        exception.Message.ShouldContain("IPv4");
        exception.Message.ShouldContain(TailscaleTunnel.TargetHostEnv);
        // The rejected value belongs in the message — an operator reading the Azure log has no
        // other way to see what the App Setting actually contained.
        exception.Message.ShouldContain(host);
    }

    [Theory]
    [InlineData("abc")]     // Not a number at all.
    [InlineData("0")]       // Port 0 means "any free port", which is meaningless for a target.
    [InlineData("70000")]   // Above the 16-bit range.
    [InlineData("-1")]
    public async Task RejectsTargetPortOutsideTheValidRange(string port)
    {
        // An unusable port must fail with a message naming the setting. Without the range check the
        // value would reach IPEndPoint, whose ArgumentOutOfRangeException names 'port', not the
        // App Setting the operator has to fix.
        using var _ = new EnvironmentVariableScope(TailscaleTunnel.TargetPortEnv, port);

        var exception = await StartTunnelAndCaptureFailureAsync();

        exception.Message.ShouldContain(TailscaleTunnel.TargetPortEnv);
        exception.Message.ShouldContain(port);
    }

    /// <summary>
    /// Starts the tunnel with a dummy key and returns the configuration failure it throws. Safe to
    /// call: target validation runs before any binary is touched or any process is started, so this
    /// never leaves the test process.
    /// </summary>
    private static Task<InvalidOperationException> StartTunnelAndCaptureFailureAsync() =>
        Should.ThrowAsync<InvalidOperationException>(
            () => TailscaleTunnel.StartIfConfiguredAsync(
                "tskey-x", Substitute.For<IHostApplicationLifetime>(), NullLogger.Instance));

    [Fact]
    public void BuildsExpectedDaemonArguments()
    {
        // --tun=userspace-networking: the Functions sandbox cannot create a TUN device.
        // --socket: the default /var/run path is not writable there.
        // --state=mem:: no state on disk, which is what an ephemeral auth key expects.
        TailscaleTunnel.TailscaledArguments.ShouldBe(
            "--tun=userspace-networking --socks5-server=localhost:1055 --socket=/tmp/tailscaled.sock --state=mem:");
    }

    [Fact]
    public void BuildsExpectedUpArguments()
    {
        // --socket must repeat on the CLI call, otherwise it talks to the default path and hangs.
        TailscaleTunnel.UpArguments("tskey-x").ShouldBe(
            "--socket=/tmp/tailscaled.sock up --authkey=tskey-x --hostname=fakvio-func --accept-dns=false --timeout=30s");
    }

    [Theory]
    // The shapes 'tailscale up' actually produces when it complains: the key quoted back inside a
    // message, twice in one blob (stdout + stderr are merged), and as the bare argument echo.
    [InlineData("backend error: invalid key: tskey-auth-secret123")]
    [InlineData("invalid key tskey-auth-secret123\nup: failed with tskey-auth-secret123")]
    [InlineData("tskey-auth-secret123")]
    public void RedactsAuthKeyFromProcessOutput(string output)
    {
        // Whatever the CLI prints ends up in a log line ('up failed on attempt … {Output}'), so the
        // key must be gone before the string leaves RunToCompletionAsync. App Insights keeps logs
        // for 90 days; a key that lands there is a key that has to be rotated.
        const string authKey = "tskey-auth-secret123";

        var redacted = TailscaleTunnel.Redact(output, authKey);

        redacted.ShouldNotContain(authKey);
        redacted.ShouldContain("<redacted>");
    }

    [Fact]
    public void RedactLeavesOutputAloneWhenNoKeyIsConfigured()
    {
        // The disabled-tunnel path passes an empty secret; replacing "" would otherwise splice the
        // marker between every character of the output.
        TailscaleTunnel.Redact("nothing secret here", string.Empty).ShouldBe("nothing secret here");
    }

    [Fact]
    public void PortsMatchTheDocumentedConnectionString()
    {
        // ADMINGUIDE §14 and SELFHOST-DB §7 tell the operator to point
        // ConnectionStrings__DefaultConnection at 127.0.0.1:15432 — keep the code and the runbooks
        // from drifting apart.
        TailscaleTunnel.ListenPort.ShouldBe(15432);
        TailscaleTunnel.SocksPort.ShouldBe(1055);
        TailscaleTunnel.AuthKeyEnv.ShouldBe("TAILSCALE_AUTHKEY");
    }

    [Fact]
    public async Task ProbesSocksPortBeforeTouchingAnyBinary()
    {
        // A bare listener stands in for a sibling worker's tailscaled: all this test needs is
        // "something answers on the SOCKS port", which is exactly what the probe checks.
        using var siblingDaemon = new TcpListener(IPAddress.Loopback, 0);
        siblingDaemon.Start();
        var socksPort = ((IPEndPoint)siblingDaemon.LocalEndpoint).Port;

        var (tailscalePath, daemon) = await TailscaleTunnel.EnsureDaemonAsync(
            socksPort,
            Substitute.For<IHostApplicationLifetime>(),
            NullLogger.Instance,
            // Copying is what used to throw 'Text file busy' here — it must not even be attempted.
            prepareBinaries: () => throw new IOException("Text file busy : '/tmp/tsbin/tailscaled'"));

        // No daemon to supervise means the sibling worker's one was reused: nothing was copied and
        // nothing was spawned, which is the whole point of probing first.
        daemon.ShouldBeNull();
        // The CLI still has to be addressable — the sibling worker left it in the shared directory.
        tailscalePath.ShouldContain("tsbin");
        tailscalePath.ShouldEndWith("tailscale");
    }

    [Fact]
    public void CopyExecutableKeepsAnIdenticallySizedFileThatIsAlreadyThere()
    {
        using var directories = new TempDirectoryScope();
        var running = new byte[] { 9, 9, 9, 9 };
        File.WriteAllBytes(Path.Combine(directories.Source, "tailscaled"), new byte[] { 1, 2, 3, 4 });
        var destination = Path.Combine(directories.Destination, "tailscaled");
        File.WriteAllBytes(destination, running);

        var result = TailscaleTunnel.CopyExecutable(
            directories.Source, directories.Destination, "tailscaled", NullLogger.Instance);

        result.ShouldBe(destination);
        // Same length = the copy another worker already made from the same package. Overwriting it
        // is what a running daemon answers with ETXTBSY, so the bytes must be left untouched.
        File.ReadAllBytes(destination).ShouldBe(running);
    }

    [Fact]
    public void CopyExecutableRefreshesAFileOfADifferentLength()
    {
        // The counterpart of the test above: skipping is keyed on the length, so a destination left
        // over from an older deploy still gets replaced.
        using var directories = new TempDirectoryScope();
        var deployed = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(Path.Combine(directories.Source, "tailscale"), deployed);
        var destination = Path.Combine(directories.Destination, "tailscale");
        File.WriteAllBytes(destination, new byte[] { 7, 7 });

        TailscaleTunnel.CopyExecutable(directories.Source, directories.Destination, "tailscale", NullLogger.Instance);

        File.ReadAllBytes(destination).ShouldBe(deployed);
    }

    [Fact]
    public void CopyExecutableToleratesADestinationThatAppearsBetweenTheCheckAndTheCopy()
    {
        // The exact interleaving from #321: at the check the destination is not there yet, so the
        // copy is attempted; by the time it runs, the sibling worker has copied the very same binary
        // and is already executing it, and Linux answers the overwrite with ETXTBSY. The snapshot
        // taken before the copy still says "missing", so the tolerance has to look at the file again
        // — otherwise the exception escapes and this worker starts no forwarder at all.
        using var directories = new TempDirectoryScope();
        var deployed = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(Path.Combine(directories.Source, "tailscaled"), deployed);
        var destination = Path.Combine(directories.Destination, "tailscaled");
        var logger = new RecordingLogger<Socks5Forwarder>();

        var result = TailscaleTunnel.CopyExecutable(
            directories.Source, directories.Destination, "tailscaled", logger,
            copyFile: (_, target) =>
            {
                // The sibling worker wins the race exactly here.
                File.WriteAllBytes(target, deployed);
                throw new IOException("Text file busy : '/tmp/tsbin/tailscaled'");
            });

        result.ShouldBe(destination);
        logger.Warnings.ShouldContain(entry => entry.Message.Contains("is in use"));
    }

    [Fact]
    public void CopyExecutableStillFailsWhenTheCopyWasCutShort()
    {
        // Same exception type, different cause: a full disk (or a sibling that died mid-copy) leaves
        // a destination that exists but is shorter than the source. "It exists" is therefore not
        // enough to keep it — handing a truncated binary to Process.Start fails later and far less
        // legibly than failing right here.
        using var directories = new TempDirectoryScope();
        File.WriteAllBytes(Path.Combine(directories.Source, "tailscaled"), new byte[] { 1, 2, 3, 4 });

        Should.Throw<IOException>(() => TailscaleTunnel.CopyExecutable(
            directories.Source, directories.Destination, "tailscaled", NullLogger.Instance,
            copyFile: (_, target) =>
            {
                File.WriteAllBytes(target, new byte[] { 1, 2 });
                throw new IOException("No space left on device");
            }));
    }

    [Fact]
    public async Task StartsTheForwarderWhenTheSocksPortComesUpAfterTheBringUpFailed()
    {
        // The worker that loses the #321 race: its own bring-up dies (a busy binary, a taken daemon
        // socket, or the daemon it spawned exiting) while the winner's daemon is still binding the
        // SOCKS port. One instant re-probe would find nothing and rethrow, so this worker would run
        // without a forwarder even though the tunnel is up half a second later.
        using var siblingDaemon = new DelayedSocksListener(TimeSpan.FromSeconds(2));
        var logger = new RecordingLogger<Socks5Forwarder>();

        var started = await TailscaleTunnel.StartIfConfiguredAsync(
            "tskey-x",
            Substitute.For<IHostApplicationLifetime>(),
            logger,
            siblingDaemon.Port,
            // 0 = let the OS pick a free port, so the test never competes for the real 15432.
            listenPort: 0,
            bringUp: _ => throw new IOException("Text file busy : '/tmp/tsbin/tailscaled'"),
            CancellationToken.None);

        started.ShouldBeTrue();
        // The forwarder is the point of the whole tolerance — the connection string has nothing to
        // talk to without it.
        logger.Entries.ShouldContain(entry => entry.Message.Contains("forwarder 127.0.0.1:"));
        logger.Warnings.ShouldContain(entry => entry.Message.Contains("continuing on another worker's daemon"));
    }

    /// <summary>
    /// Stands in for the sibling worker's tailscaled: the port stays closed for a moment (the window
    /// in which the losing worker's bring-up fails) and then answers the SOCKS5 handshake with
    /// "connected", so the reachability probe finishes on its first attempt instead of waiting out
    /// all ten of them.
    /// </summary>
    private sealed class DelayedSocksListener : IDisposable
    {
        // SOCKS5 (RFC 1928): the client greeting is 3 bytes, a CONNECT to an IPv4 address is 10, and
        // both replies are fixed-size for that address type.
        private static readonly byte[] GreetingReply = [0x05, 0x00];
        private static readonly byte[] ConnectReply = [0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0];

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();

        public DelayedSocksListener(TimeSpan startDelay)
        {
            // The port has to be known before anything listens on it, so it is reserved and released
            // first — the caller passes it in as "the port the daemon will use".
            Port = ReserveFreePort();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _ = ServeAsync(startDelay);
        }

        public int Port { get; }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Dispose();
            _stopping.Dispose();
        }

        private static int ReserveFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync(TimeSpan startDelay)
        {
            try
            {
                await Task.Delay(startDelay, _stopping.Token);
                _listener.Start();
                while (!_stopping.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    _ = AnswerAsync(client);
                }
            }
            catch (Exception)
            {
                // Disposed while waiting or accepting — the test is over, there is nothing to report.
            }
        }

        private static async Task AnswerAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    await stream.ReadExactlyAsync(new byte[3]);
                    await stream.WriteAsync(GreetingReply);
                    await stream.ReadExactlyAsync(new byte[10]);
                    await stream.WriteAsync(ConnectReply);
                    // Stay open until the other side hangs up, like a real proxy would.
                    await stream.ReadAsync(new byte[1]);
                }
                catch (Exception)
                {
                    // The port probe only connects and drops the connection — no handshake to finish.
                }
            }
        }
    }

    /// <summary>Source and destination directory for one copy test, removed afterwards.</summary>
    private sealed class TempDirectoryScope : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("fakvio-tsbin");

        public string Source => Directory.CreateDirectory(Path.Combine(_root.FullName, "package")).FullName;

        public string Destination => Directory.CreateDirectory(Path.Combine(_root.FullName, "runtime")).FullName;

        public void Dispose() => _root.Delete(recursive: true);
    }

    /// <summary>
    /// Sets an environment variable for the duration of one test and puts the original value back
    /// afterwards, so a failed assertion cannot leak configuration into the next test.
    /// </summary>
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
    }
}
