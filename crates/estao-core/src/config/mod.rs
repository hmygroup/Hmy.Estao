//! Config types (v2 simplified schema), ported from `ConfigModels.cs`.
//!
//! The v2 schema replaces the v1 taskbar/harness sections with a compact
//! overlay block; v1 files are migrated by the config store, so the legacy
//! harness, overlay-control and top-level pacing types are intentionally not
//! modelled here.

use serde::{Deserialize, Serialize};

/// Root Estao configuration.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EstaoConfig {
    /// Schema version. v2 is the current simplified schema.
    #[serde(default = "default_config_version")]
    pub version: i32,
    /// UI theme name.
    #[serde(default = "default_theme")]
    pub theme: String,
    /// Background refresh settings.
    #[serde(default)]
    pub refresh: RefreshConfig,
    /// Taskbar overlay settings.
    #[serde(default)]
    pub overlay: OverlayConfig,
    /// Per-provider configuration.
    #[serde(default)]
    pub providers: Vec<ProviderConfig>,
}

impl Default for EstaoConfig {
    fn default() -> Self {
        Self {
            version: default_config_version(),
            theme: default_theme(),
            refresh: RefreshConfig::default(),
            overlay: OverlayConfig::default(),
            providers: Vec::new(),
        }
    }
}

/// Background refresh settings.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RefreshConfig {
    /// Whether the background refresh loop is enabled.
    #[serde(default = "default_true")]
    pub enabled: bool,
    /// Refresh interval in minutes; see [`RefreshIntervalCatalog::MINUTES`].
    #[serde(default = "default_refresh_interval")]
    pub interval_minutes: i32,
}

impl Default for RefreshConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            interval_minutes: default_refresh_interval(),
        }
    }
}

/// The refresh intervals offered by the UI.
pub struct RefreshIntervalCatalog;

impl RefreshIntervalCatalog {
    /// Selectable refresh intervals, in minutes.
    pub const MINUTES: [i32; 6] = [1, 5, 10, 15, 30, 60];
}

/// Taskbar overlay settings (v2 schema).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OverlayConfig {
    /// Whether the overlay is shown.
    #[serde(default = "default_true")]
    pub enabled: bool,
    /// Providers to show; empty means "all enabled providers".
    #[serde(default)]
    pub provider_ids: Vec<String>,
    /// Overlay size (`compact`, `normal` or `spacious`).
    #[serde(default = "default_overlay_size")]
    pub size: String,
    /// Horizontal position after a manual drag; `None` keeps adaptive placement.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub position_x: Option<i32>,
    /// Vertical position after a manual drag; `None` keeps adaptive placement.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub position_y: Option<i32>,
}

impl Default for OverlayConfig {
    fn default() -> Self {
        Self {
            enabled: true,
            provider_ids: Vec::new(),
            size: default_overlay_size(),
            position_x: None,
            position_y: None,
        }
    }
}

/// Per-provider configuration.
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ProviderConfig {
    /// Normalized provider id.
    #[serde(default)]
    pub id: String,
    /// Whether the provider is enabled; `None` means "use the default".
    #[serde(default)]
    pub enabled: Option<bool>,
    /// Preferred transport (`auto`, `web`, `cli`, `oauth`, `api`).
    #[serde(default)]
    pub source: Option<String>,
    /// Cookie source (`auto`, `manual`, `off`).
    #[serde(default)]
    pub cookie_source: Option<String>,
    /// Legacy plain-text cookie header, kept for compatibility.
    #[serde(default)]
    pub cookie_header: Option<String>,
    /// API key, when the provider uses one.
    #[serde(default)]
    pub api_key: Option<String>,
    /// Enterprise host override.
    #[serde(default)]
    pub enterprise_host: Option<String>,
    /// Cloud region, when the provider needs one.
    #[serde(default)]
    pub region: Option<String>,
    /// Workspace id. .NET persists this as `workspaceID`.
    #[serde(rename = "workspaceID", default)]
    pub workspace_id: Option<String>,
    /// Multiple token accounts for the provider.
    #[serde(default)]
    pub token_accounts: Option<TokenAccountsConfig>,
    /// Index of the selected token account.
    #[serde(default)]
    pub active_account_index: Option<i32>,
    /// Extra Codex profile home directories.
    #[serde(default)]
    pub codex_profile_home_paths: Option<Vec<String>>,
    /// Usage pacing settings.
    #[serde(default)]
    pub pacing: PacingConfig,
    /// Usage color settings.
    #[serde(default)]
    pub usage_colors: UsageColorConfig,
}

