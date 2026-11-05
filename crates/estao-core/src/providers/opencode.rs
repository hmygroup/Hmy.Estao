//! OpenCode usage provider, ported from `OpenCodeProvider.cs`.
//!
//! OpenCode exposes usage through SolidStart server functions on
//! `https://opencode.ai/_server`. The provider resolves a workspace id (from
//! the account/config or the workspaces function), then reads the
//! subscription function and extracts the `rollingUsage`/`weeklyUsage`
//! objects from its JavaScript response.

use std::sync::Arc;

use anyhow::{bail, Result};
use serde_json::Value;

use super::helpers;
use super::{
    build_snapshot, is_blank, parse_json, rate_window, CookieSecretStore, HttpClient, UsageProvider,
};
use crate::config::{CookieSource, ProviderConfig};
use crate::models::{FetchRequest, ProviderAccount, RateWindow, UsageSnapshot};

const WORKSPACES_FUNCTION: &str =
    "def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f";
const SUBSCRIPTION_FUNCTION: &str =
    "7abeebee372f304e050aaaf92be863f4a86490e382f8c79db68fd94040d691b4";

/// The OpenCode provider (cookie based web session).
pub struct OpenCodeProvider {
    http_client: HttpClient,
    cookie_store: Arc<dyn CookieSecretStore>,
}

impl OpenCodeProvider {
    /// Creates a provider using the supplied HTTP client and cookie store.
    pub fn new(http_client: HttpClient, cookie_store: Arc<dyn CookieSecretStore>) -> Self {
        Self {
            http_client,
            cookie_store,
        }
    }

    fn resolve_cookie(
        &self,
        request: &FetchRequest<'_>,
        account: Option<&ProviderAccount>,
    ) -> Result<Option<String>> {
        let source = helpers::parse_cookie_source(request.config.cookie_source.as_deref());
        if source == CookieSource::Off {
            return Ok(None);
        }

        if let Some(secret) = account.and_then(|account| account.secret.as_deref()) {
            if !secret.trim().is_empty() {
                return Ok(Some(helpers::normalize_cookie_header(secret)));
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

    fn post_server_function(
        &self,
        function_id: &str,
        cookie: &str,
        payload: &str,
    ) -> Result<String> {
        let response = self.http_client.post_json(
            "https://opencode.ai/_server",
            &[
                ("Cookie", cookie),
                ("Accept", "text/javascript, application/json"),
                ("x-solidstart-server-function", function_id),
            ],
            payload,
        )?;

        if !response.is_success() {
            bail!("OpenCode API returned {}.", response.status);
        }

        Ok(response.body)
    }

    fn fetch_workspace_id(&self, cookie: &str) -> Result<Option<String>> {
        let body = self.post_server_function(WORKSPACES_FUNCTION, cookie, "[]")?;

        static WORKSPACE_REGEX: std::sync::LazyLock<regex::Regex> =
            std::sync::LazyLock::new(|| {
                regex::Regex::new("wrk_[A-Za-z0-9_-]+")
                    .expect("the OpenCode workspace regex is valid")
            });

        Ok(WORKSPACE_REGEX
            .find(&body)
            .map(|found| found.as_str().to_string()))
    }
}

impl UsageProvider for OpenCodeProvider {
    fn id(&self) -> &'static str {
        "opencode"
    }

    fn accounts(&self, config: &ProviderConfig) -> Vec<ProviderAccount> {
        let mut accounts = helpers::token_accounts(config);

        if !is_blank(config.workspace_id.as_deref()) || !is_blank(config.cookie_header.as_deref()) {
            accounts.insert(
                0,
                ProviderAccount {
                    id: "configured".to_string(),
                    label: "Configured workspace".to_string(),
                    secret: config.cookie_header.clone(),
                    home_path: None,
                    workspace_id: config.workspace_id.clone(),
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

        let cookie = self.resolve_cookie(request, account.as_ref())?;
        if is_blank(cookie.as_deref()) {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "web",
                "OpenCode requires a saved or legacy manual cookie.",
            ));
        }
        let cookie = cookie.unwrap_or_default();

        let mut workspace_id = account
            .as_ref()
            .and_then(|account| account.workspace_id.clone())
            .or_else(|| request.config.workspace_id.clone());
        if is_blank(workspace_id.as_deref()) {
            workspace_id = self.fetch_workspace_id(&cookie)?;
        }

        if is_blank(workspace_id.as_deref()) {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "web",
                "OpenCode workspace id was not found.",
            ));
        }
        let workspace_id = workspace_id.unwrap_or_default();

        let body = self.post_server_function(
            SUBSCRIPTION_FUNCTION,
            &cookie,
            &format!("[\"{workspace_id}\"]"),
        )?;
        let account_label = account
            .as_ref()
            .map(|account| account.label.clone())
            .unwrap_or_else(|| workspace_id.clone());

        snapshot_from_body(&body, &account_label)
    }
}

fn snapshot_from_body(body: &str, account: &str) -> Result<UsageSnapshot> {
    let mut windows: Vec<RateWindow> = Vec::new();
    for captures in helpers::opencode_window_regex().captures_iter(body) {
        let name = captures
            .name("name")
            .map(|capture| capture.as_str())
            .unwrap_or_default();
        let id = if name == "rollingUsage" {
            "session"
        } else {
            "weekly"
        };
        let title = if id == "session" { "Session" } else { "Weekly" };
        let json = format!(
            "{{{}}}",
            captures
                .name("body")
                .map(|capture| capture.as_str())
                .unwrap_or_default()
        );
        let value: Value = parse_json(&json, "an OpenCode usage window")?;
        windows.push(rate_window(
            id,
            title,
            helpers::percent_used_from(&value, false),
            helpers::reset_from(&value),
        ));
    }

    if windows.is_empty() {
        return Ok(UsageSnapshot::failure(
            "opencode",
            "web",
            "OpenCode usage fields were not found.",
        ));
    }

    Ok(build_snapshot(
        "opencode",
        "web",
        windows,
        Some(account.to_string()),
        None,
        None,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshot_from_body_extracts_rolling_and_weekly_windows() {
        let body = r#"
            const data = {
              "rollingUsage": { "used": 50, "limit": 200, "resetInSec": 3600 },
              "weeklyUsage": { "percentUsed": 25, "resetAt": "2026-08-01T00:00:00Z" }
            };
        "#;

        let snapshot = snapshot_from_body(body, "wrk_123").unwrap();
        assert_eq!(snapshot.provider, "opencode");
        assert_eq!(snapshot.source, "web");
        assert_eq!(snapshot.account.as_deref(), Some("wrk_123"));
        assert_eq!(snapshot.windows.len(), 2);
        assert_eq!(snapshot.windows[0].id, "session");
        assert_eq!(snapshot.windows[0].percent_used, Some(0.25));
        assert_eq!(snapshot.windows[1].id, "weekly");
        assert_eq!(snapshot.windows[1].percent_used, Some(0.25));
        assert!(snapshot.windows[1].reset_at.is_some());
    }

    #[test]
    fn snapshot_from_body_returns_a_failure_without_usage_fields() {
        let snapshot = snapshot_from_body("nothing here", "wrk_123").unwrap();
        assert_eq!(
            snapshot.error.as_deref(),
            Some("OpenCode usage fields were not found.")
        );
        assert!(snapshot.windows.is_empty());
    }
}
