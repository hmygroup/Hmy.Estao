//! GitHub Copilot usage provider, ported from `CopilotProvider.cs`.
//!
//! Uses a configured token/API key or the GitHub OAuth token saved by the
//! device-flow login (the login itself lives in the app layer), then calls
//! `https://api.github.com/copilot_internal/user` (or the enterprise host
//! variant) with the same headers VS Code sends.

use std::sync::Arc;

use anyhow::Result;
use serde_json::Value;

use super::helpers;
use super::{
    build_snapshot, is_blank, parse_json, rate_window, DefaultOAuthTokenStore, HttpClient,
    OAuthTokenStore, UsageProvider,
};
use crate::config::{ProviderConfig, ProviderSource};
use crate::models::{FetchRequest, ProviderAccount, RateWindow, UsageSnapshot};

/// The GitHub Copilot provider.
pub struct CopilotProvider {
    http_client: HttpClient,
    oauth_token_store: Arc<dyn OAuthTokenStore>,
}

impl CopilotProvider {
    /// Creates a provider using the default [`SecureOAuthTokenStore`].
    pub fn new(http_client: HttpClient) -> Self {
        Self {
            http_client,
            oauth_token_store: Arc::new(DefaultOAuthTokenStore::new()),
        }
    }

    /// Creates a provider with an injected OAuth token store.
    pub fn with_oauth_token_store(
        http_client: HttpClient,
        oauth_token_store: Arc<dyn OAuthTokenStore>,
    ) -> Self {
        Self {
            http_client,
            oauth_token_store,
        }
    }

    fn fetch_with_token(
        &self,
        token: &str,
        account_label: Option<&str>,
        source: &str,
        request: &FetchRequest<'_>,
    ) -> Result<UsageSnapshot> {
        let host = normalize_enterprise_host(request.config.enterprise_host.as_deref());
        let api_host = match host.as_deref() {
            Some(host) if !host.trim().is_empty() => format!("api.{host}"),
            _ => "api.github.com".to_string(),
        };

        let url = format!("https://{api_host}/copilot_internal/user");
        let authorization = format!("token {token}");
        let response = self.http_client.get(
            &url,
            &[
                ("Authorization", authorization.as_str()),
                ("Accept", "application/json"),
                ("User-Agent", "GitHubCopilotChat/0.26.7"),
                ("Editor-Version", "vscode/1.96.2"),
                ("Editor-Plugin-Version", "copilot-chat/0.26.7"),
                ("X-Github-Api-Version", "2025-04-01"),
            ],
        )?;

        if !response.is_success() {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "api",
                &format!("Copilot usage API returned {}.", response.status),
            ));
        }

        let root = parse_json(&response.body, "the Copilot usage response")?;
        Ok(snapshot_from_json(&root, account_label, source))
    }
}

impl UsageProvider for CopilotProvider {
    fn id(&self) -> &'static str {
        "copilot"
    }

    fn accounts(&self, config: &ProviderConfig) -> Vec<ProviderAccount> {
        let mut accounts = helpers::token_accounts(config);

        if !is_blank(config.api_key.as_deref()) {
            accounts.insert(
                0,
                ProviderAccount {
                    id: "api-key".to_string(),
                    label: "Configured token".to_string(),
                    secret: config.api_key.clone(),
                    home_path: None,
                    workspace_id: None,
                },
            );
        }

        accounts
    }

    fn fetch(&self, request: &FetchRequest<'_>) -> Result<UsageSnapshot> {
        let accounts = self.accounts(request.config);
        let mut account = request
            .account
            .cloned()
            .or_else(|| accounts.first().cloned());
        let mut token = account
            .as_ref()
            .and_then(|account| account.secret.clone())
            .or_else(|| request.config.api_key.clone());
        let mut source = "api";

        if is_blank(token.as_deref())
            && matches!(request.source, ProviderSource::Auto | ProviderSource::OAuth)
        {
            let oauth = self.oauth_token_store.read(self.id())?;
            token = oauth.as_ref().map(|oauth| oauth.access_token.clone());
            if account.is_none() {
                account = oauth.as_ref().map(|oauth| ProviderAccount {
                    id: "oauth".to_string(),
                    label: oauth
                        .account_label
                        .clone()
                        .unwrap_or_else(|| "GitHub OAuth".to_string()),
                    secret: token.clone(),
                    home_path: None,
                    workspace_id: None,
                });
            }
            source = "oauth";
        }

        if is_blank(token.as_deref()) {
            return Ok(UsageSnapshot::failure(
                self.id(),
                source,
                "Copilot is not connected. In Settings, choose Sign in with OAuth and complete the GitHub device flow.",
            ));
        }

        let token = token.unwrap_or_default();
        self.fetch_with_token(
            &token,
            account.as_ref().map(|a| a.label.as_str()),
            source,
            request,
        )
    }
}

