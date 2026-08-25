using Fakvio.Contracts.Common.Pagination;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="PaginationParams"/> — the base class that every paged filter DTO
/// in the app inherits from (invoices, received invoices, clients, logs, …). Its only
/// behaviour is the <see cref="PaginationParams.PageSize"/> setter, which clamps the
/// requested size into the allowed 1..100 range.
///
/// Junior note: the clamp sits in a property setter on purpose, so it fires no matter how
/// the DTO came to be — object initializer, query-string model binding, JSON deserialization.
/// That is what stops a caller asking for <c>pageSize=100000</c> from pulling a whole table
/// into memory, and a caller asking for <c>pageSize=0</c> from getting an always-empty page.
/// Individual callers sometimes clamp again on their own (e.g. <c>Math.Min(pageSize, 100)</c>
/// in the MCP tools); those are belt-and-braces, this suite is what pins the guarantee.
/// </summary>
public class PaginationParamsTests
{
    private const int MaxPageSize = 100;
    private const int MinPageSize = 1;
    private const int DefaultPageSize = 50;

    [Theory]
    [InlineData(500, MaxPageSize)]                  // far over the limit — the case that protects the DB
    [InlineData(MaxPageSize + 1, MaxPageSize)]      // one over the boundary
    [InlineData(MaxPageSize, MaxPageSize)]          // exactly on the boundary — must NOT be clamped down
    [InlineData(20, 20)]                            // ordinary value passes through untouched
    [InlineData(MinPageSize, MinPageSize)]          // lower boundary is a legal request
    [InlineData(0, MinPageSize)]                    // "not filled in" must not mean "empty page"
    [InlineData(-5, MinPageSize)]                   // negative would break Skip/Take on the query
    public void PageSize_IsClampedIntoAllowedRange(int requestedPageSize, int expectedPageSize)
    {
        // Arrange & Act: the clamp runs inside the setter, so assignment is the whole action
        var pagination = new PaginationParams { PageSize = requestedPageSize };

        // Assert
        pagination.PageSize.ShouldBe(expectedPageSize);
    }

    [Fact]
    public void PageSize_WhenNeverSet_FallsBackToDefault()
    {
        // Arrange & Act: a filter DTO built without any paging input
        var pagination = new PaginationParams();

        // Assert: callers rely on getting a sane page rather than 0 rows
        pagination.PageSize.ShouldBe(DefaultPageSize);
    }
}
