using System.Net;
using System.Text.Json;
using Fakvio.Contracts.Dto.VatReport;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the VAT Report API endpoint.
/// Fetches aggregated VAT data for a given period and handles EPO XML downloads.
/// Inherits ApiClientBase for shared auth, logging, impersonation, and error handling.
/// </summary>
public class VatReportApiService : ApiClientBase
{
    public VatReportApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<VatReportApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Fetches a VAT report for the specified period.
    /// Returns output VAT, input VAT, tax liability, revenue, expenses, and profit.
    /// </summary>
    public async Task<VatReportDto?> GetReportAsync(DateTime from, DateTime to)
    {
        try
        {
            return await GetAsync<VatReportDto>(
                $"/api/vat-report?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        }
        catch (ApiException)
        {
            return null;
        }
    }

    // =========================================================================
    // EPO export methods
    // =========================================================================

    /// <summary>
    /// Downloads the EPO DPHDP3 (VAT return) XML for the given year and period.
    /// Returns <see cref="EpoDownloadResult"/> instead of throwing so the calling
    /// component can distinguish EPO_HEADER_INCOMPLETE (guide user to settings)
    /// from VAT_PAYER_REQUIRED (show info alert) without catching exceptions.
    /// </summary>
    /// <param name="year">Tax year, e.g. 2026.</param>
    /// <param name="period">1–12 for Monthly, 1–4 for Quarterly.</param>
    /// <param name="type">Monthly or Quarterly.</param>
    public async Task<EpoDownloadResult> DownloadEpoVatReturnAsync(int year, int period, EVatPeriodType type)
    {
        var url = $"/api/vat-report/epo/return?year={year}&period={period}&type={(int)type}";
        return await DownloadEpoFileAsync(url, "DPHDP3", year, period, type);
    }

    /// <summary>
    /// Downloads the EPO DPHKH1 (control statement) XML for the given year and period.
    /// Returns <see cref="EpoDownloadResult"/> instead of throwing — see DownloadEpoVatReturnAsync.
    /// </summary>
    /// <param name="year">Tax year, e.g. 2026.</param>
    /// <param name="period">1–12 for Monthly, 1–4 for Quarterly.</param>
    /// <param name="type">Monthly or Quarterly.</param>
    public async Task<EpoDownloadResult> DownloadEpoControlStatementAsync(int year, int period, EVatPeriodType type)
    {
        var url = $"/api/vat-report/epo/control-statement?year={year}&period={period}&type={(int)type}";
        return await DownloadEpoFileAsync(url, "DPHKH1", year, period, type);
    }

    // =========================================================================
    // Private helpers
    // =========================================================================

    /// <summary>
    /// Shared implementation for both EPO download endpoints.
    /// Sends a GET, reads the response bytes on success, or parses the JSON error body
    /// on 400/403 to produce a structured <see cref="EpoDownloadResult"/>.
    /// </summary>
    private async Task<EpoDownloadResult> DownloadEpoFileAsync(
        string url, string formType, int year, int period, EVatPeriodType type)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation("GET (EPO) {Url}", url);

            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();

                // Prefer the server-provided filename from Content-Disposition.
                // Fall back to the locally-built name so the download always has a name.
                var fileName = ExtractContentDispositionFileName(response)
                    ?? BuildFallbackFileName(formType, year, period, type);

                return EpoDownloadResult.Success(bytes, fileName);
            }

            // Non-success: parse the JSON body for code + missingFields.
            return await ParseEpoErrorAsync(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error downloading EPO file from {Url}", url);
            return EpoDownloadResult.Failure("UNEXPECTED_ERROR", ex.Message);
        }
    }

    /// <summary>
    /// Reads the JSON error body returned by the API for 400 / 403 responses
    /// and maps it to an <see cref="EpoDownloadResult"/>.
    /// Expected shape: { "code": "...", "message": "...", "missingFields": ["..."] }
    /// </summary>
    private static async Task<EpoDownloadResult> ParseEpoErrorAsync(HttpResponseMessage response)
    {
        var errorBody = await response.Content.ReadAsStringAsync();

        string code = response.StatusCode == HttpStatusCode.Forbidden
            ? "VAT_PAYER_REQUIRED"
            : "UNKNOWN_ERROR";
        string message = errorBody;
        var missingFields = new List<string>();

        if (!string.IsNullOrWhiteSpace(errorBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("code", out var codeProp))
                    code = codeProp.GetString() ?? code;
                if (doc.RootElement.TryGetProperty("message", out var msgProp))
                    message = msgProp.GetString() ?? message;
                if (doc.RootElement.TryGetProperty("missingFields", out var fieldsProp)
                    && fieldsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in fieldsProp.EnumerateArray())
                    {
                        var field = item.GetString();
                        if (field is not null)
                            missingFields.Add(field);
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON response (e.g. proxy error) — use the raw body as message.
                message = errorBody.Length > 300 ? errorBody[..300] + "…" : errorBody;
            }
        }

        return EpoDownloadResult.Failure(code, message, missingFields);
    }

    /// <summary>
    /// Extracts the filename from the Content-Disposition header if present.
    /// Example header: attachment; filename=DPHDP3_2026_M04.xml
    /// </summary>
    private static string? ExtractContentDispositionFileName(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentDisposition?.FileName is { } fileName
            && !string.IsNullOrWhiteSpace(fileName))
        {
            // Some servers quote the filename — strip surrounding quotes.
            return fileName.Trim('"');
        }

        return null;
    }

    /// <summary>
    /// Builds a fallback file name when Content-Disposition is missing.
    /// Format: DPHDP3_2026_M04.xml (monthly) or DPHKH1_2026_Q1.xml (quarterly).
    /// </summary>
    private static string BuildFallbackFileName(
        string formType, int year, int period, EVatPeriodType type)
    {
        var periodPart = type == EVatPeriodType.Monthly
            ? $"M{period:D2}"
            : $"Q{period}";

        return $"{formType}_{year}_{periodPart}.xml";
    }
}
