//! Z.ai (GLM coding plan) usage provider, ported from `ZaiProvider.cs`.
//!
//! Calls `https://api.z.ai/api/monitor/usage/quota/limit` with the configured
//! API key (`Authorization: <key>`) and maps the quota limit rows to session,
//! weekly and MCP-monthly windows.

use anyhow::Result;
use chrono::{DateTime, Utc};
use serde_json::Value;

use super::helpers::{self, ESTAO_USER_AGENT};
use super::{build_snapshot, is_blank, parse_json, HttpClient, UsageProvider};
use crate::config::ProviderConfig;
use crate::models::{FetchRequest, ProviderAccount, RateWindow, UsageSnapshot};

const CREDIT_LIMIT_TYPE: &str = "CREDIT_LIMIT";
const TOKENS_LIMIT_TYPE: &str = "TOKENS_LIMIT";
const TIME_LIMIT_TYPE: &str = "TIME_LIMIT";

/// The Z.ai provider (API key based).
pub struct ZaiProvider {
    http_client: HttpClient,
}

impl ZaiProvider {
    /// Creates a provider using the supplied HTTP client.
    pub fn new(http_client: HttpClient) -> Self {
        Self { http_client }
    }
}

impl UsageProvider for ZaiProvider {
    fn id(&self) -> &'static str {
        "zai"
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
        let account = request
            .account
            .cloned()
            .or_else(|| accounts.first().cloned());
        let token = account
            .as_ref()
            .and_then(|account| account.secret.clone())
            .or_else(|| request.config.api_key.clone());

        if is_blank(token.as_deref()) {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "api",
                "Z.ai is not connected. Save your z.ai API key in Settings (API Keys page at z.ai).",
            ));
        }
        let token = token.unwrap_or_default();

        let response = self.http_client.get(
            "https://api.z.ai/api/monitor/usage/quota/limit",
            &[
                ("Authorization", token.as_str()),
                ("Accept", "application/json"),
                ("User-Agent", ESTAO_USER_AGENT),
            ],
        )?;

        if !response.is_success() {
            return Ok(UsageSnapshot::failure(
                self.id(),
                "api",
                &format!("Z.ai usage API returned {}.", response.status),
            ));
        }

        let root = parse_json(&response.body, "the Z.ai usage response")?;
        Ok(parse_quota_limits(
            &root,
            account.as_ref().map(|account| account.label.as_str()),
        ))
    }
}

/// Parses quota limits, mirroring `ZaiProvider.ParseQuotaLimits`.
pub(crate) fn parse_quota_limits(root: &Value, account_label: Option<&str>) -> UsageSnapshot {
    let data = match helpers::find_property(root, &["data"]) {
        Some(payload) if payload.is_object() => payload,
        _ => root,
    };

    let mut windows = Vec::new();
    if let Some(Value::Array(limits)) = helpers::find_property(data, &["limits"]) {
        for limit in limits {
            if let Some(window) = parse_limit(limit) {
                windows.push(window);
            }
        }
    }

    // The popover, tray and overlay render the first window as the primary
    // usage bar, so the rolling 5-hour window always comes first.
    windows.sort_by_key(|window| match window.id.as_str() {
        "session" => 0,
        "weekly" => 1,
        _ => 2,
    });

    if windows.is_empty() {
        return UsageSnapshot::failure("zai", "api", "Z.ai quota limits were not found.");
    }

    build_snapshot(
        "zai",
        "api",
        windows,
        account_label.map(str::to_string),
        helpers::first_string(data, &["level"]),
        None,
    )
}

fn parse_limit(limit: &Value) -> Option<RateWindow> {
    if !limit.is_object() {
        return None;
    }

    let limit_type = helpers::first_string(limit, &["type"]);
    let percent = helpers::first_number(limit, &["percentage"]);
    let percent_used = percent.map(|percent| (percent / 100.0).clamp(0.0, 1.0));
    let reset = reset_from(limit);

    match limit_type.as_deref() {
        // Quota rows are identified by unit/number: unit=3, number=5 is the
        // rolling 5-hour window and unit=6, number=1 the weekly one. The
        // current API reports CREDIT_LIMIT rows with used/total credits;
        // TOKENS_LIMIT is kept for older responses.
        Some(CREDIT_LIMIT_TYPE | TOKENS_LIMIT_TYPE) => {
            Some(quota_window(limit, percent_used, reset))
        }
        Some(TIME_LIMIT_TYPE) => Some(RateWindow {
            id: "mcp-monthly".to_string(),
            title: "MCP (1 Month)".to_string(),
            percent_used,
            reset_at: reset,
            used: None,
            limit: None,
            unit: None,
        }),
        _ => None,
    }
}

fn quota_window(
    limit: &Value,
    percent_used: Option<f64>,
    reset: Option<DateTime<Utc>>,
) -> RateWindow {
    let used = helpers::first_number(limit, &["currentValue"]);
    let total = helpers::first_number(limit, &["usage"]);
    let unit = helpers::first_string(limit, &["unitName"]).unwrap_or_else(|| "credits".to_string());

    let unit_value = helpers::first_number(limit, &["unit"]);
    let number = helpers::first_number(limit, &["number"]);
    let is_weekly =
        matches!((unit_value, number), (Some(unit), Some(number)) if unit == 6.0 && number == 1.0);

    if is_weekly {
        RateWindow {
            id: "weekly".to_string(),
            title: "Weekly".to_string(),
            percent_used,
            reset_at: reset,
            used,
            limit: total,
            unit: Some(unit),
        }
    } else {
        RateWindow {
            id: "session".to_string(),
            title: "5h".to_string(),
            percent_used,
            reset_at: reset,
            used,
            limit: total,
            unit: Some(unit),
        }
    }
}

