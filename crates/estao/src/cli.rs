//! Command-line interface: `estao usage` and `estao config ...`.
//!
//! The clap argument definitions and the dispatch in [`Cli::run`] are the
//! stable interface; the command handlers are ported from
//! `src/Hmy.Estao.Cli/Program.cs` (Cli.RunAsync/UsageAsync/ConfigAsync).

use std::fs;
use std::io::{self, Read};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{anyhow, bail, Context, Result};
use clap::{Parser, Subcommand};

use estao_core::catalog;
use estao_core::config::{ConfigStore, ConfigValidation, EstaoConfig, ProviderConfig};
use estao_core::formatting;
use estao_core::refresh::UsageRefreshService;
use estao_core::security::SecureCookieStore;

use crate::app;

/// Parses and dispatches one CLI invocation.
#[derive(Debug, Parser)]
#[command(name = "estao", version, about = "Estao - provider usage tray monitor")]
pub struct Cli {
    /// Launch the GUI with the settings window open
    #[arg(long)]
    pub settings: bool,

    #[command(subcommand)]
    pub command: Option<Command>,
}

#[derive(Debug, Subcommand)]
pub enum Command {
    /// Fetch and print current usage for all enabled providers
    #[command(name = "usage")]
    Usage {
        /// Only this provider (codex|claude|copilot|opencode|zai)
        #[arg(long)]
        provider: Option<String>,

        /// 1-based account index for the selected provider
        #[arg(long, value_name = "N")]
        account_index: Option<usize>,

        /// Output format: text or json
        #[arg(long, default_value = "text")]
        format: String,

        /// Pretty-print JSON output
        #[arg(long)]
        pretty: bool,
    },

    /// Manage the raw JSON config file
    #[command(name = "config")]
    Config {
        #[command(subcommand)]
        action: Option<ConfigAction>,
    },
}

#[derive(Debug, Subcommand)]
pub enum ConfigAction {
    /// Validate the config file
    Validate,

    /// Print the normalized config as JSON
    Dump,

    /// Enable a provider
    Enable {
        /// Provider id or alias
        #[arg(long)]
        provider: String,
    },

    /// Disable a provider
    Disable {
        /// Provider id or alias
        #[arg(long)]
        provider: String,
    },

    /// Save an API key for a provider (read from --stdin, --value, or --api-key)
    #[command(name = "set-api-key")]
    SetApiKey {
        /// Provider id or alias
        #[arg(long)]
        provider: String,

        /// Read the secret from stdin
        #[arg(long)]
        stdin: bool,

        /// The secret value
        #[arg(long)]
        api_key: Option<String>,

        /// Alias for --api-key
        #[arg(long)]
        value: Option<String>,
    },

    /// Save an API key or token for a provider (read from --stdin, --value, or --api-key)
    Set {
        /// Provider id or alias
        #[arg(long)]
        provider: String,

        /// Read the secret from stdin
        #[arg(long)]
        stdin: bool,

        /// The secret value
        #[arg(long)]
        api_key: Option<String>,

        /// Alias for --api-key
        #[arg(long)]
        value: Option<String>,
    },

    /// Save an encrypted cookie header for a provider (read from --stdin, --cookie, or --value)
    #[command(name = "set-cookie")]
    SetCookie {
        /// Provider id or alias
        #[arg(long)]
        provider: String,

        /// Read the cookie header from stdin
        #[arg(long)]
        stdin: bool,

        /// The cookie header value
        #[arg(long)]
        cookie: Option<String>,

        /// Alias for --cookie
        #[arg(long)]
        value: Option<String>,
    },

    /// Remove a provider's saved cookie
    #[command(name = "clear-cookie")]
    ClearCookie {
        /// Provider id or alias
        #[arg(long)]
        provider: String,
    },

    /// Import a config file explicitly
    Import {
        /// Path of the config file to import
        #[arg(long)]
        file: String,
    },
}

impl Cli {
    /// Runs the parsed invocation and returns the process exit code:
    /// 0 on success and 1 when any provider reported an error or a config
    /// command found problems. Runtime errors bubble up to `main`, which
    /// prints them and exits 1.
    pub fn run(self) -> anyhow::Result<i32> {
        if let Some(command) = self.command {
            return match command {
                Command::Usage {
                    provider,
                    account_index,
                    format,
                    pretty,
                } => handle_usage(provider, account_index, &format, pretty),
                Command::Config { action } => handle_config(action),
            };
        }

        app::run(app::AppOptions {
            open_settings: self.settings,
        });
        Ok(0)
    }
}

