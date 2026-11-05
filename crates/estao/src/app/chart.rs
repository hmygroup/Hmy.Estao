//! The usage history chart element (canvas drawing).
//!
//! Wave 6 contract: a gpui element painting the per-window usage history
//! (percent-used over time) with the pacing target line, ported from the
//! .NET `ZarpaUsageChart`.
//!
//! The .NET control draws a smooth line with an area fill, a "now" marker,
//! percentage labels on the right and a dashed daily-target line. This port
//! keeps the visual intent while dropping the animation timer and the
//! WinForms-specific caching: the pure [`UsageChart`] model is built once per
//! frame from the shared history and a `canvas()` element paints it.

use chrono::{DateTime, Local, TimeDelta, Utc};
use estao_core::catalog;
use estao_core::config::PacingConfig;
use estao_core::models::{RateWindow, UsageHistoryPoint};
use estao_core::window_catalog;
use gpui::{
    canvas, div, point, prelude::*, px, size, Bounds, ColorExt, FontWeight, PaintQuad, PathBuilder,
    Pixels, Point, Rgba, SharedString, Window,
};

use crate::app::theme::Palette;

/// Height of the whole chart card, in logical pixels.
pub const CHART_HEIGHT: f32 = 178.0;

/// Width of the right-hand percentage gutter, in logical pixels.
const AXIS_GUTTER: f32 = 34.0;

/// Panel padding, in logical pixels.
const PANEL_PADDING: f32 = 10.0;

/// A single sample on the percent-used line.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ChartPoint {
    /// When the sample was taken.
    pub at: DateTime<Utc>,
    /// Fraction of the window consumed (`0.0` - `1.0`).
    pub value: f64,
}

/// Everything the canvas needs to paint one provider window's history.
#[derive(Debug, Clone, PartialEq)]
pub struct UsageChart {
    /// Card subtitle, e.g. `Stored locally · 5h window · daily target 15%`.
    pub subtitle: SharedString,
    /// Left edge of the displayed time range.
    pub range_start: DateTime<Utc>,
    /// Right edge of the displayed time range (the provider reset when known).
    pub range_end: DateTime<Utc>,
    /// Percent-used samples, ordered by timestamp and seeded at `0%` on the
    /// left edge when the history does not reach it.
    pub points: Vec<ChartPoint>,
    /// Daily pacing target as a percentage (`0.0` - `100.0`), when enabled.
    pub target_percent: Option<f64>,
    /// Color of the usage line.
    pub color: Rgba,
    /// Whether any stored history sample backed the line.
    pub has_history: bool,
}

impl UsageChart {
    /// The percentage labels shown on the right gutter.
    pub fn percent_labels() -> [&'static str; 3] {
        ["100%", "50%", "0%"]
    }
}

/// Builds the chart model for one provider rate window.
///
/// Mirrors `ZarpaUsageContent.AddUsageCharts`: the display range comes from the
/// window catalog, the visible samples are clipped to `[range_start, now]`, and
/// a `0%` point is seeded on the left edge so a sparse history still starts at
/// the window boundary instead of appearing as an isolated dot near "now".
pub fn build_chart(
    provider: &str,
    window: &RateWindow,
    history: &[UsageHistoryPoint],
    pacing: &PacingConfig,
    now: DateTime<Utc>,
    color: Rgba,
) -> UsageChart {
    let provider_id = catalog::normalize_id(provider);
    let time_range =
        window_catalog::display_range(Some(&provider_id), Some(&window.id), Some(&window.title));

    let range_end = match window.reset_at {
        Some(reset_at) if reset_at > now => reset_at,
        _ => now,
    };
    let range_start = range_end - time_range;

    let mut samples: Vec<ChartPoint> = history
        .iter()
        .filter(|point| catalog::normalize_id(&point.provider) == provider_id)
        .filter(|point| point.window.eq_ignore_ascii_case(&window.id))
        .filter(|point| point.timestamp >= range_start && point.timestamp <= now)
        .filter(|point| point.percent_used.is_finite())
        .map(|point| ChartPoint {
            at: point.timestamp,
            value: point.percent_used.clamp(0.0, 1.0),
        })
        .collect();

    samples.sort_by_key(|point| point.at);
    // Repeated refreshes can land in the same tick; keep the latest sample so
    // the line never loops back on itself.
    let mut points: Vec<ChartPoint> = Vec::with_capacity(samples.len() + 1);
    for sample in samples {
        match points.last_mut() {
            Some(last) if last.at == sample.at => *last = sample,
            _ => points.push(sample),
        }
    }

    let has_history = !points.is_empty();
    if points.first().map(|point| point.at) != Some(range_start) {
        points.insert(
            0,
            ChartPoint {
                at: range_start,
                value: 0.0,
            },
        );
    }

    let target_percent = (pacing.enabled && pacing.daily_target_percent > 0.0)
        .then_some(pacing.daily_target_percent.clamp(0.0, 100.0));

    let mut subtitle = format!(
        "Stored locally · {} window",
        window_catalog::display_label(time_range)
    );
    if let Some(target) = target_percent {
        subtitle.push_str(&format!(" · daily target {target:.0}%"));
    }

    UsageChart {
        subtitle: SharedString::from(subtitle),
        range_start,
        range_end,
        points,
        target_percent,
        color,
        has_history,
    }
}

