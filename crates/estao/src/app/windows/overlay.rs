//! The taskbar usage overlay window.
//!
//! A small borderless always-on-top no-activate window painted over the
//! taskbar's unused area: compact provider tiles with percentage and usage
//! bars; hides when the taskbar cannot accommodate it; draggable with
//! persisted position; clicking a provider toggles its usage popover.
//! Ported from `src/Hmy.Estao.App/Controls/Zarpa/TaskbarUsageOverlay.cs`.
//!
//! GPUI owns the painting, so the layout is a row of divs instead of the
//! WinForms `OnPaint` loop. Placement, z-order and the manual drag still go
//! through Win32 (`Shell_TrayWnd` geometry and `SetWindowPos`), because GPUI
//! 0.3 cannot move an existing window or make it topmost.
//!
//! Documented deviations from the .NET control:
//! - Pointer movement has no visual effect and never opens the popover.
//! - Compact GPUI tiles replace the legacy icon/title/donut/reset layout;
//!   detailed reset times are shown in the click popover.
//! - Click-through hit-testing (`WM_NCHITTEST`/`HTTRANSPARENT`) is omitted, so
//!   clicks inside the small unused-taskbar pill are captured instead of
//!   reaching the taskbar.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use chrono::{DateTime, TimeDelta, Utc};
use estao_core::catalog;
use estao_core::config::{
    EstaoConfig, OverlayConfig, ProviderConfig, UsageColorCatalog, UsageColorLevel,
};
use estao_core::formatting;
use estao_core::models::UsageSnapshot;
use gpui::{
    div, img, point, prelude::*, px, rgb, size, Bounds, ColorExt, Context, Entity, FontWeight,
    MouseButton, MouseDownEvent, Pixels, Render, RenderImage, Rgba, SharedString, Subscription,
    Window,
};

use crate::app::icons;
use crate::app::state::AppState;
use crate::app::theme::Theme;
use crate::app::windows::popover;

/// Height of the compact overlay, in logical pixels.
pub const COMPACT_OVERLAY_HEIGHT: i32 = 34;
/// Height of the standard overlay, in logical pixels.
pub const STANDARD_OVERLAY_HEIGHT: i32 = 38;
/// Height of the spacious overlay, in logical pixels.
pub const SPACIOUS_OVERLAY_HEIGHT: i32 = 46;

/// Horizontal taskbar space kept free on the left of the overlay.
const TASKBAR_RESERVED_LEFT: i32 = 120;
/// Gap between the taskbar right edge and the overlay fallback.
const TASKBAR_RIGHT_INSET: i32 = 12;
/// Gap used by the Windows 11 fallback when the tray rectangle is unusable.
const TASKBAR_FALLBACK_INSET: i32 = 10;
/// Gap between the tray rectangle and the overlay.
const TASKBAR_GAP: i32 = 6;

/// Width of the drag handle, in logical pixels.
const DRAG_HANDLE_WIDTH: i32 = 18;
/// Horizontal padding between the drag handle and the providers.
const OUTER_PADDING: i32 = 7;
/// Horizontal padding inside a provider row.
const PROVIDER_PADDING: i32 = 6;
/// Extra provider padding used by the spacious size.
const PROVIDER_PADDING_SPACIOUS: i32 = 5;
/// Gap between provider rows.
const PROVIDER_GAP: i32 = 10;
/// Provider icon edge length, in logical pixels.
const PROVIDER_ICON_SIZE: i32 = 16;
/// Rasterization size of the provider icons, in physical pixels.
const OVERLAY_ICON_RASTER: u32 = 48;
/// Overlay font size, in logical pixels (8.5 pt at 96 DPI).
const OVERLAY_FONT_SIZE: f32 = 11.0;

/// The three overlay sizes offered by the v2 schema.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum OverlaySize {
    /// Smallest pill.
    Compact,
    /// .NET default.
    #[default]
    Normal,
    /// Roomiest pill.
    Spacious,
}

impl OverlaySize {
    /// Parses the persisted `size` value, defaulting to [`OverlaySize::Normal`].
    pub fn parse(value: &str) -> Self {
        match value.trim().to_ascii_lowercase().as_str() {
            "compact" => Self::Compact,
            "spacious" => Self::Spacious,
            _ => Self::Normal,
        }
    }

    /// The overlay height for this size, in logical pixels.
    pub fn height(self) -> i32 {
        match self {
            Self::Compact => COMPACT_OVERLAY_HEIGHT,
            Self::Normal => STANDARD_OVERLAY_HEIGHT,
            Self::Spacious => SPACIOUS_OVERLAY_HEIGHT,
        }
    }

    /// Horizontal padding inside a provider row.
    fn provider_padding(self) -> i32 {
        PROVIDER_PADDING
            + if self == Self::Spacious {
                PROVIDER_PADDING_SPACIOUS
            } else {
                0
            }
    }
}

/// One provider row of the overlay.
#[derive(Debug, Clone, PartialEq)]
pub struct OverlayRow {
    /// Normalized provider id.
    pub provider: String,
    /// User-facing provider name.
    pub display_name: String,
    /// Fraction of the primary window already consumed (`0.0` - `1.0`).
    pub percent_used: Option<f64>,
    /// Compact reset countdown, when the provider reports a reset time.
    pub reset_text: Option<String>,
}

impl OverlayRow {
    /// Mirrors .NET's `P0` percentage, with an em dash when unavailable.
    pub fn percent_text(&self) -> String {
        match self.percent_used {
            Some(used) => format!("{}%", (used.clamp(0.0, 1.0) * 100.0).round() as i64),
            None => "—".to_string(),
        }
    }
}

/// A rectangle in physical screen pixels.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct PhysicalRect {
    /// Left edge.
    pub left: i32,
    /// Top edge.
    pub top: i32,
    /// Width.
    pub width: i32,
    /// Height.
    pub height: i32,
}

impl PhysicalRect {
    /// Builds a rectangle from its top-left corner and size.
    pub fn new(left: i32, top: i32, width: i32, height: i32) -> Self {
        Self {
            left,
            top,
            width,
            height,
        }
    }

    /// The right edge (exclusive).
    pub fn right(self) -> i32 {
        self.left + self.width
    }

    /// The bottom edge (exclusive).
    pub fn bottom(self) -> i32 {
        self.top + self.height
    }

    pub fn contains(self, x: i32, y: i32) -> bool {
        x >= self.left && x < self.right() && y >= self.top && y < self.bottom()
    }