/// Ported from `Cli.UsageAsync`.
fn handle_usage(
    provider: Option<String>,
    account_index: Option<usize>,
    format: &str,
    pretty: bool,
) -> Result<i32> {
    let store = ConfigStore::new();

    // The .NET CLI reloads the config and refreshes from a temporary store
    // whenever `--provider` or `--account-index` is present, so the filter
    // never touches the user's config file.
    let provider = provider.filter(|provider| !provider.trim().is_empty());
    let (store, temp_path) = if provider.is_some() || account_index.is_some() {
        let mut config = store.load()?;
        if let Some(provider) = provider.as_deref() {
            apply_provider_filter(&mut config, provider, account_index);
        }

        let temp_path = unique_temp_config_path();
        let temp_store = ConfigStore::with_path(&temp_path);
        temp_store.save(config)?;
        (temp_store, Some(temp_path))
    } else {
        (store, None)
    };

    let service = UsageRefreshService::new(Arc::new(store));
    let snapshots = service.refresh();

    if let Some(temp_path) = &temp_path {
        let _ = fs::remove_file(temp_path);
    }

    let output = if format.eq_ignore_ascii_case("json") {
        formatting::to_json(&snapshots, pretty)?
    } else {
        formatting::to_text(&snapshots)
    };
    println!("{output}");

    Ok(
        if snapshots.iter().any(|snapshot| snapshot.error.is_some()) {
            1
        } else {
            0
        },
    )
}

/// Ported from `Cli.ConfigAsync`; no action means `validate`.
fn handle_config(action: Option<ConfigAction>) -> Result<i32> {
    let store = ConfigStore::new();

    match action.unwrap_or(ConfigAction::Validate) {
        ConfigAction::Validate => {
            let config = store.load()?;
            let errors = ConfigValidation::validate(&config);
            if errors.is_empty() {
                println!("Config valid: {}", store.path().display());
                return Ok(0);
            }

            for error in errors {
                eprintln!("{error}");
            }

            Ok(1)
        }
        ConfigAction::Dump => {
            let config = store.load()?;
            println!("{}", config_json(&config)?);
            Ok(0)
        }
        ConfigAction::Enable { provider } => set_provider_enabled(&store, &provider, true),
        ConfigAction::Disable { provider } => set_provider_enabled(&store, &provider, false),
        ConfigAction::SetApiKey {
            provider,
            stdin,
            api_key,
            value,
        }
        | ConfigAction::Set {
            provider,
            stdin,
            api_key,
            value,
        } => set_api_key(&store, &provider, stdin, api_key.or(value)),
        ConfigAction::SetCookie {
            provider,
            stdin,
            cookie,
            value,
        } => set_cookie(&store, &provider, stdin, cookie.or(value)),
        ConfigAction::ClearCookie { provider } => clear_cookie(&store, &provider),
        ConfigAction::Import { file } => import(&store, &file),
    }
}

/// Ported from the `enable`/`disable` cases of `Cli.ConfigAsync`.
fn set_provider_enabled(store: &ConfigStore, provider: &str, enabled: bool) -> Result<i32> {
    let mut config = store.load()?;
    let id = {
        let provider_config = find_or_create_provider(&mut config, provider)?;
        provider_config.enabled = Some(enabled);
        provider_config.id.clone()
    };
    store.save(config)?;

    println!("{}", enabled_message(&catalog::display_name(&id), enabled));
    Ok(0)
}

/// Ported from the `set`/`set-api-key` cases of `Cli.ConfigAsync`.
fn set_api_key(
    store: &ConfigStore,
    provider: &str,
    from_stdin: bool,
    value: Option<String>,
) -> Result<i32> {
    let value = read_secret(from_stdin, value, "Use --stdin, --api-key, or --value.")?;

    let mut config = store.load()?;
    let id = {
        let provider_config = find_or_create_provider(&mut config, provider)?;
        provider_config.api_key = Some(value.trim().to_string());
        if provider_config.enabled.is_none() {
            provider_config.enabled = Some(true);
        }
        provider_config.id.clone()
    };
    store.save(config)?;

    println!(
        "Saved token for {} in raw JSON config.",
        catalog::display_name(&id)
    );
    Ok(0)
}

