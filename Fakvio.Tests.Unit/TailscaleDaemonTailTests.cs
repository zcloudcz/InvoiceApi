using Fakvio.Infrastructure.Tailscale;
using Shouldly;
using Xunit;

namespace Fakvio.Tests.Unit;

/// <summary>
/// The daemon tail is the only clue Azure gets about why 'tailscale up' hung (the daemon's stderr
/// never reaches App Insights otherwise). Pins the two things that matter: it is bounded, and the
/// auth key never leaks through it.
/// </summary>
public class TailscaleDaemonTailTests
{
    [Fact]
    public void KeepsOnlyTheLastLinesAndRedactsTheKey()
    {
        for (var i = 1; i <= 60; i++)
        {
            TailscaleTunnel.RecordDaemonLine($"line {i} tskey-auth-SECRET123");
        }

        var tail = TailscaleTunnel.DaemonTailText("tskey-auth-SECRET123");

        tail.ShouldNotContain("SECRET123");
        tail.ShouldContain("line 60 <redacted>");
        tail.ShouldContain("line 21 <redacted>");   // 60 - 40 + 1 = oldest survivor
        tail.ShouldNotContain("line 20 ");
    }
}
