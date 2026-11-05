//! GPUI application shell for Estao.
//!
//! Wave 5 fills in [`run`]: single-instance guard, the gpui platform
//! application, the shared [`state::AppState`] entity, the tray host, the
//! refresh loop, and the menu/tray event pump. See `spike/src/main.rs` in the
//! repository root for the proven tray + GPUI integration pattern.

pub mod chart;
pub mod icons;
pub mod oauth_login;
pub mod single_instance;
pub mod startup;
pub mod state;
pub mod theme;
pub mod toasts;
pub mod tray;
pub mod windows;

/// Options for a GUI launch.
pub struct AppOptions {
    /// Open the settings window immediately (the `--settings` flag).
    pub open_settings: bool,
}

/// Runs the tray app until the user quits from the tray menu.
///
/// Implemented in Wave 5.
pub fn run(_options: AppOptions) {
    todo!("Wave 5: application shell (single instance, tray host, refresh loop, event pump)")
}
