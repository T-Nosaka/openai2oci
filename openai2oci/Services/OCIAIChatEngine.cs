using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Oci.GenerativeaiinferenceService.Models;
using Oci.GenerativeaiinferenceService.Requests;
using Oci.GenerativeaiinferenceService.Responses;

namespace openai2oci.Services;

/// <summary>
/// OCI SDK の GenericChatRequest には既知の問題がある: Temperature/TopP/FrequencyPenalty/
/// PresencePenalty が非Nullableの double で、[JsonProperty(DefaultValueHandling=Include)]
/// のため「未設定でも常にデフォルト値0.0がJSONに含まれて送信される」。
/// Grok(xAI)等、presencePenalty/frequencyPenaltyフィールドの存在自体を許容しないモデルでは
/// これが invalid-argument エラーの原因になる(実機検証・エラーメッセージにて確認済み)。
/// GenericChatRequest は非sealedなので、問題の4プロパティだけを new 修飾子で隠し、
/// Nullable + NullValueHandling.Ignore で上書きする。
/// </summary>
public class OptionalGenericChatRequest : GenericChatRequest
{
    [JsonProperty("temperature", NullValueHandling = NullValueHandling.Ignore)]
    public new double? Temperature { get; set; }

    [JsonProperty("topP", NullValueHandling = NullValueHandling.Ignore)]
    public new double? TopP { get; set; }

    [JsonProperty("frequencyPenalty", NullValueHandling = NullValueHandling.Ignore)]
    public new double? FrequencyPenalty { get; set; }

    [JsonProperty("presencePenalty", NullValueHandling = NullValueHandling.Ignore)]
    public new double? PresencePenalty { get; set; }
}

/// <summary>
/// OCI Generative AI 推論クライアント ラッパー(最小版)。
/// モデルごとに異なるOCI認証プロファイル(テナント/リージョン)を使えるよう、
/// 呼び出しごとに authProfileId で OciClientRegistry から解決する。
/// </summary>
public class OCIAIChatEngine
{
    private readonly OciClientRegistry _registry;
    private readonly ILogger<OCIAIChatEngine>? _logger;

    public OCIAIChatEngine(
        OciClientRegistry registry,
        ILogger<OCIAIChatEngine>? logger = null)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// OCI Chat を呼び出す。authProfileId で使用するOCI認証(テナント/リージョン)とコンパートメントを解決する。
    /// frequencyPenalty/presencePenalty は null の場合フィールド自体を送信しない
    /// (Grok等、フィールドの存在自体を許容しないモデルへの対応)。
    /// </summary>
    public async Task<ChatResponse> ChatAsync(
        string authProfileId,
        string modelId,
        List<Message> messages,
        int maxTokens,
        double temperature,
        double topP,
        int? topK,
        double? frequencyPenalty = null,
        double? presencePenalty = null,
        List<ToolDefinition>? tools = null,
        CancellationToken ct = default)
    {
        var (ociClient, compartmentId) = _registry.Get(authProfileId);

        var chatDetails = new ChatDetails
        {
            ServingMode = new OnDemandServingMode { ModelId = modelId },
            CompartmentId = compartmentId,
            ChatRequest = new OptionalGenericChatRequest
            {
                Messages = messages,
                MaxTokens = maxTokens,
                Temperature = temperature,
                TopP = topP,
                TopK = topK,
                FrequencyPenalty = frequencyPenalty,
                PresencePenalty = presencePenalty,
                Tools = tools
            }
        };

        var chatRequest = new ChatRequest { ChatDetails = chatDetails };

        _logger?.LogInformation(
            "OCI Chat: authProfile={AuthProfile}, model={Model}, messages={Count}, tools={ToolCount}, maxTokens={MaxTokens}, temperature={Temperature}, freqPenalty={FreqPenalty}, presPenalty={PresPenalty}",
            authProfileId, modelId, messages.Count, tools?.Count ?? 0, maxTokens, temperature, frequencyPenalty, presencePenalty);

        return await ociClient.Chat(chatRequest);
    }
}