    /// The center point.
    pub fn center(self) -> (i32, i32) {
        (self.left + self.width / 2, self.top + self.height / 2)
    }
}

/// A computed overlay placement plus the DPI scale of its monitor.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct OverlayPlacement {
    /// Window rectangle in physical pixels.
    pub rect: PhysicalRect,
    /// Physical pixels per logical pixel.
    pub scale: f32,
}

impl OverlayPlacement {
    /// Converts the placement into the logical bounds GPUI expects.
    pub fn logical_bounds(self) -> Bounds<Pixels> {
        let scale = if self.scale > 0.0 { self.scale } else { 1.0 };
        Bounds::new(
            point(
                px(self.rect.left as f32 / scale),
                px(self.rect.top as f32 / scale),
            ),
            size(
                px(self.rect.width as f32 / scale),
                px(self.rect.height as f32 / scale),
            ),
        )
    }
}

/// Providers shown by the overlay for the current snapshots and selection.
///
/// Mirrors `TaskbarUsageOverlay.VisibleSnapshots`: providers disabled in
/// Settings never appear; an empty `providerIds` means every enabled provider
/// with a successful snapshot; otherwise the saved selection wins, limited to
/// four rows. A configured selection with no snapshot yet yields `waiting`
/// rows so the rail stays visible during the first refresh.
pub fn visible_rows(
    snapshots: &[UsageSnapshot],
    overlay: &OverlayConfig,
    providers: &[ProviderConfig],
    now: DateTime<Utc>,
) -> Vec<OverlayRow> {
    let enabled: HashSet<String> = providers
        .iter()
        .filter(|provider| provider.enabled == Some(true))
        .map(|provider| catalog::normalize_id(&provider.id))
        .collect();

    let configured: Vec<String> = overlay
        .provider_ids
        .iter()
        .map(|id| catalog::normalize_id(id))
        .filter(|id| enabled.contains(id))
        .collect();

    let selected: Vec<&UsageSnapshot> = if configured.is_empty() {
        snapshots
            .iter()
            .filter(|snapshot| {
                enabled.contains(&catalog::normalize_id(&snapshot.provider))
                    && snapshot.error.is_none()
                    && !snapshot.windows.is_empty()
            })
            .collect()
    } else {
        snapshots
            .iter()
            .filter(|snapshot| configured.contains(&catalog::normalize_id(&snapshot.provider)))
            .collect()
    };

    let rows: Vec<OverlayRow> = selected
        .into_iter()
        .take(4)
        .map(|snapshot| row_from_snapshot(snapshot, now))
        .collect();
    if !rows.is_empty() || configured.is_empty() {
        return rows;
    }

    configured
        .into_iter()
        .take(4)
        .map(|provider| OverlayRow {
            display_name: catalog::display_name(&provider),
            provider,
            percent_used: None,
            reset_text: None,
        })
        .collect()
}

/// Builds one overlay row from a snapshot.
fn row_from_snapshot(snapshot: &UsageSnapshot, now: DateTime<Utc>) -> OverlayRow {
    let window = snapshot.windows.first();
    let display_name = if snapshot.display_name.trim().is_empty() {
        catalog::display_name(&snapshot.provider)
    } else {
        snapshot.display_name.clone()
    };
    OverlayRow {
        provider: catalog::normalize_id(&snapshot.provider),
        display_name,
        percent_used: window.and_then(|window| window.percent_used),
        reset_text: reset_text(window.and_then(|window| window.reset_at), now),
    }
}

/// Port of `TaskbarUsageOverlay.ResetText`.
fn reset_text(reset_at: Option<DateTime<Utc>>, now: DateTime<Utc>) -> Option<String> {
    let reset_at = reset_at?;
    let remaining = reset_at - now;
    if remaining <= TimeDelta::zero() {
        Some("Reset due".to_string())
    } else {
        Some(formatting::to_compact(remaining))
    }
}

/// Width of one provider row, in logical pixels.
fn row_width(_row: &OverlayRow, size: OverlaySize) -> i32 {
    size.provider_padding() * 2 + PROVIDER_ICON_SIZE + 8 + 96
}

/// Total overlay width, in logical pixels.
pub fn overlay_width(rows: &[OverlayRow], size: OverlaySize) -> i32 {
    if rows.is_empty() {
        return 0;
    }
    2 + DRAG_HANDLE_WIDTH
        + OUTER_PADDING * 2
        + rows.iter().map(|row| row_width(row, size)).sum::<i32>()
        + PROVIDER_GAP * (rows.len() as i32 - 1)
}

/// Places the overlay in the taskbar's unused area.
///
/// Mirrors `TaskbarUsageOverlay.TryPlace`: prefer the left edge of the
/// notification area, keep [`TASKBAR_RESERVED_LEFT`] free on the left, and
/// fall back to the taskbar's right edge when the tray rectangle is unusable.
pub fn place_against_taskbar(
    taskbar: PhysicalRect,
    tray: Option<PhysicalRect>,
    width: i32,
    height: i32,
) -> Option<PhysicalRect> {
    if taskbar.height <= 0 || taskbar.height >= taskbar.width {
        return None;
    }

    let mut right = taskbar.right() - TASKBAR_RIGHT_INSET;
    if let Some(tray) = tray {
        right = tray.left - TASKBAR_GAP;
    }

    let mut left = right - width;
    if left < taskbar.left + TASKBAR_RESERVED_LEFT || width > taskbar.width - TASKBAR_RESERVED_LEFT
    {
        right = taskbar.right() - TASKBAR_FALLBACK_INSET;
        left = right - width;
        if left < taskbar.left + 12 || width > taskbar.width - 24 {
            return None;
        }
    }

    Some(PhysicalRect::new(
        left,
        taskbar.top + ((taskbar.height - height) / 2).max(0),
        width,
        height,
    ))
}

/// Keeps `placement` inside `bounds`, preserving its size.
pub fn constrain_to_monitor(placement: PhysicalRect, bounds: PhysicalRect) -> PhysicalRect {
    let max_x = (bounds.right() - placement.width).max(bounds.left);
    let max_y = (bounds.bottom() - placement.height).max(bounds.top);
    PhysicalRect::new(
        placement.left.clamp(bounds.left, max_x),
        placement.top.clamp(bounds.top, max_y),
        placement.width,
        placement.height,
    )
}

