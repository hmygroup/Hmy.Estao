//! System tray host.
//!
//! Wave 5 contract: builds the tray icon from `assets/estao-icon.svg`,
//! rebuilds the context menu from the latest snapshots (disabled summary
//! items per provider like the .NET `TrayApplicationContext.RebuildMenu`,
//! "Refresh now", "Settings…", "Launch at sign-in" toggle, "Quit"), and
//! translates `MenuEvent`/`TrayIconEvent` receiver activity into app
//! actions. Left-click on the tray icon opens the usage popover.

use anyhow::{Context, Result};
use estao_core::constants::DISPLAY_NAME;
use estao_core::models::UsageSnapshot;
use tray_icon::menu::{CheckMenuItem, Menu, MenuId, MenuItem, PredefinedMenuItem};
use tray_icon::{MouseButton, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent};

use crate::app::icons;

/// Stable menu ids survive every menu rebuild, so a queued menu event still
/// identifies its action after a refresh replaced the menu.
const REFRESH_ID: &str = "estao.menu.refresh";
const SETTINGS_ID: &str = "estao.menu.settings";
const STARTUP_ID: &str = "estao.menu.startup";
const QUIT_ID: &str = "estao.menu.quit";

/// An app action requested from the tray.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrayAction {
    /// Fetch usage for every enabled provider now.
    RefreshNow,
    /// Open the settings window.
    OpenSettings,
    /// Flip the Windows "Launch at sign-in" registration.
    ToggleStartup,
    /// Exit the tray app.
    Quit,
    /// Toggle the usage popover.
    OpenPopover,
}

/// Owns the notification-area icon and its current context menu.
pub struct TrayHost {
    tray: TrayIcon,
}

impl TrayHost {
    /// Avoid closing and reopening the popover on the same tray click.
    pub fn contains_pointer(&self) -> bool {
        let Some(rect) = self.tray.rect() else {
            return false;
        };
        let Some((x, y)) = crate::app::windows::overlay::cursor_position() else {
            return false;
        };
        let x = f64::from(x);
        let y = f64::from(y);
        x >= rect.position.x
            && x < rect.position.x + f64::from(rect.size.width)
            && y >= rect.position.y
            && y < rect.position.y + f64::from(rect.size.height)
    }
    /// Creates the tray icon and its first context menu.
    pub fn new(snapshots: &[UsageSnapshot], startup_enabled: bool) -> Result<Self> {
        let icon = icons::tray_icon().context("failed to build the Estao tray icon")?;
        let tray = TrayIconBuilder::new()
            .with_menu(Box::new(Menu::new()))
            .with_tooltip(DISPLAY_NAME)
            .with_icon(icon)
            // The .NET app opens the usage popover on left click and the
            // context menu on right click.
            .with_menu_on_left_click(false)
            .with_menu_on_right_click(true)
            .build()
            .context("failed to create the Estao tray icon")?;

        let host = Self { tray };
        host.rebuild_menu(snapshots, startup_enabled)?;
        Ok(host)
    }

    /// Replaces the context menu with one built from the latest snapshots.
    ///
    /// Called after every refresh, exactly like `TrayApplicationContext`
    /// rebuilds its `ContextMenuStrip`.
    pub fn rebuild_menu(&self, snapshots: &[UsageSnapshot], startup_enabled: bool) -> Result<()> {
        let menu = Menu::new();

        if snapshots.is_empty() {
            menu.append(&MenuItem::new("Loading usage...", false, None))
                .context("failed to add the loading item to the tray menu")?;
        } else {
            for snapshot in snapshots {
                menu.append(&MenuItem::new(summary(snapshot), false, None))
                    .context("failed to add a provider summary to the tray menu")?;
            }
        }

        menu.append(&PredefinedMenuItem::separator())
            .context("failed to add a separator to the tray menu")?;
        menu.append(&MenuItem::with_id(REFRESH_ID, "Refresh now", true, None))
            .context("failed to add the refresh item to the tray menu")?;
        menu.append(&MenuItem::with_id(SETTINGS_ID, "Settings...", true, None))
            .context("failed to add the settings item to the tray menu")?;
        menu.append(&CheckMenuItem::with_id(
            STARTUP_ID,
            "Launch at sign-in",
            true,
            startup_enabled,
            None,
        ))
        .context("failed to add the startup item to the tray menu")?;
        menu.append(&MenuItem::with_id(QUIT_ID, "Quit", true, None))
            .context("failed to add the quit item to the tray menu")?;

        self.tray.set_menu(Some(Box::new(menu)));
        Ok(())
    }

