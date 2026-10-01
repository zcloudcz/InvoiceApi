using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Fakvio.UI.Shared.Services;

/// <summary>Uses the application's culture/resources for built-in grid controls and accessible labels.</summary>
public sealed class FakvioMudLocalizer(IStringLocalizer<SharedResource> resources) : MudLocalizer
{
    public override LocalizedString this[string key] => resources[key];
    public override LocalizedString this[string key, params object[] arguments] => resources[key, arguments];
}
