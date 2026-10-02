using Fakvio.Contracts.Dto.User;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// In-memory cache of the current user's preferences for the UI session.
///
/// Why a separate state service? FakvioGrid needs the default page size on EVERY
/// list page — calling the API from each grid would mean one HTTP request per
/// page navigation. This scoped service loads the preferences once per app
/// session and hands out the cached value synchronously afterwards.
///
/// In Blazor WASM "scoped" effectively means one instance per browser tab,
/// so the cache lives as long as the app. After saving on the preferences page
/// call <see cref="Set"/> so already-cached values update immediately.
/// </summary>
public class UserPreferencesState
{
    private readonly UserPreferencesApiService _api = null!;
    private UserPreferencesDto? _preferences;
    private Task<UserPreferencesDto>? _loadTask;

    public UserPreferencesState(UserPreferencesApiService api)
    {
        _api = api;
    }

    /// <summary>
    /// Test-double constructor — virtual members are overridden by mocking
    /// frameworks, so the API dependency is never touched.
    /// </summary>
    protected UserPreferencesState()
    {
    }

    /// <summary>
    /// Current preferences — defaults until <see cref="EnsureLoadedAsync"/> completes.
    /// </summary>
    public virtual UserPreferencesDto Preferences => _preferences ?? new UserPreferencesDto();

    /// <summary>
    /// Loads preferences from the API on first call; subsequent calls return the cache.
    /// Concurrent first calls (several grids rendering at once) share one HTTP request.
    /// </summary>
    public virtual async Task<UserPreferencesDto> EnsureLoadedAsync()
    {
        if (_preferences is not null)
            return _preferences;

        // Share a single in-flight request between concurrent callers
        _loadTask ??= _api.GetAsync();
        _preferences = await _loadTask;
        return _preferences;
    }

    /// <summary>
    /// Replaces the cached preferences (called after a successful save).
    /// </summary>
    public virtual void Set(UserPreferencesDto preferences)
    {
        _preferences = preferences;
    }

    /// <summary>
    /// True once the dashboard has made its once-per-session decision about redirecting to the
    /// setup wizard, so returning to the dashboard later does not bounce the user again.
    /// </summary>
    public bool SetupWizardRedirectHandled { get; set; }

    /// <summary>
    /// Clears the cache (e.g. on logout) so the next user loads their own values.
    /// </summary>
    public void Reset()
    {
        _preferences = null;
        _loadTask = null;
        SetupWizardRedirectHandled = false;
    }
}