/// Computes where the overlay should be, if it has anything to show.
pub fn desired_placement(
    config: &OverlayConfig,
    snapshots: &[UsageSnapshot],
    providers: &[ProviderConfig],
    now: DateTime<Utc>,
) -> Option<OverlayPlacement> {
    let rows = visible_rows(snapshots, config, providers, now);
    placement_for_rows(config, &rows)
}

/// Computes the placement for already-built rows.
pub fn placement_for_rows(config: &OverlayConfig, rows: &[OverlayRow]) -> Option<OverlayPlacement> {
    if !config.enabled || rows.is_empty() {
        return None;
    }

    let size = OverlaySize::parse(&config.size);
    win::place(config, overlay_width(rows, size), size.height())
}

/// The strip uses the same theme tokens as the other app surfaces.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TaskbarPalette {
    /// Card background.
    pub surface: Rgba,
    /// Border and separator color.
    pub border: Rgba,
    /// Primary text color.
    pub text: Rgba,
    /// Secondary text color.
    pub text_muted: Rgba,
    /// Accent used below the warning threshold.
    pub accent: Rgba,
    /// `0xRRGGBB` form of [`TaskbarPalette::text`], used to tint icons.
    pub text_hex: u32,
}

impl TaskbarPalette {
    /// Reuses the same design tokens as settings and the usage popover.
    pub fn from_theme(theme: Theme) -> Self {
        let palette = theme.palette();
        Self {
            surface: palette.surface,
            border: palette.border,
            text: palette.text,
            text_muted: palette.text_muted,
            accent: palette.accent,
            text_hex: rgba_hex(palette.text),
        }
    }
}

impl Default for TaskbarPalette {
    fn default() -> Self {
        Self::from_theme(Theme::default())
    }
}

/// Converts a gpui color into a `0xRRGGBB` value.
fn rgba_hex(color: Rgba) -> u32 {
    let channel = |value: f32| (value.clamp(0.0, 1.0) * 255.0).round() as u32;
    (channel(color.red) << 16) | (channel(color.green) << 8) | channel(color.blue)
}

