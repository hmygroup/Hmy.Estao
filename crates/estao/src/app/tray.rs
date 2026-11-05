//! System tray host.
//!
//! Wave 5 contract: builds the tray icon from `assets/estao-icon.svg`,
//! rebuilds the context menu from the latest snapshots (disabled summary
//! items per provider like the .NET `TrayApplicationContext.RebuildMenu`,
//! "Refresh now", "Settings…", "Launch at sign-in" toggle, "Quit"), and
//! translates `MenuEvent`/`TrayIconEvent` receiver activity into app
//! actions. Left-click on the tray icon opens the usage popover.
