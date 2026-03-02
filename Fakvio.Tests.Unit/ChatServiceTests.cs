using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Chat;
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
/// Unit tests for ChatService.
/// Tests conversation CRUD, message sending, streaming, and auto-title generation.
/// Uses InMemoryDatabase for isolated test data and NSubstitute for AI provider mocking.
/// </summary>
public class ChatServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ChatService _service;
    private readonly IAiProviderFactory _providerFactory;
    private readonly IAiProvider _mockProvider;
    private readonly IChatContextBuilder _contextBuilder;
    private readonly IChatToolExecutor _toolExecutor;
    private readonly ILogger<ChatService> _logger;
    private const long TestUserId = 42;

    public ChatServiceTests()
    {
        // Fresh in-memory database for each test.
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<ChatService>>();

        // Mock AI provider — returns a fixed response.
        _mockProvider = Substitute.For<IAiProvider>();
        _mockProvider.ProviderName.Returns("TestProvider");
        _mockProvider
            .GetCompletionAsync(Arg.Any<List<ChatMessageDto>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("AI response text");

        // Mock provider factory — always returns the test provider.
        _providerFactory = Substitute.For<IAiProviderFactory>();
        _providerFactory.GetDefaultProvider().Returns(_mockProvider);
        _providerFactory.GetProvider(Arg.Any<string>()).Returns(_mockProvider);
        _providerFactory.AvailableProviders.Returns(new List<string> { "TestProvider" });

        // Mock context builder — returns a simple system prompt.
        _contextBuilder = Substitute.For<IChatContextBuilder>();
        _contextBuilder
            .BuildSystemPromptAsync(Arg.Any<CancellationToken>())
            .Returns("You are a test assistant.");

        // Mock tool executor — by default, no tool intent is detected.
        // This ensures all existing tests pass unchanged (regular chat flow).
        _toolExecutor = Substitute.For<IChatToolExecutor>();
        _toolExecutor.DetectToolIntent(Arg.Any<string>()).Returns(false);

        _service = new ChatService(_context, _providerFactory, _contextBuilder, _toolExecutor, _logger);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── SendMessageAsync Tests ──────────────────────────────────────────

    [Fact]
    public async Task SendMessage_CreatesNewConversation_WhenConversationIdIsNull()
    {
        // Arrange
        var request = new SendMessageRequest { Message = "Hello AI", ConversationId = null };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert
        result.ConversationId.ShouldBeGreaterThan(0);
        result.AssistantMessage.ShouldNotBeNull();
        result.AssistantMessage.Content.ShouldBe("AI response text");
        result.AssistantMessage.ProviderUsed.ShouldBe("TestProvider");

        // Verify conversation was saved.
        var conversation = await _context.ChatConversation
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == result.ConversationId);
        conversation.ShouldNotBeNull();
        conversation.UserId.ShouldBe(TestUserId);
        conversation.Messages.Count.ShouldBe(2); // User + Assistant
    }

    [Fact]
    public async Task SendMessage_SavesBothUserAndAssistantMessages()
    {
        // Arrange
        var request = new SendMessageRequest { Message = "Test message" };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert
        var messages = await _context.ChatMessage
            .Where(m => m.ConversationId == result.ConversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        messages.Count.ShouldBe(2);
        messages[0].Role.ShouldBe(EChatRole.User);
        messages[0].Content.ShouldBe("Test message");
        messages[1].Role.ShouldBe(EChatRole.Assistant);
        messages[1].Content.ShouldBe("AI response text");
        messages[1].ProviderUsed.ShouldBe("TestProvider");
    }

    [Fact]
    public async Task SendMessage_ReusesExistingConversation()
    {
        // Arrange — create an initial conversation.
        var firstResult = await _service.SendMessageAsync(TestUserId,
            new SendMessageRequest { Message = "First message" });

        // Act — send another message to the same conversation.
        var secondResult = await _service.SendMessageAsync(TestUserId,
            new SendMessageRequest { Message = "Second message", ConversationId = firstResult.ConversationId });

        // Assert — same conversation, 4 messages total (2 user + 2 assistant).
        secondResult.ConversationId.ShouldBe(firstResult.ConversationId);

        var messages = await _context.ChatMessage
            .Where(m => m.ConversationId == firstResult.ConversationId)
            .ToListAsync();
        messages.Count.ShouldBe(4);
    }

    [Fact]
    public async Task SendMessage_AutoGeneratesTitle_FromFirstMessage()
    {
        // Arrange
        var request = new SendMessageRequest { Message = "How do I create an invoice for my client?" };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — title should be first 50 chars + "..."
        var conversation = await _context.ChatConversation.FindAsync(result.ConversationId);
        conversation.ShouldNotBeNull();
        conversation.Title.ShouldStartWith("How do I create an invoice");
    }

    [Fact]
    public async Task SendMessage_ShortMessage_TitleNotTruncated()
    {
        // Arrange
        var request = new SendMessageRequest { Message = "Hello" };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert
        var conversation = await _context.ChatConversation.FindAsync(result.ConversationId);
        conversation.ShouldNotBeNull();
        conversation.Title.ShouldBe("Hello");
    }

    [Fact]
    public async Task SendMessage_ThrowsForNonExistentConversation()
    {
        // Arrange
        var request = new SendMessageRequest { Message = "Test", ConversationId = 9999 };

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.SendMessageAsync(TestUserId, request));
    }

    [Fact]
    public async Task SendMessage_ThrowsForWrongUser()
    {
        // Arrange — create conversation for user 42.
        var result = await _service.SendMessageAsync(TestUserId,
            new SendMessageRequest { Message = "My conversation" });

        // Act & Assert — user 99 tries to use the same conversation.
        var request = new SendMessageRequest { Message = "Hijack", ConversationId = result.ConversationId };
        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.SendMessageAsync(99, request));
    }

    // ─── GetConversationsAsync Tests ─────────────────────────────────────

    [Fact]
    public async Task GetConversations_ReturnsOnlyUserConversations()
    {
        // Arrange — create conversations for two different users.
        await _service.SendMessageAsync(TestUserId, new SendMessageRequest { Message = "User 42 conv" });
        await _service.SendMessageAsync(99, new SendMessageRequest { Message = "User 99 conv" });

        // Act
        var conversations = await _service.GetConversationsAsync(TestUserId);

        // Assert — only user 42's conversation.
        conversations.Count.ShouldBe(1);
        conversations[0].Title.ShouldContain("User 42");
    }

    [Fact]
    public async Task GetConversations_OrderedByMostRecentFirst()
    {
        // Arrange
        await _service.SendMessageAsync(TestUserId, new SendMessageRequest { Message = "First" });
        await Task.Delay(10); // Ensure different timestamps.
        await _service.SendMessageAsync(TestUserId, new SendMessageRequest { Message = "Second" });

        // Act
        var conversations = await _service.GetConversationsAsync(TestUserId);

        // Assert
        conversations.Count.ShouldBe(2);
        conversations[0].Title.ShouldBe("Second");
        conversations[1].Title.ShouldBe("First");
    }

    // ─── GetConversationAsync Tests ──────────────────────────────────────

    [Fact]
    public async Task GetConversation_ReturnsWithMessages()
    {
        // Arrange
        var sendResult = await _service.SendMessageAsync(TestUserId,
            new SendMessageRequest { Message = "Test" });

        // Act
        var conversation = await _service.GetConversationAsync(sendResult.ConversationId, TestUserId);

        // Assert
        conversation.ShouldNotBeNull();
        conversation.Messages.Count.ShouldBe(2); // User + Assistant
        conversation.Messages[0].Role.ShouldBe("User");
        conversation.Messages[1].Role.ShouldBe("Assistant");
    }

    // ─── DeleteConversationAsync Tests ───────────────────────────────────

    [Fact]
    public async Task DeleteConversation_RemovesConversation()
    {
        // Arrange
        var result = await _service.SendMessageAsync(TestUserId,
            new SendMessageRequest { Message = "To be deleted" });

        // Act
        await _service.DeleteConversationAsync(result.ConversationId, TestUserId);

        // Assert
        var conversation = await _context.ChatConversation.FindAsync(result.ConversationId);
        conversation.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteConversation_ThrowsForWrongUser()
    {
        // Arrange
        var result = await _service.SendMessageAsync(TestUserId,
            new SendMessageRequest { Message = "Mine" });

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.DeleteConversationAsync(result.ConversationId, 99));
    }

    // ─── GetAvailableProviders Tests ─────────────────────────────────────

    [Fact]
    public void GetAvailableProviders_ReturnsProviderList()
    {
        var providers = _service.GetAvailableProviders();
        providers.Count.ShouldBe(1);
        providers[0].ShouldBe("TestProvider");
    }

    // ─── Tool Flow Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_UsesTwoPassFlow_WhenToolIntentDetected()
    {
        // Arrange — configure tool executor to detect intent and return a tool call.
        _toolExecutor.DetectToolIntent(Arg.Any<string>()).Returns(true);
        _toolExecutor.BuildToolInstructions().Returns("\nTOOLS: ...");
        _toolExecutor.ParseToolCall(Arg.Any<string>()).Returns(
            new ParsedToolCall
            {
                Action = "ares_lookup",
                Parameters = new Dictionary<string, string> { ["registration_number"] = "12345678" }
            });
        _toolExecutor.ExecuteToolAsync(Arg.Any<ParsedToolCall>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("Company found: Test s.r.o."));

        // Second AI call (with tool results) returns the final user-facing response.
        _mockProvider
            .GetCompletionAsync(Arg.Any<List<ChatMessageDto>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("AI response text", "Found company Test s.r.o. in ARES registry.");

        var request = new SendMessageRequest { Message = "Najdi firmu IČO 12345678" };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — response should contain the second-pass AI response.
        result.AssistantMessage.Content.ShouldBe("Found company Test s.r.o. in ARES registry.");

        // Verify the tool executor was called in the correct sequence.
        _toolExecutor.Received(1).DetectToolIntent("Najdi firmu IČO 12345678");
        _toolExecutor.Received(1).BuildToolInstructions();
        _toolExecutor.Received(1).ParseToolCall("AI response text");
        await _toolExecutor.Received(1).ExecuteToolAsync(
            Arg.Is<ParsedToolCall>(tc => tc.Action == "ares_lookup"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessage_FallsBackToRegularFlow_WhenToolCallNotParsed()
    {
        // Arrange — intent detected but AI doesn't produce a tool call.
        _toolExecutor.DetectToolIntent(Arg.Any<string>()).Returns(true);
        _toolExecutor.BuildToolInstructions().Returns("\nTOOLS: ...");
        _toolExecutor.ParseToolCall(Arg.Any<string>()).Returns((ParsedToolCall?)null);

        var request = new SendMessageRequest { Message = "Firma 12345678 info" };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — should use the first-pass response directly (no tool execution).
        result.AssistantMessage.Content.ShouldBe("AI response text");

        // Tool executor should NOT have executed any tool.
        await _toolExecutor.DidNotReceive().ExecuteToolAsync(
            Arg.Any<ParsedToolCall>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessage_SkipsToolFlow_WhenNoToolIntentDetected()
    {
        // Arrange — no tool intent (default mock already returns false).
        var request = new SendMessageRequest { Message = "Kolik mám faktur?" };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — regular flow, no tool involvement.
        result.AssistantMessage.Content.ShouldBe("AI response text");

        // BuildToolInstructions should NOT be called when no intent detected.
        _toolExecutor.DidNotReceive().BuildToolInstructions();
    }

    // ─── Pending UI Action Tests ──────────────────────────────────────────

    [Fact]
    public async Task SendMessage_PendingUiAction_IsSetAfterToolExecution()
    {
        // Arrange — tool returns a result with a UiAction (navigation).
        var navAction = Fakvio.Contracts.Dto.Chat.ChatUiAction.Navigate("/invoices/create");
        _toolExecutor.DetectToolIntent(Arg.Any<string>()).Returns(true);
        _toolExecutor.BuildToolInstructions().Returns("\nTOOLS: ...");
        _toolExecutor.ParseToolCall(Arg.Any<string>()).Returns(
            new ParsedToolCall
            {
                Action = "navigate",
                Parameters = new Dictionary<string, string> { ["target"] = "new_invoice" }
            });
        _toolExecutor.ExecuteToolAsync(Arg.Any<ParsedToolCall>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.SuccessWithAction("Opening new invoice form.", navAction));

        // Need two AI responses: first pass (tool JSON), second pass (natural language).
        _mockProvider
            .GetCompletionAsync(Arg.Any<List<ChatMessageDto>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("AI response text", "Opening the new invoice form for you.");

        var request = new SendMessageRequest { Message = "Otevři novou fakturu" };

        // Act
        await _service.SendMessageAsync(TestUserId, request);

        // Assert — GetPendingUiAction should return the navigation action.
        var pendingAction = _service.GetPendingUiAction();
        pendingAction.ShouldNotBeNull();
        pendingAction.Type.ShouldBe("navigate");
        pendingAction.Url.ShouldBe("/invoices/create");
    }

    [Fact]
    public async Task SendMessage_PendingUiAction_IsNull_WhenNoToolUsed()
    {
        // Arrange — regular message, no tool intent.
        var request = new SendMessageRequest { Message = "Hello" };

        // Act
        await _service.SendMessageAsync(TestUserId, request);

        // Assert — no tool executed, so no pending action.
        _service.GetPendingUiAction().ShouldBeNull();
    }
}
