//! Pacing state, pacing/target calculators, exhaustion and trend projections.
//!
//! Ported from `Configuration/PacingStateStore.cs`, `Models/PacingModels.cs`,
//! `Models/UsageExhaustionCalculator.cs` and
//! `Models/UsageTrendCalculator.cs`.
//!
//! The calculators are pure math. The state store keeps the same
//! `pacing-state.json` shape the .NET app writes, so a single profile can be
//! shared between both implementations:
//!
//! ```json
//! {
//!   "version": 1,
//!   "entries": {
//!     "codex\u001fsession": "2026-11-05"
//!   }
//! }
//! ```

use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use chrono::{DateTime, NaiveDate, TimeDelta, Utc};
use serde::{Deserialize, Serialize};

// ---------------------------------------------------------------------------
// Pacing state store (`PacingStateStore.cs`)
// ---------------------------------------------------------------------------

/// The pacing state file name, created beside the config file.
const STATE_FILE_NAME: &str = "pacing-state.json";

/// Schema version written to the pacing state file.
const STATE_VERSION: i32 = 1;

/// Tiny piece of runtime bookkeeping (NOT user-editable settings, so it lives
/// outside the config store) that remembers the last day Estao already warned
/// about a provider/window going over its daily pacing target, so the tray
/// only nags once per day per window.
pub struct PacingStateStore {
    path: PathBuf,
    gate: Mutex<()>,
}

impl PacingStateStore {
    /// Creates a store beside the given config file path.
    pub fn new(config_path: impl AsRef<Path>) -> Self {
        let config_path = config_path.as_ref();
        let directory = config_path
            .parent()
            .filter(|directory| !directory.as_os_str().is_empty())
            .map(Path::to_path_buf)
            .unwrap_or_else(|| env::current_dir().unwrap_or_else(|_| PathBuf::from(".")));

        Self {
            path: directory.join(STATE_FILE_NAME),
            gate: Mutex::new(()),
        }
    }

    /// The state file this store reads and writes.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Returns `true` and records `today` the first time a given
    /// provider/window crosses its pacing target on a given day; returns
    /// `false` on subsequent calls for the same day so callers only notify
    /// once.
    ///
    /// Storage failures are reported as `false` (do not notify) by this
    /// convenience wrapper; use [`PacingStateStore::try_mark_notified_checked`]
    /// when the caller wants to surface them.
    pub fn try_mark_notified(&self, provider: &str, window: &str, today: NaiveDate) -> bool {
        self.try_mark_notified_checked(provider, window, today)
            .unwrap_or(false)
    }

    /// Fallible variant of [`PacingStateStore::try_mark_notified`], mirroring
    /// the exceptions the .NET method can raise on IO errors.
    pub fn try_mark_notified_checked(
        &self,
        provider: &str,
        window: &str,
        today: NaiveDate,
    ) -> Result<bool> {
        let _guard = self
            .gate
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());

        let mut state = Self::read_core(&self.path)?;
        let key = state_key(provider, window);
        if let Some(last_notified) = state.entries.get(&key) {
            if *last_notified == today {
                return Ok(false);
            }
        }

        state.entries.insert(key, today);
        Self::write_core(&self.path, &state)?;
        Ok(true)
    }

    fn read_core(path: &Path) -> Result<StateDocument> {
        if !path.exists() {
            return Ok(StateDocument::default());
        }

        let text = match fs::read_to_string(path) {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(StateDocument::default());
            }
            Err(error) => {
                return Err(error)
                    .with_context(|| format!("Failed to read pacing state '{}'.", path.display()));
            }
        };

        // A malformed local file must not break pacing notifications; the .NET
        // store catches `JsonException` and starts from an empty state.
        Ok(serde_json::from_str(&text).unwrap_or_default())
    }

    fn write_core(path: &Path, state: &StateDocument) -> Result<()> {
        if let Some(directory) = path.parent() {
            if !directory.as_os_str().is_empty() {
                fs::create_dir_all(directory).with_context(|| {
                    format!(
                        "Failed to create pacing state directory '{}'.",
                        directory.display()
                    )
                })?;
            }
        }

        let json =
            serde_json::to_string_pretty(state).context("Failed to serialize the pacing state.")?;
        write_atomically(path, &json)
    }
}

