//! Ported from `tests/Hmy.Estao.Tests/ConfigStoreTests.cs` (v2 schema).
//!
//! Cases that only exercised the v1 harness-manager or backdrop sections are
//! intentionally omitted: those sections do not exist in v2.

use std::fs;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use estao_core::config::{ConfigStore, ConfigValidation, EstaoConfig, ProviderConfig};
use serde_json::Value;

static TEMP_COUNTER: AtomicU64 = AtomicU64::new(0);

fn temp_dir() -> PathBuf {
    let unique = format!(
        "estao-config-store-{}-{}-{}",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock before epoch")
            .as_nanos(),
        TEMP_COUNTER.fetch_add(1, Ordering::Relaxed)
    );
    let directory = std::env::temp_dir().join(unique);
    fs::create_dir_all(&directory).expect("temp dir");
    directory
}

fn temp_config_path() -> PathBuf {
    temp_dir().join("config.json")
}

fn provider<'a>(config: &'a EstaoConfig, id: &str) -> &'a ProviderConfig {
    config
        .providers
        .iter()
        .find(|provider| provider.id == id)
        .unwrap_or_else(|| panic!("provider '{id}' missing"))
}

// Ported from ConfigStoreTests.load_returns_default_config_when_file_is_missing.
// The harness-manager assertions are v1-only and therefore dropped.
#[test]
fn load_returns_default_config_when_file_is_missing() {
    let path = temp_config_path();
    let config = ConfigStore::with_path(&path).load().unwrap();

    assert_eq!(config.version, 2);
    assert_eq!(config.theme, "dark");
    assert!(config.refresh.enabled);
    assert_eq!(config.refresh.interval_minutes, 15);
    assert!(config.overlay.enabled);
    assert_eq!(config.overlay.size, "normal");
    assert!(config.overlay.provider_ids.is_empty());
    assert_eq!(config.overlay.position_x, None);
    assert_eq!(config.overlay.position_y, None);

    assert!(provider(&config, "codex").enabled == Some(true));
    assert!(config.providers.iter().any(|p| p.id == "claude"));
    assert_eq!(config.providers.len(), 5);
    assert!(config.providers.iter().all(|p| !p.usage_colors.enabled));
}

// Ported from ConfigStoreTests.save_and_load_preserves_compatible_raw_fields.
#[test]
fn save_and_load_preserves_compatible_raw_fields() {
    let path = temp_config_path();
    let store = ConfigStore::with_path(&path);

    store
        .save(EstaoConfig {
            theme: "NordFrost".to_string(),
            providers: vec![ProviderConfig {
                id: "copilot".to_string(),
                enabled: Some(true),
                source: Some("api".to_string()),
                api_key: Some("secret".to_string()),
                enterprise_host: Some("github.example.com".to_string()),
                ..ProviderConfig::default()
            }],
            ..EstaoConfig::default()
        })
        .unwrap();

    let loaded = store.load().unwrap();
    assert_eq!(loaded.theme, "NordFrost");

    let copilot = provider(&loaded, "copilot");
    assert_eq!(copilot.api_key.as_deref(), Some("secret"));
    assert_eq!(
        copilot.enterprise_host.as_deref(),
        Some("github.example.com")
    );
    assert!(!copilot.pacing.enabled);
}

// Ported from ConfigStoreTests.legacy_global_pacing_is_migrated_to_each_provider.
#[test]
fn legacy_global_pacing_is_migrated_to_each_provider() {
    let path = temp_config_path();
    fs::write(
        &path,
        r#"{
          "version": 1,
          "pacing": { "enabled": true, "dailyTargetPercent": 22, "notifyOnExceed": false },
          "providers": [
            { "id": "codex", "enabled": true },
            { "id": "claude", "enabled": true }
          ]
        }"#,
    )
    .unwrap();

    let store = ConfigStore::with_path(&path);
    let config = store.load().unwrap();

    for id in ["codex", "claude"] {
        let provider = provider(&config, id);
        assert!(provider.pacing.enabled, "{id}");
        assert_eq!(provider.pacing.daily_target_percent, 22.0, "{id}");
        assert!(!provider.pacing.notify_on_exceed, "{id}");
    }

    store.save(config).unwrap();

    let saved: Value = serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
    assert!(saved.get("pacing").is_none());
    for provider in saved["providers"].as_array().unwrap() {
        assert!(provider.get("pacing").is_some(), "{provider}");
    }
}

