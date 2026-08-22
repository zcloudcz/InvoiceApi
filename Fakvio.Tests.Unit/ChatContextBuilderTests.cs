using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ChatContextBuilder.
/// Tests that the system prompt is built correctly with business data from the tenant
/// database, and that the SysAdmin-editable instructions are placed correctly.
/// </summary>
public class ChatContextBuilderTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly IAiInstructionsService _aiInstructions;
    private readonly ChatContextBuilder _builder;
    private readonly ILogger<ChatContextBuilder> _logger;

    public ChatContextBuilderTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<ChatContextBuilder>>();

        // Default for every test: nothing stored, so the built-in block applies.
        _aiInstructions = Substitute.For<IAiInstructionsService>();
        StoredInstructions(null, null);

        _builder = new ChatContextBuilder(_context, _aiInstructions, _logger);
    }

    /// <summary>Sets what the (faked) instructions service returns to the builder.</summary>
    private void StoredInstructions(string? customPrompt, string? appendix)
        => _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns((customPrompt, appendix));

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Fact]
    public async Task BuildSystemPrompt_ContainsAppIdentity()
    {
        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — should identify as Fakvio assistant.
        prompt.ShouldContain("Fakvio AI Assistant");
        prompt.ShouldContain("invoicing");
    }

    [Fact]
    public async Task BuildSystemPrompt_IncludesBusinessData()
    {
        // Arrange — add some test data.
        var issuer = new Client
        {
            CompanyName = "Issuer Co",
            RegistrationNumber = "12345678",
            IsIssuer = true,
            IsActive = true
        };
        _context.Client.Add(issuer);
        await _context.SaveChangesAsync();

        var customer = new Client
        {
            CompanyName = "Customer Co",
            RegistrationNumber = "87654321",
            IsIssuer = false,
            IsActive = true
        };
        _context.Client.Add(customer);
        await _context.SaveChangesAsync();

        var currency = new Currency
        {
            Code = "CZK",
            Name = "Czech Crown",
            Symbol = "Kč",
            DecimalPlaces = 2,
            IsActive = true,
            SortOrder = 1
        };
        _context.Currency.Add(currency);
        await _context.SaveChangesAsync();

        var invoice = new Invoice
        {
            DocumentNumber = "INV-001",
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            IssuerId = issuer.Id,
            Issuer = issuer,
            ClientId = customer.Id,
            TotalWithVat = 10000m,
            CurrencyId = currency.Id,
            IssueDate = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(-5), // Overdue
            InvoiceItem = new List<InvoiceItem>()
        };
        _context.Invoice.Add(invoice);
        await _context.SaveChangesAsync();

        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — should contain aggregated stats.
        prompt.ShouldContain("Total active clients: 1");
        prompt.ShouldContain("Open (unpaid) invoices: 1");
        prompt.ShouldContain("Overdue invoices: 1");
    }

    [Fact]
    public async Task BuildSystemPrompt_HandlesEmptyTenant()
    {
        // Act — no data in the database.
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — should still return a valid prompt with zero counts.
        prompt.ShouldContain("Total active clients: 0");
        prompt.ShouldContain("Open (unpaid) invoices: 0");
        prompt.ShouldContain("Overdue invoices: 0");
    }

    // ── SysAdmin-editable instructions ────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BuildSystemPrompt_WithoutCustomPrompt_UsesTheBuiltInBlock(string? customPrompt)
    {
        // "Never set" (null), "cleared by the user" (empty) and a value that is only
        // whitespace all fall back to the built-in block — blanks must not blank the prompt.
        StoredInstructions(customPrompt, null);

        var prompt = await _builder.BuildSystemPromptAsync();

        prompt.ShouldContainBuiltInMainBlock();
    }

    [Fact]
    public async Task BuildSystemPrompt_WithCustomPrompt_ReplacesTheBuiltInBlock()
    {
        const string customPrompt = "CUSTOM RULES: Be very concise. Only respond in English.";
        StoredInstructions(customPrompt, null);

        var prompt = await _builder.BuildSystemPromptAsync();

        prompt.ShouldContain(customPrompt);
        prompt.ShouldNotContain("RESPONSE STYLE");
        prompt.ShouldNotContain("IMPORT RULES");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithCustomPrompt_KeepsIdentityAndBusinessContext()
    {
        // A custom prompt may replace the rules, but never the app-generated parts.
        StoredInstructions("Custom rules only.", null);

        var prompt = await _builder.BuildSystemPromptAsync();

        prompt.ShouldContain(AiSystemPrompt.Identity);
        prompt.ShouldContain(AiSystemPrompt.BusinessContextHeader);
        prompt.ShouldContain("Total active clients: 0");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithAppendix_KeepsTheBuiltInBlock()
    {
        const string appendix = "EXTRA: Always respond in formal Czech.";
        StoredInstructions(null, appendix);

        var prompt = await _builder.BuildSystemPromptAsync();

        prompt.ShouldContain(appendix);
        prompt.ShouldContainBuiltInMainBlock();
    }

    [Fact]
    public async Task BuildSystemPrompt_WithAppendix_PlacesItBeforeTheBusinessContext()
    {
        // Order matters: the statistics block must stay last so the AI reads it as data.
        const string appendix = "EXTRA: Unique appendix marker XYZ123.";
        StoredInstructions(null, appendix);

        var prompt = await _builder.BuildSystemPromptAsync();

        var appendixPosition = prompt.IndexOf(appendix, StringComparison.Ordinal);
        var contextPosition = prompt.IndexOf(AiSystemPrompt.BusinessContextHeader, StringComparison.Ordinal);

        appendixPosition.ShouldBeGreaterThanOrEqualTo(0);
        contextPosition.ShouldBeGreaterThanOrEqualTo(0);
        appendixPosition.ShouldBeLessThan(contextPosition);
    }

    [Fact]
    public async Task BuildSystemPrompt_WithCustomPromptAndAppendix_ContainsBoth()
    {
        const string customPrompt = "CUSTOM: Short custom instructions.";
        const string appendix = "APPENDIX: Additional rules here.";
        StoredInstructions(customPrompt, appendix);

        var prompt = await _builder.BuildSystemPromptAsync();

        prompt.ShouldContain(customPrompt);
        prompt.ShouldContain(appendix);
        prompt.ShouldNotContain("RESPONSE STYLE");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithWhitespaceOnlyAppendix_MatchesThePromptWithoutOne()
    {
        StoredInstructions(null, null);
        var withoutAppendix = await _builder.BuildSystemPromptAsync();

        StoredInstructions(null, "   ");
        var withBlankAppendix = await _builder.BuildSystemPromptAsync();

        // A blank appendix must not push an empty section between the rules and the stats.
        withBlankAppendix.ShouldBe(withoutAppendix);
    }

    [Fact]
    public async Task BuildSystemPrompt_ReadsInstructionsThroughTheCache()
    {
        // The hot path must go through the cached accessor — one call per prompt, no more.
        await _builder.BuildSystemPromptAsync();

        await _aiInstructions.Received(1).GetCachedInstructionsAsync(Arg.Any<CancellationToken>());
    }
}
