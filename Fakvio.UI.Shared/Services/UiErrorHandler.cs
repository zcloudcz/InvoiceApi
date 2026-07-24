using Microsoft.Extensions.Localization;
using MudBlazor;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Central error handler for catch blocks in pages and components.
///
/// Problem it solves: pages catch exceptions and show a snackbar, but the error never
/// reaches the server-side AppLog table — it only lives in the browser console. This
/// handler does both in one call, so a catch block is a single line:
///
///     catch (Exception ex) { ErrorHandler.Handle(ex, "Invoices.Save"); }
///
/// Logging rules:
/// - ApiException is NOT forwarded — ApiClientBase already logged it to AppLog when the
///   HTTP call failed. Forwarding again would create duplicate entries for every API error.
/// - Everything else (JS interop, parsing, local logic) IS forwarded via IClientLogger.
/// - Forwarding is fire-and-forget and never throws (IClientLogger contract), so the
///   snackbar always shows regardless of network state.
/// </summary>
public interface IUiErrorHandler
{
    /// <summary>
    /// Logs the exception to the server (unless it is an already-logged ApiException)
    /// and shows an error snackbar.
    /// </summary>
    /// <param name="ex">The caught exception.</param>
    /// <param name="context">Where it happened, e.g. "Invoices.Save" — becomes AppLog.Source
    /// ("Fakvio.UI.{context}"), so keep it short and greppable.</param>
    /// <param name="userMessage">Optional custom snackbar text. Default: localized
    /// "Error: {ex.Message}".</param>
    void Handle(Exception ex, string context, string? userMessage = null);
}

public class UiErrorHandler : IUiErrorHandler
{
    private readonly IClientLogger _clientLogger;
    private readonly ISnackbar _snackbar;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public UiErrorHandler(
        IClientLogger clientLogger,
        ISnackbar snackbar,
        IStringLocalizer<SharedResource> localizer)
    {
        _clientLogger = clientLogger;
        _snackbar = snackbar;
        _localizer = localizer;
    }

    public void Handle(Exception ex, string context, string? userMessage = null)
    {
        // ApiException was already forwarded to AppLog by ApiClientBase.HandleErrorResponseAsync —
        // logging it again here would duplicate every API error in the log.
        if (ex is not ApiException)
        {
            // Fire-and-forget: never block or break the UI flow because of logging.
            _ = _clientLogger.LogErrorAsync(ex, context);
        }

        _snackbar.Add(userMessage ?? $"{_localizer["Msg_Error"].Value}: {ex.Message}", Severity.Error);
    }
}
