//! Codex usage provider, ported from `CodexProvider.cs`.
//!
//! Two transports are supported:
//! * `oauth` reads `auth.json` from a Codex home directory and calls
//!   `https://chatgpt.com/backend-api/wham/usage`.
//! * `cli` drives `codex app-server` over JSON-RPC and reads
//!   `account/rateLimits/read`.
//!
//! `auto` prefers usable OAuth data and falls back to the CLI.

use std::fs;
use std::path::Path;

use anyhow::{Context, Result};
use serde_json::Value;

use super::helpers::{self, ESTAO_USER_AGENT};
use super::{build_snapshot, is_blank, parse_json, rate_window, HttpClient, UsageProvider};
use crate::config::{ProviderConfig, ProviderSource};
use crate::models::{CreditsSnapshot, FetchRequest, ProviderAccount, RateWindow, UsageSnapshot};

const INITIALIZE: &str = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"Estao\",\"version\":\"0.1\"}}}\n";
const INITIALIZED: &str = "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}\n";
const ACCOUNT_READ: &str =
    "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"account/read\",\"params\":{}}\n";
const LIMITS_READ: &str =
    "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"account/rateLimits/read\",\"params\":{}}\n";

/// The Codex provider (OAuth `auth.json` plus `codex app-server`).
pub struct CodexProvider {
    http_client: HttpClient,
}

impl CodexProvider {
    /// Creates a provider using the supplied HTTP client.
    pub fn new(http_client: HttpClient) -> Self {
        Self { http_client }
    }

    fn try_fetch_oauth(
        &self,
        home: &str,
        account_label: Option<&str>,
    ) -> Result<Option<UsageSnapshot>> {
        let auth_path = Path::new(home).join("auth.json");
        if !auth_path.exists() {
            return Ok(None);
        }

        let text = fs::read_to_string(&auth_path)
            .with_context(|| format!("failed to read {}", auth_path.display()))?;
        let auth = parse_json(&text, &auth_path.display().to_string())?;
        let token = helpers::first_string(&auth, &["access_token", "accessToken"]);
        if is_blank(token.as_deref()) {
            return Ok(None);
        }
        let token = token.unwrap_or_default();

        let authorization = format!("Bearer {token}");
        let response = self.http_client.get(
            "https://chatgpt.com/backend-api/wham/usage",
            &[
                ("Authorization", authorization.as_str()),
                ("User-Agent", ESTAO_USER_AGENT),
            ],
        )?;

        if !response.is_success() {
            return Ok(Some(UsageSnapshot::failure(
                self.id(),
                "oauth",
                &format!("Codex usage API returned {}.", response.status),
            )));
        }

        let root = parse_json(&response.body, "the Codex usage response")?;
        Ok(Some(parse_rate_limits(&root, "oauth", account_label)))
    }

    fn fetch_cli(&self, home: &str, account_label: Option<&str>) -> Result<UsageSnapshot> {
        let stdin = format!("{INITIALIZE}{INITIALIZED}{ACCOUNT_READ}{LIMITS_READ}");
        let output = helpers::run_process(
            if cfg!(windows) { "codex.cmd" } else { "codex" },
            "-s read-only -a untrusted app-server",
            Some(home),
            &[("CODEX_HOME", Some(home))],
            Some(&stdin),
        )?;

        let mut windows: Vec<RateWindow> = Vec::new();
        let mut email = account_label.map(str::to_string);
        let mut plan: Option<String> = None;
        let mut credits: Option<CreditsSnapshot> = None;

        for line in output
            .lines()
            .map(str::trim)
            .filter(|line| !line.is_empty())
        {
            if !line.starts_with('{') {
                continue;
            }

            let root = parse_json(line, "a codex app-server response line")?;
            let parsed = parse_rate_limits(&root, "cli", email.as_deref());

            if let Some(Value::String(value)) = helpers::find_property(&root, &["email"]) {
                email = Some(value.clone());
            }

            if plan.is_none() {
                plan = parsed.plan.clone();
            }
            if credits.is_none() {
                credits = parsed.credits.clone();
            }

            for window in parse_windows(&root) {
                windows.retain(|existing| !existing.id.eq_ignore_ascii_case(&window.id));
                windows.push(window);
            }

            if let Some(balance) = helpers::first_number(&root, &["balance", "credits"]) {
                credits = Some(CreditsSnapshot {
                    balance: Some(balance),
                    unit: Some("credits".to_string()),
                    unlimited: None,
                });
            }
        }

        if windows.is_empty() && credits.is_none() {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "cli",
                "codex app-server did not return rate-limit data.",
            ));
        }

        Ok(build_snapshot(
            "codex", "cli", windows, email, plan, credits,
        ))
    }
}

