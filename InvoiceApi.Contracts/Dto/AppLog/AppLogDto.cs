namespace InvoiceApi.Contracts.Dto.AppLog;

/// <summary>
/// DTO for a single application log entry.
/// Returned by the /api/logs endpoints for display on the Logs page.
/// </summary>
public class AppLogDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
    public long? UserId { get; set; }
    public long? CompanyId { get; set; }
    public string? RequestPath { get; set; }
}