/// The strftime pattern used for the time axis at a given range.
///
/// Mirrors `ZarpaUsageChart.FormatAxisTime`: intraday labels show only the
/// time, anything up to two weeks adds the date, and longer ranges use the
/// date alone.
fn axis_pattern(range: TimeDelta) -> &'static str {
    if range <= TimeDelta::days(1) {
        "%H:%M"
    } else if range <= TimeDelta::days(14) {
        "%d/%m %H:%M"
    } else {
        "%d/%m"
    }
}

/// The start, middle and end labels of the time axis, in local time.
pub fn axis_labels(chart: &UsageChart) -> (String, String, String) {
    let range = chart.range_end - chart.range_start;
    let pattern = axis_pattern(range);
    let midpoint = chart.range_start + range / 2;
    let format =
        |timestamp: DateTime<Utc>| timestamp.with_timezone(&Local).format(pattern).to_string();

    (
        format(chart.range_start),
        format(midpoint),
        format(chart.range_end),
    )
}

/// Normalized horizontal position of `at` inside `[start, end]`.
fn x_fraction(at: DateTime<Utc>, start: DateTime<Utc>, end: DateTime<Utc>) -> f32 {
    let span = (end - start).num_milliseconds();
    if span <= 0 {
        return 0.0;
    }

    let elapsed = (at - start).num_milliseconds();
    (elapsed as f64 / span as f64).clamp(0.0, 1.0) as f32
}

/// Normalized vertical position of a percent-used value.
fn y_fraction(value: f64) -> f32 {
    value.clamp(0.0, 1.0) as f32
}

/// Colors the canvas needs; bundled so the paint closure captures one value.
#[derive(Debug, Clone, Copy)]
struct PlotTheme {
    line: Rgba,
    target: Rgba,
    now: Rgba,
}

