//! Shared application state (a GPUI entity).
//!
//! An `AppState` entity holding the loaded
//! `EstaoConfig`, the `UsageRefreshService` + `AdaptiveRefreshLoop`
//! handles, the latest `Vec<UsageSnapshot>` and usage history, and the
//! currently open window handles (popover / settings / overlay). Exposes
//! actions such as `refresh_now`, `open_settings`, `open_popover`,
//! `toggle_startup`, and `quit` so the tray host can drive the app.

use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::Result;
use chrono::{DateTime, Utc};
use estao_core::config::{ConfigStore, EstaoConfig};
use estao_core::models::{UsageHistoryPoint, UsageSnapshot};
use estao_core::refresh::{AdaptiveRefreshLoop, UsageRefreshService};
use gpui::{prelude::*, px, size, App, Bounds, Context, WindowHandle};
use tray_icon::menu::MenuId;
use tray_icon::TrayIconEvent;

use crate::app::startup;
use crate::app::theme::Theme;
use crate::app::toasts;
use crate::app::tray::{TrayAction, TrayHost};
use crate::app::windows;
use crate::app::windows::overlay;
use crate::app::windows::popover;
use crate::app::windows::settings;

/// Shared state for the tray app.
pub struct AppState {
    config: EstaoConfig,
    config_store: Arc<ConfigStore>,
    service: Arc<UsageRefreshService>,
    refresh_loop: Arc<AdaptiveRefreshLoop>,
    snapshots: Vec<UsageSnapshot>,
    history: Vec<UsageHistoryPoint>,
    theme: Theme,
    tray: Option<TrayHost>,
    settings_window: Option<WindowHandle<settings::SettingsWindow>>,
    popover_window: Option<WindowHandle<popover::UsagePopover>>,
    popover_hover_owned: bool,
    popover_hover_left_at: Option<Instant>,
    overlay_window: Option<WindowHandle<overlay::TaskbarOverlay>>,
    last_overlay_poll: Option<Instant>,
    /// Newest `updated_at` across the applied snapshots; used to detect a
    /// background refresh without diffing the whole payload.
    last_stamp: Option<DateTime<Utc>>,
    refreshing: bool,
    quitting: bool,
}

impl AppState {
    /// Builds the application state around the already-created services and
    /// tray icon.
    pub fn new(
        config: EstaoConfig,
        config_store: Arc<ConfigStore>,
        service: Arc<UsageRefreshService>,
        refresh_loop: Arc<AdaptiveRefreshLoop>,
        tray: Option<TrayHost>,
    ) -> Self {
        let theme = Theme::from_name(&config.theme);
        Self {
            config,
            config_store,
            service,
            refresh_loop,
            snapshots: Vec::new(),
            history: Vec::new(),
            theme,
            tray,
            settings_window: None,
            popover_window: None,
            popover_hover_owned: false,
            popover_hover_left_at: None,
            overlay_window: None,
            last_overlay_poll: None,
            last_stamp: None,
            refreshing: false,
            quitting: false,
        }
    }

    /// Handles a context-menu event from the tray.
    pub fn handle_menu_event(&mut self, id: &MenuId, cx: &mut Context<Self>) {
        if let Some(action) = TrayHost::action_for_menu_id(id) {
            self.apply_action(action, cx);
        }
    }

    /// Handles a notification-area click.
    pub fn handle_tray_event(&mut self, event: &TrayIconEvent, cx: &mut Context<Self>) {
        if let Some(action) = TrayHost::action_for_tray_event(event) {
            if action == TrayAction::OpenPopover {
                // The click event carries the tray icon rectangle, which is
                // the anchor for the popup.
                self.toggle_popover(popover::anchor_from_event(event), cx);
            } else {
                self.apply_action(action, cx);
            }
        }
    }

    /// Picks up a refresh produced by the background loop.
    ///
    /// Called from the event pump. The refresh service has no event channel,
    /// so the newest `updated_at` acts as its change stamp.
    pub fn poll_service(&mut self, cx: &mut Context<Self>) {
        let snapshots = self.service.last_snapshots();
        if snapshot_stamp(&snapshots) != self.last_stamp {
            self.apply_snapshots(snapshots, cx);
        }
    }

    /// Refreshes every provider on the background executor.
    pub fn refresh_now(&mut self, cx: &mut Context<Self>) {
        if self.refreshing {
            return;
        }

        self.refreshing = true;
        self.refresh_loop.mark_interaction();

        let service = self.service.clone();
        let task = cx
            .background_executor()
            .spawn(async move { service.refresh() });

        cx.spawn(async move |this, cx| {
            let snapshots = task.await;
            cx.update(|cx| {
                if let Some(this) = this.upgrade() {
                    this.update(cx, |state, cx| {
                        state.refreshing = false;
                        state.apply_snapshots(snapshots, cx);
                    });
                }
            });
        })
        .detach();
    }

