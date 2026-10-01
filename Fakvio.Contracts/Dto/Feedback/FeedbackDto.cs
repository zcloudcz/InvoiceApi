using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Feedback;

/// <summary>User-supplied report content. Ownership and status are assigned only by the server.</summary>
public class CreateFeedbackDto
{
    public EFeedbackType Type { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Page { get; set; }
    public string? AppVersion { get; set; }
}

/// <summary>Persisted report and the public response, shared by HTTP, Blazor and MCP.</summary>
public class FeedbackDto : CreateFeedbackDto
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long CompanyId { get; set; }
    public EFeedbackStatus Status { get; set; }
    public string? PublicResponse { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>One-based, bounded paging; filters apply before the page is selected.</summary>
public class FeedbackFilterDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public EFeedbackType? Type { get; set; }
    public EFeedbackStatus? Status { get; set; }
}

/// <summary>Only SysAdmin may change these fields; response text is visible to the submitter.</summary>
public class UpdateFeedbackStatusDto
{
    public EFeedbackStatus Status { get; set; }
    public string? PublicResponse { get; set; }
}
