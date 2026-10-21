using System.ComponentModel;
using System.Text.Json;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using ModelContextProtocol.Server;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// MCP tools for reading stored file attachments (invoice PDFs from suppliers, contracts, ...).
/// Uploading is a separate tool (UploadReceivedInvoiceAttachment); here the file never travels through
/// the conversation - the tools list metadata and hand out short-lived download links.
/// </summary>
[McpServerToolType]
public static class AttachmentTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>Lists the attachments of one record (metadata only, newest first).</summary>
    [McpServerTool(Title = "List attachments of a record", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "List the stored file attachments (id, file name, type, size, description) of a record, e.g. entityName 'ReceivedInvoice' " +
        "or 'Invoice' with the record ID. Use the returned attachment id with GetAttachmentDownloadLink.")]
    public static async Task<string> ListAttachments(
        IFakvioApiClient api,
        [Description("Entity type of the record, e.g. 'Invoice' or 'ReceivedInvoice'")] string entityName,
        [Description("ID of the record")] long recordId,
        CancellationToken ct = default)
    {
        try
        {
            var attachments = await api.GetFileAttachmentsAsync(entityName, recordId, ct);
            return JsonSerializer.Serialize(attachments, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>Download link for one stored attachment.</summary>
    [McpServerTool(Title = "Get attachment download link", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Get a short-lived download link (downloadUrl) for ONE stored attachment - give that link to the user and do NOT fetch it " +
        "or load the file contents into the conversation. Find the attachment id with ListAttachments.")]
    public static async Task<string> GetAttachmentDownloadLink(
        IFakvioApiClient api,
        [Description("The attachment ID (from ListAttachments)")] long attachmentId,
        CancellationToken ct = default)
    {
        try
        {
            var link = await api.CreateDownloadLinkAsync(
                new CreateDownloadLinkDto { Kind = EDownloadKind.AttachmentFile, Id = attachmentId }, ct);
            return DownloadLinkResult.ToJson(link, mimeType: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }

    /// <summary>Download link for a ZIP of all attachments of one record.</summary>
    [McpServerTool(Title = "Get ZIP of all attachments of a record", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Get a short-lived download link (downloadUrl) for a ZIP with ALL attachments of one record - give that link to the user and " +
        "do NOT fetch it or load the file contents into the conversation. The link fails when the record has no attachments.")]
    public static async Task<string> GetRecordAttachmentsZipLink(
        IFakvioApiClient api,
        [Description("Entity type of the record, e.g. 'Invoice' or 'ReceivedInvoice'")] string entityName,
        [Description("ID of the record")] long recordId,
        CancellationToken ct = default)
    {
        try
        {
            var link = await api.CreateDownloadLinkAsync(
                new CreateDownloadLinkDto { Kind = EDownloadKind.AttachmentsZip, EntityName = entityName, Id = recordId }, ct);
            return DownloadLinkResult.ToJson(link, "application/zip");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpToolError.ToJson(ex);
        }
    }
}
