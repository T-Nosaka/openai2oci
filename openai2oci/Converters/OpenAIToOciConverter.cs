using Newtonsoft.Json.Linq;
using Oci.GenerativeaiinferenceService.Models;
using openai2oci.Models;
using System.Text.Json;

namespace openai2oci.Converters;

/// <summary>
/// OpenAI Chat Completions 形式 → OCI Generative AI 形式 変換(設計書 6章/8章)。
/// </summary>
public static class OpenAIToOciConverter
{
    /// <summary>
    /// OpenAI Message → OCI Message(1件のOpenAIメッセージが複数のOCIメッセージに
    /// 分割される場合があるため IEnumerable を返す)。
    /// role: tool → ToolMessage(ToolCallId 必須)。画像が含まれる場合は
    ///   ToolMessage(テキストのみ) + 直後の UserMessage(画像のみ) に分割する
    ///   (OCIはtoolロールに画像を入れられないため。設計書8章)。
    /// role: assistant + tool_calls → AssistantMessage{ToolCalls, Content} ※往復時に必須
    /// role: system/user/assistant → 対応する OCI Message(マルチモーダル対応)
    /// </summary>
    public static IEnumerable<Message> ToOciMessages(OpenAIMessage m)
    {
        if (m.Role == "tool")
        {
            if (string.IsNullOrEmpty(m.ToolCallId))
            {
                throw new ArgumentException("tool message requires tool_call_id");
            }

            var allContents = ToOciContents(m.Content);
            var textContents = allContents.Where(c => c is TextContent).ToList();
            var imageContents = allContents.Where(c => c is ImageContent).ToList();

            // ツール結果にテキストが1つも無い場合(画像のみ返すツール等)でも、
            // ToolMessage自体は空文字のTextContentを1つ持たせておく(OCI側の必須項目対策)。
            if (textContents.Count == 0)
            {
                textContents.Add(new TextContent { Text = "" });
            }

            yield return new ToolMessage
            {
                ToolCallId = m.ToolCallId,
                Content = textContents
            };

            if (imageContents.Count > 0)
            {
                yield return new UserMessage { Content = imageContents };
            }

            yield break;
        }

        // role: assistant + tool_calls(往復時の整合性のため ToolCalls を保持)
        if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
        {
            var ociToolCalls = new List<ToolCall>();
            foreach (var tc in m.ToolCalls)
            {
                ociToolCalls.Add(new FunctionCall
                {
                    Id = tc.Id,
                    Name = tc.Function.Name,
                    Arguments = tc.Function.Arguments
                });
            }

            var contents = new List<ChatContent>();
            if (m.Content.ValueKind == JsonValueKind.String)
            {
                var text = m.Content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    contents.Add(new TextContent { Text = text });
                }
            }

            yield return new AssistantMessage
            {
                Content = contents,
                ToolCalls = ociToolCalls
            };
            yield break;
        }

        var contents2 = ToOciContents(m.Content);

