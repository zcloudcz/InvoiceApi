// ============================================================================
// TailscaleTunnelTests — the two things about the tunnel that can be checked
// without a tailnet, a network or a child process.
//
// 1. No auth key = no tunnel. Local development, the production host and this
//    very test run all lack the key, so any accidental side effect on that path
//    would break every one of them.
// 2. The command-line arguments. They are the whole configuration of the tunnel
//    and a single wrong flag fails only in Azure, minutes into a deploy —
//    pinning them here turns that into a red test.
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
