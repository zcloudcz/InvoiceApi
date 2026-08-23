using Fakvio.Application.Exceptions;
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
    private readonly ICompanyAiSettingsResolver _companyAiResolver;
    private readonly IAiProvider _mockProvider;
    private readonly IChatContextBuilder _contextBuilder;
    private readonly IChatToolExecutor _toolExecutor;
    private readonly ILogger<ChatService> _logger;
    private const long TestUserId = 42;

    /// <summary>A second user, used for the "this conversation is not yours" scenarios.</summary>
    private const long OtherUserId = 99;

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

        // Mock tool executor — by default it parses no tool call,
        // so the tests below exercise the regular chat flow.
        _toolExecutor = Substitute.For<IChatToolExecutor>();

        // Mock company AI settings resolver — delegates to the global factory by default.
        _companyAiResolver = Substitute.For<ICompanyAiSettingsResolver>();
        _companyAiResolver
            .ResolveProviderAsync(Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_mockProvider);
        _companyAiResolver
            .GetAvailableProvidersAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string> { "TestProvider" }.AsReadOnly());

        // Mock tenant resolver — returns a default CompanyId for tests.
        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(1L);

        _service = new ChatService(_context, tenantResolver, _providerFactory, _companyAiResolver, _contextBuilder, _toolExecutor, _logger);
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
        await Should.ThrowAsync<ChatConversationNotFoundException>(
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
        await Should.ThrowAsync<ChatConversationNotFoundException>(
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

    /// <summary>
    /// Loading somebody else's conversation must fail exactly like loading a non-existent one:
    /// the lookup filters by conversation ID AND user ID, and the dedicated exception type is
    /// what lets the controller answer 404 with its own text instead of 500 (issue #156).
    /// </summary>
    [Fact]
    public async Task GetConversation_ThrowsChatConversationNotFound_ForWrongUser()
    {
        // Arrange — conversation owned by TestUserId.
        var sendResult = await _service.SendMessageAsync(TestUserId,
            new SendMessageRequest { Message = "Mine" });

        // Act & Assert — user 99 tries to read it.
        var caught = await Should.ThrowAsync<ChatConversationNotFoundException>(
            () => _service.GetConversationAsync(sendResult.ConversationId, OtherUserId));

        caught.ConversationId.ShouldBe(sendResult.ConversationId);
    }

    // ─── StreamMessageAsync Tests ────────────────────────────────────────

    /// <summary>
    /// The streaming path resolves the conversation through the same lookup as SendMessageAsync,
    /// and /api/chat/stream is the endpoint the chat panel actually calls — so this is where a
    /// conversation deleted in another browser tab shows up first (issue #156, round 3).
    ///
    /// Junior note on the "before yielding any chunk" part: StreamMessageAsync is an async
    /// iterator, so its body only starts running on the first MoveNextAsync. The lookup is the
    /// first await, before any yield, which is why the exception surfaces while the controller is
    /// still inside its try block and can answer with a clean error event instead of having to
    /// abort a half-written SSE response.
    /// </summary>
    [Fact]
    public async Task StreamMessage_ThrowsChatConversationNotFound_BeforeYieldingAnyChunk()
    {
        // Arrange — an ID no conversation has.
        const long missingConversationId = 9999;
        var request = new SendMessageRequest { Message = "Test", ConversationId = missingConversationId };
        var receivedChunks = new List<string>();

        // Act & Assert
        var caught = await Should.ThrowAsync<ChatConversationNotFoundException>(async () =>
        {
            await foreach (var chunk in _service.StreamMessageAsync(TestUserId, request))
            {
                receivedChunks.Add(chunk);
            }
        });

        caught.ConversationId.ShouldBe(missingConversationId);
        receivedChunks.ShouldBeEmpty();
    }

    /// <summary>
    /// Same guarantee for a valid ID that belongs to somebody else — the case a hand-crafted
    /// request probing foreign conversation IDs would hit.
    /// </summary>
    [Fact]
    public async Task StreamMessage_ThrowsChatConversationNotFound_ForWrongUser()
    {
        // Arrange — conversation owned by TestUserId.
        var sendResult = await _service.SendMessageAsync(TestUserId,
            new SendMessageRequest { Message = "Mine" });

        var request = new SendMessageRequest { Message = "Hijack", ConversationId = sendResult.ConversationId };

        // Act & Assert
        var caught = await Should.ThrowAsync<ChatConversationNotFoundException>(async () =>
        {
            await foreach (var _ in _service.StreamMessageAsync(OtherUserId, request))
            {
                // No chunk can arrive — the lookup fails first.
            }
        });

        caught.ConversationId.ShouldBe(sendResult.ConversationId);
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
        await Should.ThrowAsync<ChatConversationNotFoundException>(
            () => _service.DeleteConversationAsync(result.ConversationId, 99));
    }

    // ─── GetAvailableProviders Tests ─────────────────────────────────────

    [Fact]
    public async Task GetAvailableProviders_ReturnsProviderList()
    {
        var providers = await _service.GetAvailableProvidersAsync();
        providers.Count.ShouldBe(1);
        providers[0].ShouldBe("TestProvider");
    }

    // ─── Tool Flow Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_UsesTwoPassFlow_WhenModelProducesToolCall()
    {
        // Arrange — configure tool executor: model produces a tool call in the first pass.
        // For non-native-tool providers, tool instructions are ALWAYS included (no regex gate).
        _toolExecutor.BuildToolInstructions().Returns("\nTOOLS: ...");
        _toolExecutor.ParseToolCall(Arg.Any<string>()).Returns(
            new ParsedToolCall
            {
                Action = "ares_lookup",
                Parameters = new Dictionary<string, string> { ["registration_number"] = "12345678" }
            });
        _toolExecutor.ExecuteToolAsync(Arg.Any<ParsedToolCall>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("Company found: Test s.r.o."));

        // Two AI calls: first pass (tool JSON), second pass (natural language with tool result).
        _mockProvider
            .GetCompletionAsync(Arg.Any<List<ChatMessageDto>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns("AI response text", "Found company Test s.r.o. in ARES registry.");

        var request = new SendMessageRequest { Message = "Najdi firmu IČO 12345678" };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — response should contain the second-pass AI response.
        result.AssistantMessage.Content.ShouldBe("Found company Test s.r.o. in ARES registry.");

        // Verify the tool executor was called in the correct sequence.
        _toolExecutor.Received(1).BuildToolInstructions();
        _toolExecutor.Received(1).ParseToolCall("AI response text");
        await _toolExecutor.Received(1).ExecuteToolAsync(
            Arg.Is<ParsedToolCall>(tc => tc.Action == "ares_lookup"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessage_TellsModelNothingChanged_WhenToolOnlyReturnedAPreview()
    {
        // Issue #212: a confirmable tool that was called without approval did NOT run.
        // If the second-pass prompt still said "was executed", the assistant would happily
        // report a change that never happened — the exact failure the confirm gate prevents.
        _toolExecutor.BuildToolInstructions().Returns("\nTOOLS: ...");
        _toolExecutor.ParseToolCall(Arg.Any<string>()).Returns(
            new ParsedToolCall
            {
                Action = "update_settings",
                Parameters = new Dictionary<string, string> { ["value"] = "FA-2026" }
            });
        _toolExecutor.ExecuteToolAsync(Arg.Any<ParsedToolCall>(), Arg.Any<CancellationToken>())
            .Returns(ChatToolResult.Success("Numbering would change to FA-2026.") with
            {
                RequiresConfirmation = true
            });

        var request = new SendMessageRequest { Message = "Změň číslování na FA-2026" };

        await _service.SendMessageAsync(TestUserId, request);

        // Second pass must be framed as a preview awaiting approval.
        await _mockProvider.Received(1).GetCompletionAsync(
            Arg.Any<List<ChatMessageDto>>(),
            Arg.Is<string?>(prompt => prompt != null
                                      && prompt.Contains("was NOT executed")
                                      && prompt.Contains("ask them to confirm")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendMessage_FallsBackToRegularFlow_WhenToolCallNotParsed()
    {
        // Arrange — tool instructions are sent, but the AI doesn't produce a tool call.
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
    public async Task SendMessage_NoToolExecuted_WhenModelDeclinesToolCall()
    {
        // Arrange — tool instructions are always included for non-native-tool providers,
        // but the model chooses not to produce a tool call (returns regular text).
        _toolExecutor.BuildToolInstructions().Returns("\nTOOLS: ...");
        _toolExecutor.ParseToolCall(Arg.Any<string>()).Returns((ParsedToolCall?)null);

        var request = new SendMessageRequest { Message = "Kolik mám faktur?" };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — first-pass text response used directly, no tool executed.
        result.AssistantMessage.Content.ShouldBe("AI response text");

        // Tool instructions ARE sent (always), but no tool should be executed.
        _toolExecutor.Received(1).BuildToolInstructions();
        await _toolExecutor.DidNotReceive().ExecuteToolAsync(
            Arg.Any<ParsedToolCall>(), Arg.Any<CancellationToken>());
    }

    // ─── Pending UI Action Tests ──────────────────────────────────────────

    [Fact]
    public async Task SendMessage_PendingUiAction_IsSetAfterToolExecution()
    {
        // Arrange — tool returns a result with a UiAction (navigation).
        var navAction = Fakvio.Contracts.Dto.Chat.ChatUiAction.Navigate("/invoices/create");
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

    // ─── PDF Attachment Tests ─────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_WithAttachedFile_PrependsContentToMessage()
    {
        // Arrange — message with attached PDF content.
        var request = new SendMessageRequest
        {
            Message = "What is this document about?",
            AttachedFileContent = "Invoice #123 for consulting services.",
            AttachedFileName = "invoice.pdf"
        };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — the saved user message should contain both the PDF content and the question.
        var userMessage = await _context.ChatMessage
            .FirstOrDefaultAsync(m => m.ConversationId == result.ConversationId
                                      && m.Role == EChatRole.User);

        userMessage.ShouldNotBeNull();
        userMessage.Content.ShouldContain("[Attached PDF: invoice.pdf]");
        userMessage.Content.ShouldContain("Invoice #123 for consulting services.");
        userMessage.Content.ShouldContain("What is this document about?");
    }

    [Fact]
    public async Task SendMessage_WithoutAttachment_UsesOriginalMessage()
    {
        // Arrange — message without any attachment.
        var request = new SendMessageRequest
        {
            Message = "How many invoices do I have?",
            AttachedFileContent = null,
            AttachedFileName = null
        };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — the saved user message should be the original text only.
        var userMessage = await _context.ChatMessage
            .FirstOrDefaultAsync(m => m.ConversationId == result.ConversationId
                                      && m.Role == EChatRole.User);

        userMessage.ShouldNotBeNull();
        userMessage.Content.ShouldBe("How many invoices do I have?");
        userMessage.Content.ShouldNotContain("[Attached PDF:");
    }

    [Fact]
    public async Task SendMessage_WithEmptyAttachment_UsesOriginalMessage()
    {
        // Arrange — attachment properties set but content is empty/whitespace.
        var request = new SendMessageRequest
        {
            Message = "Hello",
            AttachedFileContent = "   ",
            AttachedFileName = "empty.pdf"
        };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — empty attachment content should be ignored.
        var userMessage = await _context.ChatMessage
            .FirstOrDefaultAsync(m => m.ConversationId == result.ConversationId
                                      && m.Role == EChatRole.User);

        userMessage.ShouldNotBeNull();
        userMessage.Content.ShouldBe("Hello");
    }

    // ─── Image Attachment Tests ───────────────────────────────────────────

    [Fact]
    public async Task SendMessage_WithImage_StoresPlaceholderInDb_NotBase64()
    {
        // Arrange — message with an attached image (base64 data).
        // The actual base64 should NOT be stored in the database message content.
        // Instead, a short "[Image: filename]" placeholder is stored.
        var fakeBase64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 }); // PNG header bytes
        var request = new SendMessageRequest
        {
            Message = "What is in this image?",
            AttachedImageBase64 = fakeBase64,
            AttachedFileName = "test.png"
        };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — DB message should contain the placeholder, NOT the base64 data.
        var userMessage = await _context.ChatMessage
            .FirstOrDefaultAsync(m => m.ConversationId == result.ConversationId
                                      && m.Role == EChatRole.User);

        userMessage.ShouldNotBeNull();
        userMessage.Content.ShouldContain("[Image: test.png]");
        userMessage.Content.ShouldContain("What is in this image?");
        userMessage.Content.ShouldNotContain(fakeBase64); // Base64 must NOT be in DB
    }

    [Fact]
    public async Task SendMessage_WithImage_PassesBase64ToProvider_ViaImagesProperty()
    {
        // Arrange — verify that the image base64 data is passed to the AI provider
        // through the transient ChatMessageDto.Images property on the last user message.
        var fakeBase64 = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF }); // JPEG header bytes
        var request = new SendMessageRequest
        {
            Message = "Describe this image",
            AttachedImageBase64 = fakeBase64,
            AttachedFileName = "photo.jpg"
        };

        // Capture the messages passed to the AI provider.
        List<ChatMessageDto>? capturedMessages = null;
        _mockProvider
            .GetCompletionAsync(
                Arg.Do<List<ChatMessageDto>>(msgs => capturedMessages = msgs),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("I see a photo.");

        // Act
        await _service.SendMessageAsync(TestUserId, request);

        // Assert — the last user message should have Images set with the base64 data.
        capturedMessages.ShouldNotBeNull();
        var lastUserMsg = capturedMessages.LastOrDefault(m =>
            m.Role.Equals("User", StringComparison.OrdinalIgnoreCase));
        lastUserMsg.ShouldNotBeNull();
        lastUserMsg.Images.ShouldNotBeNull();
        lastUserMsg.Images.Count.ShouldBe(1);
        lastUserMsg.Images[0].ShouldBe(fakeBase64);
    }

    [Fact]
    public async Task SendMessage_WithImageAndPdf_ImageTakesPrecedence()
    {
        // Arrange — edge case: both image and PDF are set.
        // Image should take precedence (UI prevents this, but backend has clear behavior).
        var fakeBase64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var request = new SendMessageRequest
        {
            Message = "Analyze this",
            AttachedImageBase64 = fakeBase64,
            AttachedFileName = "invoice.png",
            AttachedFileContent = "This is extracted PDF text that should be ignored."
        };

        // Act
        var result = await _service.SendMessageAsync(TestUserId, request);

        // Assert — image placeholder should be used, not PDF content block.
        var userMessage = await _context.ChatMessage
            .FirstOrDefaultAsync(m => m.ConversationId == result.ConversationId
                                      && m.Role == EChatRole.User);

        userMessage.ShouldNotBeNull();
        userMessage.Content.ShouldContain("[Image: invoice.png]");
        userMessage.Content.ShouldNotContain("[Attached PDF:");
        userMessage.Content.ShouldNotContain("extracted PDF text");
    }
}
