using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fakvio.UI.Shared.Components.Shared;

/// <summary>
/// Drop-in replacement for Blazor's built-in <see cref="ErrorBoundary"/> that also forwards
/// caught exceptions to the server log via <see cref="IClientLogger"/>. Without this, an
/// unhandled component error would only appear in the browser console.
///
/// Usage: wrap any region whose failures should be logged centrally:
/// <code>
/// &lt;LoggingErrorBoundary&gt;
///     @Body
/// &lt;/LoggingErrorBoundary&gt;
/// </code>
/// </summary>
public class LoggingErrorBoundary : ErrorBoundary
{
    [Inject] private IClientLogger ClientLogger { get; set; } = default!;

    protected override async Task OnErrorAsync(Exception exception)
    {
        // Forward first — even if the base handler does something blocking, we want the log out.
        await ClientLogger.LogErrorAsync(exception, "ErrorBoundary");
        await base.OnErrorAsync(exception);
    }
}
