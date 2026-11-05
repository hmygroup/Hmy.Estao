//! Window creation helpers shared by all Estao windows.
//!
//! Wave 5 contract: builders returning `gpui::WindowOptions` for the three
//! window kinds: a normal titled window (settings), a borderless
//! no-activate popup near an anchor point (usage popover), and a borderless
//! always-on-top no-activate popup pinned over the taskbar (overlay).

use gpui::{px, size, Bounds, Pixels, TitlebarOptions, WindowBounds, WindowKind, WindowOptions};

pub mod popover;

/// A normal, resizable, titled window for the settings surface.
pub fn settings_options(bounds: Bounds<Pixels>) -> WindowOptions {
    WindowOptions {
        window_bounds: Some(WindowBounds::Windowed(bounds)),
        titlebar: Some(TitlebarOptions {
            title: Some("Estao settings".into()),
            ..TitlebarOptions::default()
        }),
        kind: WindowKind::Normal,
        focus: true,
        show: true,
        is_movable: true,
        is_resizable: true,
        is_minimizable: true,
        window_min_size: Some(size(px(560.0), px(420.0))),
        app_id: Some("estao".to_string()),
        ..WindowOptions::default()
    }
}

/// A borderless popup for the usage popover.
///
/// The bounds are the requested popup rectangle, already anchored above the
/// tray icon by [`popover::popover_bounds`] (or centered when the platform
/// reports no anchor). Unlike the Wave 5 stub this window takes keyboard
/// focus when it opens: GPUI 0.3 has no window-deactivation hook, so Escape is
/// the only keyboard dismissal and requires the popup to be active.
pub fn popover_options(bounds: Bounds<Pixels>) -> WindowOptions {
    WindowOptions {
        window_bounds: Some(WindowBounds::Windowed(bounds)),
        titlebar: None,
        focus: true,
        show: true,
        kind: WindowKind::PopUp,
        is_movable: false,
        is_resizable: false,
        is_minimizable: false,
        app_id: Some("estao".to_string()),
        ..WindowOptions::default()
    }
}

/// A borderless, non-activating popup for the taskbar usage overlay.
///
/// `WindowOptions` in gpui-ce 0.3 has no `topmost`/z-order field, so this
/// builder uses [`WindowKind::PopUp`] (the platform's "above other windows"
/// kind) as the closest approximation. Wave 8 owns the exact Win32
/// `SetWindowPos(HWND_TOPMOST | SWP_NOACTIVATE)` behaviour.
#[allow(dead_code)] // Consumed by the taskbar overlay wave.
pub fn overlay_options(bounds: Bounds<Pixels>) -> WindowOptions {
    WindowOptions {
        window_bounds: Some(WindowBounds::Windowed(bounds)),
        titlebar: None,
        focus: false,
        show: true,
        kind: WindowKind::PopUp,
        is_movable: true,
        is_resizable: false,
        is_minimizable: false,
        app_id: Some("estao".to_string()),
        ..WindowOptions::default()
    }
}