fn state_key(provider: &str, window: &str) -> String {
    format!("{provider}\u{1f}{window}").to_lowercase()
}

/// The on-disk pacing state document. Field names and order match the .NET
/// serializer.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct StateDocument {
    #[serde(default = "default_state_version")]
    version: i32,
    #[serde(default)]
    entries: std::collections::BTreeMap<String, NaiveDate>,
}

impl Default for StateDocument {
    fn default() -> Self {
        Self {
            version: STATE_VERSION,
            entries: std::collections::BTreeMap::new(),
        }
    }
}

fn default_state_version() -> i32 {
    STATE_VERSION
}

/// Writes `contents` through a temporary sibling file, then renames it over
/// `path`, mirroring `WriteCoreAsync`.
fn write_atomically(path: &Path, contents: &str) -> Result<()> {
    let temporary = temporary_path(path);
    let result = fs::write(&temporary, contents)
        .and_then(|_| fs::rename(&temporary, path))
        .with_context(|| format!("Failed to write pacing state '{}'.", path.display()));
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

// ---------------------------------------------------------------------------
// Pacing models (`PacingModels.cs`)
// ---------------------------------------------------------------------------

/// A single point on a pacing/target reference line. Shares the same shape as
/// the chart's own history points so the UI layer can map it 1:1.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PacingPoint {
    /// When the point applies.
    pub timestamp: DateTime<Utc>,
    /// Fraction of the window (`0.0` - `1.0`).
    pub value: f64,
}

impl PacingPoint {
    /// Builds a point from a timestamp and value.
    pub fn new(timestamp: DateTime<Utc>, value: f64) -> Self {
        Self { timestamp, value }
    }
}

/// Result of comparing a window's real usage against a daily pacing target.
#[derive(Debug, Clone, PartialEq)]
pub struct PacingResult {
    /// Reference line from the window start to now.
    pub target_line: Vec<PacingPoint>,
    /// Start of the current pacing window.
    pub window_start: DateTime<Utc>,
    /// Fraction of the window the target expects to be consumed by now.
    pub expected_percent_now: f64,
    /// Fraction of the window actually consumed now.
    pub actual_percent_now: f64,
    /// Whether actual usage is ahead of the target.
    pub is_over_pace: bool,
}

// ---------------------------------------------------------------------------
// Pacing calculator (`PacingModels.cs`)
// ---------------------------------------------------------------------------

/// A usage drop larger than this (in absolute percent-used terms) between two
/// consecutive samples is treated as the provider's window resetting, rather
/// than normal noise/measurement jitter.
const RESET_DROP_THRESHOLD: f64 = 0.05;

/// Computes a "budget pacing" reference line for a rate-limit window: how much
/// of the window's quota the user should have consumed by now if they spend it
/// at a steady `daily_target_percent` per day, starting either from the last
/// detected reset (a drop in the history) or from the beginning of the rate
/// limit's visible window.
pub struct PacingCalculator;

impl PacingCalculator {
    /// Computes the pacing result for `ordered_points`, or `None` when the
    /// target is unusable or the range is empty.
    pub fn compute(
        ordered_points: &[PacingPoint],
        daily_target_percent: f64,
        range_start: DateTime<Utc>,
        now: DateTime<Utc>,
    ) -> Option<PacingResult> {
        if daily_target_percent <= 0.0 || now <= range_start {
            return None;
        }

        let window_start = Self::detect_window_start(ordered_points, range_start);
        let daily_rate = daily_target_percent.clamp(0.0, 1000.0) / 100.0;
        let total_span = now - window_start;
        if total_span <= TimeDelta::zero() {
            return None;
        }

        let expected_now = (daily_rate * total_days(total_span)).clamp(0.0, 1.0);
        let line = Self::build_target_line_for_daily_rate(window_start, now, daily_rate);
        let actual_now = ordered_points
            .last()
            .map(|point| point.value)
            .unwrap_or(0.0);

        Some(PacingResult {
            target_line: line,
            window_start,
            expected_percent_now: expected_now,
            actual_percent_now: actual_now,
            is_over_pace: actual_now > expected_now,
        })
    }

