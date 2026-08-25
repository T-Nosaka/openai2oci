# OCI Generative AI 向け OpenAI互換プロキシサーバー 仕様書

- バージョン: 1.0 (Specification)
- 作成日: 2026-08-24
- 更新日: 2026-08-25(仕様書化:旧 Draft を実装同期済の仕様へ全面改訂、Cohere 関連を全削除)
- ステータス: 全機能実装済み・opencode で動作確認済(ストリーミング・Function Calling・マルチモーダル)

> 本書は旧版(Draft)からの仕様確定版です。1〜13章は実装済み項目を**仕様**として記述しています。検討中・未実装の項目は各章の「制約」/「TODO」節、または 13章「オープン事項(残存課題)」を参照してください。実装メモ・決定履歴は 15章「実装メモ」を参照してください。

---

## 1. 概要

### 1.1 背景

OCI Generative AI サービスは独自のリクエスト/レスポンス形式（OCI SDK / REST）を持つ。OCI自身もOpenAI互換エンドポイント（`/openai/v1/...`）を提供しているが、以下の理由により、そのままではエージェント系クライアント（opencode等）から利用できない、または要件を満たさない。

| 課題 | 詳細 |
|---|---|
| モデル一覧APIが無い | OCI公式OpenAI互換エンドポイントには `/v1/models` に相当するものが提供されておらず、モデル自動検出を行うクライアント（opencode等）が起動時に失敗する |
| サードパーティゲートウェイの限界 | LiteLLM等の汎用ゲートウェイを検証したが、マルチモーダル非対応、一部モデルで動作しない、モデル固有の詳細パラメータ（reasoning_effort, safety_mode等）を渡せない、という制約があった |
| モデルファミリーごとの非互換性 | OCIは内部的に `GenericChatRequest`（Llama/xAI/OpenAI/xAI/Google系）を使用。`CohereChatRequest` 系は本プロキシのスコープ外(`cohere.*` モデル ID を登録してもOCIがエラーを返すため設定してはならない) |

### 1.2 目的

OCI Generative AIをバックエンドとし、OpenAI Chat Completions API互換のインターフェースを提供する **自前制御のプロキシサーバー** を構築する。これにより：

- opencode等、OpenAI互換クライアントからOCIのモデルを問題なく利用できるようにする
- マルチモーダル（画像等）入力に対応する
- モデルごとの細かいパラメータ制御を可能にする
- ツール呼び出し（Function Calling）を正しく往復させる
- 将来的にストリーミング応答にも対応する

### 1.3 非目的（スコープ外・将来検討）

- OCIのResponses API / Conversations API（ステートフルAPI）への対応（初期版はChat Completions相当のステートレスAPIのみ）
- Embeddings / Rerank API
- Files / Vector Stores / Containers 等のエージェント基盤API
- 複数テナント/複数ユーザーでの認証多重化（初期版はシングルユーザー・個人利用を想定）

---

## 2. 全体アーキテクチャ

### 2.1 構成図（概念）

```
[opencode / OpenAI互換クライアント]
        │  HTTPS
        │  Authorization: Bearer <プロキシ用APIキー>
        ▼
┌─────────────────────────────────────────┐
│  OCI-OpenAI互換プロキシ (ASP.NET Core)     │
│                                           │
│  ┌───────────────┐   ┌────────────────┐  │
│  │ 認証ミドルウェア │   │ モデルルーター  │  │
│  └───────────────┘   └────────────────┘  │
│         │                     │           │
│  ┌──────▼─────────────────────▼────────┐ │
│  │   リクエスト変換層                    │ │
│  │  OpenAI形式 → OCI形式(Generic) │ │
│  └──────┬────────────────────────────────┘ │
│         │                                  │
│  ┌──────▼───────────┐                      │
│  │ OCI呼び出し層      │                      │
│  │ (SDK or REST直叩き)│                      │
│  └──────┬───────────┘                      │
│         │                                  │
│  ┌──────▼────────────────────────────────┐ │
│  │   レスポンス変換層                      │ │
│  │  OCI形式 → OpenAI形式（非stream/stream）│ │
│  └────────────────────────────────────────┘ │
└─────────────────────────────────────────┘
        │  HTTPS (署名 or Bearerトークン)
        ▼
[OCI Generative AI Inference Service]
```

### 2.2 技術スタック

