//! The taskbar usage overlay window.
//!
//! A small borderless always-on-top no-activate window painted over the
//! taskbar's unused area: provider icons with percentage, donut, and reset
//! time; hides when the taskbar cannot accommodate it; draggable with
//! persisted position; hovering a provider opens the usage popover for it.
//! Ported from `src/Hmy.Estao.App/Controls/Zarpa/TaskbarUsageOverlay.cs`.
//!
//! GPUI owns the painting, so the layout is a row of divs instead of the
//! WinForms `OnPaint` loop. Placement, z-order and the manual drag still go
//! through Win32 (`Shell_TrayWnd` geometry and `SetWindowPos`), because GPUI
//! 0.3 cannot move an existing window or make it topmost.
//!
//! Documented deviations from the .NET control:
//! - Hover opens the popover immediately instead of after 350 ms.
//! - The overlay always renders `icon-title` with `percentage,pie,reset`;
//!   the v2 schema dropped `displayMode` and `controls`.
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
    canvas, div, img, point, prelude::*, px, rgb, size, Bounds, ColorExt, Context, Entity,
    FontWeight, MouseButton, MouseDownEvent, PathBuilder, Pixels, Render, RenderImage, Rgba,
    SharedString, Subscription, Window,
};

use crate::app::icons;
use crate::app::state::AppState;
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
/// Gap between the elements of a provider row.
const ELEMENT_GAP: i32 = 6;
/// Gap between provider rows.
const PROVIDER_GAP: i32 = 10;
/// Provider icon edge length, in logical pixels.
const PROVIDER_ICON_SIZE: i32 = 20;
/// Donut edge length, in logical pixels.
const USAGE_DONUT_SIZE: i32 = 18;
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
fn row_width(row: &OverlayRow, size: OverlaySize) -> i32 {
    let percent = row.percent_text();
    let mut width = size.provider_padding() * 2
        + PROVIDER_ICON_SIZE
        + ELEMENT_GAP
        + estimate_text_width(&row.display_name, true)
        + ELEMENT_GAP
        + estimate_text_width(&percent, false)
        + ELEMENT_GAP
        + USAGE_DONUT_SIZE;
    if let Some(reset) = &row.reset_text {
        width += ELEMENT_GAP + estimate_text_width(reset, false);
    }
    width
}

/// Total overlay width, in logical pixels.
pub fn overlay_width(rows: &[OverlayRow], size: OverlaySize) -> i32 {
    if rows.is_empty() {
        return 0;
    }
    DRAG_HANDLE_WIDTH
        + OUTER_PADDING * 2
        + rows.iter().map(|row| row_width(row, size)).sum::<i32>()
        + PROVIDER_GAP * (rows.len() as i32 - 1)
}

/// Estimates the rendered width of `text` for layout before painting.
///
/// GPUI can only measure text with a window, and the overlay needs its target
/// width before the window exists, so this uses the same per-glyph heuristic
/// for every frame. It overestimates slightly to avoid clipping.
fn estimate_text_width(text: &str, bold: bool) -> i32 {
    let base: i32 = text.chars().map(character_width).sum();
    let width = if bold {
        (base as f32 * 1.06).round() as i32
    } else {
        base
    };
    (width + 2).max(1)
}

/// Approximate advance width of one glyph at [`OVERLAY_FONT_SIZE`].
fn character_width(character: char) -> i32 {
    match character {
        ' ' | '.' | ',' | ':' | ';' | '|' | '\'' | 'i' | 'l' | 'I' | '!' => 3,
        'm' | 'w' | 'M' | 'W' | '%' | '@' | '—' => 9,
        '0'..='9' => 6,
        'A'..='Z' => 7,
        _ => 6,
    }
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

/// The taskbar palette.
///
/// Follows the Windows personalization setting rather than the Estao theme,
/// mirroring `WindowsTaskbarPalette.Read`: the light/dark preference comes
/// from the registry and the accent/text colors from the system colors.
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
    /// Reads the current Windows taskbar palette.
    pub fn read() -> Self {
        let light = apps_use_light_theme();
        win::palette(light)
    }

    /// Builds the palette from an explicit light/dark choice and system colors.
    pub fn build(light: bool, accent: Rgba, window_text: Rgba, gray_text: Rgba) -> Self {
        // `SystemColors.Highlight` is black or white in some high-contrast
        // configurations; the .NET control replaces it with a visible blue.
        let accent = if accent == rgb(0x000000) || accent == rgb(0xFFFFFF) {
            if light {
                rgb(0x0067C0)
            } else {
                rgb(0x60A5FA)
            }
        } else {
            accent
        };

        if light {
            Self {
                surface: rgb(0xF8F8F8),
                border: rgb(0xD2D2D2),
                text: window_text,
                text_muted: gray_text,
                accent,
                text_hex: rgba_hex(window_text),
            }
        } else {
            Self {
                surface: rgb(0x202020),
                border: rgb(0x484848),
                text: rgb(0xF5F5F5),
                text_muted: rgb(0xB9B9B9),
                accent,
                text_hex: 0xF5F5F5,
            }
        }
    }
}