/// Warn/critical override used by the usage bar, falling back to the accent.
fn indicator_color(
    config: &EstaoConfig,
    provider: &str,
    percent_used: Option<f64>,
    accent: Rgba,
) -> Rgba {
    let Some(percent_used) = percent_used else {
        return accent;
    };
    let colors = config
        .providers
        .iter()
        .find(|configured| catalog::normalize_id(&configured.id) == provider)
        .map(|configured| &configured.usage_colors);
    match UsageColorCatalog::level_for(colors, percent_used) {
        UsageColorLevel::Warning => colors
            .and_then(|colors| parse_hex_color(&colors.warning_color))
            .unwrap_or(accent),
        UsageColorLevel::Critical => colors
            .and_then(|colors| parse_hex_color(&colors.critical_color))
            .unwrap_or(accent),
        UsageColorLevel::Default => accent,
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

/// The taskbar overlay view.
pub struct TaskbarOverlay {
    state: Entity<AppState>,
    palette: TaskbarPalette,
    rows: Vec<OverlayRow>,
    row_widths: Vec<i32>,
    icons: HashMap<String, Arc<RenderImage>>,
    size: OverlaySize,
    placement: Option<PhysicalRect>,
    scale: f32,
    hwnd: Option<isize>,
    dragging: bool,
    drag_offset: (i32, i32),
    drag_start: (i32, i32),
    drag_rect: PhysicalRect,
    _state_subscription: Subscription,
}

impl TaskbarOverlay {
    /// Builds the overlay around the shared application state.
    pub fn new(state: Entity<AppState>, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let subscription = cx.observe(&state, |_, _, cx| cx.notify());
        let hwnd = win::hwnd_from_window(window);
        if let Some(hwnd) = hwnd {
            win::apply_overlay_styles(hwnd);
        }
        let palette = TaskbarPalette::from_theme(state.read(cx).theme());
        let mut overlay = Self {
            state,
            palette,
            rows: Vec::new(),
            row_widths: Vec::new(),
            icons: HashMap::new(),
            size: OverlaySize::default(),
            placement: None,
            scale: 1.0,
            hwnd,
            dragging: false,
            drag_offset: (0, 0),
            drag_start: (0, 0),
            drag_rect: PhysicalRect::default(),
            _state_subscription: subscription,
        };
        overlay.refresh(window, cx);
        overlay
    }

    /// Recomputes the rows, size and position, then shows or hides the window.
    pub fn refresh(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self.dragging {
            return;
        }
        if self.hwnd.is_none() {
            self.hwnd = win::hwnd_from_window(window);
            if let Some(hwnd) = self.hwnd {
                win::apply_overlay_styles(hwnd);
            }
        }

        let (config, snapshots) = {
            let state = self.state.read(cx);
            (state.config().clone(), state.snapshots().to_vec())
        };

        let size = OverlaySize::parse(&config.overlay.size);
        let rows = if config.overlay.enabled {
            visible_rows(&snapshots, &config.overlay, &config.providers, Utc::now())
        } else {
            Vec::new()
        };

        let placement = if rows.is_empty() {
            None
        } else {
            placement_for_rows(&config.overlay, &rows)
        };
        let Some(placement) = placement else {
            if self.placement.is_none() {
                return;
            }
            self.rows.clear();
            self.row_widths.clear();
            self.icons.clear();
            self.placement = None;
            self.dragging = false;
            window.set_visible(false);
            cx.notify();
            return;
        };

        let palette = TaskbarPalette::from_theme(Theme::from_name(&config.theme));
        let previous = self.placement;
        let changed = self.rows != rows || self.size != size || self.palette != palette;
        if palette.text_hex != self.palette.text_hex {
            self.icons.clear();
        }
        self.palette = palette;
        self.size = size;
        self.scale = placement.scale;
        self.placement = Some(placement.rect);
        self.row_widths = rows.iter().map(|row| row_width(row, size)).collect();
        self.rows = rows;
        self.cache_icons();

        if previous != Some(placement.rect) {
            if let Some(hwnd) = self.hwnd {
                let show = previous.is_none();
                cx.foreground_executor()
                    .spawn(async move {
                        win::set_placement(hwnd, placement.rect, show);
                    })
                    .detach();
            }
        }
        if changed || previous != Some(placement.rect) {
            cx.notify();
        }
    }

    /// Lazily rasterizes the provider icons with the taskbar text tint.
    fn cache_icons(&mut self) {
        for row in &self.rows {
            if self.icons.contains_key(&row.provider) {
                continue;
            }
            if let Some(image) = icons::provider_icon_image_with_tint(
                &row.provider,
                OVERLAY_ICON_RASTER,
                Some(self.palette.text_hex),
            ) {
                self.icons.insert(row.provider.clone(), image);
            }
        }
    }

    /// Starts the manual drag. GPUI has no cross-window move loop, so the
    /// position is driven by polling the physical mouse button and cursor.
    fn start_drag(&mut self, event: &MouseDownEvent, window: &mut Window, cx: &mut Context<Self>) {
        if event.button != MouseButton::Left || self.rows.is_empty() {
            return;
        }

        let hwnd = self.hwnd.or_else(|| win::hwnd_from_window(window));
        let Some(hwnd) = hwnd else {
            return;
        };
        let (Some(cursor), Some(rect)) = (win::cursor_position(), win::window_rect(hwnd)) else {
            return;
        };

        self.hwnd = Some(hwnd);
        self.dragging = true;
        self.drag_offset = (cursor.0 - rect.left, cursor.1 - rect.top);
        self.drag_start = (rect.left, rect.top);
        self.drag_rect = rect;
        cx.notify();

        self.schedule_drag_frame(window, cx);
    }

    fn schedule_drag_frame(&self, window: &Window, cx: &Context<Self>) {
        let this = cx.weak_entity();
        window.on_next_frame(move |window, cx| {
            let _ = this.update(cx, |overlay, cx| {
                if overlay.poll_drag(cx) {
                    overlay.schedule_drag_frame(window, cx);
                }
            });
        });
    }

    /// Advances the drag; returns whether the button is still down.
    fn poll_drag(&mut self, cx: &mut Context<Self>) -> bool {
        if !self.dragging {
            return false;
        }
        if !win::left_button_down() {
            self.finish_drag(cx);
            return false;
        }

        let (Some(cursor), Some(hwnd)) = (win::cursor_position(), self.hwnd) else {
            self.finish_drag(cx);
            return false;
        };

        let desired = PhysicalRect::new(
            cursor.0 - self.drag_offset.0,
            cursor.1 - self.drag_offset.1,
            self.drag_rect.width,
            self.drag_rect.height,
        );
        let placed = win::constrain(desired);
        if placed.left != self.drag_rect.left || placed.top != self.drag_rect.top {
            // A monitor change sends synchronous Win32 callbacks into GPUI.
            // Move only after the current App borrow has been released.
            cx.foreground_executor()
                .spawn(async move {
                    win::set_placement(hwnd, placed, false);
                })
                .detach();
            self.drag_rect = placed;
            self.placement = Some(placed);
        }
        true
    }

    /// Stops the drag and persists the new position when it moved.
    fn finish_drag(&mut self, cx: &mut Context<Self>) {
        self.dragging = false;
        let position = (self.drag_rect.left, self.drag_rect.top);
        if position != self.drag_start {
            let state = self.state.clone();
            state.update(cx, |state, cx| {
                state.set_overlay_position(position.0, position.1, cx)
            });
        }
        cx.notify();
    }

    /// Toggle only on an explicit click; pointer movement never opens a window.
    fn click_provider(&mut self, index: usize, cx: &mut Context<Self>) {
        if self.dragging {
            return;
        }
        let Some(row) = self.rows.get(index) else {
            return;
        };
        let Some(placement) = self.placement else {
            return;
        };
        let provider = row.provider.clone();
        let offset: i32 = (1 + DRAG_HANDLE_WIDTH + OUTER_PADDING)
            + self.row_widths[..index]
                .iter()
                .map(|width| width + PROVIDER_GAP)
                .sum::<i32>();
        let center_x = placement.left as f32
            + (offset as f32 + self.row_widths[index] as f32 / 2.0) * self.scale;
        let anchor = popover::TrayAnchor {
            x: center_x,
            y: placement.top as f32,
            height: placement.height as f32,
        };
        self.state.update(cx, |state, cx| {
            state.toggle_provider_popover(provider, anchor, cx)
        });
    }

    pub fn contains_pointer(&self) -> bool {
        let Some(hwnd) = self.hwnd else { return false };
        if !win::is_visible(hwnd) {
            return false;
        }
        match (win::window_rect(hwnd), win::cursor_position()) {
            (Some(rect), Some((x, y))) => rect.contains(x, y),
            _ => false,
        }
    }

    /// A subdued six-dot grab handle.
    fn drag_handle(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = self.palette;
        let dot = if self.dragging {
            palette.accent
        } else {
            palette.text_muted.opacity(0.8)
        };
        let rows = (0..3).map(|_| {
            div()
                .flex()
                .gap(px(3.0))
                .child(dot_element(dot))
                .child(dot_element(dot))
        });

        div()
            .id("estao-overlay-drag")
            .relative()
            .w(px(DRAG_HANDLE_WIDTH as f32))
            .h_full()
            .flex_shrink_0()
            .flex()
            .items_center()
            .justify_center()
            .cursor_pointer()
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(|this, event: &MouseDownEvent, window, cx| {
                    this.start_drag(event, window, cx)
                }),
            )
            .child(div().flex().flex_col().gap(px(3.0)).children(rows))
    }

    /// A compact provider tile; detailed windows and reset times live in the popover.
    fn provider_row(
        &self,
        index: usize,
        row: &OverlayRow,
        width: i32,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let palette = self.palette;
        let padding = self.size.provider_padding();
        let percent = row.percent_text();
        let icon = self.icons.get(&row.provider).cloned();
        let used = row.percent_used.unwrap_or(0.0).clamp(0.0, 1.0) as f32;
        let color = indicator_color(
            self.state.read(cx).config(),
            &row.provider,
            row.percent_used,
            palette.accent,
        );

        div()
            .id(SharedString::from(format!(
                "estao-overlay-provider-{}",
                row.provider
            )))
            .w(px(width as f32))
            .h(px((self.size.height() - 8) as f32))
            .flex_shrink_0()
            .flex()
            .flex_row()
            .items_center()
            .gap(px(8.0))
            .px(px(padding as f32))
            .rounded(px(6.0))
            .cursor_pointer()
            .on_mouse_down(
                MouseButton::Left,
                cx.listener(move |this, _, _, cx| {
                    this.click_provider(index, cx);
                }),
            )
            .child(match icon {
                Some(icon) => img(icon)
                    .w(px(PROVIDER_ICON_SIZE as f32))
                    .h(px(PROVIDER_ICON_SIZE as f32))
                    .flex_shrink_0()
                    .into_any_element(),
                None => div()
                    .w(px(PROVIDER_ICON_SIZE as f32))
                    .h(px(PROVIDER_ICON_SIZE as f32))
                    .flex_shrink_0()
                    .rounded_full()
                    .bg(palette.border)
                    .into_any_element(),
            })
            .child(
                div()
                    .flex_1()
                    .min_w_0()
                    .flex()
                    .flex_col()
                    .gap(px(4.0))
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .justify_between()
                            .gap(px(6.0))
                            .child(
                                div()
                                    .min_w_0()
                                    .truncate()
                                    .font_weight(FontWeight::MEDIUM)
                                    .child(if row.provider == "copilot" {
                                        "Copilot".to_string()
                                    } else {
                                        row.display_name.clone()
                                    }),
                            )
                            .child(label(percent, 32, palette.text, false)),
                    )
                    .child(
                        div()
                            .w_full()
                            .h(px(2.0))
                            .rounded_full()
                            .overflow_hidden()
                            .bg(palette.border.opacity(0.45))
                            .child(
                                div()
                                    .h_full()
                                    .w(gpui::relative(used))
                                    .rounded_full()
                                    .bg(color),
                            ),
                    ),
            )
    }
}