/// Renders the chart card: title, subtitle, plot, percentage gutter and time axis.
pub fn render(chart: &UsageChart, palette: Palette) -> impl IntoElement {
    let theme = PlotTheme {
        line: chart.color,
        target: palette.warning,
        now: palette.border,
    };
    let points = chart.points.clone();
    let range_start = chart.range_start;
    let range_end = chart.range_end;
    let target_percent = chart.target_percent;
    let now = Utc::now();
    let show_now = now > range_start && now < range_end;
    let has_history = chart.has_history;
    let (start_label, middle_label, end_label) = axis_labels(chart);
    let muted = palette.text_muted;

    div()
        .w_full()
        .h(px(CHART_HEIGHT))
        .flex()
        .flex_col()
        .gap(px(4.0))
        .p(px(PANEL_PADDING))
        .bg(palette.surface)
        .border_1()
        .border_color(palette.border)
        .rounded_md()
        .child(
            div()
                .flex()
                .flex_col()
                .child(
                    div()
                        .text_size(px(11.0))
                        .font_weight(FontWeight::SEMIBOLD)
                        .text_color(palette.text)
                        .child("Usage history"),
                )
                .child(
                    div()
                        .text_size(px(9.0))
                        .text_color(muted)
                        .child(chart.subtitle.clone()),
                ),
        )
        .child(
            div()
                .flex_1()
                .min_h_0()
                .flex()
                .flex_row()
                .gap(px(4.0))
                .child(
                    div()
                        .flex_1()
                        .min_w_0()
                        .relative()
                        .child(
                            canvas(
                                |_, _, _| (),
                                move |bounds, _, window, _| {
                                    paint_plot(
                                        bounds,
                                        &points,
                                        range_start,
                                        range_end,
                                        now,
                                        show_now,
                                        target_percent,
                                        theme,
                                        window,
                                    );
                                },
                            )
                            .size_full(),
                        )
                        .when(!has_history, |element| {
                            element.child(
                                div()
                                    .absolute()
                                    .inset_0()
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .text_size(px(10.0))
                                    .text_color(muted)
                                    .child("Collecting local usage data..."),
                            )
                        }),
                )
                .child(
                    div()
                        .w(px(AXIS_GUTTER))
                        .flex()
                        .flex_col()
                        .justify_between()
                        .items_end()
                        .text_size(px(9.0))
                        .text_color(muted)
                        .children(
                            UsageChart::percent_labels()
                                .into_iter()
                                .map(|label| div().child(label)),
                        ),
                ),
        )
        .child(
            div()
                .flex()
                .justify_between()
                .pr(px(AXIS_GUTTER + 4.0))
                .text_size(px(9.0))
                .text_color(muted)
                .child(start_label)
                .child(middle_label)
                .child(end_label),
        )
}

/// Paints the usage line, its area fill, the pacing target and the "now" marker.
#[allow(clippy::too_many_arguments)]
fn paint_plot(
    bounds: Bounds<Pixels>,
    points: &[ChartPoint],
    range_start: DateTime<Utc>,
    range_end: DateTime<Utc>,
    now: DateTime<Utc>,
    show_now: bool,
    target_percent: Option<f64>,
    theme: PlotTheme,
    window: &mut Window,
) {
    let width = bounds.size.width;
    let height = bounds.size.height;
    if width <= px(1.0) || height <= px(1.0) {
        return;
    }

    let left = bounds.origin.x;
    let top = bounds.origin.y;
    let bottom = top + height;
    // Leave a pixel of headroom so a 100% line does not merge with the edge.
    let plot_height = height - px(2.0);
    let x_at = |at: DateTime<Utc>| left + width * x_fraction(at, range_start, range_end);
    let y_at = |value: f64| top + plot_height * (1.0 - y_fraction(value)) + px(1.0);

    let mapped: Vec<Point<Pixels>> = points
        .iter()
        .map(|sample| point(x_at(sample.at), y_at(sample.value)))
        .collect();
    if mapped.is_empty() {
        return;
    }

    if mapped.len() >= 2 {
        let first = mapped[0];
        let last = mapped[mapped.len() - 1];

        let mut area = PathBuilder::fill();
        area.move_to(point(first.x, bottom));
        for mapped_point in &mapped {
            area.line_to(*mapped_point);
        }
        area.line_to(point(last.x, bottom));
        area.close();
        if let Ok(path) = area.build() {
            window.paint_path(path, theme.line.opacity(0.16));
        }

        let mut line = PathBuilder::stroke(px(1.5));
        line.move_to(first);
        for mapped_point in &mapped[1..] {
            line.line_to(*mapped_point);
        }
        if let Ok(path) = line.build() {
            window.paint_path(path, theme.line);
        }
    }

    let last = mapped[mapped.len() - 1];
    window.paint_quad(
        PaintQuad::new(Bounds::centered_at(last, size(px(5.0), px(5.0))))
            .background(theme.line)
            .corner_radii(px(2.5)),
    );

    if let Some(target_percent) = target_percent {
        paint_dashed_horizontal(
            left,
            left + width,
            y_at(target_percent / 100.0),
            theme.target,
            window,
        );
    }

    if show_now {
        paint_dotted_vertical(x_at(now), top, bottom, theme.now.opacity(0.6), window);
    }
}

