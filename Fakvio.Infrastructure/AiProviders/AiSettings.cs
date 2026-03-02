namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// Configuration model for AI provider settings.
/// Bound from appsettings.json "AiSettings" section.
///
/// Only providers with non-empty API keys (or reachable base URLs for Ollama)
/// will be registered as available in the IAiProviderFactory.
/// </summary>
public class AiSettings
{
    /// <summary>
    /// Name of the default provider to use when the user doesn't specify one.
    /// Must match one of the configured provider names (e.g., "Claude", "OpenAI").
    /// </summary>
    public string DefaultProvider { get; set; } = "Claude";

    /// <summary>
    /// Claude (Anthropic) API settings.
    /// </summary>
    public ProviderSettings Claude { get; set; } = new();

    /// <summary>
    /// OpenAI (GPT) API settings.
    /// </summary>
    public ProviderSettings OpenAI { get; set; } = new();

    /// <summary>
    /// Google Gemini API settings.
    /// </summary>
    public ProviderSettings Gemini { get; set; } = new();

    /// <summary>
    /// Ollama (local LLM) settings.
    /// Uses BaseUrl instead of ApiKey (no authentication needed for local Ollama).
    /// </summary>
    public OllamaSettings Ollama { get; set; } = new();
}

/// <summary>
/// Settings for a cloud-based AI provider (Claude, OpenAI, Gemini).
/// </summary>
public class ProviderSettings
{
    /// <summary>
    /// API key for authentication.
    /// Empty string means the provider is not configured and won't be available.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Model identifier to use (e.g., "claude-sonnet-4-20250514", "gpt-4o", "gemini-2.0-flash").
    /// </summary>
    public string Model { get; set; } = string.Empty;
}

/// <summary>
/// Settings for Ollama (local LLM server).
/// Ollama doesn't require an API key — it runs locally.
/// </summary>
public class OllamaSettings
{
    /// <summary>
    /// Base URL of the Ollama server (e.g., "http://localhost:11434").
    /// Empty string means Ollama is not configured.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Model to use (e.g., "llama3.2", "mistral", "codellama").
    /// </summary>
    public string Model { get; set; } = "llama3.2";
}
