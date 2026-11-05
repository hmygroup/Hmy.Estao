//! Usage history persistence, ported from `Configuration/UsageHistoryStore.cs`.
//!
//! The file lives beside the config file as `usage-history.json` and keeps the
//! same JSON shape the .NET app writes, so an existing install's history keeps
//! working:
//!
//! ```json
//! {
//!   "version": 1,
//!   "points": [
//!     { "provider": "codex", "window": "session", "timestamp": "2026-08-10T04:00:00+00:00", "percentUsed": 0.18 }
//!   ]
//! }
//! ```

use std::collections::HashMap;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use chrono::{DateTime, TimeDelta, Utc};
use serde::{Deserialize, Serialize};

use crate::catalog;
use crate::models::{UsageHistoryPoint, UsageSnapshot};

/// Schema version written to the history file.
const VERSION: i32 = 1;

/// How long samples are kept.
const RETENTION_DAYS: i64 = 30;

/// Maximum number of points kept per provider/window series.
const MAX_POINTS_PER_SERIES: usize = 500;

/// Two samples for the same series closer than this are collapsed into one.
const MIN_SAMPLE_GAP_SECONDS: i64 = 30;

/// The history file name, created beside the config file.
const FILE_NAME: &str = "usage-history.json";

/// Stores usage samples locally beside Estao's configuration. The provider
/// APIs are only used to create a new sample; the chart reads the persisted
/// samples.
pub struct UsageHistoryStore {
    path: PathBuf,
    gate: Mutex<()>,
}

impl UsageHistoryStore {
    /// Creates a store beside the given config file path.
    pub fn new(config_path: impl AsRef<Path>) -> Self {
        let config_path = config_path.as_ref();
        let directory = config_path
            .parent()
            .filter(|directory| !directory.as_os_str().is_empty())
            .map(Path::to_path_buf)
            .unwrap_or_else(|| env::current_dir().unwrap_or_else(|_| PathBuf::from(".")));

        Self {
            path: directory.join(FILE_NAME),
            gate: Mutex::new(()),
        }
    }

    /// The history file this store reads and writes.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Reads every persisted sample. A missing file yields an empty list; a
    /// malformed file is an error, exactly like the .NET `ReadCoreAsync`.
    pub fn load(&self) -> Result<Vec<UsageHistoryPoint>> {
        let _guard = self.lock();
        Self::read_core(&self.path)
    }

    /// Appends one point per successful snapshot window, trims old samples and
    /// returns the resulting history.
    pub fn append(
        &self,
        snapshots: &[UsageSnapshot],
        timestamp: Option<DateTime<Utc>>,
    ) -> Result<Vec<UsageHistoryPoint>> {
        let _guard = self.lock();
        let mut points = Self::read_core(&self.path)?;
        let sample_time = timestamp.unwrap_or_else(Utc::now);

        for snapshot in snapshots {
            if snapshot.error.is_some() {
                continue;
            }

            let provider = catalog::normalize_id(&snapshot.provider);
            for window in &snapshot.windows {
                let Some(mut percent) = window.percent_used else {
                    continue;
                };
                if !percent.is_finite() {
                    continue;
                }

                percent = percent.clamp(0.0, 1.0);
                let previous = points.iter().rposition(|point| {
                    point.provider.eq_ignore_ascii_case(&provider)
                        && point.window.eq_ignore_ascii_case(&window.id)
                });
                if let Some(index) = previous {
                    if sample_time - points[index].timestamp
                        < TimeDelta::seconds(MIN_SAMPLE_GAP_SECONDS)
                    {
                        points.remove(index);
                    }
                }

                points.push(UsageHistoryPoint {
                    provider: provider.clone(),
                    window: window.id.clone(),
                    timestamp: sample_time,
                    percent_used: percent,
                });
            }
        }

        let cutoff = sample_time - TimeDelta::days(RETENTION_DAYS);
        let mut groups: Vec<Vec<UsageHistoryPoint>> = Vec::new();
        let mut group_indexes: HashMap<String, usize> = HashMap::new();
        for point in points.into_iter().filter(|point| point.timestamp >= cutoff) {
            let key = format!(
                "{}\u{1f}{}",
                point.provider.to_lowercase(),
                point.window.to_lowercase()
            );
            let index = *group_indexes.entry(key).or_insert_with(|| {
                groups.push(Vec::new());
                groups.len() - 1
            });
            groups[index].push(point);
        }

        let mut trimmed: Vec<UsageHistoryPoint> = Vec::new();
        for mut series in groups {
            // LINQ's OrderBy is stable; sort_by_key preserves that contract.
            series.sort_by_key(|point| point.timestamp);
            if series.len() > MAX_POINTS_PER_SERIES {
                series.drain(0..series.len() - MAX_POINTS_PER_SERIES);
            }
            trimmed.extend(series);
        }
        trimmed.sort_by_key(|point| point.timestamp);

        Self::write_core(&self.path, &trimmed)?;
        Ok(trimmed)
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, ()> {
        self.gate
            .lock()
            .unwrap_or_else(|poison| poison.into_inner())
    }

    fn read_core(path: &Path) -> Result<Vec<UsageHistoryPoint>> {
        if !path.exists() {
            return Ok(Vec::new());
        }

        let text = fs::read_to_string(path)
            .with_context(|| format!("Failed to read history file '{}'.", path.display()))?;
        let document: HistoryDocument = serde_json::from_str(&text)
            .with_context(|| format!("Invalid history JSON in '{}'.", path.display()))?;
        Ok(document.points)
    }

    fn write_core(path: &Path, points: &[UsageHistoryPoint]) -> Result<()> {
        if let Some(directory) = path.parent() {
            if !directory.as_os_str().is_empty() {
                fs::create_dir_all(directory).with_context(|| {
                    format!(
                        "Failed to create history directory '{}'.",
                        directory.display()
                    )
                })?;
            }
        }

        let document = HistoryDocument {
            version: VERSION,
            points: points.to_vec(),
        };
        let json =
            serde_json::to_string_pretty(&document).context("Failed to serialize the history.")?;
        write_atomically(path, &json)
    }
}

/// The on-disk document. Field names and order match the .NET serializer.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct HistoryDocument {
    #[serde(default = "default_version")]
    version: i32,
    #[serde(default)]
    points: Vec<UsageHistoryPoint>,
}

