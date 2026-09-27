using System.Net;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Thrown when the API rejects a request specifically because reCAPTCHA verification
/// failed (see <c>ICaptchaService.VerifyAsync</c> on the server).
///
/// Why this exists (issue RC.2): before this type, a failed CAPTCHA looked exactly like
/// any other 400 to the calling page — "Invalid email or password", "Registration
/// failed", etc. — which is misleading: the user did nothing wrong, an ad blocker or a
/// slow network kept the token from reaching Google. Pages catch this exception
/// specifically to show one localized, actionable message ("CAPTCHA verification
/// failed — reload the page, or disable ad blocking for this site") instead of the
/// generic error for that endpoint.
/// </summary>
public class CaptchaException : Exception
{
    /// <summary>
    /// The exact text the server uses for every CAPTCHA rejection — see
    /// <c>AuthController</c> / <c>UserController</c>, all of which return
    /// <c>BadRequest(new { message = "CAPTCHA verification failed. Please try again." })</c>.
    /// Kept in one place so the client-side check and the server text cannot drift apart
    /// silently (a rename on either side breaks the corresponding unit test instead).
    /// </summary>
    public const string ServerMessageMarker = "CAPTCHA verification failed";

    public CaptchaException() : base("CAPTCHA verification failed.")
    {
    }

    /// <summary>
    /// True when a non-success API response is specifically the CAPTCHA rejection —
    /// a 400 whose body contains <see cref="ServerMessageMarker"/> — as opposed to any
    /// other 400 (wrong password, duplicate email, unknown IČO, ...).
    /// </summary>
    public static bool Matches(HttpStatusCode statusCode, string? errorContent)
        => statusCode == HttpStatusCode.BadRequest
           && !string.IsNullOrEmpty(errorContent)
           && errorContent.Contains(ServerMessageMarker, StringComparison.Ordinal);
}