    /// Opens (or focuses) the settings window.
    pub fn open_settings(&mut self, cx: &mut Context<Self>) {
        if focus_window(&self.settings_window, cx) {
            cx.activate(true);
            return;
        }
        self.settings_window = None;

        // The .NET form reloads the config on Load; do the same so an edit
        // made by the CLI or another window is visible when this one opens.
        let config = self.config_store.load().unwrap_or_else(|error| {
            eprintln!("Estao: failed to reload the config for settings: {error:#}");
            self.config.clone()
        });
        let bounds = Bounds::centered(
            None,
            size(px(settings::SETTINGS_SIZE.0), px(settings::SETTINGS_SIZE.1)),
            cx,
        );
        let palette = Theme::from_name(&config.theme).palette();
        let store = self.config_store.clone();
        let state = cx.entity();

        match cx.open_window(windows::settings_options(bounds), move |_window, cx| {
            cx.new(|cx| {
                settings::SettingsWindow::new(
                    state.clone(),
                    store.clone(),
                    config.clone(),
                    palette,
                    cx,
                )
            })
        }) {
            Ok(handle) => {
                self.settings_window = Some(handle);
                cx.activate(true);
            }
            Err(error) => {
                eprintln!("Estao: failed to open the settings window: {error:#}");
            }
        }
    }

    /// Persists an edited config and applies it to the live app.
    ///
    /// The settings window owns the editable copy; saving normalizes and writes
    /// it, then swaps the in-memory config so the tray menu and the popover
    /// observe the new provider/theme state immediately.
    pub fn replace_config(&mut self, config: EstaoConfig, cx: &mut Context<Self>) -> Result<()> {
        let mut config = ConfigStore::normalize(config);
        let theme = Theme::from_name(&config.theme);
        config.theme = theme.name().to_string();
        self.config_store.save(config.clone())?;
        self.config = config;
        self.theme = theme;
        self.rebuild_tray_menu();
        cx.notify();
        self.sync_overlay(cx);
        self.refresh_now(cx);
        Ok(())
    }

    /// Toggles the usage popover window.
    pub fn open_popover(&mut self, cx: &mut Context<Self>) {
        self.toggle_popover(None, cx);
    }

    /// Toggles the usage popover, anchoring it to the tray icon when known.
    fn toggle_popover(&mut self, anchor: Option<popover::TrayAnchor>, cx: &mut Context<Self>) {
        if let Some(handle) = &self.popover_window {
            if handle
                .update(cx, |_, window, _| window.remove_window())
                .is_ok()
            {
                self.popover_window = None;
                self.popover_hover_owned = false;
                self.popover_hover_left_at = None;
                return;
            }
        }
        self.popover_window = None;

        self.show_popover(anchor, None, cx);
    }

    /// Opens or selects a provider from the taskbar overlay.
    pub fn open_popover_for(
        &mut self,
        provider: String,
        anchor: popover::TrayAnchor,
        cx: &mut Context<Self>,
    ) {
        let state = cx.entity();
        cx.defer(move |cx| {
            let handle = state.read(cx).popover_window;
            if let Some(handle) = handle {
                if handle
                    .update(cx, |view, _, cx| {
                        view.select_provider(&provider, cx);
                    })
                    .is_ok()
                {
                    return;
                }
            }
            state.update(cx, |state, cx| {
                state.popover_window = None;
                state.show_popover(Some(anchor), Some(provider), cx);
            });
        });
    }

