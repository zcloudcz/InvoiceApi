using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ReverseChargeCodeService.
///
/// Uses an in-memory EF Core database so no real PostgreSQL is needed.
/// Each test gets an isolated database (Guid-named) to prevent state leakage.
///
/// Covered acceptance criteria (issue #44):
/// - GetAllActiveAsync returns only records where IsActive == true AND ValidFrom &lt;= today
///   AND (ValidTo == null OR ValidTo >= today).
/// - GetByCodeAsync("5") returns the expected record.
/// - GetByCodeAsync for a non-existent code returns null.
/// - GetByIdAsync for an existing ID returns the correct record.
/// - GetByIdAsync for a non-existent ID returns null.
/// </summary>
public class ReverseChargeCodeServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ReverseChargeCodeService _service;

    // Fixed "today" used in ValidFrom/ValidTo test data — keeps tests deterministic.
    private static readonly DateOnly Today = new(2025, 6, 1);
    private static readonly DateTime SeedCreatedAt = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public ReverseChargeCodeServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        var logger = Substitute.For<ILogger<ReverseChargeCodeService>>();
        _service = new ReverseChargeCodeService(_context, logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ── Seed helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a controlled set of reverse charge codes for testing:
    ///
    ///  Id=1  code="1"   §92b  active,   ValidFrom=2016-01-01, ValidTo=null          → included in "active" set
    ///  Id=2  code="5"   §92c  active,   ValidFrom=2016-01-01, ValidTo=null          → included in "active" set
    ///  Id=3  code="11"  §92d  inactive, ValidFrom=2016-01-01, ValidTo=null          → excluded (IsActive=false)
    ///  Id=4  code="99"  §92e  active,   ValidFrom=2016-01-01, ValidTo=2024-12-31    → excluded (ValidTo in past)
    ///  Id=5  code="98"  §92e  active,   ValidFrom=2030-01-01, ValidTo=null          → excluded (ValidFrom in future)
    /// </summary>
    private void SeedTestData()
    {
        var codes = new List<ReverseChargeCode>
        {
            // Active, no expiry — should appear in GetAllActiveAsync
            new ReverseChargeCode
            {
                Id = 1, Code = "1", NameCs = "Zlato", NameEn = "Gold",
                ParagraphRef = "§92b",
                ValidFrom = new DateOnly(2016, 1, 1), ValidTo = null,
                IsActive = true, CreatedAt = SeedCreatedAt
            },
            // Active, no expiry — the key test for GetByCodeAsync("5")
            new ReverseChargeCode
            {
                Id = 2, Code = "5", NameCs = "Mobilní telefony", NameEn = "Mobile phones",
                ParagraphRef = "§92c",
                ValidFrom = new DateOnly(2016, 1, 1), ValidTo = null,
                IsActive = true, CreatedAt = SeedCreatedAt
            },
            // Inactive — must NOT appear in GetAllActiveAsync
            new ReverseChargeCode
            {
                Id = 3, Code = "11", NameCs = "Stavební práce", NameEn = "Construction work",
                ParagraphRef = "§92d",
                ValidFrom = new DateOnly(2016, 1, 1), ValidTo = null,
                IsActive = false, CreatedAt = SeedCreatedAt
            },
            // Active but ValidTo is in the past — must NOT appear in GetAllActiveAsync
            new ReverseChargeCode
            {
                Id = 4, Code = "99", NameCs = "Expired code", NameEn = null,
                ParagraphRef = "§92e",
                ValidFrom = new DateOnly(2016, 1, 1), ValidTo = new DateOnly(2024, 12, 31),
                IsActive = true, CreatedAt = SeedCreatedAt
            },
            // Active but ValidFrom is in the future — must NOT appear in GetAllActiveAsync
            new ReverseChargeCode
            {
                Id = 5, Code = "98", NameCs = "Future code", NameEn = null,
                ParagraphRef = "§92e",
                ValidFrom = new DateOnly(2030, 1, 1), ValidTo = null,
                IsActive = true, CreatedAt = SeedCreatedAt
            }
        };

        _context.ReverseChargeCode.AddRange(codes);
        _context.SaveChanges();
    }

    // ── GetAllActiveAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllActiveAsync_ReturnsOnlyActive_WithValidWindowCovering_Today()
    {
        // Act — note: service uses DateTime.UtcNow internally; seed data uses 2016 ValidFrom
        //        so codes 1 and 2 are definitely in range.
        var result = await _service.GetAllActiveAsync();

        // Assert: only the two active, non-expired, non-future-dated codes
        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.ShouldAllBe(r => r.IsActive);
    }

    [Fact]
    public async Task GetAllActiveAsync_ExcludesInactiveRecords()
    {
        // code "11" (Id=3) is inactive
        var result = await _service.GetAllActiveAsync();

        result.ShouldNotContain(r => r.Code == "11");
    }

    [Fact]
    public async Task GetAllActiveAsync_ExcludesExpiredRecords()
    {
        // code "99" (Id=4) has ValidTo=2024-12-31 which is in the past
        var result = await _service.GetAllActiveAsync();

        result.ShouldNotContain(r => r.Code == "99");
    }

    [Fact]
    public async Task GetAllActiveAsync_ExcludesFutureRecords()
    {
        // code "98" (Id=5) has ValidFrom=2030-01-01 which is in the future
        var result = await _service.GetAllActiveAsync();

        result.ShouldNotContain(r => r.Code == "98");
    }

    [Fact]
    public async Task GetAllActiveAsync_ResultsAreOrderedByCode()
    {
        var result = await _service.GetAllActiveAsync();

        // "1" comes before "5" lexicographically
        result[0].Code.ShouldBe("1");
        result[1].Code.ShouldBe("5");
    }

    // ── GetByCodeAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByCodeAsync_ReturnsCorrectRecord_ForCode5()
    {
        // Acceptance criterion: GetByCodeAsync("5") returns the expected record
        var result = await _service.GetByCodeAsync("5");

        result.ShouldNotBeNull();
        result.Code.ShouldBe("5");
        result.NameCs.ShouldBe("Mobilní telefony");
        result.NameEn.ShouldBe("Mobile phones");
        result.ParagraphRef.ShouldBe("§92c");
        result.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task GetByCodeAsync_ReturnsNull_ForNonExistentCode()
    {
        var result = await _service.GetByCodeAsync("000");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetByCodeAsync_ReturnsRecord_EvenWhenInactive()
    {
        // GetByCodeAsync does NOT filter by IsActive — it returns the row regardless.
        // The caller decides what to do with inactive codes.
        var result = await _service.GetByCodeAsync("11");

        result.ShouldNotBeNull();
        result.IsActive.ShouldBeFalse();
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_ReturnsCorrectRecord_ForExistingId()
    {
        var result = await _service.GetByIdAsync(1);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(1L);
        result.Code.ShouldBe("1");
        result.NameCs.ShouldBe("Zlato");
        result.ParagraphRef.ShouldBe("§92b");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_ForNonExistentId()
    {
        var result = await _service.GetByIdAsync(9999);

        result.ShouldBeNull();
    }

    // ── Boundary: ValidTo == today (inclusive) ────────────────────────────────

    [Fact]
    public async Task GetAllActiveAsync_IncludesRecord_WhenValidToEqualsToday()
    {
        // A code expiring exactly today must still be returned (ValidTo is inclusive).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _context.ReverseChargeCode.Add(new ReverseChargeCode
        {
            Id = 100, Code = "boundary-to", NameCs = "Boundary ValidTo",
            NameEn = null, ParagraphRef = "§92b",
            ValidFrom = new DateOnly(2016, 1, 1), ValidTo = today,
            IsActive = true, CreatedAt = SeedCreatedAt
        });
        await _context.SaveChangesAsync();

        var result = await _service.GetAllActiveAsync();

        result.ShouldContain(r => r.Code == "boundary-to");
    }

    // ── Boundary: ValidFrom == today (inclusive) ──────────────────────────────

    [Fact]
    public async Task GetAllActiveAsync_IncludesRecord_WhenValidFromEqualsToday()
    {
        // A code starting exactly today must be returned (ValidFrom is inclusive).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _context.ReverseChargeCode.Add(new ReverseChargeCode
        {
            Id = 101, Code = "boundary-from", NameCs = "Boundary ValidFrom",
            NameEn = null, ParagraphRef = "§92b",
            ValidFrom = today, ValidTo = null,
            IsActive = true, CreatedAt = SeedCreatedAt
        });
        await _context.SaveChangesAsync();

        var result = await _service.GetAllActiveAsync();

        result.ShouldContain(r => r.Code == "boundary-from");
    }

    // ── Empty database edge case ──────────────────────────────────────────────

    [Fact]
    public async Task GetAllActiveAsync_ReturnsEmptyList_WhenNoRecordsExist()
    {
        // Arrange: create a fresh context with no seed data
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var emptyContext = new TenantDbContext(options);
        var logger = NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<ReverseChargeCodeService>>();
        var emptyService = new ReverseChargeCodeService(emptyContext, logger);

        var result = await emptyService.GetAllActiveAsync();

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    // ── Alphanumeric code lookup ───────────────────────────────────────────────

    [Fact]
    public async Task GetByCodeAsync_ReturnsRecord_ForAlphanumericCode()
    {
        // Codes "1a" and "3a" exist in the MFČR číselník — service must handle them.
        _context.ReverseChargeCode.Add(new ReverseChargeCode
        {
            Id = 102, Code = "1a", NameCs = "Investiční zlato", NameEn = "Investment gold",
            ParagraphRef = "§92b",
            ValidFrom = new DateOnly(2016, 1, 1), ValidTo = null,
            IsActive = true, CreatedAt = SeedCreatedAt
        });
        await _context.SaveChangesAsync();

        var result = await _service.GetByCodeAsync("1a");

        result.ShouldNotBeNull();
        result.Code.ShouldBe("1a");
        result.NameCs.ShouldBe("Investiční zlato");
        result.ParagraphRef.ShouldBe("§92b");
    }

    // ── DTO field mapping completeness ────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_MapsAllDtoFields_Correctly()
    {
        // Verifies that ZMapper maps every field of ReverseChargeCode → ReverseChargeCodeDto.
        // Note: CreatedAt is overwritten to DateTime.UtcNow by TenantDbContext.SaveChanges(),
        //       so we only assert it is not the default DateTime value.
        var result = await _service.GetByIdAsync(2);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(2L);
        result.Code.ShouldBe("5");
        result.NameCs.ShouldBe("Mobilní telefony");
        result.NameEn.ShouldBe("Mobile phones");
        result.ParagraphRef.ShouldBe("§92c");
        result.ValidFrom.ShouldBe(new DateOnly(2016, 1, 1));
        result.ValidTo.ShouldBeNull();
        result.IsActive.ShouldBeTrue();
        result.CreatedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task GetByCodeAsync_MapsNullNameEn_AsNullInDto()
    {
        // NameEn is optional — when null on the entity it must be null on the DTO.
        var result = await _service.GetByCodeAsync("99");

        result.ShouldNotBeNull();
        result.NameEn.ShouldBeNull();
    }
}