impl UsageProvider for CodexProvider {
    fn id(&self) -> &'static str {
        "codex"
    }

    fn accounts(&self, config: &ProviderConfig) -> Vec<ProviderAccount> {
        let homes: Vec<String> = match config.codex_profile_home_paths.as_ref() {
            Some(paths) if !paths.is_empty() => paths.clone(),
            _ => vec![std::env::var("CODEX_HOME").ok().unwrap_or_else(|| {
                helpers::user_home()
                    .join(".codex")
                    .to_string_lossy()
                    .into_owned()
            })],
        };

        homes
            .into_iter()
            .filter(|path| !path.trim().is_empty())
            .map(|raw_home| {
                let home = helpers::expand_home(&raw_home);
                let trimmed = home.trim_end_matches(['/', '\\']);
                let label = Path::new(trimmed)
                    .file_name()
                    .map(|name| name.to_string_lossy().into_owned())
                    .filter(|label| !label.trim().is_empty())
                    .unwrap_or_else(|| "Default".to_string());

                ProviderAccount {
                    id: home.clone(),
                    label,
                    secret: None,
                    home_path: Some(home),
                    workspace_id: None,
                }
            })
            .collect()
    }

    fn fetch(&self, request: &FetchRequest<'_>) -> Result<UsageSnapshot> {
        let accounts = self.accounts(request.config);
        let account = request
            .account
            .cloned()
            .or_else(|| accounts.first().cloned());
        let account_label = account.as_ref().map(|account| account.label.as_str());
        let home = account
            .as_ref()
            .and_then(|account| account.home_path.clone())
            .unwrap_or_else(|| {
                helpers::user_home()
                    .join(".codex")
                    .to_string_lossy()
                    .into_owned()
            });

        let mut oauth_snapshot: Option<UsageSnapshot> = None;

        if matches!(request.source, ProviderSource::Auto | ProviderSource::OAuth) {
            let oauth = self.try_fetch_oauth(&home, account_label)?;
            oauth_snapshot = oauth.clone();
            if request.source == ProviderSource::OAuth {
                return Ok(oauth.unwrap_or_else(|| {
                    UsageSnapshot::failure(
                        self.id(),
                        "oauth",
                        "Codex auth.json is missing or does not contain an access token.",
                    )
                }));
            }

            if let Some(snapshot) = oauth {
                if has_usable_rate_limits(&snapshot) {
                    return Ok(snapshot);
                }
            }
        }

        if matches!(request.source, ProviderSource::Auto | ProviderSource::Cli) {
            let cli = self.fetch_cli(&home, account_label)?;
            return Ok(if cli.error.is_none() || oauth_snapshot.is_none() {
                cli
            } else {
                oauth_snapshot.unwrap_or(cli)
            });
        }

        Ok(UsageSnapshot::failure(
            self.id(),
            request.source.as_str(),
            "Codex MVP supports OAuth auth.json and codex app-server only.",
        ))
    }
}

fn has_usable_rate_limits(snapshot: &UsageSnapshot) -> bool {
    snapshot
        .windows
        .iter()
        .any(|window| window.percent_used.is_some() || window.reset_at.is_some())
}

/// Parses a WHAM/app-server payload into a snapshot, mirroring
/// `CodexProvider.ParseRateLimits`.
pub(crate) fn parse_rate_limits(
    root: &Value,
    source: &str,
    account_label: Option<&str>,
) -> UsageSnapshot {
    let windows = parse_windows(root);

    let email = helpers::first_string(root, &["email", "accountEmail"])
        .or_else(|| account_label.map(str::to_string));
    let rate_limits = find_rate_limits(root);
    let plan_names = [
        "plan",
        "planType",
        "plan_type",
        "subscriptionType",
        "subscription_type",
    ];
    let plan = rate_limits
        .and_then(|limits| helpers::first_string(limits, &plan_names))
        .or_else(|| helpers::first_string(root, &plan_names));

    let credits_element = rate_limits
        .and_then(|limits| helpers::find_property(limits, &["credits"]))
        .or_else(|| helpers::find_property(root, &["credits"]));
    let balance = match credits_element {
        Some(credits) => helpers::first_number(credits, &["balance", "credits"]),
        None => helpers::first_number(root, &["balance", "credits"]),
    };
    let unlimited = credits_element.and_then(|credits| first_boolean(credits, &["unlimited"]));
    let credits = balance.map(|value| CreditsSnapshot {
        balance: Some(value),
        unit: Some("credits".to_string()),
        unlimited,
    });

    build_snapshot("codex", source, windows, email, plan, credits)
}