    /// Builds the target line for a display range, always ending at
    /// `window_end`.
    pub fn build_target_line(
        window_start: DateTime<Utc>,
        window_end: DateTime<Utc>,
        daily_target_percent: f64,
    ) -> Vec<PacingPoint> {
        if daily_target_percent <= 0.0 || window_end <= window_start {
            return Vec::new();
        }

        let daily_rate = daily_target_percent.clamp(0.0, 1000.0) / 100.0;
        Self::build_target_line_for_daily_rate(window_start, window_end, daily_rate)
    }

    fn detect_window_start(
        ordered_points: &[PacingPoint],
        range_start: DateTime<Utc>,
    ) -> DateTime<Utc> {
        for index in (1..ordered_points.len()).rev() {
            let current = ordered_points[index];
            let previous = ordered_points[index - 1];
            if current.value + RESET_DROP_THRESHOLD < previous.value {
                return if current.timestamp < range_start {
                    range_start
                } else {
                    current.timestamp
                };
            }
        }

        range_start
    }

    fn build_target_line_for_daily_rate(
        window_start: DateTime<Utc>,
        window_end: DateTime<Utc>,
        daily_rate: f64,
    ) -> Vec<PacingPoint> {
        if daily_rate <= 0.0 {
            return vec![
                PacingPoint::new(window_start, 0.0),
                PacingPoint::new(window_end, 0.0),
            ];
        }

        // Days needed to reach 100% at the configured daily rate.
        let saturation_days = 1.0 / daily_rate;
        let saturation_at = window_start + duration_from_days(saturation_days);

        if saturation_at >= window_end {
            let expected_at_end =
                (daily_rate * total_days(window_end - window_start)).clamp(0.0, 1.0);
            return vec![
                PacingPoint::new(window_start, 0.0),
                PacingPoint::new(window_end, expected_at_end),
            ];
        }

        vec![
            PacingPoint::new(window_start, 0.0),
            PacingPoint::new(saturation_at, 1.0),
            PacingPoint::new(window_end, 1.0),
        ]
    }
}

// ---------------------------------------------------------------------------
// Usage exhaustion calculator (`UsageExhaustionCalculator.cs`)
// ---------------------------------------------------------------------------

/// Projects when a rate limit reaches 100% from its observed consumption
/// velocity. A detected reset starts a new trend; with sparse history, the
/// provider's reset boundary supplies the beginning of the current window.
#[derive(Debug, Clone, PartialEq)]
pub struct UsageExhaustionForecast {
    /// When the window is expected to reach 100%.
    pub estimated_at: DateTime<Utc>,
    /// The provider's next reset, when known.
    pub reset_at: Option<DateTime<Utc>>,
    /// Whether the reset happens before the projected exhaustion.
    pub reset_occurs_first: bool,
    /// Observed consumption, as a fraction of the window per day.
    pub daily_consumption_rate: f64,
}

/// Projects exhaustion from observed usage samples.
pub struct UsageExhaustionCalculator;

