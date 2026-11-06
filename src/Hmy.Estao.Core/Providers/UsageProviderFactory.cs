using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Core.Providers;

public sealed class UsageProviderFactory
{
    private readonly HttpClient _httpClient;
    private readonly ICookieSecretStore _cookieStore;
    private readonly IOAuthTokenStore _oauthStore;

    public UsageProviderFactory(HttpClient? httpClient = null, ICookieSecretStore? cookieStore = null, IOAuthTokenStore? oauthStore = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _cookieStore = cookieStore ?? new SecureCookieStore();
        _oauthStore = oauthStore ?? new SecureOAuthTokenStore();
    }

    public IUsageProvider Create(string id)
    {
        return Configuration.ProviderCatalog.NormalizeId(id) switch
        {
            "codex" => new CodexProvider(_httpClient),
            "claude" => new ClaudeProvider(_httpClient, _cookieStore),
            "copilot" => new CopilotProvider(_httpClient, _oauthStore),
            "opencode" => new OpenCodeProvider(_httpClient, _cookieStore),
            "zai" => new ZaiProvider(_httpClient),
            var unknown => throw new NotSupportedException($"Provider '{unknown}' is not supported by Estao MVP.")
        };
    }
}
