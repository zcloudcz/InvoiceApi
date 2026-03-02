using Fakvio.Application.Service;
using Fakvio.Infrastructure.AiProviders;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for AiProviderFactory.
/// Tests provider resolution by name, default provider selection, and error handling.
/// </summary>
public class AiProviderFactoryTests
{
    /// <summary>
    /// Creates a factory with the given providers and default provider name.
    /// </summary>
    private static AiProviderFactory CreateFactory(
        List<IAiProvider> providers,
        string defaultProvider = "Claude")
    {
        var settings = Options.Create(new AiSettings { DefaultProvider = defaultProvider });
        return new AiProviderFactory(providers, settings);
    }

    /// <summary>
    /// Creates a mock IAiProvider with the given name.
    /// </summary>
    private static IAiProvider MockProvider(string name)
    {
        var provider = Substitute.For<IAiProvider>();
        provider.ProviderName.Returns(name);
        return provider;
    }

    [Fact]
    public void GetProvider_ReturnsCorrectProvider_ByName()
    {
        // Arrange
        var claude = MockProvider("Claude");
        var openai = MockProvider("OpenAI");
        var factory = CreateFactory(new List<IAiProvider> { claude, openai });

        // Act
        var result = factory.GetProvider("Claude");

        // Assert
        result.ShouldBe(claude);
    }

    [Fact]
    public void GetProvider_IsCaseInsensitive()
    {
        // Arrange
        var claude = MockProvider("Claude");
        var factory = CreateFactory(new List<IAiProvider> { claude });

        // Act
        var result = factory.GetProvider("claude");

        // Assert
        result.ShouldBe(claude);
    }

    [Fact]
    public void GetProvider_ThrowsForUnknownProvider()
    {
        // Arrange
        var claude = MockProvider("Claude");
        var factory = CreateFactory(new List<IAiProvider> { claude });

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => factory.GetProvider("NonExistent"))
            .Message.ShouldContain("not configured");
    }

    [Fact]
    public void GetDefaultProvider_ReturnsConfiguredDefault()
    {
        // Arrange
        var claude = MockProvider("Claude");
        var openai = MockProvider("OpenAI");
        var factory = CreateFactory(new List<IAiProvider> { claude, openai }, defaultProvider: "OpenAI");

        // Act
        var result = factory.GetDefaultProvider();

        // Assert
        result.ShouldBe(openai);
    }

    [Fact]
    public void GetDefaultProvider_FallsBackToFirst_WhenDefaultNotConfigured()
    {
        // Arrange — default is "Gemini" but only Claude is registered.
        var claude = MockProvider("Claude");
        var factory = CreateFactory(new List<IAiProvider> { claude }, defaultProvider: "Gemini");

        // Act
        var result = factory.GetDefaultProvider();

        // Assert — falls back to Claude (first registered).
        result.ShouldBe(claude);
    }

    [Fact]
    public void GetDefaultProvider_ThrowsWhenNoProviders()
    {
        // Arrange
        var factory = CreateFactory(new List<IAiProvider>());

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => factory.GetDefaultProvider())
            .Message.ShouldContain("No AI providers");
    }

    [Fact]
    public void AvailableProviders_ListsAllRegistered()
    {
        // Arrange
        var claude = MockProvider("Claude");
        var openai = MockProvider("OpenAI");
        var factory = CreateFactory(new List<IAiProvider> { claude, openai });

        // Act
        var available = factory.AvailableProviders;

        // Assert
        available.Count.ShouldBe(2);
        available.ShouldContain("Claude");
        available.ShouldContain("OpenAI");
    }
}