    fn show_popover(
        &mut self,
        anchor: Option<popover::TrayAnchor>,
        provider: Option<String>,
        cx: &mut Context<Self>,
    ) {
        let hover_owned = provider.is_some();
        // Opening the popover counts as user activity for the adaptive loop.
        self.refresh_loop.mark_interaction();

        // Tray/menu handlers run inside `AppState::update`, and the popover
        // renders straight from that entity. Opening the window here would
        // make its first draw re-enter the entity borrow, so defer the open
        // until GPUI flushes effects at the end of the outermost update.
        let state = cx.entity();
        let palette = self.theme.palette();
        cx.defer(move |cx| {
            if state.read(cx).popover_window.is_some() {
                return;
            }

            let popover_size = size(px(popover::POPOVER_SIZE.0), px(popover::POPOVER_SIZE.1));
            // The tray event reports physical pixels; `popover_bounds`
            // converts them with the monitor scale factor and clamps to the
            // work area. Without an anchor (tray menu, failed metrics) the
            // popup is centered.
            let bounds = popover::popover_bounds(anchor)
                .unwrap_or_else(|| Bounds::centered(None, popover_size, cx));

            let view_state = state.clone();
            let mut options = windows::popover_options(bounds);
            options.focus = !hover_owned;
            match cx.open_window(options, move |window, cx| {
                if hover_owned {
                    overlay::make_nonactivating(window);
                }
                cx.new(|cx| match provider {
                    Some(provider) => popover::UsagePopover::new_with_provider(
                        view_state.clone(),
                        palette,
                        Some(provider),
                        window,
                        cx,
                    ),
                    None => popover::UsagePopover::new(view_state.clone(), palette, window, cx),
                })
            }) {
                Ok(handle) => {
                    state.update(cx, |state, _cx| {
                        state.popover_window = Some(handle);
                        state.popover_hover_owned = hover_owned;
                        state.popover_hover_left_at = None;
                    });
                }
                Err(error) => {
                    eprintln!("Estao: failed to open the usage popover: {error:#}");
                }
            }
        });
    }

    /// Flips the Windows "Launch at sign-in" registration.
    pub fn toggle_startup(&mut self, cx: &mut Context<Self>) {
        let updated = if startup::is_enabled() {
            startup::disable()
        } else {
            startup::enable()
        };

        if !updated {
            eprintln!("Estao: could not update the Windows startup setting.");
        }

        self.rebuild_tray_menu();
        cx.notify();
    }

    /// Refreshes taskbar placement and countdowns once per second.
    pub fn poll_overlay(&mut self, cx: &mut Context<Self>) {
        self.poll_hover_popover(cx);
        if self
            .last_overlay_poll
            .is_none_or(|last| last.elapsed() >= Duration::from_secs(1))
        {
            self.sync_overlay(cx);
        }
    }

    fn poll_hover_popover(&mut self, cx: &mut Context<Self>) {
        if !self.popover_hover_owned {
            return;
        }
        let state = cx.entity();
        let popover = self.popover_window;
        let overlay = self.overlay_window;
        cx.defer(move |cx| {
            let inside_popover = popover.is_some_and(|handle| {
                handle
                    .update(cx, |_, window, _| overlay::pointer_in_window(window))
                    .unwrap_or(false)
            });
            let inside_overlay = overlay.is_some_and(|handle| {
                handle
                    .update(cx, |_, window, _| overlay::pointer_in_window(window))
                    .unwrap_or(false)
            });
            let close = state.update(cx, |state, _| {
                hover_dismiss_due(
                    inside_popover || inside_overlay,
                    &mut state.popover_hover_left_at,
                    Instant::now(),
                )
            });
            if close {
                if let Some(handle) = popover {
                    let _ = handle.update(cx, |_, window, _| window.remove_window());
                }
                state.update(cx, |state, _| {
                    state.popover_window = None;
                    state.popover_hover_owned = false;
                    state.popover_hover_left_at = None;
                });
            }
        });
    }

    /// Defers window access so its view can safely read shared state.
    pub fn sync_overlay(&mut self, cx: &mut Context<Self>) {
        self.last_overlay_poll = Some(Instant::now());
        let state = cx.entity();
        cx.defer(move |cx| {
            let handle = state.read(cx).overlay_window;
            if let Some(handle) = handle {
                if handle
                    .update(cx, |view, window, cx| {
                        view.refresh(window, cx);
                    })
                    .is_ok()
                {
                    return;
                }
            }
            let placement = {
                let state = state.read(cx);
                overlay::desired_placement(
                    &state.config.overlay,
                    &state.snapshots,
                    &state.config.providers,
                    Utc::now(),
                )
            };
            let Some(placement) = placement else { return };
            let view_state = state.clone();
            match cx.open_window(
                windows::overlay_options(placement.logical_bounds()),
                move |window, cx| cx.new(|cx| overlay::TaskbarOverlay::new(view_state, window, cx)),
            ) {
                Ok(handle) => state.update(cx, |state, _| state.overlay_window = Some(handle)),
                Err(error) => eprintln!("Estao: failed to open the taskbar overlay: {error:#}"),
            }
        });
    }

    /// Persists a physical-screen drag position without refreshing providers.
    pub fn set_overlay_position(&mut self, x: i32, y: i32, cx: &mut Context<Self>) {
        let mut config = self.config.clone();
        config.overlay.position_x = Some(x);
        config.overlay.position_y = Some(y);
        match self.config_store.save(config.clone()) {
            Ok(()) => {
                self.config = config;
                cx.notify();
            }
            Err(error) => eprintln!("Estao: failed to save overlay position: {error:#}"),
        }
    }

