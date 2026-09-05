namespace openai2oci;

/// <summary>
/// アプリケーション設定データクラス。
/// appsettings.json の "OpenAI2Oci" セクションから IConfiguration 経由でバインドする。
/// OCI認証情報はグローバル1つではなく、OciAuthProfiles として複数保持し、
/// 各モデル(ModelSetting.AuthProfileId)がどれを使うかを個別に指定する。
/// これによりリージョン違い・テナント違いのモデルを同一プロキシで一元管理できる。
/// </summary>
public class AppSetinfo
{
    /// <summary>プロキシ用 API キー(空なら認証なし)</summary>
    public string ProxyApiKey { get; set; } = "";

    /// <summary>OCI認証プロファイル一覧(テナント/リージョンごと)</summary>
    public List<OciAuthProfile> OciAuthProfiles { get; set; } = new();

    /// <summary>/v1/models で公開するモデルとその既定パラメータの一覧</summary>
    public List<ModelSetting> Models { get; set; } = new();

    /// <summary>/v1/embeddings で公開する Embedding モデル一覧(Cohere Embed 4 想定)。チャット用 Models とは分離。</summary>
    public List<EmbeddingModelSetting> EmbeddingModels { get; set; } = new();

    /// <summary>指定モデルIDの設定を取得(未登録なら null)</summary>
    public ModelSetting? FindModel(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId);

    /// <summary>指定 Embedding モデルIDの設定を取得(未登録なら null)</summary>
    public EmbeddingModelSetting? FindEmbeddingModel(string modelId) =>
        EmbeddingModels.FirstOrDefault(m => m.Id == modelId);

    /// <summary>指定IDのOCI認証プロファイルを取得(未登録なら null)</summary>
    public OciAuthProfile? FindAuthProfile(string authProfileId) =>
        OciAuthProfiles.FirstOrDefault(p => p.Id == authProfileId);
}

/// <summary>
/// OCI認証プロファイル1件分。~/.oci/config 内の named profile ＋ コンパートメントIDの組。
/// リージョンは OciConfigProfile が指す ~/.oci/config のセクション内の "region" キーから
/// ConfigFileAuthenticationDetailsProvider が自動解決するため、ここでは持たない。
/// </summary>
public class OciAuthProfile
{
    /// <summary>このプロキシ内で使う識別子(ModelSetting.AuthProfileId から参照される)</summary>
    public string Id { get; set; } = "";

    /// <summary>~/.oci/config 内のプロファイル名(例: "DEFAULT", "TENANT_B")</summary>
    public string OciConfigProfile { get; set; } = "DEFAULT";

    /// <summary>OCI コンパートメント OCID(必須)</summary>
    public string OciCompartmentId { get; set; } = "";
}

/// <summary>
/// モデルごとの既定パラメータ設定。
/// Cohere系モデルはOCI側で非推奨のため対象外(Generic系のみ)。
/// </summary>
public class ModelSetting
{
    /// <summary>OCIモデルID(例: "openai.gpt-oss-120b")</summary>
    public string Id { get; set; } = "";

    /// <summary>使用するOCI認証プロファイルのID(AppSetinfo.OciAuthProfiles[].Id を参照)</summary>
    public string AuthProfileId { get; set; } = "";

    /// <summary>既定の最大出力トークン数(リクエストで明示指定があればそちらを優先)</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>既定のtemperature(リクエストで明示指定があればそちらを優先)</summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>既定のtop_p(リクエストで明示指定があればそちらを優先)</summary>
    public double TopP { get; set; } = 1.0;

    /// <summary>既定のtop_k(OpenAI標準には無いためリクエスト拡張フィールドが優先されない限りこれを使用)</summary>
    public int? TopK { get; set; } = 1;
}

/// <summary>
/// Embedding モデルごとの設定。Cohere Embed 4 をターゲットに分離。
/// チャット用 ModelSetting とは独立して管理する。
/// </summary>
public class EmbeddingModelSetting
{
    /// <summary>OCIモデルID(例: "cohere.embed-multilingual-v3.0", "cohere.embed-v4.0")</summary>
    public string Id { get; set; } = "";

    /// <summary>使用するOCI認証プロファイルのID(AppSetinfo.OciAuthProfiles[].Id を参照)</summary>
    public string AuthProfileId { get; set; } = "";
}
