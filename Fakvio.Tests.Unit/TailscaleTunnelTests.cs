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

        var (tailscalePath, daemon, alreadyRunning) = await TailscaleTunnel.EnsureDaemonAsync(
            socksPort,
            Substitute.For<IHostApplicationLifetime>(),
            NullLogger.Instance,
            // Copying is what used to throw 'Text file busy' here — it must not even be attempted.
            prepareBinaries: () => throw new IOException("Text file busy : '/tmp/tsbin/tailscaled'"));

        alreadyRunning.ShouldBeTrue();
        // Nothing to supervise: the daemon belongs to the worker that started it.
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