        yield return m.Role switch
        {
            "system"    => new SystemMessage    { Content = contents2 },
            "assistant" => new AssistantMessage { Content = contents2 },
            _           => new UserMessage      { Content = contents2 } // "user" / その他フォールバック
        };
    }

    /// <summary>
    /// 直前の assistant.tool_calls に対応しない tool メッセージを破棄する。
    /// OCI は tool 結果に対応する tool 要求が無いと 400 を返すため、変換前に除外する。
    /// </summary>
    public static List<OpenAIMessage> DropOrphanToolMessages(
        IEnumerable<OpenAIMessage> messages,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var result = new List<OpenAIMessage>();
        var pendingIds = new HashSet<string>(StringComparer.Ordinal);
        int dropped = 0;

        foreach (var m in messages)
        {
            if (m.Role == "assistant")
            {
                pendingIds = m.ToolCalls is { Count: > 0 }
                    ? m.ToolCalls.Select(tc => tc.Id).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }
            else if (m.Role == "tool")
            {
                if (string.IsNullOrEmpty(m.ToolCallId) || !pendingIds.Remove(m.ToolCallId))
                {
                    dropped++;
                    logger?.LogWarning("Orphan tool message dropped: tool_call_id={ToolCallId}", m.ToolCallId);
                    continue;
                }
            }
            else
            {
                // user / system が挟まったら、直前の tool_calls への対応は終わったとみなす
                pendingIds.Clear();
            }

            result.Add(m);
        }

        if (dropped > 0)
        {
            logger?.LogWarning("Dropped {Count} orphan tool message(s)", dropped);
        }
        return result;
    }

    /// <summary>
    /// OpenAI の message.content(文字列 or マルチモーダル配列)を OCI の ChatContent[] に変換。
    /// 対応: {"type":"text","text":...} → TextContent
    ///       {"type":"image_url","image_url":{"url":...,"detail":...}} → ImageContent
    /// 未対応の type(input_audio 等)は現時点ではスキップする(設計書8章: フェーズ2以降で対応)。
    /// </summary>
    private static List<ChatContent> ToOciContents(JsonElement content)
    {
        var result = new List<ChatContent>();

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    result.Add(new TextContent { Text = text });
                }
                break;

            case JsonValueKind.Array:
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object ||
                        !part.TryGetProperty("type", out var typeEl))
                    {
                        continue;
                    }

                    var type = typeEl.GetString();
                    switch (type)
                    {
                        case "text":
                            if (part.TryGetProperty("text", out var textEl))
                            {
                                var t = textEl.GetString();
                                if (!string.IsNullOrEmpty(t))
                                {
                                    result.Add(new TextContent { Text = t });
                                }
                            }
                            break;

                        case "image_url":
                            if (part.TryGetProperty("image_url", out var imgEl))
                            {
                                var url = imgEl.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                                if (!string.IsNullOrEmpty(url))
                                {
                                    var imageUrl = new ImageUrl { Url = url };
                                    if (imgEl.TryGetProperty("detail", out var detailEl) &&
                                        detailEl.ValueKind == JsonValueKind.String &&
                                        Enum.TryParse<ImageUrl.DetailEnum>(
                                            detailEl.GetString(), ignoreCase: true, out var detail))
                                    {
                                        imageUrl.Detail = detail;
                                    }
                                    result.Add(new ImageContent { ImageUrl = imageUrl });
                                }
                            }
                            break;

                        // input_audio 等、未対応の type は現状スキップ(設計書8章 オープン事項)
                        default:
                            break;
                    }
                }
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;

            default:
                // 想定外の形。生JSONをテキストとしてフォールバック。
                result.Add(new TextContent { Text = content.GetRawText() });
                break;
        }

        return result;
    }

    /// <summary>
    /// OpenAI Tools[] → OCI FunctionDefinition[]。
    /// 設計書 6章: parameters の JSON Schema はほぼ互換、非対応キーワードは除去(SanitizeSchemaForOci)。
    /// </summary>
    public static List<ToolDefinition>? ToOciTools(List<OpenAITool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        var result = new List<ToolDefinition>();
        foreach (var t in tools)
        {
            if (t.Type != "function")
            {
                continue;
            }

            var jo = JObject.Parse(t.Function.Parameters.GetRawText());
            SanitizeSchemaForOci(jo);

            result.Add(new FunctionDefinition
            {
                Name = t.Function.Name,
                Description = t.Function.Description ?? "",
                Parameters = jo
            });
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Gemini(Vertex AI経由)は JSON Schema のフルセットではなく、制限された
    /// OpenAPI 3.0 サブセットしか受け付けない(公式ドキュメント: type/format/description/
    /// nullable/enum/maxItems/minItems/properties/required/items/propertyOrdering 等)。
    /// exclusiveMinimum/exclusiveMaximum 等の未対応キーワードが含まれると
    /// "Unknown name ... Cannot find field" エラーで拒否される(実機確認済み)。
    /// properties/items配下を含め再帰的に非対応キーワードを除去する。
    /// </summary>
    private static readonly HashSet<string> UnsupportedSchemaKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$defs", "$ref", "$comment",
        "exclusiveMinimum", "exclusiveMaximum",
        "additionalProperties", "patternProperties", "unevaluatedProperties",
        "const", "examples", "default",
        "contentEncoding", "contentMediaType",
        "if", "then", "else",
        "allOf", "anyOf", "oneOf", "not",
        "multipleOf", "pattern",
    };

private static void SanitizeSchemaForOci(JObject schema)
    {
        foreach (var key in UnsupportedSchemaKeywords)
        {
            schema.Remove(key);
        }

        // Gemini Vertex AI Function Calling の parameters は全プロパティに
        // `type` フィールドが必須("schema didn't specify the schema type field" 対策)。
        // MCPツール等で type 省略されたスキーマを補完する(properties / items から再帰前)。
        EnsureTypeField(schema);

        if (schema.TryGetValue("properties", out var propsToken) && propsToken is JObject props)
        {
            foreach (var prop in props.Properties())
            {
                if (prop.Value is JObject propSchema)
                {
                    SanitizeSchemaForOci(propSchema);
                }
            }
        }

        if (schema.TryGetValue("items", out var itemsToken))
        {
            if (itemsToken is JObject itemsSchema)
            {
                SanitizeSchemaForOci(itemsSchema);
            }
            else if (itemsToken is JArray itemsArray)
            {
                foreach (var item in itemsArray)
                {
                    if (item is JObject itemSchema)
                    {
                        SanitizeSchemaForOci(itemSchema);
                    }
                }
            }
        }
    }

    /// <summary>
    /// OpenAI の reasoning_effort → OCI の ReasoningEffortEnum。
    /// 未指定(null/空文字)は null を返し、OCI へはフィールド自体を送信しない
    /// (非対応モデルが存在自体を拒否する可能性があるため。呼び出し側の責任で明示指定時のみ透過)。
    /// OCI が対応するのは NONE/MINIMAL/LOW/MEDIUM/HIGH のみ。xhigh/max 等は 400 扱い。
    /// </summary>
    public static GenericChatRequest.ReasoningEffortEnum? ToOciReasoningEffort(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }
        return reasoningEffort.Trim().ToLowerInvariant() switch
        {
            "none" => GenericChatRequest.ReasoningEffortEnum.None,
            "minimal" => GenericChatRequest.ReasoningEffortEnum.Minimal,
            "low" => GenericChatRequest.ReasoningEffortEnum.Low,
            "medium" => GenericChatRequest.ReasoningEffortEnum.Medium,
            "high" => GenericChatRequest.ReasoningEffortEnum.High,
            _ => throw new ArgumentException(
                $"Unsupported reasoning_effort '{reasoningEffort}'. Supported: none, minimal, low, medium, high.")
        };
    }

    /// <summary>
    /// Gemini Vertex AI の Function Calling は properties 内の各パラメータに
    /// `type` 必須。MCP由来のスキーマで省略されている場合に補完する。
    /// ・properties あり → "object" ・items あり → "array" ・それ以外 → "string"
    /// </summary>
    private static void EnsureTypeField(JObject schema)
    {
        if (schema.ContainsKey("type"))
        {
            return;
        }

        if (schema.ContainsKey("properties"))
        {
            schema["type"] = "object";
        }
        else if (schema.ContainsKey("items"))
        {
            schema["type"] = "array";
        }
        else
        {
            // Leaf ノードは大半が string(MCPツールのパラメータはほぼ文字列)。
            // 整数/配列が必要なら送信側で明示的に type を付ける。
            schema["type"] = "string";
        }
    }
}
