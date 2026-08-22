namespace Fakvio.Application.Exceptions;

/// <summary>
/// Thrown when a chat conversation cannot be loaded for the current user — either the ID
/// does not exist at all, or it belongs to a different user. Both cases are deliberately
/// reported as one thing so the API never confirms that someone else's ID exists.
///
/// Why a dedicated type: the conversation lookup is a *client* mistake (stale ID from
/// another browser tab, hand-crafted request), not a server failure. Without its own type
/// it is indistinguishable from an infrastructure failure and ends up as HTTP 500 — which
/// is what monitoring alerts on. The controller catches this type and answers 404 with
/// its own text; <c>ex.Message</c> is never sent to the client (issue #156).
///
/// Junior note: this follows the same pattern as <see cref="VatPayerRequiredException"/> —
/// domain exception in Fakvio.Application, typed catch in the controller, status code and
/// user-facing text decided there.
/// </summary>
public sealed class ChatConversationNotFoundException : Exception
{
    /// <summary>
    /// ID of the conversation that could not be found. Kept as structured data so the
    /// controller can log it without having to parse the message.
    /// </summary>
    public long ConversationId { get; }

    /// <summary>
    /// Initializes a new instance for the given conversation ID.
    /// The message is written for the server log, not for the client.
    /// </summary>
    public ChatConversationNotFoundException(long conversationId)
        : base($"Conversation {conversationId} not found or does not belong to the current user.")
    {
        ConversationId = conversationId;
    }
}