| 項目 | 内容 |
|---|---|
| 言語/フレームワーク | C# / ASP.NET Core Minimal API |
| ターゲット | .NET 10 |
| OCI連携 | OCI公式SDK `OCI.DotNetSDK.Generativeaiinference 135.0.0` を採用(3.1節) |
| 設定管理 | `appsettings.json` の `OpenAI2Oci` セクションに `IConfiguration` 経由でバインド。詳細 10章 / 15.3節 |
| ロギング | `Microsoft.Extensions.Logging`(ASP.NET Core 標準) |

---

## 3. 実装方式の決定事項

### 3.1 OCI呼び出し方式：**OCI公式SDK**

OCI公式SDK `OCI.DotNetSDK.Generativeaiinference` を採用。

| 項目 | 内容 |
|---|---|
| 利用パッケージ | `OCI.DotNetSDK.Generativeaiinference 135.0.0`(+ 推移的依存 `OCI.DotNetSDK.Common`) |
| 認証 | `ConfigFileAuthenticationDetailsProvider("~/.oci/config", profile)` で **OCI 署名認証**。Bearer 形式への切替は将来課題。 |
| ストリーミング | SDK の同期 API のみ使用。OCI SDK の SSE サポートは公式に未確認のため、真のトークン単位ストリーミングは未対応(7.2節)。OCI 応答を単一チャンクにまとめて SSE 送出する方式で実装。 |
| 互換性問題 | `GenericChatRequest` の `Temperature` / `TopP` / `FrequencyPenalty` / `PresencePenalty` が非Nullable `double` で常に JSON 出力される問題を、`OptionalGenericChatRequest`(`new` でプロパティ隠し + Newtonsoft `[JsonProperty(NullValueHandling=Ignore)]`)で回避(Grok 等、フィールド存在を許容しないモデルに対応) |

REST直叩きは将来真のトークンストリーミングが必要になった場合に再検討。

### 3.2 モデルファミリー判定ロジック

**全モデル `GenericChatRequest` 固定**。`CohereChatRequest` 系は本プロキシのスコープ外。

| モデルID prefix | 使用するOCIリクエスト形式 |
|---|---|
| `meta.*`, `openai.*`, `xai.*`, `google.*` | `GenericChatRequest` |
| `cohere.*` | **スコープ外**(登録してもOCIがエラーを返す) |

新モデル追加時も常に `GenericChatRequest` を使うため、判定テーブルは不要。将来 `CohereChatRequest` 系を対応する場合は明示的に分岐を追加する。

---

## 4. 提供エンドポイント仕様

### 4.1 `GET /v1/models`

**目的**: opencode等のクライアントによる自動モデル検出に対応する(OCI公式には存在しないため自前実装)。

**レスポンス(OpenAI互換形式)**:

```json
{
  "object": "list",
  "data": [
    { "id": "openai.gpt-oss-120b", "object": "model", "owned_by": "oci" },
    { "id": "google.gemini-2.5-pro", "object": "model", "owned_by": "oci" },
    { "id": "xai.grok-4.3", "object": "model", "owned_by": "oci" }
  ]
}
```

**データソース**: `appsettings.json` の `OpenAI2Oci:Models` 配列(固定リスト案A)。

- 各エントリは `ModelSetting` 構造体(`Id` / `AuthProfileId` / `MaxTokens` / `Temperature` / `TopP` / `TopK`)で、`IConfiguration.GetSection().Bind()` で読み込み
- `Id` のみが `/v1/models` 返却値に使用される。残りは `chat/completions` の既定値として使われる
- 起動時に `OpenAI2Oci:OciAuthProfiles` と `Models[].AuthProfileId` の整合性チェック(fail-fast)

> 注: Cohere 系モデル (`cohere.*`) は本プロキシのスコープ外(3.2節)。`Models` に含めないこと。

### 4.2 `POST /v1/chat/completions`

**対応パラメータ(OpenAI準拠)**:

