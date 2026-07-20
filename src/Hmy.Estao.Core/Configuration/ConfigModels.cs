using System.Text.Json.Serialization;

namespace Hmy.Estao.Core.Configuration;

public sealed class EstaoConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("hooks")]
    public object? Hooks { get; set; }

    [JsonPropertyName("providers")]
    public List<ProviderConfig> Providers { get; set; } = [];
}

public sealed class ProviderConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("cookieSource")]
    public string? CookieSource { get; set; }

    [JsonPropertyName("cookieHeader")]
    public string? CookieHeader { get; set; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("enterpriseHost")]
    public string? EnterpriseHost { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("workspaceID")]
    public string? WorkspaceId { get; set; }

    [JsonPropertyName("tokenAccounts")]
    public TokenAccountsConfig? TokenAccounts { get; set; }

    [JsonPropertyName("activeAccountIndex")]
    public int? ActiveAccountIndex { get; set; }

    [JsonPropertyName("codexProfileHomePaths")]
    public List<string>? CodexProfileHomePaths { get; set; }
}

public sealed class TokenAccountsConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("activeIndex")]
    public int ActiveIndex { get; set; }

    [JsonPropertyName("accounts")]
    public List<TokenAccountConfig> Accounts { get; set; } = [];
}

public sealed class TokenAccountConfig
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("workspaceID")]
    public string? WorkspaceId { get; set; }

}

public enum ProviderSource
{
    Auto,
    Web,
    Cli,
    OAuth,
    Api
}

public enum CookieSource
{
    Auto,
    Manual,
    Off
}
