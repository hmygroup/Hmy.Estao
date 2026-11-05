//! Usage models, ported from `Models/UsageModels.cs`.
//!
//! JSON field names and timestamp formatting match the original .NET
//! implementation (`System.Text.Json` web defaults), so `estao usage --format
//! json` keeps producing the same payload.

use chrono::{DateTime, Utc};
use serde::{ser::SerializeStruct, Deserialize, Serialize, Serializer};

use crate::catalog;
use crate::config::{ProviderConfig, ProviderSource};

/// A rate-limit window (session, weekly, monthly, ...) reported by a provider.
#[derive(Debug, Clone, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RateWindow {
    /// Stable window id, e.g. `session`.
    pub id: String,
    /// User-facing window title.
    pub title: String,
    /// Fraction of the window already consumed (`0.0` - `1.0`).
    #[serde(default)]
    pub percent_used: Option<f64>,
    /// When the window resets.
    #[serde(default, deserialize_with = "datetime_utc::deserialize_option")]
    pub reset_at: Option<DateTime<Utc>>,
    /// Absolute amount used, when the provider reports it.
    #[serde(default)]
    pub used: Option<f64>,
    /// Absolute limit, when the provider reports it.
    #[serde(default)]
    pub limit: Option<f64>,
    /// Unit of `used`/`limit`, e.g. `requests`.
    #[serde(default)]
    pub unit: Option<String>,
}

impl RateWindow {
    /// Fraction of the window still available, clamped to `0.0` - `1.0`.
    pub fn percent_remaining(&self) -> Option<f64> {
        self.percent_used.map(|used| (1.0 - used).clamp(0.0, 1.0))
    }
}

impl Serialize for RateWindow {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        // `System.Text.Json` also serializes the computed `PercentRemaining`
        // property, so it is written explicitly here.
        let mut state = serializer.serialize_struct("RateWindow", 8)?;
        state.serialize_field("id", &self.id)?;
        state.serialize_field("title", &self.title)?;
        state.serialize_field("percentUsed", &self.percent_used)?;
        state.serialize_field("resetAt", &OptionalDotNetDateTime(self.reset_at))?;
        state.serialize_field("used", &self.used)?;
        state.serialize_field("limit", &self.limit)?;
        state.serialize_field("unit", &self.unit)?;
        state.serialize_field("percentRemaining", &self.percent_remaining())?;
        state.end()
    }
}

/// Credits (or prepaid balance) reported by a provider.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreditsSnapshot {
    /// Remaining balance, when reported.
    #[serde(default)]
    pub balance: Option<f64>,
    /// Unit of the balance, e.g. `USD`.
    #[serde(default)]
    pub unit: Option<String>,
    /// Whether the plan is an unlimited one.
    #[serde(default)]
    pub unlimited: Option<bool>,
}

/// A single provider usage snapshot.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UsageSnapshot {
    /// Normalized provider id.
    pub provider: String,
    /// User-facing provider name.
    pub display_name: String,
    /// Transport used to fetch the usage, e.g. `oauth`.
    pub source: String,
    /// When the snapshot was fetched.
    #[serde(with = "datetime_utc")]
    pub updated_at: DateTime<Utc>,
    /// Rate-limit windows reported by the provider.
    #[serde(default)]
    pub windows: Vec<RateWindow>,
    /// Account the snapshot belongs to, when known.
    #[serde(default)]
    pub account: Option<String>,
    /// Plan name, when known.
    #[serde(default)]
    pub plan: Option<String>,
    /// Credits snapshot, when the provider reports one.
    #[serde(default)]
    pub credits: Option<CreditsSnapshot>,
    /// Error message when the fetch failed.
    #[serde(default)]
    pub error: Option<String>,
    /// Whether the snapshot comes from history because a fresh fetch failed.
    #[serde(default)]
    pub is_stale: bool,
}

impl UsageSnapshot {
    /// Builds a failed snapshot for `provider`, using the catalog display name.
    pub fn failure(provider: &str, source: &str, error: &str) -> Self {
        Self {
            provider: provider.to_string(),
            display_name: catalog::display_name(provider),
            source: source.to_string(),
            updated_at: Utc::now(),
            windows: Vec::new(),
            account: None,
            plan: None,
            credits: None,
            error: Some(error.to_string()),
            is_stale: false,
        }
    }
}

