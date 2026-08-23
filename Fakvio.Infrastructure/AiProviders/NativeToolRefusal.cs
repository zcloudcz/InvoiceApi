namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// Decides whether a failed native tool-calling request means "this model will never accept
/// our tools" (latch native tools off for the rest of the provider's lifetime) or merely
/// "this one call failed" (fall back for this message, retry natively on the next one).
///
/// Why so narrow: the latch is sticky until the process restarts, and the singleton providers
/// are shared by every tenant. Latching on the whole 4xx range would let an unrelated outage
/// permanently downgrade everybody:
/// <list type="bullet">
///   <item>401 / 403 — an expired or rotated API key. The text-based tool path uses the same
///     key and fails during the same outage, so the latch buys nothing, yet the degradation
///     would outlive the fixed key.</item>
///   <item>400 <c>context_length_exceeded</c> — the conversation history is sent whole
///     (<c>ChatService.GetConversationHistoryAsync</c> has no window), so a long enough chat
///     reliably produces it. That says nothing about tool support.</item>
///   <item>408 / 413 and the rest — transient or payload-sized, not a capability statement.</item>
/// </list>
///
/// Junior note: only two answers really mean "no tools here" — 404 (the model does not exist
/// for this key, so no request shape will ever work) and a 400 whose error text names tools or
/// functions, which is how OpenAI and Gemini report a model without function calling.
/// </summary>
internal static class NativeToolRefusal
{
    /// <summary>
    /// True when the failure is a definitive refusal of native tools.
    /// </summary>
    /// <param name="status">HTTP status code of the failed call.</param>
    /// <param name="responseBody">Raw error body, when the provider gave us one.</param>
    public static bool IsPermanent(int status, string? responseBody)
        => status == 404
           || (status == 400 && MentionsTools(responseBody));

    private static bool MentionsTools(string? body)
        => body is not null
           && (body.Contains("tool", StringComparison.OrdinalIgnoreCase)
               || body.Contains("function", StringComparison.OrdinalIgnoreCase));
}
