//! Pacing toast notifications.
//!
//! Wave 5 contract: after each refresh, compare every snapshot window
//! against its provider's pacing config (via the core pacing calculator +
//! `PacingStateStore` once-per-day semantics) and raise a Windows toast
//! with `tauri_winrt_notification`. Ported from
//! `TrayApplicationContext.CheckPacingWarningsAsync` +
//! `ShowPacingBalloon`.

use std::path::Path;

use chrono::{Local, Utc};
use estao_core::catalog;
use estao_core::config::EstaoConfig;
use estao_core::constants::DISPLAY_NAME;
use estao_core::models::{UsageHistoryPoint, UsageSnapshot};
use estao_core::pacing::{PacingCalculator, PacingPoint, PacingStateStore};
use estao_core::window_catalog;
use tauri_winrt_notification::{Sound, Toast};

/// One "provider window ahead of pace" notification, after the once-per-day
/// gate has been satisfied.
struct PacingWarning {
    display_name: String,
    window_title: String,
    actual_percent: i64,
    expected_percent: i64,
}

/// Checks every snapshot window against its pacing target and shows at most
/// one toast per provider/window/day.
///
/// `config_path` is the Estao config file; the pacing state file lives beside
/// it, exactly like the .NET `PacingStateStore(configStore.Path)`.
pub fn check_pacing_warnings(
    config: &EstaoConfig,
    snapshots: &[UsageSnapshot],
    history: &[UsageHistoryPoint],
    config_path: &Path,
) {
    let now = Utc::now();
    let today = Local::now().date_naive();
    let state_store = PacingStateStore::new(config_path);

    for snapshot in snapshots {
        if snapshot.error.is_some() {
            continue;
        }

        let provider = catalog::normalize_id(&snapshot.provider);
        let Some(configured) = config
            .providers
            .iter()
            .find(|configured| catalog::normalize_id(&configured.id) == provider)
        else {
            continue;
        };

        let pacing = &configured.pacing;
        if !pacing.enabled || !pacing.notify_on_exceed {
            continue;
        }

        for window in &snapshot.windows {
            let range_start = now
                - window_catalog::display_range(
                    Some(&provider),
                    Some(&window.id),
                    Some(&window.title),
                );

            let mut points: Vec<PacingPoint> = history
                .iter()
                .filter(|point| {
                    point.provider.eq_ignore_ascii_case(&provider)
                        && point.window.eq_ignore_ascii_case(&window.id)
                })
                .map(|point| PacingPoint::new(point.timestamp, point.percent_used))
                .collect();
            if points.is_empty() {
                continue;
            }
            points.sort_by_key(|point| point.timestamp);

            let Some(result) =
                PacingCalculator::compute(&points, pacing.daily_target_percent, range_start, now)
            else {
                continue;
            };
            if !result.is_over_pace {
                continue;
            }

            // `try_mark_notified` returns true only the first time this
            // provider/window crosses the target today.
            if !state_store.try_mark_notified(&provider, &window.id, today) {
                continue;
            }

            show_warning(&PacingWarning {
                display_name: snapshot.display_name.clone(),
                window_title: window.title.clone(),
                actual_percent: (result.actual_percent_now * 100.0).round() as i64,
                expected_percent: (result.expected_percent_now * 100.0).round() as i64,
            });
        }
    }
}

/// Port of `TrayApplicationContext.ShowPacingBalloon` using a Windows toast.
fn show_warning(warning: &PacingWarning) {
    let heading = format!(
        "{}: {} ahead of pace",
        warning.display_name, warning.window_title
    );
    let body = format!(
        "You've used {}% of the {} limit, above your {}% daily target.",
        warning.actual_percent,
        warning.window_title.to_lowercase(),
        warning.expected_percent
    );

    let result = Toast::new(Toast::POWERSHELL_APP_ID)
        .title(DISPLAY_NAME)
        .text1(&heading)
        .text2(&body)
        .sound(Some(Sound::Default))
        .show();

    if let Err(error) = result {
        eprintln!("Estao: failed to show the pacing notification: {error}");
    }
}
