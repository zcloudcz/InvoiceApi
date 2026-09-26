using System.Net;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Thrown when the API answers 429 Too Many Requests — the per-IP "auth-anon" rate
/// limiter (RC.4, <c>Fakvio.API/Program.cs</c>) in front of login, register, ares,
/// forgot-password, set-password, validate-invitation and 2FA verify.
///
/// Why a dedicated exception (same idea as <see cref="CaptchaException"/>): 429 is not
/// "wrong password" or "registration failed" — the request never even reached the
/// business logic. Pages catch this specifically to show one localized message
/// ("Too many attempts, try again later") instead of whatever generic error that
/// endpoint would otherwise show for a 4xx.
/// </summary>
public class RateLimitExceededException : Exception
{
    public RateLimitExceededException() : base("Too many attempts. Please try again later.")
    {
    }

    public static bool Matches(HttpStatusCode statusCode) => statusCode == HttpStatusCode.TooManyRequests;
}