/// Provider-level usage pacing: a self-imposed daily budget for a rate-limit
/// window, so Estao can draw a target line and warn when usage exceeds it.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PacingConfig {
    /// Whether pacing is enabled for the provider.
    #[serde(default)]
    pub enabled: bool,
    /// Daily budget as a percentage of the window.
    #[serde(default = "default_daily_target_percent")]
    pub daily_target_percent: f64,
    /// Whether to notify once per day when the target is exceeded.
    #[serde(default = "default_true")]
    pub notify_on_exceed: bool,
}

impl Default for PacingConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            daily_target_percent: default_daily_target_percent(),
            notify_on_exceed: true,
        }
    }
}

/// Pacing bounds and presets offered by the UI.
pub struct PacingCatalog;

impl PacingCatalog {
    /// Smallest selectable daily target.
    pub const MIN_DAILY_TARGET_PERCENT: f64 = 1.0;
    /// Largest selectable daily target.
    pub const MAX_DAILY_TARGET_PERCENT: f64 = 100.0;
    /// Selectable daily targets, as percentages.
    pub const PRESETS: [f64; 7] = [5.0, 10.0, 15.0, 20.0, 25.0, 30.0, 50.0];
}

/// Usage color settings for a provider.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UsageColorConfig {
    /// Whether threshold colors are enabled.
    #[serde(default)]
    pub enabled: bool,
    /// Percentage at which the warning color kicks in.
    #[serde(default = "default_warning_percent")]
    pub warning_percent: f64,
    /// Warning color, as `#RRGGBB`.
    #[serde(default = "default_warning_color")]
    pub warning_color: String,
    /// Percentage at which the critical color kicks in.
    #[serde(default = "default_critical_percent")]
    pub critical_percent: f64,
    /// Critical color, as `#RRGGBB`.
    #[serde(default = "default_critical_color")]
    pub critical_color: String,
}

impl Default for UsageColorConfig {
    fn default() -> Self {
        Self {
            enabled: false,
            warning_percent: UsageColorCatalog::DEFAULT_WARNING_PERCENT,
            warning_color: UsageColorCatalog::DEFAULT_WARNING_COLOR.to_string(),
            critical_percent: UsageColorCatalog::DEFAULT_CRITICAL_PERCENT,
            critical_color: UsageColorCatalog::DEFAULT_CRITICAL_COLOR.to_string(),
        }
    }
}

/// Severity of a usage percentage.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UsageColorLevel {
    /// Below the warning threshold.
    Default,
    /// At or above the warning threshold.
    Warning,
    /// At or above the critical threshold.
    Critical,
}

/// Threshold logic and defaults for usage colors.
pub struct UsageColorCatalog;

impl UsageColorCatalog {
    /// Default warning threshold, as a percentage.
    pub const DEFAULT_WARNING_PERCENT: f64 = 75.0;
    /// Default critical threshold, as a percentage.
    pub const DEFAULT_CRITICAL_PERCENT: f64 = 90.0;
    /// Highest allowed warning threshold, as a percentage.
    pub const MAXIMUM_WARNING_PERCENT: f64 = 99.0;
    /// Default warning color.
    pub const DEFAULT_WARNING_COLOR: &str = "#F59E0B";
    /// Default critical color.
    pub const DEFAULT_CRITICAL_COLOR: &str = "#EF4444";

    /// Classifies `percent_used` (`0.0` - `1.0`) against the configured
    /// thresholds. Disabled or missing configs always yield
    /// [`UsageColorLevel::Default`].
    pub fn level_for(config: Option<&UsageColorConfig>, percent_used: f64) -> UsageColorLevel {
        let Some(config) = config.filter(|config| config.enabled) else {
            return UsageColorLevel::Default;
        };

        let percent = percent_used.clamp(0.0, 1.0) * 100.0;
        if percent >= config.critical_percent {
            return UsageColorLevel::Critical;
        }

        if percent >= config.warning_percent {
            return UsageColorLevel::Warning;
        }

        UsageColorLevel::Default
    }

    /// The configured color for `percent_used`, or `None` below the warning
    /// threshold (or when colors are disabled).
    pub fn color_for(config: Option<&UsageColorConfig>, percent_used: f64) -> Option<String> {
        match Self::level_for(config, percent_used) {
            UsageColorLevel::Warning => config.map(|config| config.warning_color.clone()),
            UsageColorLevel::Critical => config.map(|config| config.critical_color.clone()),
            UsageColorLevel::Default => None,
        }
    }

    /// Normalizes a `#RRGGBB` color, falling back to `fallback` when `value`
    /// is missing or malformed.
    pub fn normalize_color(value: Option<&str>, fallback: &str) -> String {
        let Some(color) = value.map(str::trim) else {
            return fallback.to_string();
        };

        if color.len() != 7
            || !color.starts_with('#')
            || !color[1..]
                .chars()
                .all(|character| character.is_ascii_hexdigit())
        {
            return fallback.to_string();
        }

        color.to_ascii_uppercase()
    }
}

