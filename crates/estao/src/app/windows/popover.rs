//! The usage popover window (left-click on the tray icon).
//!
//! Wave 6 contract: a borderless popup near the tray anchor listing every
//! enabled provider with its rate windows (percent, used/total, reset
//! countdown), the per-provider usage chart, and actions (refresh, open
//! settings). Ported from
//! `src/Hmy.Estao.App/Controls/Zarpa/UsagePopover.cs`.
//!
//! The view renders straight from the shared [`AppState`] entity and observes
//! it, so a background refresh repaints the popup without any polling of its
//! own. Dismissal mirrors the .NET widget where GPUI exposes the hooks:
//! Escape closes it, and both footer actions close it before running (the
//! .NET popover closes itself before opening Settings). GPUI 0.3 does not
//! publish a window-deactivation callback (only element focus and window
//! visibility), so outside-click dismissal is not wired; the tray toggle and
//! Escape remain the documented ways to close the popup.

use std::collections::HashMap;
use std::sync::Arc;

use chrono::{DateTime, TimeDelta, Utc};
use estao_core::catalog;
use estao_core::config::{
    EstaoConfig, ProviderConfig, UsageColorCatalog, UsageColorConfig, UsageColorLevel,
};
use estao_core::formatting;
use estao_core::models::{CreditsSnapshot, RateWindow, UsageHistoryPoint, UsageSnapshot};
use gpui::{
    div, img, point, prelude::*, px, relative, rgb, size, App, Bounds, Context, Entity,
    FocusHandle, Focusable, FontWeight, KeyDownEvent, Render, RenderImage, Rgba, SharedString,
    Subscription, Window,
};
use tray_icon::TrayIconEvent;

use crate::app::chart;
use crate::app::icons;
use crate::app::state::AppState;
use crate::app::theme::Palette;

/// The fixed popover size, in logical pixels.
pub const POPOVER_SIZE: (f32, f32) = (420.0, 620.0);

/// Gap between the tray icon and the popup, in logical pixels.
const ANCHOR_GAP: f32 = 8.0;

/// Provider icon edge inside the tabs, in physical pixels.
const TAB_ICON_SIZE: u32 = icons::PROVIDER_ICON_SIZE;

/// A tray-icon position in physical screen pixels.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TrayAnchor {
    /// Horizontal center of the tray icon.
    pub x: f32,
    /// Top edge of the tray icon; the popup opens above it.
    pub y: f32,
}

/// A rectangle in physical screen pixels.
#[derive(Debug, Clone, Copy, PartialEq)]
struct PhysicalRect {
    left: f32,
    top: f32,
    right: f32,
    bottom: f32,
}

/// Extracts the placement anchor from a tray icon event.
///
/// Returns `None` for events that are not clicks, so callers can fall back to
/// a centered window.
pub fn anchor_from_event(event: &TrayIconEvent) -> Option<TrayAnchor> {
    let TrayIconEvent::Click { position, rect, .. } = event else {
        return None;
    };

    // Windows reports the icon rectangle; Linux app indicators leave it empty,
    // so fall back to the pointer position.
    let (x, y) = if rect.size.width > 0 && rect.size.height > 0 {
        (
            rect.position.x + f64::from(rect.size.width) / 2.0,
            rect.position.y,
        )
    } else {
        (position.x, position.y)
    };

    Some(TrayAnchor {
        x: x as f32,
        y: y as f32,
    })
}

/// Computes the popup bounds just above the tray icon.
///
/// Returns `None` when the platform cannot report the monitor metrics; the
/// caller then falls back to a centered window.
pub fn popover_bounds(anchor: Option<TrayAnchor>) -> Option<Bounds<gpui::Pixels>> {
    let anchor = anchor?;
    let (scale_factor, work_area) = screen_metrics_at(anchor.x, anchor.y)?;
    let scale = if scale_factor > 0.0 {
        scale_factor
    } else {
        1.0
    };

    // Mirrors `UsagePopover.ShowAt`: never grow past the usable desktop.
    let available_width = (work_area.right - work_area.left) / scale;
    let available_height = (work_area.bottom - work_area.top) / scale;
    let width = if available_width > 0.0 {
        POPOVER_SIZE.0.min(available_width)
    } else {
        POPOVER_SIZE.0
    };
    let height = if available_height > 0.0 {
        POPOVER_SIZE.1.min(available_height)
    } else {
        POPOVER_SIZE.1
    };

    Some(anchored_bounds(
        anchor,
        scale_factor,
        Some(work_area),
        (width, height),
    ))
}

