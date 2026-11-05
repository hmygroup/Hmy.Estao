//! Shared provider helpers, ported from `ProviderHelpers.cs`.
//!
//! The JSON heuristics (`first_string`, `reset_from`, `percent_used_from`,
//! `find_property`) are intentionally lenient and mirror the .NET behavior
//! exactly: the providers accept several API response shapes, and these
//! functions are what make that work.

use std::io::{Read, Write};
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::sync::LazyLock;

use anyhow::{anyhow, bail, Context, Result};
use chrono::{DateTime, Duration, Local, NaiveDate, NaiveDateTime, Utc};
use regex::Regex;
use serde_json::Value;

use crate::config::{CookieSource, ProviderConfig, ProviderSource};
use crate::models::ProviderAccount;
use crate::paths;

/// The `User-Agent` Estao sends to the Codex and Z.ai APIs.
pub const ESTAO_USER_AGENT: &str = "Estao/0.1";

/// Parses a configured provider source, mirroring `ParseSource`.
pub fn parse_source(source: Option<&str>) -> ProviderSource {
    ProviderSource::parse(source)
}

/// Parses a configured cookie source, mirroring `ParseCookieSource`.
pub fn parse_cookie_source(source: Option<&str>) -> CookieSource {
    CookieSource::parse(source)
}

/// Ports `ProviderHelpers.TokenAccounts`: the configured token accounts with
/// stable ids and labels.
pub fn token_accounts(config: &ProviderConfig) -> Vec<ProviderAccount> {
    let Some(token_accounts) = config.token_accounts.as_ref() else {
        return Vec::new();
    };

    token_accounts
        .accounts
        .iter()
        .enumerate()
        .map(|(index, account)| ProviderAccount {
            id: account
                .id
                .as_deref()
                .filter(|id| !id.trim().is_empty())
                .map(str::to_string)
                .unwrap_or_else(|| index.to_string()),
            label: account
                .label
                .as_deref()
                .filter(|label| !label.trim().is_empty())
                .map(str::to_string)
                .unwrap_or_else(|| format!("Account {}", index + 1)),
            secret: account.token.clone(),
            home_path: None,
            workspace_id: account.workspace_id.clone(),
        })
        .collect()
}

/// Returns the first string property named in `names`, searching nested
/// objects and arrays like the .NET implementation.
pub fn first_string(element: &Value, names: &[&str]) -> Option<String> {
    match element {
        Value::Object(map) => {
            for name in names {
                if let Some(Value::String(value)) = map.get(*name) {
                    return Some(value.clone());
                }
            }

            for value in map.values() {
                if let Some(found) = first_string(value, names) {
                    if !found.trim().is_empty() {
                        return Some(found);
                    }
                }
            }

            None
        }
        Value::Array(items) => {
            for item in items {
                if let Some(found) = first_string(item, names) {
                    if !found.trim().is_empty() {
                        return Some(found);
                    }
                }
            }

            None
        }
        _ => None,
    }
}

/// Returns the first numeric property named in `names` on an object.
pub fn first_number(element: &Value, names: &[&str]) -> Option<f64> {
    let map = element.as_object()?;
    for name in names {
        if let Some(property) = map.get(*name) {
            if let Some(number) = try_get_number(property) {
                return Some(number);
            }
        }
    }

    None
}

/// Reads a number from a JSON number or a numeric string.
pub fn try_get_number(element: &Value) -> Option<f64> {
    match element {
        Value::Number(number) => number.as_f64(),
        Value::String(text) => text.trim().parse::<f64>().ok(),
        _ => None,
    }
}

