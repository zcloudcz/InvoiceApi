using Microsoft.JSInterop;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Carries an OAuth consent ticket across the login/2FA detour (ADR 0001,
/// docs/adr/0001-mcp-oauth21.md §4.2 "Consent" step 1).
///
/// Deliberately NOT a general <c>returnUrl</c> mechanism: the ADR is explicit that adding one
/// to <c>Login.razor</c>/<c>AuthCallback.razor</c> would open an open-redirect hole in the UI.
/// This stores exactly one thing — an opaque, server-issued, 10-minute consent ticket — under
/// a fixed key, and only <see cref="OAuthConsent.razor"/> ever writes it and only
/// <c>Login.razor</c>/<c>TwoFactorVerification.razor</c> ever read it back. <c>sessionStorage</c>
/// (not <c>localStorage</c>): the ticket has no reason to outlive the tab, and clears itself if
/// the user abandons the flow.
/// </summary>
public static class OAuthConsentReturnStorage
{
    private const string Key = "fakvio.oauthConsentTicket";

    public static ValueTask StoreAsync(IJSRuntime js, string ticket)
        => js.InvokeVoidAsync("sessionStorage.setItem", Key, ticket);

    /// <summary>Reads and immediately clears the pending ticket, if any — one-shot by design.</summary>
    public static async Task<string?> ConsumeAsync(IJSRuntime js)
    {
        var ticket = await js.InvokeAsync<string?>("sessionStorage.getItem", Key);
        if (!string.IsNullOrEmpty(ticket))
            await js.InvokeVoidAsync("sessionStorage.removeItem", Key);
        return ticket;
    }
}
