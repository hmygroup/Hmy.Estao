//! Config store: load/save, v1 -> v2 migration, validation. Ported from
//! `ConfigStore.cs`.
//!
//! v2 deliberately drops the harness-manager, taskbar-overlay-control and
//! backdrop sections. Old files are still readable: [`ConfigStore::load`]
//! migrates the v1 shape before normalizing, and the next save only emits the
//! simplified schema.

use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use anyhow::{anyhow, bail, Context, Result};
use serde_json::{Map, Value};

use crate::catalog::{self, INITIAL_PROVIDER_IDS};
use crate::config::{
    EstaoConfig, OverlayConfig, PacingCatalog, PacingConfig, ProviderConfig, RefreshConfig,
    RefreshIntervalCatalog, UsageColorCatalog, UsageColorConfig,
};
use crate::paths;

/// The only schema version this store reads and writes.
const CURRENT_VERSION: i32 = 2;

/// Overlay sizes accepted by the v2 schema.
const OVERLAY_SIZES: [&str; 3] = ["compact", "normal", "spacious"];

/// Provider transports accepted by [`ConfigValidation`].
const PROVIDER_SOURCES: [&str; 5] = ["auto", "web", "cli", "oauth", "api"];

/// Cookie sources accepted by [`ConfigValidation`].
const COOKIE_SOURCES: [&str; 3] = ["auto", "manual", "off"];

/// Reads and writes the raw Estao JSON config file.
pub struct ConfigStore {
    path: PathBuf,
}

impl ConfigStore {
    /// Creates a store for the default config location (see
    /// [`crate::paths::resolve_config_path`]).
    pub fn new() -> Self {
        Self::with_path(paths::resolve_config_path())
    }

