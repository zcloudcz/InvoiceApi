using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Import;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the Import API endpoints.
/// Handles PDF upload (multipart/form-data) for preview and JSON POST for confirm.
///
/// Unlike other API services, the preview endpoint requires multipart/form-data
/// for file uploads, which is not supported by ApiClientBase. So this service
/// uses HttpClient directly for the preview call.
/// </summary>
public class ImportApiService : ApiClientBase
{
    public ImportApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<ImportApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Uploads PDF files and returns extraction previews.
    /// Uses multipart/form-data encoding for the file uploads.
    ///
    /// Each file is sent as a form file named "files", and the target
    /// is sent as a form field named "target".
    /// </summary>
    /// <param name="files">List of (fileName, pdfBytes) tuples.</param>
    /// <param name="target">Import target: IssuedInvoice or ReceivedInvoice.</param>
    /// <returns>List of preview DTOs (one per file).</returns>
    public async Task<List<InvoiceImportPreviewDto>> PreviewAsync(
        List<(string FileName, byte[] Bytes)> files,
        EImportTarget target)
    {
        try
        {
            // Build multipart form data content
            using var content = new MultipartFormDataContent();

            // Add each PDF file
            foreach (var (fileName, bytes) in files)
            {
                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
                content.Add(fileContent, "files", fileName);
            }

            // Add the target enum as a form field
            content.Add(new StringContent(((int)target).ToString()), "target");

            // Use the underlying HttpClient directly (multipart not supported by base class)
            var response = await _httpClient.PostAsync("api/import/preview", content);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<List<InvoiceImportPreviewDto>>()
                    ?? new List<InvoiceImportPreviewDto>();
            }

            var error = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Import preview failed: {Status} - {Error}", response.StatusCode, error);
            return new List<InvoiceImportPreviewDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import preview request failed");
            return new List<InvoiceImportPreviewDto>();
        }
    }

    /// <summary>
    /// Confirms the import of previously previewed invoices.
    /// Standard JSON POST — delegated to base class helper.
    /// </summary>
    public async Task<List<ImportResultDto>> ConfirmAsync(ConfirmInvoiceImportRequest request)
    {
        return await PostAsync<ConfirmInvoiceImportRequest, List<ImportResultDto>>(
            "api/import/confirm", request) ?? new List<ImportResultDto>();
    }

    // ─── Client CSV import (N6) ────────────────────────────────────────────

    /// <summary>
    /// Uploads a single CSV file and returns the client import preview
    /// (New/Duplicate/Invalid per row). Nothing is saved yet.
    /// Throws on a non-success response so the caller can show the server's error message
    /// (e.g. "file too large") instead of a silent empty result.
    /// </summary>
    public async Task<ClientImportPreviewDto> PreviewClientsAsync(string fileName, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(fileContent, "file", fileName);

        var response = await _httpClient.PostAsync("api/import/clients/preview", content);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<ClientImportPreviewDto>() ?? new ClientImportPreviewDto();
        }

        await HandleErrorResponseAsync(response, "POST", "api/import/clients/preview");
        return new ClientImportPreviewDto(); // Unreachable — HandleErrorResponseAsync always throws
    }

    /// <summary>
    /// Confirms a client CSV import — creates the clients the user kept from the preview.
    /// </summary>
    public async Task<ClientImportResultDto> ConfirmClientsAsync(ClientImportConfirmDto request)
    {
        return await PostAsync<ClientImportConfirmDto, ClientImportResultDto>(
            "api/import/clients/confirm", request) ?? new ClientImportResultDto();
    }
}
