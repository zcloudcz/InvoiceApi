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
/// Tests that the system prompt is built correctly with business data from the tenant database,
/// and that custom AI instructions (from IAiInstructionsService) are correctly integrated.
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

        // Default: no custom prompt, no appendix (uses hardcoded defaults)
        _aiInstructions = Substitute.For<IAiInstructionsService>();
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns(((string?)null, (string?)null));

        _builder = new ChatContextBuilder(_context, _aiInstructions, _logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ── Existing tests updated for new constructor ─────────────────────────

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

    // ── New tests for custom AI instructions support ───────────────────────

    [Fact]
    public async Task BuildSystemPrompt_WithNullCustom_UsesHardcodedDefault()
    {
        // Arrange — no custom prompt (returns (null, null))
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns(((string?)null, (string?)null));

        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — hardcoded default sections should be present
        prompt.ShouldContain("RESPONSE STYLE");
        prompt.ShouldContain("IMPORT RULES");
        prompt.ShouldContain("TOOLS");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithEmptyCustom_UsesHardcodedDefault()
    {
        // Arrange — empty string also falls back to default (same as null)
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns((string.Empty, (string?)null));

        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — hardcoded default sections should be present
        prompt.ShouldContain("RESPONSE STYLE");
        prompt.ShouldContain("IMPORT RULES");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithCustomPrompt_ReplacesHardcodedSections()
    {
        // Arrange — SysAdmin has set a custom prompt
        const string customPrompt = "CUSTOM RULES: Be very concise. Only respond in English.";
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns((customPrompt, (string?)null));

        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — custom prompt IS present, hardcoded sections are NOT
        prompt.ShouldContain(customPrompt);
        prompt.ShouldNotContain("RESPONSE STYLE");
        prompt.ShouldNotContain("IMPORT RULES");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithCustomPrompt_StillIncludesAppIdentity()
    {
        // Arrange — custom prompt replaces rules but NOT the app identity line
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns(("Custom rules here.", (string?)null));

        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — app identity always present regardless of custom prompt
        prompt.ShouldContain("Fakvio AI Assistant");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithCustomPrompt_StillIncludesBusinessContext()
    {
        // Arrange — custom prompt replaces rules but NOT business context
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns(("Custom rules only.", (string?)null));

        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — business context always appended at the end
        prompt.ShouldContain("Current tenant business context");
        prompt.ShouldContain("Total active clients: 0");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithAppendix_AppendsAfterMainSections()
    {
        // Arrange — appendix only, no custom prompt → default + appendix
        const string appendix = "EXTRA: Always respond in formal Czech.";
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns(((string?)null, appendix));

        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — appendix is present AND default sections are also present
        prompt.ShouldContain(appendix);
        prompt.ShouldContain("RESPONSE STYLE"); // Default still present
    }

    [Fact]
    public async Task BuildSystemPrompt_WithAppendix_AppendixAppearsBeforeBusinessContext()
    {
        // Arrange — appendix should come before the business context stats
        const string appendix = "EXTRA: Unique appendix marker XYZ123.";
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns(((string?)null, appendix));

        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — appendix before business context in the string
        var appendixPos = prompt.IndexOf(appendix, StringComparison.Ordinal);
        var contextPos = prompt.IndexOf("Current tenant business context", StringComparison.Ordinal);

        appendixPos.ShouldBeGreaterThanOrEqualTo(0);
        contextPos.ShouldBeGreaterThanOrEqualTo(0);
        appendixPos.ShouldBeLessThan(contextPos);
    }

    [Fact]
    public async Task BuildSystemPrompt_WithBothCustomAndAppendix_BothPresent()
    {
        // Arrange — both custom prompt and appendix set
        const string customPrompt = "CUSTOM: Short custom instructions.";
        const string appendix = "APPENDIX: Additional rules here.";
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns((customPrompt, appendix));

        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — both present, hardcoded sections absent
        prompt.ShouldContain(customPrompt);
        prompt.ShouldContain(appendix);
        prompt.ShouldNotContain("RESPONSE STYLE");
        prompt.ShouldNotContain("IMPORT RULES");
    }

    [Fact]
    public async Task BuildSystemPrompt_CallsGetCachedInstructionsAsync()
    {
        // Arrange — verify that the builder asks the cache service, not the DB directly
        _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns(((string?)null, (string?)null));

        // Act
        await _builder.BuildSystemPromptAsync();

        // Assert — cache service was called exactly once
        await _aiInstructions.Received(1).GetCachedInstructionsAsync(Arg.Any<CancellationToken>());
    }
}
