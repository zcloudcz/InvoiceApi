using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Fakvio.UI.Shared.Services;

/// <summary>One tab's invitation login detour. Only a fixed local route is allowed, never a supplied return URL.</summary>
public static class CompanyInvitationReturnStorage
{
    private const string Key = "fakvio.companyInvitation";
    public static ValueTask StoreAsync(IJSRuntime js, string token) =>
        js.InvokeVoidAsync("sessionStorage.setItem", Key, token);

    public static async Task<bool> TryResumeAsync(IJSRuntime js, NavigationManager navigation)
    {
        var token = await js.InvokeAsync<string?>("sessionStorage.getItem", Key);
        if (string.IsNullOrWhiteSpace(token)) return false;
        await js.InvokeVoidAsync("sessionStorage.removeItem", Key);
        if (token.Length > 200) return false;
        navigation.NavigateTo("/company-invitations/accept#" + Uri.EscapeDataString(token), forceLoad: true);
        return true;
    }
}
