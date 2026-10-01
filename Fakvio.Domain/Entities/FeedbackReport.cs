using Fakvio.Domain.Common;
using Fakvio.Domain.Enums;

namespace Fakvio.Domain.Entities;

/// <summary>A centrally stored report. UserId and CompanyId are server-derived ownership boundaries.</summary>
public class FeedbackReport : BaseEntity
{
    public EFeedbackType Type { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Page { get; set; }
    public string? AppVersion { get; set; }
    public long UserId { get; set; }
    public long CompanyId { get; set; }
    public EFeedbackStatus Status { get; set; }
    public string? PublicResponse { get; set; }
}