fn parse_windows(root: &Value) -> Vec<RateWindow> {
    let rate_limits = find_rate_limits(root).unwrap_or(root);
    let mut windows = Vec::new();

    add_window(
        &mut windows,
        rate_limits,
        "session",
        "Session",
        &[
            "primary",
            "primary_window",
            "primaryWindow",
            "five_hour",
            "fiveHour",
        ],
    );
    add_window(
        &mut windows,
        rate_limits,
        "weekly",
        "Weekly",
        &[
            "secondary",
            "secondary_window",
            "secondaryWindow",
            "weekly",
            "weekly_window",
            "weeklyWindow",
        ],
    );

    windows
}

fn find_rate_limits(root: &Value) -> Option<&Value> {
    if let Some(limits_by_id) =
        helpers::find_property(root, &["rateLimitsByLimitId", "rate_limits_by_limit_id"])
    {
        if let Some(codex) = helpers::find_property(limits_by_id, &["codex"]) {
            if has_rate_limit_data(codex) {
                return Some(codex);
            }
        }
    }

    helpers::find_property(root, &["rateLimits", "rate_limits", "rate_limit"])
}

fn has_rate_limit_data(element: &Value) -> bool {
    [
        "primary",
        "secondary",
        "primary_window",
        "secondary_window",
        "primaryWindow",
        "secondaryWindow",
        "five_hour",
        "weekly",
    ]
    .iter()
    .any(|name| helpers::find_property(element, &[*name]).is_some_and(|value| value.is_object()))
}

fn add_window(
    windows: &mut Vec<RateWindow>,
    rate_limits: &Value,
    fallback_id: &str,
    fallback_title: &str,
    names: &[&str],
) {
    let Some(element) = helpers::find_property(rate_limits, names) else {
        return;
    };
    if !element.is_object() {
        return;
    }

    let duration_minutes = window_duration_minutes(element);
    let is_weekly = duration_minutes.is_some_and(|minutes| minutes >= 1000.0);
    let is_session = duration_minutes.is_some_and(|minutes| minutes > 0.0 && minutes < 1000.0);
    let id = if is_weekly {
        "weekly"
    } else if is_session {
        "session"
    } else {
        fallback_id
    };
    let title = match id {
        "weekly" => "Weekly",
        "session" => "Session",
        _ => fallback_title,
    };

    let window = rate_window(
        id,
        title,
        helpers::percent_used_from(element, false),
        helpers::reset_from(element),
    );
    windows.retain(|existing| !existing.id.eq_ignore_ascii_case(id));
    windows.push(window);
}

fn window_duration_minutes(element: &Value) -> Option<f64> {
    let minutes = helpers::first_number(
        element,
        &[
            "windowDurationMins",
            "window_duration_mins",
            "windowMinutes",
            "window_minutes",
        ],
    );
    if minutes.is_some() {
        return minutes;
    }

    helpers::first_number(
        element,
        &[
            "limitWindowSeconds",
            "limit_window_seconds",
            "windowDurationSeconds",
            "window_duration_seconds",
        ],
    )
    .map(|seconds| seconds / 60.0)
}

