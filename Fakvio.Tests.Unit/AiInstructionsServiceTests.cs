using Fakvio.Contracts.Dto.SystemConfiguration;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for AiInstructionsService.
/// Tests the CRUD operations (Get / Update / Reset) and cache behavior.
///
/// Uses InMemoryDatabase + MemoryCache for fast, isolated testing.
/// MasterDbContext is the same as used by SystemConfigurationService.
/// </summary>
public class AiInstructionsServiceTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly AiInstructionsService _service;

    public AiInstructionsServiceTests()
    {
        // Each test gets a fresh in-memory DB to prevent state leakage.
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: $"AiInstructionsTest_{Guid.NewGuid()}")
            .Options;

        _context = new MasterDbContext(options);
        _cache = new MemoryCache(new MemoryCacheOptions());
        var logger = Substitute.For<ILogger<AiInstructionsService>>();

        _service = new AiInstructionsService(_context, _cache, logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _cache.Dispose();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a SystemConfiguration row with the given prompt values.
    /// </summary>
    private async Task SeedConfigAsync(string? customPrompt, string? appendix)
    {
        var config = new SystemConfiguration
        {
            SmtpHost = "",
            SmtpPort = 587,
            SmtpSenderEmail = "",
            SmtpSenderName = "Fakvio",
            SmtpUseSsl = true,
            JwtExpirationHours = 24,
            AiSystemPromptCustom = customPrompt,
            AiSystemPromptAppendix = appendix,
            CreatedAt = DateTime.UtcNow
        };
        _context.Set<SystemConfiguration>().Add(config);
        await _context.SaveChangesAsync();
    }

    // ── GetAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_WhenNoRow_ReturnsDefaultDto()
    {
        // Act — no seed, auto-creates the row
        var result = await _service.GetAsync();

        // Assert — both fields null (no custom instructions)
        result.ShouldNotBeNull();
        result.CustomPrompt.ShouldBeNull();
        result.Appendix.ShouldBeNull();
        result.IsCustomActive.ShouldBeFalse();
        result.IsAppendixActive.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_WhenCustomPromptSet_ReturnsIt()
    {
        // Arrange
        await SeedConfigAsync("Custom prompt text", null);

        // Act
        var result = await _service.GetAsync();

        // Assert
        result.CustomPrompt.ShouldBe("Custom prompt text");
        result.IsCustomActive.ShouldBeTrue();
        result.IsAppendixActive.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_WhenAppendixSet_ReturnsIt()
    {
        // Arrange
        await SeedConfigAsync(null, "Extra rules appendix");

        // Act
        var result = await _service.GetAsync();

        // Assert
        result.Appendix.ShouldBe("Extra rules appendix");
        result.IsAppendixActive.ShouldBeTrue();
        result.IsCustomActive.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_WhenBothSet_ReturnsBoth()
    {
        // Arrange
        await SeedConfigAsync("Custom prompt", "Appendix text");

        // Act
        var result = await _service.GetAsync();

        // Assert
        result.CustomPrompt.ShouldBe("Custom prompt");
        result.Appendix.ShouldBe("Appendix text");
        result.IsCustomActive.ShouldBeTrue();
        result.IsAppendixActive.ShouldBeTrue();
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_SetsCustomPrompt()
    {
        // Arrange — start with no custom prompt
        await SeedConfigAsync(null, null);
        var dto = new UpdateAiInstructionsDto { CustomPrompt = "New custom prompt" };

        // Act
        var result = await _service.UpdateAsync(dto);

        // Assert — field saved
        result.CustomPrompt.ShouldBe("New custom prompt");
        result.IsCustomActive.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateAsync_EmptyCustomPrompt_ClearsIt()
    {
        // Arrange — start with a custom prompt
        await SeedConfigAsync("Old prompt", null);
        var dto = new UpdateAiInstructionsDto { CustomPrompt = "" }; // empty = clear

        // Act
        var result = await _service.UpdateAsync(dto);

        // Assert — field cleared to null
        result.CustomPrompt.ShouldBeNull();
        result.IsCustomActive.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateAsync_NullCustomPrompt_KeepsExisting()
    {
        // Arrange — start with a custom prompt
        await SeedConfigAsync("Existing prompt", null);
        var dto = new UpdateAiInstructionsDto { CustomPrompt = null }; // null = keep

        // Act
        var result = await _service.UpdateAsync(dto);

        // Assert — existing value preserved
        result.CustomPrompt.ShouldBe("Existing prompt");
    }

    [Fact]
    public async Task UpdateAsync_SetsAppendix()
    {
        // Arrange
        await SeedConfigAsync(null, null);
        var dto = new UpdateAiInstructionsDto { Appendix = "Extra rules" };

        // Act
        var result = await _service.UpdateAsync(dto);

        // Assert
        result.Appendix.ShouldBe("Extra rules");
        result.IsAppendixActive.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateAsync_InvalidatesCacheOnSave()
    {
        // Arrange — pre-populate cache with a value
        await SeedConfigAsync("Old custom", null);
        _cache.Set(AiInstructionsService.CacheKey, ("Old custom", (string?)null));

        var dto = new UpdateAiInstructionsDto { CustomPrompt = "New custom" };

        // Act — update should remove the cache entry
        await _service.UpdateAsync(dto);

        // Assert — cache entry removed; next call will re-read from DB
        _cache.TryGetValue(AiInstructionsService.CacheKey, out _).ShouldBeFalse();
    }

    // ── ResetToDefaultAsync ───────────────────────────────────────────────

    [Fact]
    public async Task ResetToDefaultAsync_ClearsBothFields()
    {
        // Arrange — both fields set
        await SeedConfigAsync("Custom prompt", "Appendix rules");

        // Act
        var result = await _service.ResetToDefaultAsync();

        // Assert — both cleared
        result.CustomPrompt.ShouldBeNull();
        result.Appendix.ShouldBeNull();
        result.IsCustomActive.ShouldBeFalse();
        result.IsAppendixActive.ShouldBeFalse();
    }

    [Fact]
    public async Task ResetToDefaultAsync_InvalidatesCache()
    {
        // Arrange — pre-populate cache
        await SeedConfigAsync("Custom", "Appendix");
        _cache.Set(AiInstructionsService.CacheKey, ("Custom", "Appendix"));

        // Act
        await _service.ResetToDefaultAsync();

        // Assert — cache cleared
        _cache.TryGetValue(AiInstructionsService.CacheKey, out _).ShouldBeFalse();
    }

    // ── GetCachedInstructionsAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetCachedInstructionsAsync_WhenCacheMiss_ReadsFromDb()
    {
        // Arrange — DB has values, cache is empty
        await SeedConfigAsync("DB custom", "DB appendix");

        // Act — first call hits the DB
        var (custom, appendix) = await _service.GetCachedInstructionsAsync();

        // Assert — values from DB returned
        custom.ShouldBe("DB custom");
        appendix.ShouldBe("DB appendix");
    }

    [Fact]
    public async Task GetCachedInstructionsAsync_WhenCacheHit_ReturnsCachedValue()
    {
        // Arrange — pre-populate cache with known values
        _cache.Set(AiInstructionsService.CacheKey, ("Cached custom", "Cached appendix"));

        // Act — call should return cache hit without going to DB
        var (custom, appendix) = await _service.GetCachedInstructionsAsync();

        // Assert — cached values returned (not from DB)
        custom.ShouldBe("Cached custom");
        appendix.ShouldBe("Cached appendix");
    }

    [Fact]
    public async Task GetCachedInstructionsAsync_PopulatesCacheForNextCall()
    {
        // Arrange — DB has values, cache is empty
        await SeedConfigAsync("Prompt for caching", null);

        // Act — first call populates cache
        await _service.GetCachedInstructionsAsync();

        // Assert — cache is now populated
        _cache.TryGetValue(AiInstructionsService.CacheKey, out (string? c, string? a) cached)
            .ShouldBeTrue();
        cached.c.ShouldBe("Prompt for caching");
    }

    // ── GetPreviewAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPreviewAsync_ContainsPlaceholderBusinessContext()
    {
        // Act — preview runs without tenant DB
        var preview = await _service.GetPreviewAsync();

        // Assert — placeholder values in business context section
        preview.FullPrompt.ShouldContain("[N/A — preview mode]");
        preview.FullPrompt.ShouldContain("Current tenant business context");
    }

    [Fact]
    public async Task GetPreviewAsync_WithCustomPrompt_ShowsCustomSection()
    {
        // Arrange
        await SeedConfigAsync("CUSTOM: My special rules.", null);

        // Act
        var preview = await _service.GetPreviewAsync();

        // Assert — custom instructions marker present
        preview.FullPrompt.ShouldContain("CUSTOM INSTRUCTIONS");
        preview.FullPrompt.ShouldContain("CUSTOM: My special rules.");
    }

    [Fact]
    public async Task GetPreviewAsync_WithoutCustomPrompt_ShowsDefaultMarker()
    {
        // Arrange — no custom prompt
        await SeedConfigAsync(null, null);

        // Act
        var preview = await _service.GetPreviewAsync();

        // Assert — default instructions shown (hardcoded text)
        preview.FullPrompt.ShouldContain("RESPONSE STYLE");
    }

    [Fact]
    public async Task GetPreviewAsync_WithAppendix_ShowsAppendixSection()
    {
        // Arrange
        await SeedConfigAsync(null, "APPENDIX: Extra company rules.");

        // Act
        var preview = await _service.GetPreviewAsync();

        // Assert — appendix marker present
        preview.FullPrompt.ShouldContain("ADDITIONAL INSTRUCTIONS");
        preview.FullPrompt.ShouldContain("APPENDIX: Extra company rules.");
    }
}
