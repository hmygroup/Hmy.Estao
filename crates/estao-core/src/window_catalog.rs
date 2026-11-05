//! Rate window catalog (display ranges), ported from
//! `Models/UsageWindowCatalog.cs`.
//!
//! Each rate-limit series is mapped against its own horizon so a five-hour
//! window does not get compressed into the final pixels of a weekly or monthly
//! chart.

use chrono::TimeDelta;

/// Horizon for five-hour ("session") windows.
pub const FIVE_HOURS: TimeDelta = TimeDelta::seconds(5 * 60 * 60);

/// Horizon for weekly windows.
pub const WEEKLY: TimeDelta = TimeDelta::seconds(7 * 24 * 60 * 60);

/// Horizon for monthly windows.
pub const MONTHLY: TimeDelta = TimeDelta::seconds(30 * 24 * 60 * 60);

/// Picks the chart horizon for a provider rate-limit window.
///
/// `window_id` is the raw window id, `title` its display title and `provider`
/// the normalized (or raw) provider id. Unknown windows fall back to weekly.
pub fn display_range(
    provider: Option<&str>,
    window_id: Option<&str>,
    title: Option<&str>,
) -> TimeDelta {
    let id = normalize(window_id);
    let name = normalize(title);
    let provider_id = normalize(provider);

    if contains_any(
        &id,
        &name,
        &["session", "primary", "fivehour", "5hour", "5h"],
    ) {
        return FIVE_HOURS;
    }

    if contains_any(
        &id,
        &name,
        &["weekly", "week", "sevenday", "7day", "secondary"],
    ) {
        return WEEKLY;
    }

    if provider_id == "copilot"
        || contains_any(&id, &name, &["monthly", "month", "premium", "chat"])
    {
        return MONTHLY;
    }

    WEEKLY
}

/// The short chart label for a horizon, e.g. `5h`, `7d` or `30d`.
pub fn display_label(range: TimeDelta) -> String {
    if range <= FIVE_HOURS {
        let hours = (range.num_seconds() as f64 / 3600.0).round().max(1.0) as i64;
        return format!("{hours}h");
    }

    let days = (range.num_seconds() as f64 / 86_400.0).round().max(1.0) as i64;
    format!("{days}d")
}

fn normalize(value: Option<&str>) -> String {
    value
        .unwrap_or_default()
        .chars()
        .filter(|character| character.is_alphanumeric())
        .collect::<String>()
        .to_lowercase()
}

fn contains_any(id: &str, title: &str, values: &[&str]) -> bool {
    values
        .iter()
        .any(|value| id.contains(value) || title.contains(value))
}

#[cfg(test)]
mod tests {
    use super::*;

    // Ported from UsageWindowCatalogTests.selects_the_display_horizon_for_each_rate_window.
    #[test]
    fn selects_the_display_horizon_for_each_rate_window() {
        let cases = [
            ("codex", "session", "Session", 5.0),
            ("claude", "five_hour", "5 hour", 5.0),
            ("codex", "weekly", "Weekly", 168.0),
            ("copilot", "premium_interactions", "Premium", 720.0),
            ("copilot", "chat", "Chat", 720.0),
        ];

        for (provider, window_id, title, expected_hours) in cases {
            let range = display_range(Some(provider), Some(window_id), Some(title));
            let hours = range.num_seconds() as f64 / 3600.0;
            assert_eq!(
                hours, expected_hours,
                "{provider}/{window_id} should use a {expected_hours}h horizon"
            );
        }
    }
}
