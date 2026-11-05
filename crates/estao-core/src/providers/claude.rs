//! Claude usage provider, ported from `ClaudeProvider.cs`.
//!
//! Supported transports:
//! * `oauth` reads `~/.claude/.credentials.json` (or an `sk-ant-oat` token
//!   from the configured account) and calls
//!   `https://api.anthropic.com/api/oauth/usage`.
//! * `web` uses a saved/configured `sessionKey` cookie, resolves the
//!   organization id via `https://claude.ai/api/organizations`, and reads
//!   that organization's usage endpoint.
//!
//! `auto` prefers OAuth and falls back to web cookies.

use std::fs;
use std::sync::Arc;

use anyhow::{Context, Result};
use serde_json::Value;

use super::helpers;
use super::{
    build_snapshot, is_blank, parse_json, rate_window, CookieSecretStore, HttpClient, UsageProvider,
};
use crate::config::{CookieSource, ProviderConfig, ProviderSource};
use crate::models::{FetchRequest, ProviderAccount, UsageSnapshot};

/// The Claude provider (OAuth credentials plus web cookies).
pub struct ClaudeProvider {
    http_client: HttpClient,
    cookie_store: Arc<dyn CookieSecretStore>,
}

impl ClaudeProvider {
    /// Creates a provider using the supplied HTTP client and cookie store.
    pub fn new(http_client: HttpClient, cookie_store: Arc<dyn CookieSecretStore>) -> Self {
        Self {
            http_client,
            cookie_store,
        }
    }

    fn try_read_oauth_token() -> Result<Option<String>> {
        let path = helpers::user_home()
            .join(".claude")
            .join(".credentials.json");
        if !path.exists() {
            return Ok(None);
        }

        let text = fs::read_to_string(&path)
            .with_context(|| format!("failed to read {}", path.display()))?;
        let document = parse_json(&text, &path.display().to_string())?;
        Ok(helpers::first_string(
            &document,
            &["access_token", "accessToken"],
        ))
    }

    fn resolve_cookie(
        &self,
        request: &FetchRequest<'_>,
        account: Option<&ProviderAccount>,
    ) -> Result<Option<String>> {
        let cookie_source = helpers::parse_cookie_source(request.config.cookie_source.as_deref());
        if cookie_source == CookieSource::Off {
            return Ok(None);
        }

        if let Some(secret) = account.and_then(|account| account.secret.as_deref()) {
            if secret.to_ascii_lowercase().contains("sessionkey=") {
                return Ok(Some(secret.to_string()));
            }
        }

        let stored_cookie = self.cookie_store.read_cookie_header(self.id())?;
        if !is_blank(stored_cookie.as_deref()) {
            return Ok(stored_cookie
                .as_deref()
                .map(helpers::normalize_cookie_header));
        }

        if !is_blank(request.config.cookie_header.as_deref()) {
            return Ok(request
                .config
                .cookie_header
                .as_deref()
                .map(helpers::normalize_cookie_header));
        }

        Ok(None)
    }

    fn fetch_oauth(&self, token: &str, account_label: Option<&str>) -> Result<UsageSnapshot> {
        let authorization = format!("Bearer {token}");
        let response = self.http_client.get(
            "https://api.anthropic.com/api/oauth/usage",
            &[
                ("Authorization", authorization.as_str()),
                ("anthropic-beta", "oauth-2025-04-20"),
            ],
        )?;

        if !response.is_success() {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "oauth",
                &format!("Claude OAuth usage API returned {}.", response.status),
            ));
        }

        let root = parse_json(&response.body, "the Claude OAuth usage response")?;
        Ok(snapshot_from_usage(&root, "oauth", account_label))
    }

    fn fetch_web(&self, cookie: &str, account_label: Option<&str>) -> Result<UsageSnapshot> {
        let org_response = self
            .http_client
            .get("https://claude.ai/api/organizations", &[("Cookie", cookie)])?;
        if !org_response.is_success() {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "web",
                &format!("Claude organizations API returned {}.", org_response.status),
            ));
        }

        let org_document = parse_json(&org_response.body, "the Claude organizations response")?;
        let org_id = helpers::first_string(&org_document, &["uuid", "id"]);
        if is_blank(org_id.as_deref()) {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "web",
                "Claude organization id was not found.",
            ));
        }
        let org_id = org_id.unwrap_or_default();

        let usage_url = format!("https://claude.ai/api/organizations/{org_id}/usage");
        let usage_response = self.http_client.get(&usage_url, &[("Cookie", cookie)])?;
        if !usage_response.is_success() {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "web",
                &format!("Claude usage API returned {}.", usage_response.status),
            ));
        }

        let root = parse_json(&usage_response.body, "the Claude usage response")?;
        Ok(snapshot_from_usage(&root, "web", account_label))
    }
}