| OpenAIパラメータ | 対応 | 備考 |
|---|---|---|
| `model` | ○ | `appsettings.json` の `Models[].Id` と完全一致必須(不一致は 404) |
| `messages` | ○ | role: system/user/assistant/tool。assistant + tool_calls も保持(往復整合性) |
| `temperature` | ○ | リクエスト値 > `ModelSetting.Temperature`(既定値) |
| `top_p` | ○ | リクエスト値 > `ModelSetting.TopP`(既定値) |
| `max_tokens` | ○ | リクエスト値 > `ModelSetting.MaxTokens`(既定値) |
| `stream` | ○ | `true` で SSE 応答(7章) |
| `tools` | ○ | Function Calling(6章) |
| `tool_choice` | △ | 受信するがOCIには渡さない(OCI既定動作) |
| `frequency_penalty` / `presence_penalty` | ○ | `null` の場合はフィールド自体を送信しない(`OptionalGenericChatRequest` で Grok 等に対応、3.1節) |
| `top_k` | ○ | OpenAI 標準には無いOCI固有拡張。`ModelSetting.TopK` またはリクエスト値 |
| `stop` | ○ | |
| `response_format` | △ | 未対応(TODO) |
| メッセージ内 `image_url` | ○ | 8章マルチモーダル |

**OCI固有拡張パラメータの透過方針**: `top_k` のみ対応。他は将来 `extra_body` 等で透過する設計余地あり。

---

## 5. 認証設計

| 項目 | 内容 |
|---|---|
| クライアント→プロキシ | `Authorization: Bearer <プロキシ独自APIキー>` を `AuthMiddleware.UseProxyApiKey` で検証。**空設定時は認証なし**。設定時は `CryptographicOperations.FixedTimeEquals` でタイミング攻撃耐性比較。 |
| プロキシ→OCI | `ConfigFileAuthenticationDetailsProvider("~/.oci/config", profile)` で **OCI 署名認証**。複数プロファイル対応(リージョン・テナント違いを `OciAuthProfiles` 配列で管理)。OCI Generative AI API キー(Bearer形式)への切替は将来課題。 |
| 秘密情報管理 | コンパートメント OCID は `appsettings.json` の `OpenAI2Oci:OciAuthProfiles[].OciCompartmentId` に記述。OCI 署名鍵 (`~/.oci/config` / `~/.oci/key.pem`) はファイルシステム上で管理。リポジトリには OCID のみコミット(運用はテナント分離前提)。 |

---

## 6. Function Calling(ツール呼び出し)変換仕様

**プロキシは変換のみ実行する。ツール実行はクライアント(opencode 等)側の責務。**

| 項目 | OpenAI形式 | OCI形式 | 変換実装 |
|---|---|---|---|
| ツール定義 | `tools: [{type:"function", function:{name, description, parameters}}]` | `FunctionDefinition{Name, Description, Parameters}` | `OpenAIToOciConverter.ToOciTools`。`parameters` の `$schema` 等 Gemini 互換外のキーワードを再帰除去(`SanitizeSchemaForOci`)。`type` 未指定プロパティは `"string"` に補完(Gemini 必須) |
| ツール呼び出し判定 | `finish_reason == "tool_calls"` | `AssistantMessage.ToolCalls.Count > 0` | `OciToOpenAIConverter.ConvertChoice` で `tool_calls` 有無で `finish_reason` 切替 |
| ツールコールID | 必須 | **OCI応答で欠落する場合あり** | 空 ID は `Guid.NewGuid()` で補完し OpenAI 形式で送出。クライアントが echo-back する同 ID を `ToolMessage.ToolCallId` にそのまま使用 |
| ツール結果(テキスト) | `role:"tool", tool_call_id, content` | `ToolMessage{ToolCallId, Content:[TextContent]}` | `OpenAIToOciConverter.ToOciMessages` で 1:1 変換 |
| ツール結果(画像) | tool role に画像不可 → 別途 `role:"user"` で画像 | 同様に `UserMessage` に分割 | `ToOciMessages` で `ToolMessage(テキスト部)` + `UserMessage(画像部)` を `yield return` で分割出力 |
| 複数ツール呼び出し | 配列で複数返る | 同様に配列 | `ToolCalls` リストをループ変換 |
| assistant 往復時 | `role:"assistant"` + `tool_calls` | `AssistantMessage{Content, ToolCalls}` | `ToOciMessages` で `ToolCalls` を保持(欠落時 OCI が `Invalid parameter 'toolCallId'` を返す) |

---

## 7. ストリーミング対応方針

OCI SDK の SSE サポートが公式に未確認のため、現状は **OCI 応答を単一チャンクにまとめて送出する暫定版** を採用。

### 7.1 動作

