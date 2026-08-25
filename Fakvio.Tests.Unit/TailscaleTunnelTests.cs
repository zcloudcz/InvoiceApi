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
// ============================================================================

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

    [Fact]
    public async Task RejectsNonIPv4Target()
    {
        // The SOCKS5 CONNECT this code sends carries a 4-byte address, so an IPv6 target cannot
        // work. It has to be refused here, at the boundary: further in it would only show up once
        // per connection, as a debug log, behind a misleading "target not reachable" warning.
        var original = Environment.GetEnvironmentVariable(TailscaleTunnel.TargetHostEnv);
        Environment.SetEnvironmentVariable(TailscaleTunnel.TargetHostEnv, "fd7a:115c:a1e0::1");
        try
        {
            var exception = await Should.ThrowAsync<InvalidOperationException>(
                () => TailscaleTunnel.StartIfConfiguredAsync(
                    "tskey-x", Substitute.For<IHostApplicationLifetime>(), NullLogger.Instance));

            exception.Message.ShouldContain("IPv4");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TailscaleTunnel.TargetHostEnv, original);
        }
    }

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
}