impl UsageProvider for ClaudeProvider {
    fn id(&self) -> &'static str {
        "claude"
    }

    fn accounts(&self, config: &ProviderConfig) -> Vec<ProviderAccount> {
        let mut accounts = helpers::token_accounts(config);

        if !is_blank(config.cookie_header.as_deref()) {
            accounts.insert(
                0,
                ProviderAccount {
                    id: "manual-cookie".to_string(),
                    label: "Manual cookie".to_string(),
                    secret: config.cookie_header.clone(),
                    home_path: None,
                    workspace_id: None,
                },
            );
        }

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
        let account = request
            .account
            .cloned()
            .or_else(|| accounts.first().cloned());
        let account_label = account.as_ref().map(|account| account.label.as_str());
        let source = request.source;

        if matches!(source, ProviderSource::Auto | ProviderSource::OAuth) {
            let token = match account
                .as_ref()
                .and_then(|account| account.secret.as_deref())
            {
                Some(secret) if secret.starts_with("sk-ant-oat") => Some(secret.to_string()),
                _ => Self::try_read_oauth_token()?,
            };

            if !is_blank(token.as_deref()) {
                return self.fetch_oauth(token.as_deref().unwrap_or_default(), account_label);
            }

            if source == ProviderSource::OAuth {
                return Ok(UsageSnapshot::failure(
                    self.id(),
                    "oauth",
                    "Claude OAuth credentials were not found.",
                ));
            }
        }

        if matches!(source, ProviderSource::Auto | ProviderSource::Web) {
            let cookie = self.resolve_cookie(request, account.as_ref())?;
            if !is_blank(cookie.as_deref()) {
                return self.fetch_web(cookie.as_deref().unwrap_or_default(), account_label);
            }

            if source == ProviderSource::Web {
                return Ok(UsageSnapshot::failure(
                    self.id(),
                    "web",
                    "Claude cookie was not saved or configured.",
                ));
            }
        }

        Ok(UsageSnapshot::failure(
            self.id(),
            source.as_str(),
            "Claude MVP supports OAuth credentials and web cookies only.",
        ))
    }
}

fn snapshot_from_usage(root: &Value, source: &str, account_label: Option<&str>) -> UsageSnapshot {
    let mut windows = Vec::new();

    if let Some(session) = helpers::find_property(root, &["five_hour", "fiveHour", "session"]) {
        windows.push(rate_window(
            "session",
            "Session",
            helpers::percent_used_from(session, false),
            helpers::reset_from(session),
        ));
    }

    if let Some(weekly) = helpers::find_property(root, &["seven_day", "sevenDay", "weekly"]) {
        windows.push(rate_window(
            "weekly",
            "Weekly",
            helpers::percent_used_from(weekly, false),
            helpers::reset_from(weekly),
        ));
    }

    let account = helpers::first_string(root, &["email", "accountEmail"])
        .or_else(|| account_label.map(str::to_string));
    let plan = helpers::first_string(root, &["subscriptionType", "rate_limit_tier", "plan"]);

    build_snapshot("claude", source, windows, account, plan, None)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn snapshot_from_usage_maps_session_and_weekly_windows() {
        let root = json!({
            "five_hour": { "utilization": 40 },
            "seven_day": { "utilization": 70 },
            "email": "user@example.com",
            "subscriptionType": "pro"
        });

        let snapshot = snapshot_from_usage(&root, "oauth", Some("fallback"));
        assert_eq!(snapshot.provider, "claude");
        assert_eq!(snapshot.source, "oauth");
        assert_eq!(snapshot.windows.len(), 2);
        assert_eq!(snapshot.windows[0].id, "session");
        assert_eq!(snapshot.windows[1].id, "weekly");
        assert_eq!(snapshot.account.as_deref(), Some("user@example.com"));
        assert_eq!(snapshot.plan.as_deref(), Some("pro"));
    }

    #[test]
    fn normalize_cookie_header_strips_the_optional_prefix() {
        assert_eq!(
            helpers::normalize_cookie_header("Cookie: sessionKey=abc"),
            "sessionKey=abc"
        );
        assert_eq!(
            helpers::normalize_cookie_header("  sessionKey=abc  "),
            "sessionKey=abc"
        );
    }
}