impl Default for TaskbarPalette {
    fn default() -> Self {
        Self::build(true, rgb(0x0067C0), rgb(0x000000), rgb(0x6D6D6D))
    }
}

/// Converts a gpui color into a `0xRRGGBB` value.
fn rgba_hex(color: Rgba) -> u32 {
    let channel = |value: f32| (value.clamp(0.0, 1.0) * 255.0).round() as u32;
    (channel(color.red) << 16) | (channel(color.green) << 8) | channel(color.blue)
}

/// Reads `AppsUseLightTheme`; the .NET default is light when missing.
fn apps_use_light_theme() -> bool {
    windows_registry::CURRENT_USER
        .open(r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
        .ok()
        .and_then(|key| key.get_u32("AppsUseLightTheme").ok())
        .map(|value| value != 0)
        .unwrap_or(true)
}

/// Warn/critical override used by the donut, falling back to the accent.
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
    hovered: Option<String>,
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
        let mut overlay = Self {
            state,
            palette: TaskbarPalette::read(),
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
            hovered: None,
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
            self.rows.clear();
            self.row_widths.clear();
            self.icons.clear();
            self.placement = None;
            self.dragging = false;
            self.hovered = None;
            window.set_visible(false);
            cx.notify();
            return;
        };

        let palette = TaskbarPalette::read();
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

        window.resize(gpui::size(
            px(overlay_width(&self.rows, size) as f32),
            px(size.height() as f32),
        ));
        if let Some(hwnd) = self.hwnd {
            win::set_placement(hwnd, placement.rect);
        }
        window.set_visible(true);
        cx.notify();
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
        self.hovered = None;
        cx.notify();

        cx.spawn(async move |this, cx| loop {
            cx.background_executor()
                .timer(std::time::Duration::from_millis(16))
                .await;
            let keep_going = cx.update(|cx| {
                this.update(cx, |overlay, cx| overlay.poll_drag(cx))
                    .unwrap_or(false)
            });
            if !keep_going {
                break;
            }
        })
        .detach();
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
            win::set_placement(hwnd, placed);
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

    /// Opens the popover for the hovered row (or switches its provider).
    fn report_hover(&mut self, index: usize, cx: &mut Context<Self>) {
        if self.dragging {
            return;
        }
        let Some(row) = self.rows.get(index) else {
            return;
        };
        if self.hovered.as_deref() == Some(row.provider.as_str()) {
            return;
        }
        let Some(placement) = self.placement else {
            return;
        };
        let provider = row.provider.clone();
        let has_snapshot = self
            .state
            .read(cx)
            .snapshots()
            .iter()
            .any(|snapshot| catalog::normalize_id(&snapshot.provider) == provider);
        if !has_snapshot {
            return;
        }

        self.hovered = Some(provider.clone());
        let offset: i32 = (DRAG_HANDLE_WIDTH + OUTER_PADDING)
            + self.row_widths[..index]
                .iter()
                .map(|width| width + PROVIDER_GAP)
                .sum::<i32>();
        let center_x = placement.left as f32
            + (offset as f32 + self.row_widths[index] as f32 / 2.0) * self.scale;
        let anchor = popover::TrayAnchor {
            x: center_x,
            y: placement.top as f32,
        };
        self.state
            .update(cx, |state, cx| state.open_popover_for(provider, anchor, cx));
    }

    /// Forgets the hovered row so re-entering it opens the popover again.
    fn clear_hover(&mut self, index: usize) {
        if let Some(row) = self.rows.get(index) {
            if self.hovered.as_deref() == Some(row.provider.as_str()) {
                self.hovered = None;
            }
        }
    }

    /// The grab handle: eight dots, the drag separator and the mouse target.
    fn drag_handle(&self, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = self.palette;
        let dot = if self.dragging {
            palette.accent
        } else {
            palette.text_muted.opacity(0.8)
        };
        let rows = (0..4).map(|_| {
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
            .child(
                div()
                    .absolute()
                    .right_0()
                    .top(px(9.0))
                    .bottom(px(9.0))
                    .w(px(1.0))
                    .bg(palette.border.opacity(90.0 / 255.0)),
            )
    }

    /// One provider row: icon, bold title, percentage, donut and reset time.
    fn provider_row(
        &self,
        index: usize,
        row: &OverlayRow,
        width: i32,
        cx: &mut Context<Self>,
    ) -> impl IntoElement {
        let palette = self.palette;
        let padding = self.size.provider_padding();
        let label_width = estimate_text_width(&row.display_name, true);
        let percent = row.percent_text();
        let percent_width = estimate_text_width(&percent, false);
        let reset_width = row
            .reset_text
            .as_deref()
            .map(|reset| estimate_text_width(reset, false));
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
            .h_full()
            .flex_shrink_0()
            .flex()
            .flex_row()
            .items_center()
            .gap(px(ELEMENT_GAP as f32))
            .px(px(padding as f32))
            .on_hover(cx.listener(move |this, hovered: &bool, _window, cx| {
                if *hovered {
                    this.report_hover(index, cx);
                } else {
                    this.clear_hover(index);
                }
            }))
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
            .child(label(
                row.display_name.clone(),
                label_width,
                palette.text,
                true,
            ))
            .child(label(percent, percent_width, palette.text_muted, false))
            .child(
                canvas(
                    |_, _, _| (),
                    move |bounds, _, window, _| paint_donut(bounds, used, color, window),
                )
                .w(px(USAGE_DONUT_SIZE as f32))
                .h(px(USAGE_DONUT_SIZE as f32))
                .flex_shrink_0(),
            )
            .when_some(
                row.reset_text.clone().zip(reset_width),
                |element, (reset, width)| {
                    element.child(label(reset, width, palette.text_muted, false))
                },
            )
    }

    /// The thin separator between two provider rows.
    fn provider_separator(&self) -> impl IntoElement {
        div()
            .w(px(1.0))
            .h(px((self.size.height() - 20) as f32))
            .flex_shrink_0()
            .bg(self.palette.border.opacity(110.0 / 255.0))
    }
}

impl Render for TaskbarOverlay {
    fn render(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let palette = self.palette;
        let mut providers = div()
            .flex()
            .flex_row()
            .items_center()
            .gap(px((PROVIDER_GAP / 2) as f32))
            .pl(px(OUTER_PADDING as f32))
            .pr(px(OUTER_PADDING as f32))
            .h_full();

        for index in 0..self.rows.len() {
            if index > 0 {
                providers = providers.child(self.provider_separator());
            }
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
                palette.border
            })
            .rounded(px(8.0))
            .font_family("Segoe UI")
            .text_size(px(OVERLAY_FONT_SIZE))
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

/// Paints the usage donut: a faint full ring plus the used arc from 12 o'clock.
fn paint_donut(bounds: Bounds<Pixels>, used: f32, color: Rgba, window: &mut Window) {
    let edge = bounds.size.width.min(bounds.size.height);
    if edge <= px(4.0) {
        return;
    }
    let radius = edge / 2.0 - px(1.5);
    let center = bounds.center();
    let radii = point(radius, radius);
    let top = point(center.x, center.y - radius);

    let mut track = PathBuilder::stroke(px(3.0));
    track.move_to(point(center.x - radius, center.y));
    track.arc_to(
        radii,
        px(0.0),
        false,
        true,
        point(center.x + radius, center.y),
    );
    track.arc_to(
        radii,
        px(0.0),
        false,
        true,
        point(center.x - radius, center.y),
    );
    if let Ok(path) = track.build() {
        window.paint_path(path, color.opacity(0.29));
    }

    let used = used.clamp(0.0, 1.0);
    if used <= 0.0 {
        return;
    }

    let mut value = PathBuilder::stroke(px(3.0));
    value.move_to(top);
    if used >= 0.9999 {
        value.arc_to(
            radii,
            px(0.0),
            false,
            true,
            point(center.x, center.y + radius),
        );
        value.arc_to(radii, px(0.0), false, true, top);
    } else {
        let angle = std::f32::consts::TAU * used;
        let end = point(
            center.x + radius * angle.sin(),
            center.y - radius * angle.cos(),
        );
        value.arc_to(radii, px(0.0), angle > std::f32::consts::PI, true, end);
    }
    if let Ok(path) = value.build() {
        window.paint_path(path, color);
    }
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
        CreateDCW, DeleteDC, GetDeviceCaps, GetMonitorInfoW, GetSysColor, MonitorFromPoint,
        COLOR_GRAYTEXT, COLOR_HIGHLIGHT, COLOR_WINDOWTEXT, LOGPIXELSX, MONITORINFO, MONITORINFOEXW,
        MONITOR_DEFAULTTONEAREST,
    };
    use windows::Win32::UI::Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON};
    use windows::Win32::UI::WindowsAndMessaging::{
        FindWindowExW, FindWindowW, GetWindowLongPtrW, GetWindowRect, SetWindowLongPtrW,
        SetWindowPos, GWL_EXSTYLE, HWND_TOPMOST, SWP_NOACTIVATE, SWP_SHOWWINDOW, WS_EX_NOACTIVATE,
        WS_EX_TOOLWINDOW,
    };

    use super::{
        constrain_to_monitor, place_against_taskbar, OverlayConfig, OverlayPlacement, PhysicalRect,
        TaskbarPalette,
    };

    /// Converts a `HWND` into the opaque handle stored by the overlay.
    fn handle(hwnd: HWND) -> isize {
        hwnd.0 as isize
    }

    /// Converts the stored handle back into an `HWND`.
    fn hwnd(value: isize) -> HWND {
        HWND(value as *mut c_void)
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

        let device = PCWSTR(info.szDevice.as_ptr());
        let dc = unsafe { CreateDCW(PCWSTR::null(), device, PCWSTR::null(), None) };
        if dc.is_invalid() {
            return None;
        }
        let dpi = unsafe { GetDeviceCaps(Some(dc), LOGPIXELSX) };
        unsafe {
            let _ = DeleteDC(dc);
        }

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
    pub fn set_placement(hwnd_value: isize, rect: PhysicalRect) {
        let _ = unsafe {
            SetWindowPos(
                hwnd(hwnd_value),
                Some(HWND_TOPMOST),
                rect.left,
                rect.top,
                rect.width,
                rect.height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW,
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

    /// Reads the taskbar palette from the Windows personalization settings.
    pub fn palette(light: bool) -> TaskbarPalette {
        let accent = colorref(unsafe { GetSysColor(COLOR_HIGHLIGHT) });
        let window_text = colorref(unsafe { GetSysColor(COLOR_WINDOWTEXT) });
        let gray_text = colorref(unsafe { GetSysColor(COLOR_GRAYTEXT) });
        TaskbarPalette::build(light, accent, window_text, gray_text)
    }

    /// Converts a Win32 `COLORREF` (`0x00BBGGRR`) into a gpui color.
    fn colorref(value: u32) -> gpui::Rgba {
        let red = value & 0xFF;
        let green = (value >> 8) & 0xFF;
        let blue = (value >> 16) & 0xFF;
        gpui::rgb((red << 16) | (green << 8) | blue)
    }
}

/// Non-Windows stubs: the tray app itself is Windows-only.
#[cfg(not(target_os = "windows"))]
mod win {
    use gpui::Window;

    use super::{OverlayConfig, OverlayPlacement, PhysicalRect, TaskbarPalette};

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
    pub fn set_placement(_hwnd: isize, _rect: PhysicalRect) {}

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

    /// Fallback palette.
    pub fn palette(light: bool) -> TaskbarPalette {
        TaskbarPalette::build(
            light,
            gpui::rgb(0x0067C0),
            gpui::rgb(0x000000),
            gpui::rgb(0x6D6D6D),
        )
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
        let expected =
            DRAG_HANDLE_WIDTH + OUTER_PADDING * 2 + row_widths.iter().sum::<i32>() + PROVIDER_GAP;
        assert_eq!(overlay_width(&rows, size), expected);
        assert_eq!(overlay_width(&[], size), 0);
        assert!(row_widths[0] > row_widths[1], "the reset text adds width");
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
    fn builds_the_light_and_dark_taskbar_palettes() {
        let black = rgb(0x000000);
        let white = rgb(0xFFFFFF);
        let light = TaskbarPalette::build(true, rgb(0x0078D4), black, rgb(0x6D6D6D));
        let dark = TaskbarPalette::build(false, rgb(0x0078D4), black, rgb(0x6D6D6D));

        assert_eq!(light.surface, rgb(0xF8F8F8));
        assert_eq!(light.border, rgb(0xD2D2D2));
        assert_eq!(light.text, black);
        assert_eq!(light.text_hex, 0x000000);
        assert_eq!(dark.surface, rgb(0x202020));
        assert_eq!(dark.border, rgb(0x484848));
        assert_eq!(dark.text, rgb(0xF5F5F5));
        assert_eq!(dark.text_muted, rgb(0xB9B9B9));
        assert_eq!(dark.text_hex, 0xF5F5F5);

        // A black/white system highlight is replaced with the .NET blue.
        let fallback = TaskbarPalette::build(false, white, black, black);
        assert_eq!(fallback.accent, rgb(0x60A5FA));
        let fallback = TaskbarPalette::build(true, black, black, black);
        assert_eq!(fallback.accent, rgb(0x0067C0));
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
