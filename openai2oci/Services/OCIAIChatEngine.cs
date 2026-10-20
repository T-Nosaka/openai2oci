using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Oci.GenerativeaiinferenceService.Models;
using Oci.GenerativeaiinferenceService.Requests;
using Oci.GenerativeaiinferenceService.Responses;
using System.Diagnostics.Tracing;
using System.Text;

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

    // ReasoningEffort は SDK 上 nullable enum のため未設定時は null だが、
    // 明示的に NullValueHandling.Ignore で隠し、未指定時にフィールド自体を送信しない。
    // (非対応モデルがフィールドの存在自体を拒否する可能性があるため。frequency_penalty 等と同方針)
    // 基底の StringEnumConverter は new 隠しで引き継がれないため明示的に付与する
    // (付けないと数値シリアル化され OCI が "Please pass in correct format of request." で拒否する)。
    [JsonProperty("reasoningEffort", NullValueHandling = NullValueHandling.Ignore)]
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public new GenericChatRequest.ReasoningEffortEnum? ReasoningEffort { get; set; }
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
        GenericChatRequest.ReasoningEffortEnum? reasoningEffort = null,
        CancellationToken ct = default)
    {
        var entry = _registry.Get(authProfileId);
        var ociClient = entry.Client;
        var compartmentId = entry.CompartmentId;

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
                Tools = tools,
                ReasoningEffort = reasoningEffort
            }
        };

        var chatRequest = new ChatRequest { ChatDetails = chatDetails };

        _logger?.LogInformation(
            "OCI Chat: authProfile={AuthProfile}, model={Model}, messages={Count}, tools={ToolCount}, maxTokens={MaxTokens}, temperature={Temperature}, freqPenalty={FreqPenalty}, presPenalty={PresPenalty}, reasoningEffort={ReasoningEffort}",
            authProfileId, modelId, messages.Count, tools?.Count ?? 0, maxTokens, temperature, frequencyPenalty, presencePenalty, reasoningEffort);

        return await ociClient.Chat(chatRequest);
    }

    /// <summary>
    /// OCI EmbedText を呼び出す(Cohere Embed 4 想定)。
    /// 入力はテキストのみ(最大96件)。dimensions は embed-v4 のみ有効(256/512/1024/1536)。
    /// </summary>
    public async Task<EmbedTextResponse> EmbedAsync(
        string authProfileId,
        string modelId,
        List<string> inputs,
        int? dimensions = null,
        CancellationToken ct = default)
    {
        var entry = _registry.Get(authProfileId);
        var ociClient = entry.Client;
        var compartmentId = entry.CompartmentId;

        var embedDetails = new EmbedTextDetails
        {
            CompartmentId = compartmentId,
            ServingMode = new OnDemandServingMode { ModelId = modelId },
            Inputs = inputs
        };

        // Cohere Embed v4 のみ dimensions が有効。指定があれば設定。
        if (dimensions.HasValue)
        {
            embedDetails.OutputDimensions = dimensions.Value;
        }

        var embedRequest = new EmbedTextRequest { EmbedTextDetails = embedDetails };

        _logger?.LogInformation(
            "OCI Embed: authProfile={AuthProfile}, model={Model}, inputs={Count}, dimensions={Dimensions}",
            authProfileId, modelId, inputs.Count, dimensions);

        return await ociClient.EmbedText(embedRequest);
    }

    // 429(レート制限)・503 の再試行設定
    private const int ThrottleMaxRetries = 5;
    private static readonly TimeSpan ThrottleBaseDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ThrottleMaxDelay = TimeSpan.FromSeconds(180);

    /// <summary>
    /// OCI へ stream 送信する。429/503 の場合はレスポンスヘッダー送出前なので、
    /// Retry-After または指数バックオフで待機してから再送する。
    /// </summary>
    private async Task<System.Net.Http.HttpResponseMessage> SendChatWithRetryAsync(
        System.Net.Http.HttpClient http,
        string url,
        string json,
        string authProfileId,
        string modelId,
        CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url);
            req.Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            var resp = await http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                return resp;
            }

            var status = (int)resp.StatusCode;
            var err = await resp.Content.ReadAsStringAsync(ct);

            // 429/503: スロットリング → Retry-After または指数バックオフで再送
            if ((status == 429 || status == 503) && attempt < ThrottleMaxRetries)
            {
                var delay = ComputeThrottleDelay(resp, attempt);
                _logger?.LogWarning(
                    "OCI throttled ({Status}): retry {Attempt}/{Max} in {DelaySec:F1}s, authProfile={AuthProfile}, model={Model}",
                    status, attempt + 1, ThrottleMaxRetries, delay.TotalSeconds, authProfileId, modelId);
                resp.Dispose();
                await Task.Delay(delay, ct);
                continue;
            }

            resp.Dispose();
            throw new InvalidOperationException($"OCI streaming chat failed ({status}): {err}");
        }
    }


    /// <summary>
    /// 利用量スロットリング対応
    /// </summary>
    /// <param name="resp"></param>
    /// <param name="attempt"></param>
    /// <returns></returns>
    private static TimeSpan ComputeThrottleDelay(System.Net.Http.HttpResponseMessage resp, int attempt)
    {
        // サーバーが Retry-After を指定していればそれを優先
        var retryAfter = resp.Headers.RetryAfter?.Delta;
        if (retryAfter is { } serverDelay && serverDelay > TimeSpan.Zero)
        {
            return serverDelay < ThrottleMaxDelay ? serverDelay : ThrottleMaxDelay;
        }

        // 指数バックオフ ジッタ(0〜1s)
        var exp = TimeSpan.FromSeconds(ThrottleBaseDelay.TotalSeconds * Math.Pow(2, attempt));
        var capped = exp < ThrottleMaxDelay ? exp : ThrottleMaxDelay;
        return capped + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
    }

    /// <summary>
    /// OCI Chat ストリーミング呼び出し(REST直叩き)。
    /// SDK の Chat() は SSE ストリームのパースに未対応のため、stream:true 時のみ
    /// 署名付き HttpClient で POST /20231130/actions/chat (isStream:true) を直接呼び、
    /// OCI の SSE イベントを OpenAI ChatCompletionChunk 形式に変換しながら HttpResponse へ素通しする。
    /// 非ストリーミング応答が返る前に OCI がエラーを返した場合は例外を送出する(呼び出し側で 502 変換)。
    /// </summary>
    public async Task ChatStreamAsync(
        string authProfileId,
        string modelId,
        List<Message> messages,
        int maxTokens,
        double temperature,
        double topP,
        int? topK,
        HttpResponse response,
        double? frequencyPenalty = null,
        double? presencePenalty = null,
        List<ToolDefinition>? tools = null,
        GenericChatRequest.ReasoningEffortEnum? reasoningEffort = null,
        CancellationToken ct = default)
    {
        var entry = _registry.Get(authProfileId);

        var chatDetails = new ChatDetails
        {
            ServingMode = new OnDemandServingMode { ModelId = modelId },
            CompartmentId = entry.CompartmentId,
            ChatRequest = new OptionalGenericChatRequest
            {
                Messages = messages,
                MaxTokens = maxTokens,
                Temperature = temperature,
                TopP = topP,
                TopK = topK,
                FrequencyPenalty = frequencyPenalty,
                PresencePenalty = presencePenalty,
                Tools = tools,
                ReasoningEffort = reasoningEffort,
                IsStream = true,
                StreamOptions = new StreamOptions { IsIncludeUsage = true }
            }
        };

        var url = $"https://inference.generativeai.{entry.RegionId}.oci.oraclecloud.com/20231130/actions/chat";
        var json = JsonConvert.SerializeObject(chatDetails);

        _logger?.LogInformation(
            "OCI Chat stream: authProfile={AuthProfile}, model={Model}, messages={Count}, tools={ToolCount}, reasoningEffort={ReasoningEffort}",
            authProfileId, modelId, messages.Count, tools?.Count ?? 0, reasoningEffort);

        using var ociResp = await SendChatWithRetryAsync(entry.StreamHttp, url, json, authProfileId, modelId, ct);

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";

        var id = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // role チャンク(先頭)
        await WriteStreamChunk(response, id, created, modelId, delta: new { role = "assistant" }, finishReason: null, usage: null);

        var sentText = new StringBuilder();
        var toolAcc = new List<ToolCallAcc>();
        string? lastFinishReason = null;
        (int prompt, int completion, int total)? lastUsage = null;

        using var stream = await ociResp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        int eventCount = 0;
        int deltaCount = 0;
        var rawDump = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            if (rawDump.Length < 65536)
            {
                rawDump.AppendLine(line);
            }
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 空行 / ": ping" 等のコメント行は無視
            }
            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                if (payload == "[DONE]")
                {
                    break;
                }
                continue;
            }

            eventCount++;
            string? delta = null;
            try
            {
                delta = ExtractStreamDelta(payload, sentText, messages, toolAcc, ref lastFinishReason, ref lastUsage);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "OCI stream event parse failed, skipping 1 event");
                continue;
            }
            if (!string.IsNullOrEmpty(delta))
            {
                deltaCount++;
                await WriteStreamChunk(response, id, created, modelId, delta: new { content = delta }, finishReason: null, usage: null);
            }
        }

        _logger?.LogInformation(
            "OCI Chat stream end: events={Events}, deltas={Deltas}, tools={Tools}, finish={Finish}",
            eventCount, deltaCount, toolAcc.Count, lastFinishReason);

        // フォールバック: SSE イベントが1件も無い場合は非ストリーミング応答として全体を処理
        if (eventCount == 0 && rawDump.Length > 0)
        {
            var fallback = ExtractStreamDelta(rawDump.ToString(), sentText, messages, toolAcc, ref lastFinishReason, ref lastUsage);
            if (!string.IsNullOrEmpty(fallback))
            {
                await WriteStreamChunk(response, id, created, modelId, delta: new { content = fallback }, finishReason: null, usage: null);
            }
        }

        // tool_calls は分割到着するため、完了後に1チャンクにまとめて送出(従来の擬似SSEと同形)
        if (toolAcc.Count > 0)
        {
            var toolCalls = toolAcc.Select((t, i) => new
            {
                index = i,
                id = t.Id,
                type = "function",
                function = new { name = t.Name, arguments = t.Arguments.ToString() }
            }).ToList();
            await WriteStreamChunk(response, id, created, modelId, delta: new { tool_calls = toolCalls }, finishReason: null, usage: null);
        }

        var finishReason = toolAcc.Count > 0 ? "tool_calls" : MapStreamFinishReason(lastFinishReason);
        object? usage = lastUsage.HasValue
            ? new { prompt_tokens = lastUsage.Value.prompt, completion_tokens = lastUsage.Value.completion, total_tokens = lastUsage.Value.total }
            : null;
        await WriteStreamChunk(response, id, created, modelId, delta: new { }, finishReason: finishReason, usage: usage);

        await response.WriteAsync("data: [DONE]\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private sealed class ToolCallAcc
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>
    /// OCI ストリーミングイベント1件を処理し、今回送出すべきテキスト差分を返す。
    /// イベント形状は2通り: (A) ChatResult ラッパー {chatResponse:{choices:[...], usage}}、
    /// (B) 素の Choice {index, message, finishReason, pad}(実機確認済み)。
    /// toolCalls は toolAcc に蓄積、finishReason/usage は ref で更新する。
    /// OCI のイベントは累積形・差分形のいずれの可能性もあるため、送信済み prefix と比較して差分だけ返す。
    /// </summary>
    private string? ExtractStreamDelta(
        string payload,
        StringBuilder sentText,
        List<Message> messages,
        List<ToolCallAcc> toolAcc,
        ref string? lastFinishReason,
        ref (int prompt, int completion, int total)? lastUsage)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.TryGetProperty("chatResponse", out var chatResp))
        {
            ReadStreamUsage(chatResp, ref lastUsage);
            if (!chatResp.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != System.Text.Json.JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return null;
            }
            return ExtractChoiceDelta(choices[0], sentText, messages, toolAcc, ref lastFinishReason);
        }
        if (root.TryGetProperty("message", out _) || root.TryGetProperty("finishReason", out _))
        {
            ReadStreamUsage(root, ref lastUsage);
            return ExtractChoiceDelta(root, sentText, messages, toolAcc, ref lastFinishReason);
        }
        return null;
    }

    private static void ReadStreamUsage(
        System.Text.Json.JsonElement container,
        ref (int prompt, int completion, int total)? lastUsage)
    {
        if (container.TryGetProperty("usage", out var usage) && usage.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            int p = usage.TryGetProperty("promptTokens", out var pe) && pe.TryGetInt32(out var pv) ? pv : 0;
            int c = usage.TryGetProperty("completionTokens", out var ce) && ce.TryGetInt32(out var cv) ? cv : 0;
            int t = usage.TryGetProperty("totalTokens", out var te) && te.TryGetInt32(out var tv) ? tv : p + c;
            lastUsage = (p, c, t);
        }
    }

    /// <summary>Choice 相当要素1件からテキスト差分を抽出する。toolCalls/finishReason も反映する。</summary>
    private string? ExtractChoiceDelta(
        System.Text.Json.JsonElement ch,
        StringBuilder sentText,
        List<Message> messages,
        List<ToolCallAcc> toolAcc,
        ref string? lastFinishReason)
    {
        if (ch.TryGetProperty("finishReason", out var fr) && fr.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            lastFinishReason = fr.GetString();
        }

        string eventText = "";
        if (ch.TryGetProperty("message", out var msg))
        {
            if (msg.TryGetProperty("content", out var content) &&
                content.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        continue;
                    }
                    var type = part.TryGetProperty("type", out var te2) ? te2.GetString() : null;
                    if (string.Equals(type, "TEXT", StringComparison.OrdinalIgnoreCase) &&
                        part.TryGetProperty("text", out var tx) &&
                        tx.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        eventText += tx.GetString();
                    }
                }
            }
            if (msg.TryGetProperty("toolCalls", out var tcs) &&
                tcs.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var tc in tcs.EnumerateArray())
                {
                    _logger?.LogInformation( "tools: {tc}", tc );

                    var id = tc.TryGetProperty("id", out var ide) && ide.ValueKind == System.Text.Json.JsonValueKind.String
                        ? ide.GetString() ?? "" : "";
                    var name = tc.TryGetProperty("name", out var ne) && ne.ValueKind == System.Text.Json.JsonValueKind.String
                        ? ne.GetString() ?? "" : "";
                    var args = tc.TryGetProperty("arguments", out var ae) && ae.ValueKind == System.Text.Json.JsonValueKind.String
                        ? ae.GetString() ?? "" : "";

                    ToolCallAcc acc;
                    if (idx < toolAcc.Count && (string.IsNullOrEmpty(id) || toolAcc[idx].Id == id))
                    {
                        //蓄積されていたIDがあった場合、採用
                        acc = toolAcc[idx];
                    }
                    else
                    {
                        var byId = string.IsNullOrEmpty(id) ? null : toolAcc.FirstOrDefault(a => a.Id == id);
                        if (byId != null)
                        {
                            //蓄積されていたIDがあった場合、採用
                            acc = byId;
                        }
                        else
                        {
                            //Toolを追加する。この時にIDがセットされない場合、UUIDをセットする。
                            acc = new ToolCallAcc { Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString() : id };
                            toolAcc.Add(acc);
                        }
                    }
                    if (!string.IsNullOrEmpty(name))
                    {
                        acc.Name = name;
                    }
                    if (!string.IsNullOrEmpty(args))
                    {
                        // arguments も累積形/差分形の可能性があるため、既蓄積の suffix なら差分のみ追加
                        var cur = acc.Arguments.ToString();
                        if (args.Length > cur.Length && args.StartsWith(cur, StringComparison.Ordinal))
                        {
                            acc.Arguments.Append(args[cur.Length..]);
                        }
                        else if (!cur.Contains(args, StringComparison.Ordinal))
                        {
                            acc.Arguments.Append(args);
                        }
                    }
                    idx++;
                }
            }
        }

        if (string.IsNullOrEmpty(eventText))
        {
            return null;
        }
        var sent = sentText.ToString();
        string delta;
        if (eventText.Length > sent.Length && eventText.StartsWith(sent, StringComparison.Ordinal))
        {
            delta = eventText[sent.Length..];
            sentText.Append(delta);
        }
        else
        {
            // 差分形イベント(累積でない)の場合はそのまま送出
            delta = eventText;
            sentText.Append(eventText);
        }
        return delta;
    }

    private static string MapStreamFinishReason(string? fr)
    {
        if (string.IsNullOrEmpty(fr))
        {
            return "stop";
        }
        return fr switch
        {
            "STOP" => "stop",
            "LENGTH" => "length",
            "CONTENT_FILTER" => "content_filter",
            "TOOL_CALLS" => "tool_calls",
            _ when fr.Equals("stop", StringComparison.OrdinalIgnoreCase) => "stop",
            _ when fr.Equals("length", StringComparison.OrdinalIgnoreCase) => "length",
            _ when fr.Equals("tool_calls", StringComparison.OrdinalIgnoreCase) => "tool_calls",
            _ => "stop"
        };
    }

    private static readonly System.Text.Json.JsonSerializerOptions streamJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task WriteStreamChunk(HttpResponse response, string id, long created, string model, object delta, string? finishReason, object? usage)
    {
        var chunk = new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new { index = 0, delta, finish_reason = finishReason }
            },
            usage
        };
        var json = System.Text.Json.JsonSerializer.Serialize(chunk, streamJsonOptions);
        await response.WriteAsync($"data: {json}\n\n");
        await response.Body.FlushAsync();
    }
}