/// Computes a reset timestamp, mirroring `ProviderHelpers.ResetFrom`.
pub fn reset_from(element: &Value) -> Option<DateTime<Utc>> {
    let text = first_string(
        element,
        &[
            "resetAt",
            "reset_at",
            "resetsAt",
            "resets_at",
            "resetDate",
            "reset_date",
            "quotaResetDate",
            "quota_reset_date",
            "endTime",
            "end_time",
        ],
    );

    if let Some(parsed) = text.as_deref().and_then(parse_datetime) {
        return Some(parsed);
    }

    if let Some(timestamp) = text
        .as_deref()
        .and_then(|text| text.trim().parse::<f64>().ok())
    {
        if let Some(parsed) = from_unix_timestamp(timestamp) {
            return Some(parsed);
        }
    }

    let timestamp = first_number(
        element,
        &[
            "resetAt",
            "reset_at",
            "resetsAt",
            "resets_at",
            "resetTimestamp",
            "reset_timestamp",
        ],
    );
    if let Some(parsed) = timestamp.and_then(from_unix_timestamp) {
        return Some(parsed);
    }

    let seconds = first_number(
        element,
        &[
            "resetInSec",
            "reset_in_seconds",
            "resetSeconds",
            "reset_after_seconds",
            "secondsUntilReset",
        ],
    );
    seconds.map(|seconds| Utc::now() + Duration::milliseconds((seconds.max(0.0) * 1000.0) as i64))
}

/// Computes the used fraction of a window, mirroring
/// `ProviderHelpers.PercentUsedFrom`.
pub fn percent_used_from(element: &Value, percent_remaining: bool) -> Option<f64> {
    // WHAM names these fields with an explicit "_percent" suffix and returns
    // 1 for one percent. Do not treat that value as the normalized fraction
    // 1.0 (which would incorrectly display 100%).
    let explicit_percent =
        first_number(element, &["usage_percent", "used_percent", "percent_used"]);
    if let Some(value) = explicit_percent {
        return normalize_whole_percent(value);
    }

    let direct = first_number(element, &["percentUsed", "usedPercent", "usagePercent"]);
    if let Some(value) = direct {
        return normalize_percent(value);
    }

    let explicit_remaining_percent =
        first_number(element, &["percent_remaining", "remaining_percent"]);
    if let Some(value) = explicit_remaining_percent {
        return normalize_whole_percent(value).map(|normalized| 1.0 - normalized);
    }

    let remaining = first_number(element, &["percentRemaining", "remainingPercent"]);
    if let Some(value) = remaining {
        return normalize_percent(value).map(|normalized| 1.0 - normalized);
    }

    let used = first_number(element, &["used", "current", "consumed"]);
    let limit = first_number(element, &["limit", "total", "maximum"]);
    if let (Some(used), Some(limit)) = (used, limit) {
        if limit > 0.0 {
            return Some((used / limit).clamp(0.0, 1.0));
        }
    }

    if percent_remaining {
        Some(1.0)
    } else {
        None
    }
}

/// Normalizes a value that may already be a fraction or a percentage.
pub fn normalize_percent(value: f64) -> Option<f64> {
    if !value.is_finite() {
        return None;
    }

    Some(if value > 1.0 { value / 100.0 } else { value }.clamp(0.0, 1.0))
}

fn normalize_whole_percent(value: f64) -> Option<f64> {
    if !value.is_finite() {
        return None;
    }

    Some((value / 100.0).clamp(0.0, 1.0))
}

/// Finds the first property named in `names`, recursively, like
/// `ProviderHelpers.FindProperty`.
pub fn find_property<'a>(element: &'a Value, names: &[&str]) -> Option<&'a Value> {
    match element {
        Value::Object(map) => {
            for name in names {
                if let Some(property) = map.get(*name) {
                    return Some(property);
                }
            }

            for value in map.values() {
                if let Some(found) = find_property(value, names) {
                    return Some(found);
                }
            }

            None
        }
        Value::Array(items) => {
            for item in items {
                if let Some(found) = find_property(item, names) {
                    return Some(found);
                }
            }

            None
        }
        _ => None,
    }
}

/// Removes a leading `Cookie:` prefix and trims the header value.
pub fn normalize_cookie_header(cookie: &str) -> String {
    let trimmed = cookie.trim();
    if trimmed.len() >= 7 && trimmed[..7].eq_ignore_ascii_case("Cookie:") {
        trimmed[7..].trim().to_string()
    } else {
        trimmed.to_string()
    }
}

/// The current user's home directory.
pub fn user_home() -> PathBuf {
    paths::user_home()
}

/// Expands `~` and `%VAR%` references in a path.
pub fn expand_home(path: &str) -> String {
    paths::expand_home(path)
}

