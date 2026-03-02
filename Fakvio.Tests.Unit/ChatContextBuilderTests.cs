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
/// Tests that the system prompt is built correctly with business data from the tenant database.
/// </summary>
public class ChatContextBuilderTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ChatContextBuilder _builder;
    private readonly ILogger<ChatContextBuilder> _logger;

    public ChatContextBuilderTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<ChatContextBuilder>>();
        _builder = new ChatContextBuilder(_context, _logger);
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
}
