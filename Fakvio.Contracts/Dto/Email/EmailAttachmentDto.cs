using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Email;

/// <summary>
/// One extra file attached to an invoice email (on top of the generated PDF / ISDOC / UBL).
/// The file travels as base64 inside the JSON body so MCP / API clients do not need multipart upload.
/// </summary>
/// <remarks>
/// Junior note: the content type is deliberately NOT part of this DTO. A client-supplied MIME type
/// is untrusted input; the server derives it from the (whitelisted) file extension instead.
/// Size / count / extension limits are enforced by <c>EmailAttachmentValidator</c> in the
/// Application layer, not by attributes here, because they depend on the whole list.
/// </remarks>
public class EmailAttachmentDto
{
    /// <summary>File name shown in the recipient's mail client, e.g. "contract.pdf". No path, allowed extensions only.</summary>
    [Required(ErrorMessage = "Attachment file name is required.")]
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>File content encoded as standard base64 (RFC 4648, with padding).</summary>
    [Required(ErrorMessage = "Attachment content is required.")]
    public string ContentBase64 { get; set; } = string.Empty;
}