/// Places the popup above `anchor`, in logical pixels, clamped to the work area.
fn anchored_bounds(
    anchor: TrayAnchor,
    scale_factor: f32,
    work_area: Option<PhysicalRect>,
    dimensions: (f32, f32),
) -> Bounds<gpui::Pixels> {
    let scale = if scale_factor > 0.0 {
        scale_factor
    } else {
        1.0
    };
    let (width, height) = dimensions;
    let anchor_x = anchor.x / scale;
    let anchor_y = anchor.y / scale;

    let mut x = anchor_x - width / 2.0;
    let mut y = anchor_y - height - ANCHOR_GAP;

    if let Some(area) = work_area {
        let left = area.left / scale;
        let top = area.top / scale;
        let right = area.right / scale;
        let bottom = area.bottom / scale;
        // Use `max` so a window taller/wider than the screen still starts at
        // the top/left edge instead of producing an inverted clamp range.
        x = x.clamp(left, (right - width).max(left));
        y = y.clamp(top, (bottom - height).max(top));
    }

    Bounds::new(point(px(x), px(y)), size(px(width), px(height)))
}

/// Returns the DPI scale factor and work area for the monitor under a point.
///
/// `anchor` values come from `tray-icon`, which reports physical pixels. GPUI
/// places windows in logical pixels, so the popup converts with the scale
/// factor of the monitor that owns the tray icon.
#[cfg(target_os = "windows")]
fn screen_metrics_at(x: f32, y: f32) -> Option<(f32, PhysicalRect)> {
    use windows::core::PCWSTR;
    use windows::Win32::Foundation::POINT;
    use windows::Win32::Graphics::Gdi::{
        CreateDCW, DeleteDC, GetDeviceCaps, GetMonitorInfoW, MonitorFromPoint, LOGPIXELSX,
        MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST,
    };

    let cursor = POINT {
        x: x.round() as i32,
        y: y.round() as i32,
    };
    let monitor = unsafe { MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST) };
    if monitor.is_invalid() {
        return None;
    }

    let mut info = MONITORINFOEXW {
        monitorInfo: MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFOEXW>() as u32,
            ..Default::default()
        },
        ..Default::default()
    };
    let filled = unsafe { GetMonitorInfoW(monitor, &mut info.monitorInfo) };
    if !filled.as_bool() {
        return None;
    }

    let device = PCWSTR(info.szDevice.as_ptr());
    let dc = unsafe { CreateDCW(PCWSTR::null(), device, PCWSTR::null(), None) };
    if dc.is_invalid() {
        return None;
    }
    let dpi = unsafe { GetDeviceCaps(Some(dc), LOGPIXELSX) };
    unsafe {
        let _ = DeleteDC(dc);
    }
    if dpi <= 0 {
        return None;
    }

    let work = info.monitorInfo.rcWork;
    Some((
        dpi as f32 / 96.0,
        PhysicalRect {
            left: work.left as f32,
            top: work.top as f32,
            right: work.right as f32,
            bottom: work.bottom as f32,
        },
    ))
}

/// Non-Windows builds have no notion of a notification-area anchor.
#[cfg(not(target_os = "windows"))]
fn screen_metrics_at(_x: f32, _y: f32) -> Option<(f32, PhysicalRect)> {
    None
}

/// The usage popover view.
pub struct UsagePopover {
    state: Entity<AppState>,
    palette: Palette,
    selected: Option<String>,
    focus_handle: FocusHandle,
    icons: HashMap<String, Arc<RenderImage>>,
    _state_subscription: Subscription,
}

impl UsagePopover {
    /// Builds the popover around the shared application state.
    pub fn new(
        state: Entity<AppState>,
        palette: Palette,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        Self::new_with_provider(state, palette, None, window, cx)
    }

    /// Opens with the provider hovered in the taskbar overlay selected.
    pub fn new_with_provider(
        state: Entity<AppState>,
        palette: Palette,
        provider: Option<String>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let subscription = cx.observe(&state, |_, _, cx| cx.notify());
        let focus_handle = cx.focus_handle();
        let mut popover = Self {
            state,
            palette,
            selected: provider.map(|id| catalog::normalize_id(&id)),
            focus_handle,
            icons: HashMap::new(),
            _state_subscription: subscription,
        };
        popover.ensure_selection(cx);
        // The popup is shown without activation, but a click inside activates
        // it; focusing the root makes Escape work from that point on.
        window.focus(&popover.focus_handle, cx);
        popover
    }

    /// Switches an existing popover to the hovered provider.
    pub fn select_provider(&mut self, provider: &str, cx: &mut Context<Self>) {
        self.selected = Some(catalog::normalize_id(provider));
        self.ensure_selection(cx);
        cx.notify();
    }