fn snapshot_from_json(root: &Value, account_label: Option<&str>, source: &str) -> UsageSnapshot {
    let mut windows = Vec::new();
    let global_reset = helpers::reset_from(root);

    // The current copilot_internal/user response uses quota_snapshots and a
    // top-level quota_reset_date (the same shape consumed by VS Code).
    if let Some(snapshots) = helpers::find_property(root, &["quota_snapshots"]) {
        add_quota_snapshot(
            &mut windows,
            snapshots,
            "premium_interactions",
            "Premium",
            global_reset,
        );
        add_quota_snapshot(&mut windows, snapshots, "chat", "Chat", global_reset);
    }

    // Older GitHub web responses expose quotas.remaining/limits and resetDate.
    if windows.is_empty() {
        if let Some(quotas) = helpers::find_property(root, &["quotas"]) {
            let remaining = helpers::find_property(quotas, &["remaining"]);
            let limits = helpers::find_property(quotas, &["limits"]);
            let reset = helpers::reset_from(quotas).or(global_reset);
            add_legacy_quota(
                &mut windows,
                "premium",
                "Premium",
                remaining,
                limits,
                reset,
                &[
                    "premiumInteractionsPercentage",
                    "premium_interactions_percentage",
                ],
                &["premiumInteractions", "premium_interactions"],
            );
            add_legacy_quota(
                &mut windows,
                "chat",
                "Chat",
                remaining,
                limits,
                reset,
                &["chatPercentage"],
                &["chat"],
            );
        }
    }

    // Keep compatibility with responses that place the quota objects at the
    // root rather than under quota_snapshots.
    if windows.is_empty() {
        add_quota_object(
            &mut windows,
            root,
            "premiumInteractions",
            "premium_interactions",
            "Premium",
            global_reset,
        );
        add_quota_object(&mut windows, root, "chat", "chat", "Chat", global_reset);
    }

    let account = helpers::first_string(root, &["login", "email", "username"])
        .or_else(|| account_label.map(str::to_string));
    let plan = helpers::first_string(root, &["copilotPlan", "plan", "licenseType"]);

    build_snapshot("copilot", source, windows, account, plan, None)
}

fn add_quota_snapshot(
    windows: &mut Vec<RateWindow>,
    snapshots: &Value,
    property_name: &str,
    title: &str,
    global_reset: Option<chrono::DateTime<chrono::Utc>>,
) {
    let Some(quota) = helpers::find_property(snapshots, &[property_name]) else {
        return;
    };
    if !quota.is_object() {
        return;
    }

    let percent = helpers::percent_used_from(quota, false);
    let has_values =
        helpers::first_number(quota, &["entitlement", "remaining", "percent_remaining"]).is_some();
    if percent.is_some() || has_values {
        windows.push(rate_window(
            property_name,
            title,
            percent,
            helpers::reset_from(quota).or(global_reset),
        ));
    }
}

fn add_quota_object(
    windows: &mut Vec<RateWindow>,
    root: &Value,
    camel_name: &str,
    snake_name: &str,
    title: &str,
    global_reset: Option<chrono::DateTime<chrono::Utc>>,
) {
    let Some(quota) = helpers::find_property(root, &[camel_name, snake_name]) else {
        return;
    };
    if !quota.is_object() {
        return;
    }

    windows.push(rate_window(
        camel_name,
        title,
        helpers::percent_used_from(quota, false),
        helpers::reset_from(quota).or(global_reset),
    ));
}