fn first_boolean(element: &Value, names: &[&str]) -> Option<bool> {
    let map = element.as_object()?;
    for name in names {
        let Some(property) = map.get(*name) else {
            continue;
        };

        match property {
            Value::Bool(value) => return Some(*value),
            Value::String(value) => {
                let value = value.trim();
                if value.eq_ignore_ascii_case("true") {
                    return Some(true);
                }
                if value.eq_ignore_ascii_case("false") {
                    return Some(false);
                }
            }
            _ => {}
        }
    }

    None
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from CodexProviderTests.parses_current_app_server_rate_limits_and_maps_by_duration.
    #[test]
    fn parses_current_app_server_rate_limits_and_maps_by_duration() {
        let root: Value = serde_json::from_str(
            r#"
            {
              "id": 3,
              "result": {
                "rateLimits": {
                  "planType": "pro",
                  "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 1779459394 },
                  "secondary": { "usedPercent": 18, "windowDurationMins": 10080, "resetsAt": 1779826837 },
                  "credits": { "unlimited": false, "balance": "766.76" }
                }
              }
            }
            "#,
        )
        .unwrap();

        let snapshot = parse_rate_limits(&root, "cli", Some("fallback@example.com"));

        assert_eq!(snapshot.plan.as_deref(), Some("pro"));
        assert_eq!(snapshot.windows.len(), 2);
        assert_eq!(
            snapshot
                .windows
                .iter()
                .find(|window| window.id == "session")
                .and_then(|window| window.percent_used),
            Some(0.25)
        );
        assert_eq!(
            snapshot
                .windows
                .iter()
                .find(|window| window.id == "weekly")
                .and_then(|window| window.percent_used),
            Some(0.18)
        );
        assert_eq!(
            snapshot
                .credits
                .as_ref()
                .and_then(|credits| credits.balance),
            Some(766.76)
        );
    }

    // Ported from CodexProviderTests.parses_legacy_wham_rate_limits.
    #[test]
    fn parses_legacy_wham_rate_limits() {
        let root: Value = serde_json::from_str(
            r#"
            {
              "plan_type": "pro",
              "rate_limit": {
                "primary_window": { "used_percent": 0, "limit_window_seconds": 18000, "reset_at": 1762147153 },
                "secondary_window": { "used_percent": 100, "limit_window_seconds": 604800, "reset_at": 1762650589 }
              }
            }
            "#,
        )
        .unwrap();

        let snapshot = parse_rate_limits(&root, "oauth", None);

        assert_eq!(snapshot.plan.as_deref(), Some("pro"));
        assert_eq!(
            snapshot
                .windows
                .iter()
                .find(|window| window.id == "session")
                .and_then(|window| window.percent_used),
            Some(0.0)
        );
        assert_eq!(
            snapshot
                .windows
                .iter()
                .find(|window| window.id == "weekly")
                .and_then(|window| window.percent_used),
            Some(1.0)
        );
    }

    // Ported from CodexProviderTests.interprets_one_used_percent_from_wham_as_one_percent.
    #[test]
    fn interprets_one_used_percent_from_wham_as_one_percent() {
        let root: Value = serde_json::from_str(
            r#"
            {
              "rate_limit": {
                "primary_window": { "used_percent": 1, "limit_window_seconds": 604800 }
              }
            }
            "#,
        )
        .unwrap();

        let snapshot = parse_rate_limits(&root, "oauth", None);

        assert_eq!(snapshot.windows.len(), 1);
        assert_eq!(snapshot.windows[0].percent_used, Some(0.01));
    }

    // Ported from CodexProviderTests.ignores_null_secondary_when_primary_is_the_weekly_window.
    #[test]
    fn ignores_null_secondary_when_primary_is_the_weekly_window() {
        let root: Value = serde_json::from_str(
            r#"
            {
              "result": {
                "rateLimits": {
                  "primary": { "usedPercent": 2, "windowDurationMins": 10080, "resetsAt": 1787030441 },
                  "secondary": null,
                  "planType": "plus"
                }
              }
            }
            "#,
        )
        .unwrap();

        let snapshot = parse_rate_limits(&root, "cli", None);

        assert_eq!(snapshot.windows.len(), 1);
        let window = &snapshot.windows[0];
        assert_eq!(window.id, "weekly");
        assert_eq!(window.percent_used, Some(0.02));
        assert!(window.reset_at.is_some());
    }

    // Ported from CodexProviderTests.prefers_codex_entry_from_rate_limits_by_limit_id.
    #[test]
    fn prefers_codex_entry_from_rate_limits_by_limit_id() {
        let root: Value = serde_json::from_str(
            r#"
            {
              "result": {
                "rateLimits": { "primary": null, "secondary": null, "planType": "plus" },
                "rateLimitsByLimitId": {
                  "codex": {
                    "primary": { "usedPercent": 42, "windowDurationMins": 10080, "resetsAt": 1779826837 }
                  }
                }
              }
            }
            "#,
        )
        .unwrap();

        let snapshot = parse_rate_limits(&root, "cli", None);

        assert_eq!(snapshot.windows.len(), 1);
        let window = &snapshot.windows[0];
        assert_eq!(window.id, "weekly");
        assert_eq!(window.percent_used, Some(0.42));
        assert!(window.reset_at.is_some());
    }

    #[test]
    fn accounts_expand_configured_profile_homes() {
        let provider = CodexProvider::new(HttpClient::new());
        let config = ProviderConfig {
            codex_profile_home_paths: Some(vec!["C:\\profiles\\work\\".to_string()]),
            ..ProviderConfig::default()
        };

        let accounts = provider.accounts(&config);
        assert_eq!(accounts.len(), 1);
        assert_eq!(accounts[0].label, "work");
        assert_eq!(
            accounts[0].home_path.as_deref(),
            Some("C:\\profiles\\work\\")
        );
    }

    #[test]
    fn oauth_auto_falls_back_to_the_cli_failure_message() {
        // Not a network test: it only verifies the documented unsupported
        // source path keeps the .NET message.
        let provider = CodexProvider::new(HttpClient::new());
        let config = ProviderConfig::default();
        let request = FetchRequest {
            config: &config,
            account: None,
            source: ProviderSource::Web,
        };

        let snapshot = provider.fetch(&request).unwrap();
        assert_eq!(
            snapshot.error.as_deref(),
            Some("Codex MVP supports OAuth auth.json and codex app-server only.")
        );
        assert_eq!(snapshot.source, "web");
    }
}