    /// Creates a store for an explicit config file, used by tests and imports.
    pub fn with_path(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    /// The config file this store reads and writes.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Loads the config, migrating v1 files to the v2 shape and normalizing
    /// the result. A missing file yields [`ConfigStore::create_default_config`].
    pub fn load(&self) -> Result<EstaoConfig> {
        if !self.path.exists() {
            return Ok(Self::create_default_config());
        }

        let text = fs::read_to_string(&self.path)
            .with_context(|| format!("Failed to read config file '{}'.", self.path.display()))?;
        let value: Value = serde_json::from_str(&text)
            .with_context(|| format!("Invalid config JSON in '{}'.", self.path.display()))?;

        Ok(Self::normalize(Self::config_from_value(value)?))
    }

    /// Serializes the normalized config as pretty JSON, creating the parent
    /// directory when needed.
    pub fn save(&self, config: EstaoConfig) -> Result<()> {
        if let Some(directory) = self.path.parent() {
            if !directory.as_os_str().is_empty() {
                fs::create_dir_all(directory).with_context(|| {
                    format!(
                        "Failed to create config directory '{}'.",
                        directory.display()
                    )
                })?;
            }
        }

        let normalized = Self::normalize(config);
        let json =
            serde_json::to_string_pretty(&normalized).context("Failed to serialize the config.")?;
        fs::write(&self.path, json)
            .with_context(|| format!("Failed to write config file '{}'.", self.path.display()))
    }

    /// Imports an explicit config file (v1 or v2) into this store's path.
    pub fn import_explicit(&self, source_path: impl AsRef<Path>) -> Result<()> {
        let source_path = source_path.as_ref();
        if !source_path.exists() {
            bail!("Config file was not found: {}", source_path.display());
        }

        let text = fs::read_to_string(source_path)
            .with_context(|| format!("Failed to read config file '{}'.", source_path.display()))?;
        let value: Value = serde_json::from_str(&text)
            .map_err(|_| anyhow!("Config file is empty or invalid JSON."))?;
        let config = Self::config_from_value(value)
            .map_err(|_| anyhow!("Config file is empty or invalid JSON."))?;

        self.save(config)
    }

    /// A fresh v2 config with only `codex` enabled, mirroring the .NET default.
    pub fn create_default_config() -> EstaoConfig {
        EstaoConfig {
            version: CURRENT_VERSION,
            theme: "dark".to_string(),
            refresh: RefreshConfig {
                enabled: true,
                interval_minutes: 15,
            },
            overlay: OverlayConfig {
                enabled: true,
                provider_ids: Vec::new(),
                size: "normal".to_string(),
                position_x: None,
                position_y: None,
            },
            providers: INITIAL_PROVIDER_IDS
                .iter()
                .map(|id| ProviderConfig {
                    id: (*id).to_string(),
                    enabled: Some(*id == "codex"),
                    source: Some("auto".to_string()),
                    cookie_source: Some("auto".to_string()),
                    ..ProviderConfig::default()
                })
                .collect(),
        }
    }

    /// Normalizes a config in place, dropping unsupported values and filling
    /// in missing providers.
    pub fn normalize(mut config: EstaoConfig) -> EstaoConfig {
        if config.version <= 0 {
            config.version = CURRENT_VERSION;
        }

        let theme = config.theme.trim().to_string();
        config.theme = if theme.is_empty() {
            "dark".to_string()
        } else {
            theme
        };

        if !RefreshIntervalCatalog::MINUTES.contains(&config.refresh.interval_minutes) {
            config.refresh.interval_minutes = 15;
        }

        let mut seen_ids = HashSet::new();
        config.overlay.provider_ids = config
            .overlay
            .provider_ids
            .iter()
            .map(|id| catalog::normalize_id(id))
            .filter(|id| catalog::is_supported(id))
            .filter(|id| seen_ids.insert(id.clone()))
            .collect();

        let size = config.overlay.size.trim().to_ascii_lowercase();
        config.overlay.size = if OVERLAY_SIZES.contains(&size.as_str()) {
            size
        } else {
            "normal".to_string()
        };

        if config.overlay.position_x.is_none() || config.overlay.position_y.is_none() {
            config.overlay.position_x = None;
            config.overlay.position_y = None;
        }

        for provider in &mut config.providers {
            provider.id = catalog::normalize_id(&provider.id);
            provider.source = Some(normalize_enum_value(provider.source.as_deref(), "auto"));
            provider.cookie_source = Some(normalize_enum_value(
                provider.cookie_source.as_deref(),
                "auto",
            ));
        }

        for id in INITIAL_PROVIDER_IDS {
            if !config
                .providers
                .iter()
                .any(|provider| provider.id.eq_ignore_ascii_case(id))
            {
                config.providers.push(ProviderConfig {
                    id: id.to_string(),
                    enabled: Some(false),
                    source: Some("auto".to_string()),
                    cookie_source: Some("auto".to_string()),
                    ..ProviderConfig::default()
                });
            }
        }

        for provider in &mut config.providers {
            normalize_pacing(&mut provider.pacing);
            normalize_usage_colors(&mut provider.usage_colors);
        }

        config
    }

    /// Deserializes a JSON value, migrating the v1 shape when needed.
    fn config_from_value(value: Value) -> Result<EstaoConfig> {
        let version = value.get("version").and_then(Value::as_i64);
        let value = match version {
            Some(2) => value,
            None | Some(1) => Self::migrate_v1(value),
            _ => value,
        };

        serde_json::from_value(value).context("Failed to parse the config.")
    }

    /// Rewrites a v1 config value into the v2 shape.
    fn migrate_v1(mut value: Value) -> Value {
        let Value::Object(ref mut map) = value else {
            return value;
        };

        // The overlay controls and display mode are gone; keep the rest.
        if let Some(Value::Object(legacy_overlay)) = map.remove("taskbarOverlay") {
            let mut overlay = Map::new();
            for key in ["enabled", "providerIds", "size", "positionX", "positionY"] {
                if let Some(value) = legacy_overlay.get(key) {
                    overlay.insert(key.to_string(), value.clone());
                }
            }
            map.insert("overlay".to_string(), Value::Object(overlay));
        }

        // A v1 top-level pacing block applies to every provider that did not
        // carry its own pacing yet. Providers missing from the file are added
        // here first, because normalization would append them afterwards and
        // the legacy block is gone by then (mirrors the .NET `LegacyPacing`
        // handling, which keeps it until the end of `Normalize`).
        if let Some(legacy_pacing) = map.remove("pacing") {
            let providers = map
                .entry("providers".to_string())
                .or_insert_with(|| Value::Array(Vec::new()));
            if !providers.is_array() {
                *providers = Value::Array(Vec::new());
            }

            if let Value::Array(providers) = providers {
                let present_ids: HashSet<String> = providers
                    .iter()
                    .filter_map(|provider| provider.get("id").and_then(Value::as_str))
                    .map(catalog::normalize_id)
                    .collect();

                for id in INITIAL_PROVIDER_IDS {
                    if !present_ids.contains(id) {
                        providers.push(serde_json::json!({
                            "id": id,
                            "enabled": false,
                            "source": "auto",
                            "cookieSource": "auto"
                        }));
                    }
                }

                for provider in providers {
                    if let Value::Object(provider) = provider {
                        if !provider.contains_key("pacing") {
                            provider.insert("pacing".to_string(), legacy_pacing.clone());
                        }
                    }
                }
            }
        }

        if let Some(Value::String(theme)) = map.get("theme") {
            let theme = match theme.trim().to_ascii_lowercase().as_str() {
                "light" => "light",
                _ => "dark",
            };
            map.insert("theme".to_string(), Value::String(theme.to_string()));
        }

        for key in [
            "harnessManager",
            "backdropStyle",
            "backdropOpacity",
            "hooks",
        ] {
            map.remove(key);
        }

        map.insert("version".to_string(), Value::Number(CURRENT_VERSION.into()));

        value
    }
}

impl Default for ConfigStore {
    fn default() -> Self {
        Self::new()
    }
}

/// Validation rules for a loaded config, ported from `ConfigValidation`.
pub struct ConfigValidation;

impl ConfigValidation {
    /// Returns the human-readable problems found in `config`; an empty list
    /// means the config is valid.
    pub fn validate(config: &EstaoConfig) -> Vec<String> {
        let mut errors = Vec::new();

        if config.version != CURRENT_VERSION {
            errors.push(format!(
                "Unsupported config version {}; only version {CURRENT_VERSION} is supported.",
                config.version
            ));
        }

        for provider in &config.providers {
            if !catalog::is_supported(&provider.id) {
                errors.push(format!("Unsupported provider '{}'.", provider.id));
            }

            if !is_valid(provider.source.as_deref(), &PROVIDER_SOURCES) {
                errors.push(format!(
                    "Provider '{}' has invalid source '{}'.",
                    provider.id,
                    provider.source.as_deref().unwrap_or_default()
                ));
            }

            if !is_valid(provider.cookie_source.as_deref(), &COOKIE_SOURCES) {
                errors.push(format!(
                    "Provider '{}' has invalid cookieSource '{}'.",
                    provider.id,
                    provider.cookie_source.as_deref().unwrap_or_default()
                ));
            }
        }

        errors
    }
}

fn normalize_enum_value(value: Option<&str>, fallback: &str) -> String {
    match value.map(str::trim) {
        Some(value) if !value.is_empty() => value.to_ascii_lowercase(),
        _ => fallback.to_string(),
    }
}

fn normalize_pacing(pacing: &mut PacingConfig) {
    let target_percent = if pacing.daily_target_percent <= 0.0 {
        15.0
    } else {
        pacing.daily_target_percent
    };

    pacing.daily_target_percent = target_percent.clamp(
        PacingCatalog::MIN_DAILY_TARGET_PERCENT,
        PacingCatalog::MAX_DAILY_TARGET_PERCENT,
    );
}

fn normalize_usage_colors(colors: &mut UsageColorConfig) {
    let warning_percent = if colors.warning_percent.is_finite() {
        colors.warning_percent
    } else {
        UsageColorCatalog::DEFAULT_WARNING_PERCENT
    };
    colors.warning_percent = warning_percent.clamp(0.0, UsageColorCatalog::MAXIMUM_WARNING_PERCENT);

    let critical_percent = if colors.critical_percent.is_finite() {
        colors.critical_percent
    } else {
        UsageColorCatalog::DEFAULT_CRITICAL_PERCENT
    };
    colors.critical_percent = critical_percent.clamp(colors.warning_percent + 1.0, 100.0);

    colors.warning_color = UsageColorCatalog::normalize_color(
        Some(&colors.warning_color),
        UsageColorCatalog::DEFAULT_WARNING_COLOR,
    );
    colors.critical_color = UsageColorCatalog::normalize_color(
        Some(&colors.critical_color),
        UsageColorCatalog::DEFAULT_CRITICAL_COLOR,
    );
}

fn is_valid(value: Option<&str>, allowed: &[&str]) -> bool {
    value.is_none_or(|value| allowed.contains(&value.trim().to_ascii_lowercase().as_str()))
}
