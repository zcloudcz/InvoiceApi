using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Stateless helper that resolves an inbound MIME message to an active alias in
/// <see cref="MasterMailboxIndex"/> by walking a predefined header fallback chain.
///
/// Why a fallback chain?
/// A message can travel through catch-all mailboxes, forwarding rules, or
/// multiple MTA relays before it arrives in Fakvio's inbox. The original
/// recipient address is then buried in secondary headers rather than in <c>To:</c>.
/// A single-header lookup (the previous <c>To: → Delivered-To</c> strategy)
/// silently moves real bank notifications to "Unrouted".
///
/// The chain order below is taken from the issue #67 specification.
/// First header that yields an <em>active</em> alias wins. A retired alias
/// does NOT block routing — the resolver skips it and continues to the next
/// candidate.
///
/// This class is designed to be called from both
/// <see cref="ImapPollService"/> (API host) and
/// <c>PaymentMatchingFunctions.RunImapPoll</c> (Azure Functions host)
/// so it must remain completely stateless (no instance fields, no DI scope
/// retained between calls).
/// </summary>
public class InboundAliasRouter
{
    private readonly ILogger<InboundAliasRouter> _logger;

    public InboundAliasRouter(ILogger<InboundAliasRouter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Walks the header fallback chain and returns the first active-alias match,
    /// or <c>null</c> when no candidate matches.
    ///
    /// On <c>null</c> all candidate addresses extracted from the message are
    /// logged at <c>Information</c> level so SysAdmin can diagnose unrouted mail.
    /// </summary>
    /// <param name="message">The fully parsed MIME message to route.</param>
    /// <param name="master">An open (already-scoped) master DbContext.</param>
    /// <param name="inboundDomain">
    /// Optional: when non-null the domain part of every candidate address is
    /// checked against this value (case-insensitive) in addition to the alias
    /// local-part lookup. Pass <c>null</c> to skip the domain check (e.g. in
    /// tests or when <see cref="PaymentMatchingSystemSettings.InboundDomain"/>
    /// is not configured).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="AliasResolution"/> with match details, or <c>null</c> when
    /// no active alias was found in any header.
    /// </returns>
    public async Task<AliasResolution?> ResolveAsync(
        MimeMessage message,
        MasterDbContext master,
        string? inboundDomain,
        CancellationToken ct = default)
    {
        // Collect all candidate (address, headerLabel) pairs in fallback order.
        // Each entry is (rawAddress, headerName) — we resolve aliases lazily.
        var candidates = CollectCandidates(message);

        // TEMP DIAGNOSTIC: dump all raw headers + extracted candidates so SysAdmin
        // can see exactly what the app receives. Remove once routing is stable.
        var allHeaders = string.Join(" | ", message.Headers.Select(h => $"{h.Field}: {h.Value?.Substring(0, Math.Min(h.Value?.Length ?? 0, 200))}"));
        var candidateSummary = string.Join(", ", candidates.Select(c => $"[{c.Header}] {c.Address}"));
        _logger.LogWarning(
            "IMAP DIAG — MessageId={MessageId} Subject={Subject} From={From} Date={Date} " +
            "Candidates=({Candidates}) AllHeaders=({AllHeaders})",
            message.MessageId ?? "(none)",
            message.Subject ?? "(none)",
            message.From?.ToString() ?? "(none)",
            message.Date,
            candidateSummary,
            allHeaders);

        foreach (var (rawAddress, headerLabel) in candidates)
        {
            var alias = ExtractLocalPart(rawAddress);
            if (string.IsNullOrEmpty(alias))
                continue;

            // Optional domain check: if the caller supplied an InboundDomain,
            // skip addresses whose domain part does not match it.
            if (!string.IsNullOrEmpty(inboundDomain))
            {
                var domain = ExtractDomain(rawAddress);
                if (!string.IsNullOrEmpty(domain) &&
                    !string.Equals(domain, inboundDomain, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "IMAP DIAG — skipping candidate [{Header}] {Address}: domain '{Domain}' != inboundDomain '{InboundDomain}'",
                        headerLabel, rawAddress, domain, inboundDomain);
                    continue;
                }
            }

            // Query master DB for a matching, non-retired alias.
            var entry = await master.MasterMailboxIndex
                .AsNoTracking()
                .FirstOrDefaultAsync(i =>
                    i.InboundAlias == alias && !i.IsAliasRetired, ct);

            // TEMP DIAGNOSTIC: log DB lookup result
            var allIndexRows = await master.MasterMailboxIndex
                .AsNoTracking()
                .Where(i => i.InboundAlias == alias)
                .Select(i => new { i.InboundAlias, i.TenantSchema, i.IsAliasRetired, i.TenantBankAccountMailboxId })
                .ToListAsync(ct);
            _logger.LogWarning(
                "IMAP DIAG — alias='{Alias}' from [{Header}] {Address}: activeMatch={HasMatch}, " +
                "dbRows={DbRows}",
                alias, headerLabel, rawAddress, entry is not null,
                string.Join("; ", allIndexRows.Select(r => $"alias={r.InboundAlias} schema={r.TenantSchema} retired={r.IsAliasRetired} mbxId={r.TenantBankAccountMailboxId}")));

            if (entry is not null)
            {
                // Found an active alias — return immediately (first-active-match-wins).
                _logger.LogInformation(
                    "InboundAliasRouter: resolved alias '{Alias}' via header '{Header}'",
                    alias, headerLabel);

                return new AliasResolution(alias, headerLabel, entry);
            }

            // Check whether the alias exists but is retired — log at debug so the
            // chain continuation is visible without polluting normal logs.
            var retiredEntry = await master.MasterMailboxIndex
                .AsNoTracking()
                .FirstOrDefaultAsync(i =>
                    i.InboundAlias == alias && i.IsAliasRetired, ct);

            if (retiredEntry is not null)
            {
                _logger.LogDebug(
                    "InboundAliasRouter: alias '{Alias}' from header '{Header}' is retired — continuing chain",
                    alias, headerLabel);
            }
        }

        // No active alias found in any header. Log all candidates so SysAdmin
        // can diagnose why the message landed in "Unrouted".
        var allAddresses = candidates
            .Select(c => $"{c.Header}:{c.Address}")
            .ToList();

        _logger.LogInformation(
            "InboundAliasRouter: no active alias matched for message '{MessageId}'. " +
            "Candidates checked: {Candidates}",
            message.MessageId ?? "(no message-id)",
            string.Join(", ", allAddresses));

        return null;
    }

    // ─── Candidate extraction ─────────────────────────────────────────────────

    /// <summary>
    /// Collects all candidate (address, headerLabel) tuples in the specified
    /// precedence order.
    ///
    /// Order (from issue #67 spec):
    ///   1. All <c>Delivered-To</c> header values
    ///   2. <c>X-Original-To</c>
    ///   3. <c>Envelope-To</c> / <c>X-Envelope-To</c>
    ///   4. Last <c>Received:</c> header "for &lt;addr&gt;" value
    ///   5. <c>To:</c> mailboxes
    ///   6. <c>Cc:</c> mailboxes
    /// </summary>
    internal static IReadOnlyList<(string Address, string Header)> CollectCandidates(MimeMessage message)
    {
        var result = new List<(string, string)>();

        // Helper: iterate all occurrences of a named header field.
        // MimeKit's HeaderList does not expose a GetValues() method —
        // instead we filter the Headers collection by field name.
        static IEnumerable<string> GetHeaderValues(HeaderList headers, string field) =>
            headers
                .Where(h => string.Equals(h.Field, field, StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v));

        // 1. Delivered-To (may appear multiple times when the message was relayed)
        foreach (var value in GetHeaderValues(message.Headers, "Delivered-To"))
            result.Add((value.Trim(), "Delivered-To"));

        // 2. X-Original-To (Postfix catch-all adds this with the original RCPT TO)
        foreach (var value in GetHeaderValues(message.Headers, "X-Original-To"))
            result.Add((value.Trim(), "X-Original-To"));

        // 3. Envelope-To / X-Envelope-To (Exim, some sending providers)
        foreach (var value in GetHeaderValues(message.Headers, "Envelope-To"))
            result.Add((value.Trim(), "Envelope-To"));

        foreach (var value in GetHeaderValues(message.Headers, "X-Envelope-To"))
            result.Add((value.Trim(), "X-Envelope-To"));

        // 4. Last Received: header that contains a "for <addr>" clause.
        //    When a message passes through multiple relays each relay prepends
        //    a Received: header, so the *last* (bottom-most) one reflects the
        //    final inbound delivery hop — which is the one most likely to carry
        //    the actual RCPT TO address.
        var receivedFor = ExtractLastReceivedFor(message);
        if (receivedFor is not null)
            result.Add((receivedFor, "Received-for"));

        // 5. To: mailboxes (all of them, not just the first)
        foreach (var mailbox in message.To.Mailboxes)
        {
            if (!string.IsNullOrWhiteSpace(mailbox.Address))
                result.Add((mailbox.Address, "To"));
        }

        // 6. Cc: mailboxes
        foreach (var mailbox in message.Cc.Mailboxes)
        {
            if (!string.IsNullOrWhiteSpace(mailbox.Address))
                result.Add((mailbox.Address, "Cc"));
        }

        return result;
    }

    /// <summary>
    /// Extracts the address from the last <c>Received:</c> header that contains
    /// a "for" keyword followed by a plain address or an angle-bracket-enclosed
    /// address.
    ///
    /// Example: <c>Received: from smtp.bank.cz ... for pay-xyz@fakvio.cz;</c>
    /// or:      <c>Received: from smtp.bank.cz ... for &lt;pay-xyz@fakvio.cz&gt;;</c>
    ///
    /// Returns <c>null</c> when no matching <c>Received:</c> header is found.
    /// </summary>
    internal static string? ExtractLastReceivedFor(MimeMessage message)
    {
        // Headers are stored in order received (first added = first in list).
        // MTA prepends Received: headers, so the topmost header in the raw message
        // is the most recent hop. We want the last (oldest / innermost) one that
        // contains "for" — that is the original inbound delivery path.
        string? lastMatch = null;

        foreach (var header in message.Headers)
        {
            if (!string.Equals(header.Field, "Received", StringComparison.OrdinalIgnoreCase))
                continue;

            var extracted = ParseReceivedFor(header.Value);
            if (extracted is not null)
                lastMatch = extracted;
        }

        return lastMatch;
    }

    /// <summary>
    /// Parses a single <c>Received:</c> header value and returns the address
    /// following the "for" keyword, or <c>null</c> if not present.
    ///
    /// Handles both bare addresses and angle-bracket form:
    ///   <c>for pay-x@fakvio.cz;</c>
    ///   <c>for &lt;pay-x@fakvio.cz&gt;;</c>
    /// </summary>
    internal static string? ParseReceivedFor(string headerValue)
    {
        // Find "for" keyword (word boundary, case-insensitive).
        var forIdx = FindForKeyword(headerValue);
        if (forIdx < 0)
            return null;

        var rest = headerValue[(forIdx + 3)..].TrimStart();

        if (rest.StartsWith('<'))
        {
            // Angle-bracket form: <addr>
            var closeIdx = rest.IndexOf('>');
            if (closeIdx <= 1)
                return null;

            var addr = rest[1..closeIdx].Trim();
            return addr.Contains('@') ? addr : null;
        }
        else
        {
            // Bare address: terminated by whitespace, semicolon, or end-of-string.
            var endIdx = rest.IndexOfAny([' ', '\t', '\r', '\n', ';']);
            var addr = endIdx < 0 ? rest : rest[..endIdx];
            return addr.Contains('@') ? addr.Trim() : null;
        }
    }

    /// <summary>
    /// Finds the zero-based index of the "for" keyword (case-insensitive) as a
    /// whole word inside a <c>Received:</c> header value. Returns -1 if absent.
    ///
    /// Matching is intentionally loose — we only require that "for" is preceded
    /// by a whitespace character (or start-of-string) to avoid matching "for" as
    /// a substring inside hostnames or comments.
    /// </summary>
    private static int FindForKeyword(string value)
    {
        var i = 0;
        while (i <= value.Length - 3)
        {
            var idx = value.IndexOf("for", i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return -1;

            // "for" must be preceded by whitespace or be at position 0.
            var preceding = idx == 0 || char.IsWhiteSpace(value[idx - 1]);
            // "for" must be followed by whitespace or end-of-string.
            var following = idx + 3 >= value.Length || char.IsWhiteSpace(value[idx + 3]);

            if (preceding && following)
                return idx;

            i = idx + 1;
        }

        return -1;
    }

    // ─── Address parsing helpers ──────────────────────────────────────────────

    /// <summary>
    /// Extracts the local-part (everything before the first <c>@</c>) from an
    /// email address, lower-cased. Returns an empty string on malformed input.
    ///
    /// Public/internal so that <see cref="ImapPollService"/> can reuse this
    /// instead of maintaining a duplicate.
    /// </summary>
    public static string ExtractLocalPart(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var at = address.IndexOf('@');
        return at <= 0 ? string.Empty : address[..at].ToLowerInvariant();
    }

    /// <summary>
    /// Extracts the domain part (everything after the first <c>@</c>) from an
    /// email address, lower-cased. Returns an empty string on malformed input.
    /// </summary>
    private static string ExtractDomain(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var at = address.IndexOf('@');
        return at < 0 || at >= address.Length - 1
            ? string.Empty
            : address[(at + 1)..].ToLowerInvariant();
    }
}