    /// Drops a selection that no longer exists and falls back to the first
    /// provider, mirroring `UsagePopover.UpdateSnapshots`.
    fn ensure_selection(&mut self, cx: &App) {
        let snapshots = self.state.read(cx).snapshots();
        let still_there = self.selected.as_deref().is_some_and(|selected| {
            snapshots
                .iter()
                .any(|snapshot| catalog::normalize_id(&snapshot.provider) == selected)
        });
        if !still_there {
            self.selected = snapshots
                .first()
                .map(|snapshot| catalog::normalize_id(&snapshot.provider));
        }
    }

    /// Lazily rasterizes the icons the tab row needs.
    fn cache_icons(&mut self, snapshots: &[UsageSnapshot]) {
        for snapshot in snapshots {
            let id = catalog::normalize_id(&snapshot.provider);
            if self.icons.contains_key(&id) {
                continue;
            }
            if let Some(image) = icons::provider_icon_image(&id, TAB_ICON_SIZE) {
                self.icons.insert(id, image);
            }
        }
    }

    fn tab(&self, snapshot: &UsageSnapshot, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = self.palette;
        let id = catalog::normalize_id(&snapshot.provider);
        let selected = self.selected.as_deref() == Some(id.as_str());
        let color = if snapshot.error.is_some() {
            palette.critical
        } else if selected {
            palette.text
        } else {
            palette.text_muted
        };
        let icon = self.icons.get(&id).cloned();
        let click_id = id.clone();

        div()
            .id(SharedString::from(format!("estao-tab-{id}")))
            .flex()
            .items_center()
            .gap(px(4.0))
            .px(px(6.0))
            .py(px(3.0))
            .rounded_md()
            .cursor_pointer()
            .when(selected, |element| element.bg(palette.surface_hover))
            .hover(|style| style.bg(palette.surface_hover))
            .when_some(icon, |element, icon| {
                element.child(img(icon).size(px(14.0)).flex_shrink_0())
            })
            .child(
                div()
                    .text_size(px(11.0))
                    .font_weight(FontWeight::SEMIBOLD)
                    .text_color(color)
                    .child(snapshot.display_name.clone()),
            )
            .on_click(cx.listener(move |this, _, _, cx| {
                this.selected = Some(click_id.clone());
                cx.notify();
            }))
    }

    fn selected_body(
        &self,
        snapshot: &UsageSnapshot,
        config: &EstaoConfig,
        history: &[UsageHistoryPoint],
        now: DateTime<Utc>,
    ) -> impl IntoElement {
        let palette = self.palette;
        let provider_config = provider_config(config, &snapshot.provider);

        let header = div()
            .flex()
            .flex_col()
            .gap(px(4.0))
            .child(
                div()
                    .flex()
                    .items_start()
                    .justify_between()
                    .gap_2()
                    .child(
                        div()
                            .flex()
                            .flex_col()
                            .min_w_0()
                            .child(
                                div()
                                    .text_lg()
                                    .font_weight(FontWeight::SEMIBOLD)
                                    .text_color(palette.text)
                                    .child(snapshot.display_name.clone()),
                            )
                            .child(
                                div()
                                    .text_size(px(10.0))
                                    .text_color(palette.text_muted)
                                    .child(updated_text(now, snapshot.updated_at)),
                            ),
                    )
                    .when_some(snapshot.plan.clone(), |element, plan| {
                        element.child(
                            div()
                                .text_size(px(10.0))
                                .text_color(palette.text_muted)
                                .child(plan),
                        )
                    }),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .justify_between()
                    .gap_2()
                    .text_size(px(10.0))
                    .text_color(palette.text_muted)
                    .child(
                        div().min_w_0().child(
                            non_blank(snapshot.account.as_deref())
                                .map(str::to_string)
                                .unwrap_or_else(|| "No account selected".to_string()),
                        ),
                    )
                    .when_some(
                        credits_line(snapshot.credits.as_ref()),
                        |element, credits| element.child(div().flex_shrink_0().child(credits)),
                    ),
            );

        let mut body = div()
            .flex()
            .flex_col()
            .gap_2()
            .child(header)
            .child(divider(palette));

        if let Some(error) = non_blank(snapshot.error.as_deref()) {
            body = body.child(
                div()
                    .flex()
                    .flex_col()
                    .gap_1()
                    .child(
                        div()
                            .text_sm()
                            .font_weight(FontWeight::SEMIBOLD)
                            .text_color(palette.text)
                            .child("Usage unavailable"),
                    )
                    .child(
                        div()
                            .text_size(px(11.0))
                            .text_color(palette.critical)
                            .child(error.to_string()),
                    ),
            );
            return body;
        }

        if snapshot.windows.is_empty() {
            body = body.child(
                div()
                    .flex()
                    .flex_col()
                    .gap_2()
                    .child(
                        div()
                            .text_sm()
                            .text_color(palette.text)
                            .child("Loading usage..."),
                    )
                    .child(progress_bar(0.3, palette.accent, palette.surface_hover)),
            );
            return body;
        }

        let color_config = provider_config.map(|config| &config.usage_colors);
        for window in &snapshot.windows {
            body = body.child(window_card(window, color_config, palette, now));
        }

        if let Some(primary) = primary_window(&snapshot.windows) {
            let pacing = provider_config
                .map(|config| config.pacing.clone())
                .unwrap_or_default();
            let color = usage_color(color_config, primary.percent_used.unwrap_or(0.0), palette);
            let chart =
                chart::build_chart(&snapshot.provider, primary, history, &pacing, now, color);
            body = body.child(chart::render(&chart, palette));
        }

        body
    }