- リクエスト `stream: true` → レスポンス `Content-Type: text/event-stream`
- チャンク構成(計 3 + 終端):
  1. role チャンク: `{delta: {role: "assistant"}}`
  2. content / tool_calls チャンク: `{delta: {content: "..."} or tool_calls: [...]}`(tool_calls 優先)
  3. finish_reason チャンク: `{delta: {}, finish_reason: "stop" or "tool_calls"}`
- 終端: `data: [DONE]\n\n`

### 7.2 制約(真のトークン単位ストリーミングは未対応)

OCI SDK の同期 API で応答全体を取得 → 単一チャンクにまとめて送出しているため、UX 上は一括表示。OCI SDK の `IsStream=true` サポートが公式に確認でき次第、真のトークンストリーミング実装に切替予定。

---

## 8. マルチモーダル対応方針

OCI Generative AI の `ImageContent` は `Gemini` / `Grok` 等の対応モデルでのみ動作。OCI モデルごとの対応可否は 15.7節「モデルごとのマルチモーダル対応状況」を参照。

| 入力種別 | OpenAI形式 | OCI形式 | 対応状況 |
|---|---|---|---|
| 画像 | `image_url`(base64 data URL または 通常URL) + `detail` | `ImageContent{ImageUrl, Detail}` | 実装済(`OpenAIToOciConverter.ToOciContents`) |
| 音声 | `input_audio` | `AudioContent{AudioUrl}` | 未対応(対応モデル追加時に実装) |
| 動画 | (OpenAI標準では非対応が多い) | `VideoContent{VideoUrl}` | 未対応 |
| PDF | `file` | (OCI 未対応) | 未対応 |

**opencode 側の宣言**: モデル定義に `"attachment": true` + `"modalities": {"input":["text","image"],"output":["text"]}` を付けたモデルでのみ画像添付 UI が有効化される(15.7節サンプル参照)。

---

## 9. エラーハンドリング方針

| シナリオ | HTTP | OpenAIエラー形式 |
|---|---|---|
| JSON パース失敗 | 400 | `{"error":{"type":"invalid_request_error","message":"Malformed JSON body: ..."}}` |
| `model` 欠落 | 400 | 同上 `"Missing 'model' field."` |
| `messages` 欠落 | 400 | 同上 `"Missing 'messages' field."` |
| `model` 未登録 | 404 | `"Model 'xxx' is not configured. Add it to OpenAI2Oci:Models..."` |
| `role:"tool"` で `tool_call_id` 欠落 | 400 | `"tool message requires tool_call_id"` |
| 起動時バリデーション失敗(fail-fast) | 起動しない | `InvalidOperationException` を例外送出 |
| 認証ミドルウェアでの Bearer 不一致 | 401 | `"Invalid or missing API key."` |
| OCI Chat 例外 | 502 | `"OCI Chat error: <OCI例外メッセージ>"` |

OCI 側エラー(401/403/429/5xx)は基本的に透過せず `502 + OCI例外メッセージ` でラップ(OCI 固有エラー構造を OpenAI 形式に正規化していないため)。

---

## 10. 設定管理

設定はすべて `appsettings.json` の `OpenAI2Oci` セクションに集約(IConfiguration バインド)。`appsetinfo.json` は廃止。

| 設定項目 | 例 | 管理方法 |
|---|---|---|
| OCI 認証プロファイル配列 | `OciAuthProfiles[].{Id, OciConfigProfile, OciCompartmentId}` | `appsettings.json` `OpenAI2Oci` セクション(15.3節) |
| プロキシ公開用APIキー | `""`(認証なし) / `"sk-..."` | `appsettings.json`(`OpenAI2Oci:ProxyApiKey`)。本番では環境変数 / `dotnet user-secrets` で上書き推奨 |
| 対応モデル一覧 | `Models[].{Id, AuthProfileId, MaxTokens, Temperature, TopP, TopK}` | `appsettings.json`(`OpenAI2Oci:Models`)。各エントリは `OciAuthProfiles` の `Id` を参照 |
| Kestrel バインド URL | `http://127.0.0.1:11434` | `appsettings.json` `Kestrel.Endpoints.Http.Url` |
| ログレベル | `Information` | `appsettings.json` `Logging.LogLevel` |

起動時に `OciAuthProfiles` と `Models[].AuthProfileId` の整合性をチェックし、不整合は `InvalidOperationException` で fail-fast。

---

## 11. ロギング方針

`Microsoft.Extensions.Logging` を用いて以下を記録(ASP.NET Core 標準):

