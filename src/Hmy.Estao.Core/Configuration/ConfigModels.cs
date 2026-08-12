using System.Text.Json.Serialization;

namespace Hmy.Estao.Core.Configuration;

public sealed class EstaoConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Graphite";

    [JsonPropertyName("backdropStyle")]
    public string BackdropStyle { get; set; } = "None";

    [JsonPropertyName("backdropOpacity")]
    public int BackdropOpacity { get; set; } = 96;

    [JsonPropertyName("hooks")]
    public object? Hooks { get; set; }

    [JsonPropertyName("providers")]
    public List<ProviderConfig> Providers { get; set; } = [];

    [JsonPropertyName("taskbarOverlay")]
    public TaskbarOverlayConfig TaskbarOverlay { get; set; } = new();

    [JsonPropertyName("refresh")]
    public RefreshConfig Refresh { get; set; } = new();

    // Kept only so older config files can be migrated. New configs persist
    // pacing inside each provider, where different accounts can have different
    // budgets and notification preferences.
    [JsonPropertyName("pacing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PacingConfig? LegacyPacing { get; set; }
}

public sealed class RefreshConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("intervalMinutes")]
    public int IntervalMinutes { get; set; } = 15;
}

public static class RefreshIntervalCatalog
{
    public static readonly int[] Minutes = [1, 5, 10, 15, 30, 60];
}

/// <summary>
/// Provider-level usage pacing: a self-imposed "budget" of how much of a
/// rate-limit window the user wants to burn per day, so Estao can draw a target
/// line and warn once per day when that provider's usage curve is above it.
/// </summary>
public sealed class PacingConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("dailyTargetPercent")]
    public double DailyTargetPercent { get; set; } = 15D;

    [JsonPropertyName("notifyOnExceed")]
    public bool NotifyOnExceed { get; set; } = true;
}

public static class PacingCatalog
{
    public const double MinDailyTargetPercent = 1D;
    public const double MaxDailyTargetPercent = 100D;
    public static readonly double[] Presets = [5, 10, 15, 20, 25, 30, 50];
}

public sealed class UsageColorConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("warningPercent")]
    public double WarningPercent { get; set; } = UsageColorCatalog.DefaultWarningPercent;

    [JsonPropertyName("warningColor")]
    public string WarningColor { get; set; } = UsageColorCatalog.DefaultWarningColor;

    [JsonPropertyName("criticalPercent")]
    public double CriticalPercent { get; set; } = UsageColorCatalog.DefaultCriticalPercent;

    [JsonPropertyName("criticalColor")]
    public string CriticalColor { get; set; } = UsageColorCatalog.DefaultCriticalColor;
}

public enum UsageColorLevel
{
    Default,
    Warning,
    Critical
}

public static class UsageColorCatalog
{
    public const double DefaultWarningPercent = 75D;
    public const double DefaultCriticalPercent = 90D;
    public const double MaximumWarningPercent = 99D;
    public const string DefaultWarningColor = "#F59E0B";
    public const string DefaultCriticalColor = "#EF4444";

    public static UsageColorLevel LevelFor(UsageColorConfig? config, double percentUsed)
    {
        if (config is not { Enabled: true }) return UsageColorLevel.Default;
        var percent = Math.Clamp(percentUsed, 0D, 1D) * 100D;
        if (percent >= config.CriticalPercent) return UsageColorLevel.Critical;
        return percent >= config.WarningPercent ? UsageColorLevel.Warning : UsageColorLevel.Default;
    }

    public static string? ColorFor(UsageColorConfig? config, double percentUsed) => LevelFor(config, percentUsed) switch
    {
        UsageColorLevel.Warning => config!.WarningColor,
        UsageColorLevel.Critical => config!.CriticalColor,
        _ => null
    };

    public static string NormalizeColor(string? value, string fallback)
    {
        var color = value?.Trim();
        if (color is not { Length: 7 } || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit))
            return fallback;
        return color.ToUpperInvariant();
    }
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

    [JsonPropertyName("moveEnabled")]
    public bool MoveEnabled { get; set; } = true;

    // Null coordinates keep the adaptive taskbar placement. Both values are
    // populated after the user drags the overlay by its handle.
    [JsonPropertyName("positionX")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PositionX { get; set; }

    [JsonPropertyName("positionY")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PositionY { get; set; }
}

public static class TaskbarOverlayControlCatalog
{
    public static readonly string[] All = ["percentage", "bar", "pie", "chart", "usedTotal", "reset"];
    public static readonly string[] Default = ["percentage", "pie", "reset"];

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
    private PacingConfig? _pacing;
    private UsageColorConfig? _usageColors;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("pacing")]
    public PacingConfig Pacing
    {
        get => _pacing ??= new PacingConfig();
        set
        {
            _pacing = value ?? new PacingConfig();
            HasExplicitPacing = true;
        }
    }

    [JsonIgnore]
    internal bool HasExplicitPacing { get; private set; }

    [JsonPropertyName("usageColors")]
    public UsageColorConfig UsageColors
    {
        get => _usageColors ??= new UsageColorConfig();
        set => _usageColors = value ?? new UsageColorConfig();
    }

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
