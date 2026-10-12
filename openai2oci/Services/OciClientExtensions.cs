using Oci.Common.Auth;
using Oci.Common.Http.Signing;
using Oci.GenerativeaiinferenceService;

namespace openai2oci.Services;

/// <summary>
/// OCI認証プロファイル1件分の解決済みリソース。
/// Client は非ストリーミング用SDKクライアント、StreamHttp はストリーミング用署名付き HttpClient(REST直叩き)。
/// </summary>
public class OciAuthEntry
{
    public GenerativeAiInferenceClient Client { get; }
    public string CompartmentId { get; }
    public HttpClient StreamHttp { get; }
    public string RegionId { get; }

    public OciAuthEntry(GenerativeAiInferenceClient client, string compartmentId, HttpClient streamHttp, string regionId)
    {
        Client = client;
        CompartmentId = compartmentId;
        StreamHttp = streamHttp;
        RegionId = regionId;
    }
}

/// <summary>
/// OCI認証プロファイルID → OciAuthEntry のレジストリ。
/// モデルごとに異なるテナント/リージョンのOCI認証を使えるようにするための仕組み。
/// </summary>
public class OciClientRegistry
{
    private readonly Dictionary<string, OciAuthEntry> _clients;

    public OciClientRegistry(Dictionary<string, OciAuthEntry> clients)
    {
        _clients = clients;
    }

    /// <summary>指定した認証プロファイルIDに対応するエントリを取得する。未登録なら例外。</summary>
    public OciAuthEntry Get(string authProfileId)
    {
        if (!_clients.TryGetValue(authProfileId, out var entry))
        {
            throw new InvalidOperationException(
                $"OCI auth profile '{authProfileId}' is not configured in OpenAI2Oci:OciAuthProfiles.");
        }
        return entry;
    }
}

/// <summary>
/// OCI Generative AI クライアントの DI 登録ヘルパ。
/// appsettings.json の OciAuthProfiles 一件ごとに GenerativeAiInferenceClient を1つ生成し、
/// OciClientRegistry としてまとめて登録する。
/// </summary>
public static class OciClientExtensions
{
    public static IServiceCollection AddOciClient(this IServiceCollection services, AppSetinfo setinfo)
    {
        var home = Environment.GetEnvironmentVariable("HOME")
                   ?? Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? throw new InvalidOperationException("HOME/USERPROFILE environment variable not found");

        var configFile = Path.Combine(home, ".oci", "config");

        var clients = new Dictionary<string, OciAuthEntry>();
        foreach (var authProfile in setinfo.OciAuthProfiles)
        {
            if (string.IsNullOrEmpty(authProfile.Id))
            {
                throw new InvalidOperationException(
                    "OpenAI2Oci:OciAuthProfiles に Id が空のエントリがあります。");
            }
            if (string.IsNullOrEmpty(authProfile.OciCompartmentId))
            {
                throw new InvalidOperationException(
                    $"OpenAI2Oci:OciAuthProfiles['{authProfile.Id}'].OciCompartmentId が未設定です。");
            }
            if (clients.ContainsKey(authProfile.Id))
            {
                throw new InvalidOperationException(
                    $"OpenAI2Oci:OciAuthProfiles に Id '{authProfile.Id}' が重複しています。");
            }

            var provider = new ConfigFileAuthenticationDetailsProvider(configFile, authProfile.OciConfigProfile);
            var ociClient = new GenerativeAiInferenceClient(provider);

            // ストリーミング用: OCI署名付き HttpClient(REST直叩き用)。SDK の Chat() は
            // SSE ストリームのパースに未対応のため、stream:true 時のみこちらを使う。
            var streamHandler = OciHttpClientHandler.FromConfigFile(configFile, authProfile.OciConfigProfile);
            var streamHttp = new HttpClient(streamHandler) { Timeout = Timeout.InfiniteTimeSpan };
            var regionId = provider.Region?.RegionId ?? "";
            if (string.IsNullOrEmpty(regionId))
            {
                throw new InvalidOperationException(
                    $"OpenAI2Oci:OciAuthProfiles['{authProfile.Id}'].region が ~/.oci/config から解決できません。" +
                    "ストリーミング用エンドポイント構築に region が必要なため、config に region を設定してください。");
            }

            clients[authProfile.Id] = new OciAuthEntry(ociClient, authProfile.OciCompartmentId, streamHttp, regionId);
        }

        services.AddSingleton(new OciClientRegistry(clients));
        services.AddSingleton<OCIAIChatEngine>();
        return services;
    }
}