- リクエスト受信時のモデル名(authProfileId、メッセージ数、tools 数)
- ツール呼び出しの名前・引数・結果概要
- OCI 呼び出しのレイテンシ・エラー
- 個人情報・APIキー等の秘匿情報はログに出力しない

---

## 12. 非機能要件

| 項目 | 方針 |
|---|---|
| 想定利用規模 | 個人利用・ローカル/小規模ホスティングを想定。高負荷対策は将来検討 |
| 可用性 | 単一プロセス稼働。冗長化は将来検討 |
| セキュリティ | プロキシ公開用APIキーによるアクセス制御(5章)。外部公開する場合はHTTPS終端・IP制限等を別途検討 |
| テナント | 単一プロキシで複数 OCI テナント/リージョンのモデルを登録可能(`OciAuthProfiles` 配列、10章) |

---

## 13. オープン事項(残存課題)

| ID | 項目 | 状態 | 影響 |
|---|---|---|---|
| T-1 | 真のトークン單位ストリーミング | **未対応** | UX 上は一括表示。OCI SDK の `IsStream=true` サポートが公式に確認でき次第対応(7.2節) |
| T-2 | `tool_choice` パラメータのOCI への透過 | **受信のみ・OCI未送信** | OCI 既定動作で動作するが、`required` / 特定関数指定等の明示は反映されない |
| T-3 | `usage` 統計(prompt_tokens / completion_tokens) | **常に 0** | クライアント側の usage 表示が機能しない。OCI 応答 `ChatResult.ChatResponse.Usage` から取得可能だが未実装 |
| T-4 | `response_format`(JSON Schema 構造化出力) | **未対応** | クライアントからの `response_format` フィールドは無視される |
| T-5 | 音声/動画/PDF マルチモーダル | **未対応** | 画像のみ対応(8章)。OCI モデル側のサポート状況に応じて順次対応 |

**スコープ外(対応しないことを確定)**:

- `CohereChatRequest` 系モデル: 3.2節にて `cohere.*` は本プロキシのスコープ外と決定。`Models` への登録も禁止。
- 認証の OCI Bearer 化: 現状は OCI 署名認証のみ。
- `Cohere` 系以外で今後分岐が必要になったモデルファミリー: 必要時に 3.2節の判定ロジックを再導入。

---

## 14. 参考資料

- OCI Chat Completions API: https://docs.oracle.com/en-us/iaas/Content/generative-ai/chat-completions-api.htm
- OCI OpenAI-Compatible Endpoints: https://docs.oracle.com/en-us/iaas/Content/generative-ai/openai-compatible-api.htm
- OCI Responses API: https://docs.oracle.com/en-us/iaas/Content/generative-ai/responses-api.htm
- OCI Generative AI API keys: https://docs.oracle.com/en-us/iaas/Content/generative-ai/api-keys.htm
- 既存資産: `C:\platform\ocitest1\ocitest1-master`（`OCIAIChatEngine.cs`, `OpenAIChatEngine.cs`, `McpFunctionAdapter.cs`）
- opencode Models設定: https://opencode.ai/v2/docs/models

---

## 15. 実装メモ（v0.2 時点）

### 15.1 ファイル構成

```
openai2oci/
├── AppSetinfo.cs                       (アプリ設定データクラス)
├── Program.cs                          (エントリポイント: 約50行)
├── Endpoints/
│   └── OpenAIEndpoints.cs              (/v1/models, /v1/chat/completions + SSE)
├── Middleware/
│   └── AuthMiddleware.cs               (Bearer API キー認証)
├── Converters/
│   ├── OpenAIToOciConverter.cs         (OpenAI → OCI: messages, tools, tool role)
│   └── OciToOpenAIConverter.cs         (OCI → OpenAI: text / tool_calls, ID 補完)
├── Models/
│   └── OpenAIDtos.cs                   (リクエスト/レスポンス record)
├── Services/
│   ├── OCIAIChatEngine.cs              (OCI SDK ラッパ、tool 実行ループは持たない)
│   └── OciClientExtensions.cs          (DI 登録拡張メソッド)
└── appsettings.json                    (Kestrel URL のみ保持)
```

### 15.2 採用した実装方針

