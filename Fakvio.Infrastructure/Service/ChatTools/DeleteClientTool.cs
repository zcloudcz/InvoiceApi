using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that deletes a client — behind the confirm gate from issue #212.
///
/// Typical usage:
///   "Smaž klienta Alfa s.r.o."  → preview → user agrees → the model repeats the call
///                                 with confirm: true
///
/// The delete is a SOFT delete: <see cref="IClientService.DeleteClientAsync"/> only clears
/// <c>IsActive</c>, and a client that already has invoices is refused outright, so the
/// accounting history can never lose the company it was issued to. The preview says so —
/// a user who hears "delete" and means "permanently erase" should not be surprised.
///
/// Junior note on what the gate is NOT: an authorization boundary. Deleting a client is
/// something the user can already do in the UI — the gate only stops the assistant from doing
/// it silently. The <c>confirm</c> parameter is added by the executor, never declared here
/// (see <see cref="ChatToolConfirmation"/> and DEVGUIDE §4.7).
/// </summary>
public class DeleteClientTool : IConfirmableChatTool
{
    private readonly IClientService _clientService;
    private readonly ILogger<DeleteClientTool> _logger;

    public DeleteClientTool(IClientService clientService, ILogger<DeleteClientTool> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    public string ToolName => "delete_client";

    public string Description =>
        "Delete a client (customer). The client is deactivated, not erased, and a client that " +
        "already has invoices cannot be deleted at all. Identify the client by id, " +
        "registration_number (IČO) or name.";

    public IReadOnlyList<ChatToolParameter> Parameters => ClientLookup.IdentityParameters;

    /// <summary>
    /// Names the client that would be deleted, so the user confirms a concrete company and not
    /// just the word "delete". Reads only — the executor appends the "confirm?" wording itself.
    /// </summary>
    public async Task<ChatToolResult> BuildPreviewAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        var resolution = await ClientLookup.ResolveAsync(_clientService, parameters, ct);
        if (resolution.Error is not null)
            return resolution.Error;

        var client = resolution.Client!;

        // An already-deleted client is not an error, but confirming a no-op wastes the user's
        // turn — say it now instead of after the write.
        if (!client.IsActive)
        {
            return ChatToolResult.Failure(
                $"Client '{client.CompanyName}' (ID={client.Id}) is already deleted (inactive).");
        }

        return ChatToolResult.Success(
            $"This would delete the client:\n{ClientLookup.Describe(client)}\n\n" +
            "The client is deactivated, not erased — existing invoices keep referring to it, " +
            "and it can be restored with update_client (is_active: true).");
    }

    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // Resolved again, deliberately: preview and execution are two independent calls with the
        // user's turns in between (see IConfirmableChatTool), so nothing from the preview is reused.
        var resolution = await ClientLookup.ResolveAsync(_clientService, parameters, ct);
        if (resolution.Error is not null)
            return resolution.Error;

        var client = resolution.Client!;
        _logger.LogInformation("DeleteClientTool: deleting client {ClientId}", client.Id);

        try
        {
            var deleted = await _clientService.DeleteClientAsync(client.Id, ct);

            return deleted
                ? ChatToolResult.Success($"Client deleted (deactivated):\n{ClientLookup.Describe(client)}")
                : ChatToolResult.Failure($"Client with ID {client.Id} no longer exists — nothing was deleted.");
        }
        catch (InvalidOperationException ex)
        {
            // ClientService refuses to delete a client that has invoices. That is a business
            // rule, not a bug — report it verbatim so the model can explain it to the user.
            _logger.LogInformation("DeleteClientTool: refused by business rule — {Reason}", ex.Message);
            return ChatToolResult.Failure(ex.Message);
        }
    }
}