fn reset_from(limit: &Value) -> Option<DateTime<Utc>> {
    let timestamp = helpers::first_number(limit, &["nextResetTime"])?;
    if !timestamp.is_finite() {
        return None;
    }

    if timestamp > 100_000_000_000.0 {
        DateTime::from_timestamp_millis(timestamp as i64)
    } else {
        DateTime::from_timestamp(timestamp as i64, 0)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from ZaiProviderTests.parses_credit_limits_from_the_current_api_shape.
    #[test]
    fn parses_credit_limits_from_the_current_api_shape() {
        let root: Value = serde_json::from_str(
            r#"
            {
              "code": 200,
              "msg": "Operation successful",
              "success": true,
              "data": {
                "level": "lite",
                "limits": [
                  { "type": "CREDIT_LIMIT", "unit": 3, "number": 5, "usage": 2000, "currentValue": 163, "remaining": 1836, "percentage": 8, "nextResetTime": 1788178718479 },
                  { "type": "CREDIT_LIMIT", "unit": 6, "number": 1, "usage": 10000, "currentValue": 163, "remaining": 9836, "percentage": 1, "nextResetTime": 1788765392998 }
                ]
              }
            }
            "#,
        )
        .unwrap();

        let snapshot = parse_quota_limits(&root, Some("api-key"));

        assert_eq!(snapshot.plan.as_deref(), Some("lite"));
        assert_eq!(snapshot.windows.len(), 2);

        let session = snapshot
            .windows
            .iter()
            .find(|window| window.id == "session")
            .unwrap();
        assert_eq!(session.percent_used, Some(0.08));
        assert_eq!(session.used, Some(163.0));
        assert_eq!(session.limit, Some(2000.0));
        assert_eq!(session.unit.as_deref(), Some("credits"));

        let weekly = snapshot
            .windows
            .iter()
            .find(|window| window.id == "weekly")
            .unwrap();
        assert_eq!(weekly.percent_used, Some(0.01));
        assert_eq!(weekly.limit, Some(10000.0));
    }

    // Ported from ZaiProviderTests.parses_legacy_token_limits_into_five_hour_weekly_and_mcp_windows.
    #[test]
    fn parses_legacy_token_limits_into_five_hour_weekly_and_mcp_windows() {
        let root: Value = serde_json::from_str(
            r#"
            {
              "success": true,
              "data": {
                "level": "pro",
                "limits": [
                  { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 42, "nextResetTime": 1779459394000 },
                  { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 12.5, "nextResetTime": 1779826837000 },
                  { "type": "TIME_LIMIT", "percentage": 33, "currentValue": 33, "usage": 100 }
                ]
              }
            }
            "#,
        )
        .unwrap();

        let snapshot = parse_quota_limits(&root, Some("api-key"));

        assert_eq!(snapshot.plan.as_deref(), Some("pro"));
        assert_eq!(snapshot.windows.len(), 3);
        assert_eq!(snapshot.windows[0].id, "session");
        assert_eq!(
            snapshot
                .windows
                .iter()
                .find(|window| window.id == "session")
                .and_then(|window| window.percent_used),
            Some(0.42)
        );
        assert_eq!(
            snapshot
                .windows
                .iter()
                .find(|window| window.id == "weekly")
                .and_then(|window| window.percent_used),
            Some(0.125)
        );
        assert_eq!(
            snapshot
                .windows
                .iter()
                .find(|window| window.id == "mcp-monthly")
                .and_then(|window| window.percent_used),
            Some(0.33)
        );
        assert_eq!(
            snapshot
                .windows
                .iter()
                .find(|window| window.id == "session")
                .and_then(|window| window.reset_at),
            DateTime::from_timestamp_millis(1779459394000)
        );
        assert_eq!(
            snapshot
                .windows
                .iter()
                .find(|window| window.id == "mcp-monthly")
                .and_then(|window| window.reset_at),
            None
        );
    }

    // Ported from ZaiProviderTests.token_limits_without_unit_hints_default_to_the_five_hour_window.
    #[test]
    fn token_limits_without_unit_hints_default_to_the_five_hour_window() {
        let root: Value = serde_json::from_str(
            r#"
            {
              "success": true,
              "data": {
                "limits": [
                  { "type": "TOKENS_LIMIT", "percentage": 4 }
                ]
              }
            }
            "#,
        )
        .unwrap();

        let snapshot = parse_quota_limits(&root, None);

        assert_eq!(snapshot.windows.len(), 1);
        assert_eq!(snapshot.windows[0].id, "session");
        assert_eq!(snapshot.windows[0].percent_used, Some(0.04));
    }

    // Ported from ZaiProviderTests.orders_session_before_weekly_windows.
    #[test]
    fn orders_session_before_weekly_windows() {
        let root: Value = serde_json::from_str(
            r#"
            {
              "success": true,
              "data": {
                "limits": [
                  { "type": "TIME_LIMIT", "percentage": 1 },
                  { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 2 },
                  { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 3 }
                ]
              }
            }
            "#,
        )
        .unwrap();

        let snapshot = parse_quota_limits(&root, None);

        let ids: Vec<&str> = snapshot
            .windows
            .iter()
            .map(|window| window.id.as_str())
            .collect();
        assert_eq!(ids, ["session", "weekly", "mcp-monthly"]);
    }

    // Ported from ZaiProviderTests.missing_quota_limits_returns_failure.
    #[test]
    fn missing_quota_limits_returns_failure() {
        let root: Value = serde_json::from_str(r#"{ "success": true, "data": {} }"#).unwrap();

        let snapshot = parse_quota_limits(&root, None);

        assert!(snapshot.error.is_some());
        assert!(snapshot.windows.is_empty());
    }
}