    /// Stops the refresh loop and exits the application.
    pub fn quit(&mut self, cx: &mut Context<Self>) {
        if self.quitting {
            return;
        }
        self.quitting = true;

        // Join the refresh worker off the UI thread: it may be blocked in a
        // provider HTTP request. The process exits right after `cx.quit()`.
        let refresh_loop = self.refresh_loop.clone();
        cx.background_executor()
            .spawn(async move {
                refresh_loop.stop();
            })
            .detach();

        cx.quit();
    }

    fn apply_action(&mut self, action: TrayAction, cx: &mut Context<Self>) {
        match action {
            TrayAction::RefreshNow => self.refresh_now(cx),
            TrayAction::OpenSettings => self.open_settings(cx),
            TrayAction::ToggleStartup => self.toggle_startup(cx),
            TrayAction::Quit => self.quit(cx),
            TrayAction::OpenPopover => self.open_popover(cx),
        }
    }

    /// Applies fresh snapshots, refreshes the tray menu and raises pacing
    /// toasts when the payload actually changed.
    fn apply_snapshots(&mut self, snapshots: Vec<UsageSnapshot>, cx: &mut Context<Self>) {
        let stamp = snapshot_stamp(&snapshots);
        let changed = stamp != self.last_stamp || self.snapshots.len() != snapshots.len();

        self.snapshots = snapshots;
        self.history = self.service.history();
        self.last_stamp = stamp;

        if !changed {
            return;
        }

        self.rebuild_tray_menu();
        toasts::check_pacing_warnings(
            &self.config,
            &self.snapshots,
            &self.history,
            self.config_store.path(),
        );
        cx.notify();
        self.sync_overlay(cx);
    }

    fn rebuild_tray_menu(&mut self) {
        let Some(tray) = &self.tray else {
            return;
        };

        if let Err(error) = tray.rebuild_menu(&self.snapshots, startup::is_enabled()) {
            eprintln!("Estao: failed to rebuild the tray menu: {error:#}");
        }
    }
}

/// Read-only accessors used by the settings/popover/overlay waves.
#[allow(dead_code)]
impl AppState {
    /// The loaded configuration.
    pub fn config(&self) -> &EstaoConfig {
        &self.config
    }

    /// The latest provider snapshots.
    pub fn snapshots(&self) -> &[UsageSnapshot] {
        &self.snapshots
    }

    /// The persisted usage history known to the refresh service.
    pub fn history(&self) -> &[UsageHistoryPoint] {
        &self.history
    }

    /// The active color theme.
    pub fn theme(&self) -> Theme {
        self.theme
    }

    /// The shared refresh service.
    pub fn refresh_service(&self) -> &Arc<UsageRefreshService> {
        &self.service
    }

    /// The adaptive background refresh loop.
    pub fn refresh_loop(&self) -> &Arc<AdaptiveRefreshLoop> {
        &self.refresh_loop
    }
}

/// The newest snapshot timestamp, used as a cheap change stamp.
fn snapshot_stamp(snapshots: &[UsageSnapshot]) -> Option<DateTime<Utc>> {
    snapshots.iter().map(|snapshot| snapshot.updated_at).max()
}

// A short grace period bridges the gap between the strip and its popover.
fn hover_dismiss_due(inside: bool, left_at: &mut Option<Instant>, now: Instant) -> bool {
    if inside {
        *left_at = None;
        return false;
    }
    let left = left_at.get_or_insert(now);
    now.duration_since(*left) >= Duration::from_millis(200)
}

/// Brings an existing window to the front, returning `false` when it is gone.
fn focus_window<T: 'static>(handle: &Option<WindowHandle<T>>, cx: &mut App) -> bool {
    let Some(handle) = handle else {
        return false;
    };

    handle.update(cx, |_, window, _| window.activate()).is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hover_popover_closes_after_leaving_both_surfaces_but_allows_crossing_the_gap() {
        let now = Instant::now();
        let mut left = None;
        assert!(!hover_dismiss_due(false, &mut left, now));
        assert!(!hover_dismiss_due(
            false,
            &mut left,
            now + Duration::from_millis(100)
        ));
        assert!(!hover_dismiss_due(
            true,
            &mut left,
            now + Duration::from_millis(150)
        ));
        assert!(left.is_none());
        assert!(!hover_dismiss_due(
            false,
            &mut left,
            now + Duration::from_millis(200)
        ));
        assert!(hover_dismiss_due(
            false,
            &mut left,
            now + Duration::from_millis(400)
        ));
    }
}