impl UsageExhaustionCalculator {
    /// Computes the forecast, or `None` when the data cannot support one.
    pub fn compute(
        points: &[PacingPoint],
        current_percent_used: Option<f64>,
        window_range: TimeDelta,
        now: DateTime<Utc>,
        reset_at: Option<DateTime<Utc>>,
    ) -> Option<UsageExhaustionForecast> {
        if window_range <= TimeDelta::zero() {
            return None;
        }

        let nominal_start = match reset_at {
            Some(reset) if reset > now => reset - window_range,
            _ => now - window_range,
        };
        let mut ordered: Vec<PacingPoint> = points
            .iter()
            .copied()
            .filter(|point| {
                point.timestamp >= nominal_start
                    && point.timestamp <= now
                    && point.value.is_finite()
            })
            .collect();
        ordered.sort_by_key(|point| point.timestamp);

        let mut window_start = nominal_start;
        for index in (1..ordered.len()).rev() {
            if ordered[index].value + RESET_DROP_THRESHOLD < ordered[index - 1].value {
                window_start = ordered[index].timestamp;
                break;
            }
        }

        let fallback_current = ordered.last().map(|point| point.value).unwrap_or(0.0);
        let current = current_percent_used
            .unwrap_or(fallback_current)
            .clamp(0.0, 1.0);
        if current <= 0.0 {
            return None;
        }

        if current >= 1.0 {
            return Some(UsageExhaustionForecast {
                estimated_at: now,
                reset_at,
                reset_occurs_first: false,
                daily_consumption_rate: f64::INFINITY,
            });
        }

        let trend: Vec<PacingPoint> = ordered
            .iter()
            .copied()
            .filter(|point| point.timestamp >= window_start)
            .collect();
        let mut baseline_at = window_start;
        let mut baseline_value = 0.0;
        if trend.len() >= 2 {
            baseline_at = trend[0].timestamp;
            baseline_value = trend[0].value.clamp(0.0, 1.0);
        }

        let mut elapsed_days = total_days(now - baseline_at);
        let mut consumed = current - baseline_value;
        if elapsed_days <= 0.0 || consumed <= 0.0 {
            // Providers such as Copilot may return several unchanged samples
            // before the first observable increment. Fall back to the average
            // consumption since reset instead of suppressing the forecast.
            baseline_at = window_start;
            baseline_value = 0.0;
            elapsed_days = total_days(now - baseline_at);
            consumed = current - baseline_value;
        }
        if elapsed_days <= 0.0 || consumed <= 0.0 {
            return None;
        }

        let daily_rate = consumed / elapsed_days;
        if daily_rate <= 0.0 || !daily_rate.is_finite() {
            return None;
        }

        let estimated_at = now + duration_from_days((1.0 - current) / daily_rate);
        let reset_occurs_first =
            matches!(reset_at, Some(next_reset) if next_reset > now && next_reset <= estimated_at);
        Some(UsageExhaustionForecast {
            estimated_at,
            reset_at,
            reset_occurs_first,
            daily_consumption_rate: daily_rate,
        })
    }
}

// ---------------------------------------------------------------------------
// Usage trend calculator (`UsageTrendCalculator.cs`)
// ---------------------------------------------------------------------------

/// Projects the observed usage slope through the current provider cycle and
/// stops at the first pacing breach, exhaustion, or reset boundary.
#[derive(Debug, Clone, PartialEq)]
pub struct UsageTrendProjection {
    /// Projection line from now to `end_at`.
    pub line: Vec<PacingPoint>,
    /// When the projection stops.
    pub end_at: DateTime<Utc>,
    /// Whether the observed trend is already (or becomes) ahead of pacing.
    pub breaches_pacing: bool,
}

/// Projects the usage trend from its observed slope.
pub struct UsageTrendCalculator;