/// The singleline regex used to extract OpenCode usage windows.
pub fn opencode_window_regex() -> &'static Regex {
    static REGEX: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(r#"(?s)"(?P<name>rollingUsage|weeklyUsage)"\s*:\s*\{(?P<body>.*?)\}"#)
            .expect("the OpenCode window regex is valid")
    });
    &REGEX
}

/// Runs a child process, ported from `ProviderHelpers.RunProcessAsync`.
///
/// The stdin timing is deliberate: the first JSON-RPC line is written, then
/// the helper waits one second so `codex app-server` can complete its
/// handshake before the remaining lines are sent. After all lines are written
/// it waits another two seconds (unless the process already exited) before
/// closing stdin. `None` environment values remove the variable.
pub fn run_process(
    file_name: &str,
    arguments: &str,
    working_directory: Option<&str>,
    environment: &[(&str, Option<&str>)],
    stdin: Option<&str>,
) -> Result<String> {
    let mut command = Command::new(file_name);
    command
        .args(arguments.split_whitespace())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .stdin(if stdin.is_some() {
            Stdio::piped()
        } else {
            Stdio::inherit()
        });

    if let Some(directory) = working_directory {
        command.current_dir(directory);
    }

    for (key, value) in environment {
        match value {
            Some(value) => {
                command.env(key, value);
            }
            None => {
                command.env_remove(key);
            }
        }
    }

    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // CREATE_NO_WINDOW: never flash a console window from the GUI.
        command.creation_flags(0x0800_0000);
    }

    let mut child = command
        .spawn()
        .with_context(|| format!("Could not start {file_name}."))?;

    if let Some(stdin) = stdin {
        let lines: Vec<&str> = stdin.split('\n').filter(|line| !line.is_empty()).collect();
        let mut pipe = child
            .stdin
            .take()
            .ok_or_else(|| anyhow!("Could not start {file_name}."))?;

        for (index, line) in lines.iter().enumerate() {
            // .NET's `WriteLineAsync` uses `Environment.NewLine`, which is
            // CRLF on Windows.
            if cfg!(windows) {
                writeln!(pipe, "{line}\r")
            } else {
                writeln!(pipe, "{line}")
            }
            .with_context(|| format!("failed to write to {file_name} stdin"))?;
            pipe.flush()
                .with_context(|| format!("failed to flush {file_name} stdin"))?;

            // Codex app-server completes initialize asynchronously. Give it a
            // chance to emit the handshake before sending initialized/read calls.
            if index == 0 && lines.len() > 1 {
                std::thread::sleep(std::time::Duration::from_secs(1));
            }
        }

        let already_exited = child
            .try_wait()
            .with_context(|| format!("failed to query {file_name}"))?
            .is_some();
        if !already_exited {
            std::thread::sleep(std::time::Duration::from_secs(2));
        }

        // Dropping the pipe closes stdin, like `process.StandardInput.Close()`.
        drop(pipe);
    }

    let stdout = child
        .stdout
        .take()
        .ok_or_else(|| anyhow!("Could not start {file_name}."))?;
    let stderr = child
        .stderr
        .take()
        .ok_or_else(|| anyhow!("Could not start {file_name}."))?;

    // Read both pipes concurrently so a full stderr buffer cannot deadlock.
    let stdout_reader = std::thread::spawn(move || read_to_end(stdout));
    let stderr_reader = std::thread::spawn(move || read_to_end(stderr));

    let status = child
        .wait()
        .with_context(|| format!("failed to wait for {file_name}"))?;
    let output = stdout_reader
        .join()
        .map_err(|_| anyhow!("failed to read {file_name} stdout"))??;
    let error = stderr_reader
        .join()
        .map_err(|_| anyhow!("failed to read {file_name} stderr"))??;

    if !status.success() {
        let error = String::from_utf8_lossy(&error);
        if error.trim().is_empty() {
            bail!(
                "{file_name} exited with {}.",
                status.code().unwrap_or_default()
            );
        }

        bail!("{}", error.trim());
    }

    Ok(String::from_utf8_lossy(&output).into_owned())
}

fn read_to_end(mut pipe: impl Read) -> Result<Vec<u8>> {
    let mut buffer = Vec::new();
    pipe.read_to_end(&mut buffer)
        .context("failed to read child process output")?;
    Ok(buffer)
}

