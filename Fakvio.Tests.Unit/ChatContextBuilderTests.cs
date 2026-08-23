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

        // The capability list in the prompt is generated from the registered tools,
        // so the builder needs them — one fake tool is enough to prove the wiring.
        var tool = Substitute.For<IChatTool>();
        tool.ToolName.Returns("ares_lookup");
        tool.Description.Returns("Look up a Czech company by IČO");

        // Default for every test: nothing stored, so the built-in block applies.
        _aiInstructions = Substitute.For<IAiInstructionsService>();
        StoredInstructions(null, null);

        _builder = new ChatContextBuilder(_context, [tool], _aiInstructions, _logger);
    }

    /// <summary>
    /// The tool catalog the fake tool above produces in the built-in block. Spelled out as
    /// a literal so the expected prompt text stays independent of the production code.
    /// </summary>
    private const string FakeToolLine = "- ares_lookup: Look up a Czech company by IČO";

    /// <summary>Sets what the (faked) instructions service returns to the builder.</summary>
    private void StoredInstructions(string? customPrompt, string? appendix)
        => _aiInstructions
            .GetCachedInstructionsAsync(Arg.Any<CancellationToken>())
            .Returns((customPrompt, appendix));

    /// <summary>Seeds the tenant's own company (the issuer) — the source of the company block.</summary>
    private async Task SeedIssuerAsync(string companyName, string registrationNumber, string? taxNumber)
    {
        _context.Client.Add(new Client
        {
            CompanyName = companyName,
            RegistrationNumber = registrationNumber,
            TaxNumber = taxNumber,
            IsIssuer = true,
            IsActive = true
        });
        await _context.SaveChangesAsync();
    }

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
    public async Task BuildSystemPrompt_ListsRegisteredToolsFromTheirOwnMetadata()
    {
        // Act
        var prompt = await _builder.BuildSystemPromptAsync();

        // Assert — no hand-written tool catalog: name and description come from the tool itself.
        prompt.ShouldContain("- ares_lookup: Look up a Czech company by IČO");
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

    // ── Company identity block ────────────────────────────────────────────

    [Fact]
    public async Task BuildSystemPrompt_WithIssuer_StatesWhichCompanyIsUs()
    {
        // import_invoice decides "issued vs received" by matching IČO against this block.
        // If any of these lines goes missing, the AI files incoming invoices as outgoing.
        await SeedIssuerAsync("Issuer Co", "12345678", taxNumber: null);

        var prompt = await _builder.BuildSystemPromptAsync();

        prompt.ShouldContain("YOUR COMPANY (the user's company — you represent this entity):");
        prompt.ShouldContain("- Name: Issuer Co");
        prompt.ShouldContain("- IČO: 12345678");
        prompt.ShouldContain(
            "When importing invoices: if YOUR IČO appears as the issuer (dodavatel), it's an ISSUED invoice.");
        prompt.ShouldContain(
            "If YOUR IČO appears as the recipient (odběratel), it's a RECEIVED invoice.");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithVatRegisteredIssuer_IncludesTheTaxNumber()
    {
        await SeedIssuerAsync("Issuer Co", "12345678", taxNumber: "CZ12345678");

        var prompt = await _builder.BuildSystemPromptAsync();

        prompt.ShouldContain("- DIČ: CZ12345678");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithIssuerWithoutTaxNumber_OmitsTheDicLineEntirely()
    {
        // A non-VAT-payer has no DIČ; an empty "- DIČ: " line would invite the AI to
        // invent one when it fills in an imported invoice.
        await SeedIssuerAsync("Small Trader", "87654321", taxNumber: null);

        var prompt = await _builder.BuildSystemPromptAsync();

        prompt.ShouldNotContain("- DIČ:");
    }

    [Fact]
    public async Task BuildSystemPrompt_WithoutIssuer_OmitsTheCompanyBlock()
    {
        // A tenant that has not configured its own company yet still gets a usable prompt,
        // just without the "this is us" section — no placeholders, no empty labels.
        var prompt = await _builder.BuildSystemPromptAsync();

        prompt.ShouldNotContain("YOUR COMPANY");
        prompt.ShouldContain(AiSystemPrompt.Identity);
        prompt.ShouldContainBuiltInMainBlock(FakeToolLine);
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

        prompt.ShouldContainBuiltInMainBlock(FakeToolLine);
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
        prompt.ShouldContainBuiltInMainBlock(FakeToolLine);
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
