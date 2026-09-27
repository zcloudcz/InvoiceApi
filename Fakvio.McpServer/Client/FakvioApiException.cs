using System.Net;

namespace Fakvio.McpServer.Client;

/// <summary>
/// Thrown by <see cref="FakvioApiClient"/> for every non-success HTTP response that is not the
/// structured <c>TENANT_NOT_READY</c> shape (that one throws <see cref="TenantNotReadyApiException"/>
/// instead — see #342).
///
/// Junior note (#279 / N2.2): before this type existed, every non-success response became a plain
/// <see cref="HttpRequestException"/> whose <c>Message</c> already carried the raw response body —
/// see the old <c>EnsureSuccessAsync</c>. Tool code then had no safe way to tell "the API rejected
/// this input with a helpful message" from "something crashed and leaked its stack trace". This
/// type keeps the raw body in <see cref="Exception.Message"/> (unchanged — it goes to the server
/// log only, via <c>McpToolError.ToJson</c>) and adds <see cref="SafeMessage"/>: the domain message
/// from the response body's <c>message</c> field, ONLY when the body is JSON (an object with a
/// string <c>message</c>, or a bare JSON string), and ONLY the first 500 characters of it. That is
/// the one thing that is safe to relay to an AI client — <c>McpToolError.ToJson</c> decides how,
/// based on <see cref="StatusCode"/>.
/// </summary>
public sealed class FakvioApiException : HttpRequestException
{
    /// <summary>Maximum length of <see cref="SafeMessage"/> — long enough for a real validation
    /// message, short enough that a body full of unrelated text cannot flood the AI client.</summary>
    private const int MaxSafeMessageLength = 500;

    /// <summary>
    /// The domain message extracted from the response body, or <see langword="null"/> when the
    /// body was not JSON, was JSON but had no string <c>message</c>, or was empty. Never the raw
    /// body — that stays in <see cref="Exception.Message"/> for the server log only.
    /// </summary>
    public string? SafeMessage { get; }

    public FakvioApiException(string message, HttpStatusCode statusCode, string? safeMessage)
        : base(message, inner: null, statusCode)
    {
        SafeMessage = safeMessage is { Length: > MaxSafeMessageLength }
            ? safeMessage[..MaxSafeMessageLength]
            : safeMessage;
    }
}
