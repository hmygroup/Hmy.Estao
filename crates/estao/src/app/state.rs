//! Shared application state (a GPUI entity).
//!
//! Wave 5 contract: an `AppState` view holding the loaded
//! `EstaoConfig`, the `UsageRefreshService` + `AdaptiveRefreshLoop`
//! handles, the latest `Vec<UsageSnapshot>` and usage history, and the
//! currently open window handles (popover / settings / overlay). Exposes
//! actions such as `refresh_now`, `open_settings`, `open_popover`,
//! `toggle_startup`, and `quit` so the tray host can drive the app.