/// Multiple token accounts configured for a provider.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TokenAccountsConfig {
    /// Schema version of the token account list.
    #[serde(default = "default_token_accounts_version")]
    pub version: i32,
    /// Index of the selected account.
    #[serde(default)]
    pub active_index: i32,
    /// Configured accounts.
    #[serde(default)]
    pub accounts: Vec<TokenAccountConfig>,
}

impl Default for TokenAccountsConfig {
    fn default() -> Self {
        Self {
            version: default_token_accounts_version(),
            active_index: 0,
            accounts: Vec::new(),
        }
    }
}

/// A single token account.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TokenAccountConfig {
    /// Stable account id.
    #[serde(default)]
    pub id: Option<String>,
    /// User-facing account label.
    #[serde(default)]
    pub label: Option<String>,
    /// Account token.
    #[serde(default)]
    pub token: Option<String>,
    /// Workspace id. .NET persists this as `workspaceID`.
    #[serde(rename = "workspaceID", default)]
    pub workspace_id: Option<String>,
}

/// Transport used to fetch provider usage.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ProviderSource {
    /// Let the provider pick the best transport.
    #[default]
    Auto,
    /// Browser/web session.
    Web,
    /// Provider CLI.
    Cli,
    /// OAuth token.
    OAuth,
    /// Direct API key.
    Api,
}

impl ProviderSource {
    /// The lowercase name persisted in the config file.
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Auto => "auto",
            Self::Web => "web",
            Self::Cli => "cli",
            Self::OAuth => "oauth",
            Self::Api => "api",
        }
    }

    /// Parses a config value, defaulting to [`ProviderSource::Auto`].
    pub fn parse(value: Option<&str>) -> Self {
        match value
            .map(|value| value.trim().to_ascii_lowercase())
            .as_deref()
        {
            Some("web") => Self::Web,
            Some("cli") => Self::Cli,
            Some("oauth") => Self::OAuth,
            Some("api") => Self::Api,
            _ => Self::Auto,
        }
    }
}

/// Where a provider's cookie header comes from.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum CookieSource {
    /// Read cookies from the user's browser.
    #[default]
    Auto,
    /// Use the manually configured cookie header.
    Manual,
    /// Do not use cookies.
    Off,
}

impl CookieSource {
    /// The lowercase name persisted in the config file.
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Auto => "auto",
            Self::Manual => "manual",
            Self::Off => "off",
        }
    }

    /// Parses a config value, defaulting to [`CookieSource::Auto`].
    pub fn parse(value: Option<&str>) -> Self {
        match value
            .map(|value| value.trim().to_ascii_lowercase())
            .as_deref()
        {
            Some("manual") => Self::Manual,
            Some("off") => Self::Off,
            _ => Self::Auto,
        }
    }
}

fn default_config_version() -> i32 {
    2
}

fn default_theme() -> String {
    "dark".to_string()
}

fn default_true() -> bool {
    true
}

fn default_refresh_interval() -> i32 {
    15
}

fn default_overlay_size() -> String {
    "normal".to_string()
}

fn default_daily_target_percent() -> f64 {
    15.0
}

fn default_warning_percent() -> f64 {
    UsageColorCatalog::DEFAULT_WARNING_PERCENT
}

fn default_warning_color() -> String {
    UsageColorCatalog::DEFAULT_WARNING_COLOR.to_string()
}

fn default_critical_percent() -> f64 {
    UsageColorCatalog::DEFAULT_CRITICAL_PERCENT
}

fn default_critical_color() -> String {
    UsageColorCatalog::DEFAULT_CRITICAL_COLOR.to_string()
}

fn default_token_accounts_version() -> i32 {
    1
}

#[cfg(test)]
mod tests {
    use super::*;

    fn enabled_color_config() -> UsageColorConfig {
        UsageColorConfig {
            enabled: true,
            warning_percent: 75.0,
            warning_color: "#F59E0B".to_string(),
            critical_percent: 90.0,
            critical_color: "#EF4444".to_string(),
        }
    }

    // Ported from UsageColorCatalogTests.level_for_uses_configured_thresholds.
    #[test]
    fn level_for_uses_configured_thresholds() {
        let config = enabled_color_config();
        let cases = [
            (0.7499, UsageColorLevel::Default),
            (0.75, UsageColorLevel::Warning),
            (0.8999, UsageColorLevel::Warning),
            (0.90, UsageColorLevel::Critical),
            (2.0, UsageColorLevel::Critical),
        ];

        for (used, expected) in cases {
            assert_eq!(
                UsageColorCatalog::level_for(Some(&config), used),
                expected,
                "percent {used}"
            );
        }
    }