fn from_unix_timestamp(value: f64) -> Option<DateTime<Utc>> {
    if !value.is_finite() {
        return None;
    }

    if value > 100_000_000_000.0 {
        DateTime::from_timestamp_millis(value as i64)
    } else {
        DateTime::from_timestamp(value as i64, 0)
    }
}

/// Parses the date formats `DateTimeOffset.TryParse` accepts in practice.
fn parse_datetime(text: &str) -> Option<DateTime<Utc>> {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return None;
    }

    if let Ok(value) = DateTime::parse_from_rfc3339(trimmed) {
        return Some(value.with_timezone(&Utc));
    }

    for format in ["%Y-%m-%dT%H:%M:%S%.f", "%Y-%m-%d %H:%M:%S%.f"] {
        if let Ok(naive) = NaiveDateTime::parse_from_str(trimmed, format) {
            return naive
                .and_local_timezone(Local)
                .earliest()
                .map(|value| value.with_timezone(&Utc));
        }
    }

    for format in ["%Y-%m-%d", "%m/%d/%Y"] {
        if let Ok(date) = NaiveDate::parse_from_str(trimmed, format) {
            let naive = date.and_hms_opt(0, 0, 0)?;
            return naive
                .and_local_timezone(Local)
                .earliest()
                .map(|value| value.with_timezone(&Utc));
        }
    }

    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn first_string_searches_nested_objects_and_arrays() {
        let value = json!({ "result": { "items": [{ "email": "a@example.com" }] } });
        assert_eq!(
            first_string(&value, &["email", "accountEmail"]),
            Some("a@example.com".to_string())
        );
    }

    #[test]
    fn percent_used_from_treats_suffixed_percent_fields_as_whole_percent() {
        assert_eq!(
            percent_used_from(&json!({ "used_percent": 1 }), false),
            Some(0.01)
        );
        assert_eq!(
            percent_used_from(&json!({ "percentUsed": 25 }), false),
            Some(0.25)
        );
        assert_eq!(
            percent_used_from(&json!({ "used": 1, "limit": 4 }), false),
            Some(0.25)
        );
        assert_eq!(percent_used_from(&json!({}), true), Some(1.0));
        assert_eq!(percent_used_from(&json!({}), false), None);
    }

    #[test]
    fn percent_remaining_falls_back_to_full_usage_without_usage() {
        assert_eq!(
            percent_used_from(&json!({ "percentRemaining": 25 }), false),
            Some(0.75)
        );
    }

    #[test]
    fn reset_from_handles_epoch_seconds_milliseconds_and_relative_seconds() {
        assert_eq!(
            reset_from(&json!({ "resetsAt": 1779459394 })),
            DateTime::from_timestamp(1779459394, 0)
        );
        assert_eq!(
            reset_from(&json!({ "reset_at": 1779459394000_i64 })),
            DateTime::from_timestamp_millis(1779459394000)
        );

        let relative = reset_from(&json!({ "resetInSec": 60 })).unwrap();
        assert!(
            (relative - (Utc::now() + Duration::seconds(60)))
                .num_seconds()
                .abs()
                <= 1
        );
    }

    #[test]
    fn find_property_recurses_into_arrays() {
        let value = json!({ "data": [{ "limits": [{ "type": "CREDIT_LIMIT" }] }] });
        let limits = find_property(&value, &["limits"]).unwrap();
        assert_eq!(limits[0]["type"], "CREDIT_LIMIT");
        assert_eq!(find_property(&value, &["missing"]), None);
    }

    #[test]
    fn token_accounts_fill_missing_ids_and_labels() {
        let config: ProviderConfig = serde_json::from_value(json!({
            "id": "zai",
            "tokenAccounts": {
                "accounts": [
                    { "token": "a" },
                    { "id": "b", "label": "Second", "token": "c", "workspaceID": "w" }
                ]
            }
        }))
        .unwrap();

        let accounts = token_accounts(&config);
        assert_eq!(accounts.len(), 2);
        assert_eq!(accounts[0].id, "0");
        assert_eq!(accounts[0].label, "Account 1");
        assert_eq!(accounts[1].id, "b");
        assert_eq!(accounts[1].label, "Second");
        assert_eq!(accounts[1].workspace_id.as_deref(), Some("w"));
    }
}
