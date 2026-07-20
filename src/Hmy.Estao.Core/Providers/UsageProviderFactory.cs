using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Core.Providers;

public sealed class UsageProviderFactory
{
    private readonly HttpClient _httpClient;
    private readonly IBrowserCookieImporter _cookieImporter;

    public UsageProviderFactory(HttpClient? httpClient = null, IBrowserCookieImporter? cookieImporter = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _cookieImporter = cookieImporter ?? new BrowserCookieImporter();
    }

    public IUsageProvider Create(string id)
    {
        return Configuration.ProviderCatalog.NormalizeId(id) switch
        {
            "codex" => new CodexProvider(_httpClient),
            "claude" => new ClaudeProvider(_httpClient, _cookieImporter),
            "copilot" => new CopilotProvider(_httpClient),
            "opencode" => new OpenCodeProvider(_httpClient, _cookieImporter),
            var unknown => throw new NotSupportedException($"Provider '{unknown}' is not supported by Estao MVP.")
        };
    }
}