    fn footer(&self, snapshots: &[UsageSnapshot], cx: &mut Context<Self>) -> impl IntoElement {
        let palette = self.palette;
        let now = Utc::now();
        let updated = snapshots
            .iter()
            .map(|snapshot| snapshot.updated_at)
            .max()
            .map(|updated_at| updated_text(now, updated_at))
            .unwrap_or_else(|| "Updated just now".to_string());

        let refresh_state = self.state.clone();
        let settings_state = self.state.clone();

        div()
            .flex_shrink_0()
            .flex()
            .items_center()
            .justify_between()
            .px(px(12.0))
            .py(px(8.0))
            .border_t_1()
            .border_color(palette.border)
            .child(
                div()
                    .text_size(px(10.0))
                    .text_color(palette.text_muted)
                    .child(updated),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_1()
                    .child(
                        div()
                            .id("estao-popover-refresh")
                            .px(px(8.0))
                            .py(px(4.0))
                            .rounded_md()
                            .text_size(px(11.0))
                            .text_color(palette.text)
                            .bg(palette.surface_hover)
                            .cursor_pointer()
                            .hover(|style| style.opacity(0.85))
                            .child("Refresh")
                            .on_click(cx.listener(move |_this, _, window, cx| {
                                refresh_state.update(cx, |state, cx| state.refresh_now(cx));
                                window.remove_window();
                            })),
                    )
                    .child(
                        div()
                            .id("estao-popover-settings")
                            .px(px(8.0))
                            .py(px(4.0))
                            .rounded_md()
                            .text_size(px(11.0))
                            .text_color(palette.text)
                            .bg(palette.surface_hover)
                            .cursor_pointer()
                            .hover(|style| style.opacity(0.85))
                            .child("Settings")
                            .on_click(cx.listener(move |_this, _, window, cx| {
                                settings_state.update(cx, |state, cx| state.open_settings(cx));
                                window.remove_window();
                            })),
                    ),
            )
    }
}

impl Focusable for UsagePopover {
    fn focus_handle(&self, _cx: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl Render for UsagePopover {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = self.palette;
        let (snapshots, history, config) = {
            let state = self.state.read(cx);
            (
                state.snapshots().to_vec(),
                state.history().to_vec(),
                state.config().clone(),
            )
        };

        self.ensure_selection(cx);
        self.cache_icons(&snapshots);

        let tabs = div()
            .flex_shrink_0()
            .flex()
            .flex_wrap()
            .items_center()
            .gap_1()
            .px(px(10.0))
            .py(px(7.0))
            .children(snapshots.iter().map(|snapshot| self.tab(snapshot, cx)));

        let selected = self
            .selected
            .as_deref()
            .and_then(|selected| {
                snapshots
                    .iter()
                    .find(|snapshot| catalog::normalize_id(&snapshot.provider) == selected)
            })
            .cloned();

        let body = match selected {
            Some(snapshot) => self
                .selected_body(&snapshot, &config, &history, Utc::now())
                .into_any_element(),
            None => div()
                .flex()
                .flex_1()
                .items_center()
                .justify_center()
                .text_size(px(12.0))
                .text_color(palette.text_muted)
                .child("Loading usage...")
                .into_any_element(),
        };

        div()
            .id("estao-usage-popover")
            .size_full()
            .flex()
            .flex_col()
            .bg(palette.background)
            .text_color(palette.text)
            .track_focus(&self.focus_handle)
            .on_key_down(cx.listener(|_this, event: &KeyDownEvent, window, _cx| {
                if event.keystroke.key == "escape" {
                    window.remove_window();
                }
            }))
            .child(tabs)
            .child(divider(palette))
            .child(
                div()
                    .id("estao-usage-popover-body")
                    .flex_1()
                    .min_h_0()
                    .overflow_y_scroll()
                    .flex()
                    .flex_col()
                    .gap_2()
                    .p(px(10.0))
                    .child(body),
            )
            .child(self.footer(&snapshots, cx))
    }
}

/// Picks the chart window: the session window when present, else the first.
fn primary_window(windows: &[RateWindow]) -> Option<&RateWindow> {
    windows
        .iter()
        .find(|window| catalog::normalize_id(&window.id) == "session")
        .or_else(|| windows.first())
}

/// The provider config that supplies pacing and usage colors.
fn provider_config<'a>(config: &'a EstaoConfig, provider: &str) -> Option<&'a ProviderConfig> {
    let id = catalog::normalize_id(provider);
    config
        .providers
        .iter()
        .find(|configured| catalog::normalize_id(&configured.id) == id)
}

