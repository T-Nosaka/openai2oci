using openai2oci.Converters;
using openai2oci.Models;
using openai2oci.Services;
using Oci.GenerativeaiinferenceService.Models;
using Oci.GenerativeaiinferenceService.Responses;
using System.Text.Json;

namespace openai2oci.Endpoints;

/// <summary>
/// OpenAI 互換エンドポイント(設計書 4章)。
/// </summary>
public static class OpenAIEndpoints
{
    public static void MapOpenAIEndpoints(this IEndpointRouteBuilder endpoints, AppSetinfo setinfo)
    {
        // --- GET /v1/models (設計書 4.1: 案A 固定リスト。appsettings.json の OpenAI2Oci:Models + EmbeddingModels から生成) ---
        endpoints.MapGet("/v1/models", () =>
        {
            var chatModels = setinfo.Models.Select(m => new OpenAIModel(m.Id));
            var embedModels = setinfo.EmbeddingModels.Select(m => new OpenAIModel(m.Id));
            var data = chatModels.Concat(embedModels);
            return Results.Ok(new OpenAIModelList(data));
        });

        // --- POST /v1/embeddings (OpenAI互換: Cohere Embed 4 想定) ---
        endpoints.MapPost("/v1/embeddings", async (HttpContext ctx, OCIAIChatEngine engine) =>
        {
            CreateEmbeddingRequest? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<CreateEmbeddingRequest>();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = $"Malformed JSON body: {ex.Message}" }
                });
            }

            if (body is null || string.IsNullOrEmpty(body.Model))
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = "Missing 'model' field." }
                });
            }

            if (body.Input.ValueKind == JsonValueKind.Undefined || body.Input.ValueKind == JsonValueKind.Null)
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = "Missing 'input' field." }
                });
            }

            var embeddingModel = setinfo.FindEmbeddingModel(body.Model);
            if (embeddingModel is null)
            {
                return Results.NotFound(new
                {
                    error = new
                    {
                        type = "invalid_request_error",
                        message = $"Model '{body.Model}' is not configured. " +
                                  "Add it to OpenAI2Oci:EmbeddingModels in appsettings.json."
                    }
                });
            }

            // input 正規化: string | string[] のみ対応(数値配列は OCI 非対応)
            List<string> inputs;
            try
            {
                inputs = ParseEmbeddingInput(body.Input);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = ex.Message }
                });
            }

            if (inputs.Count == 0)
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = "'input' must not be empty." }
                });
            }

            // encoding_format 検証
            var encodingFormat = body.EncodingFormat ?? "float";
            if (encodingFormat != "float" && encodingFormat != "base64")
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = $"Unsupported encoding_format '{encodingFormat}'. Use 'float' or 'base64'." }
                });
            }

            // dimensions 検証(Cohere Embed v4: 256/512/1024/1536)
            int? dimensions = body.Dimensions;
            if (dimensions.HasValue && dimensions.Value != 256 && dimensions.Value != 512 && dimensions.Value != 1024 && dimensions.Value != 1536)
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = $"Unsupported dimensions '{dimensions}'. Supported: 256, 512, 1024, 1536 (Cohere Embed v4)." }
                });
            }

            EmbedTextResponse ociResponse;
            try
            {
                ociResponse = await engine.EmbedAsync(
                    embeddingModel.AuthProfileId, body.Model, inputs, dimensions);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new
                    {
                        error = new
                        {
                            type = "invalid_request_error",
                            message = $"OCI Embed error: {ex.Message}"
                        }
                    },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var embeddings = ociResponse.EmbedTextResult?.Embeddings;
            if (embeddings is null)
            {
                return Results.Json(
                    new
                    {
                        error = new { type = "server_error", message = "OCI returned no embeddings." }
                    },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            var data = new List<object>();
            for (int i = 0; i < embeddings.Count; i++)
            {
                object embeddingValue;
                if (encodingFormat == "base64")
                {
                    // float[] -> little-endian bytes -> base64(OpenAI互換)
                    var floats = embeddings[i];
                    var bytes = new byte[floats.Count * 4];
                    for (int j = 0; j < floats.Count; j++)
                    {
                        var fb = BitConverter.GetBytes(floats[j]);
                        Buffer.BlockCopy(fb, 0, bytes, j * 4, 4);
                    }
                    embeddingValue = Convert.ToBase64String(bytes);
                }
                else
                {
                    embeddingValue = embeddings[i];
                }

                data.Add(new
                {
                    @object = "embedding",
                    index = i,
                    embedding = embeddingValue
                });
            }

            // OCI は usage を返さないため 0 埋め(OpenAI互換形のみ維持)
            var usage = new { prompt_tokens = 0, total_tokens = 0 };

            return Results.Ok(new
            {
                @object = "list",
                data,
                model = body.Model,
                usage
            });
        });

        // --- POST /v1/chat/completions (設計書 4.2 / 6章 / 7章) ---
        endpoints.MapPost("/v1/chat/completions", async (HttpContext ctx, OCIAIChatEngine engine) =>
        {
            ChatCompletionRequest? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<ChatCompletionRequest>();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = $"Malformed JSON body: {ex.Message}" }
                });
            }

            if (body is null || string.IsNullOrEmpty(body.Model))
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = "Missing 'model' field." }
                });
            }

            if (body.Messages is null || body.Messages.Count == 0)
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = "Missing 'messages' field." }
                });
            }

            // モデル設定解決(appsettings.json の OpenAI2Oci:Models に無いモデルは拒否)
            var modelSetting = setinfo.FindModel(body.Model);
            if (modelSetting is null)
            {
                return Results.NotFound(new
                {
                    error = new
                    {
                        type = "invalid_request_error",
                        message = $"Model '{body.Model}' is not configured. " +
                                  "Add it to OpenAI2Oci:Models in appsettings.json."
                    }
                });
            }

            // パラメータ解決: リクエスト明示値 > appsettings.json のモデル既定値
            var maxTokens = body.MaxTokens ?? modelSetting.MaxTokens;
            var temperature = body.Temperature ?? modelSetting.Temperature;
            var topP = body.TopP ?? modelSetting.TopP;
            var topK = body.TopK ?? modelSetting.TopK;

            // OpenAI → OCI 変換(messages + tools)
            List<Message> ociMessages;
            try
            {
                ociMessages = body.Messages.SelectMany(OpenAIToOciConverter.ToOciMessages).ToList();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = new { type = "invalid_request_error", message = ex.Message }
                });
            }

            var ociTools = OpenAIToOciConverter.ToOciTools(body.Tools);

            // ストリーミング応答: OCI REST を直叩きしてトークン単位で SSE 素通し(OCIAIChatEngine.ChatStreamAsync)
            if (body.Stream == true)
            {
                try
                {
                    await engine.ChatStreamAsync(
                        modelSetting.AuthProfileId, body.Model, ociMessages, maxTokens, temperature, topP, topK,
                        ctx.Response,
                        frequencyPenalty: body.FrequencyPenalty,
                        presencePenalty: body.PresencePenalty,
                        tools: ociTools,
                        ct: ctx.RequestAborted);
                }
                catch (Exception ex)
                {
                    // ストリーミング開始前なら OpenAI 形式エラーで返せる。開始後(HasStarted)は切断のみ。
                    if (!ctx.Response.HasStarted)
                    {
                        return Results.Json(
                            new
                            {
                                error = new
                                {
                                    type = "invalid_request_error",
                                    message = $"OCI Chat error: {ex.Message}"
                                }
                            },
                            statusCode: StatusCodes.Status502BadGateway);
                    }
                }
                return Results.Empty;
            }

            ChatResponse ociResponse;
            try
            {
                ociResponse = await engine.ChatAsync(
                    modelSetting.AuthProfileId, body.Model, ociMessages, maxTokens, temperature, topP, topK,
                    frequencyPenalty: body.FrequencyPenalty,
                    presencePenalty: body.PresencePenalty,
                    tools: ociTools);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new
                    {
                        error = new
                        {
                            type = "invalid_request_error",
                            message = $"OCI Chat error: {ex.Message}"
                        }
                    },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            // 非ストリーミング
            var genericResponse = ociResponse.ChatResult?.ChatResponse as GenericChatResponse;
            var choices = new List<object>();
            if (genericResponse?.Choices != null)
            {
                for (int i = 0; i < genericResponse.Choices.Count; i++)
                {
                    var (content, toolCalls, finishReason) = OciToOpenAIConverter.ConvertChoice(genericResponse.Choices[i]);
                    choices.Add(new
                    {
                        index = i,
                        message = new { role = "assistant", content, tool_calls = toolCalls },
                        finish_reason = finishReason
                    });
                }
            }

            // OCIレスポンスのUsageをOpenAI形式にマッピング(設計書13章 T-3)。
            // OCI側が値を返さない場合はnull-合体で0にフォールバックする。
            var usage = new
            {
                prompt_tokens = genericResponse?.Usage?.PromptTokens ?? 0,
                completion_tokens = genericResponse?.Usage?.CompletionTokens ?? 0,
                total_tokens = genericResponse?.Usage?.TotalTokens ?? 0
            };

            return Results.Ok(new
            {
                id = $"chatcmpl-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = body.Model,
                choices,
                usage
            });
        });
    }

    /// <summary>
    /// OpenAI embeddings の input (string | string[] | number[] | number[][]) を List&lt;string&gt; に正規化。
    /// 現状 OCI はテキストのみサポートのため string / string[] のみ受け付ける。
    /// 数値配列が来た場合は 400 扱いとする。
    /// </summary>
    private static List<string> ParseEmbeddingInput(JsonElement input)
    {
        switch (input.ValueKind)
        {
            case JsonValueKind.String:
                var s = input.GetString();
                return string.IsNullOrEmpty(s) ? new List<string>() : new List<string> { s };

            case JsonValueKind.Array:
                var list = new List<string>();
                foreach (var el in input.EnumerateArray())
                {
                    switch (el.ValueKind)
                    {
                        case JsonValueKind.String:
                            var str = el.GetString();
                            if (!string.IsNullOrEmpty(str))
                            {
                                list.Add(str);
                            }
                            break;
                        case JsonValueKind.Number:
                            throw new ArgumentException("Numeric token input is not supported for OCI embeddings. Use string or array of strings.");
                        case JsonValueKind.Array:
                            throw new ArgumentException("Nested array input is not supported for OCI embeddings.");
                        default:
                            throw new ArgumentException($"Unsupported input element type: {el.ValueKind}");
                    }
                }
                return list;

            case JsonValueKind.Number:
                throw new ArgumentException("Numeric input is not supported for OCI embeddings.");

            default:
                throw new ArgumentException($"Unsupported input type: {input.ValueKind}");
        }
    }
}