| 設計書項目 | 決定 |
|---|---|
| 3.1 OCI呼び出し方式 | **OCI公式SDK** を採用（`OCI.DotNetSDK.Generativeaiinference 135.0.0`）。既存資産 `ocitest1` との整合性重視。Beraer 形式への切替は将来課題として残置。 |
| 3.2 モデルファミリー判定 | **`GenericChatRequest` 固定**(`CohereChatRequest` は対応外、3.2節)。分岐ロジックは導入しない。 |
| 4.1 モデル一覧データソース | **案A（固定リスト）**。`appsettings.json` の `OpenAI2Oci:Models` から生成。`ContentRootPath = AppContext.BaseDirectory` で VS 起動時の読み込み問題を回避。 |
| 5章 認証（プロキシ→OCI） | `ConfigFileAuthenticationDetailsProvider("~/.oci/config", profile)` で **OCI 署名認証** を使用。Bearer 形式には未切替。 |
| 6章 Function Calling | **プロキシは変換のみ**(A方針)。ツール実行はクライアント(opencode)側。再帰OCI呼び出しループは `OCIAIChatEngine` から削除済み（旧 `processToolCalls` 廃止）。 |
| 7章 ストリーミング | **Phase 2 暫定版**。OCI 応答を単一 SSE チャンクにまとめて送出。3 チャンク（role → content/tool_calls → finish_reason）+ `data: [DONE]`。真のトークン単位ストリーミングは未対応。`WriteSseAsync` で role/content/tool_calls/finish_reason を個別チャンクで送出後、`Results.Empty` を返す。 |

### 15.3 設定管理の実装形

設定はすべて `appsettings.json` の `OpenAI2Oci` セクションに記述(10章)。`appsetinfo.json` は廃止。

```jsonc
{
  "OpenAI2Oci": {
    "ProxyApiKey": "",
    "OciAuthProfiles": [
      {
        "Id": "defaultus",
        "OciConfigProfile": "DEFAULT_US",
        "OciCompartmentId": "ocid1.compartment.oc1..xxxxx"
      },
      {
        "Id": "defaultjp",
        "OciConfigProfile": "DEFAULT",
        "OciCompartmentId": "ocid1.compartment.oc1..yyyyy"
      }
    ],
    "Models": [
      {
        "Id": "google.gemini-2.5-pro",
        "AuthProfileId": "defaultjp",
        "MaxTokens": 65535,
        "Temperature": 0.3,
        "TopP": 1,
        "TopK": 1
      },
      {
        "Id": "xai.grok-4.3",
        "AuthProfileId": "defaultus",
        "MaxTokens": 131000,
        "Temperature": 0.3,
        "TopP": 1,
        "TopK": 1
      },
      {
        "Id": "openai.gpt-oss-120b",
        "AuthProfileId": "defaultjp",
        "MaxTokens": 16000,
        "Temperature": 0.3,
        "TopP": 1,
        "TopK": 1
      }
    ]
  }
}
```

- `ContentRootPath` を `AppContext.BaseDirectory` に固定(VSの"Project"プロファイルで `appsettings.json` が読めない問題を回避)
- 起動時に `OciAuthProfiles` の `Id` 重複・`OciCompartmentId` 空・`Models[].AuthProfileId` の存在を検証(fail-fast)
- リクエスト時のパラメータ解決: `body.Temperature ?? modelSetting.Temperature`(リクエスト値優先、無ければ `Models[]` の既定値)
- **Cohere 系モデル (`cohere.*`) は登録不可**(3.2節・13章)

### 15.4 snake_case / PascalCase マッピング

OpenAI プロトコルは `tool_call_id` / `tool_calls` のような snake_case フィールドを含むが、System.Text.Json 既定の camelCase 規則ではこれらにマッチしないため、`[JsonPropertyName]` 属性で明示的にマッピング:

| C# プロパティ | JSON フィールド | 状態 |
|---|---|---|
| `ToolCallId` | `tool_call_id` | ✓ 対応済 |
| `ToolCalls` | `tool_calls` | ✓ 対応済 |
| `ToolChoice` | `tool_choice` | △ 未対応（現状未使用） |
| その他 `Role` / `Content` / `Name` 等 | camelCase で一致 | ✓ |

### 15.5 動作確認履歴

| 日付 | 内容 |
|---|---|
| 2026-08-24 | opencode から `GET /v1/models` 動作確認（モデル 7 件返却） |
| 2026-08-24 | opencode から `POST /v1/chat/completions`(text) 動作確認 |
| 2026-08-24 | SSE 形式でのチャット応答表示確認(streaming) |
| 2026-08-24 | ツール呼び出し往復（多段: 7 連続 tool call）動作確認。seleniumsvr 経由で kakaku.com から REGZA 43M550M の価格情報を取得・要約 |

