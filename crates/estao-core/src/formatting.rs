//! Usage formatting (text + JSON), ported from `Formatting/UsageFormatter.cs`
//! and `Formatting/DurationFormatter.cs`.

use anyhow::{Context, Result};
use chrono::{DateTime, Local, TimeDelta, Utc};

use crate::models::UsageSnapshot;

/// Formats a duration using its most appropriate unit, e.g. `1m`, `7h 32m`
/// or `5d 19h`.
pub fn to_compact(duration: TimeDelta) -> String {
    if duration.num_days() >= 1 {
        return format!("{}d {}h", duration.num_days(), duration.num_hours() % 24);
    }

    if duration.num_hours() >= 1 {
        return format!("{}h {}m", duration.num_hours(), duration.num_minutes() % 60);
    }

    format!("{}m", (duration.num_minutes() % 60).max(1))
}

/// Renders snapshots as human-readable text, one block per provider.
pub fn to_text(snapshots: &[UsageSnapshot]) -> String {
    let mut builder = String::new();
    for snapshot in snapshots {
        if !builder.is_empty() {
            builder.push('\n');
        }

        push_line(
            &mut builder,
            &format!("== {} ({}) ==", snapshot.display_name, snapshot.source),
        );

        if let Some(error) = snapshot
            .error
            .as_deref()
            .filter(|error| !error.trim().is_empty())
        {
            push_line(&mut builder, &format!("Error: {error}"));
            continue;
        }

        for window in &snapshot.windows {
            let remaining = match window.percent_remaining() {
                Some(percent) => format!("{} left", format_percent(percent)),
                None => "unknown".to_string(),
            };
            push_line(&mut builder, &format!("{}: {remaining}", window.title));

            if let Some(reset_at) = window.reset_at {
                push_line(
                    &mut builder,
                    &format!("Resets: {}", format_local_short(reset_at)),
                );
            }
        }

        if let Some(balance) = snapshot
            .credits
            .as_ref()
            .and_then(|credits| credits.balance)
        {
            let unit = snapshot
                .credits
                .as_ref()
                .and_then(|credits| credits.unit.as_deref())
                .unwrap_or_default();
            let line = format!("Credits: {} {unit}", format_decimal_2(balance));
            push_line(&mut builder, line.trim_end());
        }

        if let Some(account) = non_blank(snapshot.account.as_deref()) {
            push_line(&mut builder, &format!("Account: {account}"));
        }

        if let Some(plan) = non_blank(snapshot.plan.as_deref()) {
            push_line(&mut builder, &format!("Plan: {plan}"));
        }
    }

    builder.trim_end().to_string()
}

/// Renders snapshots as a JSON array using the same field names as the .NET
/// CLI. `pretty` mirrors `--pretty` (indented output).
pub fn to_json(snapshots: &[UsageSnapshot], pretty: bool) -> Result<String> {
    let json = if pretty {
        serde_json::to_string_pretty(snapshots)
    } else {
        serde_json::to_string(snapshots)
    };
    json.context("failed to serialize usage snapshots")
}

fn push_line(builder: &mut String, line: &str) {
    builder.push_str(line);
    builder.push('\n');
}

fn non_blank(value: Option<&str>) -> Option<&str> {
    value.filter(|value| !value.trim().is_empty())
}

/// Mirrors .NET's `P0` format: a whole percentage with a `%` suffix.
fn format_percent(value: f64) -> String {
    format!("{:.0}%", value * 100.0)
}

/// Formats a `DateTime<Utc>` in local time using .NET's short date/time
/// pattern (`g`), e.g. `7/20/2026 3:00:00 PM`.
fn format_local_short(value: DateTime<Utc>) -> String {
    value
        .with_timezone(&Local)
        .format("%-m/%-d/%Y %-I:%M:%S %p")
        .to_string()
}

/// Mirrors .NET's `0.##` format: up to two decimals, trailing zeros trimmed.
fn format_decimal_2(value: f64) -> String {
    let rounded = (value * 100.0).round() / 100.0;
    let mut text = format!("{rounded:.2}");
    while text.ends_with('0') {
        text.pop();
    }
    if text.ends_with('.') {
        text.pop();
    }
    if text == "-0" {
        return "0".to_string();
    }
    text
}

#[cfg(test)]
mod tests {
    use chrono::DateTime;

    use super::*;
    use crate::models::RateWindow;

    fn snapshot_with_session() -> UsageSnapshot {
        UsageSnapshot {
            provider: "codex".to_string(),
            display_name: "Codex".to_string(),
            source: "oauth".to_string(),
            updated_at: Utc::now(),
            windows: vec![RateWindow {
                id: "session".to_string(),
                title: "Session".to_string(),
                percent_used: Some(0.25),
                reset_at: Some(
                    DateTime::parse_from_rfc3339("2026-07-20T13:00:00Z")
                        .unwrap()
                        .with_timezone(&Utc),
                ),
                used: None,
                limit: None,
                unit: None,
            }],
            account: Some("user@example.com".to_string()),
            plan: Some("Pro".to_string()),
            credits: None,
            error: None,
            is_stale: false,
        }
    }

    // Ported from UsageFormatterTests.text_output_includes_provider_window_and_account.
    #[test]
    fn text_output_includes_provider_window_and_account() {
        let text = to_text(&[snapshot_with_session()]);

        assert!(text.contains("== Codex (oauth) =="), "{text}");
        assert!(text.contains("Session: 75"), "{text}");
        assert!(text.contains("Account: user@example.com"), "{text}");
    }

    // Ported from DurationFormatterTests.compact_output_uses_the_most_appropriate_units.
    #[test]
    fn compact_output_uses_the_most_appropriate_units() {
        let cases = [
            (0, 0, 0, 1, "1m"),
            (0, 0, 32, 0, "32m"),
            (0, 7, 32, 0, "7h 32m"),
            (1, 0, 0, 0, "1d 0h"),
            (5, 7, 32, 0, "5d 7h"),
            (5, 19, 32, 0, "5d 19h"),
        ];

        for (days, hours, minutes, seconds, expected) in cases {
            let duration = TimeDelta::seconds(
                i64::from(days) * 86_400
                    + i64::from(hours) * 3_600
                    + i64::from(minutes) * 60
                    + i64::from(seconds),
            );
            assert_eq!(to_compact(duration), expected);
        }
    }

    // Guards the JSON contract of `estao usage --format json` (not in the
    // original suite): computed percentRemaining and camelCase field names.
    #[test]
    fn json_output_keeps_dotnet_field_names() {
        let json = to_json(&[snapshot_with_session()], false).unwrap();

        assert!(json.contains("\"displayName\":\"Codex\""), "{json}");
        assert!(json.contains("\"percentUsed\":0.25"), "{json}");
        assert!(json.contains("\"percentRemaining\":0.75"), "{json}");
        assert!(json.contains("\"isStale\":false"), "{json}");
        assert!(json.contains("\"updatedAt\":\""), "{json}");
        assert!(
            json.contains("\"resetAt\":\"2026-07-20T13:00:00+00:00\""),
            "{json}"
        );
    }
}