/// Paints a dashed horizontal reference line.
fn paint_dashed_horizontal(
    start_x: Pixels,
    end_x: Pixels,
    y: Pixels,
    color: Rgba,
    window: &mut Window,
) {
    const DASH: f32 = 5.0;
    const GAP: f32 = 4.0;

    let mut x = start_x;
    while x < end_x {
        let remaining = end_x - x;
        let dash = if remaining < px(DASH) {
            remaining
        } else {
            px(DASH)
        };
        window.paint_quad(
            PaintQuad::new(Bounds::new(point(x, y), size(dash, px(1.0)))).background(color),
        );
        x += px(DASH + GAP);
    }
}

/// Paints a dotted vertical "now" marker.
fn paint_dotted_vertical(
    x: Pixels,
    start_y: Pixels,
    end_y: Pixels,
    color: Rgba,
    window: &mut Window,
) {
    const DOT: f32 = 3.0;
    const GAP: f32 = 3.0;

    let mut y = start_y;
    while y < end_y {
        let remaining = end_y - y;
        let dot = if remaining < px(DOT) {
            remaining
        } else {
            px(DOT)
        };
        window.paint_quad(
            PaintQuad::new(Bounds::new(point(x, y), size(px(1.0), dot))).background(color),
        );
        y += px(DOT + GAP);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use estao_core::models::UsageHistoryPoint;

    fn at(text: &str) -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(text)
            .unwrap()
            .with_timezone(&Utc)
    }

    fn session_window(reset_at: Option<DateTime<Utc>>) -> RateWindow {
        RateWindow {
            id: "session".to_string(),
            title: "Session".to_string(),
            percent_used: Some(0.4),
            reset_at,
            used: None,
            limit: None,
            unit: None,
        }
    }

    fn point(provider: &str, window: &str, timestamp: &str, value: f64) -> UsageHistoryPoint {
        UsageHistoryPoint {
            provider: provider.to_string(),
            window: window.to_string(),
            timestamp: at(timestamp),
            percent_used: value,
        }
    }

    #[test]
    fn seeds_the_left_edge_with_zero_percent() {
        let now = at("2026-11-05T12:00:00Z");
        let chart = build_chart(
            "codex",
            &session_window(None),
            &[],
            &PacingConfig::default(),
            now,
            gpui::rgb(0x4C8DFF),
        );

        assert_eq!(chart.range_start, now - window_catalog::FIVE_HOURS);
        assert_eq!(chart.range_end, now);
        assert_eq!(
            chart.points,
            vec![ChartPoint {
                at: now - window_catalog::FIVE_HOURS,
                value: 0.0
            }]
        );
        assert!(!chart.has_history);
        assert_eq!(chart.target_percent, None);
    }

    #[test]
    fn keeps_only_the_selected_provider_and_window_inside_the_range() {
        let now = at("2026-11-05T12:00:00Z");
        let history = vec![
            point("codex", "session", "2026-11-05T11:55:00Z", 0.20),
            point("codex", "session", "2026-11-05T11:59:00Z", 0.30),
            point("codex", "weekly", "2026-11-05T11:58:00Z", 0.90),
            point("claude", "session", "2026-11-05T11:57:00Z", 0.50),
            point("codex", "session", "2026-11-05T07:00:00Z", 0.10),
        ];

        let chart = build_chart(
            "Codex",
            &session_window(None),
            &history,
            &PacingConfig::default(),
            now,
            gpui::rgb(0x4C8DFF),
        );

        assert!(chart.has_history);
        // The sample exactly on the left edge wins over the seeded 0%.
        assert_eq!(
            chart.points,
            vec![
                ChartPoint {
                    at: now - window_catalog::FIVE_HOURS,
                    value: 0.10
                },
                ChartPoint {
                    at: at("2026-11-05T11:55:00Z"),
                    value: 0.20
                },
                ChartPoint {
                    at: at("2026-11-05T11:59:00Z"),
                    value: 0.30
                },
            ]
        );
    }

    #[test]
    fn keeps_the_latest_sample_when_two_share_a_timestamp() {
        let now = at("2026-11-05T12:00:00Z");
        let history = vec![
            point("codex", "session", "2026-11-05T11:59:00Z", 0.20),
            point("codex", "session", "2026-11-05T11:59:00Z", 0.35),
        ];

        let chart = build_chart(
            "codex",
            &session_window(None),
            &history,
            &PacingConfig::default(),
            now,
            gpui::rgb(0x4C8DFF),
        );

        assert_eq!(
            chart.points.last().map(|point| point.value),
            Some(0.35),
            "the newest sample wins"
        );
        assert_eq!(
            chart
                .points
                .iter()
                .filter(|point| point.at == at("2026-11-05T11:59:00Z"))
                .count(),
            1
        );
    }

    #[test]
    fn uses_the_reset_boundary_for_the_visible_range() {
        let now = at("2026-11-05T12:00:00Z");
        let reset_at = now + TimeDelta::hours(2);
        let chart = build_chart(
            "codex",
            &session_window(Some(reset_at)),
            &[],
            &PacingConfig::default(),
            now,
            gpui::rgb(0x4C8DFF),
        );

        assert_eq!(chart.range_end, reset_at);
        assert_eq!(chart.range_start, reset_at - window_catalog::FIVE_HOURS);
    }

    #[test]
    fn only_enables_the_target_line_for_enabled_pacing() {
        let now = at("2026-11-05T12:00:00Z");
        let disabled = PacingConfig::default();
        let enabled = PacingConfig {
            enabled: true,
            daily_target_percent: 15.0,
            notify_on_exceed: true,
        };

        let without = build_chart(
            "codex",
            &session_window(None),
            &[],
            &disabled,
            now,
            gpui::rgb(0x4C8DFF),
        );
        let with = build_chart(
            "codex",
            &session_window(None),
            &[],
            &enabled,
            now,
            gpui::rgb(0x4C8DFF),
        );

        assert_eq!(without.target_percent, None);
        assert_eq!(with.target_percent, Some(15.0));
        assert!(with.subtitle.as_ref().contains("daily target 15%"));
    }

    #[test]
    fn selects_the_axis_pattern_from_the_display_range() {
        assert_eq!(axis_pattern(window_catalog::FIVE_HOURS), "%H:%M");
        assert_eq!(axis_pattern(window_catalog::WEEKLY), "%d/%m %H:%M");
        assert_eq!(axis_pattern(window_catalog::MONTHLY), "%d/%m");
    }

    #[test]
    fn maps_timestamps_and_values_into_the_plot() {
        let start = at("2026-11-05T07:00:00Z");
        let end = at("2026-11-05T12:00:00Z");

        assert_eq!(x_fraction(start, start, end), 0.0);
        assert_eq!(x_fraction(end, start, end), 1.0);
        assert!((x_fraction(start + TimeDelta::minutes(150), start, end) - 0.5).abs() < 1e-6);
        assert_eq!(x_fraction(start - TimeDelta::hours(1), start, end), 0.0);
        assert_eq!(x_fraction(end + TimeDelta::hours(1), start, end), 1.0);
        assert_eq!(x_fraction(start, start, start), 0.0);

        assert_eq!(y_fraction(0.0), 0.0);
        assert_eq!(y_fraction(1.0), 1.0);
        assert_eq!(y_fraction(2.0), 1.0);
        assert_eq!(y_fraction(-1.0), 0.0);
    }

    #[test]
    fn axis_labels_use_start_middle_and_end_of_the_range() {
        let now = at("2026-11-05T12:00:00Z");
        let chart = build_chart(
            "codex",
            &session_window(Some(now + TimeDelta::hours(3))),
            &[],
            &PacingConfig::default(),
            now,
            gpui::rgb(0x4C8DFF),
        );

        let (start, middle, end) = axis_labels(&chart);
        assert!(!start.is_empty());
        assert!(!middle.is_empty());
        assert!(!end.is_empty());
        assert_ne!(start, end);
    }
}
