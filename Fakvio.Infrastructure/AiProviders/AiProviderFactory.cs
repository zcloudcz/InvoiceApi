using Fakvio.Application.Service;
using Microsoft.Extensions.Options;

namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// Factory that resolves IAiProvider instances by name.
/// Only providers with valid configuration (API key or base URL) are registered.
///
/// The factory receives all registered IAiProvider instances via DI (IEnumerable)
/// and builds a lookup dictionary keyed by ProviderName (case-insensitive).
/// </summary>
public class AiProviderFactory : IAiProviderFactory
{
    private readonly Dictionary<string, IAiProvider> _providers;
    private readonly string _defaultProviderName;

    public AiProviderFactory(
        IEnumerable<IAiProvider> providers,
        IOptions<AiSettings> settings)
    {
        // Build a case-insensitive lookup from all registered providers.
        _providers = providers.ToDictionary(
            p => p.ProviderName,
            p => p,
            StringComparer.OrdinalIgnoreCase);

        _defaultProviderName = settings.Value.DefaultProvider;
    }

    /// <summary>
    /// Gets a provider by name (case-insensitive).
    /// Throws InvalidOperationException if the provider is not configured.
    /// </summary>
    public IAiProvider GetProvider(string providerName)
    {
        if (_providers.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"AI provider '{providerName}' is not configured. " +
            $"Available providers: {string.Join(", ", _providers.Keys)}");
    }

    /// <summary>
    /// Gets the default provider as configured in AiSettings.DefaultProvider.
    /// Falls back to the first available provider if the default is not configured.
    /// </summary>
    public IAiProvider GetDefaultProvider()
    {
        if (_providers.TryGetValue(_defaultProviderName, out var provider))
        {
            return provider;
        }

        // Fallback: use the first available provider.
        return _providers.Values.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No AI providers are configured. Add at least one API key in AiSettings.");
    }

    /// <summary>
    /// Lists the names of all configured and available providers.
    /// </summary>
    public IReadOnlyList<string> AvailableProviders => _providers.Keys.ToList().AsReadOnly();
}