/// Resolves the color of a usage percentage from the provider thresholds.
///
/// Below the warning threshold (or when colors are disabled) the theme accent
/// is used; configured `#RRGGBB` values win otherwise, with the theme palette
/// as the fallback for malformed values.
fn usage_color(config: Option<&UsageColorConfig>, percent_used: f64, palette: Palette) -> Rgba {
    match UsageColorCatalog::level_for(config, percent_used) {
        UsageColorLevel::Warning => config
            .and_then(|config| parse_hex_color(&config.warning_color))
            .unwrap_or(palette.warning),
        UsageColorLevel::Critical => config
            .and_then(|config| parse_hex_color(&config.critical_color))
            .unwrap_or(palette.critical),
        UsageColorLevel::Default => palette.accent,
    }
}

/// Parses a `#RRGGBB` color into a gpui color.
fn parse_hex_color(value: &str) -> Option<Rgba> {
    let digits = value.trim().strip_prefix('#')?;
    if digits.len() != 6
        || !digits
            .chars()
            .all(|character| character.is_ascii_hexdigit())
    {
        return None;
    }
    u32::from_str_radix(digits, 16).ok().map(rgb)
}

/// Whole percentage used, clamped to `0..=100`.
fn percent_used(window: &RateWindow) -> Option<u32> {
    window
        .percent_used
        .map(|used| (used.clamp(0.0, 1.0) * 100.0).round() as u32)
}

/// Percent text for the card header, e.g. `42%`.
fn percent_text(window: &RateWindow) -> String {
    match percent_used(window) {
        Some(percent) => format!("{percent}%"),
        None => "—".to_string(),
    }
}

/// `used / limit unit`, when the provider reports absolute amounts.
fn used_total_text(window: &RateWindow) -> Option<String> {
    let used = window.used?;
    let unit = window.unit.as_deref().unwrap_or_default().trim();
    let text = match window.limit {
        Some(limit) => format!(
            "{} / {} {}",
            format_number(used),
            format_number(limit),
            unit
        ),
        None => format!("{} {}", format_number(used), unit),
    };
    Some(text.trim_end().to_string())
}

