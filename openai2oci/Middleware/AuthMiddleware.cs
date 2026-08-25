using System.Security.Cryptography;
using System.Text;

namespace openai2oci.Middleware;

/// <summary>
/// プロキシ用 Bearer API キー認証ミドルウェア(設計書 5章)。
/// apiKey が空文字列なら認証なし。設定時は Authorization: Bearer と FixedTime 比較。
/// </summary>
public static class AuthMiddleware
{
    public static IApplicationBuilder UseProxyApiKey(this IApplicationBuilder app, string apiKey)
    {
        return app.Use(async (ctx, next) =>
        {
            if (apiKey.Length == 0)
            {
                await next();
                return;
            }

            var auth = ctx.Request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !FixedTimeEquals(auth.Substring(7).Trim(), apiKey))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = new { type = "invalid_request_error", message = "Invalid or missing API key." }
                });
                return;
            }

            await next();
        });
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
