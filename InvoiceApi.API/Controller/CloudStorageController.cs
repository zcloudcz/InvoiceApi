using InvoiceApi.Contracts.Dto.CloudStorage;
using InvoiceApi.Application.Service;
using InvoiceApi.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceApi.API.Controller;

/// <summary>
/// API controller for cloud storage operations (Google Drive, OneDrive).
/// Manages OAuth authorization flows, folder selection, connection testing,
/// and provides status information for the settings UI.
///
/// All endpoints require authentication. The current company is resolved
/// from the JWT CompanyId claim by the ITenantResolver.
///
/// Route: /api/cloud-storage
/// This path is added to MasterOnlyPaths in TenantContextMiddleware
/// because cloud storage settings are stored in the master database (CompanySystemSettings).
/// </summary>
[ApiController]
[Route("api/cloud-storage")]
[Authorize]
public class CloudStorageController : ControllerBase
{
    private readonly ICloudStorageOrchestrator _orchestrator;
    private readonly ILogger<CloudStorageController> _logger;

    public CloudStorageController(
        ICloudStorageOrchestrator orchestrator,
        ILogger<CloudStorageController> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Gets the connection status of all cloud storage providers for the current company.
    /// Returns one entry per supported provider (GoogleDrive, OneDrive) with connection state,
    /// selected folder, and token expiry information.
    /// </summary>
    [HttpGet("status")]
    public async Task<ActionResult<List<CloudStorageStatusDto>>> GetStatus(CancellationToken ct)
    {
        var statuses = await _orchestrator.GetProvidersStatusAsync(ct);
        return Ok(statuses);
    }

    /// <summary>
    /// Generates the OAuth authorization URL for a specific cloud storage provider.
    /// The frontend opens this URL in a popup window for the user to authorize access.
    /// </summary>
    /// <param name="provider">Which provider to generate the auth URL for (GoogleDrive or OneDrive).</param>
    /// <param name="redirectUri">The callback URL that the OAuth provider will redirect to after authorization.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("auth-url")]
    public async Task<ActionResult<string>> GetAuthorizationUrl(
        [FromQuery] ECloudStorageProvider provider,
        [FromQuery] string redirectUri,
        CancellationToken ct)
    {
        if (provider == ECloudStorageProvider.None)
            return BadRequest(new { message = "Provider is required." });

        if (string.IsNullOrWhiteSpace(redirectUri))
            return BadRequest(new { message = "Redirect URI is required." });

        var url = await _orchestrator.GetAuthorizationUrlAsync(provider, redirectUri, ct);
        return Ok(url);
    }

    /// <summary>
    /// Exchanges an OAuth authorization code for access and refresh tokens.
    /// Called by the OAuth callback page after the user approves access in the popup window.
    /// </summary>
    /// <param name="provider">Which provider the code is for.</param>
    /// <param name="code">The authorization code received from the OAuth callback.</param>
    /// <param name="redirectUri">The same redirect URI used when generating the auth URL.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("callback")]
    public async Task<ActionResult> ExchangeCode(
        [FromQuery] ECloudStorageProvider provider,
        [FromQuery] string code,
        [FromQuery] string redirectUri,
        CancellationToken ct)
    {
        if (provider == ECloudStorageProvider.None)
            return BadRequest(new { message = "Provider is required." });

        var success = await _orchestrator.ExchangeCodeAsync(provider, code, redirectUri, ct);
        if (!success)
            return BadRequest(new { message = "Failed to exchange authorization code." });

        return Ok(new { message = "Connected successfully." });
    }

    /// <summary>
    /// Disconnects a cloud storage provider by revoking tokens and clearing credentials.
    /// After disconnection, no automatic uploads will be attempted for this provider.
    /// </summary>
    /// <param name="provider">Which provider to disconnect.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("disconnect")]
    public async Task<ActionResult> Disconnect(
        [FromQuery] ECloudStorageProvider provider,
        CancellationToken ct)
    {
        var success = await _orchestrator.DisconnectAsync(provider, ct);
        if (!success)
            return BadRequest(new { message = "Failed to disconnect provider." });

        return Ok(new { message = "Disconnected successfully." });
    }

    /// <summary>
    /// Lists subfolders in a cloud storage provider for the folder picker UI.
    /// Pass parentId=null to list root-level folders.
    /// </summary>
    /// <param name="provider">Which provider to list folders from.</param>
    /// <param name="parentId">Parent folder ID, or null for root-level folders.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("folders")]
    public async Task<ActionResult<List<CloudStorageFolderDto>>> ListFolders(
        [FromQuery] ECloudStorageProvider provider,
        [FromQuery] string? parentId,
        CancellationToken ct)
    {
        var folders = await _orchestrator.ListFoldersAsync(provider, parentId, ct);
        return Ok(folders);
    }

    /// <summary>
    /// Sets the destination folder for a specific cloud storage provider.
    /// All future PDF uploads to this provider will be placed in this folder.
    /// Pass null folderId to clear the selection (uploads go to root).
    /// </summary>
    /// <param name="provider">Which provider to set the folder for.</param>
    /// <param name="request">Folder ID and name to set.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("set-folder")]
    public async Task<ActionResult> SetFolder(
        [FromQuery] ECloudStorageProvider provider,
        [FromBody] CloudStorageSetFolderRequest request,
        CancellationToken ct)
    {
        var success = await _orchestrator.SetFolderAsync(provider, request, ct);
        if (!success)
            return BadRequest(new { message = "Failed to set folder." });

        return Ok(new { message = "Folder set successfully." });
    }

    /// <summary>
    /// Tests the connection to a specific cloud storage provider.
    /// Makes a lightweight API call to verify that stored tokens are valid and the API is reachable.
    /// </summary>
    /// <param name="provider">Which provider to test.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("test")]
    public async Task<ActionResult> TestConnection(
        [FromQuery] ECloudStorageProvider provider,
        CancellationToken ct)
    {
        var success = await _orchestrator.TestConnectionAsync(provider, ct);
        return Ok(new { success });
    }
}
