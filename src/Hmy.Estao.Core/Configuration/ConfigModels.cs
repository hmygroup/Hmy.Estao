using System.Text.Json.Serialization;

namespace Hmy.Estao.Core.Configuration;

public sealed class EstaoConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Graphite";

    [JsonPropertyName("hooks")]
    public object? Hooks { get; set; }

    [JsonPropertyName("providers")]
    public List<ProviderConfig> Providers { get; set; } = [];

    [JsonPropertyName("taskbarOverlay")]
    public TaskbarOverlayConfig TaskbarOverlay { get; set; } = new();
}

public sealed class TaskbarOverlayConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    // Empty means "all enabled providers". Keeping that as the default makes
    // newly added providers visible without requiring a config migration.
    [JsonPropertyName("providerIds")]
    public List<string> ProviderIds { get; set; } = [];

    [JsonPropertyName("controls")]
    public List<string> Controls { get; set; } = TaskbarOverlayControlCatalog.Default.ToList();

    [JsonPropertyName("displayMode")]
    public string DisplayMode { get; set; } = "icon-title";

    [JsonPropertyName("size")]
    public string Size { get; set; } = "normal";
}

public static class TaskbarOverlayControlCatalog
{
    public static readonly string[] All = ["percentage", "bar", "pie", "chart", "usedTotal", "reset"];
    public static readonly string[] Default = ["percentage", "bar", "pie", "chart"];

    public static string DisplayName(string id) => id switch
    {
        "percentage" => "Percentage",
        "bar" => "Usage bar",
        "pie" => "Donut",
        "chart" => "Mini chart",
        "usedTotal" => "Used / total",
        "reset" => "Reset time",
        _ => id
    };
}

public static class TaskbarOverlayDisplayCatalog
{
    public static readonly string[] DisplayModes = ["icon", "icon-title", "title"];
    public static readonly string[] Sizes = ["compact", "normal", "spacious"];
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