    // Ported from UsageColorCatalogTests.disabled_colors_always_use_the_default_level.
    #[test]
    fn disabled_colors_always_use_the_default_level() {
        let mut config = enabled_color_config();
        config.enabled = false;

        assert_eq!(
            UsageColorCatalog::level_for(Some(&config), 1.0),
            UsageColorLevel::Default
        );
        assert_eq!(UsageColorCatalog::color_for(Some(&config), 1.0), None);
    }

    #[test]
    fn defaults_match_the_v2_schema() {
        let config = EstaoConfig::default();
        assert_eq!(config.version, 2);
        assert_eq!(config.theme, "dark");
        assert!(config.refresh.enabled);
        assert_eq!(config.refresh.interval_minutes, 15);
        assert!(config.overlay.enabled);
        assert_eq!(config.overlay.size, "normal");
        assert!(config.overlay.provider_ids.is_empty());
        assert_eq!(config.overlay.position_x, None);

        let provider = ProviderConfig::default();
        assert!(!provider.pacing.enabled);
        assert_eq!(provider.pacing.daily_target_percent, 15.0);
        assert!(provider.pacing.notify_on_exceed);
        assert!(!provider.usage_colors.enabled);
        assert_eq!(provider.usage_colors.warning_color, "#F59E0B");
    }

    #[test]
    fn provider_json_uses_the_dotnet_field_names() {
        let provider = ProviderConfig {
            id: "zai".to_string(),
            workspace_id: Some("workspace-1".to_string()),
            codex_profile_home_paths: Some(vec!["~/.codex".to_string()]),
            ..ProviderConfig::default()
        };

        let json = serde_json::to_string(&provider).unwrap();
        assert!(json.contains("\"workspaceID\":\"workspace-1\""), "{json}");
        assert!(json.contains("\"codexProfileHomePaths\""), "{json}");
        assert!(json.contains("\"dailyTargetPercent\":15.0"), "{json}");
        assert!(json.contains("\"usageColors\""), "{json}");
        assert!(json.contains("\"notifyOnExceed\":true"), "{json}");

        let parsed: ProviderConfig = serde_json::from_str(&json).unwrap();
        assert_eq!(parsed.workspace_id.as_deref(), Some("workspace-1"));
        assert_eq!(
            parsed.codex_profile_home_paths,
            Some(vec!["~/.codex".to_string()])
        );
    }

    #[test]
    fn deserializes_a_minimal_v2_config_with_defaults() {
        let config: EstaoConfig = serde_json::from_str("{}").unwrap();

        assert_eq!(config.version, 2);
        assert_eq!(config.theme, "dark");
        assert!(config.refresh.enabled);
        assert_eq!(config.refresh.interval_minutes, 15);
        assert!(config.overlay.enabled);
        assert!(config.providers.is_empty());
    }

    #[test]
    fn parses_provider_sources_like_the_dotnet_helpers() {
        assert_eq!(ProviderSource::parse(Some(" Web ")), ProviderSource::Web);
        assert_eq!(ProviderSource::parse(Some("CLI")), ProviderSource::Cli);
        assert_eq!(ProviderSource::parse(Some("oauth")), ProviderSource::OAuth);
        assert_eq!(ProviderSource::parse(Some("api")), ProviderSource::Api);
        assert_eq!(ProviderSource::parse(Some("nope")), ProviderSource::Auto);
        assert_eq!(ProviderSource::parse(None), ProviderSource::Auto);

        assert_eq!(CookieSource::parse(Some("Manual")), CookieSource::Manual);
        assert_eq!(CookieSource::parse(Some("OFF")), CookieSource::Off);
        assert_eq!(CookieSource::parse(Some("other")), CookieSource::Auto);
        assert_eq!(CookieSource::parse(None), CookieSource::Auto);

        assert_eq!(
            serde_json::to_string(&ProviderSource::OAuth).unwrap(),
            "\"oauth\""
        );
        assert_eq!(
            serde_json::to_string(&CookieSource::Off).unwrap(),
            "\"off\""
        );
    }

    #[test]
    fn normalize_color_uppercases_valid_values_and_rejects_the_rest() {
        assert_eq!(
            UsageColorCatalog::normalize_color(Some(" #a1b2c3 "), "#000000"),
            "#A1B2C3"
        );
        assert_eq!(
            UsageColorCatalog::normalize_color(Some("#12345"), "#000000"),
            "#000000"
        );
        assert_eq!(
            UsageColorCatalog::normalize_color(Some("1234567"), "#000000"),
            "#000000"
        );
        assert_eq!(
            UsageColorCatalog::normalize_color(Some("#GGGGGG"), "#000000"),
            "#000000"
        );
        assert_eq!(
            UsageColorCatalog::normalize_color(None, "#EF4444"),
            "#EF4444"
        );
    }
}
