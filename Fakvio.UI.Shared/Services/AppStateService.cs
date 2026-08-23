namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Lightweight shared state service for cross-component notifications.
/// Registered as Scoped (one instance per WASM app lifecycle).
///
/// Use case: CompanyDetail creates a new company → fires OnCompanyListChanged
/// → MainLayout subscribes and refreshes the impersonation dropdown.
/// This replaces forceLoad: true navigation which restarts the entire WASM app
/// and can cause race conditions with localStorage auth token loading.
/// </summary>
public class AppStateService
{
    /// <summary>
    /// Fired when the company list changes (create, delete, rename).
    /// MainLayout subscribes to this to refresh the impersonation dropdown.
    /// </summary>
    public event Action? OnCompanyListChanged;

    /// <summary>
    /// Notifies all subscribers that the company list has changed.
    /// Call this after creating, deleting, or renaming a company.
    /// </summary>
    public void NotifyCompanyListChanged() => OnCompanyListChanged?.Invoke();

    /// <summary>
    /// Fired when something outside MainLayout wants the AI chat drawer opened or closed
    /// (currently the "AI asistent" entry in the navigation menu). MainLayout owns the
    /// drawer state, so it subscribes here instead of exposing the flag globally.
    /// </summary>
    public event Action? OnChatToggleRequested;

    /// <summary>
    /// Asks MainLayout to toggle the AI chat drawer.
    /// </summary>
    public void NotifyChatToggleRequested() => OnChatToggleRequested?.Invoke();
}
