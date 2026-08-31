using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.SystemConfiguration;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for AiInstructionsService — CRUD, cache behaviour and the SysAdmin preview.
///
/// Uses an in-memory MasterDbContext and a real MemoryCache: both are cheap and
/// deterministic, so faking them would only hide behaviour.
/// </summary>
public class AiInstructionsServiceTests : IDisposable
{
    private readonly MasterDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly AiInstructionsService _service;

    public AiInstructionsServiceTests()
    {
        // A unique database name per test instance prevents state leaking between tests.
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: $"AiInstructionsTest_{Guid.NewGuid()}")
            .Options;

        _context = new MasterDbContext(options);
        _cache = new MemoryCache(new MemoryCacheOptions());

        _service = new AiInstructionsService(
            _context,
            _cache,
            [FakeTool()],
            Substitute.For<ILogger<AiInstructionsService>>());
    }

    /// <summary>
    /// One fake chat tool — enough to prove the preview renders the generated catalog
    /// rather than a frozen list. The preview only reads name and description.
    /// </summary>
    private static IChatTool FakeTool()
    {
        var tool = Substitute.For<IChatTool>();
        tool.ToolName.Returns("ares_lookup");
        tool.Description.Returns("Look up a Czech company by IČO");
        return tool;
    }

    /// <summary>
    /// The catalog line <see cref="FakeTool"/> is expected to render. A literal, so the
    /// expected prompt text stays independent of the production code.
    /// </summary>
    private const string FakeToolLine = "- ares_lookup: Look up a Czech company by IČO";

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _cache.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Seeds the single SystemConfiguration row with the given prompt values.</summary>
    private async Task SeedConfigAsync(string? customPrompt, string? appendix)
    {
        _context.Set<SystemConfiguration>().Add(new SystemConfiguration
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
        });
        await _context.SaveChangesAsync();
    }

    // ── GetAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_WhenNoConfigurationRow_ReturnsEmptyInstructions()
    {
        var result = await _service.GetAsync();

        result.ShouldNotBeNull();
        result.CustomPrompt.ShouldBeNull();
        result.Appendix.ShouldBeNull();
        result.IsCustomActive.ShouldBeFalse();
        result.IsAppendixActive.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_WhenNoConfigurationRow_DoesNotCreateOne()
    {
        // A read must never write — otherwise every chat message would INSERT into master.
        await _service.GetAsync();

        (await _context.Set<SystemConfiguration>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task GetAsync_WhenBothPartsStored_ReturnsBoth()
    {
        await SeedConfigAsync("Custom prompt", "Appendix text");

        var result = await _service.GetAsync();

        result.CustomPrompt.ShouldBe("Custom prompt");
        result.Appendix.ShouldBe("Appendix text");
        result.IsCustomActive.ShouldBeTrue();
        result.IsAppendixActive.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAsync_WhenOnlyAppendixStored_MarksOnlyAppendixActive()
    {
        await SeedConfigAsync(null, "Extra rules appendix");

        var result = await _service.GetAsync();

        result.Appendix.ShouldBe("Extra rules appendix");
        result.IsAppendixActive.ShouldBeTrue();
        result.IsCustomActive.ShouldBeFalse();
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_StoresCustomPrompt()
    {
        await SeedConfigAsync(null, null);

        var result = await _service.UpdateAsync(new UpdateAiInstructionsDto { CustomPrompt = "New custom prompt" });

        result.CustomPrompt.ShouldBe("New custom prompt");
        result.IsCustomActive.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateAsync_StoresAppendix()
    {
        await SeedConfigAsync(null, null);

        var result = await _service.UpdateAsync(new UpdateAiInstructionsDto { Appendix = "Extra rules" });

        result.Appendix.ShouldBe("Extra rules");
        result.IsAppendixActive.ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateAsync_EmptyString_ClearsTheValue()
    {
        await SeedConfigAsync("Old prompt", null);

        // Empty string is how the UI reports "the user cleared the box".
        var result = await _service.UpdateAsync(new UpdateAiInstructionsDto { CustomPrompt = "" });

        result.CustomPrompt.ShouldBeNull();
        result.IsCustomActive.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateAsync_WhitespaceOnlyValue_ClearsTheValue()
    {
        await SeedConfigAsync("Old prompt", "Old appendix");

        // Whitespace is not a usable prompt: storing it as "custom" would replace the whole
        // built-in block with an empty line and leave the AI without tools and rules.
        var result = await _service.UpdateAsync(new UpdateAiInstructionsDto
        {
            CustomPrompt = "   ",
            Appendix = "  "
        });

        result.CustomPrompt.ShouldBeNull();
        result.Appendix.ShouldBeNull();
        result.IsCustomActive.ShouldBeFalse();
        result.IsAppendixActive.ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateAsync_Null_KeepsTheStoredValue()
    {
        await SeedConfigAsync("Existing prompt", "Existing appendix");

        // Null means "the caller did not send this field" — a partial update.
        var result = await _service.UpdateAsync(new UpdateAiInstructionsDto { Appendix = "Changed" });

        result.CustomPrompt.ShouldBe("Existing prompt");
        result.Appendix.ShouldBe("Changed");
    }

    [Fact]
    public async Task UpdateAsync_WhenNoConfigurationRow_CreatesIt()
    {
        await _service.UpdateAsync(new UpdateAiInstructionsDto { CustomPrompt = "First ever value" });

        (await _context.Set<SystemConfiguration>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task UpdateAsync_InvalidatesTheCache()
    {
        await SeedConfigAsync("Old custom", null);
        _cache.Set(AiInstructionsService.CacheKey, ("Old custom", (string?)null));

        await _service.UpdateAsync(new UpdateAiInstructionsDto { CustomPrompt = "New custom" });

        _cache.TryGetValue(AiInstructionsService.CacheKey, out _).ShouldBeFalse();
    }

    // ── ResetToDefaultAsync ───────────────────────────────────────────────

    [Fact]
    public async Task ResetToDefaultAsync_ClearsBothParts()
    {
        await SeedConfigAsync("Custom prompt", "Appendix rules");

        var result = await _service.ResetToDefaultAsync();

        result.CustomPrompt.ShouldBeNull();
        result.Appendix.ShouldBeNull();

        // The values are gone from the database too, not just from the returned DTO.
        var stored = await _context.Set<SystemConfiguration>().SingleAsync();
        stored.AiSystemPromptCustom.ShouldBeNull();
        stored.AiSystemPromptAppendix.ShouldBeNull();
    }

    [Fact]
    public async Task ResetToDefaultAsync_InvalidatesTheCache()
    {
        await SeedConfigAsync("Custom", "Appendix");
        _cache.Set(AiInstructionsService.CacheKey, ("Custom", "Appendix"));

        await _service.ResetToDefaultAsync();

        _cache.TryGetValue(AiInstructionsService.CacheKey, out _).ShouldBeFalse();
    }

    // ── GetCachedInstructionsAsync ────────────────────────────────────────

    [Fact]
    public async Task GetCachedInstructionsAsync_OnCacheMiss_ReadsFromDatabase()
    {
        await SeedConfigAsync("DB custom", "DB appendix");

        var (custom, appendix) = await _service.GetCachedInstructionsAsync();

        custom.ShouldBe("DB custom");
        appendix.ShouldBe("DB appendix");
    }

    [Fact]
    public async Task GetCachedInstructionsAsync_OnCacheHit_DoesNotReadTheDatabase()
    {
        // The cached value deliberately differs from the stored one — if the service
        // hit the database, the assertion below would fail.
        await SeedConfigAsync("DB custom", "DB appendix");
        _cache.Set(AiInstructionsService.CacheKey, ("Cached custom", "Cached appendix"));

        var (custom, appendix) = await _service.GetCachedInstructionsAsync();

        custom.ShouldBe("Cached custom");
        appendix.ShouldBe("Cached appendix");
    }

    [Fact]
    public async Task GetCachedInstructionsAsync_PopulatesTheCache()
    {
        await SeedConfigAsync("Prompt for caching", null);

        await _service.GetCachedInstructionsAsync();

        _cache.TryGetValue(AiInstructionsService.CacheKey, out (string? Custom, string? Appendix) cached)
            .ShouldBeTrue();
        cached.Custom.ShouldBe("Prompt for caching");
    }

    [Fact]
    public async Task GetCachedInstructionsAsync_WhenNoConfigurationRow_DoesNotCreateOne()
    {
        await _service.GetCachedInstructionsAsync();

        (await _context.Set<SystemConfiguration>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task GetCachedInstructionsAsync_DropsTheEntry_AfterTheAbsoluteExpiry()
    {
        // IMemoryCache is process-local: a SysAdmin edit served by another instance never
        // invalidates this one. The only guarantee that the stale value goes away is the
        // absolute expiry — a sliding one would be renewed by the traffic and never fire.
        var clock = new TestClock();
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var service = new AiInstructionsService(
            _context, cache, [FakeTool()], Substitute.For<ILogger<AiInstructionsService>>());

        await SeedConfigAsync("Original prompt", null);
        (await service.GetCachedInstructionsAsync()).CustomPrompt.ShouldBe("Original prompt");

        // Another instance rewrites the row; this process gets no notification.
        var stored = await _context.Set<SystemConfiguration>().SingleAsync();
        stored.AiSystemPromptCustom = "Changed elsewhere";
        await _context.SaveChangesAsync();

        // Chat traffic every four minutes keeps a sliding entry alive forever.
        clock.UtcNow = clock.UtcNow.AddMinutes(4);
        (await service.GetCachedInstructionsAsync()).CustomPrompt.ShouldBe("Original prompt");

        // Six minutes after it was cached the entry must be gone regardless of the traffic.
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        (await service.GetCachedInstructionsAsync()).CustomPrompt.ShouldBe("Changed elsewhere");
    }

    /// <summary>
    /// Drives MemoryCache expiry from the test instead of from the wall clock, so the
    /// five-minute bound can be verified without the test sleeping for five minutes.
    /// </summary>
    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    // ── GetPreviewAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPreviewAsync_ShowsPlaceholdersForTenantData()
    {
        var preview = await _service.GetPreviewAsync();

        preview.FullPrompt.ShouldContain(AiSystemPrompt.BusinessContextHeader);
        preview.FullPrompt.ShouldContain(AiSystemPrompt.PreviewPlaceholder);
    }

    [Fact]
    public async Task GetPreviewAsync_ShowsTheSituationalBlock()
    {
        // The SysAdmin writes a custom prompt against the whole layout. The situational block
        // is runtime-only, so the preview has to show its shape with placeholders — otherwise
        // the last section of the real prompt is invisible to the person replacing it.
        var preview = await _service.GetPreviewAsync();

        preview.FullPrompt.ShouldContain(AiSystemPrompt.SituationalContextHeader);
        preview.FullPrompt.ShouldContain($"- Today's date: {AiSystemPrompt.PreviewPlaceholder}");
        preview.FullPrompt.ShouldContain($"- Current page: {AiSystemPrompt.PreviewPlaceholder}");
        preview.FullPrompt.ShouldContain($"- Open record: {AiSystemPrompt.PreviewPlaceholder}");
        preview.FullPrompt.ShouldContain($"- Setup not finished yet: {AiSystemPrompt.PreviewPlaceholder}");
    }

    [Fact]
    public async Task GetPreviewAsync_WithoutCustomPrompt_ShowsTheBuiltInBlock()
    {
        await SeedConfigAsync(null, null);

        var preview = await _service.GetPreviewAsync();

        // The preview must show the real built-in text, not a "see the source code" stub.
        preview.FullPrompt.ShouldContainBuiltInMainBlock(FakeToolLine);
    }

    [Fact]
    public async Task GetPreviewAsync_IgnoresTheCache_AndShowsTheStoredText()
    {
        await SeedConfigAsync("Stored in the database", null);

        // A cache entry from before the last write (or written by another instance) must
        // not leak into the preview — the SysAdmin has to see what is actually stored.
        _cache.Set(AiInstructionsService.CacheKey, ("Stale cached prompt", (string?)null));

        var preview = await _service.GetPreviewAsync();

        preview.FullPrompt.ShouldContain("Stored in the database");
        preview.FullPrompt.ShouldNotContain("Stale cached prompt");
    }

    [Fact]
    public async Task GetPreviewAsync_WithCustomPrompt_ReplacesTheBuiltInBlock()
    {
        await SeedConfigAsync("CUSTOM: My special rules.", null);

        var preview = await _service.GetPreviewAsync();

        preview.FullPrompt.ShouldContain("CUSTOM: My special rules.");
        preview.FullPrompt.ShouldNotContain("RESPONSE STYLE");
    }

    [Fact]
    public async Task GetPreviewAsync_WithWhitespaceOnlyPrompt_StillShowsTheBuiltInBlock()
    {
        // Defence in depth for rows written before NullIfBlank existed, or edited in SQL.
        await SeedConfigAsync("   ", null);

        var preview = await _service.GetPreviewAsync();

        preview.FullPrompt.ShouldContainBuiltInMainBlock(FakeToolLine);
    }

    [Fact]
    public async Task GetPreviewAsync_WithAppendix_ShowsBuiltInBlockAndAppendix()
    {
        await SeedConfigAsync(null, "APPENDIX: Extra company rules.");

        var preview = await _service.GetPreviewAsync();

        preview.FullPrompt.ShouldContain("APPENDIX: Extra company rules.");
        preview.FullPrompt.ShouldContain("RESPONSE STYLE");
    }
}
