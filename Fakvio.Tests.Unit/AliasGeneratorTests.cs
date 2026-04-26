using Fakvio.Infrastructure.Service;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AliasGenerator"/> — the CSPRNG-based generator that
/// produces inbound email aliases.
/// We do not test randomness itself (that's <see cref="System.Security.Cryptography.RandomNumberGenerator"/>'s
/// responsibility), but we verify format, length, character set, and that ten
/// thousand generations yield unique values with overwhelming probability.
/// </summary>
public class AliasGeneratorTests
{
    [Fact]
    public void Generate_StartsWithExpectedPrefix()
    {
        var gen = new AliasGenerator();

        var alias = gen.Generate();

        alias.ShouldStartWith("pay-");
    }

    [Fact]
    public void Generate_HasExactLength()
    {
        var gen = new AliasGenerator();

        var alias = gen.Generate();

        // "pay-" (4) + 10-char body = 14
        alias.Length.ShouldBe(14);
    }

    [Fact]
    public void Generate_UsesOnlySafeCharacters()
    {
        var gen = new AliasGenerator();

        // Must not contain visually ambiguous chars (0, 1, I, L, O, U).
        const string forbidden = "01ilouILOU";

        for (var i = 0; i < 200; i++)
        {
            var alias = gen.Generate();
            foreach (var ch in alias[4..])
            {
                forbidden.Contains(ch).ShouldBeFalse(
                    $"alias '{alias}' contains forbidden character '{ch}'");
            }
        }
    }

    [Fact]
    public void Generate_10kIterations_AllUnique()
    {
        // ~47 bits of entropy — 10k samples should have ~0 collisions (birthday paradox).
        var gen = new AliasGenerator();
        var set = new HashSet<string>();

        for (var i = 0; i < 10_000; i++)
        {
            set.Add(gen.Generate()).ShouldBeTrue();
        }

        set.Count.ShouldBe(10_000);
    }

    [Fact]
    public void Generate_IsLowercase()
    {
        var gen = new AliasGenerator();

        // Emails are case-insensitive; to avoid case drift we only produce lowercase.
        for (var i = 0; i < 100; i++)
        {
            var alias = gen.Generate();
            alias.ShouldBe(alias.ToLowerInvariant());
        }
    }
}
