//! GPUI application shell for Estao.
//!
//! Wave 5 fills in [`run`]: single-instance guard, the gpui platform
//! application, the shared [`state::AppState`] entity, the tray host, the
//! refresh loop, and the menu/tray event pump. See `spike/src/main.rs` in the
//! repository root for the proven tray + GPUI integration pattern.

use std::sync::Arc;
use std::time::Duration;

use chrono::TimeDelta;
use estao_core::config::ConfigStore;
use estao_core::refresh::{AdaptiveRefreshLoop, UsageRefreshService};
use gpui::{App, AppContext, QuitMode};
use gpui_ce_platform as gpui_platform;

use self::state::AppState;
use self::tray::TrayHost;

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
pub fn run(options: AppOptions) {
    let Some(_instance) = single_instance::SingleInstance::acquire() else {
        eprintln!("Estao is already running; exiting this second instance.");
        return;
    };

    // Load the config eagerly: the tray, theme, refresh interval and provider
    // list all depend on it before the first frame is drawn.
    let config_store = Arc::new(ConfigStore::new());
    let config = config_store.load().unwrap_or_else(|error| {
        eprintln!("Estao: failed to load the config: {error:#}");
        ConfigStore::create_default_config()
    });

    let service = Arc::new(UsageRefreshService::new(config_store.clone()));

    // Mirrors `TrayApplicationContext.ConfiguredRefreshDelay`: a disabled
    // refresh loop falls back to one minute, otherwise the configured interval
    // is clamped to 1..=60 minutes. Power-state awareness is a later wave, so
    // the loop is never treated as power constrained here.
    let refresh_config = config.refresh.clone();
    let configured_delay = move || {
        let minutes = if refresh_config.enabled {
            refresh_config.interval_minutes.clamp(1, 60)
        } else {
            1
        };
        Some(TimeDelta::minutes(i64::from(minutes)))
    };
    let refresh_loop = Arc::new(AdaptiveRefreshLoop::new(
        service.clone(),
        || false,
        configured_delay,
    ));

    // The tray must exist before the GPUI event loop starts: tray-icon installs
    // its message window on the calling (main) thread.
    let tray = match TrayHost::new(&[], startup::is_enabled()) {
        Ok(tray) => Some(tray),
        Err(error) => {
            eprintln!("Estao: failed to create the tray icon: {error:#}");
            None
        }
    };

    let open_settings = options.open_settings;

    gpui_platform::application()
        .with_quit_mode(QuitMode::Explicit)
        .run(move |cx: &mut App| {
            refresh_loop.start();

            let state =
                cx.new(move |_cx| AppState::new(config, config_store, service, refresh_loop, tray));
            let state_keepalive = state.clone();
            let state_weak = state.downgrade();

            if open_settings {
                state.update(cx, |state, cx| state.open_settings(cx));
            }
            cx.activate(true);

            let menu_rx = tray_icon::menu::MenuEvent::receiver().clone();
            let tray_rx = tray_icon::TrayIconEvent::receiver().clone();

            // No static Send/Sync handlers: poll muda's crossbeam receivers
            // from a GPUI main-thread task, like the spike.
            cx.spawn(async move |cx| {
                // Keep the state entity alive for the process lifetime; the
                // pump itself only holds a weak handle.
                let _keepalive = state_keepalive;

                loop {
                    while let Ok(event) = menu_rx.try_recv() {
                        cx.update(|cx| {
                            if let Some(state) = state_weak.upgrade() {
                                state.update(cx, |state, cx| {
                                    state.handle_menu_event(&event.id, cx);
                                });
                            }
                        });
                    }

                    while let Ok(event) = tray_rx.try_recv() {
                        cx.update(|cx| {
                            if let Some(state) = state_weak.upgrade() {
                                state.update(cx, |state, cx| {
                                    state.handle_tray_event(&event, cx);
                                });
                            }
                        });
                    }

                    // The refresh service has no callback, so the pump also
                    // checks whether the background loop produced a new round
                    // of snapshots.
                    cx.update(|cx| {
                        if let Some(state) = state_weak.upgrade() {
                            state.update(cx, |state, cx| state.poll_service(cx));
                        }
                    });

                    cx.background_executor()
                        .timer(Duration::from_millis(100))
                        .await;
                }
            })
            .detach();
        });
}