// Ported from ConfigStoreTests.provider_pacing_takes_precedence_over_legacy_global_pacing.
#[test]
fn provider_pacing_takes_precedence_over_legacy_global_pacing() {
    let path = temp_config_path();
    fs::write(
        &path,
        r#"{
          "version": 1,
          "pacing": { "enabled": true, "dailyTargetPercent": 22 },
          "providers": [
            {
              "id": "codex",
              "enabled": true,
              "pacing": { "enabled": true, "dailyTargetPercent": 9 }
            }
          ]
        }"#,
    )
    .unwrap();

    let config = ConfigStore::with_path(&path).load().unwrap();

    assert_eq!(provider(&config, "codex").pacing.daily_target_percent, 9.0);
    assert_eq!(
        provider(&config, "claude").pacing.daily_target_percent,
        22.0
    );
}

// Ported from ConfigStoreTests.taskbar_overlay_custom_position_is_optional_and_round_trips.
// v2 renamed `taskbarOverlay` to `overlay` and dropped `moveEnabled`.
#[test]
fn overlay_custom_position_is_optional_and_round_trips() {
    let path = temp_config_path();
    let store = ConfigStore::with_path(&path);
    let mut config = ConfigStore::create_default_config();
    config.overlay.position_x = Some(420);
    config.overlay.position_y = Some(180);

    store.save(config).unwrap();
    let mut loaded = store.load().unwrap();

    assert_eq!(loaded.overlay.position_x, Some(420));
    assert_eq!(loaded.overlay.position_y, Some(180));

    loaded.overlay.position_y = None;
    store.save(loaded).unwrap();

    let saved: Value = serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
    let overlay = &saved["overlay"];
    assert!(overlay.get("positionX").is_none());
    assert!(overlay.get("positionY").is_none());
}

// Ported from ConfigStoreTests.provider_usage_colors_are_normalized_and_round_trip.
#[test]
fn provider_usage_colors_are_normalized_and_round_trip() {
    let path = temp_config_path();
    let store = ConfigStore::with_path(&path);
    let mut config = ConfigStore::create_default_config();
    let codex = config
        .providers
        .iter_mut()
        .find(|provider| provider.id == "codex")
        .unwrap();
    codex.usage_colors.enabled = true;
    codex.usage_colors.warning_percent = 82.0;
    codex.usage_colors.warning_color = "#a1b2c3".to_string();
    codex.usage_colors.critical_percent = 40.0;
    codex.usage_colors.critical_color = "not-a-color".to_string();

    store.save(config).unwrap();
    let loaded = store.load().unwrap();
    let colors = &provider(&loaded, "codex").usage_colors;

    assert!(colors.enabled);
    assert_eq!(colors.warning_percent, 82.0);
    assert_eq!(colors.warning_color, "#A1B2C3");
    assert_eq!(colors.critical_percent, 83.0);
    assert_eq!(colors.critical_color, "#EF4444");
}

// Ported from ConfigStoreTests.provider_usage_color_thresholds_remain_distinct_at_the_upper_limit.
#[test]
fn provider_usage_color_thresholds_remain_distinct_at_the_upper_limit() {
    let mut config = ConfigStore::create_default_config();
    config.providers[0].usage_colors.warning_percent = 100.0;
    config.providers[0].usage_colors.critical_percent = 20.0;

    let normalized = ConfigStore::normalize(config);

    assert_eq!(normalized.providers[0].usage_colors.warning_percent, 99.0);
    assert_eq!(normalized.providers[0].usage_colors.critical_percent, 100.0);
}

// Covers the v2 migration rules that replace the v1 overlay/backdrop sections.
#[test]
fn migrates_v1_overlay_theme_and_legacy_sections() {
    let path = temp_config_path();
    fs::write(
        &path,
        r#"{
          "version": 1,
          "theme": "Graphite",
          "backdropStyle": "Mica",
          "backdropOpacity": 68,
          "harnessManager": { "schemaVersion": 1 },
          "hooks": [],
          "taskbarOverlay": {
            "enabled": true,
            "providerIds": ["github-copilot", "z.ai", "copilot", "not-a-provider"],
            "size": "Compact",
            "positionX": 420,
            "displayMode": "icon-title",
            "moveEnabled": false,
            "controls": ["percentage", "pie", "reset"]
          }
        }"#,
    )
    .unwrap();

    let store = ConfigStore::with_path(&path);
    let config = store.load().unwrap();

    assert_eq!(config.version, 2);
    assert_eq!(config.theme, "dark");
    assert!(config.overlay.enabled);
    assert_eq!(config.overlay.provider_ids, vec!["copilot", "zai"]);
    assert_eq!(config.overlay.size, "compact");
    // positionY is missing, so the whole custom position is dropped.
    assert_eq!(config.overlay.position_x, None);
    assert_eq!(config.overlay.position_y, None);

    store.save(config).unwrap();
    let saved: Value = serde_json::from_str(&fs::read_to_string(&path).unwrap()).unwrap();
    assert_eq!(saved["version"], 2);
    assert!(saved.get("taskbarOverlay").is_none());
    assert!(saved.get("backdropStyle").is_none());
    assert!(saved.get("backdropOpacity").is_none());
    assert!(saved.get("harnessManager").is_none());
    assert!(saved.get("hooks").is_none());
    assert!(saved["overlay"].get("displayMode").is_none());
    assert!(saved["overlay"].get("moveEnabled").is_none());
    assert!(saved["overlay"].get("controls").is_none());
}

