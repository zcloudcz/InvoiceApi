using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.FileAttachment;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor service for communicating with the FileAttachment API endpoints.
/// Handles file upload (multipart/form-data), download (byte array), list, and delete.
///
/// Upload uses MultipartFormDataContent because the API expects IFormFile + form fields.
/// Download returns raw bytes for client-side file save via JS interop.
/// </summary>
public class FileAttachmentApiService : ApiClientBase
{
    public FileAttachmentApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<FileAttachmentApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Uploads a file to the API using multipart/form-data.
    /// Builds a MultipartFormDataContent with the file stream and metadata fields.
    /// </summary>
    /// <param name="entityName">Entity type (e.g., "Invoice", "Client").</param>
    /// <param name="recordId">ID of the entity record.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="contentType">MIME type of the file.</param>
    /// <param name="fileContent">File bytes.</param>
    /// <param name="description">Optional description.</param>
    /// <returns>The created FileAttachmentDto, or null on failure.</returns>
    public async Task<FileAttachmentDto?> UploadAsync(
        string entityName,
        long recordId,
        string fileName,
        string contentType,
        byte[] fileContent,
        string? description = null)
    {
        try
        {
            await AddAuthorizationHeaderAsync();
            _logger.LogInformation(
                "Uploading file {FileName} ({Size} bytes) for {EntityName} #{RecordId}",
                fileName, fileContent.Length, entityName, recordId);

            // Build multipart form data — this is what the API controller expects (IFormFile + form fields)
            using var content = new MultipartFormDataContent();

            // Add the file as "file" field (matches IFormFile parameter name in controller)
            var fileStreamContent = new ByteArrayContent(fileContent);
            fileStreamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Add(fileStreamContent, "file", fileName);

            // Add metadata fields (match [FromForm] parameter names in controller)
            content.Add(new StringContent(entityName), "entityName");
            content.Add(new StringContent(recordId.ToString()), "recordId");

            if (!string.IsNullOrWhiteSpace(description))
            {
                content.Add(new StringContent(description), "description");
            }

            var response = await _httpClient.PostAsync("/api/file-attachment/upload", content);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<FileAttachmentDto>();
            }

            // Read error response and throw ApiException (same pattern as base class methods)
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("POST (upload) /api/file-attachment/upload failed with {StatusCode}: {Error}",
                response.StatusCode, errorContent);
            throw new ApiException(response.StatusCode, errorContent, "/api/file-attachment/upload");
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file {FileName} for {EntityName} #{RecordId}",
                fileName, entityName, recordId);
            throw;
        }
    }

    /// <summary>
    /// Downloads file bytes by attachment ID.
    /// Returns the raw bytes — the caller (component) handles saving to disk via JS interop.
    /// </summary>
    /// <param name="attachmentId">FileAttachment ID.</param>
    /// <returns>File bytes, or null if not found.</returns>
    public async Task<byte[]?> DownloadAsync(long attachmentId)
    {
        try
        {
            return await GetBytesAsync($"/api/file-attachment/{attachmentId}/download");
        }
        catch (ApiException)
        {
            throw;
        }
    }

    /// <summary>
    /// Lists all file attachments for a given entity record.
    /// </summary>
    /// <param name="entityName">Entity type (e.g., "Invoice").</param>
    /// <param name="recordId">ID of the entity record.</param>
    /// <returns>List of attachments (newest first), or empty list on error.</returns>
    public async Task<List<FileAttachmentDto>> GetByEntityAsync(string entityName, long recordId)
    {
        try
        {
            return await GetAsync<List<FileAttachmentDto>>($"/api/file-attachment/{entityName}/{recordId}") ?? [];
        }
        catch (ApiException)
        {
            return [];
        }
    }

    /// <summary>
    /// Deletes a file attachment by its ID.
    /// </summary>
    /// <param name="attachmentId">FileAttachment ID to delete.</param>
    /// <returns>True if deleted successfully.</returns>
    public async Task<bool> DeleteAsync(long attachmentId)
    {
        return await DeleteAsync($"/api/file-attachment/{attachmentId}");
    }
}