impl Render for TaskbarOverlay {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = self.palette;
        let mut providers = div()
            .flex()
            .flex_row()
            .items_center()
            .gap(px(PROVIDER_GAP as f32))
            .pl(px(OUTER_PADDING as f32))
            .pr(px(OUTER_PADDING as f32))
            .h_full();

        for index in 0..self.rows.len() {
            let row = self.rows[index].clone();
            let width = self.row_widths[index];
            providers = providers.child(self.provider_row(index, &row, width, cx));
        }

        div()
            .id("estao-taskbar-overlay")
            .size_full()
            .flex()
            .flex_row()
            .items_center()
            .bg(palette.surface)
            .border_1()
            .border_color(if self.dragging {
                palette.accent
            } else {
                palette.border.opacity(0.55)
            })
            .rounded(px(10.0))
            .font_family("Segoe UI")
            .text_size(px(OVERLAY_FONT_SIZE))
            .line_height(px(14.0))
            .text_color(palette.text)
            .child(self.drag_handle(cx))
            .child(providers)
    }
}

/// A 2x2 pixel drag dot.
fn dot_element(color: Rgba) -> impl IntoElement {
    div().w(px(2.0)).h(px(2.0)).rounded_full().bg(color)
}

/// A fixed-width text element with ellipsis trimming.
fn label(text: impl Into<SharedString>, width: i32, color: Rgba, bold: bool) -> impl IntoElement {
    div()
        .w(px(width as f32))
        .flex_shrink_0()
        .overflow_hidden()
        .whitespace_nowrap()
        .text_ellipsis()
        .text_color(color)
        .when(bold, |element| element.font_weight(FontWeight::BOLD))
        .child(text.into())
}

pub fn cursor_position() -> Option<(i32, i32)> {
    win::cursor_position()
}

/// Win32 access, all of it physical pixels.
#[cfg(target_os = "windows")]
mod win {
    use std::ffi::c_void;

