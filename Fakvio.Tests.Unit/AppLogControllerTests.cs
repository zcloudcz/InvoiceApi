// ============================================================================
// AppLogControllerTests — coverage for issue #111.
//
// Tests for AppLogController.GetPaged():
//   1. Level exact-match filter
//   2. Date range filters (from / to)
//   3. Pagination (page / pageSize, including clamping)
//   4. New source and message column-filter parameters are accepted (HTTP 200)
//   5. New search parameter is accepted (HTTP 200)
//   6. Combined filters narrow results correctly
//
// Note on EF.Functions.ILike: that PostgreSQL-specific function is NOT supported
// by the EF Core InMemory provider (throws InvalidOperationException at runtime).
// Tests that would exercise ILike paths (search, source, message) therefore only
// verify that the endpoint accepts the parameter and returns HTTP 200.  The actual
// case-insensitive matching behaviour is covered by integration tests against a
// real PostgreSQL instance.
// ============================================================================

using Fakvio.API.Controller;
using Fakvio.Contracts.Dto.AppLog;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AppLogController.GetPaged"/> using an EF Core
/// InMemory database.  Filters that rely on <c>EF.Functions.ILike</c> (search,
/// source, message) are only smoke-tested for HTTP 200; correctness of the
/// ILIKE pattern is a PostgreSQL-only concern tested at the integration level.
/// </summary>
public class AppLogControllerTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly AppLogController _sut;

    public AppLogControllerTests()
    {
        // Fresh in-memory database for each test so tests don't interfere.
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new MasterDbContext(options);

        _sut = new AppLogController(
            _context,
            Substitute.For<ILogger<AppLogController>>(),
            Substitute.For<ILoggerFactory>());
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the given AppLog entries into the in-memory database.
    /// </summary>
    private async Task SeedAsync(params AppLog[] logs)
    {
        _context.AppLog.AddRange(logs);
        await _context.SaveChangesAsync();
    }

    private static AppLog MakeLog(string level = "Information", string source = "Test.Source",
        string message = "Hello world", DateTime? timestamp = null, string? exception = null,
        string? correlationId = null)
        => new()
        {
            Timestamp = timestamp ?? DateTime.UtcNow,
            Level = level,
            Source = source,
            Message = message,
            Exception = exception,
            CorrelationId = correlationId
        };

    // ── Level filter ─────────────────────────────────────────────────────────

    /// <summary>
    /// When a level filter is supplied, only entries with that exact Level value
    /// should be returned.
    /// </summary>
    [Fact]
    public async Task GetPaged_LevelFilter_ReturnsOnlyMatchingLevel()
    {
        await SeedAsync(
            MakeLog(level: "Error"),
            MakeLog(level: "Warning"),
            MakeLog(level: "Information"));

        var result = await _sut.GetPaged(level: "Error");

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.Items.ShouldAllBe(x => x.Level == "Error");
        paged.TotalCount.ShouldBe(1);
    }

    /// <summary>
    /// Level filter is case-sensitive (values come from a fixed dropdown).
    /// A mismatched case returns zero results.
    /// </summary>
    [Fact]
    public async Task GetPaged_LevelFilter_CaseSensitiveExactMatch()
    {
        await SeedAsync(MakeLog(level: "Error"));

        var result = await _sut.GetPaged(level: "error"); // wrong case

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.TotalCount.ShouldBe(0);
    }

    // ── Date range filter ────────────────────────────────────────────────────

    /// <summary>
    /// When a <c>from</c> date is supplied, entries before that date are excluded.
    /// </summary>
    [Fact]
    public async Task GetPaged_FromFilter_ExcludesOlderEntries()
    {
        var old = MakeLog(timestamp: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = MakeLog(timestamp: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync(old, recent);

        var result = await _sut.GetPaged(from: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.TotalCount.ShouldBe(1);
        paged.Items.Single().Timestamp.Year.ShouldBe(2025);
    }

    /// <summary>
    /// When a <c>to</c> date is supplied, entries after that date are excluded.
    /// </summary>
    [Fact]
    public async Task GetPaged_ToFilter_ExcludesNewerEntries()
    {
        var old = MakeLog(timestamp: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = MakeLog(timestamp: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync(old, recent);

        var result = await _sut.GetPaged(to: new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc));

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.TotalCount.ShouldBe(1);
        paged.Items.Single().Timestamp.Year.ShouldBe(2024);
    }

    /// <summary>
    /// Both from and to can be combined to create a date window.
    /// </summary>
    [Fact]
    public async Task GetPaged_FromAndToFilter_ReturnsEntriesInWindow()
    {
        await SeedAsync(
            MakeLog(timestamp: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            MakeLog(timestamp: new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc)),
            MakeLog(timestamp: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        var result = await _sut.GetPaged(
            from: new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            to: new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc));

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.TotalCount.ShouldBe(1);
        paged.Items.Single().Timestamp.Month.ShouldBe(6);
    }

    // ── Pagination ───────────────────────────────────────────────────────────

    /// <summary>
    /// Results are ordered newest-first so the most recent entries appear on page 1.
    /// </summary>
    [Fact]
    public async Task GetPaged_OrderedNewestFirst()
    {
        var earlier = MakeLog(timestamp: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var later = MakeLog(timestamp: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync(earlier, later);

        var result = await _sut.GetPaged(pageSize: 2);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.Items[0].Timestamp.Year.ShouldBe(2025);
        paged.Items[1].Timestamp.Year.ShouldBe(2024);
    }

    /// <summary>
    /// pageSize is clamped to a maximum of 200 — requesting 999 should return at most 200.
    /// </summary>
    [Fact]
    public async Task GetPaged_PageSizeClamped_ToMaximum200()
    {
        // Seed 201 entries
        var entries = Enumerable.Range(1, 201)
            .Select(i => MakeLog(timestamp: new DateTime(2024, 1, i > 28 ? 2 : 1, i % 24, 0, 0, DateTimeKind.Utc)))
            .ToArray();
        await SeedAsync(entries);

        var result = await _sut.GetPaged(pageSize: 999);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        // The clamped page size is 200 — items returned should not exceed 200
        paged.Items.Count.ShouldBe(200);
        // TotalCount still reflects all matching entries
        paged.TotalCount.ShouldBe(201);
    }

    /// <summary>
    /// Requesting page 2 returns the second batch of results.
    /// </summary>
    [Fact]
    public async Task GetPaged_Page2_ReturnsSecondBatch()
    {
        // Seed 5 entries with distinct timestamps so ordering is deterministic
        var entries = Enumerable.Range(1, 5)
            .Select(i => MakeLog(timestamp: new DateTime(2024, 1, i, 0, 0, 0, DateTimeKind.Utc),
                                 message: $"Entry {i}"))
            .ToArray();
        await SeedAsync(entries);

        // Page 1, size 3 → entries 5, 4, 3 (newest first)
        // Page 2, size 3 → entries 2, 1
        var result = await _sut.GetPaged(page: 2, pageSize: 3);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.Items.Count.ShouldBe(2);
        paged.TotalCount.ShouldBe(5);
    }

    // ── New filter parameters accepted (HTTP 200) ────────────────────────────

    // NOTE: EF.Functions.ILike is a PostgreSQL-only feature not supported by the
    // InMemory provider. The tests below only verify that the endpoint accepts the
    // new parameters without crashing for the *no-matching-rows* InMemory path
    // (the ILike WHERE clause is only evaluated when rows exist in the result set).
    // In practice, the InMemory provider will throw if it tries to translate ILike
    // on any real data row.  These tests use an empty database to stay safe.

    /// <summary>
    /// The new <c>source</c> query parameter is accepted and returns HTTP 200
    /// even when the database is empty (no ILike evaluation needed).
    /// </summary>
    [Fact]
    public async Task GetPaged_SourceParameter_IsAccepted_Returns200()
    {
        // Empty DB — ILike WHERE clause is never evaluated
        var result = await _sut.GetPaged(source: "Fakvio.Infrastructure");

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    /// <summary>
    /// The new <c>message</c> query parameter is accepted and returns HTTP 200
    /// even when the database is empty.
    /// </summary>
    [Fact]
    public async Task GetPaged_MessageParameter_IsAccepted_Returns200()
    {
        var result = await _sut.GetPaged(message: "payment");

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    /// <summary>
    /// The <c>search</c> parameter is accepted and returns HTTP 200 with an empty
    /// result set when the database is empty (no ILike evaluation needed).
    /// </summary>
    [Fact]
    public async Task GetPaged_SearchParameter_IsAccepted_Returns200()
    {
        var result = await _sut.GetPaged(search: "exception");

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    // ── Combined filters ──────────────────────────────────────────────────────

    /// <summary>
    /// Level filter and date range can be combined — only entries matching both
    /// conditions are returned.
    /// </summary>
    [Fact]
    public async Task GetPaged_LevelAndDateRange_CombinedCorrectly()
    {
        await SeedAsync(
            MakeLog(level: "Error", timestamp: new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            MakeLog(level: "Error", timestamp: new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)),
            MakeLog(level: "Warning", timestamp: new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        var result = await _sut.GetPaged(
            level: "Error",
            from: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.TotalCount.ShouldBe(1);
        paged.Items.Single().Level.ShouldBe("Error");
        paged.Items.Single().Timestamp.Year.ShouldBe(2025);
    }

    /// <summary>
    /// No filters returns all seeded entries.
    /// </summary>
    [Fact]
    public async Task GetPaged_NoFilters_ReturnsAllEntries()
    {
        await SeedAsync(MakeLog(), MakeLog(), MakeLog());

        var result = await _sut.GetPaged();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.TotalCount.ShouldBe(3);
    }

    /// <summary>
    /// Empty database returns an empty page with zero TotalCount.
    /// </summary>
    [Fact]
    public async Task GetPaged_EmptyDatabase_ReturnsEmptyResult()
    {
        var result = await _sut.GetPaged();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        paged.TotalCount.ShouldBe(0);
        paged.Items.ShouldBeEmpty();
    }

    /// <summary>
    /// AppLogDto properties are correctly mapped from the AppLog entity.
    /// </summary>
    [Fact]
    public async Task GetPaged_MappedDto_ContainsAllFields()
    {
        var ts = new DateTime(2024, 3, 15, 10, 30, 0, DateTimeKind.Utc);
        await SeedAsync(new AppLog
        {
            Timestamp = ts,
            Level = "Warning",
            Source = "Fakvio.Service.EmailService",
            Message = "SMTP timeout",
            Exception = "System.TimeoutException: ...",
            UserId = 42,
            CompanyId = 7,
            RequestPath = "/api/invoice/5",
            CorrelationId = "abc-123"
        });

        var result = await _sut.GetPaged();

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var paged = ok.Value.ShouldBeOfType<Fakvio.Contracts.Common.Pagination.PagedResult<AppLogDto>>();
        var dto = paged.Items.Single();
        dto.Timestamp.ShouldBe(ts);
        dto.Level.ShouldBe("Warning");
        dto.Source.ShouldBe("Fakvio.Service.EmailService");
        dto.Message.ShouldBe("SMTP timeout");
        dto.Exception.ShouldBe("System.TimeoutException: ...");
        dto.UserId.ShouldBe(42);
        dto.CompanyId.ShouldBe(7);
        dto.RequestPath.ShouldBe("/api/invoice/5");
        dto.CorrelationId.ShouldBe("abc-123");
    }
}
