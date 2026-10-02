using System.Net.Http.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor-side client for the tenant payment-matching API
/// (<see cref="PaymentMatchingController"/> on the server).
///
/// Uses the shared <see cref="ApiClientBase"/> machinery so impersonation,
/// auth refresh, and typed error handling come for free.
/// </summary>
public class PaymentMatchingApiService : ApiClientBase
{
    public PaymentMatchingApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<PaymentMatchingApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    // ─── Mailbox lifecycle ──────────────────────────────────────────────────

    public Task<BankAccountMailboxDto?> GetMailboxAsync(long bankAccountId) =>
        GetAsync<BankAccountMailboxDto>($"api/payment-matching/mailbox/{bankAccountId}");

    public Task<BankAccountMailboxDto?> ActivateMailboxAsync(long bankAccountId) =>
        PostAsync<object, BankAccountMailboxDto>(
            $"api/payment-matching/mailbox/{bankAccountId}/activate", new { });

    public Task<BankAccountMailboxDto?> DeactivateMailboxAsync(long bankAccountId) =>
        PostAsync<object, BankAccountMailboxDto>(
            $"api/payment-matching/mailbox/{bankAccountId}/deactivate", new { });

    public Task<BankAccountMailboxDto?> RegenerateMailboxAsync(long bankAccountId) =>
        PostAsync<object, BankAccountMailboxDto>(
            $"api/payment-matching/mailbox/{bankAccountId}/regenerate", new { });

    // ─── Bank transactions ──────────────────────────────────────────────────

    public Task<PagedResult<BankTransactionDto>?> ListTransactionsAsync(
        EMatchStatus? status = null,
        EPaymentDirection? direction = null,
        long? bankAccountId = null,
        DateTime? from = null,
        DateTime? to = null,
        string? search = null,
        int page = 1,
        int pageSize = 50)
    {
        var qs = new List<string>();
        if (status.HasValue) qs.Add($"status={status.Value}");
        if (direction.HasValue) qs.Add($"direction={direction.Value}");
        if (bankAccountId.HasValue) qs.Add($"bankAccountId={bankAccountId.Value}");
        if (from.HasValue) qs.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to.HasValue) qs.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        qs.Add($"page={page}");
        qs.Add($"pageSize={pageSize}");

        return GetAsync<PagedResult<BankTransactionDto>>(
            "api/payment-matching/transactions?" + string.Join("&", qs));
    }

    public Task<BankTransactionDto?> GetTransactionAsync(long id) =>
        GetAsync<BankTransactionDto>($"api/payment-matching/transactions/{id}");

    public Task<ManualMatchResponse?> MatchAsync(long transactionId, ManualMatchRequest req) =>
        PostAsync<ManualMatchRequest, ManualMatchResponse>(
            $"api/payment-matching/transactions/{transactionId}/match", req);

    public Task UnmatchAsync(long transactionId, UnmatchRequest req) =>
        PostAsync<UnmatchRequest, object>(
            $"api/payment-matching/transactions/{transactionId}/unmatch", req);

    public Task IgnoreAsync(long transactionId) =>
        PostAsync<object, object>(
            $"api/payment-matching/transactions/{transactionId}/ignore", new { });

    public Task<int> GetUnmatchedCountAsync() =>
        GetAsync<int>("api/payment-matching/unmatched-count");

    // ─── Auto-match ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reverse auto-match: given an unmatched incoming bank transaction ID, asks the server
    /// to find the best candidate issued invoice.
    /// Calls POST /api/payment-matching/transactions/{id}/auto-match.
    /// Returns null when no invoice candidate is found (HTTP 204) or on error.
    /// </summary>
    public Task<TransactionAutoMatchProposalDto?> FindAutoMatchForTransactionAsync(long transactionId) =>
        PostWithoutBodyAsync<TransactionAutoMatchProposalDto>(
            $"api/payment-matching/transactions/{transactionId}/auto-match");

    /// <summary>
    /// Calls POST /api/payment-matching/confirm-auto-match.
    /// Returns null when the server responds with 4xx/5xx.
    /// </summary>
    public Task<ConfirmAutoMatchResponse?> ConfirmAutoMatchAsync(ConfirmAutoMatchRequest req) =>
        PostAsync<ConfirmAutoMatchRequest, ConfirmAutoMatchResponse>(
            "api/payment-matching/confirm-auto-match", req);

    // ─── Recognized counterparty assignment ──────────────────────────────────

    /// <summary>Manually assigns a transaction to a recognized-counterparty registry entry.</summary>
    public Task AssignRecognizedAsync(long transactionId, long recognizedCounterpartyId) =>
        PostAsync<Contracts.Dto.RecognizedCounterparty.AssignRecognizedRequest, object>(
            $"api/payment-matching/transactions/{transactionId}/assign-recognized",
            new Contracts.Dto.RecognizedCounterparty.AssignRecognizedRequest
            {
                RecognizedCounterpartyId = recognizedCounterpartyId
            });

    /// <summary>Removes the recognized-counterparty assignment (back to Unmatched).</summary>
    public Task UnassignRecognizedAsync(long transactionId) =>
        PostAsync<object, object>(
            $"api/payment-matching/transactions/{transactionId}/unassign-recognized", new { });

    // ─── GPC/ABO statement import ───────────────────────────────────────────

    /// <summary>
    /// Uploads a GPC/ABO bank statement (multipart, bypasses ApiClientBase's JSON helpers).
    /// Throws ApiException with the server's message on failure (e.g. unsupported file).
    /// </summary>
    public async Task<BankStatementImportResultDto> ImportStatementAsync(string fileName, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);

        await AddAuthorizationHeaderAsync();
        var response = await _httpClient.PostAsync("api/bank-statement/import", content);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<BankStatementImportResultDto>() ?? new BankStatementImportResultDto();

        await HandleErrorResponseAsync(response, "POST", "api/bank-statement/import");
        return new BankStatementImportResultDto(); // unreachable - HandleErrorResponseAsync always throws
    }
}
