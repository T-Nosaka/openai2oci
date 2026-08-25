using Oci.GenerativeaiinferenceService.Models;
using openai2oci.Models;
using Oci.GenerativeaiinferenceService.Responses;

namespace openai2oci.Converters;

/// <summary>
/// OCI Generative AI 形式 → OpenAI Chat Completions 形式 変換(設計書 6章)。
/// </summary>
public static class OciToOpenAIConverter
{
    /// <summary>
    /// OCI ChatChoice → (content, tool_calls, finish_reason)。
    /// 設計書 6章: OCI ToolCall.Id が空なら Guid 補完。
    /// </summary>
    public static (string? content, List<OpenAIToolCall>? toolCalls, string finishReason) ConvertChoice(
        ChatChoice ociChoice)
    {
        var asmsg = ociChoice.Message as AssistantMessage;
        var text = ExtractText(asmsg?.Content);
        var toolCalls = ConvertToolCalls(asmsg?.ToolCalls);

        string finishReason = toolCalls is { Count: > 0 }
            ? "tool_calls"
            : MapFinishReason(ociChoice.FinishReason);

        var content = toolCalls is { Count: > 0 } ? null : text;
        return (content, toolCalls, finishReason);
    }

    private static List<OpenAIToolCall>? ConvertToolCalls(List<ToolCall>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0)
        {
            return null;
        }

        return toolCalls
            .OfType<FunctionCall>()
            .Select(fc => new OpenAIToolCall(
                Id: string.IsNullOrEmpty(fc.Id) ? Guid.NewGuid().ToString() : fc.Id,
                Type: "function",
                Function: new OpenAIToolCallFunction(
                    Name: fc.Name ?? "",
                    Arguments: string.IsNullOrEmpty(fc.Arguments) ? "{}" : fc.Arguments)))
            .ToList();
    }

    private static string ExtractText(List<ChatContent>? contents)
    {
        if (contents is null)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var content in contents)
        {
            if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
            {
                parts.Add(tc.Text);
            }
        }
        return string.Join("\n", parts);
    }

    private static string MapFinishReason(object? fr)
    {
        if (fr is null) return "stop";
        var s = fr.ToString() ?? "";
        return s switch
        {
            "STOP"           => "stop",
            "LENGTH"         => "length",
            "CONTENT_FILTER" => "content_filter",
            _ when s.Equals("stop",           StringComparison.OrdinalIgnoreCase) => "stop",
            _ when s.Equals("length",         StringComparison.OrdinalIgnoreCase) => "length",
            _ when s.Equals("content_filter", StringComparison.OrdinalIgnoreCase) => "content_filter",
            _ => "stop"
        };
    }
}