    use gpui::Window;
    use raw_window_handle::{HasWindowHandle, RawWindowHandle};
    use windows::core::{w, PCWSTR};
    use windows::Win32::Foundation::{HWND, POINT, RECT};
    use windows::Win32::Graphics::Gdi::{
        GetMonitorInfoW, MonitorFromPoint, MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST,
    };
    use windows::Win32::UI::HiDpi::{GetDpiForMonitor, MDT_EFFECTIVE_DPI};
    use windows::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON};
    use windows::Win32::UI::WindowsAndMessaging::{
        FindWindowExW, FindWindowW, GetWindowLongPtrW, GetWindowRect, SetWindowLongPtrW,
        SetWindowPos, GWLP_HWNDPARENT, GWL_EXSTYLE, GWL_STYLE, HWND_TOPMOST, SWP_FRAMECHANGED,
        SWP_NOACTIVATE, SWP_NOSIZE, SWP_SHOWWINDOW, WS_CAPTION, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
        WS_POPUP, WS_THICKFRAME,
    };

    use super::{
        constrain_to_monitor, place_against_taskbar, OverlayConfig, OverlayPlacement, PhysicalRect,
    };

    /// Converts a `HWND` into the opaque handle stored by the overlay.
    fn handle(hwnd: HWND) -> isize {
        hwnd.0 as isize
    }

    /// Converts the stored handle back into an `HWND`.
    fn hwnd(value: isize) -> HWND {
        HWND(value as *mut c_void)
    }

    pub fn is_visible(value: isize) -> bool {
        unsafe { windows::Win32::UI::WindowsAndMessaging::IsWindowVisible(hwnd(value)).as_bool() }
    }

    /// Reads a window rectangle.
    pub fn window_rect(hwnd_value: isize) -> Option<PhysicalRect> {
        let mut rect = RECT::default();
        unsafe { GetWindowRect(hwnd(hwnd_value), &mut rect) }.ok()?;
        Some(PhysicalRect::new(
            rect.left,
            rect.top,
            rect.right - rect.left,
            rect.bottom - rect.top,
        ))
    }

    /// Returns the `Shell_TrayWnd` and notification-area rectangles.
    fn taskbar_geometry() -> Option<(PhysicalRect, Option<PhysicalRect>)> {
        let taskbar = unsafe { FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) }.ok()?;
        let taskbar_rect = window_rect(handle(taskbar))?;

        let tray =
            unsafe { FindWindowExW(Some(taskbar), None, w!("TrayNotifyWnd"), PCWSTR::null()) }
                .ok()
                .or_else(|| unsafe { FindWindowW(w!("TrayNotifyWnd"), PCWSTR::null()) }.ok())
                .and_then(|tray| window_rect(handle(tray)));

        Some((taskbar_rect, tray))
    }

    /// DPI scale and monitor bounds for the monitor under `(x, y)`.
    fn monitor_metrics_at(x: i32, y: i32) -> Option<(f32, PhysicalRect)> {
        let monitor = unsafe { MonitorFromPoint(POINT { x, y }, MONITOR_DEFAULTTONEAREST) };
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
        if !unsafe { GetMonitorInfoW(monitor, &mut info.monitorInfo) }.as_bool() {
            return None;
        }

        let mut dpi = 96;
        let mut dpi_y = 96;
        unsafe { GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dpi, &mut dpi_y) }.ok()?;

        let scale = if dpi > 0 { dpi as f32 / 96.0 } else { 1.0 };
        let monitor = info.monitorInfo.rcMonitor;
        Some((
            scale,
            PhysicalRect::new(
                monitor.left,
                monitor.top,
                monitor.right - monitor.left,
                monitor.bottom - monitor.top,
            ),
        ))
    }

    /// Computes the physical placement for the current taskbar.
    pub fn place(config: &OverlayConfig, width: i32, height: i32) -> Option<OverlayPlacement> {
        let (taskbar, tray) = taskbar_geometry()?;
        let (taskbar_x, taskbar_y) = taskbar.center();
        let (taskbar_scale, taskbar_bounds) = monitor_metrics_at(taskbar_x, taskbar_y)?;

        if let (Some(x), Some(y)) = (config.position_x, config.position_y) {
            let (scale, bounds) = monitor_metrics_at(x, y)
                .or_else(|| monitor_metrics_at(taskbar_x, taskbar_y))
                .unwrap_or((taskbar_scale, taskbar_bounds));
            let rect = PhysicalRect::new(
                x,
                y,
                (width as f32 * scale).round() as i32,
                (height as f32 * scale).round() as i32,
            );
            return Some(OverlayPlacement {
                rect: constrain_to_monitor(rect, bounds),
                scale,
            });
        }

        let rect = place_against_taskbar(
            taskbar,
            tray,
            (width as f32 * taskbar_scale).round() as i32,
            (height as f32 * taskbar_scale).round() as i32,
        )?;
        Some(OverlayPlacement {
            rect: constrain_to_monitor(rect, taskbar_bounds),
            scale: taskbar_scale,
        })
    }

    /// Keeps a dragged rectangle on the monitor under its center.
    pub fn constrain(rect: PhysicalRect) -> PhysicalRect {
        let (x, y) = rect.center();
        match monitor_metrics_at(x, y) {
            Some((_, bounds)) => constrain_to_monitor(rect, bounds),
            None => rect,
        }
    }

    /// Moves the overlay to `rect`, topmost and without activating it.
    pub fn set_placement(hwnd_value: isize, rect: PhysicalRect, show: bool) {
        let mut flags = SWP_NOACTIVATE;
        if show {
            flags |= SWP_SHOWWINDOW | SWP_FRAMECHANGED;
        }
        if window_rect(hwnd_value)
            .is_some_and(|old| old.width == rect.width && old.height == rect.height)
        {
            flags |= SWP_NOSIZE;
        }
        let _ = unsafe {
            SetWindowPos(
                hwnd(hwnd_value),
                Some(HWND_TOPMOST),
                rect.left,
                rect.top,
                rect.width,
                rect.height,
                flags,
            )
        };
    }

    /// Adds `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` to the window.
    pub fn apply_overlay_styles(hwnd_value: isize) {
        let window = hwnd(hwnd_value);
        let style = unsafe { GetWindowLongPtrW(window, GWL_EXSTYLE) } as u32;
        let style = style | WS_EX_NOACTIVATE.0 | WS_EX_TOOLWINDOW.0;
        unsafe {
            SetWindowLongPtrW(window, GWL_EXSTYLE, style as isize);
            // GPUI's PopUp currently uses WS_OVERLAPPED, leaving invisible
            // resize borders that shrink the client area and clip the tiles.
            let style = GetWindowLongPtrW(window, GWL_STYLE) as u32;
            let style = (style & !(WS_CAPTION.0 | WS_THICKFRAME.0)) | WS_POPUP.0;
            SetWindowLongPtrW(window, GWL_STYLE, style as isize);
            // Owned popups stay above their owner when Explorer raises the
            // taskbar. No periodic hide/show or z-order tug-of-war is needed.
            if let Ok(taskbar) = FindWindowW(w!("Shell_TrayWnd"), PCWSTR::null()) {
                SetWindowLongPtrW(window, GWLP_HWNDPARENT, taskbar.0 as isize);
            }
        }
    }

    /// The HWND behind a gpui window.
    pub fn hwnd_from_window(window: &Window) -> Option<isize> {
        let handle = HasWindowHandle::window_handle(window).ok()?;
        match handle.as_raw() {
            RawWindowHandle::Win32(handle) => Some(handle.hwnd.get()),
            _ => None,
        }
    }

    /// The current cursor position in physical pixels.
    pub fn cursor_position() -> Option<(i32, i32)> {
        let mut point = POINT::default();
        unsafe { windows::Win32::UI::WindowsAndMessaging::GetCursorPos(&mut point) }.ok()?;
        Some((point.x, point.y))
    }

    /// Whether the physical left mouse button is currently down.
    pub fn left_button_down() -> bool {
        (unsafe { GetAsyncKeyState(VK_LBUTTON.0 as i32) } & 0x8000u16 as i16) != 0
    }
}

/// Non-Windows stubs: the tray app itself is Windows-only.
#[cfg(not(target_os = "windows"))]
mod win {
    use gpui::Window;

    use super::{OverlayConfig, OverlayPlacement, PhysicalRect};

    pub fn is_visible(_value: isize) -> bool {
        false
    }

    /// No taskbar on this platform.
    pub fn window_rect(_hwnd: isize) -> Option<PhysicalRect> {
        None
    }

    /// No taskbar on this platform.
    pub fn place(_config: &OverlayConfig, _width: i32, _height: i32) -> Option<OverlayPlacement> {
        None
    }

    /// We cannot drag without a Win32 handle.
    pub fn constrain(rect: PhysicalRect) -> PhysicalRect {
        rect
    }

    /// No-op.
    pub fn set_placement(_hwnd: isize, _rect: PhysicalRect, _show: bool) {}

    /// No-op.
    pub fn apply_overlay_styles(_hwnd: isize) {}

    /// No-op.
    pub fn hwnd_from_window(_window: &Window) -> Option<isize> {
        None
    }

    /// No cursor polling on this platform.
    pub fn cursor_position() -> Option<(i32, i32)> {
        None
    }

