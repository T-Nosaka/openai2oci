using openai2oci.Converters;
using openai2oci.Models;
using openai2oci.Services;
using Oci.GenerativeaiinferenceService.Models;
using Oci.GenerativeaiinferenceService.Responses;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace openai2oci.Endpoints;

/// <summary>
/// OpenAI 互換エンドポイント(設計書 4章)。
/// </summary>
public static class OpenAIEndpoints
{
    public static void MapOpenAIEndpoints(this IEndpointRouteBuilder endpoints, AppSetinfo setinfo)
    {
        // --- GET /v1/models (設計書 4.1: 案A 固定リスト。appsettings.json の OpenAI2Oci:Models から生成) ---
        endpoints.MapGet("/v1/models", () =>
        {
            var data = setinfo.Models.Select(m => new OpenAIModel(m.Id));
            return Results.Ok(new OpenAIModelList(data));
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

            // ストリーミング応答(設計書 7章 Phase 2: とりあえず OCI 応答を単一チャンクにまとめて送出)
            if (body.Stream == true)
            {
                await WriteSseAsync(ctx.Response, ociResponse, body.Model);
                return Results.Empty;
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

            return Results.Ok(new
            {
                id = $"chatcmpl-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = body.Model,
                choices,
                usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
            });
        });
    }

    /// <summary>
    /// SSE ストリーム出力。OCI の非ストリーミング応答を OpenAI ChatCompletionChunk 形式に変換して送出。
    /// 単一チャンクにまとめて送出するため、真のトークン単位ストリーミングは未実装(設計書 7.2 課題)。
    /// </summary>
    private static async Task WriteSseAsync(HttpResponse response, ChatResponse ociResponse, string model)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";

        var id = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Chunk 1: role
        await WriteChunk(response, id, created, model, delta: new { role = "assistant" }, finishReason: null);

        var generic = ociResponse.ChatResult?.ChatResponse as GenericChatResponse;
        if (generic?.Choices is { Count: > 0 })
        {
            var (content, toolCalls, _) = OciToOpenAIConverter.ConvertChoice(generic.Choices[0]);

            // Chunk 2: tool_calls 優先
            if (toolCalls is { Count: > 0 })
            {
                await WriteChunk(response, id, created, model, delta: new { tool_calls = toolCalls }, finishReason: null);
            }
            // Chunk 2': text content
            else if (!string.IsNullOrEmpty(content))
            {
                await WriteChunk(response, id, created, model, delta: new { content }, finishReason: null);
            }
        }

        // Chunk 3: finish_reason
        await WriteChunk(response, id, created, model, delta: new { }, finishReason: "stop");

        // 終端マーカー
        await response.WriteAsync("data: [DONE]\n\n");
        await response.Body.FlushAsync();
    }

    private static async Task WriteChunk(HttpResponse response, string id, long created, string model, object delta, string? finishReason)
    {
        var chunk = new
        {
            id,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = new[]
            {
                new { index = 0, delta, finish_reason = (string?)finishReason }
            }
        };
        var json = JsonSerializer.Serialize(chunk, sseJsonOptions);
        await response.WriteAsync($"data: {json}\n\n");
        await response.Body.FlushAsync();
    }

    private static readonly JsonSerializerOptions sseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
