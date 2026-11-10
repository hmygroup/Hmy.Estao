using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Core.Providers;

public sealed class UsageProviderFactory
{
    private readonly HttpClient _httpClient;
    private readonly ICookieSecretStore _cookieStore;
    private readonly IOAuthTokenStore _oauthStore;
    private readonly ClaudeUsageRetryGate _claudeRetry;
    private readonly Func<CancellationToken, Task<OAuthToken?>>? _claudeCliCredentials;

    public UsageProviderFactory(HttpClient? httpClient = null, ICookieSecretStore? cookieStore = null, IOAuthTokenStore? oauthStore = null,
        TimeProvider? timeProvider = null, Func<CancellationToken, Task<OAuthToken?>>? claudeCliCredentials = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _cookieStore = cookieStore ?? new SecureCookieStore();
        _oauthStore = oauthStore ?? new SecureOAuthTokenStore();
        _claudeRetry = new(timeProvider ?? TimeProvider.System);
        _claudeCliCredentials = claudeCliCredentials;
    }

    public DateTimeOffset? ClaudeRetryAt => _claudeRetry.RetryAt;
    internal string? ClaudeUsageIdentity => _claudeRetry.CredentialIdentity;
    public bool TakeDueClaudeRetry() => _claudeRetry.TakeDueRetry();
    public void ClearClaudeRetry() => _claudeRetry.Clear();
    public event EventHandler? ClaudeRetryChanged { add => _claudeRetry.Changed += value; remove => _claudeRetry.Changed -= value; }

    public IUsageProvider Create(string id)
    {
        return Configuration.ProviderCatalog.NormalizeId(id) switch
        {
            "codex" => new CodexProvider(_httpClient, _oauthStore),
            "claude" => new ClaudeProvider(_httpClient, _cookieStore, _oauthStore, _claudeRetry, _claudeCliCredentials),
            "copilot" => new CopilotProvider(_httpClient, _oauthStore),
            "opencode" => new OpenCodeProvider(_httpClient, _cookieStore),
            "zai" => new ZaiProvider(_httpClient),
            var unknown => throw new NotSupportedException($"Provider '{unknown}' is not supported by Estao MVP.")
        };
    }
}
