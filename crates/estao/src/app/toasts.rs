//! Pacing toast notifications.
//!
//! Wave 5 contract: after each refresh, compare every snapshot window
//! against its provider's pacing config (via the core pacing calculator +
//! `PacingStateStore` once-per-day semantics) and raise a Windows toast
//! with `tauri_winrt_notification`. Ported from
//! `TrayApplicationContext.CheckPacingWarningsAsync` +
//! `ShowPacingBalloon`.
