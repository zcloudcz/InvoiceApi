namespace Fakvio.Contracts.Dto.SystemConfiguration;

/// <summary>
/// Response DTO returned by POST /api/system-configuration/test-blob-connection.
/// Always returned with HTTP 200 so the UI can read the result without catching
/// HTTP-level exceptions. The Success flag tells whether the connection worked.
/// </summary>
public class BlobConnectionTestResultDto
{
    /// <summary>
    /// True if the Azure Blob Storage connection test succeeded.
    /// False if no connection string is configured or the connection attempt failed.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Short error message when Success = false. Null when Success = true.
    /// This is the raw exception message from the Azure SDK — it is safe to display
    /// to SysAdmin (they are the credential owner), but should not be exposed to
    /// regular users.
    /// </summary>
    public string? Error { get; init; }
}
