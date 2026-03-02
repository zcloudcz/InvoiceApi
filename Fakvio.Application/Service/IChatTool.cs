using Fakvio.Contracts.Dto.Chat;

namespace Fakvio.Application.Service;

/// <summary>
/// Represents the result of executing a chat tool.
/// Contains whether the tool succeeded and the formatted output text
/// that will be injected into the AI's context for generating the final response.
///
/// Optionally includes a UiAction — a command for the Blazor client to execute
/// (e.g., navigate to a page). The UiAction is sent via a separate SSE event
/// after the text stream completes.
///
/// Junior note: This is a C# "record" — an immutable data object.
/// Once created, its properties cannot be changed. This makes it safe to pass around.
/// </summary>
public record ChatToolResult
{
    /// <summary>
    /// Whether the tool executed successfully.
    /// False means an error occurred (e.g., ARES API down, invalid IČO, duplicate client).
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Human-readable output text included in the AI's context.
    /// Example: "Company found: ABC s.r.o., IČO: 12345678, Address: ..."
    /// On failure, this contains the error message prefixed with "Error: ".
    /// </summary>
    public string OutputText { get; init; } = string.Empty;

    /// <summary>
    /// Optional error message when IsSuccess is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Optional UI action to execute on the Blazor client after the AI response.
    /// For example, after finding a client, the tool can request navigation
    /// to the client detail page. Null for tools that don't trigger UI actions.
    /// </summary>
    public ChatUiAction? UiAction { get; init; }

    /// <summary>
    /// Factory method for successful results (no UI action).
    /// </summary>
    public static ChatToolResult Success(string outputText)
        => new() { IsSuccess = true, OutputText = outputText };

    /// <summary>
    /// Factory method for successful results that also trigger a UI action.
    /// Example: SuccessWithAction("Navigating to new invoice...", ChatUiAction.Navigate("/invoices/create"))
    /// </summary>
    public static ChatToolResult SuccessWithAction(string outputText, ChatUiAction action)
        => new() { IsSuccess = true, OutputText = outputText, UiAction = action };

    /// <summary>
    /// Factory method for failed results.
    /// </summary>
    public static ChatToolResult Failure(string errorMessage)
        => new() { IsSuccess = false, ErrorMessage = errorMessage, OutputText = $"Error: {errorMessage}" };
}

/// <summary>
/// Interface for a chat tool that the AI assistant can invoke.
/// Each tool has a unique name, a description (used in the AI system prompt),
/// and an Execute method that performs the actual work.
///
/// To add a new tool:
/// 1. Implement this interface
/// 2. Register it as IChatTool in DI (ServiceCollectionExtensions.cs)
/// 3. The ChatToolExecutor discovers it automatically via IEnumerable{IChatTool}
///
/// Junior note: This is the "Strategy" pattern — each tool is a different strategy
/// for handling a specific type of user request (ARES lookup, client creation, etc.).
/// </summary>
public interface IChatTool
{
    /// <summary>
    /// Unique name of the tool (e.g., "ares_lookup", "create_client").
    /// Used in the AI system prompt and in the JSON tool call format.
    /// Must be lowercase with underscores (snake_case) for LLM compatibility.
    /// </summary>
    string ToolName { get; }

    /// <summary>
    /// Human-readable description of what this tool does.
    /// Included in the system prompt so the AI knows when to use it.
    /// Keep it concise — small models (Ollama llama3.1:8b) work best with short instructions.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Description of parameters this tool expects, in a format the AI can understand.
    /// Example: "registration_number (string, required): Czech company IČO, exactly 8 digits"
    /// </summary>
    string ParameterDescription { get; }

    /// <summary>
    /// Executes the tool with the given parameters.
    /// Parameters are a dictionary of string key-value pairs extracted from the AI's JSON response.
    /// </summary>
    /// <param name="parameters">Tool parameters extracted from the AI response.</param>
    /// <param name="ct">Cancellation token for async operations.</param>
    /// <returns>Result with success/failure and output text for the AI.</returns>
    Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default);
}