// Covers the theme rule for values other than dark/light, plus version fill-in.
#[test]
fn migrates_v1_theme_and_version() {
    let path = temp_config_path();
    fs::write(
        &path,
        r#"{
          "version": 1,
          "theme": "Solarized",
          "providers": [{ "id": "Claude" }]
        }"#,
    )
    .unwrap();

    let config = ConfigStore::with_path(&path).load().unwrap();

    assert_eq!(config.version, 2);
    assert_eq!(config.theme, "dark");
    assert_eq!(provider(&config, "claude").source.as_deref(), Some("auto"));
}

// Covers the `import_explicit` contract: parse, migrate, then save in place.
#[test]
fn import_explicit_migrates_and_saves() {
    let directory = temp_dir();
    let source_path = directory.join("legacy.json");
    let target_path = directory.join("nested").join("config.json");
    fs::write(
        &source_path,
        r#"{
          "version": 1,
          "providers": [{ "id": "github-copilot", "enabled": true, "apiKey": "secret" }]
        }"#,
    )
    .unwrap();

    let store = ConfigStore::with_path(&target_path);
    store.import_explicit(&source_path).unwrap();

    let loaded = store.load().unwrap();
    assert_eq!(loaded.version, 2);
    let copilot = provider(&loaded, "copilot");
    assert_eq!(copilot.enabled, Some(true));
    assert_eq!(copilot.api_key.as_deref(), Some("secret"));
}

#[test]
fn import_explicit_rejects_missing_or_invalid_sources() {
    let directory = temp_dir();
    let store = ConfigStore::with_path(directory.join("config.json"));

    let missing = store.import_explicit(directory.join("missing.json"));
    assert!(missing.is_err(), "missing source must fail");

    let empty_path = directory.join("empty.json");
    fs::write(&empty_path, "").unwrap();
    let empty = store.import_explicit(&empty_path).unwrap_err();
    assert_eq!(empty.to_string(), "Config file is empty or invalid JSON.");

    let broken_path = directory.join("broken.json");
    fs::write(&broken_path, "{ not json").unwrap();
    let broken = store.import_explicit(&broken_path).unwrap_err();
    assert_eq!(broken.to_string(), "Config file is empty or invalid JSON.");
}

#[test]
fn load_rejects_malformed_json() {
    let path = temp_config_path();
    fs::write(&path, "{ not json").unwrap();

    assert!(ConfigStore::with_path(&path).load().is_err());
}

// Ported from ConfigValidation.Validate, with the v2 version number.
#[test]
fn validation_reports_invalid_provider_values() {
    let config = EstaoConfig {
        version: 1,
        providers: vec![ProviderConfig {
            id: "unknown".to_string(),
            source: Some("nope".to_string()),
            cookie_source: Some("bad".to_string()),
            ..ProviderConfig::default()
        }],
        ..EstaoConfig::default()
    };

    assert_eq!(
        ConfigValidation::validate(&config),
        vec![
            "Unsupported config version 1; only version 2 is supported.".to_string(),
            "Unsupported provider 'unknown'.".to_string(),
            "Provider 'unknown' has invalid source 'nope'.".to_string(),
            "Provider 'unknown' has invalid cookieSource 'bad'.".to_string(),
        ]
    );
}

// Mirrors the C# rule that a null source/cookieSource is valid.
#[test]
fn validation_accepts_missing_sources_and_known_providers() {
    let config = EstaoConfig {
        providers: vec![ProviderConfig {
            id: "codex".to_string(),
            ..ProviderConfig::default()
        }],
        ..EstaoConfig::default()
    };

    assert!(ConfigValidation::validate(&config).is_empty());
}