#[allow(clippy::too_many_arguments)]
fn add_legacy_quota(
    windows: &mut Vec<RateWindow>,
    id: &str,
    title: &str,
    remaining: Option<&Value>,
    limits: Option<&Value>,
    reset: Option<chrono::DateTime<chrono::Utc>>,
    percentage_names: &[&str],
    value_names: &[&str],
) {
    let Some(remaining_element) = remaining else {
        return;
    };

    let percent_remaining = helpers::first_number(remaining_element, percentage_names);
    let mut percent_used = percent_remaining
        .and_then(|value| helpers::normalize_percent(value).map(|normalized| 1.0 - normalized));

    if percent_used.is_none() {
        if let Some(limits) = limits {
            let remaining_value = helpers::first_number(remaining_element, value_names);
            let limit_value = helpers::first_number(limits, value_names);
            if let (Some(remaining_value), Some(limit_value)) = (remaining_value, limit_value) {
                if limit_value > 0.0 {
                    percent_used = Some((1.0 - remaining_value / limit_value).clamp(0.0, 1.0));
                }
            }
        }
    }

    if percent_used.is_some() || helpers::first_number(remaining_element, value_names).is_some() {
        windows.push(rate_window(id, title, percent_used, reset));
    }
}

fn normalize_enterprise_host(host: Option<&str>) -> Option<String> {
    let host = host?;
    if host.trim().is_empty() {
        return None;
    }

    let trimmed = host.trim();
    if let Some((_, rest)) = trimmed.split_once("://") {
        let authority = rest.split('/').next().unwrap_or_default();
        let authority = authority.rsplit('@').next().unwrap_or(authority);
        let host = match authority.strip_prefix('[') {
            // IPv6 literal: keep the brackets, drop any port.
            Some(rest) => rest.split(']').next().map(|inner| format!("[{inner}]")),
            None => authority
                .rsplit_once(':')
                .map(|(host, _)| host.to_string())
                .or_else(|| Some(authority.to_string())),
        };
        return host.filter(|host| !host.is_empty());
    }

    Some(trimmed.trim_matches('/').to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn maps_quota_snapshots_like_vscode() {
        let root = json!({
            "login": "octocat",
            "copilotPlan": "individual",
            "quota_reset_date": "2026-08-01T00:00:00Z",
            "quota_snapshots": {
                "premium_interactions": { "percent_remaining": 25, "remaining": 50, "entitlement": 200 },
                "chat": { "percent_remaining": 100 }
            }
        });

        let snapshot = snapshot_from_json(&root, None, "oauth");
        assert_eq!(snapshot.account.as_deref(), Some("octocat"));
        assert_eq!(snapshot.plan.as_deref(), Some("individual"));
        assert_eq!(snapshot.windows.len(), 2);
        assert_eq!(snapshot.windows[0].id, "premium_interactions");
        assert_eq!(snapshot.windows[0].percent_used, Some(0.75));
        assert!(snapshot.windows[0].reset_at.is_some());
        assert_eq!(snapshot.windows[1].id, "chat");
        assert_eq!(snapshot.windows[1].percent_used, Some(0.0));
    }

    #[test]
    fn falls_back_to_legacy_quotas() {
        let root = json!({
            "quotas": {
                "remaining": { "premiumInteractionsPercentage": 80, "chatPercentage": 50 },
                "limits": { "premiumInteractions": 100, "chat": 100 },
                "resetDate": "2026-08-01T00:00:00Z"
            }
        });

        let snapshot = snapshot_from_json(&root, Some("fallback"), "api");
        assert_eq!(snapshot.account.as_deref(), Some("fallback"));
        assert_eq!(snapshot.windows.len(), 2);
        let premium = snapshot.windows[0].percent_used.unwrap();
        assert!((premium - 0.2).abs() < 1e-9, "{premium}");
        assert_eq!(snapshot.windows[1].percent_used, Some(0.5));
    }

    #[test]
    fn legacy_value_fallback_uses_remaining_and_limits() {
        let root = json!({
            "quotas": {
                "remaining": { "premiumInteractions": 25 },
                "limits": { "premiumInteractions": 100 }
            }
        });

        let snapshot = snapshot_from_json(&root, None, "api");
        assert_eq!(snapshot.windows.len(), 1);
        assert_eq!(snapshot.windows[0].percent_used, Some(0.75));
    }

    #[test]
    fn normalizes_enterprise_hosts() {
        assert_eq!(normalize_enterprise_host(None), None);
        assert_eq!(normalize_enterprise_host(Some("  ")), None);
        assert_eq!(
            normalize_enterprise_host(Some("https://ghe.example.com/")),
            Some("ghe.example.com".to_string())
        );
        assert_eq!(
            normalize_enterprise_host(Some("https://ghe.example.com:8443")),
            Some("ghe.example.com".to_string())
        );
        assert_eq!(
            normalize_enterprise_host(Some("ghe.example.com/")),
            Some("ghe.example.com".to_string())
        );
    }
}
