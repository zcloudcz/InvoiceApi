using System.ClientModel;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Fakvio.Infrastructure.AiProviders;

/// <summary>
/// OpenAI native function calling in one place.
///
/// Shared by the singleton <see cref="OpenAiProvider"/> and the per-company
/// <c>AdHocOpenAiProvider</c> in <c>CompanyAiSettingsResolver</c> — both talk to the same
/// Chat Completions API through the same SDK, so the translation lives here once.
///
/// Junior note: the OpenAI SDK models a tool as <see cref="ChatTool"/> with the parameter
/// schema handed over as raw JSON (<see cref="BinaryData"/>). We produce that JSON from the
/// shared <see cref="NativeToolSchema"/> so OpenAI sees the exact same schema as Claude.
/// </summary>
internal static class OpenAiToolCalling
{
    /// <summary>
    /// Builds the request options carrying the tool definitions.
    /// </summary>
    public static ChatCompletionOptions BuildOptions(List<NativeToolDefinition> tools)
    {
        // ChatService executes a single tool call per turn (it reads ToolCalls[0]), so asking
        // for parallel calls would silently drop the extra ones. Better to have the model
        // pick one and come back for the next in the following turn.
        var options = new ChatCompletionOptions { AllowParallelToolCalls = false };

        foreach (var tool in tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(
                functionName: tool.Name,
                functionDescription: tool.Description,
                functionParameters: BinaryData.FromObjectAsJson(NativeToolSchema.BuildJsonSchema(tool))));
        }

        return options;
    }

    /// <summary>
    /// Sends the conversation with tool definitions attached.
    ///
    /// Returns null when the call could not be completed — the caller then degrades to the
    /// text-based flow. When OpenAI refuses the tools outright (see
    /// <see cref="NativeToolRefusal"/>, e.g. a model without function calling),
    /// <paramref name="disableNativeTools"/> is invoked so the provider stops paying for a
    /// doomed extra round trip on every message.
    /// </summary>
    public static async Task<NativeToolCallResult?> CompleteWithToolsAsync(
        ChatClient chatClient,
        List<ChatMessage> chatMessages,
        List<NativeToolDefinition> tools,
        ILogger logger,
        Action disableNativeTools,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Sending {Count} messages to OpenAI with {ToolCount} native tools",
            chatMessages.Count, tools.Count);

        try
        {
            var completion = await chatClient.CompleteChatAsync(chatMessages, BuildOptions(tools), ct);
            return ParseCompletion(completion.Value, logger);
        }
        catch (ClientResultException ex)
        {
            // Only a definitive refusal latches the provider onto the text protocol — see
            // NativeToolRefusal for why the whole 4xx range would be too wide. The SDK
            // repeats the service error body in the exception message, which is what the
            // predicate reads; if a future SDK stopped doing that we would simply keep
            // retrying natively, never latch by mistake.
            if (NativeToolRefusal.IsPermanent(ex.Status, ex.Message))
            {
                logger.LogWarning(ex,
                    "OpenAI rejected native tool calling ({Status}). " +
                    "Falling back to text-based tools for the rest of this provider's lifetime.",
                    ex.Status);
                disableNativeTools();
            }
            else
            {
                logger.LogWarning(ex,
                    "OpenAI native tool call failed with {Status} — falling back for this message.",
                    ex.Status);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "OpenAI API error during tool calling: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Converts an OpenAI completion into the provider-agnostic result.
    /// Tool calls win over text, exactly like Claude, Gemini and Ollama.
    /// </summary>
    public static NativeToolCallResult ParseCompletion(ChatCompletion completion, ILogger logger)
    {
        var result = new NativeToolCallResult();

        foreach (var toolCall in completion.ToolCalls)
        {
            if (toolCall.Kind != ChatToolCallKind.Function)
                continue;

            var arguments = ReadArguments(toolCall, logger);

            logger.LogInformation(
                "OpenAI native tool call: {ToolName}({Args})",
                toolCall.FunctionName,
                string.Join(", ", arguments.Select(kv => $"{kv.Key}={kv.Value}")));

            result.ToolCalls.Add(new NativeToolCall
            {
                ToolName = toolCall.FunctionName,
                Arguments = arguments
            });
        }

        if (result.HasToolCalls)
            return result;

        result.TextContent = completion.Content?.FirstOrDefault()?.Text;
        return result;
    }

    /// <summary>
    /// OpenAI returns the arguments as a JSON string, and as an EMPTY payload for a
    /// parameterless call — parsing that as JSON would throw, so it is treated as "no arguments".
    /// </summary>
    private static Dictionary<string, string> ReadArguments(ChatToolCall toolCall, ILogger logger)
    {
        var json = toolCall.FunctionArguments?.ToString();

        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);

            // Same reader as every other provider and as the text-based flow, so the same
            // model answer produces the same arguments everywhere.
            return ToolArgumentReader.ReadArguments(document.RootElement);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse OpenAI tool arguments for {ToolName}", toolCall.FunctionName);
            return [];
        }
    }
}