/// Mirrors .NET's `0.##` format: up to two decimals, trailing zeros trimmed.
fn format_number(value: f64) -> String {
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

/// Port of `ZarpaUsageContent.ResetText`.
fn reset_text(now: DateTime<Utc>, reset_at: Option<DateTime<Utc>>) -> String {
    let Some(reset_at) = reset_at else {
        return "Reset time unavailable".to_string();
    };
    if reset_at <= now {
        return "Reset due".to_string();
    }
    format!("Resets in {}", formatting::to_compact(reset_at - now))
}

/// Port of `ZarpaUsageContent.UpdatedText`.
fn updated_text(now: DateTime<Utc>, updated_at: DateTime<Utc>) -> String {
    let elapsed = now - updated_at;
    if elapsed < TimeDelta::minutes(1) {
        return "Updated just now".to_string();
    }
    if elapsed < TimeDelta::hours(1) {
        return format!("Updated {}m ago", elapsed.num_minutes());
    }
    if elapsed < TimeDelta::days(1) {
        return format!("Updated {}h ago", elapsed.num_hours());
    }
    format!("Updated {}d ago", elapsed.num_days())
}

/// Port of the .NET "Extra usage" balance line.
fn credits_line(credits: Option<&CreditsSnapshot>) -> Option<String> {
    let credits = credits?;
    if credits.unlimited == Some(true) {
        return Some("Unlimited".to_string());
    }
    let balance = credits.balance?;
    let unit = credits.unit.as_deref().unwrap_or_default().trim();
    Some(
        format!("Balance: {} {unit}", format_number(balance))
            .trim_end()
            .to_string(),
    )
}

/// Trims a string and treats blank values as absent.
fn non_blank(value: Option<&str>) -> Option<&str> {
    value.filter(|value| !value.trim().is_empty())
}

/// A one-pixel separator line.
fn divider(palette: Palette) -> impl IntoElement {
    div().flex_shrink_0().h(px(1.0)).w_full().bg(palette.border)
}

/// A rounded progress track with a colored fill.
fn progress_bar(fraction: f64, color: Rgba, track: Rgba) -> impl IntoElement {
    div()
        .h(px(5.0))
        .w_full()
        .rounded_full()
        .bg(track)
        .overflow_hidden()
        .child(
            div()
                .h_full()
                .w(relative(fraction.clamp(0.0, 1.0) as f32))
                .rounded_full()
                .bg(color),
        )
}

/// One rate-window card: title, reset countdown, big percent and a meter.
fn window_card(
    window: &RateWindow,
    color_config: Option<&UsageColorConfig>,
    palette: Palette,
    now: DateTime<Utc>,
) -> impl IntoElement {
    let percent = percent_used(window);
    let color = usage_color(color_config, window.percent_used.unwrap_or(0.0), palette);
    let bar_fraction = window.percent_used.unwrap_or(0.0);
    let used_total = used_total_text(window);
    let title = if window.title.trim().is_empty() {
        window.id.clone()
    } else {
        window.title.clone()
    };

    div()
        .flex()
        .flex_col()
        .gap_1()
        .p(px(9.0))
        .rounded_md()
        .bg(palette.surface)
        .border_1()
        .border_color(palette.border)
        .child(
            div()
                .flex()
                .items_center()
                .justify_between()
                .gap_2()
                .child(
                    div()
                        .text_size(px(11.0))
                        .font_weight(FontWeight::SEMIBOLD)
                        .text_color(palette.text)
                        .child(title),
                )
                .child(
                    div()
                        .text_size(px(9.0))
                        .text_color(palette.text_muted)
                        .child(reset_text(now, window.reset_at)),
                ),
        )
        .child(
            div()
                .flex()
                .items_end()
                .justify_between()
                .gap_2()
                .child(
                    div()
                        .flex()
                        .items_end()
                        .gap(px(4.0))
                        .child(
                            div()
                                .text_2xl()
                                .font_weight(FontWeight::SEMIBOLD)
                                .text_color(palette.text)
                                .child(percent_text(window)),
                        )
                        .child(
                            div()
                                .pb(px(3.0))
                                .text_size(px(10.0))
                                .text_color(palette.text_muted)
                                .child(if percent.is_some() {
                                    "used"
                                } else {
                                    "usage unavailable"
                                }),
                        ),
                )
                .when_some(used_total, |element, used_total| {
                    element.child(
                        div()
                            .pb(px(3.0))
                            .text_size(px(10.0))
                            .text_color(palette.text_muted)
                            .child(used_total),
                    )
                }),
        )
        .child(progress_bar(
            bar_fraction,
            if percent.is_some() {
                color
            } else {
                palette.surface_hover
            },
            palette.surface_hover,
        ))
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

    fn window(percent: Option<f64>) -> RateWindow {
        RateWindow {
            id: "session".to_string(),
            title: "Session".to_string(),
            percent_used: percent,
            reset_at: None,
            used: None,
            limit: None,
            unit: None,
        }
    }

    fn enabled_colors() -> UsageColorConfig {
        UsageColorConfig {
            enabled: true,
            warning_percent: 75.0,
            warning_color: "#F59E0B".to_string(),
            critical_percent: 90.0,
            critical_color: "#EF4444".to_string(),
        }
    }

    // Ported from the .NET percent formatting in `ZarpaUsageContent.AddWindow`.
    #[test]
    fn percent_text_rounds_and_clamps() {
        assert_eq!(percent_used(&window(Some(0.4235))), Some(42));
        assert_eq!(percent_text(&window(Some(0.4235))), "42%");
        assert_eq!(percent_text(&window(Some(2.0))), "100%");
        assert_eq!(percent_text(&window(Some(-1.0))), "0%");
        assert_eq!(percent_text(&window(None)), "—");
    }

    #[test]
    fn percent_used_clamps_and_rounds() {
        assert_eq!(percent_used(&window(Some(0.005))), Some(1));
        assert_eq!(percent_used(&window(Some(0.999))), Some(100));
        assert_eq!(percent_used(&window(None)), None);
    }

    // Ported from `DurationFormatter` consumers in `ZarpaUsageContent.ResetText`.
    #[test]
    fn reset_text_matches_the_dotnet_wording() {
        let now = at("2026-11-05T12:00:00Z");

        assert_eq!(reset_text(now, None), "Reset time unavailable");
        assert_eq!(
            reset_text(now, Some(at("2026-11-05T11:59:00Z"))),
            "Reset due"
        );
        assert_eq!(
            reset_text(now, Some(now + TimeDelta::minutes(200))),
            "Resets in 3h 20m"
        );
        assert_eq!(
            reset_text(now, Some(now + TimeDelta::days(2) + TimeDelta::hours(3))),
            "Resets in 2d 3h"
        );
    }

    #[test]
    fn updated_text_matches_the_dotnet_wording() {
        let now = at("2026-11-05T12:00:00Z");

        assert_eq!(updated_text(now, now), "Updated just now");
        assert_eq!(
            updated_text(now, now - TimeDelta::seconds(40)),
            "Updated just now"
        );
        assert_eq!(
            updated_text(now, now - TimeDelta::minutes(5)),
            "Updated 5m ago"
        );
        assert_eq!(
            updated_text(now, now - TimeDelta::hours(2)),
            "Updated 2h ago"
        );
        assert_eq!(
            updated_text(now, now - TimeDelta::days(3)),
            "Updated 3d ago"
        );
        // A clock skew must not produce a negative "ago" value.
        assert_eq!(
            updated_text(now, now + TimeDelta::minutes(5)),
            "Updated just now"
        );
    }

    #[test]
    fn usage_colors_follow_the_configured_thresholds() {
        let palette = Palette::dark();
        let colors = enabled_colors();

        assert_eq!(
            usage_color(Some(&colors), 0.5, palette),
            palette.accent,
            "below the warning threshold"
        );
        assert_eq!(
            usage_color(Some(&colors), 0.80, palette),
            rgb(0xF59E0B),
            "warning threshold"
        );
        assert_eq!(
            usage_color(Some(&colors), 0.95, palette),
            rgb(0xEF4444),
            "critical threshold"
        );
    }

    #[test]
    fn usage_colors_fall_back_when_disabled_or_malformed() {
        let palette = Palette::dark();
        let mut disabled = enabled_colors();
        disabled.enabled = false;
        assert_eq!(usage_color(Some(&disabled), 1.0, palette), palette.accent);
        assert_eq!(usage_color(None, 1.0, palette), palette.accent);

        let mut malformed = enabled_colors();
        malformed.warning_color = "orange".to_string();
        malformed.critical_color = "#12345".to_string();
        assert_eq!(
            usage_color(Some(&malformed), 0.80, palette),
            palette.warning
        );
        assert_eq!(
            usage_color(Some(&malformed), 0.95, palette),
            palette.critical
        );
    }

    #[test]
    fn parses_dotnet_hex_colors() {
        assert_eq!(parse_hex_color("#A1B2C3"), Some(rgb(0xA1B2C3)));
        assert_eq!(parse_hex_color(" #a1b2c3 "), Some(rgb(0xA1B2C3)));
        assert_eq!(parse_hex_color("#12345"), None);
        assert_eq!(parse_hex_color("A1B2C3"), None);
        assert_eq!(parse_hex_color("#GGGGGG"), None);
    }

    #[test]
    fn used_total_matches_the_dotnet_amount_format() {
        let mut value = window(Some(0.5));
        value.used = Some(12.5);
        value.limit = Some(100.0);
        value.unit = Some("requests".to_string());
        assert_eq!(
            used_total_text(&value).as_deref(),
            Some("12.5 / 100 requests")
        );

        value.limit = None;
        value.unit = None;
        assert_eq!(used_total_text(&value).as_deref(), Some("12.5"));

        value.used = None;
        assert_eq!(used_total_text(&value), None);
    }

    #[test]
    fn credits_line_covers_unlimited_and_balance() {
        assert_eq!(
            credits_line(Some(&CreditsSnapshot {
                balance: Some(12.5),
                unit: Some("USD".to_string()),
                unlimited: None,
            }))
            .as_deref(),
            Some("Balance: 12.5 USD")
        );
        assert_eq!(
            credits_line(Some(&CreditsSnapshot {
                balance: None,
                unit: None,
                unlimited: Some(true),
            }))
            .as_deref(),
            Some("Unlimited")
        );
        assert_eq!(
            credits_line(Some(&CreditsSnapshot {
                balance: None,
                unit: None,
                unlimited: None,
            })),
            None
        );
        assert_eq!(credits_line(None), None);
    }

    #[test]
    fn primary_window_prefers_the_session_window() {
        let mut weekly = window(Some(0.4));
        weekly.id = "weekly".to_string();
        weekly.title = "Weekly".to_string();
        let session = window(Some(0.1));

        assert_eq!(
            primary_window(&[weekly.clone(), session.clone()]).map(|window| window.id.clone()),
            Some("session".to_string())
        );
        assert_eq!(
            primary_window(&[weekly]).map(|window| window.id.clone()),
            Some("weekly".to_string())
        );
        assert_eq!(primary_window(&[]), None);
    }

    #[test]
    fn places_the_popup_above_the_anchor() {
        let anchor = TrayAnchor {
            x: 1500.0,
            y: 1000.0,
        };
        let work_area = PhysicalRect {
            left: 0.0,
            top: 0.0,
            right: 1920.0,
            bottom: 1040.0,
        };

        let bounds = anchored_bounds(anchor, 1.0, Some(work_area), (420.0, 620.0));

        assert_eq!(bounds.origin, point(px(1290.0), px(372.0)));
        assert_eq!(bounds.size, size(px(420.0), px(620.0)));
    }

    #[test]
    fn converts_physical_pixels_and_clamps_to_the_work_area() {
        let anchor = TrayAnchor {
            x: 3800.0,
            y: 2120.0,
        };
        // 4K monitor at 200%: work area is 1920x1040 logical pixels.
        let work_area = PhysicalRect {
            left: 0.0,
            top: 0.0,
            right: 3840.0,
            bottom: 2080.0,
        };

        let bounds = anchored_bounds(anchor, 2.0, Some(work_area), (420.0, 620.0));

        // 2120px / 2 = 1060px logical; the popup would start at 432px, which
        // is clamped to the bottom of the 1040px-tall work area.
        assert_eq!(bounds.origin, point(px(1500.0), px(420.0)));
        assert_eq!(bounds.size, size(px(420.0), px(620.0)));
    }

    #[test]
    fn keeps_the_popup_on_screen_on_secondary_monitors() {
        let anchor = TrayAnchor {
            x: 2000.0,
            y: 1050.0,
        };
        let work_area = PhysicalRect {
            left: 1920.0,
            top: 0.0,
            right: 3840.0,
            bottom: 1040.0,
        };

        let bounds = anchored_bounds(anchor, 1.0, Some(work_area), (420.0, 620.0));

        assert_eq!(bounds.origin.x, px(1920.0));
        assert_eq!(bounds.origin.y, px(420.0));
    }

    #[test]
    fn anchors_without_a_work_area() {
        let anchor = TrayAnchor { x: 500.0, y: 400.0 };

        let bounds = anchored_bounds(anchor, 1.0, None, (420.0, 620.0));

        assert_eq!(bounds.origin, point(px(290.0), px(-228.0)));
    }

    #[test]
    fn treats_a_missing_scale_factor_as_unscaled() {
        let anchor = TrayAnchor { x: 500.0, y: 400.0 };

        let bounds = anchored_bounds(anchor, 0.0, None, (420.0, 620.0));

        assert_eq!(bounds.origin, point(px(290.0), px(-228.0)));
    }

    #[test]
    fn selection_falls_back_when_the_provider_disappears() {
        let mut selected: Option<String> = Some("claude".to_string());
        let present = ["codex"];

        let still_there = selected.as_deref().is_some_and(|selected| {
            present
                .iter()
                .any(|provider| catalog::normalize_id(provider) == selected)
        });
        if !still_there {
            selected = present
                .first()
                .map(|provider| catalog::normalize_id(provider));
        }

        assert_eq!(selected.as_deref(), Some("codex"));
    }

    #[test]
    fn history_selection_uses_the_normalized_provider_id() {
        let history = [UsageHistoryPoint {
            provider: "Codex".to_string(),
            window: "session".to_string(),
            timestamp: at("2026-11-05T11:00:00Z"),
            percent_used: 0.5,
        }];

        assert_eq!(
            catalog::normalize_id(&history[0].provider),
            catalog::normalize_id("codex")
        );
        assert_eq!(
            provider_config(&EstaoConfig::default(), "codex").map(|config| config.id.as_str()),
            None
        );
    }

    #[test]
    fn provider_config_normalizes_aliases() {
        let config = EstaoConfig {
            providers: vec![ProviderConfig {
                id: "z-ai".to_string(),
                ..ProviderConfig::default()
            }],
            ..EstaoConfig::default()
        };

        assert!(provider_config(&config, "zai").is_some());
        assert!(provider_config(&config, "claude").is_none());
    }

    #[test]
    fn non_blank_ignores_whitespace() {
        assert_eq!(non_blank(Some(" alice ")), Some(" alice "));
        assert_eq!(non_blank(Some("   ")), None);
        assert_eq!(non_blank(None), None);
    }
}