    /// No mouse polling on this platform.
    pub fn left_button_down() -> bool {
        false
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use estao_core::config::UsageColorConfig;
    use estao_core::models::RateWindow;

    fn at(text: &str) -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(text)
            .unwrap()
            .with_timezone(&Utc)
    }

    fn provider(id: &str, enabled: bool) -> ProviderConfig {
        ProviderConfig {
            id: id.to_string(),
            enabled: Some(enabled),
            ..ProviderConfig::default()
        }
    }

    fn snapshot(provider: &str, percent: Option<f64>, error: Option<&str>) -> UsageSnapshot {
        UsageSnapshot {
            provider: provider.to_string(),
            display_name: catalog::display_name(provider),
            source: "oauth".to_string(),
            updated_at: at("2026-11-05T12:00:00Z"),
            windows: if error.is_some() {
                Vec::new()
            } else {
                vec![RateWindow {
                    id: "session".to_string(),
                    title: "Session".to_string(),
                    percent_used: percent,
                    reset_at: Some(at("2026-11-05T15:00:00Z")),
                    used: None,
                    limit: None,
                    unit: None,
                }]
            },
            account: None,
            plan: None,
            credits: None,
            error: error.map(str::to_string),
            is_stale: false,
        }
    }

    fn config(
        providers: &[(&str, bool)],
        selected: &[&str],
    ) -> (OverlayConfig, Vec<ProviderConfig>) {
        let overlay = OverlayConfig {
            provider_ids: selected.iter().map(|id| id.to_string()).collect(),
            ..OverlayConfig::default()
        };
        (
            overlay,
            providers
                .iter()
                .map(|(id, enabled)| provider(id, *enabled))
                .collect(),
        )
    }

    #[test]
    fn parses_the_three_overlay_sizes() {
        assert_eq!(OverlaySize::parse("compact"), OverlaySize::Compact);
        assert_eq!(OverlaySize::parse("normal"), OverlaySize::Normal);
        assert_eq!(OverlaySize::parse(" spacious "), OverlaySize::Spacious);
        assert_eq!(OverlaySize::parse("huge"), OverlaySize::Normal);
        assert_eq!(OverlaySize::default(), OverlaySize::Normal);
    }

    #[test]
    fn sizes_match_the_dotnet_heights() {
        assert_eq!(OverlaySize::Compact.height(), 34);
        assert_eq!(OverlaySize::Normal.height(), 38);
        assert_eq!(OverlaySize::Spacious.height(), 46);
    }

    #[test]
    fn selects_enabled_providers_with_usage_when_no_selection_is_saved() {
        let (overlay, providers) =
            config(&[("codex", true), ("claude", true), ("zai", false)], &[]);
        let snapshots = vec![
            snapshot("codex", Some(0.42), None),
            snapshot("claude", Some(0.10), None),
            snapshot("zai", Some(0.90), None),
            snapshot("copilot", Some(0.20), None),
        ];
        let now = at("2026-11-05T12:00:00Z");

        let rows = visible_rows(&snapshots, &overlay, &providers, now);

        assert_eq!(
            rows.iter()
                .map(|row| row.provider.as_str())
                .collect::<Vec<_>>(),
            vec!["codex", "claude"]
        );
        assert_eq!(rows[0].percent_text(), "42%");
        assert_eq!(rows[0].reset_text.as_deref(), Some("3h 0m"));
    }

    #[test]
    fn skips_failed_or_empty_snapshots_in_the_default_selection() {
        let (overlay, providers) = config(&[("codex", true), ("claude", true)], &[]);
        let mut snapshots = vec![
            snapshot("codex", None, Some("no credentials")),
            snapshot("claude", None, None),
        ];
        snapshots[1].windows.clear();
        let now = at("2026-11-05T12:00:00Z");

        assert!(visible_rows(&snapshots, &overlay, &providers, now).is_empty());
    }

    #[test]
    fn honors_a_saved_selection_and_keeps_waiting_rows() {
        let (overlay, providers) = config(
            &[("codex", true), ("claude", true), ("zai", true)],
            &["claude", "zai"],
        );
        let snapshots = vec![snapshot("codex", Some(0.5), None)];
        let now = at("2026-11-05T12:00:00Z");

        let rows = visible_rows(&snapshots, &overlay, &providers, now);

        assert_eq!(
            rows.iter()
                .map(|row| row.provider.as_str())
                .collect::<Vec<_>>(),
            vec!["claude", "zai"]
        );
        assert_eq!(rows[0].display_name, "Claude");
        assert_eq!(rows[0].percent_text(), "—");
        assert_eq!(rows[0].reset_text, None);
    }

    #[test]
    fn drops_selected_providers_that_are_disabled() {
        let (overlay, providers) = config(&[("codex", true), ("claude", false)], &["claude"]);
        let snapshots = vec![snapshot("claude", Some(0.5), None)];
        let now = at("2026-11-05T12:00:00Z");

        assert!(visible_rows(&snapshots, &overlay, &providers, now).is_empty());
    }

    #[test]
    fn takes_at_most_four_rows() {
        let ids = ["codex", "claude", "copilot", "opencode", "zai"];
        let providers: Vec<ProviderConfig> = ids.iter().map(|id| provider(id, true)).collect();
        let snapshots: Vec<UsageSnapshot> =
            ids.iter().map(|id| snapshot(id, Some(0.5), None)).collect();
        let now = at("2026-11-05T12:00:00Z");

        let rows = visible_rows(&snapshots, &OverlayConfig::default(), &providers, now);

        assert_eq!(rows.len(), 4);
    }

    #[test]
    fn formats_the_reset_countdown_like_dotnet() {
        let now = at("2026-11-05T12:00:00Z");
        assert_eq!(reset_text(None, now), None);
        assert_eq!(
            reset_text(Some(at("2026-11-05T11:59:00Z")), now).as_deref(),
            Some("Reset due")
        );
        assert_eq!(
            reset_text(Some(now + TimeDelta::minutes(200)), now).as_deref(),
            Some("3h 20m")
        );
    }

    #[test]
    fn percentages_round_and_clamp() {
        let row = |percent| OverlayRow {
            provider: "codex".to_string(),
            display_name: "Codex".to_string(),
            percent_used: percent,
            reset_text: None,
        };

        assert_eq!(row(Some(0.4235)).percent_text(), "42%");
        assert_eq!(row(Some(2.0)).percent_text(), "100%");
        assert_eq!(row(Some(-1.0)).percent_text(), "0%");
        assert_eq!(row(None).percent_text(), "—");
    }

