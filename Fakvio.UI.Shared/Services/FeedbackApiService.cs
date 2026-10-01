using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Feedback;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>Uses the normal authenticated client, but never logs report or response bodies.</summary>
public sealed class FeedbackApiService(IHttpClientFactory factory, ILogger<FeedbackApiService> logger,
    AuthenticationStateProvider authentication) : ApiClientBase(factory, logger, authentication)
{
    public Task<FeedbackDto> CreateAsync(CreateFeedbackDto report, CancellationToken ct = default) =>
        SendAsync<FeedbackDto>(HttpMethod.Post, "/api/feedback", report, ct);

    public Task<PagedResult<FeedbackDto>> ListAsync(FeedbackFilterDto filter, bool admin, CancellationToken ct = default)
    {
        var url = $"{Root(admin)}?page={filter.Page}&pageSize={filter.PageSize}";
        if (filter.Type.HasValue) url += $"&type={(int)filter.Type.Value}";
        if (filter.Status.HasValue) url += $"&status={(int)filter.Status.Value}";
        return SendAsync<PagedResult<FeedbackDto>>(HttpMethod.Get, url, null, ct);
    }

    public Task<FeedbackDto> GetReportAsync(long id, bool admin, CancellationToken ct = default) =>
        SendAsync<FeedbackDto>(HttpMethod.Get, $"{Root(admin)}/{id}", null, ct);

    public Task<FeedbackDto> UpdateAsync(long id, UpdateFeedbackStatusDto update, CancellationToken ct = default) =>
        SendAsync<FeedbackDto>(HttpMethod.Patch, $"{Root(true)}/{id}", update, ct);

    private static string Root(bool admin) => admin ? "/api/sysadmin/feedback" : "/api/feedback";

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        await AddAuthorizationHeaderAsync();
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Even validation responses could contain submitted text. Log only the status.
            _logger.LogWarning("Feedback request failed with HTTP {Status}", (int)response.StatusCode);
            throw new ApiException(response.StatusCode, $"HTTP {(int)response.StatusCode}", url);
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new HttpRequestException("Feedback response was empty.");
    }
}

/// <summary>Collects only a local path: query strings and fragments may contain secrets.</summary>
public static class FeedbackPageContext
{
    /// <summary>Accepts a path or a URL for this app only; secrets in queries/fragments are discarded.</summary>
    public static string? NormalizeInput(string? value, string currentUri)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.StartsWith('/')) return NormalizePath(value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var supplied) ||
            !Uri.TryCreate(currentUri, UriKind.Absolute, out var current) ||
            supplied.Scheme != current.Scheme || supplied.Authority != current.Authority ||
            !string.IsNullOrEmpty(supplied.UserInfo)) return null;
        return NormalizePath(supplied.AbsolutePath);
    }

    public static string? FromUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        return NormalizePath(parsed.AbsolutePath);
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Split('?', '#')[0];
        var decoded = Uri.UnescapeDataString(path);
        if (decoded.StartsWith("//") || decoded.Contains('\\') || decoded.Any(char.IsControl)) return null;
        return path.Length <= 2048 && path.StartsWith('/') && !path.StartsWith("//")
            && !path.Contains('\\') && !path.Any(char.IsControl) ? path : null;
    }
}