/// Ported from the `set-cookie` case of `Cli.ConfigAsync`.
fn set_cookie(
    store: &ConfigStore,
    provider: &str,
    from_stdin: bool,
    value: Option<String>,
) -> Result<i32> {
    let value = read_secret(from_stdin, value, "Use --stdin, --cookie, or --value.")?;

    let mut config = store.load()?;
    let id = {
        let provider_config = find_or_create_provider(&mut config, provider)?;
        let id = provider_config.id.clone();
        SecureCookieStore::new().save_cookie_header(&id, &value)?;
        provider_config.cookie_source = Some("manual".to_string());
        provider_config.cookie_header = None;
        if provider_config.enabled.is_none() {
            provider_config.enabled = Some(true);
        }
        id
    };
    store.save(config)?;

    println!("Saved encrypted cookie for {}.", catalog::display_name(&id));
    Ok(0)
}

/// Ported from the `clear-cookie` case of `Cli.ConfigAsync`.
fn clear_cookie(store: &ConfigStore, provider: &str) -> Result<i32> {
    let mut config = store.load()?;
    let id = {
        let provider_config = find_or_create_provider(&mut config, provider)?;
        let id = provider_config.id.clone();
        SecureCookieStore::new().clear_cookie_header(&id)?;
        provider_config.cookie_header = None;
        id
    };
    store.save(config)?;

    println!("Cleared saved cookie for {}.", catalog::display_name(&id));
    Ok(0)
}

/// Ported from the `import` case of `Cli.ConfigAsync`.
fn import(store: &ConfigStore, file: &str) -> Result<i32> {
    store.import_explicit(file)?;
    println!("Imported config to {}", store.path().display());
    Ok(0)
}

/// Ported from `Cli.FindOrCreate`: validates the provider id and returns its
/// entry, appending a fresh one when the config does not have it yet.
fn find_or_create_provider<'a>(
    config: &'a mut EstaoConfig,
    provider: &str,
) -> Result<&'a mut ProviderConfig> {
    let id = catalog::normalize_id(provider);
    if !catalog::is_supported(&id) {
        bail!("Provider '{provider}' is not supported by Estao MVP.");
    }

    let index = match config.providers.iter().position(|item| item.id == id) {
        Some(index) => index,
        None => {
            config.providers.push(ProviderConfig {
                id,
                source: Some("auto".to_string()),
                cookie_source: Some("auto".to_string()),
                ..ProviderConfig::default()
            });
            config.providers.len() - 1
        }
    };

    Ok(&mut config.providers[index])
}

/// Applies `estao usage --provider` to a loaded config: exactly one provider
/// stays enabled, and the optional 1-based account index selects its account.
fn apply_provider_filter(config: &mut EstaoConfig, provider: &str, account_index: Option<usize>) {
    let normalized = catalog::normalize_id(provider);
    for item in &mut config.providers {
        item.enabled = Some(item.id == normalized);
        if item.enabled == Some(true) {
            if let Some(index) = account_index {
                item.active_account_index = Some(account_index_to_config(index));
            }
        }
    }
}

/// Converts the CLI's 1-based `--account-index` into the config's 0-based
/// index, clamped at 0 like the .NET `Math.Max(0, parsedIndex - 1)`.
fn account_index_to_config(index: usize) -> i32 {
    i32::try_from(index.saturating_sub(1)).unwrap_or(i32::MAX)
}

/// The `enable`/`disable` confirmation line.
fn enabled_message(display_name: &str, enabled: bool) -> String {
    format!(
        "{display_name} {}",
        if enabled { "enabled" } else { "disabled" }
    )
}

/// Reads a secret from stdin or from an option value.
fn read_secret(from_stdin: bool, value: Option<String>, missing_message: &str) -> Result<String> {
    if from_stdin {
        let mut text = String::new();
        io::stdin()
            .read_to_string(&mut text)
            .context("Failed to read stdin.")?;
        return Ok(text);
    }

    value.ok_or_else(|| anyhow!("{missing_message}"))
}

/// Serializes `config` as the pretty JSON stored in the config file.
///
/// `serde_json` is not a dependency of this crate and `Cargo.toml` is out of
/// scope, so this reuses [`ConfigStore`]'s serializer through a throwaway file
/// instead of duplicating the schema (it applies the same normalization too).
fn config_json(config: &EstaoConfig) -> Result<String> {
    let path = unique_temp_config_path();
    let result = (|| {
        let store = ConfigStore::with_path(&path);
        store.save(config.clone())?;
        fs::read_to_string(&path).context("Failed to read the serialized config.")
    })();
    let _ = fs::remove_file(&path);
    result
}