    #[test]
    fn width_includes_the_handle_padding_and_provider_gaps() {
        let rows = vec![
            OverlayRow {
                provider: "codex".to_string(),
                display_name: "Codex".to_string(),
                percent_used: Some(0.5),
                reset_text: Some("1h 0m".to_string()),
            },
            OverlayRow {
                provider: "claude".to_string(),
                display_name: "Claude".to_string(),
                percent_used: Some(0.1),
                reset_text: None,
            },
        ];

        let size = OverlaySize::Normal;
        let row_widths: Vec<i32> = rows.iter().map(|row| row_width(row, size)).collect();
        let expected = 2
            + DRAG_HANDLE_WIDTH
            + OUTER_PADDING * 2
            + row_widths.iter().sum::<i32>()
            + PROVIDER_GAP;
        assert_eq!(overlay_width(&rows, size), expected);
        assert_eq!(
            row_widths[0], row_widths[1],
            "provider columns must share the same alignment"
        );
        assert_eq!(overlay_width(&[], size), 0);
        let mut updated = rows[0].clone();
        updated.percent_used = Some(1.0);
        updated.reset_text = Some("Reset due".to_string());
        assert_eq!(
            row_width(&updated, size),
            row_widths[0],
            "usage and countdown updates must not resize the strip"
        );
    }

    #[test]
    fn spacious_rows_get_extra_horizontal_padding() {
        let row = OverlayRow {
            provider: "codex".to_string(),
            display_name: "Codex".to_string(),
            percent_used: Some(0.5),
            reset_text: None,
        };

        assert_eq!(
            row_width(&row, OverlaySize::Spacious) - row_width(&row, OverlaySize::Normal),
            PROVIDER_PADDING_SPACIOUS * 2
        );
    }

    #[test]
    fn places_the_overlay_left_of_the_notification_area() {
        let taskbar = PhysicalRect::new(0, 1040, 1920, 40);
        let tray = PhysicalRect::new(1700, 1040, 220, 40);

        let placed = place_against_taskbar(taskbar, Some(tray), 300, 38).unwrap();

        assert_eq!(placed, PhysicalRect::new(1394, 1041, 300, 38));
    }

    #[test]
    fn falls_back_to_the_taskbar_right_edge_when_the_tray_is_unusable() {
        let taskbar = PhysicalRect::new(0, 1040, 1920, 40);

        let placed = place_against_taskbar(taskbar, None, 300, 38).unwrap();

        assert_eq!(placed, PhysicalRect::new(1608, 1041, 300, 38));
        let stale_tray = PhysicalRect::new(0, 1040, 1920, 40);
        let placed = place_against_taskbar(taskbar, Some(stale_tray), 300, 38).unwrap();
        assert_eq!(placed, PhysicalRect::new(1610, 1041, 300, 38));
    }

    #[test]
    fn hides_when_the_taskbar_cannot_fit_the_overlay() {
        let taskbar = PhysicalRect::new(0, 1040, 700, 40);

        assert!(place_against_taskbar(taskbar, None, 800, 38).is_none());
        assert!(place_against_taskbar(taskbar, None, 680, 38).is_none());
        assert!(place_against_taskbar(taskbar, None, 500, 38).is_some());
    }

    #[test]
    fn hides_on_a_vertical_taskbar() {
        let taskbar = PhysicalRect::new(0, 0, 40, 1080);

        assert!(place_against_taskbar(taskbar, None, 300, 38).is_none());
    }

    #[test]
    fn clamps_to_the_monitor_bounds() {
        let monitor = PhysicalRect::new(0, 0, 1920, 1080);

        assert_eq!(
            constrain_to_monitor(PhysicalRect::new(-20, 1070, 300, 38), monitor),
            PhysicalRect::new(0, 1042, 300, 38)
        );
        assert_eq!(
            constrain_to_monitor(PhysicalRect::new(1900, -10, 300, 38), monitor),
            PhysicalRect::new(1620, 0, 300, 38)
        );
    }

    #[test]
    fn converts_placements_to_logical_bounds() {
        let placement = OverlayPlacement {
            rect: PhysicalRect::new(1920, 2080, 600, 76),
            scale: 2.0,
        };

        let bounds = placement.logical_bounds();

        assert_eq!(bounds.origin, point(px(960.0), px(1040.0)));
        assert_eq!(bounds.size, size(px(300.0), px(38.0)));
    }

    #[test]
    fn strip_uses_the_same_theme_tokens_as_the_other_windows() {
        for theme in [Theme::Light, Theme::Dark] {
            let strip = TaskbarPalette::from_theme(theme);
            let app = theme.palette();
            assert_eq!(strip.surface, app.surface);
            assert_eq!(strip.border, app.border);
            assert_eq!(strip.text, app.text);
            assert_eq!(strip.accent, app.accent);
            assert_eq!(strip.text_hex, rgba_hex(app.text));
        }
    }

    #[test]
    fn indicator_colors_follow_the_provider_thresholds() {
        let palette = TaskbarPalette::default();
        let estao = EstaoConfig {
            providers: vec![ProviderConfig {
                id: "codex".to_string(),
                enabled: Some(true),
                usage_colors: UsageColorConfig {
                    enabled: true,
                    warning_percent: 75.0,
                    warning_color: "#F59E0B".to_string(),
                    critical_percent: 90.0,
                    critical_color: "#EF4444".to_string(),
                },
                ..ProviderConfig::default()
            }],
            ..EstaoConfig::default()
        };

        assert_eq!(
            indicator_color(&estao, "codex", Some(0.5), palette.accent),
            palette.accent
        );
        assert_eq!(
            indicator_color(&estao, "codex", Some(0.80), palette.accent),
            rgb(0xF59E0B)
        );
        assert_eq!(
            indicator_color(&estao, "codex", Some(0.95), palette.accent),
            rgb(0xEF4444)
        );
        assert_eq!(
            indicator_color(&estao, "codex", None, palette.accent),
            palette.accent
        );
        assert_eq!(
            indicator_color(&estao, "claude", Some(0.95), palette.accent),
            palette.accent
        );
    }

    #[test]
    fn parses_hex_colors() {
        assert_eq!(parse_hex_color("#A1B2C3"), Some(rgb(0xA1B2C3)));
        assert_eq!(parse_hex_color("#12345"), None);
        assert_eq!(parse_hex_color("A1B2C3"), None);
    }
}