/// A locally persisted usage sample, used to draw the usage chart when a
/// provider is offline.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UsageHistoryPoint {
    /// Normalized provider id.
    pub provider: String,
    /// Window id the sample belongs to.
    pub window: String,
    /// When the sample was taken.
    #[serde(with = "datetime_utc")]
    pub timestamp: DateTime<Utc>,
    /// Fraction of the window consumed (`0.0` - `1.0`).
    pub percent_used: f64,
}

/// An account (or token) attached to a provider config.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderAccount {
    /// Stable account id.
    pub id: String,
    /// User-facing account label.
    pub label: String,
    /// Secret token, when the account uses one.
    pub secret: Option<String>,
    /// Home directory override (e.g. a Codex profile), when used.
    pub home_path: Option<String>,
    /// Workspace id, when the provider is workspace scoped.
    pub workspace_id: Option<String>,
}

/// A provider fetch request. The .NET `CancellationToken` has no Rust
/// counterpart and is intentionally omitted.
#[derive(Debug, Clone, Copy)]
pub struct FetchRequest<'a> {
    /// Provider configuration to fetch with.
    pub config: &'a ProviderConfig,
    /// Account to fetch usage for, if any.
    pub account: Option<&'a ProviderAccount>,
    /// Transport requested by the caller.
    pub source: ProviderSource,
}

/// Serializes a `DateTime<Utc>` like `System.Text.Json` writes a
/// `DateTimeOffset`: `2026-07-20T13:00:00+00:00`.
struct DotNetDateTime(DateTime<Utc>);

/// Serializes an optional `DateTime<Utc>` using [`DotNetDateTime`] rules.
struct OptionalDotNetDateTime(Option<DateTime<Utc>>);

impl Serialize for DotNetDateTime {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_str(&format_dotnet_datetime(self.0))
    }
}

impl Serialize for OptionalDotNetDateTime {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        match self.0 {
            Some(value) => DotNetDateTime(value).serialize(serializer),
            None => serializer.serialize_none(),
        }
    }
}

fn format_dotnet_datetime(value: DateTime<Utc>) -> String {
    let mut text = value.format("%Y-%m-%dT%H:%M:%S").to_string();
    let nanos = value.timestamp_subsec_nanos();
    if nanos > 0 {
        // `DateTimeOffset` has 100 ns (tick) resolution, so keep at most seven
        // fractional digits and drop trailing zeros, like .NET does.
        let mut fraction = format!("{nanos:09}");
        fraction.truncate(7);
        while fraction.ends_with('0') {
            fraction.pop();
        }
        if !fraction.is_empty() {
            text.push('.');
            text.push_str(&fraction);
        }
    }
    text.push_str("+00:00");
    text
}

mod datetime_utc {
    use chrono::{DateTime, Utc};
    use serde::{de, Deserialize, Deserializer, Serialize, Serializer};

    use super::DotNetDateTime;

    pub fn serialize<S: Serializer>(
        value: &DateTime<Utc>,
        serializer: S,
    ) -> Result<S::Ok, S::Error> {
        DotNetDateTime(*value).serialize(serializer)
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(
        deserializer: D,
    ) -> Result<DateTime<Utc>, D::Error> {
        let text = String::deserialize(deserializer)?;
        parse(&text).map_err(de::Error::custom)
    }

    pub fn deserialize_option<'de, D: Deserializer<'de>>(
        deserializer: D,
    ) -> Result<Option<DateTime<Utc>>, D::Error> {
        match Option::<String>::deserialize(deserializer)? {
            Some(text) => parse(&text).map(Some).map_err(de::Error::custom),
            None => Ok(None),
        }
    }

    fn parse(text: &str) -> Result<DateTime<Utc>, chrono::ParseError> {
        DateTime::parse_from_rfc3339(text).map(|value| value.with_timezone(&Utc))
    }
}
