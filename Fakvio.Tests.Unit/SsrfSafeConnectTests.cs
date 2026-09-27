using System.Net;
using Fakvio.Infrastructure.Authentication.OAuth;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the SSRF guard behind the CIMD fetch (ADR 0001, threat T7).
/// See <see cref="OAuthClientResolverTests"/> for the end-to-end resolver behaviour.
/// </summary>
public class SsrfSafeConnectTests
{
    [Theory]
    [InlineData("127.0.0.1")]      // loopback
    [InlineData("::1")]            // loopback (IPv6)
    [InlineData("10.0.0.5")]       // RFC 1918
    [InlineData("172.16.0.1")]     // RFC 1918
    [InlineData("172.31.255.255")] // RFC 1918 upper bound
    [InlineData("192.168.1.1")]    // RFC 1918
    [InlineData("169.254.169.254")] // Azure IMDS / link-local
    [InlineData("168.63.129.16")]  // Azure host metadata
    [InlineData("100.64.0.1")]     // CGNAT
    [InlineData("224.0.0.1")]      // multicast
    [InlineData("0.0.0.0")]        // unspecified
    [InlineData("fe80::1")]        // link-local IPv6
    [InlineData("fc00::1")]        // unique local IPv6
    [InlineData("::ffff:10.0.0.1")] // IPv4-mapped IPv6 of a blocked range
    [InlineData("0.0.0.1")]        // "this network" 0.0.0.0/8
    [InlineData("192.0.0.1")]      // IETF protocol assignments / NAT64-DNS64 192.0.0.0/24
    [InlineData("198.18.0.1")]     // benchmarking 198.18.0.0/15
    [InlineData("198.19.255.254")] // benchmarking 198.18.0.0/15, upper bound
    [InlineData("240.0.0.1")]      // reserved "Class E" 240.0.0.0/4
    [InlineData("255.255.255.255")] // limited broadcast
    [InlineData("fec0::1")]        // deprecated IPv6 site-local fec0::/10
    [InlineData("64:ff9b::1.1.1.1")] // well-known NAT64 prefix 64:ff9b::/96
    public void IsForbidden_BlocksPrivateAndSpecialUseAddresses(string ip)
    {
        SsrfSafeConnect.IsForbidden(IPAddress.Parse(ip)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]  // just outside the RFC 1918 172.16/12 range
    [InlineData("192.169.0.1")] // just outside 192.168/16
    public void IsForbidden_AllowsPublicAddresses(string ip)
    {
        SsrfSafeConnect.IsForbidden(IPAddress.Parse(ip)).ShouldBeFalse();
    }
}
