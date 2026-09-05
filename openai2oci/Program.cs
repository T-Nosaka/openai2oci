using openai2oci;
using openai2oci.Endpoints;
using openai2oci.Middleware;
using openai2oci.Services;
using System.Text.Json.Serialization;

// ContentRootPath を明示的に実行ファイルのあるフォルダに固定する。
// 既定では Directory.GetCurrentDirectory() が使われ、Visual Studio の
// "Project" 起動プロファイルではこれがプロジェクトのソースフォルダになってしまい、
// appsettings.json が実行ファイルと同じフォルダ(bin\Debug\net10.0 等)から
// 読み込まれない問題があった。AppContext.BaseDirectory を使うことで、
// 常に「起動したアプリと同じフォルダ」の appsettings.json を優先して読み込む。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.ConfigureHttpJsonOptions(opt =>
{
    opt.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// アプリ設定バインド(appsettings.json の "OpenAI2Oci" セクション。設計書10章)
var setinfo = new AppSetinfo();
builder.Configuration.GetSection("OpenAI2Oci").Bind(setinfo);

// 必須項目チェック(未設定のまま気づかず起動してしまうことを防ぐ)
if (setinfo.OciAuthProfiles.Count == 0)
{
    throw new InvalidOperationException(
        "OpenAI2Oci:OciAuthProfiles が未設定です。" +
        $"'{Path.Combine(builder.Environment.ContentRootPath, "appsettings.json")}' " +
        "に少なくとも1つのOCI認証プロファイルを登録してください。");
}
if (setinfo.Models.Count == 0)
{
    throw new InvalidOperationException(
        "OpenAI2Oci:Models が未設定です。appsettings.json に少なくとも1つのモデルを登録してください。");
}

// モデル→認証プロファイルの整合性チェック(存在しないAuthProfileIdを指すモデルがあれば起動時に落とす)
var authProfileIds = setinfo.OciAuthProfiles.Select(p => p.Id).ToHashSet();
foreach (var model in setinfo.Models)
{
    if (string.IsNullOrEmpty(model.AuthProfileId))
    {
        throw new InvalidOperationException(
            $"OpenAI2Oci:Models['{model.Id}'].AuthProfileId が未設定です。");
    }
    if (!authProfileIds.Contains(model.AuthProfileId))
    {
        throw new InvalidOperationException(
            $"OpenAI2Oci:Models['{model.Id}'].AuthProfileId = '{model.AuthProfileId}' に該当する " +
            "OciAuthProfiles が見つかりません。");
    }
}
foreach (var model in setinfo.EmbeddingModels)
{
    if (string.IsNullOrEmpty(model.Id))
    {
        throw new InvalidOperationException(
            "OpenAI2Oci:EmbeddingModels に Id が空のエントリがあります。");
    }
    if (string.IsNullOrEmpty(model.AuthProfileId))
    {
        throw new InvalidOperationException(
            $"OpenAI2Oci:EmbeddingModels['{model.Id}'].AuthProfileId が未設定です。");
    }
    if (!authProfileIds.Contains(model.AuthProfileId))
    {
        throw new InvalidOperationException(
            $"OpenAI2Oci:EmbeddingModels['{model.Id}'].AuthProfileId = '{model.AuthProfileId}' に該当する " +
            "OciAuthProfiles が見つかりません。");
    }
}

// OCI Generative AI クライアント登録(OciAuthProfiles 一件ごとにクライアントを生成)
builder.Services.AddOciClient(setinfo);

var app = builder.Build();

app.Logger.LogInformation("ContentRoot: {ContentRoot}", app.Environment.ContentRootPath);

// Bearer 認証ミドルウェア(AppSetinfo.ProxyApiKey 空なら無効)
app.UseProxyApiKey(setinfo.ProxyApiKey);

// OpenAI 互換エンドポイント
app.MapOpenAIEndpoints(setinfo);

app.Run();
