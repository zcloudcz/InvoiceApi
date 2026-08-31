using Fakvio.Infrastructure.AiProviders;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// The latch predicate on its own (issue #160).
///
/// The provider tests drive it through real HTTP stubs, which is the right level for the
/// wiring. What they cannot reach are the degenerate inputs a provider may hand it — a body
/// that never arrived — and the assumption its 404 rule rests on.
///
/// Junior note: the latch is sticky until the process restarts and the providers are
/// singletons shared by every tenant, so a false positive here downgrades everybody until
/// the next deploy. That asymmetry is why the predicate must stay grudging.
/// </summary>
public class NativeToolRefusalTests
{
    /// <summary>
    /// A 400 with nothing readable in it says nothing about tools, so it must not latch.
    /// Treating an absent body as a match would be the fail-open version of this predicate
    /// and every body-less 400 — a proxy, a gateway, a truncated response — would
    /// permanently downgrade the provider.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPermanent_WhenTheProviderGaveNoUsableBody_TreatsA400AsTransient(string? body)
    {
        NativeToolRefusal.IsPermanent(400, body).ShouldBeFalse();
    }

    /// <summary>
    /// The 404 rule ignores the body on purpose — a missing model is a missing model whatever
    /// the wording — so it must hold with no body at all.
    /// </summary>
    [Fact]
    public void IsPermanent_WhenA404ArrivesWithoutABody_StillLatches()
    {
        NativeToolRefusal.IsPermanent(404, null).ShouldBeTrue();
    }

    /// <summary>
    /// Latching on a bare 404 is only sound while no operator can point a cloud provider at a
    /// different host: with a fixed endpoint a 404 means "no such model", but with a
    /// configurable one it could just as well mean "the base URL has a typo" — and a typo
    /// would silently and permanently downgrade tool calling.
    ///
    /// This test names that assumption. If it ever fails because a base URL was added to the
    /// cloud provider settings, the fix is to revisit the 404 rule in
    /// <see cref="NativeToolRefusal"/>, not to update the expected list here.
    /// (Ollama is deliberately not covered: it has a base URL and no latch.)
    /// </summary>
    [Fact]
    public void CloudProviderSettings_OfferNoConfigurableEndpoint_WhichIsWhatMakesThe404RuleSafe()
    {
        var configurable = typeof(ProviderSettings)
            .GetProperties()
            .Select(property => property.Name);

        configurable.ShouldBe(["ApiKey", "Model"], ignoreOrder: true);
    }
}