/// A unique path for the temporary config used by `usage --provider/--account-index`.
fn unique_temp_config_path() -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or(0);
    let counter = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!(
        "estao-config-{}-{nanos}-{counter}.json",
        std::process::id()
    ))
}

#[cfg(test)]
mod tests {
    use super::*;
    use estao_core::config::RefreshConfig;

    fn config_with_providers() -> EstaoConfig {
        EstaoConfig {
            refresh: RefreshConfig::default(),
            providers: vec![
                ProviderConfig {
                    id: "codex".to_string(),
                    enabled: Some(true),
                    active_account_index: Some(0),
                    ..ProviderConfig::default()
                },
                ProviderConfig {
                    id: "claude".to_string(),
                    enabled: None,
                    ..ProviderConfig::default()
                },
            ],
            ..EstaoConfig::default()
        }
    }

    #[test]
    fn provider_filter_enables_only_the_selected_provider() {
        let mut config = config_with_providers();

        apply_provider_filter(&mut config, "Claude", None);

        assert_eq!(config.providers[0].enabled, Some(false));
        assert_eq!(config.providers[1].enabled, Some(true));
        // No index given: the existing selection is untouched.
        assert_eq!(config.providers[0].active_account_index, Some(0));
    }

    #[test]
    fn provider_filter_normalizes_aliases_and_keeps_all_others_disabled() {
        let mut config = config_with_providers();

        apply_provider_filter(&mut config, "github-copilot", None);

        assert!(config
            .providers
            .iter()
            .all(|item| item.enabled == Some(false)));
    }

    #[test]
    fn provider_filter_converts_the_one_based_account_index() {
        let mut config = config_with_providers();

        apply_provider_filter(&mut config, "claude", Some(3));
        assert_eq!(config.providers[1].active_account_index, Some(2));
        // The index only applies to the provider that is enabled.
        assert_eq!(config.providers[0].active_account_index, Some(0));

        apply_provider_filter(&mut config, "claude", Some(0));
        assert_eq!(config.providers[1].active_account_index, Some(0));
    }

    #[test]
    fn provider_filter_clamps_huge_account_indexes() {
        assert_eq!(account_index_to_config(0), 0);
        assert_eq!(account_index_to_config(1), 0);
        assert_eq!(account_index_to_config(5), 4);
        assert_eq!(account_index_to_config(usize::MAX), i32::MAX);
    }

    #[test]
    fn find_or_create_provider_returns_the_existing_entry() {
        let mut config = config_with_providers();

        let found = find_or_create_provider(&mut config, "codex").unwrap();

        assert_eq!(found.id, "codex");
        assert_eq!(config.providers.len(), 2);
    }

    #[test]
    fn find_or_create_provider_appends_a_supported_provider() {
        let mut config = config_with_providers();

        let created = find_or_create_provider(&mut config, "Z.AI").unwrap();

        assert_eq!(created.id, "zai");
        assert_eq!(created.source.as_deref(), Some("auto"));
        assert_eq!(created.cookie_source.as_deref(), Some("auto"));
        assert_eq!(config.providers.len(), 3);
    }

    #[test]
    fn find_or_create_provider_rejects_unsupported_providers() {
        let mut config = config_with_providers();

        let error = find_or_create_provider(&mut config, "nope").unwrap_err();

        assert_eq!(
            error.to_string(),
            "Provider 'nope' is not supported by Estao MVP."
        );
        assert_eq!(config.providers.len(), 2);
    }

    #[test]
    fn enabled_message_matches_the_dotnet_output() {
        assert_eq!(enabled_message("Claude", true), "Claude enabled");
        assert_eq!(
            enabled_message("GitHub Copilot", false),
            "GitHub Copilot disabled"
        );
    }

    #[test]
    fn config_json_is_pretty_normalized_v2_json() {
        let json = config_json(&config_with_providers()).unwrap();

        assert!(json.contains("\n  \"version\": 2"), "{json}");
        assert!(json.contains("\"theme\": \"dark\""), "{json}");
        assert!(json.contains("\"id\": \"codex\""), "{json}");
        // The store fills in the providers the fixture omitted.
        assert!(json.contains("\"id\": \"zai\""), "{json}");
    }
}