    /// Maps a menu event id to its app action.
    pub fn action_for_menu_id(id: &MenuId) -> Option<TrayAction> {
        match id.as_ref() {
            REFRESH_ID => Some(TrayAction::RefreshNow),
            SETTINGS_ID => Some(TrayAction::OpenSettings),
            STARTUP_ID => Some(TrayAction::ToggleStartup),
            QUIT_ID => Some(TrayAction::Quit),
            _ => None,
        }
    }

    /// Maps a tray icon event to its app action.
    ///
    /// Only a completed left click is meaningful: right clicks are handled by
    /// the native context menu.
    pub fn action_for_tray_event(event: &TrayIconEvent) -> Option<TrayAction> {
        match event {
            TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } => Some(TrayAction::OpenPopover),
            _ => None,
        }
    }
}

/// Port of `TrayApplicationContext.Summary`.
///
/// Examples: `Codex: 42% left - alice`, `Claude: usage unavailable`,
/// `Z.ai: request timed out`.
fn summary(snapshot: &UsageSnapshot) -> String {
    if let Some(error) = &snapshot.error {
        return format!("{}: {error}", snapshot.display_name);
    }

    let primary = snapshot.windows.first();
    let Some(remaining) = primary.and_then(|window| window.percent_remaining()) else {
        return format!("{}: usage unavailable", snapshot.display_name);
    };

    let account = snapshot
        .account
        .as_deref()
        .map(str::trim)
        .filter(|account| !account.is_empty())
        .map(|account| format!(" - {account}"))
        .unwrap_or_default();

    format!(
        "{}: {:.0}% left{account}",
        snapshot.display_name,
        remaining * 100.0
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::Utc;
    use estao_core::models::RateWindow;

    fn snapshot_with(
        display_name: &str,
        percent_used: Option<f64>,
        account: Option<&str>,
        error: Option<&str>,
    ) -> UsageSnapshot {
        UsageSnapshot {
            provider: display_name.to_lowercase(),
            display_name: display_name.to_string(),
            source: "auto".to_string(),
            updated_at: Utc::now(),
            windows: percent_used
                .map(|percent_used| {
                    vec![RateWindow {
                        id: "session".to_string(),
                        title: "Session".to_string(),
                        percent_used: Some(percent_used),
                        reset_at: None,
                        used: None,
                        limit: None,
                        unit: None,
                    }]
                })
                .unwrap_or_default(),
            account: account.map(str::to_string),
            plan: None,
            credits: None,
            error: error.map(str::to_string),
            is_stale: false,
        }
    }

    // Ported from TrayApplicationContext.Summary.
    #[test]
    fn summary_matches_the_dotnet_tray_text() {
        assert_eq!(
            summary(&snapshot_with("Codex", Some(0.25), Some("alice"), None)),
            "Codex: 75% left - alice"
        );
        assert_eq!(
            summary(&snapshot_with("Codex", Some(0.25), None, None)),
            "Codex: 75% left"
        );
        assert_eq!(
            summary(&snapshot_with("Claude", None, None, None)),
            "Claude: usage unavailable"
        );
        assert_eq!(
            summary(&snapshot_with(
                "Z.ai",
                None,
                None,
                Some("request timed out")
            )),
            "Z.ai: request timed out"
        );
    }

    #[test]
    fn menu_ids_map_to_actions() {
        let action = |id: &str| TrayHost::action_for_menu_id(&MenuId::new(id));
        assert_eq!(action(REFRESH_ID), Some(TrayAction::RefreshNow));
        assert_eq!(action(SETTINGS_ID), Some(TrayAction::OpenSettings));
        assert_eq!(action(STARTUP_ID), Some(TrayAction::ToggleStartup));
        assert_eq!(action(QUIT_ID), Some(TrayAction::Quit));
        assert_eq!(action("other"), None);
    }
}
