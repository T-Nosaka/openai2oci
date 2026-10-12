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
    [property: JsonPropertyName("top_k")] int? TopK = null,
    // OpenAI標準パラメータ。明示指定時のみOCIの reasoningEffort に透過する。
    // 非対応モデルに送るとOCIが400を返すため、未指定時はフィールド自体を送信しない(呼び出し側の責任)。
    [property: JsonPropertyName("reasoning_effort")] string? ReasoningEffort = null);

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
    [property: JsonPropertyName("object")]
    public string Object { get; init; } = "model";

    [property: JsonPropertyName("owned_by")]
    public string OwnedBy { get; init; } = "oci";
}

public record OpenAIModelList(IEnumerable<OpenAIModel> Data)
{
    public string Object { get; init; } = "list";
}

/// <summary>
/// OpenAI Embeddings API リクエスト DTO。
/// input は string または string[] (数値配列は OCI 非対応のため 400)。
/// </summary>
public record CreateEmbeddingRequest(
    string? Model,
    JsonElement Input,
    [property: JsonPropertyName("encoding_format")] string? EncodingFormat = null,
    [property: JsonPropertyName("dimensions")] int? Dimensions = null,
    string? User = null);

public record EmbeddingData(
    [property: JsonPropertyName("object")] string Object,
    int Index,
    object Embedding);

public record EmbeddingUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

public record CreateEmbeddingResponse(
    [property: JsonPropertyName("object")] string Object,
    List<EmbeddingData> Data,
    string Model,
    EmbeddingUsage Usage);
