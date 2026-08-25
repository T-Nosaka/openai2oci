using Oci.Common.Auth;
using Oci.GenerativeaiinferenceService;

namespace openai2oci.Services;

/// <summary>
/// OCI認証プロファイルID → (GenerativeAiInferenceClient, CompartmentId) のレジストリ。
/// モデルごとに異なるテナント/リージョンのOCI認証を使えるようにするための仕組み。
/// </summary>
public class OciClientRegistry
{
    private readonly Dictionary<string, (GenerativeAiInferenceClient Client, string CompartmentId)> _clients;

    public OciClientRegistry(Dictionary<string, (GenerativeAiInferenceClient, string)> clients)
    {
        _clients = clients;
    }

    /// <summary>指定した認証プロファイルIDに対応するクライアントとコンパートメントIDを取得する。未登録なら例外。</summary>
    public (GenerativeAiInferenceClient Client, string CompartmentId) Get(string authProfileId)
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

        var clients = new Dictionary<string, (GenerativeAiInferenceClient, string)>();
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
            clients[authProfile.Id] = (ociClient, authProfile.OciCompartmentId);
        }

        services.AddSingleton(new OciClientRegistry(clients));
        services.AddSingleton<OCIAIChatEngine>();
        return services;
    }
}
