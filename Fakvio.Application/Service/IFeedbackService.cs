using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Feedback;

namespace Fakvio.Application.Service;

/// <summary>Shared feedback boundary. Implementations derive ownership from trusted request context.</summary>
public interface IFeedbackService
{
    Task<FeedbackDto> CreateAsync(CreateFeedbackDto input, CancellationToken ct = default);
    Task<PagedResult<FeedbackDto>> ListMineAsync(FeedbackFilterDto filter, CancellationToken ct = default);
    Task<FeedbackDto?> GetMineAsync(long id, CancellationToken ct = default);
    Task<PagedResult<FeedbackDto>> ListAllAsync(FeedbackFilterDto filter, CancellationToken ct = default);
    Task<FeedbackDto?> GetAnyAsync(long id, CancellationToken ct = default);
    Task<FeedbackDto?> UpdateAsync(long id, UpdateFeedbackStatusDto input, CancellationToken ct = default);
}
