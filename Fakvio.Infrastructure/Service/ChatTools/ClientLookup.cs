using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Identifies the ONE client a chat tool (get / update / delete) is supposed to act on,
/// from the parameters the model sent.
///
/// Why it is shared: the three tools ask the same question ("which client?") and must answer
/// it identically. A per-tool copy is how "update" and "delete" quietly end up picking
/// different clients for the same words.
///
/// Why more than one candidate is an error and not a silent pick: a company name is matched
/// as a substring, so "Alfa" can hit "Alfa s.r.o." and "Alfa Trade a.s.". Showing the wrong
/// detail is annoying; renaming or deleting the wrong company is data loss.
///
/// Junior note: nothing here enforces business rules (who may be deleted, what may be
/// changed). Those live in <see cref="IClientService"/> and must stay there — a copy in a
/// chat tool is a second source of truth that will drift.
/// </summary>
internal static class ClientLookup
{
    /// <summary>
    /// The three ways a user can point at a client, shared by every tool that acts on one.
    ///
    /// All are optional in the schema because "at least one of them" is a rule JSON Schema
    /// cannot express; <see cref="ResolveAsync"/> enforces it and reports it to the model.
    /// </summary>
    public static readonly ChatToolParameter[] IdentityParameters =
    [
        new()
        {
            Name = "id",
            Type = ChatToolParameterType.Integer,
            Description = "Internal database ID of the client (from a list_clients result)"
        },
        new()
        {
            Name = "registration_number",
            Type = ChatToolParameterType.String,
            Description = "Czech company registration number (IČO), exactly 8 digits. Matched exactly."
        },
        new()
        {
            Name = "name",
            Type = ChatToolParameterType.String,
            Description = "Company or trading name (case-insensitive substring). " +
                          "Must match exactly one client."
        }
    ];

    /// <summary>
    /// Resolves the client from <c>id</c>, then <c>registration_number</c>, then <c>name</c>.
    /// Returns either the client or a ready-to-return failure explaining what to do next —
    /// never both, never neither.
    ///
    /// The order is by precision: an ID identifies one row, an IČO identifies one company,
    /// a name is a guess. Whichever the model sends, the most precise one wins.
    /// </summary>
    public static async Task<Resolution> ResolveAsync(
        IClientService service,
        Dictionary<string, string> parameters,
        CancellationToken ct)
    {
        var rawId = Read(parameters, "id");
        var rawRegistrationNumber = Read(parameters, "registration_number");
        var rawName = Read(parameters, "name");

        if (rawId is null && rawRegistrationNumber is null && rawName is null)
        {
            return Resolution.Failed(
                "Provide at least one of: id (database ID), registration_number (IČO), or name.");
        }

        // Path 1 — internal ID. Precise, so it wins whenever several identifiers are supplied.
        if (rawId is not null)
        {
            // The schema declares 'id' as an integer and the executor validates that before the
            // tool runs, so this only guards direct calls (tests, future callers).
            if (!long.TryParse(rawId, out var id))
                return Resolution.Failed("Invalid id parameter — must be a numeric database ID.");

            var byId = await service.GetClientByIdAsync(id, ct);

            return byId is null
                ? Resolution.Failed($"Client with ID {id} not found.")
                : Resolution.Found(byId);
        }

        // Path 2 — IČO. Unique per company, so an exact lookup is enough.
        if (rawRegistrationNumber is not null)
        {
            // Users dictate IČO with spaces ("123 456 78") and paste it out of documents with
            // tabs or non-breaking spaces; the column holds it without any of them.
            var ico = string.Concat(rawRegistrationNumber.Where(character => !char.IsWhiteSpace(character)));
            var byRegistrationNumber = await service.GetClientByRegistrationNumberAsync(ico, ct);

            return byRegistrationNumber is null
                ? Resolution.Failed($"Client with IČO '{ico}' not found.")
                : Resolution.Found(byRegistrationNumber);
        }

        // Path 3 — name, matched case-insensitively as a substring, the way the other chat
        // tools already match company names (a user rarely dictates the legal name exactly).
        // Inactive clients are included so "delete X" can still report "X is already deleted"
        // instead of the misleading "not found".
        var wanted = rawName!;
        var all = await service.GetAllClientsAsync(includeInactive: true, cancellationToken: ct);
        var matches = all
            .Where(client => Matches(client, wanted))
            .ToList();

        return matches.Count switch
        {
            0 => Resolution.Failed($"Client matching '{wanted}' not found."),
            1 => Resolution.Found(matches[0]),
            _ => Resolution.Failed(
                $"'{wanted}' matches {matches.Count} clients:\n{DescribeCandidates(matches)}\n" +
                "Say which one — the safest is to pass its id.")
        };
    }

    /// <summary>
    /// One-line description of a client, used both in the "which one did you mean?" list and
    /// in what the write tools report back, so the same client always reads the same way.
    /// </summary>
    public static string Describe(ClientDto client)
        => $"ID={client.Id} | {client.CompanyName} | IČO: {client.RegistrationNumber ?? "(none)"} | " +
           $"DIČ: {client.TaxNumber ?? "(none)"} | VAT payer: {YesNo(client.IsVatPayer)} | " +
           $"{(client.IsActive ? "active" : "inactive")}{(client.IsIssuer ? " | ISSUER (your own company)" : string.Empty)}";

    /// <summary>Shared yes/no wording — the model should not see "True" in one tool and "Yes" in another.</summary>
    public static string YesNo(bool value) => value ? "Yes" : "No";

    /// <summary>Caps the candidate list — a model does not need fifty rows to ask one question.</summary>
    private const int MaxCandidatesShown = 10;

    private static bool Matches(ClientDto client, string wanted)
        => client.CompanyName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
           || (client.TradingName is not null &&
               client.TradingName.Contains(wanted, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads one identifier: trimmed, or null when it is missing or blank.
    /// Blank counts as missing so <c>"name": ""</c> cannot turn into a substring that
    /// matches every client in the database.
    /// </summary>
    private static string? Read(Dictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;

    private static string DescribeCandidates(IReadOnlyList<ClientDto> matches)
    {
        var shown = matches.Take(MaxCandidatesShown).Select(client => $"  - {Describe(client)}");
        var suffix = matches.Count > MaxCandidatesShown
            ? $"\n  … and {matches.Count - MaxCandidatesShown} more"
            : string.Empty;

        return string.Join("\n", shown) + suffix;
    }

    /// <summary>
    /// Outcome of the lookup: exactly one of the two properties is set.
    /// A record instead of a tuple so call sites read as <c>resolution.Error</c>, not <c>.Item2</c>.
    /// </summary>
    internal sealed record Resolution
    {
        public ClientDto? Client { get; private init; }

        public ChatToolResult? Error { get; private init; }

        public static Resolution Found(ClientDto client) => new() { Client = client };

        public static Resolution Failed(string message) => new() { Error = ChatToolResult.Failure(message) };
    }
}
