using System.Text.Json;
using System.Text.Json.Serialization;

namespace openai2oci.Models;

/// <summary>
/// OpenAI Chat Completions API のリクエスト/レスポンス DTO。
/// </summary>
public record ChatCompletionRequest(
    string? Model,
    List<OpenAIMessage>? Messages,
    bool? Stream = null,
    List<OpenAITool>? Tools = null,
    object? ToolChoice = null,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("top_p")] double? TopP = null,
    // Grok等、フィールドの存在自体を許容しないモデルがあるため、
    // 明示指定が無い限りOCIへは送信しない(OCIAIChatEngine.OptionalGenericChatRequest参照)。
    [property: JsonPropertyName("frequency_penalty")] double? FrequencyPenalty = null,
    [property: JsonPropertyName("presence_penalty")] double? PresencePenalty = null,
    // OpenAI標準には無いOCI固有拡張。クライアントが明示指定した場合のみ優先する(設計書4.2節)。
    [property: JsonPropertyName("top_k")] int? TopK = null);

public record OpenAIMessage(
    string Role,
    JsonElement Content,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null,
    [property: JsonPropertyName("tool_calls")] List<OpenAIToolCall>? ToolCalls = null);

public record OpenAITool(string Type, OpenAIToolFunction Function);

public record OpenAIToolFunction(string Name, string? Description, JsonElement Parameters);

/// <summary>
/// OpenAI Chat Completions の tool_call(送信 / 受信 両方向で共用)。
/// </summary>
public record OpenAIToolCall(string Id, string Type, OpenAIToolCallFunction Function);

public record OpenAIToolCallFunction(string Name, string Arguments);

public record OpenAIModel(string Id)
{
    public string Object { get; init; } = "model";
    public string OwnedBy { get; init; } = "oci";
}

public record OpenAIModelList(IEnumerable<OpenAIModel> Data)
{
    public string Object { get; init; } = "list";
}
