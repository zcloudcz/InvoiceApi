using System.Net;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.Extensions.Localization;
using MudBlazor;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="UiErrorHandler"/> — the central catch-block handler
/// that shows a snackbar and forwards client-origin errors to the server AppLog.
///
/// Key behavior under test:
///   1. Plain exceptions ARE forwarded via IClientLogger (they would otherwise be lost).
///   2. ApiException is NOT forwarded — ApiClientBase already logged it when the HTTP
///      call failed; forwarding again would duplicate every API error in AppLog.
///   3. The snackbar is ALWAYS shown, with either the default localized message
///      or the caller-supplied override.
/// </summary>
public class UiErrorHandlerTests
{
    private readonly IClientLogger _clientLogger = Substitute.For<IClientLogger>();
    private readonly ISnackbar _snackbar = Substitute.For<ISnackbar>();
    private readonly IStringLocalizer<SharedResource> _localizer =
        Substitute.For<IStringLocalizer<SharedResource>>();

    private UiErrorHandler CreateHandler()
    {
        _localizer["Msg_Error"].Returns(new LocalizedString("Msg_Error", "Chyba"));
        _clientLogger.LogErrorAsync(Arg.Any<Exception>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        return new UiErrorHandler(_clientLogger, _snackbar, _localizer);
    }

    [Fact]
    public void Handle_PlainException_ForwardsToServerLog_AndShowsSnackbar()
    {
        var handler = CreateHandler();
        var ex = new InvalidOperationException("boom");

        handler.Handle(ex, "Invoices.Save");

        // Client-origin exception → must reach AppLog with the caller's context as source.
        _clientLogger.Received(1).LogErrorAsync(ex, "Invoices.Save");
        _snackbar.Received(1).Add(
            Arg.Is<string>(m => m.Contains("Chyba") && m.Contains("boom")),
            Severity.Error,
            Arg.Any<Action<SnackbarOptions>>(),
            Arg.Any<string>());
    }

    [Fact]
    public void Handle_ApiException_DoesNotForward_ButShowsSnackbar()
    {
        var handler = CreateHandler();
        var ex = new ApiException(HttpStatusCode.BadRequest, "Invalid invoice number", "/api/invoices");

        handler.Handle(ex, "Invoices.Save");

        // ApiClientBase already forwarded this error when the HTTP call failed —
        // the handler must not create a duplicate AppLog entry.
        _clientLogger.DidNotReceiveWithAnyArgs().LogErrorAsync(default!, default);
        _snackbar.Received(1).Add(
            Arg.Is<string>(m => m.Contains("Invalid invoice number")),
            Severity.Error,
            Arg.Any<Action<SnackbarOptions>>(),
            Arg.Any<string>());
    }

    [Fact]
    public void Handle_CustomUserMessage_OverridesDefaultText()
    {
        var handler = CreateHandler();

        handler.Handle(new Exception("internal detail"), "Import.Parse", "Soubor se nepodařilo načíst.");

        _snackbar.Received(1).Add(
            "Soubor se nepodařilo načíst.",
            Severity.Error,
            Arg.Any<Action<SnackbarOptions>>(),
            Arg.Any<string>());
    }
}
