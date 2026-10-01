using Fakvio.Contracts.Dto.Readiness;

namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Result of a UBL/Peppol eInvoice download attempt (ADR 0002, F1.6).
/// On success, FileBytes and FileName are set. On error, ErrorCode and Issues describe the
/// problem — same "don't lose the 400 body" reasoning as <see cref="EpoDownloadResult"/>, but
/// carrying the readiness <see cref="Issues"/> list (with per-issue FixRoute/MissingFields)
/// instead of a flat MissingFields list, because <c>UblExportService</c> (F1.5) returns the
/// richer <see cref="ReadinessIssueDto"/> shape every other readiness gate already uses.
/// </summary>
public class UblDownloadResult
{
    /// <summary>True when the API returned 200 with the XML file.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>Raw XML bytes returned by the API (populated on success).</summary>
    public byte[]? FileBytes { get; init; }

    /// <summary>Suggested file name, e.g. "Invoice_INV2026001.xml" (populated on success).</summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Error code from the API JSON body — "TENANT_NOT_READY" for a blocked export,
    /// "NOT_FOUND" for a 404, "UNEXPECTED_ERROR" for anything else. Populated on failure.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error message from the API. Populated on failure.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Blocking readiness issues from a "TENANT_NOT_READY" response — e.g. "EINVOICE_DRAFT",
    /// "EINVOICE_SELLER_ENDPOINT_MISSING". Empty for any other failure.
    /// </summary>
    public List<ReadinessIssueDto> Issues { get; init; } = [];

    /// <summary>Creates a success result.</summary>
    public static UblDownloadResult Success(byte[] bytes, string fileName)
        => new() { IsSuccess = true, FileBytes = bytes, FileName = fileName };

    /// <summary>Creates a failure result.</summary>
    public static UblDownloadResult Failure(string errorCode, string errorMessage, List<ReadinessIssueDto>? issues = null)
        => new()
        {
            IsSuccess = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Issues = issues ?? []
        };
}