fn default_version() -> i32 {
    VERSION
}

/// Writes `contents` through a temporary sibling file, then renames it over
/// `path`, mirroring `WriteCoreAsync`.
fn write_atomically(path: &Path, contents: &str) -> Result<()> {
    let temporary = temporary_path(path);
    let result = fs::write(&temporary, contents)
        .and_then(|_| fs::rename(&temporary, path))
        .with_context(|| format!("Failed to write history file '{}'.", path.display()));
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

fn temporary_path(path: &Path) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);

    let counter = COUNTER.fetch_add(1, Ordering::Relaxed);
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or(0);
    let mut name = path.as_os_str().to_os_string();
    name.push(format!(".{nanos:032x}{counter:016x}.tmp"));
    PathBuf::from(name)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::RateWindow;

    // Ported from UsageHistoryStoreTests.AppendPersistsUsageSamplesLocally.
    #[test]
    fn append_persists_usage_samples_locally() {
        let directory = env::temp_dir().join(format!(
            "estao-history-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map(|duration| duration.as_nanos())
                .unwrap_or(0)
        ));
        fs::create_dir_all(&directory).unwrap();
        let result = std::panic::catch_unwind(|| {
            let store = UsageHistoryStore::new(directory.join("config.json"));
            let timestamp = Utc::now();
            let snapshot = UsageSnapshot {
                provider: "codex".to_string(),
                display_name: "Codex".to_string(),
                source: "test".to_string(),
                updated_at: timestamp,
                windows: vec![RateWindow {
                    id: "session".to_string(),
                    title: "Session".to_string(),
                    percent_used: Some(0.25),
                    reset_at: Some(timestamp + TimeDelta::hours(2)),
                    used: None,
                    limit: None,
                    unit: None,
                }],
                account: None,
                plan: None,
                credits: None,
                error: None,
                is_stale: false,
            };

            let written = store.append(&[snapshot], Some(timestamp)).unwrap();
            let loaded = store.load().unwrap();

            assert_eq!(loaded.len(), 1);
            let point = &loaded[0];
            assert_eq!(point.provider, "codex");
            assert_eq!(point.window, "session");
            assert_eq!(point.percent_used, 0.25);
            assert_eq!(written.len(), loaded.len());
            assert!(store.path().exists());
        });
        let _ = fs::remove_dir_all(&directory);
        result.unwrap();
    }

    // The JSON shape below is what `System.Text.Json` (web defaults, indented)
    // writes for the same document, so both apps can share one history file.
    #[test]
    fn serializes_the_dotnet_history_shape() {
        let document = HistoryDocument {
            version: 1,
            points: vec![UsageHistoryPoint {
                provider: "codex".to_string(),
                window: "session".to_string(),
                timestamp: DateTime::parse_from_rfc3339("2026-08-10T04:00:00Z")
                    .unwrap()
                    .with_timezone(&Utc),
                percent_used: 0.18,
            }],
        };

        let json = serde_json::to_string_pretty(&document).unwrap();
        assert_eq!(
            json,
            "{\n  \"version\": 1,\n  \"points\": [\n    {\n      \"provider\": \"codex\",\n      \"window\": \"session\",\n      \"timestamp\": \"2026-08-10T04:00:00+00:00\",\n      \"percentUsed\": 0.18\n    }\n  ]\n}"
        );
    }

    // Reads the sample that ships with the .NET app (timestamps written with
    // a trailing `Z`), proving old history files keep loading.
    #[test]
    fn reads_the_dotnet_sample_history() {
        let text = include_str!("../../../assets/usage-history.sample.json");
        let document: HistoryDocument = serde_json::from_str(text).unwrap();

        assert_eq!(document.version, 1);
        assert_eq!(document.points.len(), 14);
        assert_eq!(document.points[0].provider, "codex");
        assert_eq!(document.points[0].window, "session");
        assert_eq!(document.points[0].percent_used, 0.18);
    }
}