impl UsageTrendCalculator {
    /// Computes the projection, or `None` when the window or rate is unusable.
    pub fn compute(
        current_percent_used: f64,
        daily_consumption_rate: f64,
        daily_target_percent: f64,
        window_start: DateTime<Utc>,
        now: DateTime<Utc>,
        window_end: DateTime<Utc>,
    ) -> Option<UsageTrendProjection> {
        if window_end <= now
            || now < window_start
            || daily_consumption_rate <= 0.0
            || !daily_consumption_rate.is_finite()
        {
            return None;
        }

        let current = current_percent_used.clamp(0.0, 1.0);
        let actual_rate = daily_consumption_rate;
        let target_rate = daily_target_percent.clamp(0.0, 1000.0) / 100.0;
        let elapsed_days = total_days(now - window_start);
        let horizon_days = total_days(window_end - now);
        let pace_now = (target_rate * elapsed_days).clamp(0.0, 1.0);
        let already_breached = current >= pace_now;

        let mut breach_days: Option<f64> = None;
        if !already_breached && target_rate > 0.0 && actual_rate > target_rate && pace_now < 1.0 {
            let candidate = (pace_now - current) / (actual_rate - target_rate);
            let target_saturates_in = (1.0 / target_rate - elapsed_days).max(0.0);
            if candidate >= 0.0 && candidate <= horizon_days && candidate <= target_saturates_in {
                breach_days = Some(candidate);
            }
        }

        let exhaustion_days = if current >= 1.0 {
            0.0
        } else {
            (1.0 - current) / actual_rate
        };
        if !already_breached && breach_days.is_none() && exhaustion_days <= horizon_days {
            breach_days = Some(exhaustion_days);
        }

        let mut end_days = breach_days.unwrap_or(exhaustion_days.min(horizon_days));
        if already_breached {
            end_days = exhaustion_days.min(horizon_days);
        }
        end_days = end_days.clamp(0.0, horizon_days);
        if end_days <= 0.0 {
            return None;
        }

        let end_milliseconds = round_half_even(end_days * 86_400_000.0);
        let end_at = now + TimeDelta::milliseconds(end_milliseconds as i64);
        let end_value = (current + actual_rate * end_days).clamp(0.0, 1.0);
        Some(UsageTrendProjection {
            line: vec![
                PacingPoint::new(now, current),
                PacingPoint::new(end_at, end_value),
            ],
            end_at,
            breaches_pacing: already_breached || breach_days.is_some(),
        })
    }
}

// ---------------------------------------------------------------------------
// Shared math helpers
// ---------------------------------------------------------------------------

/// `DateTimeOffset` subtraction expressed as fractional days.
fn total_days(duration: TimeDelta) -> f64 {
    duration.num_seconds() as f64 / 86_400.0
        + f64::from(duration.subsec_nanos()) / (86_400.0 * 1_000_000_000.0)
}

/// `TimeSpan.FromDays` with sub-second (nanosecond) precision.
fn duration_from_days(days: f64) -> TimeDelta {
    TimeDelta::nanoseconds((days * 86_400_000_000_000.0).round() as i64)
}