### 15.6 既知の制約 / 残 TODO

- **真のトークン単位ストリーミング**: OCI 応答を単一チャンクで送出しているため、UX 上は一括表示。真のストリーミングには OCI SDK の SSE サポート検証が必要。
- **`tool_choice` パラメータ**: 受信するが OCI には渡していない（OCI 既定動作）。
- **`usage` 統計**: 常に 0 を返却。OCI 応答の `ChatResult.ChatResponse.Usage` から取得可能だが未実装。
- **`response_format`**: JSON Schema 構造化出力、未対応。
- **マルチモーダル入力**: 実装済（`OpenAIToOciConverter.ToOciContents`）。`image_url`(base64/URL + `detail`) → OCI `ImageContent{ImageUrl}` に変換。opencode 側で `attachment: true` + `modalities.input: ["text","image"]` を宣言したモデルでのみ画像添付 UI が有効化される。
- **マルチモーダル tool 結果**: 設計書 6章の「tool role ではなく user に画像分割」実装済（`ToOciMessages` で ToolMessage(text) + UserMessage(images) に分割）。
- **OCI ToolCallId 欠落時 GUID 補完**: 実装済(`OciToOpenAIConverter.ConvertToolCalls`)。クライアントが echo-back する ID と一致するため、ToolMessage 変換時に同 ID がそのまま使われる。
- **Gemini Vertex AI 対応**: `OpenAIToOciConverter` で JSON Schema サニタイズ（`UnsupportedSchemaKeywords`: `$schema`/`exclusiveMinimum` 等22キーワード除去）+ `EnsureTypeField` によるtype自動補完（properties→object / items→array / その他→string）。MCPツール由来のtype省略スキーマ対策。
- **tool結果が画像のみの場合**: `ToOciMessages` で空TextContentを補完（OCI側の必須項目対策）。
- **finish_reasonマッピング**: `OciToOpenAIConverter.MapFinishReason` で STOP→stop, LENGTH→length, CONTENT_FILTER→content_filter に正規化。

### 15.7 モデルごとのマルチモーダル対応状況（2026-08-25 時点）

| OCI モデル ID | マルチモーダル | opencode.jsonc での宣言 |
|---|---|---|
| `google.gemini-2.5-pro` | **対応**(画像入力可) | `"attachment": true, "modalities": {"input":["text","image"],"output":["text"]}` |
| `xai.grok-4.3` | **対応**(画像入力可) | 同上 |
| `openai.gpt-oss-120b` | **非対応**(text-only) | 宣言不要(`attachment` 省略) |
| `openai.gpt-oss-20b` | **非対応**(text-only) | 宣言不要 |
| `meta.llama-*-instruct` | **非対応**(text-only) | 宣言不要 |

> 注: Cohere 系モデル (`cohere.*`) は本プロキシのスコープ外(3.2節)のため `Models` に登録しない。表にも掲載しない。

**opencode.jsonc 定義サンプル（抜粋）**:

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "provider": {
    "openai2oci": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "OCI OpenAI互換プロキシ",
      "options": { "baseURL": "http://127.0.0.1:11434/v1" },
      "models": {
        "google.gemini-2.5-pro": {
          "name": "Gemini 2.5 Pro (OCI)",
          "attachment": true,
          "modalities": {
            "input": ["text", "image"],
            "output": ["text"]
          }
        },
        "xai.grok-4.3": {
          "name": "Grok 4.3 (OCI)",
          "attachment": true,
          "modalities": {
            "input": ["text", "image"],
            "output": ["text"]
          }
        },
        "openai.gpt-oss-120b": {
          "name": "GPT-OSS 120B (OCI)"
          // attachment / modalities は付けない（text-only）
        }
      }
    }
  }
}
```

**注意**:
- `attachment: true` を付けないモデルに画像を送ると、opencode 側で拒否/エラーになります。
- `openai.gpt-oss-120b` 等 text-only モデルに `attachment: true` を付けると、opencode が画像を送信し、OCI が `INVALID_ARGUMENT` で拒否する可能性があります。モデル能力と opencode 宣言を一致させてください。
- opencode 側の画像前処理は `attachment.image` ブロック（デフォルト 2000x2000 / 5MB 自動縮小）で制御可能。詳細は opencode ドキュメント参照。