/// `Math.Round(value)` (banker's rounding, midpoint to even). The trend
/// calculator only feeds non-negative values here.
fn round_half_even(value: f64) -> f64 {
    let rounded = value.round();
    if (value - value.trunc()).abs() == 0.5 && (rounded as i64) % 2 != 0 {
        rounded - value.signum()
    } else {
        rounded
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(text: &str) -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(text)
            .unwrap()
            .with_timezone(&Utc)
    }

    // -----------------------------------------------------------------------
    // Pacing state store
    // -----------------------------------------------------------------------

    // The JSON shape below is what `System.Text.Json` (web defaults, indented)
    // writes, so both apps can share one pacing state file.
    #[test]
    fn serializes_the_dotnet_pacing_state_shape() {
        let mut entries = std::collections::BTreeMap::new();
        entries.insert(
            "codex\u{1f}session".to_string(),
            NaiveDate::from_ymd_opt(2026, 11, 5).unwrap(),
        );
        let state = StateDocument {
            version: 1,
            entries,
        };

        let json = serde_json::to_string_pretty(&state).unwrap();
        assert_eq!(
            json,
            "{\n  \"version\": 1,\n  \"entries\": {\n    \"codex\\u001fsession\": \"2026-11-05\"\n  }\n}"
        );
    }

    #[test]
    fn notifies_once_per_day_per_provider_window() {
        let directory = env::temp_dir().join(format!(
            "estao-pacing-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .map(|duration| duration.as_nanos())
                .unwrap_or(0)
        ));
        fs::create_dir_all(&directory).unwrap();

        let store = PacingStateStore::new(directory.join("config.json"));
        let today = NaiveDate::from_ymd_opt(2026, 11, 5).unwrap();
        let tomorrow = NaiveDate::from_ymd_opt(2026, 11, 6).unwrap();

        assert!(store.try_mark_notified("Codex", "Session", today));
        assert!(!store.try_mark_notified("codex", "session", today));
        assert!(store.try_mark_notified("codex", "session", tomorrow));

        let _ = fs::remove_dir_all(&directory);
    }

    // -----------------------------------------------------------------------
    // Ported from PacingCalculatorTests.cs
    // -----------------------------------------------------------------------

    // target_line_uses_the_full_window_when_history_is_empty
    #[test]
    fn target_line_uses_the_full_window_when_history_is_empty() {
        let now = at("2026-08-11T12:00:00Z");
        let range_start = now - TimeDelta::days(7);

        let result = PacingCalculator::compute(&[], 15.0, range_start, now).unwrap();

        assert_eq!(result.window_start, range_start);
        assert_eq!(result.target_line[0].timestamp, range_start);
        assert_eq!(result.target_line.last().unwrap().timestamp, now);
    }

    // a_single_recent_sample_does_not_collapse_the_target_line_at_now
    #[test]
    fn a_single_recent_sample_does_not_collapse_the_target_line_at_now() {
        let now = at("2026-08-11T12:00:00Z");
        let range_start = now - TimeDelta::days(7);
        let points = [PacingPoint::new(now - TimeDelta::minutes(2), 0.35)];

        let result = PacingCalculator::compute(&points, 15.0, range_start, now).unwrap();

        assert_eq!(result.window_start, range_start);
        assert_eq!(result.target_line[0].timestamp, range_start);
    }

    // a_detected_reset_still_starts_a_new_target_line
    #[test]
    fn a_detected_reset_still_starts_a_new_target_line() {
        let now = at("2026-08-11T12:00:00Z");
        let range_start = now - TimeDelta::days(7);
        let reset_at = now - TimeDelta::hours(3);
        let points = [
            PacingPoint::new(reset_at - TimeDelta::minutes(1), 0.82),
            PacingPoint::new(reset_at, 0.04),
            PacingPoint::new(now, 0.12),
        ];

        let result = PacingCalculator::compute(&points, 15.0, range_start, now).unwrap();

        assert_eq!(result.window_start, reset_at);
        assert_eq!(result.target_line[0].timestamp, reset_at);
    }

    // display_target_always_reaches_the_end_of_the_cycle
    #[test]
    fn display_target_always_reaches_the_end_of_the_cycle() {
        let start = at("2026-08-01T00:00:00Z");
        let end = start + TimeDelta::days(30);

        let line = PacingCalculator::build_target_line(start, end, 15.0);

        assert_eq!(line[0].timestamp, start);
        assert_eq!(line.last().unwrap().timestamp, end);
        assert_eq!(line.last().unwrap().value, 1.0);
    }

    // low_target_reaches_cycle_end_without_forcing_one_hundred_percent
    #[test]
    fn low_target_reaches_cycle_end_without_forcing_one_hundred_percent() {
        let start = at("2026-08-01T00:00:00Z");
        let end = start + TimeDelta::hours(5);

        let line = PacingCalculator::build_target_line(start, end, 15.0);

        assert_eq!(line.last().unwrap().timestamp, end);
        assert!((line.last().unwrap().value - 0.03125).abs() < 1e-6);
    }

    // -----------------------------------------------------------------------
    // Ported from UsageExhaustionCalculatorTests.cs
    // -----------------------------------------------------------------------

    // projects_exhaustion_from_the_observed_rate
    #[test]
    fn projects_exhaustion_from_the_observed_rate() {
        let now = at("2026-08-11T12:00:00Z");
        let points = [
            PacingPoint::new(now - TimeDelta::days(1), 0.25),
            PacingPoint::new(now, 0.50),
        ];

        let forecast = UsageExhaustionCalculator::compute(
            &points,
            Some(0.50),
            TimeDelta::days(7),
            now,
            Some(now + TimeDelta::days(3)),
        )
        .unwrap();

        assert_eq!(forecast.estimated_at, now + TimeDelta::days(2));
        assert!(!forecast.reset_occurs_first);
    }

    // reports_when_reset_happens_before_exhaustion
    #[test]
    fn reports_when_reset_happens_before_exhaustion() {
        let now = at("2026-08-11T12:00:00Z");
        let reset_at = now + TimeDelta::hours(6);
        let points = [
            PacingPoint::new(now - TimeDelta::days(1), 0.10),
            PacingPoint::new(now, 0.20),
        ];

        let forecast = UsageExhaustionCalculator::compute(
            &points,
            Some(0.20),
            TimeDelta::days(7),
            now,
            Some(reset_at),
        )
        .unwrap();

        assert!(forecast.reset_occurs_first);
        assert_eq!(forecast.reset_at, Some(reset_at));
    }

    // uses_window_start_when_only_current_usage_is_known
    #[test]
    fn uses_window_start_when_only_current_usage_is_known() {
        let now = at("2026-08-11T12:00:00Z");
        let reset_at = now + TimeDelta::days(5);

        let forecast = UsageExhaustionCalculator::compute(
            &[],
            Some(0.50),
            TimeDelta::days(7),
            now,
            Some(reset_at),
        )
        .unwrap();

        assert_eq!(forecast.estimated_at, now + TimeDelta::days(2));
    }

    // flat_recent_samples_fall_back_to_average_since_reset
    #[test]
    fn flat_recent_samples_fall_back_to_average_since_reset() {
        let now = at("2026-08-11T12:00:00Z");
        let reset_at = now + TimeDelta::days(21);
        let points = [
            PacingPoint::new(now - TimeDelta::hours(2), 0.10),
            PacingPoint::new(now, 0.10),
        ];

        let forecast = UsageExhaustionCalculator::compute(
            &points,
            Some(0.10),
            TimeDelta::days(30),
            now,
            Some(reset_at),
        )
        .unwrap();

        assert!(forecast.reset_occurs_first);
        assert!((forecast.daily_consumption_rate - 1.0 / 90.0).abs() < 1e-8);
    }

    // -----------------------------------------------------------------------
    // Ported from UsageTrendCalculatorTests.cs
    // -----------------------------------------------------------------------

    // fast_trend_stops_at_first_pacing_breach
    #[test]
    fn fast_trend_stops_at_first_pacing_breach() {
        let start = at("2026-08-01T00:00:00Z");
        let now = start + TimeDelta::days(2);
        let end = start + TimeDelta::days(7);

        let projection = UsageTrendCalculator::compute(0.20, 0.20, 15.0, start, now, end).unwrap();

        assert!(projection.breaches_pacing);
        assert_eq!(projection.end_at, now + TimeDelta::days(2));
        assert!((projection.line.last().unwrap().value - 0.60).abs() < 1e-8);
    }

    // slower_trend_continues_to_reset_without_false_breach
    #[test]
    fn slower_trend_continues_to_reset_without_false_breach() {
        let start = at("2026-08-01T00:00:00Z");
        let now = start + TimeDelta::days(2);
        let end = start + TimeDelta::days(7);

        let projection = UsageTrendCalculator::compute(0.10, 0.02, 15.0, start, now, end).unwrap();

        assert!(!projection.breaches_pacing);
        assert_eq!(projection.end_at, end);
        assert!((projection.line.last().unwrap().value - 0.20).abs() < 1e-8);
    }

    // already_breached_trend_remains_visible_until_exhaustion
    #[test]
    fn already_breached_trend_remains_visible_until_exhaustion() {
        let start = at("2026-08-01T00:00:00Z");
        let now = start + TimeDelta::days(1);
        let end = start + TimeDelta::days(7);

        let projection = UsageTrendCalculator::compute(0.40, 0.30, 15.0, start, now, end).unwrap();

        assert!(projection.breaches_pacing);
        assert_eq!(projection.end_at, now + TimeDelta::days(2));
        assert_eq!(projection.line.last().unwrap().value, 1.0);
    }
}
